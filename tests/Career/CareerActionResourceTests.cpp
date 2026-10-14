#include <catch2/catch_test_macros.hpp>
#include <catch2/catch_approx.hpp>
#include <catch2/generators/catch_generators.hpp>

#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/Career/CareerFlowCoordinator.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Career/Flows/CareerSkillLearningFlow.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "UmaAssistantCore/Vision/TemplateMatcher.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <vector>
#include <chrono>
#include <thread>

namespace
{

using namespace UmaAssistant::Career;
using namespace UmaAssistant::CareerDomain;
using namespace UmaAssistant::TaskEngine;

std::string FoldPath(const std::string& path)
{
    std::string result = std::filesystem::path(path).filename().string();
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return result;
}

class CareerActionReplayContext final : public ITaskExecutionContext
{
public:
    explicit CareerActionReplayContext(std::vector<std::string> skillNames = {})
        : m_skillNames(std::move(skillNames))
    {
    }

    bool CaptureScreen() override
    {
        if (captureFailuresRemaining > 0) { --captureFailuresRemaining; return false; }
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = 900;
        frame->height = 1600;
        frame->frameId = ++m_nextFrameId;
        frame->grayPixels.assign(static_cast<std::size_t>(frame->width) * frame->height, 0);
        if (entryAnimating) frame->grayPixels[0] = static_cast<std::uint8_t>(frame->frameId);
        if (m_eventSignatureReplay)
        {
            m_eventImage = UmaAssistant::Resources::WicImageDecoder::decode_file(
                UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR)
                / "resource/hachimi/ura/testdata/captures/runtime_frames/support_event_choice_ura.png");
            frame->grayPixels = m_eventImage.gray;
            frame->rgbaPixels = m_eventImage.rgba;
        }
        m_frame = std::move(frame);
        return true;
    }

    TaskMatchResult MatchTemplate(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm) override
    {
        return MatchTemplateAdvanced(path, threshold, roi, algorithm, 900, 1600, {});
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string&, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        m_lastTemplate = path;
        matchedTemplates.push_back(path);
        const auto file = FoldPath(path);
        lastRoi = roi;
        lastThreshold = threshold;
        bool found = m_raceFrameReplay
            ? file == m_raceFrameName
            : m_eventSignatureReplay
            ? file == "event_effects_button.png" : m_matchEverything;
        if (m_entryRetryResources)
        {
            auto const* screen = m_entryRetryResources->FindScreen(
                entryTaps >= 2 || (entryAnimating && m_nextFrameId > 8) ? "race_list" : "career_main");
            auto matches = [&](auto const& marker) { return FoldPath(marker.path) == file; };
            found = screen && (std::any_of(screen->templates.begin(), screen->templates.end(), matches)
                || std::any_of(screen->requiredTemplates.begin(), screen->requiredTemplates.end(), matches));
            if (file == m_entryButtonPath) found = true;
        }
        if (!m_eventSignatureReplay && !m_raceFrameReplay && !m_entryRetryResources)
        {
            if (!found && file == "race_day_skills_open.png") found = true;
            if (!found && file == "career_skill_back.png") found = true;
            if (!found && file == "career_skill_plus.png") found = !m_plusTapped;
            if (!found && file == "career_skill_confirm.png") found = m_plusTapped && !m_confirmationOpen;
            if (!found && file == "career_skill_confirmation_title.png") found = m_confirmationOpen;
            if (!found && file == "career_skill_confirmation_learn.png") found = m_confirmationOpen;
            if (!found && file == "career_skill_learned_title.png") found = m_learnedDialogOpen;
            if (!found && file == "career_skill_learned_close.png") found = m_learnedDialogOpen;
            if (!found && file.find("race_day.png") != std::string::npos) found = true;
            if (m_retryAlarm && file == "race_retry_try_again.png") found = true;
            if (!m_retryAlarm && file == "race_retry_cancel.png") found = true;
        }

        TaskMatchResult result;
        result.found = found;
        result.score = found ? (std::max)(threshold, 0.99) : 0.0;
        result.x = file == "career_skill_plus.png" ? 780
            : file == "summer_rest_action.png" ? 100
            : file == "career_main_action_rest.png" ? 50
            : file == "race_retry_try_again.png" ? 500
            : file == "race_retry_cancel.png" ? 100 : 400;
        result.y = file == "career_skill_plus.png" ? 595
            : file == "summer_rest_action.png" ? 1240
            : file == "career_main_action_rest.png" ? 1160
            : file == "race_retry_try_again.png" ? 1100
            : file == "race_retry_cancel.png" ? 1080 : 1100;
        result.width = 40;
        result.height = 40;
        result.referenceWidth = referenceWidth;
        result.referenceHeight = referenceHeight;
        result.frameId = m_frame ? m_frame->frameId : 0;
        if (m_eventSignatureReplay && file == "event_effects_button.png")
        {
            auto const marker = LoadTemplateImage(path);
            if (!marker) return {};
            UmaAssistant::Vision::Roi bounds = std::nullopt;
            if (roi) bounds = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
            auto const match = UmaAssistant::Vision::find(m_eventImage, *marker,
                bounds, threshold, referenceWidth, referenceHeight);
            result.found = match.found;
            result.score = match.score;
            result.x = match.x;
            result.y = match.y;
            result.width = match.width;
            result.height = match.height;
        }
        return result;
    }

    std::shared_ptr<const UmaAssistant::Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    {
        try { return std::make_shared<const UmaAssistant::Vision::GrayImage>(
            UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(path)); }
        catch (...) { return {}; }
    }

    bool CheckImageChange(const TaskMatchResult&) override { return false; }

    std::vector<TaskOcrDetection> RecognizeText(const std::string&,
        const std::optional<TaskRoi>& roi = std::nullopt) override
    {
        if (m_eventSignatureReplay)
        {
            if (roi && roi->x == 140 && roi->y == 295)
                return {{"Going to the URA Finale!", 160, 300, 260, 30, 0.99}};
            if (roi)
                return {};
            const bool changed = m_frame && m_frame->frameId > 2;
            return {
                {"Turn Three", 100, 850, 140, 30, 0.99},
                {changed ? "Choice B" : "Choice A", 200, 1000, 140, 30, 0.99},
                {"Career", 700, 1200, 120, 30, 0.99}};
        }
        std::vector<TaskOcrDetection> result;
        if (roi && roi->x >= 600 && roi->x < 700 && roi->y < 750)
        {
            if (m_plusTapped)
                result.push_back({"Obtained", 630, 595, 130, 30, 0.99});
            return result;
        }
        const auto skillName = CurrentSkillName();
        if (!skillName.empty())
            result.push_back({skillName, 160, 600, 280, 35, 0.99});
        result.push_back({std::to_string(m_skillPoints), 770, 1155, 65, 30, 0.99});
        result.push_back({std::to_string(m_displayedPrice), 700, 600, 65, 30, 0.99});
        return result;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return m_frame; }
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override
    { return false; }

