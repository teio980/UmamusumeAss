#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerFlowCoordinator.hpp"
#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"

#include <algorithm>
#include <cctype>
#include <deque>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace
{

using namespace UmaAssistant::Career;
using namespace UmaAssistant::CareerDomain;
using UmaAssistant::TaskEngine::ITaskExecutionContext;
using UmaAssistant::TaskEngine::TaskFrameSnapshot;
using UmaAssistant::TaskEngine::TaskMatchResult;
using UmaAssistant::TaskEngine::TaskOcrDetection;
using UmaAssistant::TaskEngine::TaskRoi;

std::string FoldFilename(const std::string& path)
{
    auto name = std::filesystem::path(path).filename().string();
    std::transform(name.begin(), name.end(), name.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return name;
}

class RaceSelectionReplayContext final : public ITaskExecutionContext
{
public:
    bool CaptureScreen() override
    {
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = 900;
        frame->height = 1600;
        frame->frameId = ++frameId;
        frame->grayPixels.assign(900u * 1600u, 0);
        currentFrame = std::move(frame);
        return true;
    }

    TaskMatchResult MatchTemplate(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm) override
    {
        return MatchTemplateAdvanced(path, threshold, roi, algorithm, 900, 1600, {});
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>&, const std::string&, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        const auto filename = FoldFilename(path);
        matchedTemplates.push_back(filename);
        TaskMatchResult match;
        match.found = filename != "race_list_recommended.png";
        match.score = match.found ? (std::max)(threshold, 0.99) : 0.0;
        match.x = 400;
        match.y = 900;
        match.width = 60;
        match.height = 40;
        match.referenceWidth = referenceWidth;
        match.referenceHeight = referenceHeight;
        match.frameId = currentFrame ? currentFrame->frameId : 0;
        return match;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return currentFrame; }
    bool CheckImageChange(const TaskMatchResult&) override { return false; }
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override
    { return false; }
    std::vector<TaskOcrDetection> RecognizeText(const std::string&,
        const std::optional<TaskRoi>&) override { return {}; }
    bool Tap(int x, int y) override { taps.emplace_back(x, y); return true; }
    bool Swipe(int, int, int, int, int) override { return true; }
    bool InputText(const std::string&) override { return true; }
    bool SendKey(const std::string&) override { return true; }
    void Sleep(int) override {}
    bool IsCancelled() const override { return false; }
    void Log(const std::string&, const std::string&) override {}
    void EmitEvent(const UmaAssistant::TaskEngine::TaskExecutionEvent&) override {}

    std::vector<std::string> matchedTemplates;
    std::vector<std::pair<int, int>> taps;

private:
    std::uint64_t frameId{0};
    std::shared_ptr<TaskFrameSnapshot> currentFrame;
};

class ReplayObserver final : public ICareerObserver
{
public:
    CareerObservation Observe(ITaskExecutionContext&) override
    {
        if (observations.empty())
            return {};
        auto result = observations.front();
        observations.pop_front();
        return result;
    }

    std::deque<CareerObservation> observations;
};

class ReplayExecutor final : public ICareerActionExecutor
{
public:
    struct Execution
    {
        CareerObservation source;
        ActionIntent intent;
    };

    ActionExecutionStatus Execute(const ActionIntent&, ITaskExecutionContext&) override
    {
        return ActionExecutionStatus::Failed;
    }

    ActionExecutionStatus ExecuteForObservation(
        const ActionIntent& intent,
        const CareerObservation& observation,
        ITaskExecutionContext&) override
    {
        executions.push_back({observation, intent});
        return ActionExecutionStatus::Success;
    }

    std::optional<CareerRaceResultObservation> MatchRaceResult(
        const CareerRaceResultExpectation& expectation,
        ITaskExecutionContext& context) override
    {
        ++raceResultMatchCount;
        lastRaceResultExpectation = expectation;
        if (cancelRaceResultMatch)
        {
            context.Sleep(250);
            return std::nullopt;
        }
        return raceResultMatch;
    }

    bool ConfirmCompletion(const ActionIntent&, ITaskExecutionContext&) override
    {
        return false;
    }

    bool ConfirmCompletionFromObservation(
        const ActionIntent&,
        const CareerObservation&,
        ITaskExecutionContext&) override
    {
        return true;
    }

    std::shared_ptr<CareerVisualResources> GetCareerVisualResources() const override
    {
        return resources;
    }

    std::shared_ptr<CareerVisualResources> resources;
    std::vector<Execution> executions;
    std::optional<CareerRaceResultObservation> raceResultMatch;
    std::optional<CareerRaceResultExpectation> lastRaceResultExpectation;
    int raceResultMatchCount{0};
    bool cancelRaceResultMatch{false};
};

class ReplayScenario final : public IScenarioModule
{
public:
    struct SubmittedAction
    {
        ActionIntentKind kind;
        std::string sourceScreen;
    };

    explicit ReplayScenario(bool cancelWarning = false) : m_cancelWarning(cancelWarning) {}

    std::string GetScenarioId() const override { return "replay"; }
    std::string GetDisplayName() const override { return "Career replay"; }
    void OnTurnStart(CareerState&) override {}
    void OnObserve(CareerState& state, const CareerObservation& observation) override
    {
        state.UpdateFromObservation(observation);
    }
    void OnTurnActionSubmitted(
        CareerState&, const ActionIntent& intent, const CareerObservation& source) override
    {
        submittedActions.push_back({intent.kind, source.screenId});
    }
    std::optional<CareerRaceResultExpectation> GetCurrentRaceResultExpectation(
        const CareerState&) const override
    {
        return raceResultExpectation;
    }
    CareerRaceResultApplication OnRaceResultConfirmed(
        CareerState&, const CareerRaceResultObservation& result) override
    {
        ++raceResultApplicationCount;
        appliedRaceResults.push_back(result);
        if (consumeRaceExpectationOnApply)
            raceResultExpectation.reset();
        return raceResultApplication;
    }
    bool IsObjectiveCompleted(const CareerState&) const override { return false; }

    std::optional<RaceStreakWarningResolution> GetRaceStreakWarningResolution(
        const CareerState&) const override
    {
        if (!m_cancelWarning)
            return RaceStreakWarningResolution::ConfirmRace;
        return RaceStreakWarningResolution::CancelRace;
    }

private:
    bool m_cancelWarning;

public:
    std::vector<SubmittedAction> submittedActions;
    std::optional<CareerRaceResultExpectation> raceResultExpectation;
    CareerRaceResultApplication raceResultApplication{true, false, {}};
    std::vector<CareerRaceResultObservation> appliedRaceResults;
    int raceResultApplicationCount{0};
    bool consumeRaceExpectationOnApply{false};
};

class ReplayStrategy final : public ICareerStrategy
{
public:
    explicit ReplayStrategy(std::string raceTarget) : m_raceTarget(std::move(raceTarget)) {}

    ActionIntent Choose(const CareerState&, const IScenarioModule*) override
    {
        if (!m_raceChosen)
        {
            m_raceChosen = true;
            return ActionIntent::MakeRace(m_raceTarget, "late", "Replay scheduled race.");
        }
        return ActionIntent::MakeTraining("speed", "Replay next main-turn action.");
    }

private:
    std::string m_raceTarget;
    bool m_raceChosen{false};
};

class TrainingOnlyStrategy final : public ICareerStrategy
{
public:
    ActionIntent Choose(const CareerState&, const IScenarioModule*) override
    {
        return ActionIntent::MakeTraining("speed", "Unknown-energy fallback replay.");
    }
};

class IntentQueueStrategy final : public ICareerStrategy
{
public:
    explicit IntentQueueStrategy(std::vector<ActionIntent> intents)
        : m_intents(std::move(intents))
    {
    }

    ActionIntent Choose(const CareerState&, const IScenarioModule*) override
    {
        if (m_intents.empty())
            return ActionIntent::MakeAdvance("No replay intent was configured.");
        const auto index = (std::min)(m_nextIntent, m_intents.size() - 1);
        ++m_nextIntent;
        return m_intents[index];
    }

private:
    std::vector<ActionIntent> m_intents;
    std::size_t m_nextIntent{0};
};

CareerObservation Screen(std::string id, CareerScreenKind kind)
{
    CareerObservation observation;
    observation.screenId = std::move(id);
    observation.screenKind = kind;
    observation.score = 0.99;
    return observation;
}

void PushFullRaceFlow(ReplayObserver& observer)
{
    observer.observations = {
        Screen("career_main", CareerScreenKind::MainTurn),
        Screen("career_races_ready", CareerScreenKind::RaceEntry),
        Screen("race_list", CareerScreenKind::RaceEntry),
        Screen("race_day", CareerScreenKind::RaceEntry),
        Screen("race_runner", CareerScreenKind::RaceExecution),
        Screen("race_runner_result", CareerScreenKind::RaceResult),
        Screen("career_main", CareerScreenKind::MainTurn)};
}

void CheckRaceTargetReplay(const std::string& target)
{
    auto observer = std::make_shared<ReplayObserver>();
    auto executor = std::make_shared<ReplayExecutor>();
    PushFullRaceFlow(*observer);
    CareerFlowCoordinator coordinator(observer, executor,
        std::make_shared<ReplayScenario>(), std::make_shared<ReplayStrategy>(target));
    UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

    for (int i = 0; i < 7; ++i)
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);

    REQUIRE(executor->executions.size() == 7);
    CHECK(executor->executions[0].source.screenId == "career_main");
    CHECK(executor->executions[0].intent.raceTarget == target);
    for (std::size_t i = 1; i <= 5; ++i)
    {
        CHECK(executor->executions[i].intent.kind == ActionIntentKind::Race);
        CHECK(executor->executions[i].intent.raceTarget == target);
    }
    CHECK(executor->executions[2].source.screenId == "race_list");
    CHECK(executor->executions[2].intent.tactic == "late");
    CHECK(executor->executions[3].source.screenId == "race_day");
    CHECK(executor->executions[3].intent.tactic == "late");
    CHECK(executor->executions[4].source.screenId == "race_runner");
    CHECK(executor->executions[5].source.screenId == "race_runner_result");
    CHECK(executor->executions[6].source.screenId == "career_main");
    CHECK(executor->executions[6].intent.kind == ActionIntentKind::Training);
}

} // namespace

