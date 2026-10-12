#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/GameTasks/NativeCareerTaskRunner.hpp"
#include "UmaAssistantCore/GameTasks/NativeGameTaskRunner.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "UmaAssistantCore/Vision/TemplateMatcher.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

namespace
{

using namespace UmaAssistant::Career;
using namespace UmaAssistant::GameTasks;
using namespace UmaAssistant::TaskEngine;

struct RaceReplayStep
{
    std::string raceId;
    std::string objectiveId;
    std::string raceName;
    std::string capturePath;
};

std::string Fold(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::vector<RaceReplayStep> ReadRaceReplayChain(const CareerVisualResources& resources)
{
    const auto* objectives = resources.GetScenarioDocument("objectives");
    const auto* races = resources.GetScenarioDocument("races");
    if (!objectives || !races || !objectives->contains("objectives")
        || !(*objectives)["objectives"].is_array() || !races->contains("races")
        || !(*races)["races"].is_array())
        return {};

    std::unordered_map<std::string, nlohmann::json> raceById;
    for (const auto& race : (*races)["races"])
        if (race.is_object() && race.contains("raceId") && race["raceId"].is_string())
            raceById.emplace(Fold(race["raceId"].get<std::string>()), race);

    std::vector<RaceReplayStep> chain;
    for (const auto& objective : (*objectives)["objectives"])
    {
        if (!objective.is_object() || !objective.contains("kind") || !objective["kind"].is_string())
            continue;
        const auto kind = Fold(objective["kind"].get<std::string>());
        const auto objectiveId = objective.value("objectiveId", std::string{});
        std::vector<std::string> raceIds;
        if (kind == "race_result" && objective.contains("raceId") && objective["raceId"].is_string())
            raceIds.push_back(objective["raceId"].get<std::string>());
        else if (kind == "race_result_count" && objective.contains("observedRaceIds")
            && objective["observedRaceIds"].is_array())
        {
            for (const auto& id : objective["observedRaceIds"])
                if (id.is_string()) raceIds.push_back(id.get<std::string>());
        }
        for (const auto& raceId : raceIds)
        {
            const auto found = raceById.find(Fold(raceId));
            if (found == raceById.end()) return {};
            const auto& race = found->second;
            if (!race.contains("observedOutcome") || !race["observedOutcome"].is_object()
                || !race["observedOutcome"].contains("capture")
                || !race["observedOutcome"]["capture"].is_string())
                return {};
            chain.push_back({raceId, objectiveId,
                race.value("name", raceId),
                UmaAssistant::Paths::ToUtf8(UmaAssistant::Paths::FromUtf8(
                    race["observedOutcome"]["capture"].get<std::string>()).filename())});
        }
    }
    return chain;
}

std::string PathKey(std::string value)
{
    std::replace(value.begin(), value.end(), '\\', '/');
    while (value.find("//") != std::string::npos)
        value.replace(value.find("//"), 2, "/");
    return Fold(std::move(value));
}

CareerRuntimeConfig PackagedConfig()
{
    CareerRuntimeConfig config;
    config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(
        config.resourceDirectory / "hachimi" / "ura" / "manifest.json");
    config.traineeId = 100101; // Valid packaged trainee for the scenario-default objective chain.
    config.maxSteps = 120;
    config.normalLineupStrategy = "pace";
    return config;
}

class ScopedReplayDirectory final
{
public:
    ScopedReplayDirectory()
    {
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        m_path = std::filesystem::temp_directory_path()
            / ("uma-native-career-replay-" + std::to_string(stamp));
        std::filesystem::create_directories(m_path);
    }
    ~ScopedReplayDirectory()
    {
        std::error_code error;
        std::filesystem::remove_all(m_path, error);
    }
    [[nodiscard]] const std::filesystem::path& Path() const noexcept { return m_path; }

private:
    std::filesystem::path m_path;
};

class RunnerReplayContext final : public ITaskExecutionContext
{
public:
    explicit RunnerReplayContext(const CareerVisualResources& resourceSnapshot,
        std::string initialScreen)
        : m_resources(resourceSnapshot), m_screen(std::move(initialScreen)),
          m_raceSteps(ReadRaceReplayChain(resourceSnapshot))
    {
#ifdef _MSC_VER
        char* trace = nullptr;
        std::size_t traceLength = 0;
        if (_dupenv_s(&trace, &traceLength, "UMA_CAREER_REPLAY_TRACE") == 0 && trace)
        {
            m_traceEnabled = *trace && std::string_view(trace) != "0";
            std::free(trace);
        }
#else
        const auto* trace = std::getenv("UMA_CAREER_REPLAY_TRACE");
        m_traceEnabled = trace && *trace && std::string_view(trace) != "0";
#endif
        Trace("context initialized screen=" + m_screen
            + " packaged-races=" + std::to_string(m_raceSteps.size()));
    }

    bool CaptureScreen() override
    {
        if (m_cancelled) return false;
        m_previousFrame = m_currentFrame;
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = kWidth;
        frame->height = kHeight;
        frame->frameId = ++m_nextFrameId;
        frame->sessionGeneration = 1;
        frame->geometryGeneration = 1;
        const auto pixelCount = static_cast<std::size_t>(kWidth) * static_cast<std::size_t>(kHeight);
        frame->grayPixels.assign(pixelCount, 0);
        frame->rgbaPixels.assign(pixelCount * 4, 0);
        for (std::size_t pixel = 0; pixel < pixelCount; ++pixel)
            frame->rgbaPixels[pixel * 4 + 3] = 255;
        if (m_screen == "goal_complete")
        {
            ++m_goalCompleteCaptureCount;
            const auto* screen = m_resources.FindScreen("goal_complete");
            if (!screen || screen->templates.empty()) return false;
            const auto image = LoadTemplateImage(screen->templates.front().path);
            if (!image || image->width <= 0 || image->height <= 0
                || image->gray.size() < static_cast<std::size_t>(image->width) * image->height)
                return false;
            frame->width = image->width;
            frame->height = image->height;
            frame->grayPixels = image->gray;
            frame->rgbaPixels = image->rgba;
        }
        if (m_screen == "career_main")
        {
            // A saturated green bar in the authored energy ROI gives the real
            // CareerScreenObserver reliable, high-energy evidence.
            constexpr int left = 287;
            constexpr int top = 186;
            constexpr int width = 310;
            constexpr int height = 28;
            for (int y = top; y < top + height; ++y)
            {
                for (int x = left; x < left + width; ++x)
                {
                    const auto offset = (static_cast<std::size_t>(y) * static_cast<std::size_t>(kWidth)
                        + static_cast<std::size_t>(x)) * 4;
                    frame->rgbaPixels[offset] = 30;
                    frame->rgbaPixels[offset + 1] = 220;
                    frame->rgbaPixels[offset + 2] = 60;
                }
            }
        }
        if (numericProbeReplay && m_screen == "training_selection")
        {
            auto const image = UmaAssistant::Resources::WicImageDecoder::decode_file(
                std::filesystem::path(CMAKE_SOURCE_DIR)
                / "testdata/hachimi/ura/captures/smart_runtime_20261006/speed-1.png");
            frame->width = image.width;
            frame->height = image.height;
            frame->grayPixels = image.gray;
            frame->rgbaPixels = image.rgba;
        }
        m_currentFrame = std::move(frame);
        return true;
    }

    std::string GetDeviceSerial() const override { return "native-career-runner-replay"; }

    TaskMatchResult MatchTemplate(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm) override
    {
        return MatchTemplateAdvanced(path, threshold, roi, algorithm, kWidth, kHeight, {});
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        m_templateTrace.push_back(path);
        if (numericProbeReplay && PathKey(path).find("common/date_changed/") != std::string::npos)
            return {};
        if (numericProbeReplay && m_screen == "training_selection" && !m_graphActive)
        {
            // The numeric fixture masks unrelated UI. Only expose its authored
            // training markers; the other screens keep their simulated absence.
            auto const key = PathKey(path);
            bool const trainingTask = std::any_of(m_resources.GetExecutionDefinition().tasks.begin(),
                m_resources.GetExecutionDefinition().tasks.end(), [&](auto const& item) {
                    return item.first.rfind("training_selection", 0) == 0 && item.second.templatePath
                        && PathKey(*item.second.templatePath) == key;
                });
            if (!trainingTask && key.find("/career/training/") == std::string::npos
                && std::filesystem::path(path).filename().string().find("training_selection") == std::string::npos)
                return {};
            auto const marker = LoadTemplateImage(path);
            if (!marker || !m_currentFrame) return {};
            UmaAssistant::Vision::GrayImage const image{m_currentFrame->width, m_currentFrame->height,
                m_currentFrame->grayPixels, m_currentFrame->rgbaPixels};
            UmaAssistant::Vision::Roi bounds;
            if (roi) bounds = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
            auto const match = algorithm == "MatchTemplateExhaustiveColor"
                ? UmaAssistant::Vision::find_exhaustive_color(image, *marker, bounds, threshold, referenceWidth, referenceHeight)
                : algorithm == "MatchTemplateColor" || algorithm == "MatchTemplateColorText"
                ? UmaAssistant::Vision::find_color(image, *marker, bounds, threshold, referenceWidth, referenceHeight,
                    algorithm == "MatchTemplateColorText")
                : UmaAssistant::Vision::find(image, *marker, bounds, threshold, referenceWidth, referenceHeight);
            Trace("numeric marker " + std::filesystem::path(path).filename().string()
                + " found=" + (match.found ? std::string("true") : std::string("false"))
                + " score=" + std::to_string(match.score));
            auto scale = [](int value, int actual, int reference) {
                return static_cast<int>(std::nearbyint(static_cast<double>(value) * reference / actual));
            };
            return {match.found, match.score, scale(match.x, image.width, referenceWidth),
                scale(match.y, image.height, referenceHeight), scale(match.width, image.width, referenceWidth),
                scale(match.height, image.height, referenceHeight),
                referenceWidth, referenceHeight, m_currentFrame->frameId,
                m_currentFrame->sessionGeneration, m_currentFrame->geometryGeneration};
        }
        TaskMatchResult result;
        result.referenceWidth = referenceWidth;
        result.referenceHeight = referenceHeight;
        if (m_currentFrame)
        {
            result.frameId = m_currentFrame->frameId;
            result.sessionGeneration = m_currentFrame->sessionGeneration;
            result.geometryGeneration = m_currentFrame->geometryGeneration;
        }

        if (m_graphActive)
        {
            // Replay the real packaged graph branches. The locked-result
            // marker is deliberately absent: the graph follows its authored
            // View Results path and leaves the result page for the coordinator.
            if (ShouldMissGraphMarker(path))
                return result;
            if (m_graphRoot == "race_runner_entry_view_results"
                && PathKey(path).find("race_entry_view_results_lock.png") != std::string::npos)
                return result;
            result.found = true;
            result.score = (std::max)(threshold, 0.995);
        }
        else if (IsExpectedRaceResultCapture(path, threshold))
        {
            result.found = true;
            result.score = 0.99;
        }
        else if (threshold >= 0.9 && IsRaceResultCapture(path))
        {
            result.found = false;
            result.score = 0.0;
        }
        else if (const auto* screen = m_resources.FindScreen(m_screen))
        {
            const auto key = PathKey(path);
            const auto isDeclared = [&](const CareerTemplateDefinition& definition) {
                return PathKey(definition.path) == key;
            };
            const bool inPrimary = std::any_of(screen->templates.begin(), screen->templates.end(), isDeclared);
            const bool inRequired = std::any_of(screen->requiredTemplates.begin(), screen->requiredTemplates.end(), isDeclared);
            // Home now requires its Career entry control on the same frame as
            // the shared selected tab. The fixture exposes both authored markers.
            const auto* entry = m_screen == "home" ? m_resources.FindExecutionTask("home_home_career") : nullptr;
            const bool inEntry = entry && entry->templatePath && PathKey(*entry->templatePath) == key;
            result.found = inPrimary || inRequired || inEntry;
            result.score = result.found ? (std::max)(threshold, 0.99) : 0.0;
        }

        if (result.found)
        {
            result.x = 405;
            result.y = 900;
            result.width = 90;
            result.height = 70;
        }
        return result;
    }

    TaskTemplateCandidatesResult MatchTemplateCandidates(
        const std::vector<std::string>& paths, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm,
        int referenceWidth, int referenceHeight, const std::vector<double>& scales,
        double minimumScoreGap) override
    {
        return ITaskExecutionContext::MatchTemplateCandidates(
            paths, threshold, roi, algorithm, referenceWidth, referenceHeight, scales, minimumScoreGap);
    }

    bool CheckImageChange(const TaskMatchResult& match) override
    {
        return match.frameId != 0 && m_currentFrame && match.frameId != m_currentFrame->frameId;
    }

    std::vector<TaskOcrDetection> RecognizeText(const std::string&,
        const std::optional<TaskRoi>& roi = std::nullopt) override
    {
        if (!roi)
        {
            if (const auto* screen = m_resources.FindScreen(m_screen))
            {
                std::vector<TaskOcrDetection> required;
                int x = 40;
                for (const auto& text : screen->requiredText)
                {
                    required.push_back({text, x, 40, 180, 36, 0.99});
                    x += 185;
                }
                return required;
            }
            return {};
        }

        if (m_screen != "career_main")
        {
            if (m_screen == "race_result")
                return {TaskOcrDetection{"1st", roi->x + 5, roi->y + 5, 40, 28, 0.98}};
            if (m_screen == "goal_complete" && roi->x == 40 && roi->y == 250)
                return {TaskOcrDetection{"All goals achieved!", roi->x + 5, roi->y + 5, 340, 40, 0.99}};
            return {};
        }

        if (roi->x == 15 && roi->y == 45)
        {
            const auto phase = CurrentPhase();
            return {TaskOcrDetection{phase, roi->x + 1, roi->y + 1, 240, 24, 0.99}};
        }
        if (roi->x == 10 && roi->y == 90)
        {
            const auto countdown = CurrentCountdown();
            return {TaskOcrDetection{countdown, roi->x + 1, roi->y + 1, 110, 24, 0.98}};
        }
        if (roi->x == 315 && roi->y == 90)
        {
            const auto goal = CurrentGoalText();
            return {TaskOcrDetection{goal, roi->x + 1, roi->y + 1, 520, 30, 0.98}};
        }
        if (roi->x == 654 && roi->y == 169)
            return {TaskOcrDetection{"Good", roi->x + 1, roi->y + 1, 75, 25, 0.98}};
        return {};
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return m_currentFrame; }
    std::shared_ptr<const TaskFrameSnapshot> GetPreviousFrame() const override { return m_previousFrame; }

    void SetCoordinateReference(int width, int height) override { m_coordinateReference = {width, height}; }
    std::pair<int, int> GetCoordinateReference() const override { return m_coordinateReference; }
    std::string GetTemplateBaseDirectory() const override { return m_templateBaseDirectory; }

    void SetTemplateBaseDirectory(const std::string& directory) override
    {
        const auto previous = m_templateBaseDirectory;
        m_templateBaseDirectory = directory;
        if (previous.empty() && !directory.empty())
        {
            m_graphActive = true;
            m_graphRoot.clear();
            m_currentGraphTask.clear();
            m_lastTaskCompleted = false;
        }
        else if (!previous.empty() && directory.empty())
        {
            // TaskGraphRunner has no PipelineCompleted event. A final
            // TaskCompleted event is the positive evidence that the graph
            // reached its authored terminal task; a timeout/failure alone
            // must never fabricate a page transition.
            if (m_graphActive && m_lastTaskCompleted)
                CompleteGraphTransition();
            m_graphActive = false;
            m_graphRoot.clear();
            m_currentGraphTask.clear();
            m_lastTaskCompleted = false;
        }
    }

    bool Tap(int x, int y) override
    {
        m_taps.emplace_back(x, y);
        if (m_graphActive && m_currentGraphTask == "independent_mode_select")
            m_independentModeTapped = true;
        if (m_graphActive && m_graphRoot == "independent_training_start")
            m_independentStartTapped = true;
        if (m_graphActive && m_graphRoot.rfind("training_selection_training_", 0) == 0)
            m_trainingTapped = true;
        return true;
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        // NativeGameTaskRunner permits coordinate-only ClickRect actions when
        // the action graph supplies a specificRect. Those arrive as an
        // unfound, frame-less match and are bound to the currently captured
        // frame by the production context.
        if ((!match.found && (match.frameId != 0 || !m_currentFrame))
            || (match.frameId != 0 && m_currentFrame
                && match.frameId != m_currentFrame->frameId && !IsMatchRetained(match)))
            return false;
        return Tap(x, y);
    }

    bool RetainMatchForRetry(const TaskMatchResult& match, int, int, int, int) override
    {
        if (!numericProbeReplay || !match.found || !m_currentFrame) return false;
        m_probeRetainedFrame = match.frameId;
        return true;
    }
    bool HasRetainedMatch() const override { return m_probeRetainedFrame != 0; }
    bool IsMatchRetained(const TaskMatchResult& match) const override
    { return m_probeRetainedFrame != 0 && match.frameId == m_probeRetainedFrame; }
    void ReleaseRetainedMatch() override { m_probeRetainedFrame = 0; }

    bool Swipe(int, int, int, int, int) override { ++m_swipes; return true; }
    bool InputText(const std::string&) override { return true; }
    bool SendKey(const std::string&) override { return true; }
    void Sleep(int milliseconds) override
    {
        // Exercise deadline/poll paths without spinning at full CPU in offline
        // replay. The cap keeps the test fast while preserving ordering.
        if (milliseconds > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds((std::clamp)(milliseconds, 1, 5)));
    }
    bool IsCancelled() const override { return m_cancelled; }
    UmaAssistant::Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<UmaAssistant::Recognition::NumericOcrBatchField>& fields) override
    {
        if (numericProbeReplay && !fields.empty())
        {
            ++numericProbeCalls;
            m_cancelled = true;
        }
        return {};
    }
    bool numericProbeReplay{false};
    int numericProbeCalls{0};
    bool cancelOnPipelineStart{false};
    void Log(const std::string& level, const std::string& message) override
    {
        m_logs.push_back(level + ": " + message);
        Trace("log " + level + ": " + message);
    }

    void EmitEvent(const TaskExecutionEvent& event) override
    {
        m_events.push_back(event);
        if (cancelOnPipelineStart && event.type == TaskEventType::PipelineStarted)
            m_cancelled = true;
        Trace("event type=" + std::to_string(static_cast<int>(event.type))
            + " name=" + event.taskName + " success=" + (event.succeeded ? "true" : "false")
            + " score=" + std::to_string(event.score) + " message=" + event.message);
        if (event.type == TaskEventType::PipelineStarted)
        {
            m_pipelineRoots.push_back(event.taskName);
            Trace("pipeline started name=" + event.taskName + " screen=" + m_screen
                + " race-index=" + std::to_string(m_raceResultIndex));
            if (m_graphRoot.empty())
            {
                m_graphRoot = event.taskName;
                m_currentGraphTask = event.taskName;
                m_lastTaskCompleted = false;
            }
        }
        else if (event.type == TaskEventType::TaskEntering)
        {
            m_currentGraphTask = event.taskName;
            m_lastTaskCompleted = false;
        }
        else if (event.type == TaskEventType::TaskCompleted)
            m_lastTaskCompleted = true;
        else if (event.type == TaskEventType::TaskFailed)
        {
            m_lastTaskCompleted = false;
            Trace("task failed name=" + event.taskName + " graph=" + m_graphRoot
                + " screen=" + m_screen);
        }
    }

    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override
    { return false; }

    [[nodiscard]] const std::vector<std::string>& PipelineRoots() const noexcept { return m_pipelineRoots; }
    [[nodiscard]] const std::vector<TaskExecutionEvent>& Events() const noexcept { return m_events; }
    [[nodiscard]] const std::vector<std::string>& TemplateTrace() const noexcept { return m_templateTrace; }
    [[nodiscard]] const std::vector<std::pair<int, int>>& Taps() const noexcept { return m_taps; }
    [[nodiscard]] const std::vector<std::string>& Logs() const noexcept { return m_logs; }
    [[nodiscard]] bool TrainingTapped() const noexcept { return m_trainingTapped; }
    [[nodiscard]] bool IndependentModeTapped() const noexcept { return m_independentModeTapped; }
    [[nodiscard]] bool IndependentStartTapped() const noexcept { return m_independentStartTapped; }
    [[nodiscard]] int GoalCompleteCaptureCount() const noexcept { return m_goalCompleteCaptureCount; }
    [[nodiscard]] int SwipeCount() const noexcept { return m_swipes; }
    [[nodiscard]] const std::string& CurrentScreen() const noexcept { return m_screen; }
    [[nodiscard]] const std::vector<std::string>& MatchedRaceIds() const noexcept { return m_matchedRaceIds; }

private:
    static constexpr int kWidth = 900;
    static constexpr int kHeight = 1600;

    void Trace(const std::string& message) const
    {
        if (!m_traceEnabled) return;
        std::cerr << "[native-career-replay] " << message << '\n';
        std::cerr.flush();
    }

    bool IsExpectedRaceResultCapture(const std::string& path, double threshold)
    {
        if (m_screen != "race_result" || threshold < 0.9 || m_raceResultIndex >= m_raceSteps.size())
            return false;
        const auto expected = PathKey(m_raceSteps[m_raceResultIndex].capturePath);
        const auto key = PathKey(path);
        const auto separator = key.find_last_of('/');
        const auto filename = separator == std::string::npos ? key : key.substr(separator + 1);
        if (filename != expected)
            return false;
        m_matchedRaceIds.push_back(m_raceSteps[m_raceResultIndex].raceId);
        ++m_raceResultIndex;
        return true;
    }

    bool IsRaceResultCapture(const std::string& path) const
    {
        const auto key = PathKey(path);
        return std::any_of(m_raceSteps.begin(), m_raceSteps.end(), [&](const auto& step) {
            const auto capture = PathKey(step.capturePath);
            const auto separator = key.find_last_of('/');
            const auto filename = separator == std::string::npos ? key : key.substr(separator + 1);
            return filename == capture;
        });
    }

    std::string CurrentPhase() const
    {
        if (!m_trainingCompleted) return "Junior Year Late May";
        switch (m_raceResultIndex)
        {
        case 0: return "Junior Year Early June";
        case 1: return "Classic Year Early May";
        case 2: return "Classic Year Late November";
        case 3: return "Senior Year Early January";
        case 4: return "Senior Year Early February";
        case 5: return "Senior Year Late February";
        case 6: return "Senior Year Late October";
        case 7: return "Senior Year Late December";
        default: return "Senior Year Late December";
        }
    }

    std::string CurrentCountdown() const
    {
        if (!m_trainingCompleted) return "1 turn left";
        if (m_raceResultIndex == 4 || m_raceResultIndex == 5)
            return "1 turn left";
        return "0 turns left";
    }

    std::string CurrentGoalText() const
    {
        if (m_raceResultIndex == 4)
            return "Place top 3 in G1 races, 2 times left";
        if (m_raceResultIndex == 5)
            return "Place top 3 in G1 races, 1 time left";
        if (m_raceResultIndex >= 8)
            return "Participate in the URA Finale";
        if (m_raceResultIndex < m_raceSteps.size())
            return "Participate in the " + m_raceSteps[m_raceResultIndex].raceName;
        return "Participate in the URA Finale";
    }

    std::shared_ptr<const UmaAssistant::Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    {
        const auto* goalScreen = m_resources.FindScreen("goal_complete");
        if (!goalScreen || goalScreen->profilePath.empty())
            return {};
        const auto resolveGoalTemplate = [&](const CareerTemplateDefinition& definition) {
            auto candidate = UmaAssistant::Paths::FromUtf8(definition.path);
            if (!candidate.is_absolute())
                candidate = goalScreen->profilePath.parent_path() / candidate;
            return candidate.lexically_normal();
        };
        auto resolved = UmaAssistant::Paths::FromUtf8(path);
        if (!resolved.is_absolute())
            resolved = goalScreen->profilePath.parent_path() / resolved;
        resolved = resolved.lexically_normal();
        const auto requestedKey = PathKey(UmaAssistant::Paths::ToUtf8(resolved));
        const auto isGoalTemplate = [&](const CareerTemplateDefinition& definition) {
            return PathKey(UmaAssistant::Paths::ToUtf8(resolveGoalTemplate(definition))) == requestedKey;
        };
        const bool isAllowlisted = std::any_of(goalScreen->templates.begin(), goalScreen->templates.end(), isGoalTemplate)
            || std::any_of(goalScreen->requiredTemplates.begin(), goalScreen->requiredTemplates.end(), isGoalTemplate);
        if (!isAllowlisted && !numericProbeReplay)
        {
            // This replay fixes every non-goal-complete frame to a deterministic
            // negative template result. Only the goal_complete resources are
            // decoded, because production recognition there has a dedicated
            // color-header gate that needs the actual authored pixels.
            return {};
        }
        const auto cacheKey = PathKey(UmaAssistant::Paths::ToUtf8(resolved));
        if (const auto found = m_templateCache.find(cacheKey); found != m_templateCache.end())
            return found->second;
        try
        {
            auto image = std::make_shared<const UmaAssistant::Vision::GrayImage>(
                UmaAssistant::Resources::WicImageDecoder::decode_file(resolved));
            m_templateCache.emplace(cacheKey, image);
            return image;
        }
        catch (const std::exception& exception)
        {
            m_logs.push_back(std::string("ERROR: WIC template load failed: ") + exception.what());
            return {};
        }
    }

    bool ShouldMissGraphMarker(const std::string& path) const
    {
        const auto marker = PathKey(path);
        if (m_graphRoot == "independent_mode_selected_probe"
            && m_currentGraphTask == "independent_mode_selected_probe"
            && marker.find("mode_independent_selected.png") != std::string::npos)
            return true; // Exercise the authored select-and-confirm branch.
        if (m_graphRoot == "independent_lineup_expand_prepare")
        {
            if (m_currentGraphTask == "independent_lineup_expanded_probe"
                && marker.find("lineup_open_down.png") != std::string::npos)
                return true; // The initial drawer is collapsed.
            if (m_currentGraphTask == "independent_lineup_expand_collapsed_guard"
                && marker.find("lineup_closed_right.png") != std::string::npos)
                return true;
            if (m_currentGraphTask == "independent_lineup_expand_post_collapsed_guard"
                && marker.find("lineup_closed_right.png") != std::string::npos)
                return true;
        }
        if (m_graphRoot == "independent_lineup_collapse_prepare")
        {
            if (m_currentGraphTask == "independent_lineup_collapsed_probe"
                && marker.find("lineup_closed_right.png") != std::string::npos)
                return true; // The expanded drawer is collapsed and verified.
            if (m_currentGraphTask == "independent_lineup_collapse_expanded_guard"
                && marker.find("lineup_open_down.png") != std::string::npos)
                return true;
            if (m_currentGraphTask == "independent_lineup_collapse_post_expanded_guard"
                && marker.find("lineup_open_down.png") != std::string::npos)
                return true;
        }
        if (m_graphRoot == "independent_lineup_strategy_precondition"
            && m_currentGraphTask == "independent_lineup_strategy_expanded_guard"
            && marker.find("lineup_open_down.png") != std::string::npos)
            return true; // Strategy selection requires a collapsed drawer.
        if (m_graphRoot == "independent_skills_reset_probe"
            && m_currentGraphTask == "independent_skills_reset_probe"
            && marker.find("skills_main_reset.png") != std::string::npos)
            return true; // There are no pre-existing skills to reset.
        return false;
    }

    void CompleteGraphTransition()
    {
        const auto previousScreen = m_screen;
        if (m_graphRoot == "home_home_career") m_screen = "career_continue";
        else if (m_graphRoot == "career_continue_resume") m_screen = "career_final_confirmation";
        else if (m_graphRoot == "career_entry_career_start") m_screen = "career_intro_event";
        else if (m_graphRoot == "normal_post_start_skip") m_screen = "normal_quick_mode_settings";
        else if (m_graphRoot == "normal_quick_mode_confirm") m_screen = "career_main";
        else if (m_graphRoot == "career_main_action_training") m_screen = "training_selection";
        else if (m_graphRoot.rfind("training_selection_training_", 0) == 0)
            m_screen = "training_result";
        else if (m_graphRoot == "training_result_event_advance")
        {
            m_trainingCompleted = true;
            m_screen = "career_main";
        }
        else if (m_graphRoot == "career_main_action_races"
            || m_graphRoot == "career_main_action_finale_races") m_screen = "career_races_ready";
        else if (m_graphRoot == "career_races_ready_action_races") m_screen = "race_day";
        else if (m_graphRoot == "race_day_race_open_list") m_screen = "race_list";
        else if (m_graphRoot == "race_list_race_recommended_entry"
            || m_graphRoot == "race_list_race_fans_entry"
            || m_graphRoot == "race_list_race_goal_entry") m_screen = "race_runner";
        else if (m_graphRoot == "race_runner_entry_view_results") m_screen = "race_result";
        else if (m_graphRoot == "race_result_result_next")
        {
            if (m_raceResultIndex > 8)
                m_screen = "reward";
            else if (m_raceResultIndex > 0 && m_raceResultIndex < m_raceSteps.size()
                && m_raceSteps[m_raceResultIndex - 1].objectiveId
                    == m_raceSteps[m_raceResultIndex].objectiveId)
                m_screen = "career_main"; // First of the two G1 count results.
            else
                m_screen = "goal_objective_complete";
        }
        else if (m_graphRoot == "goal_update_goal_update_next")
            m_screen = m_raceResultIndex == 8 ? "goal_complete" : "career_main";
        else if (m_graphRoot == "goal_complete_goal_next")
            m_screen = m_screen == "goal_complete" ? "scenario_event" : "goal_update";
        else if (m_graphRoot == "scenario_event_event_advance") m_screen = "career_main";
        else if (m_graphRoot == "reward_reward_next") m_screen = "reward_support";
        else if (m_graphRoot == "reward_support_reward_next")
            m_screen = m_raceResultIndex >= m_raceSteps.size() ? "career_complete" : "career_main";
        else if (m_graphRoot == "career_complete_career_to_home") m_screen = "home";
        else if (m_graphRoot == "independent_training_start") m_screen = "home";
        Trace("graph transition name=" + m_graphRoot + " screen=" + previousScreen
            + " -> " + m_screen + " race-index=" + std::to_string(m_raceResultIndex));
    }

    const CareerVisualResources& m_resources;
    std::string m_screen;
    std::string m_templateBaseDirectory;
    std::string m_graphRoot;
    std::string m_currentGraphTask;
    std::shared_ptr<const TaskFrameSnapshot> m_currentFrame;
    std::shared_ptr<const TaskFrameSnapshot> m_previousFrame;
    std::uint64_t m_nextFrameId{0};
    std::uint64_t m_probeRetainedFrame{0};
    std::pair<int, int> m_coordinateReference{900, 1600};
    std::vector<RaceReplayStep> m_raceSteps;
    std::size_t m_raceResultIndex{0};
    std::vector<std::string> m_matchedRaceIds;
    std::unordered_map<std::string, std::shared_ptr<const UmaAssistant::Vision::GrayImage>> m_templateCache;
    std::vector<std::string> m_pipelineRoots;
    std::vector<TaskExecutionEvent> m_events;
    bool m_cancelled{false};
    std::vector<std::string> m_templateTrace;
    std::vector<std::pair<int, int>> m_taps;
    std::vector<std::string> m_logs;
    int m_swipes{0};
    bool m_graphActive{false};
    bool m_lastTaskCompleted{false};
    bool m_traceEnabled{false};
    bool m_trainingCompleted{false};
    bool m_trainingTapped{false};
    bool m_independentModeTapped{false};
    bool m_independentStartTapped{false};
    int m_goalCompleteCaptureCount{0};
};

std::string Join(const std::vector<std::string>& values)
{
    std::string result;
    for (const auto& value : values)
    {
        if (!result.empty()) result += "\n";
        result += value;
    }
    return result;
}

} // namespace