    bool Tap(int x, int y) override
    {
        taps.emplace_back(x, y);
        const auto file = FoldPath(m_lastTemplate);
        if (m_entryRetryResources && file == m_entryButtonPath) ++entryTaps;
        if (file == "race_day_skills_open.png") ++skillsOpened;
        return true;
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        if (!m_frame || (match.frameId != 0 && match.frameId != m_frame->frameId))
            return false;
        const auto file = FoldPath(m_lastTemplate);
        if (file == "career_skill_plus.png")
        {
            m_plusTapped = true;
            ++plusTaps;
        }
        else if (file == "career_skill_confirm.png")
            m_confirmationOpen = true;
        else if (file == "career_skill_confirmation_learn.png")
        {
            m_confirmationOpen = false;
            m_learnedDialogOpen = true;
            ++learnTaps;
        }
        else if (file == "career_skill_learned_close.png")
        {
            m_learnedDialogOpen = false;
            m_plusTapped = false;
        }
        return Tap(x, y);
    }

    bool Swipe(int, int, int, int, int) override { return true; }
    bool InputText(const std::string&) override { return true; }
    bool SendKey(const std::string&) override { return true; }
    void Sleep(int) override {}
    bool IsCancelled() const override { return false; }
    void Log(const std::string&, const std::string&) override {}
    void EmitEvent(const TaskExecutionEvent&) override {}

    void SetEverything(bool value) { m_matchEverything = value; }
    void SetRetryAlarm(bool value) { m_retryAlarm = value; }
    void SetSkillPoints(int value) { m_skillPoints = value; }
    void SetDisplayedPrice(int value) { m_displayedPrice = value; }
    void SetEventSignatureReplay(bool value) { m_eventSignatureReplay = value; }
    void SetEntryRetryResources(std::shared_ptr<CareerVisualResources> resources)
    {
        m_entryRetryResources = std::move(resources);
        auto const* task = m_entryRetryResources->FindExecutionTask("career_main_action_races");
        if (task && task->templatePath) m_entryButtonPath = FoldPath(*task->templatePath);
    }
    void SetRaceFrameReplay(std::string filename)
    {
        m_raceFrameReplay = true;
        m_raceFrameName = std::move(filename);
    }
    [[nodiscard]] std::uint64_t CaptureCount() const noexcept { return m_nextFrameId; }
    int skillsOpened{0};
    int entryTaps{0};
    bool entryAnimating{false};
    int captureFailuresRemaining{0};
    int plusTaps{0};
    int learnTaps{0};
    std::vector<std::pair<int, int>> taps;
    std::vector<std::string> matchedTemplates;
    std::optional<TaskRoi> lastRoi;
    double lastThreshold{0.0};

private:
    std::string CurrentSkillName() const
    {
        if (m_skillNames.empty()) return {};
        const auto index = (std::min)(skillsOpened > 0 ? skillsOpened - 1 : 0,
            static_cast<int>(m_skillNames.size()) - 1);
        return m_skillNames[static_cast<std::size_t>(index)];
    }

    std::vector<std::string> m_skillNames;
    std::shared_ptr<CareerVisualResources> m_entryRetryResources;
    std::string m_entryButtonPath;
    std::shared_ptr<TaskFrameSnapshot> m_frame;
    UmaAssistant::Vision::GrayImage m_eventImage;
    std::uint64_t m_nextFrameId{0};
    std::string m_lastTemplate;
    bool m_matchEverything{false};
    bool m_eventSignatureReplay{false};
    bool m_raceFrameReplay{false};
    std::string m_raceFrameName;
    bool m_retryAlarm{false};
    bool m_plusTapped{false};
    bool m_confirmationOpen{false};
    bool m_learnedDialogOpen{false};
    int m_skillPoints{500};
    int m_displayedPrice{90};
};

CareerRuntimeConfig PackagedConfig()
{
    CareerRuntimeConfig config;
    config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(
        config.resourceDirectory / "hachimi" / "ura" / "manifest.json");
    return config;
}