TEST_CASE("Career race target and lineup survive the resource race flow", "[Career][Replay]")
{
    SECTION("Fans target")
    {
        CheckRaceTargetReplay("fans");
    }
    SECTION("Grade-race-count target")
    {
        CheckRaceTargetReplay("grade_race_count");
    }
}

TEST_CASE("Cancelling a race streak warning and returning to Main clears the pending target", "[Career][Replay]")
{
    auto observer = std::make_shared<ReplayObserver>();
    auto executor = std::make_shared<ReplayExecutor>();
    observer->observations = {
        Screen("career_main", CareerScreenKind::MainTurn),
        Screen("race_streak_warning", CareerScreenKind::RaceEntry),
        Screen("career_main", CareerScreenKind::MainTurn),
        Screen("race_list", CareerScreenKind::RaceEntry)};
    auto scenario = std::make_shared<ReplayScenario>(true);
    auto strategy = std::make_shared<ReplayStrategy>("fans");
    CareerFlowCoordinator coordinator(observer, executor, scenario, strategy);
    UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

    for (int i = 0; i < 4; ++i)
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);

    REQUIRE(executor->executions.size() == 4);
    CHECK(executor->executions[0].intent.raceTarget == "fans");
    CHECK(executor->executions[1].source.screenId == "race_streak_warning");
    CHECK(executor->executions[1].intent.raceTarget == "fans");
    CHECK(executor->executions[1].intent.customActionName == "race.streak.cancel");
    CHECK(executor->executions[2].source.screenId == "career_main");
    CHECK(executor->executions[2].intent.kind == ActionIntentKind::Training);
    CHECK(executor->executions[3].source.screenId == "race_list");
    CHECK(executor->executions[3].intent.kind == ActionIntentKind::Race);
    CHECK(executor->executions[3].intent.raceTarget.empty());
}

