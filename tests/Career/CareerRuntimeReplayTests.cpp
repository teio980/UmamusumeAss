#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "UmaAssistantCore/Resources/ImageTransforms.hpp"
#include "UmaAssistantCore/Recognition/WindowsOcrRecognizer.hpp"
#include "UmaAssistantCore/Recognition/NumericOcrBatch.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "UmaAssistantCore/Vision/TemplateMatcher.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

namespace
{

using Json = nlohmann::json;
using UmaAssistant::Career::CareerRuntimeConfig;
using UmaAssistant::Career::CareerScreenObserver;
using UmaAssistant::Career::CareerVisualResources;
using UmaAssistant::Career::CareerTransitionPolicy;
using UmaAssistant::TaskEngine::ITaskExecutionContext;
using UmaAssistant::TaskEngine::TaskFrameSnapshot;
using UmaAssistant::TaskEngine::TaskMatchResult;
using UmaAssistant::TaskEngine::TaskOcrDetection;
using UmaAssistant::TaskEngine::TaskRoi;
using UmaAssistant::Vision::GrayImage;

class RemoveTestFiles
{
public:
    explicit RemoveTestFiles(std::filesystem::path path) : m_path(std::move(path)) {}
    ~RemoveTestFiles()
    {
        std::error_code error;
        std::filesystem::remove_all(m_path, error);
    }

    RemoveTestFiles(const RemoveTestFiles&) = delete;
    RemoveTestFiles& operator=(const RemoveTestFiles&) = delete;

private:
    std::filesystem::path m_path;
};

Json ReadJson(const std::filesystem::path& path)
{
    std::ifstream input(path);
    if (!input)
        throw std::runtime_error("Could not read packaged career profile: " + path.string());
    return Json::parse(input);
}

Json FindProfileScreen(const Json& profile, const std::string& id)
{
    for (const auto& screen : profile.at("screens"))
    {
        if (screen.value("screenId", std::string{}) == id)
            return screen;
    }
    throw std::runtime_error("Packaged screen is missing: " + id);
}

void MakeTemplatePathsAbsolute(Json& screen, const std::filesystem::path& profilePath)
{
    auto& recognition = screen["recognition"];
    const auto absolutize = [&](Json& value) {
        if (!value.is_string())
            return;
        const auto path = profilePath.parent_path() / UmaAssistant::Paths::FromUtf8(value.get<std::string>());
        value = UmaAssistant::Paths::ToUtf8(std::filesystem::absolute(path).lexically_normal());
    };
    if (recognition.contains("template"))
        absolutize(recognition["template"]);
    if (recognition.contains("templates") && recognition["templates"].is_array())
    {
        for (auto& path : recognition["templates"])
            absolutize(path);
    }
    if (recognition.contains("requiredTemplate"))
        absolutize(recognition["requiredTemplate"]);
    if (recognition.contains("requiredTemplates") && recognition["requiredTemplates"].is_array())
    {
        for (auto& path : recognition["requiredTemplates"])
            absolutize(path);
    }
}

std::filesystem::path WriteSmallReplayManifest(const std::filesystem::path& resourceRoot)
{
    const auto careerRoot = resourceRoot / "hachimi" / "career";
    const auto sourceEntry = careerRoot / "entry" / "profile.json";
    const auto sourceTurn = careerRoot / "turn" / "profile.json";
    const auto id = std::to_string(std::chrono::steady_clock::now().time_since_epoch().count());
    const auto testDirectory = careerRoot / "testdata" / ("runtime_replay_" + id);
    std::filesystem::create_directories(testDirectory);

    auto home = FindProfileScreen(ReadJson(sourceEntry), "home");
    auto careerMain = FindProfileScreen(ReadJson(sourceTurn), "career_main");
    MakeTemplatePathsAbsolute(home, sourceEntry);
    MakeTemplatePathsAbsolute(careerMain, sourceTurn);

    const auto profilePath = testDirectory / "profile.json";
    Json profile{
        {"scenarioId", "ura"},
        {"screens", Json::array({home, careerMain})}
    };
    {
        std::ofstream output(profilePath);
        output << profile.dump(2);
    }

    const auto manifestPath = testDirectory / "manifest.json";
    Json manifest{
        {"schemaVersion", 2},
        {"scenarioId", "ura"},
        {"dataVersion", "runtime-replay-test"},
        {"screens", Json::array({"profile.json"})},
        {"execution", Json::array({"../../entry/execution.json", "../../turn/execution.json"})},
        {"resourceCatalog", "../../catalog.json"}
    };
    {
        std::ofstream output(manifestPath);
        output << manifest.dump(2);
    }

    // Keep the cleanup object alive through the test; static destruction removes
    // the temporary manifest/profile after all observations have completed.
    static std::vector<std::unique_ptr<RemoveTestFiles>> cleanups;
    cleanups.push_back(std::make_unique<RemoveTestFiles>(testDirectory));
    return manifestPath;
}

class ScreenshotReplayContext final : public ITaskExecutionContext
{
public:
    explicit ScreenshotReplayContext(std::vector<std::filesystem::path> screenshots)
        : m_screenshots(std::move(screenshots))
    {
    }

    bool CaptureScreen() override
    {
        if (m_screenshots.empty())
            return false;
        const auto index = (std::min)(m_captureCount, m_screenshots.size() - 1);
        try
        {
        m_image = frameOverride ? *frameOverride : UmaAssistant::Resources::WicImageDecoder::decode_file(m_screenshots[index]);
        }
        catch (...)
        {
            return false;
        }
        ++m_captureCount;
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = m_image.width;
        frame->height = m_image.height;
        frame->frameId = static_cast<std::uint64_t>(m_captureCount);
        frame->grayPixels = m_image.gray;
        frame->rgbaPixels = m_image.rgba;
        frame->geometryGeneration = geometryGeneration;
        m_frame = std::move(frame);
        return true;
    }