class ActualRaceResultContext : public UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext
{
public:
    explicit ActualRaceResultContext(const std::string& filename)
        : image(UmaAssistant::Resources::WicImageDecoder::decode_file(
            std::filesystem::path(filename).is_absolute()
                ? std::filesystem::path(filename)
                : std::filesystem::exists(UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / filename)
                ? UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / filename
                : UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures" / filename)) {}

    bool CaptureScreen() override
    {
        if (alternatingImage) std::swap(image, *alternatingImage);
        if (nextImage && !taps.empty() && captures >= capturesBeforeNextImage) image = *nextImage;
        if (!framesAfterTap.empty() && !taps.empty())
            image = framesAfterTap[(std::min)(taps.size(), framesAfterTap.size()) - 1];
        if (!taps.empty() && captureDelayAfterTapMs > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(captureDelayAfterTapMs));
        if (animateOutsideMarker || animateInsideMarker)
        {
            const std::size_t pixel = animateInsideMarker ? 190 * image.width + 50 : 1000 * image.width + 850;
            const auto value = static_cast<std::uint8_t>((captures % 2) == 0 ? 0 : 255);
            image.gray[pixel] = value;
            for (std::size_t channel = 0; channel < 3; ++channel) image.rgba[pixel * 4 + channel] = value;
        }
        auto snapshot = std::make_shared<TaskFrameSnapshot>();
        snapshot->frameId = ++captures;
        snapshot->sessionGeneration = sessionGeneration;
        snapshot->geometryGeneration = geometryGeneration;
        snapshot->width = image.width;
        snapshot->height = image.height;
        snapshot->grayPixels = image.gray;
        snapshot->rgbaPixels = image.rgba;
        frame = std::move(snapshot);
        return true;
    }
    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }
    std::shared_ptr<const UmaAssistant::Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    {
        return std::make_shared<const UmaAssistant::Vision::GrayImage>(
            UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(path));
    }
    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        const auto marker = LoadTemplateImage(path);
        UmaAssistant::Vision::Roi bounds;
        if (roi) bounds = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
        const auto match = algorithm == "MatchTemplateColor" || algorithm == "MatchTemplateColorText"
            ? UmaAssistant::Vision::find_color(image, *marker, bounds, threshold, referenceWidth, referenceHeight,
                algorithm == "MatchTemplateColorText")
            : UmaAssistant::Vision::find(image, *marker, bounds, threshold, referenceWidth, referenceHeight);
        TaskMatchResult result;
        result.found = match.found;
        result.score = match.score;
        result.x = match.x; result.y = match.y;
        result.width = match.width; result.height = match.height;
        result.frameId = frame ? frame->frameId : 0;
        result.referenceWidth = referenceWidth; result.referenceHeight = referenceHeight;
        result.sessionGeneration = frame ? frame->sessionGeneration : 0;
        result.geometryGeneration = frame ? frame->geometryGeneration : 0;
        // Entry-only replays can start after a separately verified strategy;
        // all entry buttons and result markers still use the actual pixels.
        if (replayConfiguredStrategy && FoldPath(path) == "race_strategy_selected_marker.png")
        {
            result.found = true;
            result.score = 1.0;
        }
        return result;
    }
    std::vector<TaskOcrDetection> RecognizeText(const std::string&, const std::optional<TaskRoi>&) override { return {}; }
    bool IsCancelled() const override { return cancelAfterCaptures != 0 && captures >= cancelAfterCaptures; }
    UmaAssistant::Vision::GrayImage image;
    std::optional<UmaAssistant::Vision::GrayImage> nextImage;
    std::vector<UmaAssistant::Vision::GrayImage> framesAfterTap;
    std::optional<UmaAssistant::Vision::GrayImage> alternatingImage;
    std::shared_ptr<const TaskFrameSnapshot> frame;
    std::uint64_t captures{0};
    std::uint64_t cancelAfterCaptures{0};
    std::uint64_t sessionGeneration{1};
    std::uint64_t geometryGeneration{1};
    bool animateOutsideMarker{false};
    bool animateInsideMarker{false};
    std::uint64_t capturesBeforeNextImage{0};
    int captureDelayAfterTapMs{0};
    bool replayConfiguredStrategy{false};
};

std::uint64_t EventSignature(std::vector<std::pair<int, std::string>> detections)
{
    std::sort(detections.begin(), detections.end(), [](const auto& left, const auto& right) {
        return left.first < right.first;
    });
    std::uint64_t hash = 14695981039346656037ull;
    for (const auto& [_, text] : detections)
    {
        for (const unsigned char character : text)
            if (std::isalnum(character))
            {
                hash ^= static_cast<unsigned char>(std::tolower(character));
                hash *= 1099511628211ull;
            }
        hash ^= 0xff;
        hash *= 1099511628211ull;
    }
    return hash == 0 ? 1 : hash;
}

} // namespace

TEST_CASE("Playback hands the actual alarm dialog back to Career before any result input",
    "[CareerRuntime][CareerAlarmHandoff]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("race_retry_oguri_3rd_mumu_20261014.png");
    auto definition = resources->GetExecutionDefinition();
    // One production monitor iteration proves the exit without a 30 s failure wait.
    definition.tasks.at("race_runner_playback_result_monitor").timeoutMilliseconds = 0;
    TaskGraphRunner runner;
    const auto result = runner.Run(definition, "race_runner_playback_result_monitor", context);
    INFO(result.errorMessage);
    REQUIRE(result.status == TaskGraphStatus::Success);
    REQUIRE(result.finalTask == "race_runner_retry_pending");
    CHECK(context.taps.empty());
    CHECK(definition.tasks.at(result.finalTask).outcome == "race.retry.pending");

    CareerActionExecutor executor(config, resources);
    CareerObservation source;
    source.screenId = "race_playback_start";
    source.screenKind = CareerScreenKind::RaceExecution;
    REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeRace("finale"), source, context));
    const auto destination = executor.TakeConfirmedObservation();
    REQUIRE(destination.has_value());
    CHECK(destination->screenId == "race_retry_dialog");
    CHECK(context.taps.empty());
}

TEST_CASE("Race playback action returns pending retry without consuming an alarm",
    "[CareerRuntime][CareerAlarmHandoff]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("senior_february_playback_ready2.png");
    ActualRaceResultContext alarm("race_retry_oguri_3rd_mumu_20261014.png");
    context.nextImage = alarm.image;
    context.cancelAfterCaptures = 16;
    CareerScreenObserver observer(resources);
    const auto source = observer.ObserveScreens(context, {"race_playback_start"});
    REQUIRE(source.screenId == "race_playback_start");
    CareerActionExecutor executor(config, resources);
    const auto intent = ActionIntent::MakeRace("finale");
    REQUIRE(executor.ExecuteForObservation(intent, source, context) == ActionExecutionStatus::Success);
    CHECK(executor.TakeLastActionOutcome() == "race.retry.pending");
    CHECK_FALSE(executor.TakeRaceRetryDeclined().has_value());
    REQUIRE(executor.ConfirmCompletionFromObservation(intent, source, context));
    const auto destination = executor.TakeConfirmedObservation();
    REQUIRE(destination.has_value());
    CHECK(destination->screenId == "race_retry_dialog");
    REQUIRE(context.taps.size() == 1); // Race! only; no Try Again / Cancel / Next.
    CHECK(context.taps.front().second >= 1380);
}

TEST_CASE("Declined alarm retries recognize the ordinary Replay result checkpoint",
    "[CareerRuntime][CareerAlarmHandoff]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("goal2_cosmos_playback_end.png");
    CareerScreenObserver observer(resources);
    const auto transition = CareerTransitionPolicy::AfterAction({}, "race_retry_dialog", "cancel", "race.retry.declined");
    const auto result = observer.ObserveTransition(context, transition);
    CHECK(result.screenId == "race_runner_result");
    CHECK(context.taps.empty());
}

