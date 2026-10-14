#include "TransitionValidator.hpp"
#include "RecognitionPipeline.hpp"
#include <algorithm>
#include <chrono>
#include <limits>
#include <string>

namespace UmaAssistant::TaskEngine
{

namespace
{

struct RetainedMatchGuard
{
    ITaskExecutionContext& context;
    bool retained{false};
    ~RetainedMatchGuard()
    {
        if (retained)
            context.ReleaseRetainedMatch();
    }
};

std::optional<TaskRoi> ResolveRoi(const std::optional<std::vector<int>>& value)
{
    if (!value || value->size() < 4)
        return std::nullopt;
    return TaskRoi{(*value)[0], (*value)[1], (*value)[2], (*value)[3]};
}

bool CheckTransitionTemplates(
    const HachimiPipelineTask& task,
    ITaskExecutionContext& context,
    std::string& confirmedTemplate)
{
    const auto defaultRoi = ResolveRoi(task.transitionRoi);
    for (std::size_t i = 0; i < task.transitionTemplates.size(); ++i)
    {
        const auto& path = task.transitionTemplates[i];
        auto roi = i < task.transitionRois.size() && task.transitionRois[i]
            ? ResolveRoi(task.transitionRois[i])
            : defaultRoi;
        auto match = context.MatchTemplateAdvanced(
            path,
            task.transitionThreshold,
            roi,
            "MatchTemplate",
            task.referenceWidth,
            task.referenceHeight,
            {});
        if (match.found)
        {
            confirmedTemplate = path;
            return true;
        }
    }
    return false;
}

bool CheckCurrentTemplate(
    const HachimiPipelineTask& task,
    ITaskExecutionContext& context,
    TaskMatchResult& match)
{
    if (!task.templatePath || task.templatePath->empty())
        return false;
    match = context.MatchTemplateAdvanced(
        *task.templatePath,
        task.templateThreshold,
        ResolveRoi(task.roi),
        task.algorithm,
        task.referenceWidth,
        task.referenceHeight,
        task.scaleCandidates);
    return match.found;
}

std::chrono::steady_clock::time_point DeadlineAfter(int milliseconds)
{
    return std::chrono::steady_clock::now() +
        std::chrono::milliseconds((std::max)(0, milliseconds));
}

int PollInterval(const HachimiPipelineTask& task)
{
    return (std::max)(1, task.transitionPollIntervalMilliseconds);
}

std::pair<int, int> ResolveTapPoint(const HachimiPipelineTask& task, const TaskMatchResult& match)
{
    if (task.clickVerification && task.clickAnchor && task.clickAnchor->size() >= 2)
        return {(*task.clickAnchor)[0], (*task.clickAnchor)[1]};
    int x = match.CenterX();
    int y = match.CenterY();
    if (CaseInsensitiveEqual{}(task.action, "ClickText"))
    {
        int left = match.x;
        int top = match.y;
        int right = match.x + (std::max)(0, match.width);
        int bottom = match.y + (std::max)(0, match.height);
        if (task.rowExpansion && task.rowExpansion->size() >= 4)
        {
            const auto& expansion = *task.rowExpansion;
            left = (std::max)(0, left - (std::max)(0, expansion[0]));
            top = (std::max)(0, top - (std::max)(0, expansion[1]));
            right = (std::max)(left, right + (std::max)(0, expansion[2]));
            bottom = (std::max)(top, bottom + (std::max)(0, expansion[3]));
        }
        x = left + (right - left) / 2;
        y = top + (bottom - top) / 2;
        if (task.clickAnchor && !task.clickAnchor->empty())
        {
            x = (*task.clickAnchor)[0];
            if (task.clickAnchor->size() >= 2)
                y = (*task.clickAnchor)[1];
        }
        else if (task.clickOffset && task.clickOffset->size() >= 2)
        {
            x += (*task.clickOffset)[0];
            y += (*task.clickOffset)[1];
        }
    }
    else if (task.clickOffset && task.clickOffset->size() >= 2)
    {
        x += (*task.clickOffset)[0];
        y += (*task.clickOffset)[1];
    }
    return {x, y};
}

} // namespace

bool TransitionValidator::ProbeHsvState(
    const HachimiClickVerification& config,
    int anchorX,
    int anchorY,
    ITaskExecutionContext& context)
{
    int px = anchorX;
    int py = anchorY;
    if (config.probeOffset && config.probeOffset->size() >= 2)
    {
        px += (*config.probeOffset)[0];
        py += (*config.probeOffset)[1];
    }

    return context.ProbeHsv(
        px, py, config.probeRadius,
        config.hueMin, config.hueMax,
        config.saturationMin, config.saturationMax,
        config.valueMin, config.valueMax,
        config.minimumMatchRatio);
}

bool TransitionValidator::PreCheckClickState(
    const HachimiPipelineTask& task,
    int anchorX,
    int anchorY,
    ITaskExecutionContext& context)
{
    if (!task.clickVerification || !task.clickVerification->preCheck)
        return false;
    context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
    return ProbeHsvState(*task.clickVerification, anchorX, anchorY, context);
}

TransitionResult TransitionValidator::ValidateTransition(
    const HachimiPipelineTask& task,
    const TaskMatchResult& originalMatch,
    ITaskExecutionContext& context,
    bool initialTapPerformed)
{
    TransitionResult result;
    context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);