    TaskMatchResult MatchTemplate(
        const std::string& path,
        double threshold,
        const std::optional<TaskRoi>& roi = std::nullopt,
        const std::string& algorithm = "MatchTemplate") override
    {
        return MatchTemplateAdvanced(path, threshold, roi, algorithm, 900, 1600, {});
    }

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& /*scaleCandidates*/) override
    {
        ++matchCalls;
        matchedPaths.push_back(templatePath);
        if (std::filesystem::path(templatePath).filename() == "career_main_action_training.png" && roi)
        {
            mainAnchorSearches.push_back(*roi);
            if (rejectNarrowMainAnchor && roi->width < 500) return {};
        }
        if (std::filesystem::path(templatePath).filename().string().starts_with("infirmary_"))
        {
            infirmaryRois.push_back(roi);
            infirmaryAlgorithms.push_back(algorithm);
        }
        TaskMatchResult result;
        if (!m_frame || referenceWidth <= 0 || referenceHeight <= 0)
            return result;

        const auto templ = LoadTemplateImage(templatePath);
        if (!templ)
            return result;

        UmaAssistant::Vision::Roi matchRoi = std::nullopt;
        if (roi)
            matchRoi = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};

        UmaAssistant::Vision::MatchResult match;
        if (algorithm == "MatchTemplateColor" || algorithm == "MatchTemplateColorText")
        {
            match = UmaAssistant::Vision::find_color(
                m_image, *templ, matchRoi, threshold, referenceWidth, referenceHeight,
                algorithm == "MatchTemplateColorText");
        }
        else if (algorithm == "Button")
        {
            match = UmaAssistant::Vision::find_button(
                m_image, *templ, matchRoi, threshold, referenceWidth, referenceHeight);
        }
        else
        {
            match = UmaAssistant::Vision::find(
                m_image, *templ, matchRoi, threshold, referenceWidth, referenceHeight);
        }
        result.found = match.found;
        result.score = match.score;
        result.x = match.x;
        result.y = match.y;
        result.width = match.width;
        result.height = match.height;
        result.referenceWidth = referenceWidth;
        result.referenceHeight = referenceHeight;
        result.frameId = m_frame->frameId;
        if (forceGoalHeader && std::filesystem::path(templatePath).filename() == "goal_complete_header.png")
        {
            result.found = true;
            result.score = 0.99;
        }
        return result;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return m_frame; }
    std::shared_ptr<const GrayImage> LoadTemplateImage(const std::string& path) override
    {
        const auto found = m_templates.find(path);
        if (found != m_templates.end())
            return found->second;
        try
        {
            auto decoded = std::make_shared<const GrayImage>(
                UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(path));
            m_templates.emplace(path, decoded);
            return decoded;
        }
        catch (...)
        {
            return {};
        }
    }
    bool CheckImageChange(const TaskMatchResult&) override { return false; }
    std::vector<TaskOcrDetection> RecognizeText(
        const std::string& language, const std::optional<TaskRoi>& roi = std::nullopt) override
    {
        ++ocrCalls;
        if (!nativeOcr) return ocrDetections;
        UmaAssistant::Recognition::WindowsOcrRecognizer reader;
        auto const cropped = roi ? UmaAssistant::Resources::crop_image(m_image,
            roi->x, roi->y, roi->width, roi->height) : m_image;
        auto const recognized = reader.recognize(cropped, language);
        std::vector<TaskOcrDetection> result;
        for (auto const& detection : recognized.detections)
            result.push_back({detection.text, detection.bounds.x + (roi ? roi->x : 0), detection.bounds.y + (roi ? roi->y : 0),
                detection.bounds.width, detection.bounds.height, detection.confidence});
        return result;
    }
    std::vector<TaskOcrDetection> RecognizeImageText(const GrayImage& image, const std::string& language) override
    {
        if (!nativeOcr) return ocrDetections;
        UmaAssistant::Recognition::WindowsOcrRecognizer reader;
        std::vector<TaskOcrDetection> result;
        for (auto const& detection : reader.recognize(image, language).detections)
        {
            processedOcrText += detection.text + "\n";
            result.push_back({detection.text, detection.bounds.x, detection.bounds.y,
                detection.bounds.width, detection.bounds.height, detection.confidence});
        }
        return result;
    }
    UmaAssistant::Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<UmaAssistant::Recognition::NumericOcrBatchField>& fields) override
    {
        if (!nativeOcr) return {};
        UmaAssistant::Recognition::NumericOcrBatch reader;
        auto const result = reader.read(fields);
        for (auto const& [key, reading] : result) numericOcrText += key + ": " + reading.rawText + "\n";
        return result;
    }
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override { return false; }
    bool Tap(int, int) override { ++tapAttempts; return false; }
    bool Swipe(int, int, int, int, int) override { return false; }
    bool InputText(const std::string&) override { return false; }
    bool SendKey(const std::string&) override { return false; }
    void Sleep(int milliseconds) override { requestedWaitMs += milliseconds; }
    bool IsCancelled() const override { return false; }
    void Log(const std::string&, const std::string& message) override { diagnostics += message + "\n"; }
    void EmitEvent(const UmaAssistant::TaskEngine::TaskExecutionEvent&) override {}

    [[nodiscard]] std::size_t CaptureCount() const noexcept { return m_captureCount; }

    std::vector<TaskOcrDetection> ocrDetections;
    bool nativeOcr{false};
    bool forceGoalHeader{false};
    std::size_t matchCalls{0};
    std::size_t ocrCalls{0};
    int requestedWaitMs{0};
    int tapAttempts{0};
    std::vector<std::string> matchedPaths;
    std::vector<TaskRoi> mainAnchorSearches;
    bool rejectNarrowMainAnchor{false};
    std::uint64_t geometryGeneration{0};
    std::optional<GrayImage> frameOverride;
    std::vector<std::optional<TaskRoi>> infirmaryRois;
    std::vector<std::string> infirmaryAlgorithms;
    std::string processedOcrText;
    std::string numericOcrText;
    std::string diagnostics;

