#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/GameTasks/NativeNormalCareerStartup.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <string>
#include <utility>
#include <vector>

namespace
{

using namespace UmaAssistant::Career;
using namespace UmaAssistant::CareerDomain;
using namespace UmaAssistant::GameTasks;
using namespace UmaAssistant::TaskEngine;

std::string FoldPath(std::string value)
{
    value = std::filesystem::path(value).filename().string();
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

class StartupReplayContext final : public ITaskExecutionContext
{
public:
    bool CaptureScreen() override { return true; }
    TaskMatchResult MatchTemplate(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm) override
    { return MatchTemplateAdvanced(path, threshold, roi, algorithm, 900, 1600, {}); }
    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>&, const std::string&, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        templates.push_back(path);
        TaskMatchResult match;
        match.found = true;
        match.score = (std::max)(threshold, 0.99);
        match.x = 400;
        match.y = 1100;
        match.width = 80;
        match.height = 50;
        match.referenceWidth = referenceWidth;
        match.referenceHeight = referenceHeight;
        return match;
    }
    bool CheckImageChange(const TaskMatchResult&) override { return false; }
    std::vector<TaskOcrDetection> RecognizeText(const std::string&,
        const std::optional<TaskRoi>& = std::nullopt) override { return {}; }
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override
    { return false; }
    bool Tap(int x, int y) override { taps.emplace_back(x, y); return true; }
    bool TapMatch(int x, int y, const TaskMatchResult&) override { return Tap(x, y); }
    bool Swipe(int, int, int, int, int) override { return true; }
    bool InputText(const std::string&) override { return true; }
    bool SendKey(const std::string&) override { return true; }
    void Sleep(int) override {}
    bool IsCancelled() const override { return false; }
    void Log(const std::string&, const std::string&) override {}
    void EmitEvent(const TaskExecutionEvent&) override {}

    std::vector<std::string> templates;
    std::vector<std::pair<int, int>> taps;
};

class SequenceCareerObserver final : public ICareerObserver
{
public:
    explicit SequenceCareerObserver(std::vector<CareerObservation> observations)
        : m_observations(std::move(observations))
    {
    }
    CareerObservation Observe(ITaskExecutionContext&) override
    {
        if (m_next < m_observations.size()) return m_observations[m_next++];
        CareerObservation done;
        done.screenId = "career_main";
        done.screenKind = CareerScreenKind::MainTurn;
        return done;
    }
    CareerObservation ObserveScreens(ITaskExecutionContext& context,
        const std::vector<std::string>& screens, bool allowResume = false) override
    {
        candidateHistory.push_back(screens);
        if (settlementTrap && allowResume)
        {
            CareerObservation wrong;
            wrong.screenId = "career_epithet";
            wrong.screenKind = CareerScreenKind::Settlement;
            return wrong;
        }
        if (goalTrap && m_next >= 2
            && std::find(screens.begin(), screens.end(), "goal_objective_complete") != screens.end())
        {
            CareerObservation wrong;
            wrong.screenId = "goal_objective_complete";
            wrong.screenKind = CareerScreenKind::ConfirmationDialog;
            return wrong;
        }
        return ICareerObserver::ObserveScreens(context, screens, allowResume);
    }
    bool settlementTrap{false};
    bool goalTrap{false};
    std::vector<std::vector<std::string>> candidateHistory;
private:
    std::vector<CareerObservation> m_observations;
    std::size_t m_next{0};
};

CareerRuntimeConfig PackagedConfig()
{
    CareerRuntimeConfig config;
    config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(
        config.resourceDirectory / "hachimi" / "ura" / "manifest.json");
    config.normalLineupStrategy = "pace";
    return config;
}

CareerObservation Screen(std::string id, CareerScreenKind kind)
{
    CareerObservation observation;
    observation.screenId = std::move(id);
    observation.screenKind = kind;
    return observation;
}

} // namespace

TEST_CASE("A confirmed entry runtime frame reaches startup without another capture", "[GameTasks][Career][CareerTransition]")
{
    const auto config = PackagedConfig();
    CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    SequenceCareerObserver observer({});
    StartupReplayContext context;
    CareerObservation confirmed;
    confirmed.screenId = "career_main";
    confirmed.screenKind = CareerScreenKind::MainTurn;
    confirmed.stableFrameCount = 2;
    const auto result = NativeNormalCareerStartup::Run(config, resources, observer, context, confirmed.screenId, confirmed);
    REQUIRE(result.succeeded);
    REQUIRE(result.runtimeObservation);
    CHECK(result.runtimeObservation->stableFrameCount == 2);
    CHECK(observer.candidateHistory.empty());
    CHECK(context.taps.empty());
}

