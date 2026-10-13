#include "CareerFlowCoordinator.hpp"
#include "CareerVisualResources.hpp"
#include "CareerPersistenceStore.hpp"

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
    return id == "career_complete" || id == "career_complete_close"
        || id == "career_story_unlocked" || id == "career_story_unlocked_compact"
        || id == "career_story_unlocked_to_home";
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

std::string TransitionSemantic(const CareerDomain::ActionIntent& intent,
    const CareerDomain::CareerObservation& source)
{
    using Kind = CareerDomain::ActionIntentKind;
    const auto id = Fold(source.screenId);
    if (id == "career_main")
    {
        switch (intent.kind)
        {
        case Kind::Training: return "action.training";
        case Kind::Rest: return "action.rest";
        case Kind::Infirmary: return "action.infirmary";
        case Kind::Recreation: return "action.recreation";
        case Kind::Race: return intent.raceTarget == "finale" ? "action.finale_races" : "action.races";
        default: return {};
        }
    }
    if (id == "training_selection") return intent.kind == Kind::Training ? "training." + intent.trainingType : "training.back";
    if (id == "race_streak_warning") return intent.customActionName;
    if (id == "race_retry_dialog") return "";
    if (id == "recreation_confirmation") return "recreation.confirm";
    return source.eventActionId.value_or("");
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

std::string DisplayValue(const std::string& value)
{
    const auto begin = value.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) return "Unknown";
    const auto end = value.find_last_not_of(" \t\r\n");
    return value.substr(begin, end - begin + 1);
}

bool IsSummerCampText(const std::string& turnPositionText)
{
    std::string text = turnPositionText;
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    const bool eligibleYear = text.find("classic") != std::string::npos ||
                              text.find("senior") != std::string::npos;
    const bool summerMonth = text.find("month 7") != std::string::npos ||
                             text.find("month 8") != std::string::npos ||
                             text.find("july") != std::string::npos ||
                             text.find("august") != std::string::npos ||
                             text.find("jul ") != std::string::npos ||
                             text.find("aug ") != std::string::npos ||
                             text.find("7月") != std::string::npos ||
                             text.find("8月") != std::string::npos;
    return eligibleYear && summerMonth;
}

std::string FormatCareerStatus(
    const CareerDomain::CareerObservation& observation,
    const CareerDomain::CareerState& state)
{
    std::string dateStr = "Unknown";
    if (state.turnPositionText.has_value() && !state.turnPositionText->empty())
    {
        dateStr = DisplayValue(*state.turnPositionText);
        if (dateStr != "Unknown" && IsSummerCampText(*state.turnPositionText))
            dateStr += " [Summer Camp]";
    }
    else if (observation.turnPositionText.has_value() && !observation.turnPositionText->empty())
    {
        dateStr = DisplayValue(*observation.turnPositionText);
        if (dateStr != "Unknown" && IsSummerCampText(*observation.turnPositionText))
            dateStr += " [Summer Camp]";
    }

    std::string turnsLeftStr = "Unknown";
    if (observation.turnsToGoal.has_value())
        turnsLeftStr = std::to_string(*observation.turnsToGoal);
    else if (state.turnsToGoal.HasValue())
        turnsLeftStr = std::to_string(state.turnsToGoal.Value());

    std::string goalStr = "Unknown";
    if (observation.goalText.has_value() && !observation.goalText->empty())
        goalStr = DisplayValue(*observation.goalText);
    else if (!state.goalText.empty())
        goalStr = DisplayValue(state.goalText);

    std::string moodStr = "Unknown";
    if (state.mood.HasValue())
        moodStr = DisplayValue(state.mood.Value());
    else if (observation.moodText.has_value())
        moodStr = DisplayValue(*observation.moodText);

    std::string energyStr = "Unknown";
    if (state.energy.HasValue())
        energyStr = std::to_string(state.energy.Value()) + "%";
    else if (observation.energyPercent.HasValue())
        energyStr = std::to_string(observation.energyPercent.Value()) + "%";

    return "Date: " + dateStr + "\n"
        + "Turns left: " + turnsLeftStr + "\n"
        + "Goal: " + goalStr + "\n"
        + "Mood: " + moodStr + "\n"
        + "Energy: " + energyStr;
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
    m_transitionContext = {};
    m_pendingTraining.reset();
    m_pendingTrainingBaseline.reset();
    m_pendingTrainingTurnsToGoal.reset();
    m_restoredSmartTrainingConfirmation = false;
    m_persistenceLoaded = false;
    // A race target is durable across the date-change/home recovery path: the
    // game may resume inside the same race flow after returning to Home.
    m_terminalScreenSeen = false;
    m_unknownObservations = 0;
    m_lastLoggedCareerStatus.clear();
    if (m_trainingFlow)
        m_trainingFlow->Reset();
    if (m_strategy)
        m_strategy->ClearTrainingCandidateEvidence();
    if (m_observer)
        m_observer->ResetForRecovery();
    if (m_actionExecutor)
        m_actionExecutor->ResetAfterRecovery();
}