TEST_CASE("Declining a race retry fails closed if ordinary Career flow resumes", "[Career][Replay]")
{
    auto observer = std::make_shared<ReplayObserver>();
    auto executor = std::make_shared<ReplayExecutor>();
    observer->observations = {
        Screen("race_retry_dialog", CareerScreenKind::ConfirmationDialog),
        Screen("race_runner_result", CareerScreenKind::RaceResult),
        Screen("career_main", CareerScreenKind::MainTurn)};
    CareerRuntimeConfig config;
    config.retryFailedRaceWithAlarmClock = false;
    CareerFlowCoordinator coordinator(observer, executor,
        std::make_shared<ReplayScenario>(), std::make_shared<TrainingOnlyStrategy>(), config);
    UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    const auto resumed = coordinator.Step(context);
    CHECK(resumed.status == CareerRunStatus::Failed);
    CHECK(resumed.failureReason.find("Race retry was declined") != std::string::npos);
    CHECK(resumed.failureReason.find("career_main") != std::string::npos);
    CHECK(executor->executions.size() == 2);
    CHECK(executor->executions[0].source.screenId == "race_retry_dialog");
    CHECK(executor->executions[1].source.screenId == "race_runner_result");
}

TEST_CASE("Race result replay applies only verified placements before advancing", "[Career][RaceResult][Replay]")
{
    const CareerRaceResultExpectation expected{
        "junior_debut", "career.runtime_frame:junior_debut_result", 1, 0.95};
    const CareerRaceResultObservation verified{"junior_debut", 1, 0.92};

    SECTION("A verified result is applied once and then the flow advances")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations = {
            Screen("race_result", CareerScreenKind::RaceResult),
            Screen("career_main", CareerScreenKind::MainTurn)};
        auto executor = std::make_shared<ReplayExecutor>();
        executor->raceResultMatch = verified;
        auto scenario = std::make_shared<ReplayScenario>();
        scenario->raceResultExpectation = expected;
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(scenario->raceResultApplicationCount == 1);
        CHECK(scenario->appliedRaceResults.front().raceId == "junior_debut");
        CHECK(scenario->appliedRaceResults.front().placement == 1);
        CHECK(executor->executions.size() == 1);
        CHECK(executor->executions.front().source.screenId == "race_result");
        CHECK(executor->raceResultMatchCount == 1);

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(scenario->raceResultApplicationCount == 1);
        CHECK(executor->raceResultMatchCount == 1);
    }

    SECTION("A repeated stale result cannot be applied a second time")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations = {
            Screen("race_result", CareerScreenKind::RaceResult),
            Screen("race_result", CareerScreenKind::RaceResult)};
        auto executor = std::make_shared<ReplayExecutor>();
        executor->raceResultMatch = verified;
        auto scenario = std::make_shared<ReplayScenario>();
        scenario->raceResultExpectation = expected;
        scenario->consumeRaceExpectationOnApply = true;
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        const auto repeated = coordinator.Step(context);
        CHECK(repeated.status == CareerRunStatus::Failed);
        CHECK(scenario->raceResultApplicationCount == 1);
        CHECK(executor->raceResultMatchCount == 1);
        CHECK(executor->executions.size() == 1);
    }

    SECTION("Missing expectation or unmatched result never sends an action")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("race_result", CareerScreenKind::RaceResult));
        auto executor = std::make_shared<ReplayExecutor>();
        executor->raceResultMatch = verified;
        auto scenario = std::make_shared<ReplayScenario>();
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
        CHECK(executor->raceResultMatchCount == 0);
        CHECK(scenario->raceResultApplicationCount == 0);
        CHECK(executor->executions.empty());
    }

    SECTION("An unknown placement frame fails closed without applying or advancing")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("race_result", CareerScreenKind::RaceResult));
        auto executor = std::make_shared<ReplayExecutor>();
        auto scenario = std::make_shared<ReplayScenario>();
        scenario->raceResultExpectation = expected;
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        const auto result = coordinator.Step(context);
        CHECK(result.status == CareerRunStatus::Failed);
        CHECK(result.failureReason.find("placement remains unknown") != std::string::npos);
        CHECK(executor->raceResultMatchCount == 1);
        CHECK(scenario->raceResultApplicationCount == 0);
        CHECK(executor->executions.empty());
    }

    SECTION("A rejected verified result never advances the screen")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("race_result", CareerScreenKind::RaceResult));
        auto executor = std::make_shared<ReplayExecutor>();
        executor->raceResultMatch = verified;
        auto scenario = std::make_shared<ReplayScenario>();
        scenario->raceResultExpectation = expected;
        scenario->raceResultApplication = {false, false, "Scenario result evidence was rejected."};
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        const auto result = coordinator.Step(context);
        CHECK(result.status == CareerRunStatus::Failed);
        CHECK(result.failureReason == "Scenario result evidence was rejected.");
        CHECK(scenario->raceResultApplicationCount == 1);
        CHECK(executor->executions.empty());
    }

    SECTION("Cancellation during result matching is reported as cancellation")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("race_result", CareerScreenKind::RaceResult));
        auto executor = std::make_shared<ReplayExecutor>();
        executor->cancelRaceResultMatch = true;
        auto scenario = std::make_shared<ReplayScenario>();
        scenario->raceResultExpectation = expected;
        CareerFlowCoordinator coordinator(observer, executor, scenario,
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;
        context.cancelOnSleep = true;

        CHECK(coordinator.Step(context).status == CareerRunStatus::Cancelled);
        CHECK(scenario->raceResultApplicationCount == 0);
        CHECK(executor->executions.empty());
    }
}

