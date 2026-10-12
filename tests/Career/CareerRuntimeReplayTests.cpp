#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "UmaAssistantCore/Resources/ImageTransforms.hpp"
#include "UmaAssistantCore/Recognition/WindowsOcrRecognizer.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "UmaAssistantCore/Vision/TemplateMatcher.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
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
            m_image = UmaAssistant::Resources::WicImageDecoder::decode_file(m_screenshots[index]);
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
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override { return false; }
    bool Tap(int, int) override { return false; }
    bool Swipe(int, int, int, int, int) override { return false; }
    bool InputText(const std::string&) override { return false; }
    bool SendKey(const std::string&) override { return false; }
    void Sleep(int) override {}
    bool IsCancelled() const override { return false; }
    void Log(const std::string&, const std::string&) override {}
    void EmitEvent(const UmaAssistant::TaskEngine::TaskExecutionEvent&) override {}

    [[nodiscard]] std::size_t CaptureCount() const noexcept { return m_captureCount; }

    std::vector<TaskOcrDetection> ocrDetections;
    bool nativeOcr{false};

private:
    std::vector<std::filesystem::path> m_screenshots;
    std::size_t m_captureCount{0};
    GrayImage m_image;
    std::shared_ptr<const TaskFrameSnapshot> m_frame;
    std::unordered_map<std::string, std::shared_ptr<const GrayImage>> m_templates;
};

} // namespace

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