void CareerFlowCoordinator::SynchronizeObservationPolicy() noexcept
{
    const bool armed = m_scenario && m_scenario->IsGoalCompletionProbeArmed(m_state);
    const bool runnerResultExpected = m_pendingRaceIntent.has_value()
        && m_state.raceStrategyConfigured && Fold(m_state.lastScreenId) == "race_runner";
    if (m_observer)
    {
        m_observer->SetGoalCompletionProbeArmed(armed);
        m_observer->SetRaceReplayFlowCompleted(m_state.raceReplayFlowCompleted);
        m_observer->SetRaceRunnerResultExpected(runnerResultExpected);
    }
    if (m_actionExecutor)
    {
        m_actionExecutor->SetGoalObservationPolicy(armed, m_state.raceReplayFlowCompleted);
        m_actionExecutor->SetRaceRunnerResultExpected(runnerResultExpected);
    }
}

bool CareerFlowCoordinator::CheckAndConfirmTrainingConsumed(
    const CareerDomain::CareerObservation& observation,
    TaskEngine::ITaskExecutionContext& context)
{
    if (!m_pendingTraining)
        return false;

    const auto id = Fold(observation.screenId);
    const bool resultShown = observation.screenKind == CareerDomain::CareerScreenKind::TrainingResult
        || id == "training_result";
    const bool mandatoryRaceReached = id == "race_day" || id == "career_races_ready"
        || (id == "race_list" && observation.turnsToGoalEvidence.IsReliable()
            && observation.turnsToGoalEvidence.Value() == 0);
    const bool finaleCountdownAdvanced = id == "career_main"
        && observation.turnPositionText && Fold(*observation.turnPositionText).find("finale") != std::string::npos
        && m_pendingTrainingTurnsToGoal && *m_pendingTrainingTurnsToGoal > 0
        && observation.turnsToGoalEvidence.IsReliable()
        && observation.turnsToGoalEvidence.Value() == *m_pendingTrainingTurnsToGoal - 1;
    const bool mainPageProvedTurnAdvance = id == "career_main"
        && m_pendingTrainingBaseline && m_state.confirmedTurnActions > *m_pendingTrainingBaseline;

    if (!resultShown && !mandatoryRaceReached && !finaleCountdownAdvanced && !mainPageProvedTurnAdvance)
        return false;

    const auto trainingType = m_pendingTraining->trainingType;
    context.Log("INFO", "Training consumption verified (" + trainingType + ") via runtime evidence.");
    if (m_strategy)
        m_strategy->ConfirmTraining(m_state, trainingType);
    if (m_trainingFlow)
        m_trainingFlow->Reset();

    m_pendingTraining.reset();
    m_pendingTrainingBaseline.reset();
    m_pendingTrainingTurnsToGoal.reset();
    m_restoredSmartTrainingConfirmation = false;
    if (m_config.traineeId)
    {
        SmartTrainingConfirmationStore::Clear(
            m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial());
    }
    return true;
}

bool CareerFlowCoordinator::TryReconcileResumedTrainingConfirmation(
    const CareerDomain::CareerObservation& observation,
    TaskEngine::ITaskExecutionContext& context)
{
    if (!m_restoredSmartTrainingConfirmation)
        return false;

    const auto id = Fold(observation.screenId);
    if (id == "career_main")
    {
        m_restoredSmartTrainingConfirmation = false;
        m_pendingTraining.reset();
        m_pendingTrainingBaseline.reset();
        m_pendingTrainingTurnsToGoal.reset();
        if (m_config.traineeId)
        {
            SmartTrainingConfirmationStore::Clear(
                m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial());
        }
        context.Log("INFO",
            "Career Main is visible after resuming; the old confirmation guard was reset without counting a training result. The next action will use fresh observations.");
        return true;
    }
    return false;
}