TEST_CASE("Goal races reuse the managed recommended-entry graph and its fans fallback", "[Career][ActionReplay]")
{
    CareerRuntimeConfig config;
    config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    config.manifestPath = UmaAssistant::Paths::ToUtf8(
        config.resourceDirectory / "hachimi" / "ura" / "manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    CAPTURE(config.manifestPath);
    std::string loadDiagnostics;
    for (const auto& diagnostic : resources->GetLoadDiagnostics())
        loadDiagnostics += (loadDiagnostics.empty() ? "" : "\n") + diagnostic;
    CAPTURE(loadDiagnostics);
    REQUIRE(resources->IsReady());
    const auto* recommended = resources->FindAction("race_list", "race.recommended_entry");
    REQUIRE(recommended != nullptr);
    CHECK(recommended->taskName == "race_list_race_recommended_entry");

    RaceSelectionReplayContext context;
    CareerActionExecutor executor(config, resources);
    const auto status = executor.ExecuteForObservation(
        ActionIntent::MakeRace("goal", "pace", "Managed default race-goal entry."),
        Screen("race_list", CareerScreenKind::RaceEntry), context);

    REQUIRE(status == ActionExecutionStatus::Success);
    CHECK(std::find(context.matchedTemplates.begin(), context.matchedTemplates.end(),
        "race_list_recommended.png") != context.matchedTemplates.end());
    CHECK(std::find(context.matchedTemplates.begin(), context.matchedTemplates.end(),
        "race_list_race_goal_entry.png") == context.matchedTemplates.end());
    REQUIRE(context.taps.size() >= 2);
    CHECK((context.taps[0] == std::pair<int, int>{450, 950})); // fans-row fallback
    CHECK((context.taps[1] == std::pair<int, int>{450, 1360})); // race-list start
}

TEST_CASE("Unreliable Main-turn energy uses the resource-backed cautious Rest fallback", "[Career][Replay]")
{
    SECTION("Rest is selected when the packaged action is available")
    {
        CareerRuntimeConfig config;
        config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
        config.manifestPath = UmaAssistant::Paths::ToUtf8(
            config.resourceDirectory / "hachimi" / "ura" / "manifest.json");
        auto resources = std::make_shared<CareerVisualResources>(config);
        REQUIRE(resources->IsReady());
        REQUIRE(resources->FindAction("career_main", "action.rest") != nullptr);

        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("career_main", CareerScreenKind::MainTurn));
        auto executor = std::make_shared<ReplayExecutor>();
        executor->resources = resources;
        CareerFlowCoordinator coordinator(observer, executor, std::make_shared<ReplayScenario>(),
            std::make_shared<TrainingOnlyStrategy>(), config);
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executions.size() == 1);
        CHECK(executor->executions.front().intent.kind == ActionIntentKind::Rest);
        CHECK(executor->executions.front().intent.source == ObservationSource::DefaultFallback);
        CHECK(executor->executions.front().intent.confidence == 0.0);
    }

    SECTION("The configured action is retained when Rest is not available to the runtime")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations.push_back(Screen("career_main", CareerScreenKind::MainTurn));
        auto executor = std::make_shared<ReplayExecutor>();
        CareerFlowCoordinator coordinator(observer, executor, std::make_shared<ReplayScenario>(),
            std::make_shared<TrainingOnlyStrategy>());
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executions.size() == 1);
        CHECK(executor->executions.front().intent.kind == ActionIntentKind::Training);
    }
}