private:
    std::vector<std::filesystem::path> m_screenshots;
    std::size_t m_captureCount{0};
    GrayImage m_image;
    std::shared_ptr<const TaskFrameSnapshot> m_frame;
    std::unordered_map<std::string, std::shared_ptr<const GrayImage>> m_templates;
};

} // namespace

TEST_CASE("URA scenario return reaches the actual Finale Main without searching or tapping Skip", "[CareerRuntime][ScenarioAutoAdvance]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    auto const main = root / "testdata/hachimi/ura/captures/career_main_oguri_finale_qualifier_20261014.png";
    auto const event = root / "resource/hachimi/career/race/templates/runtime_frames/ura_finale_after_goal_next.png";
    for (bool alreadyAtMain : {false, true})
    {
        CAPTURE(alreadyAtMain);
        ScreenshotReplayContext context(alreadyAtMain ? std::vector<std::filesystem::path>{main}
            : std::vector<std::filesystem::path>{event, main});
        UmaAssistant::Career::CareerActionExecutor executor(config, resources);
        UmaAssistant::CareerDomain::CareerObservation source;
        source.screenId = "scenario_event";
        source.screenKind = UmaAssistant::CareerDomain::CareerScreenKind::EventChoice;
        auto const intent = UmaAssistant::CareerDomain::ActionIntent::MakeAdvance("Observe the automatic scenario return.");
        REQUIRE(executor.ExecuteForObservation(intent, source, context) == UmaAssistant::Career::ActionExecutionStatus::Success);
        REQUIRE(executor.ConfirmCompletionFromObservation(intent, source, context));
        auto const handoff = executor.TakeConfirmedObservation();
        REQUIRE(handoff);
        CHECK(handoff->screenId == "career_main");
        CHECK(handoff->stableFrameCount == 2);
        CHECK(context.tapAttempts == 0);
        CHECK(context.requestedWaitMs < 1000);
    }
}

TEST_CASE("EventHandle can finish a support event before training returns to Career Main",
    "[CareerRuntime][CareerEventHandle]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    auto const event = root / "testdata/hachimi/ura/captures/support_event_result_ura_3.png";
    auto const main = root / "resource/hachimi/ura/testdata/captures/runtime_frames/training_main_turn2_ura.png";
    ScreenshotReplayContext context({event, event, event, main, main});
    UmaAssistant::Career::CareerActionExecutor executor(config, resources);
    UmaAssistant::CareerDomain::CareerObservation source;
    source.screenId = "training_selection";
    source.screenKind = UmaAssistant::CareerDomain::CareerScreenKind::TrainingSelect;

    REQUIRE(executor.ConfirmCompletionFromObservation(
        UmaAssistant::CareerDomain::ActionIntent::MakeTraining("power"), source, context));
    auto const handoff = executor.TakeConfirmedObservation();
    REQUIRE(handoff);
    CHECK(handoff->screenId == "career_main");
    CHECK(handoff->stableFrameCount == 2);
    CHECK(context.CaptureCount() >= 5);
    CHECK(context.tapAttempts == 0);
}

TEST_CASE("Career fixed screenshots report recognition work and elapsed time", "[CareerPerformance]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    for (auto const& fixture : std::vector<std::pair<std::string, std::string>>{
        {"career_main_oguri_predebut_mumu_20261013.png", "career_main"},
        {"rest_confirmation_ura.png", "rest_confirmation"},
        {"rewards_support_mumu_20261013.png", "rewards"}})
    {
        ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures" / fixture.first});
        CareerScreenObserver observer(resources);
        std::vector<double> elapsed;
        for (int iteration = 0; iteration < 22; ++iteration)
        {
            auto const started = std::chrono::steady_clock::now();
            auto const transition = CareerTransitionPolicy::FromObservation(fixture.second);
            auto const observation = observer.ObserveTransition(context, transition);
            REQUIRE(observation.screenId == fixture.second);
            if (iteration >= 2)
                elapsed.push_back(std::chrono::duration<double, std::milli>(
                    std::chrono::steady_clock::now() - started).count());
        }
        std::sort(elapsed.begin(), elapsed.end());
        std::cout << "CAREER_BENCH " << fixture.second << " captures=" << context.CaptureCount()
            << " matches=" << context.matchCalls << " ocr=" << context.ocrCalls
            << " waitMs=" << context.requestedWaitMs << " medianMs=" << elapsed[10]
            << " p95Ms=" << elapsed[18] << '\n';
    }
}

