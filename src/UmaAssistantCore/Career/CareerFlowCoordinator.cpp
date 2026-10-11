#include "CareerFlowCoordinator.hpp"
#include "CareerVisualResources.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>

namespace UmaAssistant::Career
{

namespace
{

std::string Fold(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

bool IsTerminalScreen(const std::string& screenId)
{
    const auto id = Fold(screenId);
    return id == "career_complete" || id == "career_complete_close";
}

bool IsUnexpectedScreenAfterDeclinedRaceRetry(const std::string& screenId)
{
    const auto id = Fold(screenId);
    return id == "career_main" || id == "training_selection"
        || id == "race_runner" || id == "race_day" || id == "race_list"
        || id == "race_list_empty" || id == "race_streak_warning"
        || id == "race_details" || id == "race_attributes"
        || id == "race_playback" || id == "race_playback_start";
}

bool IsCommittedTurnAction(
    const CareerDomain::ActionIntent& intent,
    const CareerDomain::CareerObservation& source)
{
    const auto screenId = Fold(source.screenId);
    switch (intent.kind)
    {
    case CareerDomain::ActionIntentKind::Rest:
        // Main-turn Rest opens a confirmation dialog. The turn is consumed
        // only after the dialog's affirmative action is verified.
        return source.screenKind == CareerDomain::CareerScreenKind::Rest
            || screenId == "rest_confirmation"
            || screenId == "summer_rest_confirmation";
    case CareerDomain::ActionIntentKind::Infirmary:
        return source.screenKind == CareerDomain::CareerScreenKind::Infirmary
            || screenId == "infirmary_confirmation";
    case CareerDomain::ActionIntentKind::Recreation:
        // Selecting the trainee is an intermediate page. Recreation commits
        // only when its confirmation action is verified.
        return (source.screenKind == CareerDomain::CareerScreenKind::Recreation
                && screenId != "recreation_selection")
            || screenId == "recreation_confirmation";
    default:
        return true;
    }
}

std::string IntentSemantic(const CareerDomain::ActionIntent& intent)
{
    using Kind = CareerDomain::ActionIntentKind;
    switch (intent.kind)
    {
    case Kind::Training: return "training.select";
    case Kind::Rest: return "rest.select";
    case Kind::Infirmary: return "infirmary.select";
    case Kind::Recreation: return "recreation.select";
    case Kind::SelectEventOption: return "event.choose";
    case Kind::Race: return "race.progress";
    case Kind::Skill: return "skill.learn";
    case Kind::DoNothing: return "wait";
    case Kind::Custom: return "custom";
    }
    return "advance";
}

void EmitCareerEvent(
    TaskEngine::ITaskExecutionContext& context,
    TaskEngine::TaskEventType type,
    const std::string& pipeline,
    const std::string& taskName,
    bool succeeded,
    double score,
    const std::string& message)
{
    TaskEngine::TaskExecutionEvent event{};
    event.type = type;
    event.pipelineName = pipeline;
    event.taskName = taskName;
    event.succeeded = succeeded;
    event.score = score;
    event.message = message;
    event.timestampMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    context.EmitEvent(event);
}

} // namespace

CareerFlowCoordinator::CareerFlowCoordinator(
    std::shared_ptr<ICareerObserver> observer,
    std::shared_ptr<ICareerActionExecutor> actionExecutor,
    std::shared_ptr<CareerDomain::IScenarioModule> scenario,
    std::shared_ptr<CareerDomain::ICareerStrategy> strategy,
    CareerRuntimeConfig config,
    std::shared_ptr<ICareerTrainingFlow> trainingFlow)
    : m_observer(std::move(observer))
    , m_actionExecutor(std::move(actionExecutor))
    , m_scenario(std::move(scenario))
    , m_strategy(std::move(strategy))
    , m_trainingFlow(std::move(trainingFlow))
    , m_config(std::move(config))
{
    if (m_scenario)
    {
        m_state.scenarioId = m_scenario->GetScenarioId();
        m_scenario->OnRunStart(m_state, m_config.continueExistingCareer);
    }
}

void CareerFlowCoordinator::ResetAfterRecovery() noexcept
{
    m_nextConfirmedObservation.reset();
    m_pendingTraining.reset();
    m_pendingTrainingTurn.reset();
    // A race target is durable across the date-change/home recovery path: the
    // game may resume inside the same race flow after returning to Home.
    m_terminalScreenSeen = false;
    m_unknownObservations = 0;
    if (m_trainingFlow)
        m_trainingFlow->Reset();
    if (m_strategy)
        m_strategy->ClearTrainingCandidateEvidence();
    if (m_observer)
        m_observer->ResetForRecovery();
    if (m_actionExecutor)
        m_actionExecutor->ResetAfterRecovery();
}

CareerRunResult CareerFlowCoordinator::Step(TaskEngine::ITaskExecutionContext& context)
{
    CareerRunResult result;
    result.status = CareerRunStatus::Running;
    if (context.IsCancelled())
    {
        result.status = CareerRunStatus::Cancelled;
        return result;
    }
    if (!m_observer || !m_actionExecutor || !m_scenario || !m_strategy)
    {
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Career runtime is missing its observer, action executor, scenario, or normal-mode strategy.";
        return result;
    }

    auto confirmedObservation = std::move(m_nextConfirmedObservation);
    m_nextConfirmedObservation.reset();
    if (!confirmedObservation)
        confirmedObservation = m_actionExecutor->TakeConfirmedObservation();
    auto observation = confirmedObservation
        ? std::move(*confirmedObservation) : m_observer->Observe(context);
    if (context.IsCancelled())
    {
        result.status = CareerRunStatus::Cancelled;
        return result;
    }
    if (observation.screenId == "capture_cancelled" || context.IsCancelled())
    {
        result.status = CareerRunStatus::Cancelled;
        return result;
    }
    if (observation.screenId == "resource_error")
    {
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Career screen observation failed: " + observation.screenId;
        return result;
    }
    if (m_state.raceRetryDeclined && IsUnexpectedScreenAfterDeclinedRaceRetry(observation.screenId))
    {
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Race retry was declined, but '" + observation.screenId
            + "' appeared instead of the race result or settlement.";
        return result;
    }
    EmitCareerEvent(context, TaskEngine::TaskEventType::RecognitionEvaluated,
        m_state.scenarioId, observation.screenId,
        observation.screenKind != CareerDomain::CareerScreenKind::Unknown,
        observation.score,
        "Career screen observation evaluated; frame-bound evidence retained.");
    if (observation.screenKind == CareerDomain::CareerScreenKind::MainTurn)
    {
        // The managed runtime treats missing readings on a new main-turn frame
        // as unknown instead of carrying a previous turn's values into a new
        // decision. Preserve the numeric turn counter for reporting, but clear
        // per-observation evidence before this frame is applied.
        m_state.energy = CareerDomain::ObservedValue<int>::Unknown();
        m_state.mood = CareerDomain::ObservedValue<std::string>::Unknown();
        m_state.infirmaryAvailable = false;
        m_state.infirmaryEvidence = CareerDomain::ObservedValue<bool>::Unknown();
        m_state.turnIndexEvidence = CareerDomain::ObservedValue<int>::Unknown();
        m_state.turnPositionText.reset();
        m_state.turnsToGoal = CareerDomain::ObservedValue<int>::Unknown();
        m_state.fansToGoal = CareerDomain::ObservedValue<int>::Unknown();
        m_state.objectiveCompleted = CareerDomain::ObservedValue<bool>::Unknown();
        m_state.goalText.clear();
    }
    m_scenario->OnObserve(m_state, observation);
    if (observation.screenKind != CareerDomain::CareerScreenKind::Unknown)
        m_unknownObservations = 0;

    const auto finishRunning = [&]() {
        result.status = CareerRunStatus::Running;
        result.totalTurnsExecuted = m_state.turnIndex;
        return result;
    };
    const auto failAction = [&](const std::string& operation, ActionExecutionStatus status) {
        if (status == ActionExecutionStatus::Cancelled || context.IsCancelled())
        {
            result.status = CareerRunStatus::Cancelled;
            return;
        }
        result.status = CareerRunStatus::Failed;
        result.failureReason = operation;
    };
    const auto perform = [&](const CareerDomain::ActionIntent& intent) {
        m_state.lastIntent = intent;
        const auto actionTask = Fold(observation.screenId) + "." + IntentSemantic(intent);
        EmitCareerEvent(context, TaskEngine::TaskEventType::ActionExecuting,
            m_state.scenarioId, actionTask, false, observation.score, intent.reason);
        const auto status = m_actionExecutor->ExecuteForObservation(intent, observation, context);
        if (status != ActionExecutionStatus::Success)
        {
            EmitCareerEvent(context, TaskEngine::TaskEventType::ActionExecuted,
                m_state.scenarioId, actionTask, false, observation.score,
                "Resource action failed or was cancelled.");
            failAction("Career action could not execute safely: " + intent.reason, status);
            return false;
        }
        EmitCareerEvent(context, TaskEngine::TaskEventType::ActionExecuted,
            m_state.scenarioId, actionTask, true, observation.score, intent.reason);
        if (intent.kind == CareerDomain::ActionIntentKind::Training
            && m_trainingFlow && m_trainingFlow->HasPendingConfirmedTap()
            && !m_trainingFlow->MarkTrainingTapSent())
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "The confirmed Ura training tap could not be committed to its scan session.";
            return false;
        }
        if (!m_actionExecutor->ConfirmCompletionFromObservation(intent, observation, context))
        {
            if (context.IsCancelled())
                result.status = CareerRunStatus::Cancelled;
            else
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Career action produced no resource-confirmed screen transition: "
                    + observation.screenId;
            }
            return false;
        }
        m_nextConfirmedObservation = m_actionExecutor->TakeConfirmedObservation();
        const auto destination = m_nextConfirmedObservation
            ? m_nextConfirmedObservation->screenId : std::string("recognized transition");
        const auto transitionScore = m_nextConfirmedObservation
            ? m_nextConfirmedObservation->score : observation.score;
        EmitCareerEvent(context, TaskEngine::TaskEventType::TransitionVerified,
            m_state.scenarioId, actionTask, true, transitionScore,
            observation.screenId + " -> " + destination);
        if (IsCommittedTurnAction(intent, observation))
            m_scenario->OnTurnActionSubmitted(m_state, intent, observation);
        if (Fold(observation.screenId) == "race_retry_dialog")
        {
            const auto observedRetryChoice = m_actionExecutor->TakeRaceRetryDeclined();
            // The composed graph may fall back from a missing Try Again marker
            // to its explicit Cancel branch even when retry was configured.
            m_state.raceRetryDeclined = observedRetryChoice.value_or(
                !m_config.retryFailedRaceWithAlarmClock);
        }
        if (intent.kind == CareerDomain::ActionIntentKind::Race
            && observation.screenKind == CareerDomain::CareerScreenKind::MainTurn)
            m_pendingRaceIntent = intent;
        return true;
    };