TEST_CASE("Rest, infirmary, and recreation commit only after their resource confirmation", "[Career][Replay]")
{
    SECTION("Rest waits for the confirmation tap")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations = {
            Screen("career_main", CareerScreenKind::MainTurn),
            Screen("rest_confirmation", CareerScreenKind::Rest)};
        auto executor = std::make_shared<ReplayExecutor>();
        auto scenario = std::make_shared<ReplayScenario>();
        auto strategy = std::make_shared<IntentQueueStrategy>(std::vector<ActionIntent>{
            ActionIntent::MakeRest("Replay rest.")});
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy);
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(scenario->submittedActions.empty());
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executions.size() == 2);
        CHECK(executor->executions[0].intent.kind == ActionIntentKind::Rest);
        CHECK(executor->executions[1].source.screenId == "rest_confirmation");
        CHECK(executor->executions[1].intent.kind == ActionIntentKind::Rest);
        REQUIRE(scenario->submittedActions.size() == 1);
        CHECK(scenario->submittedActions[0].kind == ActionIntentKind::Rest);
        CHECK(scenario->submittedActions[0].sourceScreen == "rest_confirmation");
    }

    SECTION("Infirmary waits for the confirmation tap")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations = {
            Screen("career_main", CareerScreenKind::MainTurn),
            Screen("infirmary_confirmation", CareerScreenKind::Infirmary)};
        auto executor = std::make_shared<ReplayExecutor>();
        auto scenario = std::make_shared<ReplayScenario>();
        auto strategy = std::make_shared<IntentQueueStrategy>(std::vector<ActionIntent>{
            ActionIntent::MakeInfirmary("Replay infirmary." )});
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy);
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(scenario->submittedActions.empty());
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executions.size() == 2);
        CHECK(executor->executions[1].intent.kind == ActionIntentKind::Infirmary);
        REQUIRE(scenario->submittedActions.size() == 1);
        CHECK(scenario->submittedActions[0].kind == ActionIntentKind::Infirmary);
        CHECK(scenario->submittedActions[0].sourceScreen == "infirmary_confirmation");
    }

    SECTION("Recreation waits through trainee selection and commits at confirmation")
    {
        auto observer = std::make_shared<ReplayObserver>();
        observer->observations = {
            Screen("career_main", CareerScreenKind::MainTurn),
            Screen("recreation_selection", CareerScreenKind::Recreation),
            Screen("recreation_confirmation", CareerScreenKind::Recreation)};
        auto executor = std::make_shared<ReplayExecutor>();
        auto scenario = std::make_shared<ReplayScenario>();
        auto strategy = std::make_shared<IntentQueueStrategy>(std::vector<ActionIntent>{
            ActionIntent::MakeRecreation("Replay recreation.")});
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy);
        UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext context;

        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(scenario->submittedActions.empty());
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executions.size() == 3);
        CHECK(executor->executions[1].source.screenId == "recreation_selection");
        CHECK(executor->executions[1].intent.kind == ActionIntentKind::Recreation);
        CHECK(executor->executions[2].source.screenId == "recreation_confirmation");
        CHECK(executor->executions[2].intent.kind == ActionIntentKind::Recreation);
        REQUIRE(scenario->submittedActions.size() == 1);
        CHECK(scenario->submittedActions[0].kind == ActionIntentKind::Recreation);
        CHECK(scenario->submittedActions[0].sourceScreen == "recreation_confirmation");
    }
}