TEST_CASE("Actual alarm cancellation returns to the Replay checkpoint exactly once",
    "[CareerRuntime][CareerAlarmHandoff]")
{
    auto config = PackagedConfig();
    config.retryFailedRaceWithAlarmClock = false;
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("race_retry_oguri_3rd_mumu_20261014.png");
    ActualRaceResultContext next("goal2_cosmos_playback_end.png");
    context.nextImage = next.image;
    context.cancelAfterCaptures = 16;
    CareerScreenObserver observer(resources);
    const auto source = observer.ObserveScreens(context, {"race_retry_dialog"});
    REQUIRE(source.screenId == "race_retry_dialog");
    const auto retryButton = resources->MatchExecutionTask("race_retry_try_again_probe", context);
    INFO(retryButton.score);
    REQUIRE(retryButton.found);
    CareerActionExecutor executor(config, resources);
    const auto intent = ActionIntent::MakeRace("finale");
    const auto status = executor.ExecuteForObservation(intent, source, context);
    std::string diagnostics;
    for (const auto& log : context.logs) diagnostics += log + "\n";
    INFO(diagnostics);
    REQUIRE(status == ActionExecutionStatus::Success);
    CHECK(executor.TakeRaceRetryDeclined() == true);
    REQUIRE(executor.ConfirmCompletionFromObservation(intent, source, context));
    const auto destination = executor.TakeConfirmedObservation();
    REQUIRE(destination.has_value());
    CHECK(destination->screenId == "race_runner_result");
    REQUIRE(context.taps.size() == 1);
    CHECK(context.taps.front().first >= 50);
    CHECK(context.taps.front().first < 450);
    CHECK(context.taps.front().second >= 1080);
    CHECK(context.taps.front().second < 1220);
}

TEST_CASE("Actual retry input remains unconfirmed until the runner appears and is never repeated",
    "[CareerRuntime][CareerAlarmHandoff]")
{
    auto config = PackagedConfig();
    config.retryFailedRaceWithAlarmClock = true;
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("race_retry_oguri_3rd_mumu_20261014.png");
    context.cancelAfterCaptures = 8;
    CareerScreenObserver observer(resources);
    const auto source = observer.ObserveScreens(context, {"race_retry_dialog"});
    REQUIRE(source.screenId == "race_retry_dialog");
    CareerActionExecutor executor(config, resources);
    // Keep the modal visible after the simulated tap: no runner proof exists.
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeRace("finale"), source, context)
        == ActionExecutionStatus::Cancelled);
    CHECK_FALSE(executor.TakeLastActionOutcome().has_value());
    CHECK_FALSE(executor.TakeRaceRetryDeclined().has_value());
    REQUIRE(context.taps.size() == 1);
    CHECK(context.taps.front().first >= 450);
    CHECK(context.taps.front().first < 850);
}

TEST_CASE("Normal career actions execute the authored rest and alarm-clock graphs", "[CareerRuntime][ActionReplay]")
{
    auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    CAPTURE(config.manifestPath);
    std::string loadDiagnostics;
    for (const auto& diagnostic : resources->GetLoadDiagnostics())
        loadDiagnostics += (loadDiagnostics.empty() ? "" : "\n") + diagnostic;
    CAPTURE(loadDiagnostics);
    REQUIRE(resources->IsReady());

    SECTION("Classic July rest resolves to the summer-rest task")
    {
        CareerActionReplayContext context;
        context.SetEverything(true);
        CareerActionExecutor executor(config, resources);
        CareerObservation observation;
        observation.screenId = "career_main";
        observation.screenKind = CareerScreenKind::MainTurn;
        observation.turnPositionText = "Classic Year Early July";
        auto intent = ActionIntent::MakeRest("summer break");
        REQUIRE(executor.ExecuteForObservation(intent, observation, context) == ActionExecutionStatus::Success);
        CHECK(std::any_of(context.matchedTemplates.begin(), context.matchedTemplates.end(), [](const auto& path) {
            return FoldPath(path) == "summer_rest_action.png";
        }));
        CHECK(std::none_of(context.matchedTemplates.begin(), context.matchedTemplates.end(), [](const auto& path) {
            return FoldPath(path) == "career_main_action_rest.png";
        }));
        CHECK(std::any_of(context.taps.begin(), context.taps.end(), [](const auto& tap) {
            return tap.first >= 55 && tap.first <= 245 && tap.second >= 1225 && tap.second <= 1310;
        }));
    }

    SECTION("Alarm-clock retry uses the enabled authored action")
    {
        config.retryFailedRaceWithAlarmClock = true;
        CareerActionReplayContext context;
        context.SetEverything(true);
        context.SetRetryAlarm(true);
        CareerActionExecutor executor(config, resources);
        CareerObservation observation;
        observation.screenId = "race_retry_dialog";
        observation.screenKind = CareerScreenKind::ConfirmationDialog;
        const auto status = executor.ExecuteForObservation(
            ActionIntent::MakeRace("goal", "pace"), observation, context);
        REQUIRE(status == ActionExecutionStatus::Success);
        CHECK(executor.TakeRaceRetryDeclined() == std::optional<bool>(false));
        CHECK(std::any_of(context.matchedTemplates.begin(), context.matchedTemplates.end(), [](const auto& path) {
            return FoldPath(path) == "race_retry_try_again.png";
        }));
        CHECK(std::any_of(context.taps.begin(), context.taps.end(), [](const auto& tap) {
            return tap.first >= 450 && tap.second >= 1080;
        }));
    }

    SECTION("Disabled alarm-clock retry selects the authored cancel action")
    {
        config.retryFailedRaceWithAlarmClock = false;
        CareerActionReplayContext context;
        context.SetEverything(true);
        CareerActionExecutor executor(config, resources);
        CareerObservation observation;
        observation.screenId = "race_retry_dialog";
        observation.screenKind = CareerScreenKind::ConfirmationDialog;
        const auto status = executor.ExecuteForObservation(
            ActionIntent::MakeRace("goal", "pace"), observation, context);
        REQUIRE(status == ActionExecutionStatus::Success);
        CHECK(executor.TakeRaceRetryDeclined() == std::optional<bool>(true));
        CHECK(std::any_of(context.matchedTemplates.begin(), context.matchedTemplates.end(), [](const auto& path) {
            return FoldPath(path) == "race_retry_cancel.png";
        }));
        CHECK(std::none_of(context.matchedTemplates.begin(), context.matchedTemplates.end(), [](const auto& path) {
            return FoldPath(path) == "race_retry_try_again.png";
        }));
    }
}

