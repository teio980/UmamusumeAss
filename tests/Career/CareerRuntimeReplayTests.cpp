#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
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
        const std::string&, const std::optional<TaskRoi>& = std::nullopt) override { return ocrDetections; }
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
    const auto observation = observer.Observe(context);
    CHECK(observation.screenId == "scenario_select");
}
