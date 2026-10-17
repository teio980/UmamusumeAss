#include "CareerActionExecutor.hpp"
#include "CareerFrameEvidence.hpp"
#include "Flows/CareerClawMachineFlow.hpp"
#include "Flows/CareerSkillLearningFlow.hpp"
#include "UmaAssistantCore/CareerDomain/CareerCalendar.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cmath>
#include <limits>

namespace UmaAssistant::Career
{

namespace
{

std::string Fold(const std::string& value)
{
    std::string folded;
    folded.reserve(value.size());
    for (unsigned char c : value)
        folded.push_back(static_cast<char>(std::tolower(c)));
    return folded;
}

std::optional<std::string> OnlyAction(const CareerVisualScreenDefinition* screen)
{
    if (!screen || screen->actions.size() != 1)
        return std::nullopt;
    return screen->actions.begin()->second.semanticId;
}

} // namespace

CareerActionExecutor::CareerActionExecutor()
    : CareerActionExecutor(CareerRuntimeConfig{})
{
}

CareerActionExecutor::CareerActionExecutor(std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources))
{
    m_skillLearningFlow = std::make_shared<CareerSkillLearningFlow>(m_config, m_resources);
    if (m_resources)
        m_observer = std::make_shared<CareerScreenObserver>(m_resources);
}

CareerActionExecutor::CareerActionExecutor(
    const CareerRuntimeConfig& config,
    std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources))
    , m_observer(m_resources ? std::make_shared<CareerScreenObserver>(m_resources) : nullptr)
    , m_config(config)
{
    m_skillLearningFlow = std::make_shared<CareerSkillLearningFlow>(m_config, m_resources);
}

CareerActionExecutor::CareerActionExecutor(
    const CareerRuntimeConfig& config,
    TaskEngine::ActionRegistry actionRegistry)
    : m_resources(std::make_shared<CareerVisualResources>(config, std::move(actionRegistry)))
    , m_observer(std::make_shared<CareerScreenObserver>(m_resources))
    , m_config(config)
{
    m_skillLearningFlow = std::make_shared<CareerSkillLearningFlow>(m_config, m_resources);
}

ActionExecutionStatus CareerActionExecutor::Execute(
    const CareerDomain::ActionIntent& intent,
    TaskEngine::ITaskExecutionContext& context)
{
    if (m_lastSourceScreen.empty())
    {
        context.Log("ERROR", "Career action requires a source screen observation; refusing coordinate fallback.");
        return ActionExecutionStatus::Failed;
    }
    CareerDomain::CareerObservation observation;
    observation.screenId = m_lastSourceScreen;
    return ExecuteForObservation(intent, observation, context);
}