TEST_CASE("Normal skills use same-frame OCR, price checks, and confirmed catalog actions", "[CareerRuntime][SkillReplay]")
{
    auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    CAPTURE(config.manifestPath);
    std::string loadDiagnostics;
    for (const auto& diagnostic : resources->GetLoadDiagnostics())
        loadDiagnostics += (loadDiagnostics.empty() ? "" : "\n") + diagnostic;
    CAPTURE(loadDiagnostics);
    REQUIRE(resources->IsReady());

    SECTION("Displayed price above the observed points returns without a purchase tap")
    {
        config.normalSkillIds = {200012}; // Right-Handed ○, fixed global catalog record.
        CareerActionReplayContext context({"Right-Handed ○"});
        context.SetSkillPoints(100);
        context.SetDisplayedPrice(101);
        CareerSkillLearningFlow flow(config, resources);
        REQUIRE(flow.RunBeforeRace(context) == ActionExecutionStatus::Success);
        CHECK(context.skillsOpened == 1);
        CHECK(context.plusTaps == 0);
        CHECK(context.learnTaps == 0);
    }

    SECTION("Confirmed configured skills are purchased in input order and skipped on a later pass")
    {
        // Descending IDs distinguish user order from an implementation that
        // silently sorts the configured skills before scanning or purchasing.
        config.normalSkillIds = {200022, 200012};
        CareerActionReplayContext context({"Left-Handed ○", "Right-Handed ○"});
        CareerSkillLearningFlow flow(config, resources);
        REQUIRE(flow.RunBeforeRace(context) == ActionExecutionStatus::Success);
        CHECK(context.skillsOpened == 2);
        CHECK(context.plusTaps == 2);
        CHECK(context.learnTaps == 2);

        const auto openedAfterConfirmedPurchase = context.skillsOpened;
        REQUIRE(flow.RunBeforeRace(context) == ActionExecutionStatus::Success);
        CHECK(context.skillsOpened == openedAfterConfirmedPurchase);
        CHECK(context.plusTaps == 2);
    }
}

TEST_CASE("Race entry retries once after the source remains confirmed", "[CareerRuntime][CareerParity]")
{
    for (bool const animating : {false, true})
    {
        CAPTURE(animating);
        auto config = PackagedConfig();
        auto resources = std::make_shared<CareerVisualResources>(config);
        REQUIRE(resources->IsReady());
        CareerActionReplayContext context;
        context.SetEntryRetryResources(resources);
        context.entryAnimating = animating;
        CareerActionExecutor executor(config, resources);
        UmaAssistant::CareerDomain::CareerObservation source;
        source.screenId = "career_main";
        source.screenKind = UmaAssistant::CareerDomain::CareerScreenKind::MainTurn;
        auto const intent = UmaAssistant::CareerDomain::ActionIntent::MakeRace("recommended");
        REQUIRE(executor.ExecuteForObservation(intent, source, context) == ActionExecutionStatus::Success);
        context.captureFailuresRemaining = 1;
        REQUIRE(executor.ConfirmCompletionFromObservation(intent, source, context));
        CHECK(context.entryTaps == (animating ? 1 : 2));
        auto const returned = executor.TakeConfirmedObservation();
        REQUIRE(returned.has_value());
        CHECK(returned->screenId == "race_list");
    }
}

TEST_CASE("Same-title event choices require a changed same-frame OCR signature", "[CareerRuntime][ActionReplay]")
{
    auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    CAPTURE(config.manifestPath);
    std::string loadDiagnostics;
    for (const auto& diagnostic : resources->GetLoadDiagnostics())
        loadDiagnostics += (loadDiagnostics.empty() ? "" : "\n") + diagnostic;
    CAPTURE(loadDiagnostics);
    REQUIRE(resources->IsReady());

    CareerActionReplayContext context;
    context.SetEventSignatureReplay(true);
    CareerActionExecutor executor(config, resources);
    CareerObservation source;
    source.screenId = "event_choice";
    source.screenKind = CareerScreenKind::EventChoice;
    source.eventTitle = "Going to the URA Finale!";
    source.eventId = "ura_entry";
    source.eventStateSignature = EventSignature({
        {850, "Turn Three"}, {1000, "Choice A"}, {1200, "Career"}});

    REQUIRE(executor.ConfirmCompletionFromObservation(
        ActionIntent::MakeEventChoice(1), source, context));
    CHECK(context.CaptureCount() >= 4);
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed.has_value());
    CHECK(confirmed->screenId == source.screenId);
    CHECK(confirmed->eventTitle == source.eventTitle);
    CHECK(confirmed->eventId == source.eventId);
    CHECK(confirmed->eventStateSignature.has_value());
    CHECK(*confirmed->eventStateSignature != *source.eventStateSignature);
}

TEST_CASE("Runtime race-result captures resolve through the packaged catalog collection", "[CareerRuntime][ActionReplay]")
{
    const auto config = PackagedConfig();
    CareerVisualResources resources(config);
    CAPTURE(config.manifestPath);
    REQUIRE(resources.IsReady());

    const auto capture = resources.ResolveCollectionAsset(
        "career.runtime_frame:debut_race_result_wait");
    REQUIRE(capture.has_value());
    CHECK(FoldPath(capture->path) == "debut_race_result_wait.png");
    CHECK(std::filesystem::is_regular_file(capture->path));
    CHECK_FALSE(resources.ResolveCollectionAsset(
        "career.runtime_frame:../debut_race_result_wait").has_value());

    const auto* matchPolicy = resources.FindRegion("career.race.result.runtime_frame_match");
    REQUIRE(matchPolicy != nullptr);
    CHECK(matchPolicy->value("threshold", 0.0) == 0.8);
    CHECK(matchPolicy->value("metadata", nlohmann::json::object())
        .value("waitTimeoutMs", 0) == 2500);
    CHECK(matchPolicy->value("metadata", nlohmann::json::object())
        .value("pollIntervalMs", 0) == 250);
}

TEST_CASE("Race results require a same-frame packaged outcome match", "[CareerRuntime][RaceResultReplay]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    const CareerRaceResultExpectation expectation{
        "junior_debut", "career.runtime_frame:debut_race_result_wait", 1, 0.95};

    SECTION("A matching capture supplies the expected placement and confidence")
    {
        CareerActionReplayContext context;
        context.SetRaceFrameReplay("debut_race_result_wait.png");
        CareerActionExecutor executor(config, resources);
        const auto result = executor.MatchRaceResult(expectation, context);
        REQUIRE(result.has_value());
        CHECK(result->raceId == "junior_debut");
        CHECK(result->placement == 1);
        CHECK(result->confidence == Catch::Approx(0.95));
        CHECK(context.lastRoi == std::nullopt); // The catalog policy intentionally matches the full frame.
        CHECK(context.lastThreshold == Catch::Approx(0.95));
    }

    SECTION("A different race capture remains unknown and cannot be applied")
    {
        CareerActionReplayContext context;
        context.SetRaceFrameReplay("year2_nhk_result_detail.png");
        CareerActionExecutor executor(config, resources);
        CHECK_FALSE(executor.MatchRaceResult(expectation, context).has_value());
        CHECK(context.CaptureCount() > 1);
    }
}