TEST_CASE("Stable entry uses one two-frame observation and training reuses a valid graph frame", "[CareerTransition][Replay]")
{
    const auto root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    SECTION("stable entry does not repeat its two-frame proof")
    {
        ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/career_final_confirmation_mumu_20261012.png"});
        CareerScreenObserver observer(resources);
        const auto observation = observer.ObserveStableScreens(context, {"career_final_confirmation"});
        CHECK(observation.screenId == "career_final_confirmation");
        CHECK(observation.stableFrameCount == 2);
        CHECK(context.CaptureCount() == 2);
        CHECK(context.requestedWaitMs == 120);
    }
    SECTION("training destination consumes the graph frame once without training OCR")
    {
        ScreenshotReplayContext context({root / "resource/hachimi/ura/testdata/captures/runtime_frames/training_selection_ura.png"});
        REQUIRE(context.CaptureScreen());
        auto seed = context.GetCurrentFrame();
        CareerScreenObserver observer(resources);
        auto transition = CareerTransitionPolicy::AfterAction({}, "career_main", "action.training", "training.selection.confirmed");
        auto observation = observer.ObserveTransition(context, transition, seed);
        CHECK(observation.screenId == "training_selection");
        CHECK(observation.stableFrameCount == 2);
        CHECK(context.CaptureCount() == 2);
        CHECK(context.ocrCalls == 0);
        for (auto const& path : context.matchedPaths)
        {
            CHECK(path.find("settlement") == std::string::npos);
            CHECK(path.find("skill/templates") == std::string::npos);
            CHECK(path.find("race/templates") == std::string::npos);
        }
        const auto captures = context.CaptureCount();
        observation = observer.ObserveTransition(context, transition, seed); // seed no longer current
        CHECK(observation.screenId == "training_selection");
        CHECK(context.CaptureCount() == captures + 2);
    }
}

TEST_CASE("Captured turn dialogs provide independent frame proof and an authored OK button",
    "[CareerTransition][Replay][TurnConfirmationSpeed]")
{
    const auto root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    struct Fixture { std::string capture; std::string screen; std::string task; };
    for (const auto& fixture : std::vector<Fixture>{
        {"testdata/hachimi/ura/captures/rest_confirmation_ura.png", "rest_confirmation", "rest_confirmation_rest_confirm"},
        {"resource/hachimi/ura/screens/captures/summer_rest_dialog.png", "summer_rest_confirmation", "rest_confirmation_rest_confirm"},
        {"resource/hachimi/ura/screens/captures/infirmary_confirm.png", "infirmary_confirmation", "infirmary_confirmation_ok"},
        {"resource/hachimi/ura/screens/captures/recreation_confirmation.png", "recreation_confirmation", "recreation_confirmation_ok"}})
    {
        DYNAMIC_SECTION(fixture.screen)
        {
            ScreenshotReplayContext context({root / fixture.capture});
            CareerScreenObserver observer(resources);
            const auto observation = observer.ObserveScreens(context, {fixture.screen});
            REQUIRE(observation.screenId == fixture.screen);
            CHECK(observation.stableFrameCount == 2);
            REQUIRE(observation.visualFrameEvidence);
            CHECK(context.CaptureCount() == 2);
            const auto button = resources->MatchExecutionTask(fixture.task, context,
                observation.visualFrameEvidence->frameId);
            CHECK(button.found);
            CHECK(button.frameId == observation.visualFrameEvidence->frameId);
            CHECK(context.tapAttempts == 0);
        }
    }
}

TEST_CASE("Main anchor hints rematch each frame and retain authored fallback and geometry boundaries", "[CareerTransition][Replay]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/career_main_oguri_predebut_mumu_20261013.png"});
    CareerScreenObserver observer(resources);
    const auto transition = CareerTransitionPolicy::FromObservation("career_main");
    REQUIRE(observer.ObserveTransition(context, transition).screenId == "career_main");
    REQUIRE(context.mainAnchorSearches.size() == 2);
    CHECK(context.mainAnchorSearches.front().width == 500);
    CHECK(context.mainAnchorSearches.back().width < 500);
    CHECK(context.CaptureCount() == 2);
    context.mainAnchorSearches.clear();
    context.rejectNarrowMainAnchor = true;
    REQUIRE(observer.ObserveTransition(context, transition).screenId == "career_main");
    REQUIRE(context.mainAnchorSearches.size() == 4);
    CHECK(context.mainAnchorSearches[0].width < 500);
    CHECK(context.mainAnchorSearches[1].width == 500);
    CHECK(context.mainAnchorSearches[2].width < 500);
    CHECK(context.mainAnchorSearches[3].width == 500);
    context.mainAnchorSearches.clear();
    context.rejectNarrowMainAnchor = false;
    ++context.geometryGeneration;
    REQUIRE(observer.ObserveTransition(context, transition).screenId == "career_main");
    REQUIRE(context.mainAnchorSearches.size() == 2);
    CHECK(context.mainAnchorSearches.front().width == 500);
    observer.ResetForEntry();
    context.mainAnchorSearches.clear();
    REQUIRE(observer.ObserveTransition(context, transition).screenId == "career_main");
    REQUIRE(context.mainAnchorSearches.size() == 2);
    CHECK(context.mainAnchorSearches.front().width == 500);
}