TEST_CASE("Career recovery context forwards numeric OCR used by smart training",
    "[GameTasks][NativeCareerTaskRunnerReplay][SmartRestRegression]")
{
    auto config = PackagedConfig();
    CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    RunnerReplayContext context(resources, "career_main");
    context.numericProbeReplay = true;
    auto const result = NativeGameTaskRunner::Run("career-training", nlohmann::json{
        {"manifestPath", config.manifestPath}, {"traineeId", *config.traineeId},
        {"strategyId", "smart-balanced-safe"}, {"maxSteps", 4}}, context, config.resourceDirectory);
    CAPTURE(result.message);
    REQUIRE_FALSE(result.succeeded);
    CHECK(context.numericProbeCalls == 1);
    CHECK(context.IsCancelled());
}

TEST_CASE("Native Career progress retains configured run counts during nested setup cancellation",
    "[GameTasks][NativeCareerTaskRunnerReplay][CareerRunProgress]")
{
    auto config = PackagedConfig();
    CareerVisualResources screenResources(config);
    REQUIRE(screenResources.IsReady());
    for (int const runCount : {1, 3})
    {
        CAPTURE(runCount);
        RunnerReplayContext context(screenResources, "career_final_confirmation");
        context.cancelOnPipelineStart = true;
        auto const result = NativeGameTaskRunner::Run("career-training", nlohmann::json{
            {"manifestPath", config.manifestPath}, {"traineeId", *config.traineeId},
            {"runCount", runCount}}, context, config.resourceDirectory);
        CAPTURE(result.message);
        REQUIRE_FALSE(result.succeeded);
        REQUIRE(context.IsCancelled());
        CHECK(result.completedUnits == 0);
        CHECK(result.totalUnits == runCount);
        REQUIRE(context.Events().size() > 1);
        CHECK(context.Events().front().taskName == "career.run");
        REQUIRE(std::any_of(context.Events().begin(), context.Events().end(), [](auto const& event) {
            return event.type == TaskEventType::PipelineStarted;
        }));
        for (auto const& event : context.Events())
        {
            CAPTURE(event.taskName);
            CHECK(event.completedUnits == 0);
            CHECK(event.totalUnits == runCount);
        }
    }
}