TEST_CASE("MuMu Cosmos Sho result satisfies the View Results destination marker", "[CareerRuntime][CareerRaceTransition]")
{
    const auto config = PackagedConfig();
    CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    const auto* task = resources.FindExecutionTask("race_runner_view_results_tap");
    REQUIRE(task != nullptr);
    REQUIRE(task->transitionTemplates.size() == 2);
    const auto frame = UmaAssistant::Resources::WicImageDecoder::decode_file(
        UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR)
        / "testdata/hachimi/ura/captures/race_view_results_cosmos_sho_3rd_mumu_20261012.png");
    const auto marker = UmaAssistant::Resources::WicImageDecoder::decode_file(
        UmaAssistant::Paths::FromUtf8(task->transitionTemplates[0]));
    REQUIRE(task->transitionRois[0].has_value());
    const auto& bounds = *task->transitionRois[0];
    REQUIRE(bounds.size() == 4);
    const auto match = UmaAssistant::Vision::find(frame, marker,
        std::array<std::int32_t, 4>{bounds[0], bounds[1], bounds[2], bounds[3]},
        task->transitionThreshold, task->referenceWidth, task->referenceHeight);
    CHECK(match.found);
    CHECK(match.score >= task->transitionThreshold);
}

TEST_CASE("Direct View Results placings are recognized after the runner checkpoint", "[CareerRuntime][CareerRaceTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    const auto filename = GENERATE(
        "race_view_results_cosmos_sho_3rd_mumu_20261012.png",
        "race_view_results_fuyo_stakes_1st_mumu_20261012.png");
    CAPTURE(filename);
    ActualRaceResultContext context(filename);
    CareerScreenObserver observer(resources);
    CareerObservation source;
    source.screenId = "race_runner";
    source.screenKind = CareerScreenKind::RaceExecution;
    observer.RememberObservation(source);
    observer.SetRaceRunnerResultExpected(true);
    const auto observed = observer.ObserveScreens(context, {"race_runner_result"});
    CHECK(observed.screenId == "race_runner_result");
    CHECK(observed.screenKind == CareerScreenKind::RaceResult);
    CHECK(context.captures == 2);

    SECTION("Outer transition confirmation hands the actual result to the coordinator")
    {
        CareerActionExecutor executor(config, resources);
        executor.SetRaceRunnerResultExpected(true);
        REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeRace("fans"), source, context));
        const auto confirmed = executor.TakeConfirmedObservation();
        REQUIRE(confirmed);
        CHECK(confirmed->screenId == "race_runner_result");
        CHECK(context.taps.empty());
    }
    SECTION("A Next button cannot establish a result without the race checkpoint policy")
    {
        observer.RememberObservation(source);
        observer.SetRaceRunnerResultExpected(false);
        CHECK(observer.ObserveScreens(context, {"race_runner_result"}).screenId == "unknown");
    }
    SECTION("The fallback is released after leaving the runner checkpoint")
    {
        observer.RememberObservation(observed);
        CHECK(observer.ObserveScreens(context, {"race_runner_result"}).screenId == "unknown");
    }
    SECTION("Recovery clears the pending runner fallback")
    {
        observer.ResetForRecovery();
        observer.RememberObservation(source);
        CHECK(observer.ObserveScreens(context, {"race_runner_result"}).screenId == "unknown");
    }
    SECTION("Expected results still require a visible Next marker")
    {
        observer.RememberObservation(source);
        std::fill(context.image.gray.begin(), context.image.gray.end(), std::uint8_t{0});
        std::fill(context.image.rgba.begin(), context.image.rgba.end(), std::uint8_t{0});
        CHECK(observer.ObserveScreens(context, {"race_runner_result"}).screenId == "unknown");
    }
}

TEST_CASE("MuMu finale playback resumes with the visible Race glyph",
    "[CareerRuntime][CareerRaceEntryRepair]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("race_playback_final_oguri_mumu_20261015.png");
    ActualRaceResultContext retry("race_retry_oguri_3rd_mumu_20261014.png");
    context.nextImage = retry.image;
    context.cancelAfterCaptures = 20;
    REQUIRE(context.CaptureScreen());
    const auto glyph = resources->MatchExecutionTask("race_runner_playback_start", context);
    CAPTURE(glyph.score);
    REQUIRE(glyph.found);
    CareerScreenObserver observer(resources);
    auto source = observer.ObserveScreens(context, {"race_playback", "race_playback_start"});
    REQUIRE(source.screenId == "race_playback_start");
    SECTION("Resume from the Race glyph checkpoint") {}
    SECTION("Continue from the legacy playback screen") { source.screenId = "race_playback"; }
    CareerActionExecutor executor(config, resources);
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeRace("finale"), source, context)
        == ActionExecutionStatus::Success);
    CHECK(executor.TakeLastActionOutcome() == "race.retry.pending");
    REQUIRE(context.taps.size() == 1);
    CHECK(context.taps.front().first >= 270);
    CHECK(context.taps.front().first < 630);
    CHECK(context.taps.front().second >= 1410);
    CHECK(context.taps.front().second < 1520);
}

TEST_CASE("Race playback recovery stages resume the shared graph through the retry checkpoint",
    "[CareerRuntime][CareerRaceEntryRepair]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext ready("race_playback_final_oguri_mumu_20261015.png");
    ActualRaceResultContext retry("race_retry_oguri_3rd_mumu_20261014.png");
    std::string screenId;
    std::string filename;
    std::vector<UmaAssistant::Vision::GrayImage> continuation;
    SECTION("Playback settings")
    {
        screenId = "race_playback_settings";
        filename = "resource/hachimi/career/race/templates/runtime_frames/ura_prelim_playback_ready.png";
        continuation = {ready.image, retry.image};
    }
    SECTION("Live race")
    {
        screenId = "race_live";
        filename = "resource/hachimi/career/race/templates/runtime_frames/ura_final_race_live.png";
        continuation = {retry.image};
    }
    ActualRaceResultContext context(filename);
    context.framesAfterTap = continuation;
    context.cancelAfterCaptures = 30;
    CareerObservation source;
    source.screenId = screenId;
    source.screenKind = CareerScreenKind::RaceExecution;
    CareerActionExecutor executor(config, resources);
    const auto status = executor.ExecuteForObservation(ActionIntent::MakeRace("finale"), source, context);
    std::string diagnostics;
    for (const auto& log : context.logs) diagnostics += log + "\n";
    INFO(diagnostics);
    REQUIRE(status == ActionExecutionStatus::Success);
    CHECK(executor.TakeLastActionOutcome() == "race.retry.pending");
    CHECK(context.taps.size() == continuation.size());
    REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeRace("finale"), source, context));
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed);
    CHECK(confirmed->screenId == "race_retry_dialog");
}