ActionExecutionStatus CareerActionExecutor::ExecuteForObservation(
    const CareerDomain::ActionIntent& intent,
    const CareerDomain::CareerObservation& observation,
    TaskEngine::ITaskExecutionContext& context)
{
    m_raceRetryDeclined.reset();
    m_lastActionOutcome.reset();
    m_actionHandoff.reset();
    m_lastSourceScreen = observation.screenId;
    m_transitionContext = CareerTransitionPolicy::FromObservation(observation.screenId, m_transitionContext);
    if (intent.kind == CareerDomain::ActionIntentKind::Race
        && (Fold(observation.screenId) == "career_main" || Fold(observation.screenId) == "race_day"))
        m_raceStrategyConfigured = false;
    if (m_observer) m_observer->RememberObservation(observation);
    TaskEngine::TaskGraphRunOptions observedOptions;
    const auto observedScreen = Fold(observation.screenId);
    const bool turnConfirmation = observedScreen == "recreation_confirmation"
        || observedScreen == "infirmary_confirmation" || observedScreen == "rest_confirmation"
        || observedScreen == "summer_rest_confirmation";
    const bool openingTurnAction = observedScreen == "career_main"
        && (intent.kind == CareerDomain::ActionIntentKind::Rest
            || intent.kind == CareerDomain::ActionIntentKind::Infirmary
            || intent.kind == CareerDomain::ActionIntentKind::Recreation);
    const bool recreationSelection = observedScreen == "recreation_selection"
        && intent.kind == CareerDomain::ActionIntentKind::Recreation;
    observedOptions.traceTurnActionTiming = turnConfirmation || openingTurnAction || recreationSelection;
    const bool reusablePage = observedScreen != "career_main" && observedScreen != "training_selection"
        && observedScreen != "race_runner" && observedScreen != "career_skills"
        && observedScreen.find("claw_machine") == std::string::npos;
    if (observedOptions.traceTurnActionTiming || reusablePage)
    {
        if (const auto frame = CurrentConfirmedCareerFrame(observation, context))
        {
            observedOptions.initialFrame = frame;
            observedOptions.initialFrameValidUntil = observation.visualFrameEvidence->capturedAt + std::chrono::seconds(1);
        }
    }
    if (Fold(observation.screenId) == "race_day"
        && intent.kind == CareerDomain::ActionIntentKind::Race
        && !m_config.normalSkillIds.empty()
        && Fold(m_config.careerMode) != "independent")
    {
        const auto status = m_skillLearningFlow
            ? m_skillLearningFlow->RunBeforeRace(context) : ActionExecutionStatus::Failed;
        if (status != ActionExecutionStatus::Success)
            return status;
        return ExecuteAction(observation.screenId, "race.open_list", context);
    }
    if (Fold(observation.screenId) == "career_skills")
    {
        if (!m_resources) return ActionExecutionStatus::Failed;
        const bool returned = m_skillLearningFlow
            && m_skillLearningFlow->ReturnToRaceDay(context);
        return returned ? ActionExecutionStatus::Success
            : context.IsCancelled() ? ActionExecutionStatus::Cancelled : ActionExecutionStatus::Failed;
    }
    if (observedScreen == "race_runner"
        && intent.kind == CareerDomain::ActionIntentKind::Race)
    {
        auto lineup = Fold(intent.tactic);
        if (lineup.empty())
            lineup = Fold(m_config.normalLineupStrategy);
        if (lineup != "front" && lineup != "pace" && lineup != "late" && lineup != "end")
        {
            context.Log("ERROR", "Race intent or normal-mode configuration has no resource-defined lineup action.");
            return ActionExecutionStatus::Failed;
        }
        if (!m_raceStrategyConfigured)
        {
            const auto strategy = ExecuteAction(observation.screenId, "strategy.apply." + lineup, context);
            if (strategy != ActionExecutionStatus::Success) return strategy;
            m_raceStrategyConfigured = true;
        }
        // Prefer View Results on the verified runner page;
        // only its locked state routes through entry.start and playback.
        return ExecuteAction(observation.screenId, "entry.view_results", context);
    }
    if (Fold(observation.screenId).find("claw_machine") != std::string::npos)
    {
        CareerClawMachineFlow clawFlow(m_resources);
        const auto result = clawFlow.Run(context, Fold(observation.screenId) == "claw_machine_result");
        if (result.status != ActionExecutionStatus::Success)
            context.Log("ERROR", "Claw machine flow failed: " + result.error);
        return result.status;
    }
    if (Fold(observation.screenId) == "training_selection"
        && intent.kind == CareerDomain::ActionIntentKind::Training)
    {
        auto type = Fold(intent.trainingType);
        if (type == "intelligence") type = "wit";
        if (!type.empty())
        {
            const std::string raisedTask = "training_selection_" + type + "_raised_probe";
            const auto* normalTask = m_resources
                ? m_resources->FindExecutionTask("training_selection_training_" + type) : nullptr;
            const auto selectedMarker = m_resources
                ? m_resources->MatchExecutionTask("training_selection_selected_chevron", context)
                : TaskEngine::TaskMatchResult{};
            // A broad raised-icon search can match background in another card.
            // Only take the single-click path when the selected chevron is in
            // the target column; otherwise use the normal select/raised chain.
            const bool targetSelected = normalTask && normalTask->roi && normalTask->roi->size() >= 4
                && selectedMarker.found
                && selectedMarker.CenterX() >= (*normalTask->roi)[0]
                && selectedMarker.CenterX() < (*normalTask->roi)[0] + (*normalTask->roi)[2];
            if (targetSelected && m_resources->MatchExecutionTask(raisedTask, context).found)
            {
                return ExecuteTask(observation.screenId, "training." + type, raisedTask, context);
            }
        }
    }
    const auto semantic = ResolveSemanticAction(intent, observation);
    if (!semantic)
    {
        context.Log("ERROR", "No resource-defined career action for screen '" + observation.screenId
            + "' and intent '" + intent.reason + "'.");
        return context.IsCancelled() ? ActionExecutionStatus::Cancelled : ActionExecutionStatus::Failed;
    }
    return ExecuteAction(observation.screenId, *semantic, context, observedOptions);
}