TEST_CASE("Native runner replays the packaged normal objective chain through URA Finale and settlement",
    "[GameTasks][NativeCareerTaskRunnerReplay]")
{
    auto config = PackagedConfig();
    config.careerMode = "normal";
    config.maxSteps = 400;
    config.continueExistingCareer = true;
    config.deleteExistingCareerData = false;
    config.normalTrainingRatio = {1, 0, 0, 0, 0};
    config.normalLineupStrategy = "pace";
    CareerVisualResources screenResources(config);
    CAPTURE(config.manifestPath);
    REQUIRE(screenResources.IsReady());
    REQUIRE(screenResources.FindScreen("home") != nullptr);
    REQUIRE(screenResources.FindAction("career_final_confirmation", "normal.start") != nullptr);
    REQUIRE(screenResources.FindAction("training_selection", "training.speed") != nullptr);
    REQUIRE(screenResources.FindAction("race_list", "race.recommended_entry") != nullptr);
    REQUIRE(screenResources.FindAction("race_list", "race.fans_entry") != nullptr);
    REQUIRE(screenResources.FindAction("career_main", "action.finale_races") != nullptr);
    REQUIRE(screenResources.FindAction("career_complete", "career.to_home") != nullptr);

    RunnerReplayContext context(screenResources, "home");
    const auto result = NativeCareerTaskRunner::Run(config, context, config.resourceDirectory);
    const auto pipelineTrace = Join(context.PipelineRoots());
    const auto logTrace = Join(context.Logs());
    CAPTURE(pipelineTrace);
    CAPTURE(logTrace);
    CAPTURE(result.message);
    INFO("The replay uses packaged profile/execution graphs with a fixed frame, template-match, and OCR context.");

    REQUIRE(result.succeeded);
    CHECK(result.completedUnits == 1);
    CHECK(context.TrainingTapped());
    CHECK(context.CurrentScreen() == "home");
    const std::vector<std::string> expectedRaceChain{
        "junior_debut", "nhk_mile_cup", "mile_championship", "arima_kinen_goal",
        "kawasaki_kinen", "february_stakes", "tenno_sho_autumn", "arima_kinen_goal",
        "ura_finale_qualifier", "ura_finale_semifinal", "ura_finale_finals"};
    CHECK(context.MatchedRaceIds() == expectedRaceChain);
    CHECK(context.GoalCompleteCaptureCount() >= 2);
    // main only admits Goal Update after the objective-complete banner.
    // Keep the simulated game order explicit instead of weakening that gate.
    const auto complete = std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "goal_complete_goal_next");
    REQUIRE(complete != context.PipelineRoots().end());
    REQUIRE(std::next(complete) != context.PipelineRoots().end());
    CHECK(*std::next(complete) == "goal_update_goal_update_next");
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "home_home_career")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "career_continue_resume")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "normal_quick_mode_confirm")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "career_main_action_training")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "career_main_action_finale_races")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "race_list_race_recommended_entry")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "race_runner_entry_view_results")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "race_list_race_fans_entry")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "scenario_event_event_advance")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "reward_reward_next")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "reward_support_reward_next")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "career_complete_career_to_home")
        != context.PipelineRoots().end());
    for (const auto& raceId : expectedRaceChain)
        CHECK(logTrace.find("Applied verified result for race " + raceId) != std::string::npos);
    CHECK(std::any_of(context.TemplateTrace().begin(), context.TemplateTrace().end(), [](const auto& path) {
        return PathKey(path).find("debut_race_result_wait.png") != std::string::npos;
    }));
}