TEST_CASE("Confirmed final confirmation keeps normal startup out of settlement recovery",
    "[GameTasks][CareerStartupReplay][StartupPhaseRegression]")
{
    auto const config = PackagedConfig();
    CareerVisualResources resources(config);
    SequenceCareerObserver observer({
        Screen("career_final_confirmation", CareerScreenKind::Unknown),
        Screen("career_intro_event", CareerScreenKind::EventChoice),
        Screen("career_main", CareerScreenKind::MainTurn)});
    observer.settlementTrap = true;
    StartupReplayContext context;
    auto const result = NativeNormalCareerStartup::Run(
        config, resources, observer, context, "career_final_confirmation");
    REQUIRE(result.succeeded);
    REQUIRE(result.runtimeObservation.has_value());
    CHECK(result.runtimeObservation->screenId == "career_main");
    CHECK(result.actionsCompleted == 10);
    REQUIRE_FALSE(observer.candidateHistory.empty());
    CHECK(observer.candidateHistory.front() == std::vector<std::string>{"career_final_confirmation"});
    for (auto const& candidates : observer.candidateHistory)
        CHECK(std::find(candidates.begin(), candidates.end(), "career_epithet") == candidates.end());
}

TEST_CASE("Normal Career startup replays the configured mode, intro skip, and quick-mode actions",
    "[GameTasks][CareerStartupReplay]")
{
    auto const config = PackagedConfig();
    CareerVisualResources resources(config);
    CAPTURE(config.manifestPath);
    std::string loadDiagnostics;
    for (const auto& diagnostic : resources.GetLoadDiagnostics())
        loadDiagnostics += (loadDiagnostics.empty() ? "" : "\n") + diagnostic;
    CAPTURE(loadDiagnostics);
    REQUIRE(resources.IsReady());
    REQUIRE(resources.FindAction("career_final_confirmation", "normal.start") != nullptr);
    REQUIRE(resources.FindAction("normal_quick_mode_settings", "normal.quick_mode.confirm") != nullptr);

    SequenceCareerObserver observer({
        Screen("career_final_confirmation", CareerScreenKind::Unknown),
        Screen("career_intro_event", CareerScreenKind::EventChoice),
        Screen("career_main", CareerScreenKind::MainTurn)});
    StartupReplayContext context;
    const auto result = NativeNormalCareerStartup::Run(config, resources, observer, context);
    std::string templateTrace;
    for (const auto& path : context.templates)
        templateTrace += (templateTrace.empty() ? "" : "\n") + path;
    CAPTURE(templateTrace);

    REQUIRE(result.succeeded);
    CHECK_FALSE(result.cancelled);
    CHECK(result.lastScreenId == "career_main");
    REQUIRE(result.runtimeObservation.has_value());
    CHECK(result.runtimeObservation->screenId == "career_main");
    CHECK(result.actionsCompleted == 10);
    CHECK(std::any_of(context.templates.begin(), context.templates.end(), [](const auto& path) {
        return FoldPath(path) == "mode_normal_selected.png";
    }));
    CHECK(std::any_of(context.templates.begin(), context.templates.end(), [](const auto& path) {
        return FoldPath(path) == "quick_mode_shorten_selected.png";
    }));
    CHECK(std::any_of(context.templates.begin(), context.templates.end(), [](const auto& path) {
        return FoldPath(path) == "quick_mode_skip_two.png";
    }));
}

TEST_CASE("Normal Career startup waits for a stable resumed event and retains it for runtime",
    "[GameTasks][CareerStartupReplay]")
{
    auto const config = PackagedConfig();
    CareerVisualResources resources(config);
    SequenceCareerObserver observer({
        Screen("capture_failed", CareerScreenKind::Unknown),
        Screen("unknown", CareerScreenKind::Unknown),
        Screen("event_choice", CareerScreenKind::EventChoice)});
    StartupReplayContext context;
    auto const result = NativeNormalCareerStartup::Run(config, resources, observer, context);
    REQUIRE(result.succeeded);
    REQUIRE(result.runtimeObservation.has_value());
    CHECK(result.runtimeObservation->screenId == "event_choice");
    CHECK(context.taps.empty());
}

TEST_CASE("New Normal Career startup excludes goal overlays before confirming its first turn",
    "[GameTasks][CareerStartupReplay][CareerStartupMainRegression]")
{
    auto const config = PackagedConfig();
    CareerVisualResources resources(config);
    SequenceCareerObserver observer({
        Screen("career_final_confirmation", CareerScreenKind::Unknown),
        Screen("career_intro_event", CareerScreenKind::EventChoice),
        Screen("career_main", CareerScreenKind::MainTurn)});
    observer.goalTrap = true;
    StartupReplayContext context;
    std::string confirmedEntry;
    SECTION("Entry confirmed Final Confirmation") { confirmedEntry = "career_final_confirmation"; }
    SECTION("Startup recovered Final Confirmation") {}
    auto const result = NativeNormalCareerStartup::Run(config, resources, observer, context, confirmedEntry);
    REQUIRE(result.succeeded);
    REQUIRE(result.runtimeObservation.has_value());
    CHECK(result.runtimeObservation->screenId == "career_main");
    CHECK(result.actionsCompleted == 10);
    for (std::size_t index = 1; index < observer.candidateHistory.size(); ++index)
    {
        auto const& candidates = observer.candidateHistory[index];
        CHECK(std::find(candidates.begin(), candidates.end(), "goal_objective_complete") == candidates.end());
        CHECK(std::find(candidates.begin(), candidates.end(), "race_runner") == candidates.end());
    }
}