ActionExecutionStatus CareerActionExecutor::ExecuteTask(
    const std::string& screenId,
    const std::string& semanticId,
    const std::string& taskName,
    TaskEngine::ITaskExecutionContext& context,
    const TaskEngine::TaskGraphRunOptions& options)
{
    if (context.IsCancelled())
        return ActionExecutionStatus::Cancelled;
    if (!m_resources || !m_resources->IsReady())
    {
        context.Log("ERROR", "Career visual resources are unavailable; refusing to tap.");
        return ActionExecutionStatus::Failed;
    }

    context.Log("INFO", "Running profile action " + screenId + "." + semanticId
        + " (task " + taskName + ").");
    auto runOptions = options;
    const auto& definition = m_resources->GetExecutionDefinition();
    for (const auto& [name, task] : definition.tasks)
    {
        if (task.postDelay != 650 && task.postDelay != 700) continue;
        const auto next = std::find_if(task.next.begin(), task.next.end(), [&](const auto& candidate) {
            return definition.TryGetTask(candidate) != nullptr;
        });
        const auto* successor = next == task.next.end() ? nullptr : definition.TryGetTask(*next);
        // The outer Career observer confirms terminal actions. Inside a graph,
        // only a visual successor can replace the delay; unguarded coordinate
        // sequences still need their authored pacing.
        const bool visualSuccessor = successor
            && ((successor->templatePath && !successor->templatePath->empty())
                || (successor->targetText && !successor->targetText->empty()));
        if (!successor || visualSuccessor)
            runOptions.postDelayOverrides.try_emplace(name, 0);
    }
    const auto result = m_resources->RunTask(taskName, context, runOptions);
    if (context.IsCancelled() || result.status == TaskEngine::TaskGraphStatus::Cancelled)
        return ActionExecutionStatus::Cancelled;
    if (result.status == TaskEngine::TaskGraphStatus::Success
        || result.status == TaskEngine::TaskGraphStatus::Completed)
    {
        std::string outcomeValue;
        if (const auto* finalTask = m_resources->FindExecutionTask(result.finalTask);
            finalTask && finalTask->outcome)
        {
            m_lastActionOutcome = *finalTask->outcome;
            outcomeValue = *finalTask->outcome;
            const auto outcome = Fold(*finalTask->outcome);
            if (outcome == "race.retry.declined")
                m_raceRetryDeclined = true;
            else if (outcome == "race.retry.accepted")
            {
                m_raceRetryDeclined = false;
                m_raceStrategyConfigured = false;
            }
        }
        m_transitionContext = CareerTransitionPolicy::AfterAction(m_transitionContext, screenId, semanticId, outcomeValue);
        m_actionHandoff = CareerActionHandoff{m_transitionContext, {}, {}};
        // This terminal task observed the destination after the click. A
        // ClickSelf frame predates its input and must never seed the next page.
        if (Fold(outcomeValue) == "training.selection.confirmed")
            m_actionHandoff->frame = context.GetCurrentFrame();
        m_handoffCreated = std::chrono::steady_clock::now();
        return ActionExecutionStatus::Success;
    }
    context.Log("ERROR", "Career profile action failed: " + result.errorMessage);
    return ActionExecutionStatus::Failed;
}

ActionExecutionStatus CareerActionExecutor::ExecuteAction(
    const std::string& screenId,
    const std::string& semanticId,
    TaskEngine::ITaskExecutionContext& context,
    const TaskEngine::TaskGraphRunOptions& options)
{
    if (context.IsCancelled())
        return ActionExecutionStatus::Cancelled;
    if (!m_resources || !m_resources->IsReady())
    {
        context.Log("ERROR", "Career visual resources are unavailable; refusing to tap.");
        return ActionExecutionStatus::Failed;
    }
    const auto* action = m_resources->FindAction(screenId, semanticId);
    if (!action || !action->valid)
    {
        context.Log("ERROR", "Career visual action is unavailable: " + screenId + "." + semanticId);
        return ActionExecutionStatus::Failed;
    }

    return ExecuteTask(screenId, semanticId, action->taskName, context, options);
}

bool CareerActionExecutor::ConfirmCompletion(
    const CareerDomain::ActionIntent& intent,
    TaskEngine::ITaskExecutionContext& context)
{
    CareerDomain::CareerObservation source;
    source.screenId = m_lastSourceScreen;
    return ConfirmCompletionFromObservation(intent, source, context);
}

