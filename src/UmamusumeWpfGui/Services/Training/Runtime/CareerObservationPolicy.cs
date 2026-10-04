namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Scenario-neutral dynamic gates for stable Career observations. Screen
/// ownership comes from the profile; these rules capture transitions that
/// depend on the current run.
/// </summary>
internal static class CareerObservationPolicy
{
    public static bool IsRuntimeCareerScreen(
        string screenId,
        UraScreenProfile profile) =>
        CareerScreenClassification.IsRuntimeScreen(screenId, profile);

    public static bool IsInitialResumeCandidate(
        string screenId,
        UraScreenProfile profile) =>
        IsRuntimeCareerScreen(screenId, profile);

    public static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state,
        UraScreenProfile profile) =>
        IsEligibleForCareerPhase(screenId, state, profile, hasProfile: true);

    public static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state) =>
        IsEligibleForCareerPhase(screenId, state, profile: null, hasProfile: false);

    private static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state,
        UraScreenProfile? profile,
        bool hasProfile)
    {
        ArgumentNullException.ThrowIfNull(state);
        var kind = Classify(screenId, profile, hasProfile);
        return IsRuntimeKind(kind)
            // Follow Trainer belongs to settlement. Turn confirmations share
            // its green header and Cancel button, so keep this popup out of
            // ordinary turn observations even if a visual match is found.
            && (!Is(screenId, "follow_trainer_limit")
                || Classify(state.LastScreenId, profile, hasProfile)
                    == CareerScreenKind.Settlement)
            && (!Is(screenId, "rewards_collected")
                || Classify(state.LastScreenId, profile, hasProfile)
                    == CareerScreenKind.Settlement)
            && (!Is(screenId, "career_epithet")
                || Is(state.LastScreenId, "career_result_close")
                // A new rating record inserts an optional Next popup before Epithet.
                || Is(state.LastScreenId, "career_rating_record_updated")
                || Is(state.LastScreenId, "career_epithet"))
            && (!Is(screenId, "career_rating_record_updated")
                || Is(state.LastScreenId, "career_result_close")
                || Is(state.LastScreenId, "career_rating_record_updated"));
    }

    public static bool IsReturningHome(UraCareerSessionState state) =>
        state.CareerStarted
        && (state.LastScreenId is "career_complete" or "career_complete_close"
            or "career_story_unlocked" or "career_story_unlocked_compact"
            or "career_story_unlocked_to_home"
            or "home_unselected");

    public static bool IsGoalFlowScreenEligible(
        string screenId,
        UraCareerSessionState state)
    {
        if (!state.CareerStarted)
            return true;

        // A resumed session can start directly on race_runner, where the
        // objective text is no longer visible. Once its result flow finishes,
        // admit the goal page even though the objective-specific probe could
        // not be armed from the missing goal classification.
        var resumedRaceFlowCompleted = state.RaceReplayFlowCompleted
            && Is(state.LastScreenId, CareerRaceRunnerCheckpointHandler.ScreenId);

        return screenId switch
        {
            "goal_objective_complete" => state.GoalCompletionProbeArmed
                || resumedRaceFlowCompleted,
            "goal_update" => Is(state.LastScreenId, "goal_objective_complete"),
            "goal_complete" => state.GoalCompletionProbeArmed
                || resumedRaceFlowCompleted
                || Is(state.LastScreenId, "goal_objective_complete")
                || Is(state.LastScreenId, "goal_update"),
            _ => true,
        };
    }

    public static bool IsCandidate(
        UraScreenDefinition screen,
        UraScreenProfile profile,
        UraCareerSessionState state,
        bool careerOnly,
        bool careerStartTransitionExpected,
        bool resumeRecovery,
        bool settlementInProgress,
        bool returningHome)
    {
        var screenId = screen.ScreenId;
        if (careerOnly && !IsInitialResumeCandidate(screenId, profile))
            return false;

        if (!IsEligibleForCareerPhase(screenId, state, profile)
            && !(careerOnly
                && (screenId is "career_epithet" or "career_rating_record_updated"
                    or "follow_trainer_limit" or "rewards_collected"))
            && !(careerStartTransitionExpected && screenId == "career_intro_event")
            && !(returningHome && (screenId is "home" or "home_unselected")))
        {
            return false;
        }

        if (!careerOnly
            && settlementInProgress
            && CareerScreenClassification.Classify(screenId, profile)
                != CareerScreenKind.Settlement
            && !(returningHome && (screenId is "home" or "home_unselected")))
        {
            return false;
        }

        if (returningHome
            && screenId is not ("career_complete" or "career_complete_close"
                or "career_story_unlocked" or "career_story_unlocked_compact"
                or "career_story_unlocked_to_home" or "home" or "home_unselected"))
        {
            return false;
        }

        if (Is(screenId, "inheritance_event")
            && !state.InheritanceEventPending
            && !careerOnly)
        {
            return false;
        }

        // Once the late-March action is selected, wait for the GO overlay
        // instead of starting another turn from the underlying main page.
        if (state.InheritanceEventPending && Is(screenId, "career_main"))
            return false;

        // Probe the banner after each non-race turn action or qualifying
        // race result. Resume handoff reconstructs that sequence without
        // prior action history.
        if (!resumeRecovery && !IsGoalFlowScreenEligible(screenId, state))
            return false;

        if (!careerOnly
            && !state.CareerStarted
            && state.TurnIndex <= 0
            && !(careerStartTransitionExpected && screenId == "career_intro_event")
            && screenId is not ("career_main" or "career_races_ready"
                or "training_selection" or "training_result" or "training_event"
                or "goal_objective_complete" or "goal_update" or "goal_complete"))
        {
            return false;
        }

        if (careerStartTransitionExpected
            && screenId is not ("career_intro_event" or "career_main" or "career_races_ready"))
        {
            return false;
        }

        return true;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static CareerScreenKind Classify(
        string? screenId,
        UraScreenProfile? profile,
        bool hasProfile) =>
        hasProfile
            ? CareerScreenClassification.Classify(screenId, profile!)
            : CareerScreenClassification.Classify(screenId);

    private static bool IsRuntimeKind(CareerScreenKind kind) =>
        kind is CareerScreenKind.Main
            or CareerScreenKind.Turn
            or CareerScreenKind.Race
            or CareerScreenKind.Event
            or CareerScreenKind.Settlement;
}