    // A retry must use the source frame belonging to the first recognition.
    // Keeping that frame leased lets TapMatch validate its session and geometry
    // generations instead of silently rebinding old coordinates to a new frame.
    RetainedMatchGuard retained{context};
    if (!initialTapPerformed && originalMatch.found &&
        (task.clickUntilGone || task.repeatTapUntilTransition ||
         (task.clickVerification && task.clickVerification->maxRetries > 0)))
    {
        const auto [tapX, tapY] = ResolveTapPoint(task, originalMatch);
        int attempts = task.maxClickAttempts > 0 ? (std::clamp)(task.maxClickAttempts, 1, 100) : 1;
        if (task.clickVerification)
            attempts = (std::max)(attempts, task.clickVerification->maxRetries + 1);
        retained.retained = context.RetainMatchForRetry(originalMatch, attempts, 30000, tapX, tapY);
    }
    if (initialTapPerformed)
        result.tapAttemptsMade = 1;

    // HSV retries count the action-phase tap as attempt one. Every later tap is
    // checked; a failed input must stop rather than pretending to verify it.
    if (task.clickVerification)
    {
        const auto& verification = *task.clickVerification;
        const int maxAttempts = 1 + (std::clamp)(verification.maxRetries, 0, 5);
        bool verified = false;
        TaskMatchResult attemptMatch = originalMatch;
        for (int attempt = 0; attempt < maxAttempts && !context.IsCancelled(); ++attempt)
        {
            if (attempt > 0)
            {
                context.Sleep((std::clamp)(verification.retryDelayMs, 0, 10000));
                if (context.IsCancelled())
                    break;
                if (!context.CaptureScreen())
                {
                    result.failureReason = "HSV verification retry could not capture a fresh frame";
                    return result;
                }

                if ((task.templatePath && !task.templatePath->empty()) ||
                    (task.targetText && !task.targetText->empty()))
                {
                    auto retryTask = task;
                    retryTask.maxScrolls = 0;
                    retryTask.alternativeTemplateTasks.clear();
                    HachimiPipelineDefinition retryDefinition;
                    retryDefinition.referenceWidth = task.referenceWidth;
                    retryDefinition.referenceHeight = task.referenceHeight;
                    const auto retryRecognition = RecognitionPipeline::EvaluateTask(
                        retryDefinition, "clickVerificationRetry", retryTask, context);
                    if (!retryRecognition.matched || !retryRecognition.matchResult.found)
                    {
                        result.failureReason = "HSV verification retry could not re-identify the target";
                        return result;
                    }
                    attemptMatch = retryRecognition.matchResult;
                }
                else if (originalMatch.found)
                {
                    result.failureReason = "HSV verification retry has no recognizer for the original match";
                    return result;
                }

                int retryAnchorX = attemptMatch.CenterX();
                int retryAnchorY = attemptMatch.CenterY();
                if (task.clickAnchor && task.clickAnchor->size() >= 2)
                {
                    retryAnchorX = (*task.clickAnchor)[0];
                    retryAnchorY = (*task.clickAnchor)[1];
                }
                if (attemptMatch.found &&
                    ProbeHsvState(verification, retryAnchorX, retryAnchorY, context))
                {
                    result.confirmed = true;
                    result.verifiedByHsv = true;
                    return result;
                }

                // Only a fresh visual match gives us evidence that the retry
                // target still exists and needs another click. Coordinate-only
                // actions have no such evidence; their verification sample is
                // sufficient and should not be duplicated before each retry.
                if (attemptMatch.found)
                {
                    const auto [retryTapX, retryTapY] = ResolveTapPoint(task, attemptMatch);
                    if (context.HasRetainedMatch() && !context.IsMatchRetained(attemptMatch))
                        context.ReleaseRetainedMatch();
                    const bool alreadyRetained = context.HasRetainedMatch();
                    if (!context.RetainMatchForRetry(
                            attemptMatch, maxAttempts - attempt, 30000, retryTapX, retryTapY))
                    {
                        result.failureReason = "HSV verification retry could not retain the fresh match";
                        return result;
                    }
                    if (!alreadyRetained)
                        retained.retained = true;
                }
            }

            if (attempt == 0 && !initialTapPerformed)
            {
                const auto [tapX, tapY] = ResolveTapPoint(task, attemptMatch);
                if (!context.TapMatch(tapX, tapY, attemptMatch))
                {
                    result.failureReason = "Initial HSV verification tap failed";
                    return result;
                }
                ++result.tapAttemptsMade;
            }
            else if (attempt > 0)
            {
                const auto [tapX, tapY] = ResolveTapPoint(task, attemptMatch);
                if (!context.TapMatch(tapX, tapY, attemptMatch))
                {
                    result.failureReason = "HSV verification retry tap failed";
                    return result;
                }
                ++result.tapAttemptsMade;
            }

            context.Sleep((std::max)(0, verification.settleDelayMs));
            const auto deadline = DeadlineAfter(verification.verifyTimeoutMs);
            while (!context.IsCancelled())
            {
                const bool captured = context.CaptureScreen();
                int anchorX = attemptMatch.CenterX();
                int anchorY = attemptMatch.CenterY();
                if (task.clickAnchor && task.clickAnchor->size() >= 2)
                {
                    anchorX = (*task.clickAnchor)[0];
                    anchorY = (*task.clickAnchor)[1];
                }
                if (captured && ProbeHsvState(verification, anchorX, anchorY, context))
                {
                    verified = true;
                    break;
                }

                // Timeout is per attempt, including failed captures. A zero
                // timeout still performs exactly one verification sample.
                if (verification.verifyTimeoutMs <= 0 ||
                    std::chrono::steady_clock::now() >= deadline)
                    break;
                context.Sleep((std::max)(1, verification.verifyPollIntervalMs));
            }

            if (verified)
                break;
        }

        if (verified)
        {
            result.confirmed = true;
            result.verifiedByHsv = true;
        }
        else
        {
            result.failureReason = context.IsCancelled()
                ? "HSV click verification canceled"
                : "HSV click verification failed to confirm expected state";
        }
        return result;
    }