bool CareerActionExecutor::ConfirmCompletionFromObservation(
    const CareerDomain::ActionIntent& intent,
    const CareerDomain::CareerObservation& observation,
    TaskEngine::ITaskExecutionContext& context)
{
    m_confirmedObservation.reset();
    if (!m_observer || observation.screenId.empty() || observation.screenId == "unknown")
    {
        context.Log("ERROR", "Cannot confirm a career action without a recognized source screen.");
        return false;
    }

    constexpr int pollMs = 250;
    m_observer->RememberObservation(observation);
    const auto transition = m_actionHandoff && m_actionHandoff->transition.sourceScreenId == Fold(observation.screenId)
        ? m_actionHandoff->transition
        : CareerTransitionPolicy::AfterAction(CareerTransitionPolicy::FromObservation(observation.screenId, m_transitionContext), observation.screenId,
            ResolveSemanticAction(intent, observation).value_or(""));
    if (transition.phase == CareerTransitionPhase::Unknown)
    {
        context.Log("ERROR", "No local transition contract for " + observation.screenId + "." + transition.semanticActionId);
        return false;
    }
    bool const raceTransition = transition.retryUnchangedRaceEntry;
    int const timeoutMs = transition.timeoutMs;
    int const maxPolls = timeoutMs / pollMs;
    auto seed = m_actionHandoff && std::chrono::steady_clock::now() - m_handoffCreated < std::chrono::seconds(1)
        ? m_actionHandoff->frame : std::shared_ptr<const TaskEngine::TaskFrameSnapshot>{};
    bool entryRetried = false;
    bool unchangedRewardsSettled = false;
    int observations = 0;
    std::string lastObservedId = "unknown";
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    const auto source = Fold(observation.screenId);
    const bool openingTurnDialog = (source == "career_main"
        && (intent.kind == CareerDomain::ActionIntentKind::Rest
            || intent.kind == CareerDomain::ActionIntentKind::Infirmary
            || intent.kind == CareerDomain::ActionIntentKind::Recreation))
        || source == "recreation_selection";
    auto dialogProbe = transition;
    if (openingTurnDialog)
        std::erase(dialogProbe.candidates, source);
    for (int poll = 0; poll < maxPolls && !context.IsCancelled()
        && std::chrono::steady_clock::now() < deadline; ++poll)
    {
        if (poll > 0) context.Sleep(pollMs);
        if (context.IsCancelled())
            return false;
        // First look only for the expected dialog/outing selection. Reading
        // the old Main's date, goal and energy cannot confirm this action.
        // Preserve the original source-page checks on subsequent polls.
        const auto current = m_observer->ObserveTransition(context,
            openingTurnDialog && poll == 0 && !dialogProbe.candidates.empty() ? dialogProbe : transition,
            std::move(seed));
        seed.reset();
        ++observations;
        lastObservedId = current.screenId;
        if (current.screenId == "resource_error" || current.screenId == "capture_cancelled")
            return false;
        if (current.screenId == "capture_failed") continue;
        if (current.screenId != "unknown" && Fold(current.screenId) != Fold(observation.screenId))
        {
            context.Log("INFO", "Career action transition observed: " + observation.screenId
                + " -> " + current.screenId);
            m_confirmedObservation = current;
            if (m_actionHandoff) m_actionHandoff->observation = current;
            return true;
        }
        if (current.screenId == observation.screenId)
        {
            const auto& beforePage = observation.pageStateEvidence;
            const auto& afterPage = current.pageStateEvidence;
            const bool pageStateChanged = beforePage && afterPage
                && beforePage->frameWidth == afterPage->frameWidth && beforePage->frameHeight == afterPage->frameHeight
                && beforePage->sessionGeneration == afterPage->sessionGeneration
                && beforePage->geometryGeneration == afterPage->geometryGeneration
                && beforePage->signature != afterPage->signature;
            // Rewards contains several pages with the same header and Next
            // button. Let the coordinator run the matched Next action again;
            // neither a changed title nor a changed page signature is needed.
            if (source == "rewards" && transition.semanticActionId == "rewards.next")
            {
                // Identical reward pages can legitimately follow each other.
                // If capture is fast, retain the old minimum pacing only for
                // this ambiguous case, then verify fresh frames before retry.
                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - m_handoffCreated).count();
                if (!pageStateChanged && !unchangedRewardsSettled && elapsed < 650)
                {
                    unchangedRewardsSettled = true;
                    context.Sleep(static_cast<int>(650 - elapsed));
                    continue;
                }
                context.Log("INFO", "Rewards Next is still available; continuing to advance rewards.");
                m_confirmedObservation = current;
                if (m_actionHandoff) m_actionHandoff->observation = current;
                return true;
            }
            if (raceTransition && !entryRetried && current.consecutiveFramesIdentical)
            {
                // ObserveScreens confirmed identical complete frames. The
                // action graph must also rematch its button before this retry.
                context.Log("INFO", "Race transition is unchanged in two frames; retrying its verified action once.");
                entryRetried = true;
                if (ExecuteForObservation(intent, current, context) != ActionExecutionStatus::Success) return false;
                continue;
            }
            const bool turnChanged = observation.turnIndexEvidence.IsReliable()
                && current.turnIndexEvidence.IsReliable()
                && current.turnIndexEvidence.Value() != observation.turnIndexEvidence.Value();
            const bool eventChanged = observation.eventTitle && current.eventTitle
                && *observation.eventTitle != *current.eventTitle;
            const bool eventIdChanged = observation.eventId && current.eventId
                && *observation.eventId != *current.eventId;
            const bool eventStateChanged = observation.eventStateSignature
                && current.eventStateSignature
                && *observation.eventStateSignature != *current.eventStateSignature;
            if (!raceTransition && (turnChanged || eventChanged || eventIdChanged || eventStateChanged || pageStateChanged))
            {
                context.Log("INFO", "Career action state transition observed within " + current.screenId + ".");
                m_confirmedObservation = current;
                if (m_actionHandoff) m_actionHandoff->observation = current;
                return true;
            }
        }
    }
    context.Log("ERROR", context.IsCancelled()
        ? "Career action transition confirmation was cancelled."
        : "Career action produced no resource-confirmed screen transition after "
            + std::to_string(observations) + " observations (window " + std::to_string(timeoutMs)
            + " ms, source " + observation.screenId + ", last " + lastObservedId + ").");
    return false;
}