TEST_CASE("Infirmary perception uses main colored button regions and score gap", "[CareerTransition][Replay]")
{
    const auto root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    for (auto const& [file, expected] : std::vector<std::pair<std::string, bool>>{
        {"infirmary_current.png", true}, {"infirmary_unavailable_reference.png", false}, {"infirmary_unavailable_shifted.png", false}})
    {
        CAPTURE(file);
        ScreenshotReplayContext context({root / "resource/hachimi/ura/screens/captures" / file});
        CareerScreenObserver observer(resources);
        const auto observed = observer.ObserveTransition(context, CareerTransitionPolicy::FromObservation("career_main"));
        REQUIRE(observed.screenId == "career_main");
        REQUIRE(observed.infirmaryEvidence.IsReliable());
        CHECK(observed.infirmaryAvailable == expected);
        REQUIRE_FALSE(context.infirmaryRois.empty());
        for (const auto& roi : context.infirmaryRois)
        {
            REQUIRE(roi);
            CHECK(roi->width == 170);
            CHECK(roi->height == 62);
            CHECK((roi->x == 130 || roi->x == 250));
        }
        for (const auto& algorithm : context.infirmaryAlgorithms) CHECK(algorithm == "MatchTemplateColor");
    }
    SECTION("The lit button is also recognized in the summer shifted position")
    {
        ScreenshotReplayContext context({root / "resource/hachimi/ura/screens/captures/infirmary_unavailable_shifted.png"});
        auto image = UmaAssistant::Resources::WicImageDecoder::decode_file(
            root / "resource/hachimi/ura/screens/captures/infirmary_unavailable_shifted.png");
        const auto* active = resources->FindAsset("career.infirmary.available");
        REQUIRE(active);
        const auto marker = context.LoadTemplateImage(active->path);
        REQUIRE(marker);
        REQUIRE(image.width >= 260 + marker->width);
        REQUIRE(image.height >= 1420 + marker->height);
        REQUIRE(marker->rgba.size() == marker->gray.size() * 4);
        // Match the existing main regression: put the active colored glyphs
        // at the authored shifted button without changing other page content.
        for (int y = 0; y < marker->height; ++y)
            for (int x = 0; x < marker->width; ++x)
            {
                const auto source = static_cast<std::size_t>(y * marker->width + x);
                const auto destination = static_cast<std::size_t>((1420 + y) * image.width + 260 + x);
                image.gray[destination] = marker->gray[source];
                std::copy_n(marker->rgba.begin() + static_cast<std::ptrdiff_t>(source * 4), 4,
                    image.rgba.begin() + static_cast<std::ptrdiff_t>(destination * 4));
            }
        context.frameOverride = std::move(image);
        CareerScreenObserver observer(resources);
        const auto observed = observer.ObserveTransition(context, CareerTransitionPolicy::FromObservation("career_main"));
        REQUIRE(observed.screenId == "career_main");
        CHECK(observed.infirmaryAvailable);
    }
}

TEST_CASE("Career observer replays a packaged Home screenshot on one captured frame", "[CareerRuntime][Replay]")
{
    const auto sourceRoot = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    const auto resourceRoot = sourceRoot / "resource";
    const auto replayRoot = resourceRoot / "hachimi" / "ura" / "testdata" / "captures" / "runtime_frames";
    const auto manifestPath = WriteSmallReplayManifest(resourceRoot);

    CareerRuntimeConfig config;
    config.resourceDirectory = resourceRoot;
    config.manifestPath = UmaAssistant::Paths::ToUtf8(manifestPath);
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    CHECK(resources->GetScenarioId() == "ura");
    CHECK(resources->GetResourceVersion() == "runtime-replay-test");
    CHECK(resources->FindScreen("home") != nullptr);
    CHECK(resources->FindScreen("career_main") != nullptr);

    ScreenshotReplayContext context({
        replayRoot / "ura_returned_home.png",
        replayRoot / "ura_returned_home.png"});
    CareerScreenObserver observer(resources);
    const auto observation = observer.Observe(context);

    CHECK(context.CaptureCount() == std::size_t{2});
    CHECK(observation.screenId == "home");
    CHECK(observation.score >= 0.86);
    CHECK(observation.screenKind == UmaAssistant::CareerDomain::CareerScreenKind::Unknown);
    const auto frame = context.GetCurrentFrame();
    REQUIRE(frame != nullptr);
    CHECK(frame->frameId == 2);
    CHECK(frame->rgbaPixels.size() == static_cast<std::size_t>(frame->width) * frame->height * 4);
}

TEST_CASE("Career entry recognizes Scenario Select despite its selected Home tab", "[CareerRuntime][Replay][Entry]")
{
    const auto resourceRoot = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    CareerRuntimeConfig config;
    config.resourceDirectory = resourceRoot;
    config.manifestPath = UmaAssistant::Paths::ToUtf8(resourceRoot / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());

    ScreenshotReplayContext context({
        resourceRoot / "hachimi/career/race/templates/runtime_frames/scenario_select_ura.png"});
    context.ocrDetections = {
        {"Scenario Select", 25, 205, 235, 35, 0.99},
        {"the URA Finale!", 590, 1120, 240, 35, 0.99}};
    REQUIRE(context.CaptureScreen());
    const auto* home = resources->FindScreen("home");
    REQUIRE(home != nullptr);
    REQUIRE_FALSE(home->templates.empty());
    const auto& tab = home->templates.front();
    // This shared navigation marker is present on both Home and Scenario Select.
    REQUIRE(context.MatchTemplate(tab.path, tab.threshold, tab.roi).found);

    CareerScreenObserver observer(resources);
    UmaAssistant::CareerDomain::CareerObservation unselectedHome;
    unselectedHome.screenId = "home_unselected";
    observer.RememberObservation(unselectedHome);
    const auto observation = observer.Observe(context);
    CHECK(observation.screenId == "scenario_select");
}