    const bool needsGone = task.clickUntilGone && task.templatePath && !task.templatePath->empty();
    const bool needsTemplates = !task.transitionTemplates.empty();
    const bool needsRepeatedTaps = task.repeatTapUntilTransition && needsTemplates;
    if (!needsGone && !needsTemplates)
    {
        result.confirmed = true;
        return result;
    }

    const int timeoutMs = task.transitionTimeoutMilliseconds > 0
        ? task.transitionTimeoutMilliseconds : 2600;
    const int pollMs = PollInterval(task);
    if (needsRepeatedTaps)
    {
        const int limit = (std::clamp)(task.maxClickAttempts, 1, 100);
        result.tapAttemptsMade = initialTapPerformed ? 1 : 0;
        // main gives every post-tap wait and pre-retry wait its own budget.
        // Screenshot and recognition latency must not consume all attempts.
        const auto waitForDestination = [&]() {
            const auto waitDeadline = DeadlineAfter(timeoutMs);
            while (!context.IsCancelled())
            {
                if (!context.CaptureScreen())
                {
                    result.failureReason = "Transition validation could not capture a fresh frame";
                    return false;
                }
                if (context.IsCancelled()) break;
                if (CheckTransitionTemplates(task, context, result.confirmedTemplate))
                {
                    result.confirmed = true;
                    return true;
                }
                if (std::chrono::steady_clock::now() >= waitDeadline) return false;
                context.Sleep(pollMs);
            }
            result.failureReason = "Transition validation canceled";
            return false;
        };
        for (int attempt = 0; attempt < limit && !context.IsCancelled(); ++attempt)
        {
            TaskMatchResult tapMatch = originalMatch;
            bool sendTap = attempt != 0 || !initialTapPerformed;
            if (attempt != 0)
            {
                if (waitForDestination() || !result.failureReason.empty()) return result;
                // Re-identify the original source on the last captured frame.
                // If it disappeared, keep observing without tapping a new page.
                sendTap = CheckCurrentTemplate(task, context, tapMatch);
            }
            if (sendTap)
            {
                if (context.IsCancelled()) break;
                const auto [tapX, tapY] = ResolveTapPoint(task, tapMatch);
                if (!context.TapMatch(tapX, tapY, tapMatch))
                {
                    result.failureReason = "Transition tap failed (attempt="
                        + std::to_string(result.tapAttemptsMade + 1)
                        + ", error=" + std::to_string(context.GetLastErrorCode())
                        + ", frame=" + std::to_string(tapMatch.frameId)
                        + ", point=" + std::to_string(tapX) + "," + std::to_string(tapY) + ")";
                    return result;
                }
                ++result.tapAttemptsMade;
            }
            if (waitForDestination() || !result.failureReason.empty()) return result;
        }
        result.failureReason = context.IsCancelled() ? "Transition validation canceled"
            : "Transition validation timed out after " + std::to_string(result.tapAttemptsMade) + " tap attempt(s)";
        return result;
    }
    const bool mayUseFallback = !needsGone && !needsRepeatedTaps
        && task.fallbackRoi && task.fallbackRoi->size() >= 4;
    const int waitTimeoutMs = needsGone ? 600000 : timeoutMs;
    const auto deadline = DeadlineAfter(mayUseFallback ? (std::min)(timeoutMs, 1000) : waitTimeoutMs);
    const int clickGoneLimit = task.maxClickAttempts > 0
        ? task.maxClickAttempts : (std::numeric_limits<int>::max)();
    int clickAttempts = initialTapPerformed ? 1 : 0;
    result.tapAttemptsMade = initialTapPerformed ? 1 : 0;
    int consecutiveGone = 0;
    const int goneSamplesRequired = (std::max)(2, task.goneConfirmationSamples);
    bool goneConfirmed = false;
    bool firstSample = true;