std::optional<CareerDomain::CareerObservation> CareerActionExecutor::TakeConfirmedObservation()
{
    auto result = std::move(m_confirmedObservation);
    m_confirmedObservation.reset();
    return result;
}

void CareerActionExecutor::ResetAfterRecovery() noexcept
{
    m_confirmedObservation.reset();
    m_raceRetryDeclined.reset();
    m_lastActionOutcome.reset();
    m_lastSourceScreen.clear();
    m_actionHandoff.reset();
    m_transitionContext = {};
    m_raceStrategyConfigured = false;
    if (m_observer)
        m_observer->ResetForRecovery();
}

std::optional<CareerActionHandoff> CareerActionExecutor::TakeActionHandoff()
{
    auto result = std::move(m_actionHandoff);
    m_actionHandoff.reset();
    return result;
}

std::optional<bool> CareerActionExecutor::TakeRaceRetryDeclined()
{
    auto result = m_raceRetryDeclined;
    m_raceRetryDeclined.reset();
    return result;
}

void CareerActionExecutor::SetGoalObservationPolicy(bool armed, bool replayCompleted) noexcept
{
    if (!m_observer) return;
    m_observer->SetGoalCompletionProbeArmed(armed);
    m_observer->SetRaceReplayFlowCompleted(replayCompleted);
}

void CareerActionExecutor::SetRaceRunnerResultExpected(bool expected) noexcept
{
    if (m_observer) m_observer->SetRaceRunnerResultExpected(expected);
}

std::optional<std::string> CareerActionExecutor::TakeLastActionOutcome()
{
    auto result = m_lastActionOutcome;
    m_lastActionOutcome.reset();
    return result;
}

void CareerActionExecutor::ClearLearnedSkills(const std::string& deviceKey)
{
    if (m_skillLearningFlow)
        m_skillLearningFlow->ClearLearnedSkills(deviceKey);
}