TEST_CASE("Attributes entry prefers available View Results after configuring strategy",
    "[CareerRuntime][CareerRaceEntryRepair]")
{
    auto config = PackagedConfig();
    config.normalLineupStrategy = "late";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("resource/hachimi/career/race/templates/runtime_frames/ura_final_attributes2.png");
    context.replayConfiguredStrategy = true;
    ActualRaceResultContext result("race_view_results_fuyo_stakes_1st_mumu_20261012.png");
    context.nextImage = result.image;
    context.cancelAfterCaptures = 10000; // Allow the bounded absent-lock probe to expire.
    CareerObservation source;
    source.screenId = "race_attributes";
    source.screenKind = CareerScreenKind::RaceExecution;
    CareerActionExecutor executor(config, resources);
    const auto status = executor.ExecuteForObservation(ActionIntent::MakeRace("finale"), source, context);
    std::string diagnostics;
    for (const auto& log : context.logs) diagnostics += log + "\n";
    INFO(diagnostics);
    REQUIRE(status == ActionExecutionStatus::Success);
    REQUIRE(context.taps.size() == 1);
    CHECK(context.taps.front().first >= 150);
    CHECK(context.taps.front().first < 450);
    executor.SetRaceRunnerResultExpected(true);
    REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeRace("finale"), source, context));
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed);
    CHECK(confirmed->screenId == "race_runner_result");
}

TEST_CASE("Training can finish directly on the actual mandatory debut Race List",
    "[CareerRuntime][CareerTrainingRaceHandoff]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("race_list_debut_after_training_mumu_20261013.png");
    CareerScreenObserver observer(resources);
    auto const current = observer.ObserveScreens(context, {"race_list"}, true);
    REQUIRE(current.screenId == "race_list");
    context.cancelAfterCaptures = context.captures + 3;
    CareerActionExecutor executor(config, resources);
    CareerObservation source;
    source.screenId = "training_selection";
    source.screenKind = CareerScreenKind::TrainingSelect;
    REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeTraining("guts"), source, context));
    auto const destination = executor.TakeConfirmedObservation();
    REQUIRE(destination.has_value());
    CHECK(destination->screenId == "race_list");
    CHECK(context.taps.empty());
}

TEST_CASE("Career Rank keeps main's default recognition threshold and confirms settlement handoff", "[CareerRuntime][CareerSettlementTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    const auto* screen = resources->FindScreen("career_rank");
    REQUIRE(screen != nullptr);
    REQUIRE(screen->templates.size() == 1);
    CHECK(screen->templates.front().threshold == Catch::Approx(0.78));
    ActualRaceResultContext context("career_rank_bplus_8533_mumu_20261013.png");
    SECTION("Current B plus result") {}
    SECTION("Packaged A rank result")
    {
        context.image = UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(screen->templates.front().path);
    }
    REQUIRE(context.CaptureScreen());
    const auto& marker = screen->templates.front();
    const auto match = context.MatchTemplateAdvanced(marker.path, marker.threshold, marker.roi,
        marker.algorithm, 900, 1600, {});
    CAPTURE(match.score, marker.threshold);
    CHECK(match.found);
    CareerObservation source;
    source.screenId = "complete_career";
    source.screenKind = CareerScreenKind::Settlement;
    CareerScreenObserver observer(resources);
    observer.RememberObservation(source);
    const auto observed = observer.ObserveScreens(context, {"career_rank"});
    CHECK(observed.screenId == "career_rank");
    CHECK(observed.screenKind == CareerScreenKind::Settlement);
    CHECK(context.taps.empty());
    CareerActionExecutor executor(config, resources);
    REQUIRE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeAdvance(), source, context));
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed);
    CHECK(confirmed->screenId == "career_rank");
    REQUIRE(executor.ExecuteAction("career_rank", "career.next", context) == ActionExecutionStatus::Success);
    REQUIRE(context.taps.size() == 1);
    CHECK(context.taps.front().first > 400);
    CHECK(context.taps.front().first < 500);
    CHECK(context.taps.front().second > 1400);
    CHECK(context.taps.front().second < 1550);
}

TEST_CASE("Packaged rank and Sparks captures remain distinct during settlement", "[CareerRuntime][CareerSettlementTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    const auto id = GENERATE("career_rank", "sparks", "sparks_confirmation");
    CAPTURE(id);
    const auto* expected = resources->FindScreen(id);
    REQUIRE(expected != nullptr);
    REQUIRE_FALSE(expected->templates.empty());
    ActualRaceResultContext context("career_rank_bplus_8533_mumu_20261013.png");
    context.image = UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(expected->templates.front().path);
    CareerObservation source;
    source.screenId = std::string(id) == "career_rank" ? "complete_career"
        : std::string(id) == "sparks" ? "career_rank" : "sparks";
    source.screenKind = CareerScreenKind::Settlement;
    CareerScreenObserver observer(resources);
    observer.RememberObservation(source);
    CHECK(observer.ObserveCareer(context).screenId == id);
    CHECK(context.taps.empty());
}

TEST_CASE("Rewards advances from Bond Level to Support Cards while keeping its screen ID", "[CareerRuntime][CareerRewardsTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("rewards_support_mumu_20261013.png");
    context.nextImage = context.image;
    context.image = UmaAssistant::Resources::WicImageDecoder::decode_file(
        config.resourceDirectory / "hachimi/ura/testdata/captures/runtime_frames/ura_rewards_loaded.png");
    CareerObservation previous;
    previous.screenId = "career_result";
    previous.screenKind = CareerScreenKind::Settlement;
    CareerScreenObserver observer(resources);
    observer.RememberObservation(previous);
    const auto source = observer.ObserveCareer(context);
    REQUIRE(source.screenId == "rewards");
    REQUIRE(source.pageStateEvidence);
    CareerActionExecutor executor(config, resources);
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeAdvance(), source, context) == ActionExecutionStatus::Success);
    REQUIRE(context.taps.size() == 1);
    const auto success = executor.ConfirmCompletionFromObservation(ActionIntent::MakeAdvance(), source, context);
    CHECK(success);
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed);
    CHECK(confirmed->screenId == "rewards");
    REQUIRE(confirmed->pageStateEvidence);
    CHECK(source.pageStateEvidence->signature != confirmed->pageStateEvidence->signature);
    CHECK(context.taps.size() == 1);
}