    while (!context.IsCancelled())
    {
        if (needsGone && clickAttempts == 0)
        {
            const auto [tapX, tapY] = ResolveTapPoint(task, originalMatch);
            if (!context.TapMatch(tapX, tapY, originalMatch))
            {
                result.failureReason = "Initial click-until-gone tap failed";
                return result;
            }
            ++clickAttempts;
            ++result.tapAttemptsMade;
        }

        if (!firstSample)
            context.Sleep(pollMs);
        firstSample = false;
        if (context.IsCancelled())
            break;

        if (!context.CaptureScreen())
        {
            // A failed frame cannot establish that a repeated click is safe.
            // Let the explicit fallback-ROI path try its own fresh observation;
            // otherwise fail rather than retrying later on an uncertain state.
            if (mayUseFallback)
                break;
            if (std::chrono::steady_clock::now() >= deadline)
                break;
            result.failureReason = "Transition validation could not capture a fresh frame";
            return result;
        }

        // A stop/success template has priority over stale action templates in
        // the same frame. clickUntilGone still requires its own gone samples.
        if (needsTemplates && (!needsGone || goneConfirmed) &&
            CheckTransitionTemplates(task, context, result.confirmedTemplate))
        {
            result.confirmed = true;
            return result;
        }

        if (needsGone)
        {
            TaskMatchResult currentMatch;
            if (!CheckCurrentTemplate(task, context, currentMatch))
            {
                ++consecutiveGone;
                if (consecutiveGone >= goneSamplesRequired)
                {
                    goneConfirmed = true;
                    if (!needsTemplates)
                    {
                        result.confirmed = true;
                        return result;
                    }
                }
            }
            else
            {
                consecutiveGone = 0;
                if (!needsRepeatedTaps && clickAttempts < clickGoneLimit)
                {
                    if (!context.TapMatch(currentMatch.CenterX(), currentMatch.CenterY(), currentMatch))
                    {
                        result.failureReason = "Click-until-gone retry tap failed";
                        return result;
                    }
                    ++clickAttempts;
                    ++result.tapAttemptsMade;
                }
            }
        }

        if (std::chrono::steady_clock::now() >= deadline)
            break;
    }