std::optional<CareerDomain::CareerRaceResultObservation> CareerActionExecutor::MatchRaceResult(
    const CareerDomain::CareerRaceResultExpectation& expectation,
    TaskEngine::ITaskExecutionContext& context)
{
    if (!m_resources || !m_resources->IsReady() || expectation.raceId.empty()
        || expectation.runtimeFrameCatalogId.empty() || expectation.expectedPlacement <= 0
        || !std::isfinite(expectation.confidence) || expectation.confidence <= 0.0
        || expectation.confidence > 1.0)
    {
        context.Log("ERROR", "Race result expectation is incomplete; refusing to advance without a verified outcome.");
        return std::nullopt;
    }

    const auto capture = m_resources->ResolveCollectionAsset(expectation.runtimeFrameCatalogId);
    const auto* region = m_resources->FindRegion("career.race.result.runtime_frame_match");
    if (!capture || !region || !region->is_object() || !region->contains("threshold")
        || !(*region)["threshold"].is_number() || !region->contains("metadata")
        || !(*region)["metadata"].is_object())
    {
        context.Log("ERROR", "Race result capture or authored runtime-frame match policy is missing.");
        return std::nullopt;
    }

    const auto& metadata = (*region)["metadata"];
    if (!metadata.contains("maximumConfidence") || !metadata["maximumConfidence"].is_number()
        || !metadata.contains("waitTimeoutMs") || !metadata["waitTimeoutMs"].is_number_integer()
        || !metadata.contains("pollIntervalMs") || !metadata["pollIntervalMs"].is_number_integer())
    {
        context.Log("ERROR", "Race result match policy has incomplete timeout/confidence metadata.");
        return std::nullopt;
    }
    const double minimumConfidence = (*region)["threshold"].get<double>();
    const double maximumConfidence = metadata["maximumConfidence"].get<double>();
    const int timeoutMs = metadata["waitTimeoutMs"].get<int>();
    const int pollIntervalMs = metadata["pollIntervalMs"].get<int>();
    if (!std::isfinite(minimumConfidence) || !std::isfinite(maximumConfidence)
        || minimumConfidence < 0.0 || maximumConfidence > 1.0
        || minimumConfidence > maximumConfidence || timeoutMs <= 0 || pollIntervalMs <= 0)
    {
        context.Log("ERROR", "Race result match policy values are invalid.");
        return std::nullopt;
    }
    const double threshold = std::clamp(expectation.confidence, minimumConfidence, maximumConfidence);

    std::optional<TaskEngine::TaskRoi> roi;
    if (region->contains("roi"))
    {
        const auto& bounds = (*region)["roi"];
        if (!bounds.is_array() || bounds.size() < 4
            || !std::all_of(bounds.begin(), bounds.begin() + 4,
                [](const auto& value) { return value.is_number_integer(); }))
        {
            context.Log("ERROR", "Race result match region has a malformed ROI.");
            return std::nullopt;
        }
        const double x = bounds[0].get<double>();
        const double y = bounds[1].get<double>();
        const double width = bounds[2].get<double>();
        const double height = bounds[3].get<double>();
        if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(width)
            || !std::isfinite(height) || x < 0 || y < 0 || width <= 0 || height <= 0
            || x + width > 900 || y + height > 1600)
        {
            context.Log("ERROR", "Race result match region ROI is outside the authored reference frame.");
            return std::nullopt;
        }
        roi = TaskEngine::TaskRoi{
            static_cast<int>(x), static_cast<int>(y),
            static_cast<int>(width), static_cast<int>(height)};
    }

    const int maximumPolls = (std::max)(1, timeoutMs / pollIntervalMs + 1);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    for (int poll = 0; poll < maximumPolls
        && std::chrono::steady_clock::now() <= deadline && !context.IsCancelled(); ++poll)
    {
        if (context.CaptureScreen())
        {
            const auto frame = context.GetCurrentFrame();
            if (frame && frame->width > 0 && frame->height > 0)
            {
                const auto match = context.MatchTemplateAdvanced(
                    capture->path, threshold, roi, "MatchTemplate", 900, 1600, {});
                const auto current = context.GetCurrentFrame();
                const bool sameFrame = current && current.get() == frame.get()
                    && current->frameId == frame->frameId
                    && match.frameId == frame->frameId
                    && match.sessionGeneration == frame->sessionGeneration
                    && match.geometryGeneration == frame->geometryGeneration;
                if (sameFrame && match.found && std::isfinite(match.score)
                    && match.score >= threshold && match.score <= 1.0)
                {
                    const double confidence = std::min(expectation.confidence, match.score);
                    context.Log("INFO", "Verified race result " + expectation.raceId
                        + " against its packaged runtime frame.");
                    return CareerDomain::CareerRaceResultObservation{
                        expectation.raceId, expectation.expectedPlacement, confidence};
                }
            }
        }
        if (poll + 1 < maximumPolls && std::chrono::steady_clock::now() < deadline)
            context.Sleep(pollIntervalMs);
    }

    context.Log("ERROR", context.IsCancelled()
        ? "Race result runtime-frame matching was canceled."
        : "The expected packaged race-result frame did not match; placement remains unknown.");
    return std::nullopt;
}