TEST_CASE("Every scenario remains Scenario Select and shares the transparent right arrow",
    "[CareerRuntime][Replay][Entry][ScenarioSelection]")
{
    const auto root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    const auto* arrow = resources->FindExecutionTask("scenario_select_scenario_next_card");
    REQUIRE(arrow != nullptr);
    REQUIRE(arrow->templatePath);
    const auto marker = UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(*arrow->templatePath);
    REQUIRE_FALSE(marker.rgba.empty());
    CHECK(marker.rgba[3] == 0);

    for (auto const* scenario : {"ura", "aoharu", "climax", "grand_concert"})
    {
        CAPTURE(scenario);
        ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/scenario_select_20261022"
            / (std::string(scenario) + ".png")});
        REQUIRE(context.CaptureScreen());
        const auto* selection = resources->FindScreen("scenario_select");
        REQUIRE(selection);
        const auto& header = selection->templates.at(1);
        const auto headerMatch = context.MatchTemplateAdvanced(header.path, header.threshold, header.roi,
            header.algorithm, 900, 1600, {});
        CAPTURE(headerMatch.score);
        CHECK(headerMatch.found);
        CareerScreenObserver observer(resources);
        const auto observation = observer.ObserveStableScreens(context, {"career_continue", "scenario_select"});
        REQUIRE(observation.screenId == "scenario_select");
        const auto frame = context.GetCurrentFrame();
        REQUIRE(frame);
        const auto ura = resources->MatchAsset("career.entry.scenario.ura_card", context, frame->frameId);
        CHECK(ura.found == (std::string(scenario) == "ura"));
        const auto right = resources->MatchExecutionTask("scenario_select_scenario_next_card", context, frame->frameId);
        CAPTURE(right.score);
        REQUIRE(right.found);
        CHECK(right.CenterX() >= 840);
        CHECK(right.CenterX() <= 895);
        CHECK(right.CenterY() >= 690);
        CHECK(right.CenterY() <= 780);
    }
}

TEST_CASE("Career support formation distinguishes disabled and enabled Start buttons",
    "[CareerRuntime][Replay][Entry]")
{
    const auto sourceRoot = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = sourceRoot / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    for (auto const& fixture : std::vector<std::pair<std::string, std::string>>{
        {"support_select_ura.png", "support_select"},
        {"support_autofill_prompt_ura.png", "support_autofill_confirmation"},
        {"support_autofilled_ura.png", "support_ready"}})
    {
        CAPTURE(fixture.first);
        ScreenshotReplayContext context({sourceRoot / "testdata/hachimi/ura/captures" / fixture.first});
        context.ocrDetections = {
            {"Support Formation", 20, 40, 400, 60, 0.99},
            {"Auto-Fill", 400, 1100, 250, 60, 0.99},
            {"Start Career!", 350, 1300, 250, 60, 0.99}};
        CareerScreenObserver observer(resources);
        CHECK(observer.Observe(context).screenId == fixture.second);
    }
}

TEST_CASE("Career entry recognizes live MuMu Support Formation with an empty borrowed slot",
    "[CareerRuntime][Replay][Entry][LiveSupport]")
{
    auto const sourceRoot = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = sourceRoot / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ScreenshotReplayContext context({sourceRoot / "testdata/hachimi/ura/captures/support_select_mumu_guest_empty_20261012.png"});
    context.nativeOcr = true;
    REQUIRE(context.CaptureScreen());
    std::string recognizedText;
    for (auto const& detection : context.RecognizeText("en-US")) recognizedText += detection.text + "\n";
    CAPTURE(recognizedText);
    for (auto const* id : {"support_select", "support_ready", "support_autofill_confirmation"})
    {
        auto const* screen = resources->FindScreen(id);
        REQUIRE(screen != nullptr);
        auto const& marker = screen->templates.front();
        auto const match = context.MatchTemplate(marker.path, marker.threshold, marker.roi, marker.algorithm);
        UNSCOPED_INFO(std::string(id) + " score=" + std::to_string(match.score));
    }
    CareerScreenObserver observer(resources);
    CHECK(observer.ObserveScreens(context, {"support_select", "support_ready", "support_autofill_confirmation"}).screenId == "support_select");
    CHECK(observer.Observe(context).screenId == "support_select");
    auto const* autoFill = resources->FindAction("support_select", "support.auto_fill");
    REQUIRE(autoFill != nullptr);
    CHECK(UmaAssistant::TaskEngine::RecognitionPipeline::EvaluateSameFrameMonitors(
        resources->GetExecutionDefinition(), {autoFill->taskName}, context).has_value());
}