TEST_CASE("Native runner completes independent setup through resource Start and checkpointed Home confirmation",
    "[GameTasks][NativeCareerTaskRunnerReplay]")
{
    auto config = PackagedConfig();
    config.careerMode = "independent";
    config.continueExistingCareer = false;
    config.deleteExistingCareerData = false;
    config.independentTrainingFocus = "balanced";
    config.independentLineupStrategy = "pace";
    config.independentAgendaSelections.clear();
    config.independentSkillIds.clear();
    ScopedReplayDirectory checkpointDirectory;
    config.checkpointDirectory = checkpointDirectory.Path();
    CareerVisualResources screenResources(config);
    CAPTURE(config.manifestPath);
    REQUIRE(screenResources.IsReady());
    REQUIRE(screenResources.FindAction("career_final_confirmation", "independent.select_mode") != nullptr);
    REQUIRE(screenResources.FindAction("career_final_confirmation", "independent.start") != nullptr);
    REQUIRE(screenResources.FindAction("career_final_confirmation", "independent.post_start.home_probe") != nullptr);

    RunnerReplayContext context(screenResources, "career_final_confirmation");
    const auto result = NativeCareerTaskRunner::Run(config, context, config.resourceDirectory);
    const auto pipelineTrace = Join(context.PipelineRoots());
    const auto logTrace = Join(context.Logs());
    CAPTURE(pipelineTrace);
    CAPTURE(logTrace);

    REQUIRE(result.succeeded);
    CHECK(result.completedUnits == 1);
    CHECK(context.IndependentModeTapped());
    CHECK(context.IndependentStartTapped());
    CHECK(context.CurrentScreen() == "home");
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "independent_training_start")
        != context.PipelineRoots().end());
    CHECK(std::find(context.PipelineRoots().begin(), context.PipelineRoots().end(), "independent_post_start_home_probe")
        != context.PipelineRoots().end());

    const auto checkpoint = checkpointDirectory.Path() / "independent-100101.json";
    REQUIRE(std::filesystem::is_regular_file(checkpoint));
    std::ifstream input(checkpoint, std::ios::binary);
    REQUIRE(input.good());
    const std::string checkpointJson((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    CHECK(checkpointJson.find("\"stage\": \"Completed\"") != std::string::npos);
    CHECK(checkpointJson.find("\"completionVerified\": true") != std::string::npos);
}