void CareerFlowCoordinator::SetConfirmedObservation(const CareerDomain::CareerObservation& observation)
{
    if (observation.screenKind == CareerDomain::CareerScreenKind::Unknown) return;
    m_state.careerStarted = true;
    m_transitionContext = CareerTransitionPolicy::FromObservation(observation.screenId);
    m_nextConfirmedObservation = observation;
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

    if (!m_persistenceLoaded)
    {
        m_persistenceLoaded = true;
        if (m_config.continueExistingCareer && m_config.traineeId
            && m_strategy->RequiresTrainingCandidateScan() && !m_config.checkpointDirectory.empty()
            && !context.GetDeviceSerial().empty())
        {
            try
            {
                if (auto record = SmartTrainingConfirmationStore::Load(
                        m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial()))
                {
                    m_restoredSmartTrainingConfirmation = true;
                    m_pendingTraining = CareerDomain::ActionIntent::MakeTraining(
                        record->trainingType, "Restored smart training confirmation guard.");
                    m_pendingTrainingBaseline = m_state.confirmedTurnActions;
                }
            }
            catch (const std::exception& error)
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = std::string("Smart training confirmation guard could not be read: ") + error.what();
                return result;
            }
        }
    }
    auto confirmedObservation = std::move(m_nextConfirmedObservation);
    m_nextConfirmedObservation.reset();
    if (!confirmedObservation)
        confirmedObservation = m_actionExecutor->TakeConfirmedObservation();
    auto observation = confirmedObservation
        ? std::move(*confirmedObservation)
        : m_transitionContext.candidates.empty() ? m_observer->ObserveCareer(context)
            : m_observer->ObserveTransition(context, m_transitionContext);
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
    if (m_restoredSmartTrainingConfirmation && m_pendingTraining
        && (Fold(observation.screenId) == "training_result" || Fold(observation.screenId) == "race_day" || Fold(observation.screenId) == "career_races_ready"
            || (Fold(observation.screenId) == "race_list" && observation.turnsToGoalEvidence.IsReliable()
                && observation.turnsToGoalEvidence.Value() == 0)))
    {
        // A persisted training tap followed by its result or mandatory race boundary
        // proves the non-race action finished, including on process restart.
        // Restore its scenario accounting before applying the destination.
        CareerDomain::CareerObservation trainingSource;
        trainingSource.screenId = "training_selection";
        trainingSource.screenKind = CareerDomain::CareerScreenKind::TrainingSelect;
        m_scenario->OnTurnActionSubmitted(m_state, *m_pendingTraining, trainingSource);
    }
    m_scenario->OnObserve(m_state, observation);
    if (observation.screenKind != CareerDomain::CareerScreenKind::Unknown)
        m_transitionContext = CareerTransitionPolicy::FromObservation(observation.screenId, m_transitionContext);
    SynchronizeObservationPolicy();
    if (observation.screenKind != CareerDomain::CareerScreenKind::Unknown)
        m_unknownObservations = 0;

    if (Fold(observation.screenId) == "career_main")
    {
        const auto careerStatus = FormatCareerStatus(observation, m_state);
        if (careerStatus != m_lastLoggedCareerStatus)
        {
            m_lastLoggedCareerStatus = careerStatus;
            EmitCareerEvent(context, TaskEngine::TaskEventType::RecognitionEvaluated,
                m_state.scenarioId, "career_status", true, observation.score, careerStatus);
        }
    }

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
        if ((observation.screenKind == CareerDomain::CareerScreenKind::MainTurn
                && intent.kind != CareerDomain::ActionIntentKind::DoNothing)
            || (intent.kind == CareerDomain::ActionIntentKind::Training
                && observation.screenKind == CareerDomain::CareerScreenKind::TrainingSelect)
            || Fold(observation.screenId) == "race_day")
        {
            m_state.raceReplayFlowCompleted = false;
            m_state.raceStrategyConfigured = false;
            SynchronizeObservationPolicy();
        }
        if (intent.kind == CareerDomain::ActionIntentKind::Training
            && observation.screenKind == CareerDomain::CareerScreenKind::TrainingSelect)
        {
            m_pendingTraining = intent;
            m_pendingTrainingBaseline = m_state.confirmedTurnActions;
            m_pendingTrainingTurnsToGoal = m_state.turnsToGoal.value;
            if (m_strategy->RequiresTrainingCandidateScan() && m_config.traineeId
                && !m_config.checkpointDirectory.empty() && !context.GetDeviceSerial().empty()
                && !SmartTrainingConfirmationStore::Save(m_config.checkpointDirectory,
                    *m_config.traineeId, context.GetDeviceSerial(), intent.trainingType, m_state.turnIndex))
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Smart training confirmation guard could not be saved before input.";
                return false;
            }
        }
        m_observer->ClearMainTextCache();
        m_state.lastIntent = intent;
        m_actionExecutor->SetTransitionContext(m_transitionContext);
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
            && m_trainingFlow && m_trainingFlow->HasPendingConfirmedTap())
        {
            if (!m_trainingFlow->MarkTrainingTapSent())
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "The confirmed Ura training tap could not be committed to its scan session.";
                return false;
            }
        }
        if (IsCommittedTurnAction(intent, observation))
        {
            m_scenario->OnTurnActionSubmitted(m_state, intent, observation);
            if (intent.kind == CareerDomain::ActionIntentKind::Race
                && observation.screenKind == CareerDomain::CareerScreenKind::MainTurn)
                m_raceTurnSubmitted = true;
        }
        const auto outcome = m_actionExecutor->TakeLastActionOutcome();
        if (outcome)
        {
            const auto foldedOutcome = Fold(*outcome);
            if (foldedOutcome == "race.replay.completed")
            {
                m_scenario->OnRaceReplayCompleted(m_state);
                m_state.raceReplayFlowCompleted = true;
                if (m_observer)
                    m_observer->SetRaceReplayFlowCompleted(true);
            }
            else if (foldedOutcome == "race.retry.declined")
            {
                m_state.raceRetryDeclined = true;
            }
            else if (foldedOutcome == "race.retry.accepted")
            {
                m_state.raceRetryDeclined = false;
            }
        }
        if (Fold(observation.screenId) == "race_runner")
        {
            m_scenario->OnRaceStrategyConfigured(m_state);
            m_state.raceStrategyConfigured = true;
        }
        SynchronizeObservationPolicy();
        if (!m_actionExecutor->ConfirmCompletionFromObservation(intent, observation, context))
        {
            result.status = context.IsCancelled() ? CareerRunStatus::Cancelled : CareerRunStatus::Failed;
            if (result.status == CareerRunStatus::Failed)
                result.failureReason = "Career action produced no resource-confirmed screen transition: " + observation.screenId;
            return false;
        }
        m_nextConfirmedObservation = m_actionExecutor->TakeConfirmedObservation();
        if (auto handoff = m_actionExecutor->TakeActionHandoff())
        {
            m_transitionContext = std::move(handoff->transition);
            if (handoff->observation) m_nextConfirmedObservation = std::move(handoff->observation);
        }
        else
        {
            m_transitionContext = CareerTransitionPolicy::AfterAction(m_transitionContext,
                observation.screenId, TransitionSemantic(intent, observation), outcome.value_or(""));
        }
        const auto destination = m_nextConfirmedObservation
            ? m_nextConfirmedObservation->screenId : std::string("recognized transition");
        const auto transitionScore = m_nextConfirmedObservation
            ? m_nextConfirmedObservation->score : observation.score;
        EmitCareerEvent(context, TaskEngine::TaskEventType::TransitionVerified,
            m_state.scenarioId, actionTask, true, transitionScore,
            observation.screenId + " -> " + destination);
        if (IsCommittedTurnAction(intent, observation))
        {
            SynchronizeObservationPolicy();
            if (intent.kind == CareerDomain::ActionIntentKind::Rest
                || intent.kind == CareerDomain::ActionIntentKind::Infirmary
                || intent.kind == CareerDomain::ActionIntentKind::Recreation
                || intent.kind == CareerDomain::ActionIntentKind::Race)
            {
                if (m_strategy) m_strategy->ClearTrainingCandidateEvidence();
                if (m_trainingFlow) m_trainingFlow->Reset();
            }
        }
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
            if (m_actionExecutor)
                m_actionExecutor->ClearLearnedSkills(context.GetDeviceSerial());
            if (m_config.traineeId)
                SmartTrainingConfirmationStore::Clear(
                    m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial());
            result.status = CareerRunStatus::Completed;
            result.totalTurnsExecuted = m_state.turnIndex;
            return result;
        }
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Career returned to Home before a terminal settlement screen was confirmed.";
        return result;
    }
    if (Fold(observation.screenId) == "home_unselected")
    {
        if (!m_terminalScreenSeen)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "Career returned to an unselected Home tab before a terminal settlement screen was confirmed.";
            return result;
        }
        if (!perform(CareerDomain::ActionIntent::MakeAdvance(
                "Select the resource-confirmed Home tab after terminal settlement."))) return result;
        return finishRunning();
    }
    if (IsTerminalScreen(observation.screenId))
        m_terminalScreenSeen = true;

    TryReconcileResumedTrainingConfirmation(observation, context);
    if (m_restoredSmartTrainingConfirmation && Fold(observation.screenId) == "training_selection")
    {
        result.status = CareerRunStatus::Failed;
        result.failureReason = "Smart training confirmation guard detected while on training selection; pause automation to avoid double-tapping.";
        return result;
    }
    CheckAndConfirmTrainingConsumed(observation, context);

    switch (observation.screenKind)
    {
    case CareerDomain::CareerScreenKind::MainTurn:
    {
        m_pendingRaceIntent.reset();
        m_raceTurnSubmitted = false;
        if (m_scenario->IsGoalCompletionProbeArmed(m_state)
            && (m_state.lastIntent.kind == CareerDomain::ActionIntentKind::Race || m_state.raceReplayFlowCompleted))
        {
            context.Log("INFO", "Waiting at Career Main for post-race event or goal completion banner to settle.");
            context.Sleep(250);
            return finishRunning();
        }
        SynchronizeObservationPolicy();
        const bool candidateScanStrategy = m_strategy
            && m_strategy->RequiresTrainingCandidateScan();
        if (candidateScanStrategy && !m_trainingFlow)
        {
            result.status = CareerRunStatus::Failed;
            result.failureReason = "The selected strategy requires a registered scenario training flow.";
            return result;
        }
        m_scenario->OnTurnStart(m_state);
        if (candidateScanStrategy && !m_trainingFlow->HasPendingOutcome()
            && !m_trainingFlow->HasCandidatesForCurrentTurn(m_state))
        {
            m_strategy->ClearTrainingCandidateEvidence();
            m_trainingFlow->Reset();
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
        if (intent.kind == CareerDomain::ActionIntentKind::DoNothing)
        {
            context.Sleep(250);
            return finishRunning();
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
        context.Log("INFO", "Career decision: energy="
            + (m_state.energy.IsReliable() ? std::to_string(m_state.energy.Value()) + "%" : std::string("unknown"))
            + ", confidence=" + std::to_string(m_state.energy.confidence) + "; " + intent.reason);
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
        return finishRunning();
    }
    case CareerDomain::CareerScreenKind::TrainingResult:
    {
        // CheckAndConfirmTrainingConsumed owns all ratio/scanner commits.
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
            SynchronizeObservationPolicy();
            context.Log("INFO", "Applied verified result for race " + verified->raceId
                + (application.retryPending ? "; scenario requests a permitted retry." : "."));
        }
        if (!m_pendingRaceIntent)
        {
            std::string target;
            if (!m_state.observedGoalKind.empty())
                target = m_state.observedGoalKind;
            else if (Fold(observation.screenId) == "race_day")
                target = "race";
            else if (Fold(observation.screenId) == "race_list")
                target = "fans";
            m_pendingRaceIntent = CareerDomain::ActionIntent::MakeRace(
                target, m_config.normalLineupStrategy,
                "Resume the recognized resource-defined race flow.");
        }
        const auto raceScreenId = Fold(observation.screenId);
        if ((raceScreenId == "race_list" || raceScreenId == "race_details" || raceScreenId == "race_attributes")
            && !m_raceTurnSubmitted)
        {
            if (m_scenario->ShouldDeferRace(m_state))
            {
                result.status = CareerRunStatus::Failed;
                result.failureReason = "Two consecutive races are confirmed or cannot be ruled out; this race can be delayed, but the current race page has no verified route back to a non-race action. Automation paused before entering a third race.";
                return result;
            }
            if (m_scenario->WouldBeThirdRace() && m_scenario->MustRaceNow())
            {
                context.Log("INFO", "Third consecutive race is required by the current career objective; proceeding.");
            }
        }
        auto intent = *m_pendingRaceIntent;
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