TEST_CASE("Rewards only confirms stable authored content changes within the same device geometry", "[CareerRuntime][CareerRewardsTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("rewards_support_mumu_20261013.png");
    CareerScreenObserver observer(resources);
    CareerObservation prior;
    prior.screenId = "career_result";
    observer.RememberObservation(prior);
    const auto source = observer.ObserveCareer(context);
    REQUIRE(source.screenId == "rewards");
    REQUIRE(source.pageStateEvidence);
    CareerActionExecutor executor(config, resources);
    SECTION("An unchanged rewards page cannot confirm a successful tap") {}
    SECTION("Animated pixels outside the group title do not count as progression") { context.animateOutsideMarker = true; }
    SECTION("An unstable group title is not reliable progress evidence") { context.animateInsideMarker = true; }
    SECTION("Changed content in a new screenshot geometry cannot confirm the old action")
    {
        ++context.geometryGeneration;
        context.image = UmaAssistant::Resources::WicImageDecoder::decode_file(
            config.resourceDirectory / "hachimi/ura/testdata/captures/runtime_frames/ura_rewards_loaded.png");
    }
    SECTION("Changed content in a new device session cannot confirm the old action")
    {
        ++context.sessionGeneration;
        context.image = UmaAssistant::Resources::WicImageDecoder::decode_file(
            config.resourceDirectory / "hachimi/ura/testdata/captures/runtime_frames/ura_rewards_loaded.png");
    }
    context.cancelAfterCaptures = context.captures + 6;
    CHECK_FALSE(executor.ConfirmCompletionFromObservation(ActionIntent::MakeAdvance(), source, context));
    CHECK_FALSE(executor.TakeConfirmedObservation());
    CHECK(context.taps.empty());
}

TEST_CASE("Two live Support Cards captures retain the same Rewards page marker", "[CareerRuntime][CareerRewardsTransition]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("rewards_support_mumu_20261013.png");
    context.alternatingImage = UmaAssistant::Resources::WicImageDecoder::decode_file(
        UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR)
        / "testdata/hachimi/ura/captures/rewards_support_second_mumu_20261013.png");
    CareerScreenObserver observer(resources);
    CareerObservation prior;
    prior.screenId = "career_result";
    observer.RememberObservation(prior);
    const auto first = observer.ObserveCareer(context);
    const auto second = observer.ObserveCareer(context);
    REQUIRE(first.screenId == "rewards");
    REQUIRE(second.screenId == "rewards");
    REQUIRE(first.pageStateEvidence);
    REQUIRE(second.pageStateEvidence);
    CHECK(first.pageStateEvidence->signature == second.pageStateEvidence->signature);
    CHECK(context.taps.empty());
}

TEST_CASE("Settlement retries a full unstable observation after slow capture instead of exhausting one sample", "[CareerRuntime][CareerSettlementAudit]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("rewards_support_mumu_20261013.png");
    CareerObservation prior;
    prior.screenId = "career_result";
    CareerScreenObserver observer(resources);
    observer.RememberObservation(prior);
    const auto source = observer.ObserveCareer(context);
    REQUIRE(source.screenId == "rewards");
    context.nextImage = UmaAssistant::Resources::WicImageDecoder::decode_file(
        UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR)
        / "testdata/hachimi/ura/captures/rewards_collected_ticket_mumu_20261013.png");
    CareerActionExecutor executor(config, resources);
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeAdvance(), source, context) == ActionExecutionStatus::Success);
    context.capturesBeforeNextImage = context.captures + 1;
    context.captureDelayAfterTapMs = 8000;
    CHECK(executor.ConfirmCompletionFromObservation(ActionIntent::MakeAdvance(), source, context));
    const auto confirmed = executor.TakeConfirmedObservation();
    REQUIRE(confirmed);
    CHECK(confirmed->screenId == "rewards_collected");
    CHECK(context.taps.size() == 1);
}

TEST_CASE("Coordinator replays the actual completed settlement through all six actions to Home", "[CareerRuntime][CareerSettlementAudit]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    class ChainContext : public ActualRaceResultContext
    {
    public:
        ChainContext() : ActualRaceResultContext("rewards_collected_ticket_mumu_20261013.png") {}
        bool CaptureScreen() override
        {
            image = stages.at((std::min)(taps.size(), stages.size() - 1));
            return ActualRaceResultContext::CaptureScreen();
        }
        std::vector<UmaAssistant::Vision::GrayImage> stages;
    } context;
    for (const auto* filename : {"rewards_collected_ticket_mumu_20261013.png", "rewards_ura_mumu_20261013.png",
        "event_reward_mumu_20261013.png", "rewards_event_mumu_20261013.png", "follow_trainer_limit_mumu_20261013.png",
        "career_complete_mumu_20261013.png", "settlement_home_mumu_20261013.png"})
        context.stages.push_back(UmaAssistant::Resources::WicImageDecoder::decode_file(
            UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures" / filename));
    CareerFlowCoordinator coordinator(std::make_shared<CareerScreenObserver>(resources),
        std::make_shared<CareerActionExecutor>(config, resources),
        std::make_shared<UraScenarioModule>(), std::make_shared<DefaultTrainingStrategy>(), config);
    const auto result = coordinator.RunToCompletion(context, 10);
    CAPTURE(result.failureReason);
    CHECK(result.status == CareerRunStatus::Completed);
    CHECK(context.taps.size() == 6);
}

TEST_CASE("Training selection executes single tap on raised card and does not hit rest button", "[CareerRuntime][CareerTrainingAction]")
{
    const auto config = PackagedConfig();
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ActualRaceResultContext context("resource/hachimi/ura/testdata/captures/runtime_frames/training_selection_ura.png");
    CareerActionExecutor executor(config, resources);
    CareerObservation observation;
    observation.screenId = "training_selection";

    const auto status = executor.ExecuteForObservation(ActionIntent::MakeTraining("speed", "Speed training"), observation, context);
    REQUIRE(status == ActionExecutionStatus::Success);
    // Exactly 1 tap should be executed
    REQUIRE(context.taps.size() == 1);
    // Tap must be on the raised card button, not the bottom unraised area
    CHECK(context.taps[0].first >= 20);
    CHECK(context.taps[0].first <= 200);
    CHECK(context.taps[0].second >= 1250);
    CHECK(context.taps[0].second <= 1350);
}