std::optional<std::string> CareerActionExecutor::ResolveSemanticAction(
    const CareerDomain::ActionIntent& intent,
    const CareerDomain::CareerObservation& observation) const
{
    const auto screenId = Fold(observation.screenId);
    const auto* screen = m_resources ? m_resources->FindScreen(observation.screenId) : nullptr;
    const auto choose = [&](const std::string& semantic) -> std::optional<std::string> {
        return screen && screen->actions.contains(Fold(semantic))
            ? std::optional<std::string>(semantic) : std::nullopt;
    };

    using Kind = CareerDomain::ActionIntentKind;
    switch (intent.kind)
    {
    case Kind::Training:
    {
        std::string type = Fold(intent.trainingType);
        if (type == "intelligence") type = "wit";
        if (screenId == "career_main")
            return choose("action.training");
        if (screenId == "training_selection")
            return choose("training." + type);
        return std::nullopt;
    }
    case Kind::Rest:
        if (screenId == "career_main")
        {
            if (observation.turnPositionText
                && CareerDomain::CareerCalendar::IsSummerCamp(*observation.turnPositionText))
                return choose("action.summer_rest");
            return choose("action.rest");
        }
        if (screenId == "summer_rest_confirmation" || screenId == "rest_confirmation")
            return choose("rest.confirm");
        return std::nullopt;
    case Kind::Infirmary:
        if (screenId == "career_main")
            return choose("action.infirmary");
        if (screenId == "infirmary_confirmation")
            return choose("infirmary.confirm");
        return std::nullopt;
    case Kind::Recreation:
        if (screenId == "career_main")
        {
            if (observation.turnPositionText
                && CareerDomain::CareerCalendar::IsSummerCamp(*observation.turnPositionText))
                return choose("action.summer_rest");
            return choose("action.recreation");
        }
        if (screenId == "summer_rest_confirmation")
            return choose("rest.confirm");
        if (screenId == "recreation_selection")
            return choose("recreation.trainee");
        if (screenId == "recreation_confirmation")
            return choose("recreation.confirm");
        return std::nullopt;
    case Kind::SelectEventOption:
        if (observation.eventActionId) return choose(*observation.eventActionId);
        if (screenId == "event_choice")
            return intent.optionIndex == 1 ? choose("event.choice_first") : std::nullopt;
        return OnlyAction(screen);
    case Kind::Race:
    {
        if (screenId == "career_main")
        {
            if (Fold(intent.raceTarget) == "finale")
                return choose("action.finale_races");
            return choose("action.races");
        }
        if (screenId == "career_races_ready")
            return choose("action.races");
        if (screenId == "race_day")
            return intent.skillIds.empty() ? choose("race.open_list") : choose("skills.open");
        if (screenId == "race_list")
        {
            const auto target = Fold(intent.raceTarget);
            if (target == "grade_race_count")
                return choose("race.fans_entry");
            if (auto rec = choose("race.recommended_entry"))
                return rec;
            return choose("race.fans_entry");
        }
        if (screenId == "race_streak_warning")
            return choose(intent.customActionName);
        if (screenId == "race_retry_dialog")
            return choose(m_config.retryFailedRaceWithAlarmClock
                ? "retry_with_alarm_clock" : "cancel");
        if (screenId == "race_runner")
            return choose("entry.view_results");
        if (screenId == "race_details")
            return choose("race.confirm");
        if (screenId == "race_result" || screenId == "race_runner_result")
            return choose("result.next");
        if (screenId == "career_complete" || screenId == "career_complete_close")
            return choose("career.to_home") ? choose("career.to_home") : choose("career.close");
        return OnlyAction(screen);
    }
    case Kind::Skill:
        // Skill navigation is owned by the dedicated learning flow.
        return std::nullopt;
    case Kind::Custom:
    case Kind::DoNothing:
    default:
        if (screenId == "career_main")
            return std::nullopt;
        if (screenId == "training_selection" && intent.customActionName == "training.back")
            return choose("training.back");
        return OnlyAction(screen);
    }
}

} // namespace UmaAssistant::Career