    if (Fold(observation.screenId) == "home")
    {
        if (m_terminalScreenSeen)
        {
            result.status = CareerRunStatus::Completed;
            result.totalTurnsExecuted = m_state.turnIndex;
            return result;
        }
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Career returned to Home before a terminal settlement screen was confirmed.";
        return result;
    }
    if (IsTerminalScreen(observation.screenId))
        m_terminalScreenSeen = true;

    switch (observation.screenKind)
    {
    case CareerDomain::CareerScreenKind::MainTurn:
    {
        m_pendingRaceIntent.reset();
        const bool candidateScanStrategy = m_strategy
            && m_strategy->RequiresTrainingCandidateScan();
        if (candidateScanStrategy && !m_trainingFlow)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "The selected strategy requires a registered scenario training flow.";
            return result;
        }
        if (candidateScanStrategy)
        {
            if (auto committedTraining = m_trainingFlow->ObserveOutcome(observation))
            {
                m_strategy->ConfirmTraining(m_state, *committedTraining);
                m_pendingTraining.reset();
                m_pendingTrainingTurn.reset();
            }
        }
        m_scenario->OnTurnStart(m_state);
        if (m_pendingTraining && m_pendingTrainingTurn && m_state.turnIndex > *m_pendingTrainingTurn)
        {
            if (m_strategy)
                m_strategy->ConfirmTraining(m_state, m_pendingTraining->trainingType);
            m_pendingTraining.reset();
            m_pendingTrainingTurn.reset();
        }
        CareerDomain::ActionIntent intent;
        {
            intent = m_strategy->Choose(m_state, m_scenario.get());
            if (candidateScanStrategy && m_trainingFlow
                && m_strategy->ShouldScanTrainingCandidates(m_state, m_scenario.get())
                && intent.kind == CareerDomain::ActionIntentKind::Rest)
            {
                // Smart mode must enter the resource-defined selection screen
                // to read the current card evidence before choosing.
                intent = CareerDomain::ActionIntent::MakeTraining(
                    "speed", "Open training selection for frame-bound smart scan.");
            }
        }
        if (intent.kind == CareerDomain::ActionIntentKind::Training
            && !m_state.energy.IsReliable())
        {
            const auto resources = m_actionExecutor->GetCareerVisualResources();
            const auto* rest = resources ? resources->FindAction("career_main", "action.rest") : nullptr;
            const auto* summerRest = resources
                ? resources->FindAction("career_main", "action.summer_rest") : nullptr;
            if ((rest && rest->valid) || (summerRest && summerRest->valid))
            {
                intent = CareerDomain::ActionIntent::MakeRest(
                    "Energy could not be read reliably; use the managed cautious Rest fallback.",
                    0.0,
                    CareerDomain::ObservationSource::DefaultFallback);
            }
        }
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::TrainingSelect:
    {
        CareerDomain::ActionIntent intent;
        if (m_strategy && m_strategy->RequiresTrainingCandidateScan())
        {
            if (!m_trainingFlow)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Candidate-scanning strategy has no registered scenario training flow.";
                return result;
            }
            auto selected = m_trainingFlow->Select(context, m_state, *m_scenario, *m_strategy);
            if (selected.status == CareerTrainingSelectionStatus::Cancelled)
            {
                result.status = CareerRunStatus::Cancelled;
                return result;
            }
            if (selected.status == CareerTrainingSelectionStatus::Failed)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = selected.error.empty()
                    ? "Scenario training flow did not produce a safe action." : selected.error;
                return result;
            }
            intent = std::move(selected.intent);
            if (selected.status == CareerTrainingSelectionStatus::BackOut)
                intent.customActionName = "training.back";
        }
        else
        {
            intent = m_strategy->Choose(m_state, m_scenario.get());
        }
        if (intent.kind == CareerDomain::ActionIntentKind::Custom
            && intent.customActionName == "Advance" && Fold(observation.screenId) == "training_selection")
        {
            // Resource profiles use the same graph to return from the drawer.
            intent.customActionName = "training.back";
        }
        if (intent.kind == CareerDomain::ActionIntentKind::Custom
            && intent.customActionName == "training.back")
        {
            if (!perform(intent)) return result;
            return finishRunning();
        }
        if (intent.kind != CareerDomain::ActionIntentKind::Training)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "Training selection screen has no confirmed training choice.";
            return result;
        }
        if (!perform(intent)) return result;
        m_pendingTraining = intent;
        m_pendingTrainingTurn = m_state.turnIndex;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::TrainingResult:
    {
        if (m_strategy && m_strategy->RequiresTrainingCandidateScan() && m_trainingFlow)
        {
            const bool pendingOutcome = m_trainingFlow->HasPendingOutcome();
            if (auto committedTraining = m_trainingFlow->ObserveOutcome(observation))
                m_strategy->ConfirmTraining(m_state, *committedTraining);
            else if (pendingOutcome)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Training result did not provide a valid scenario training commit.";
                return result;
            }
        }
        else if (m_pendingTraining && m_strategy)
            m_strategy->ConfirmTraining(m_state, m_pendingTraining->trainingType);
        m_pendingTraining.reset();
        m_pendingTrainingTurn.reset();
        const auto intent = CareerDomain::ActionIntent::MakeAdvance("Advance the observed training result.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::EventChoice:
    {
        CareerDomain::ActionIntent intent;
        if (Fold(observation.screenId) == "event_choice")
        {
            // Keep the existing default event policy; the resource task itself
            // verifies the authored option marker before it taps.
            intent = CareerDomain::ActionIntent::MakeEventChoice(
                1, "Select the profile-backed default event option.");
        }
        else
            intent = CareerDomain::ActionIntent::MakeAdvance("Advance the observed career event.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::RaceEntry:
    case CareerDomain::CareerScreenKind::RaceExecution:
    case CareerDomain::CareerScreenKind::RaceResult:
    {
        if (Fold(observation.screenId) == "race_result" && !m_state.raceRetryDeclined)
        {
            const auto expectation = m_scenario->GetCurrentRaceResultExpectation(m_state);
            if (!expectation)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Race result has no scenario-provided expected capture and placement.";
                return result;
            }
            const auto verified = m_actionExecutor->MatchRaceResult(*expectation, context);
            if (!verified)
            {
                result.status = context.IsCancelled() ? CareerRunStatus::Cancelled : CareerRunStatus::Failed;
                if (result.status == CareerRunStatus::Failed)
                    result.failureReason = "The expected runtime frame did not verify race result '"
                        + expectation->raceId + "'; placement remains unknown.";
                return result;
            }
            const auto application = m_scenario->OnRaceResultConfirmed(m_state, *verified);
            if (!application.applied)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = application.failureReason.empty()
                    ? "Scenario rejected the verified race result; placement was not applied."
                    : application.failureReason;
                return result;
            }
            context.Log("INFO", "Applied verified result for race " + verified->raceId
                + (application.retryPending ? "; scenario requests a permitted retry." : "."));
        }
        auto intent = m_pendingRaceIntent.value_or(
            CareerDomain::ActionIntent::MakeRace({}, m_config.normalLineupStrategy,
                "Resume the recognized resource-defined race flow."));
        if (Fold(observation.screenId) == "race_streak_warning")
        {
            const auto resolution = m_scenario->GetRaceStreakWarningResolution(m_state);
            if (!resolution)
            {
                context.Log("WARNING", "Race-streak decision lacks current scenario evidence; waiting for another observation.");
                ++m_unknownObservations;
                context.Sleep(500);
                return finishRunning();
            }
            intent.customActionName = *resolution
                    == CareerDomain::RaceStreakWarningResolution::ConfirmRace
                ? "race.streak.confirm" : "race.streak.cancel";
        }
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::GoalUpdate:
    case CareerDomain::CareerScreenKind::ConfirmationDialog:
    case CareerDomain::CareerScreenKind::Settlement:
    case CareerDomain::CareerScreenKind::ClawMachine:
    {
        const auto intent = CareerDomain::ActionIntent::MakeAdvance(
            "Advance the action registered for " + observation.screenId + ".");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::Rest:
    {
        const auto intent = CareerDomain::ActionIntent::MakeRest(
            "Confirm the resource-recognized Rest action.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::Infirmary:
    {
        const auto intent = CareerDomain::ActionIntent::MakeInfirmary(
            "Confirm the resource-recognized Infirmary action.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::Recreation:
    {
        const auto intent = CareerDomain::ActionIntent::MakeRecreation(
            "Complete the resource-recognized Recreation action.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::SkillLearning:
    {
        const auto intent = CareerDomain::ActionIntent::MakeAdvance(
            "Recover the catalog-recognized skills page back to Race Day.");
        if (!perform(intent)) return result;
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::Unknown:
    default:
        if (!m_state.careerStarted)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "Career coordinator received a non-career entry screen: " + observation.screenId;
            return result;
        }
        ++m_unknownObservations;
        const int maximumUnknownObservations = m_config.pauseOnUnknownOutcome ? 30 : 120;
        if (m_unknownObservations >= maximumUnknownObservations)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "Career screen remained unrecognized for "
                + std::to_string(maximumUnknownObservations) + " observations. "
                + (m_config.pauseOnUnknownOutcome
                    ? "Paused because pauseOnUnknownOutcome is enabled."
                    : "Stopped at the runtime recovery limit.");
            return result;
        }
        context.Log("WARNING", "Career screen is currently unrecognized; waiting for profile evidence.");
        context.Sleep(500);
        return finishRunning();
    }
}

CareerRunResult CareerFlowCoordinator::RunToCompletion(
    TaskEngine::ITaskExecutionContext& context,
    int maxSteps)
{
    CareerRunResult finalResult;
    if (maxSteps <= 0)
    {
        finalResult.status = CareerRunStatus::Failed;
        finalResult.failureReason = "Career step budget must be positive.";
        return finalResult;
    }
    for (int step = 0; step < maxSteps; ++step)
    {
        if (context.IsCancelled())
        {
            finalResult.status = CareerRunStatus::Cancelled;
            finalResult.totalTurnsExecuted = m_state.turnIndex;
            return finalResult;
        }
        auto stepResult = Step(context);
        if (stepResult.status != CareerRunStatus::Running)
            return stepResult;
        context.Sleep(250);
    }
    finalResult.status = CareerRunStatus::Failed;
    finalResult.failureReason = "Career exceeded the configured step budget before reaching the confirmed Home screen.";
    finalResult.totalTurnsExecuted = m_state.turnIndex;
    return finalResult;
}

} // namespace UmaAssistant::Career