TEST_CASE("Native Career perception identifies real entry turn event race and settlement frames",
    "[CareerRuntime][Replay][NativePerceptionParity]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    for (auto const& fixture : std::vector<std::pair<std::string, std::string>>{
        {"testdata/hachimi/ura/captures/support_ready_mumu_20261012.png", "support_ready"},
        {"testdata/hachimi/ura/captures/support_after_selection_mumu_20261012.png", "support_ready"},
        {"testdata/hachimi/ura/captures/career_final_confirmation_mumu_20261012.png", "career_final_confirmation"},
        {"testdata/hachimi/ura/captures/career_start_ura.png", "support_select"},
        {"testdata/hachimi/ura/captures/legacy_select_ura.png", "legacy_select"},
        {"resource/hachimi/ura/testdata/captures/runtime_frames/training_selection_ura.png", "training_selection"},
        {"testdata/hachimi/ura/captures/rest_confirmation_ura.png", "rest_confirmation"},
        {"testdata/hachimi/ura/captures/acupuncturist_no_worries_choice.png", "event_choice"},
        {"resource/hachimi/ura/testdata/captures/runtime_frames/support_event_choice_ura.png", "event_choice"},
        {"testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png", "race_day"},
        {"testdata/hachimi/ura/captures/career_complete_close.png", "career_complete_close"},
        {"testdata/hachimi/ura/captures/career_epithet_after_rating_record_updated.png", "career_epithet"},
        {"testdata/hachimi/ura/captures/rating_record_updated.png", "career_rating_record_updated"},
        {"resource/hachimi/ura/screens/captures/career_skill_learn_sample.png", "career_skills"},
        {"resource/hachimi/ura/screens/captures/career_skill_confirmation_sample.png", "career_skills"},
        {"resource/hachimi/ura/screens/captures/career_skill_learned_sample.png", "career_skills"},
        {"resource/hachimi/ura/screens/captures/career_skill_exit_confirmation_sample.png", "career_skills"},
        {"resource/hachimi/ura/screens/captures/career_skill_obtained_sample.png", "career_skills"}})
    {
        CAPTURE(fixture.first);
        ScreenshotReplayContext context({root / fixture.first});
        context.nativeOcr = true;
        CareerScreenObserver observer(resources);
        auto const* expected = resources->FindScreen(fixture.second);
        REQUIRE(expected != nullptr);
        REQUIRE(context.CaptureScreen());
        auto const& primary = expected->templates.front();
        auto const raw = context.MatchTemplate(primary.path, primary.threshold, primary.roi,
            primary.requiresAlphaMatching ? "MatchTemplateColor" : primary.algorithm);
        CAPTURE(raw.score);
        std::string text;
        for (auto const& detection : context.RecognizeText("en-US")) text += detection.text + "\n";
        CAPTURE(text);
        auto const observation = expected->flow == "entry"
            ? observer.ObserveScreens(context, {}, true) : observer.ObserveCareer(context);
        CHECK(observation.screenId == fixture.second);
    }
}

TEST_CASE("MuMu Oguri pre-debut main remains a turn screen during startup resume and goal probing",
    "[CareerRuntime][Replay][CareerStartupMainRegression]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/career_main_oguri_predebut_mumu_20261013.png"});
    context.nativeOcr = true;
    CareerScreenObserver observer(resources);
    bool allowResume = false;
    SECTION("Fresh startup") {}
    SECTION("Resumed Career") { allowResume = true; }
    SECTION("A goal header correlation still requires its actual Next button")
    {
        allowResume = true;
        context.forceGoalHeader = true;
    }
    SECTION("An action armed goal completion")
    {
        UmaAssistant::CareerDomain::CareerObservation source;
        source.screenId = "career_main";
        observer.RememberObservation(source);
        observer.SetGoalCompletionProbeArmed(true);
    }
    REQUIRE(context.CaptureScreen());
    for (auto const* id : {"career_main", "goal_objective_complete"})
    {
        auto const* screen = resources->FindScreen(id);
        REQUIRE(screen != nullptr);
        auto const& marker = screen->templates.front();
        auto const match = context.MatchTemplate(marker.path, marker.threshold, marker.roi, marker.algorithm);
        INFO(id << " primary score=" << match.score);
        if (std::string(id) == "career_main")
        {
            for (auto const& required : screen->requiredTemplates)
            {
                auto const proof = context.MatchTemplate(required.path, required.threshold, required.roi, required.algorithm);
                INFO("Training score=" << proof.score);
                CHECK(proof.found);
            }
        }
    }
    auto const observation = observer.ObserveScreens(context, {}, allowResume);
    CHECK(observation.screenId == "career_main");
    auto const* mainScreen = resources->FindScreen("career_main");
    REQUIRE(mainScreen != nullptr);
    auto const countdownRegion = std::find_if(mainScreen->ocrRegions.begin(), mainScreen->ocrRegions.end(),
        [](auto const& region) { return region.semanticId == "objective.turns_left"; });
    REQUIRE(countdownRegion != mainScreen->ocrRegions.end());
    REQUIRE(countdownRegion->numericRoi.has_value());
    std::string countdownText;
    for (auto const& text : context.RecognizeText("en-US", TaskRoi{10, 90, 175, 140}))
        countdownText += text.text + "\n";
    INFO("Countdown OCR: " << countdownText);
    INFO("Processed OCR: " << context.processedOcrText);
    INFO("Numeric OCR: " << context.numericOcrText);
    INFO("Diagnostics: " << context.diagnostics);
    CHECK(observation.turnsToGoal == 11);
}

TEST_CASE("Finale training main remains a decision page after a goal update",
    "[CareerRuntime][Replay][CareerRaceEntryRegression]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/career_main_oguri_finale_training_available.png"});
    context.nativeOcr = true;
    CareerScreenObserver observer(resources);
    const auto transition = CareerTransitionPolicy::AfterAction({}, "goal_update", "goal.update_next");

    const auto observation = observer.ObserveTransition(context, transition);

    CHECK(observation.screenId == "career_main");
    CHECK(observation.screenKind == UmaAssistant::CareerDomain::CareerScreenKind::MainTurn);
    CHECK(observation.turnsToGoal == 1);
}

TEST_CASE("An actual single objective complete banner still advances through its authored Next",
    "[CareerRuntime][Replay][CareerStartupMainRegression]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures/ura_finale_entry_boundary.png"});
    context.nativeOcr = true;
    CareerScreenObserver observer(resources);
    auto const observation = observer.ObserveScreens(context, {"goal_objective_complete"}, true);
    CHECK(observation.screenId == "goal_objective_complete");
    auto const* action = resources->FindAction("goal_objective_complete", "goal.next");
    REQUIRE(action != nullptr);
    auto const* task = resources->FindExecutionTask(action->taskName);
    REQUIRE(task != nullptr);
    REQUIRE(task->templatePath.has_value());
    REQUIRE(task->roi.has_value());
    auto const& bounds = *task->roi;
    auto const next = context.MatchTemplate(*task->templatePath, task->templateThreshold,
        TaskRoi{bounds[0], bounds[1], bounds[2], bounds[3]}, task->algorithm);
    CHECK(next.found);
}