    result.confirmed = false;
    result.failureReason = context.IsCancelled()
        ? "Transition validation canceled"
        : "Transition validation timed out";

    if (mayUseFallback && !context.IsCancelled() && task.templatePath && !task.templatePath->empty())
    {
        const auto fallbackDeadline = DeadlineAfter((std::max)(250, timeoutMs));
        while (!context.IsCancelled() && std::chrono::steady_clock::now() < fallbackDeadline)
        {
            if (context.CaptureScreen())
            {
                TaskMatchResult fallbackMatch;
                auto fallbackTask = task;
                fallbackTask.roi = task.fallbackRoi;
                if (CheckCurrentTemplate(fallbackTask, context, fallbackMatch))
                {
                    int targetX = fallbackMatch.CenterX();
                    int targetY = fallbackMatch.CenterY();
                    if (task.fallbackClickOffset && task.fallbackClickOffset->size() >= 2)
                    {
                        targetX += (*task.fallbackClickOffset)[0];
                        targetY += (*task.fallbackClickOffset)[1];
                    }
                    if (!context.TapMatch(targetX, targetY, fallbackMatch))
                    {
                        result.failureReason = "Fallback transition tap failed";
                        return result;
                    }

                    const auto transitionDeadline = DeadlineAfter((std::max)(250, timeoutMs));
                    while (!context.IsCancelled() && std::chrono::steady_clock::now() < transitionDeadline)
                    {
                        if (context.CaptureScreen() && CheckTransitionTemplates(task, context, result.confirmedTemplate))
                        {
                            result.confirmed = true;
                            return result;
                        }
                        context.Sleep(pollMs);
                    }
                    result.failureReason = "Fallback click did not confirm a transition";
                    return result;
                }
            }
            context.Sleep(pollMs);
        }
    }
    return result;
}

} // namespace UmaAssistant::TaskEngine