TEST_CASE("Actual Race Lists distinguish the mandatory Race Day boundary from an optional race",
    "[CareerRuntime][Replay][CareerTrainingRaceHandoff]")
{
    auto const root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    for (auto const& [file, mandatory] : std::vector<std::pair<std::string, bool>>{
        {"race_list_debut_after_training_mumu_20261013.png", true},
        {"year2_insufficient_fans_race_list.png", false}})
    {
        CAPTURE(file);
        ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures" / file});
        context.nativeOcr = true;
        CareerScreenObserver observer(resources);
        auto const observed = observer.ObserveScreens(context, {"race_list"}, true);
        REQUIRE(observed.screenId == "race_list");
        CHECK(observed.turnsToGoalEvidence.IsReliable() == mandatory);
        if (mandatory) CHECK(observed.turnsToGoalEvidence.Value() == 0);
    }
}

TEST_CASE("Settlement catalog recognizes every captured optional popup in its actual phase", "[CareerRuntime][Replay][CareerSettlementAudit]")
{
    const auto root = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR);
    CareerRuntimeConfig config;
    config.resourceDirectory = root / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    struct Stage { const char* file; const char* previous; const char* expected; bool nextVerified{true}; };
    for (const auto& stage : std::vector<Stage>{
        {"complete_career_entry.png", "race_runner_result", "complete_career_entry"},
        {"career_rank_bplus_8533_mumu_20261013.png", "complete_career", "career_rank"},
        {"ura_sparks_keep_confirmed.png", "sparks_confirmation", "career_result_close"},
        {"rating_record_updated.png", "career_result_close", "career_rating_record_updated"},
        {"career_epithet_after_rating_record_updated.png", "career_rating_record_updated", "career_epithet"},
        {"rewards_support_mumu_20261013.png", "rewards", "rewards"},
        // This older capture has a Next score of 0.709, below the authored
        // 0.78 gate. It must wait for a verified button rather than click it.
        {"ura_rewards_next.png", "rewards", "rewards", false},
        {"rewards_collected.png", "rewards", "rewards_collected"},
        {"rewards_collected_ticket_mumu_20261013.png", "rewards", "rewards_collected"},
        {"all_rewards_earned_mumu_20261017.png", "rewards", "all_rewards_earned"},
        {"rewards_ura_mumu_20261013.png", "rewards_collected", "rewards"},
        {"event_reward_mumu_20261013.png", "rewards", "event_reward"},
        {"rewards_event_mumu_20261013.png", "event_reward", "rewards"},
        {"follow_trainer_limit_mumu_20261013.png", "rewards", "follow_trainer_limit"},
        {"career_complete_mumu_20261013.png", "follow_trainer_limit", "career_complete"},
        {"settlement_home_mumu_20261013.png", "career_complete", "home"},
        {"career_complete_close.png", "rewards_collected", "career_complete_close"},
        {"event_home_mumu_20261016.png", "career_complete_close", "event_home"},
        {"event_home_cancelled_mumu_20261016.png", "event_home", "home"},
        {"career_story_unlocked.png", "career_complete_close", "career_story_unlocked"},
        {"career_story_unlocked_compact.png", "career_complete_close", "career_story_unlocked_compact"},
        {"career_story_unlocked_to_home.png", "career_complete_close", "career_story_unlocked_to_home"}})
    {
        CAPTURE(stage.file, stage.previous, stage.expected);
        ScreenshotReplayContext context({root / "testdata/hachimi/ura/captures" / stage.file});
        context.nativeOcr = true;
        CareerScreenObserver observer(resources);
        UmaAssistant::CareerDomain::CareerObservation previous;
        previous.screenId = stage.previous;
        observer.RememberObservation(previous);
        REQUIRE(context.CaptureScreen());
        const auto* markers = resources->FindScreen(stage.expected);
        REQUIRE(markers != nullptr);
        for (const auto& marker : markers->templates)
        {
            const auto score = context.MatchTemplate(marker.path, marker.threshold, marker.roi,
                marker.requiresAlphaMatching ? "MatchTemplateColor" : marker.algorithm);
            UNSCOPED_INFO("primary=" << score.score << " threshold=" << marker.threshold);
        }
        for (const auto& marker : markers->requiredTemplates)
        {
            const auto score = context.MatchTemplate(marker.path, marker.threshold, marker.roi,
                marker.requiresAlphaMatching ? "MatchTemplateColor" : marker.algorithm);
            UNSCOPED_INFO("required=" << score.score << " threshold=" << marker.threshold);
        }
        const auto observed = observer.ObserveCareer(context);
        CHECK(observed.screenId == (stage.nextVerified ? stage.expected : "unknown"));
        const auto* definition = resources->FindScreen(stage.expected);
        REQUIRE(definition != nullptr);
        if (observed.screenId == "home") continue; // Coordinator completes here instead of starting another Career.
        REQUIRE(definition->actions.size() == 1);
        CHECK(definition->actions.begin()->second.valid);
        CHECK(resources->FindExecutionTask(definition->actions.begin()->second.taskName) != nullptr);
    }
}
