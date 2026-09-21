namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Top-level area of a stable Career runtime screen.
/// The area is used to validate an observation before any runtime state is
/// updated or a flow is dispatched.
/// </summary>
public enum CareerScreenKind
{
    Unknown,
    Main,
    Turn,
    Race,
    Event,
    Settlement,
}

internal static class CareerScreenClassification
{
    public static CareerScreenKind Classify(string? screenId) => screenId switch
    {
        "career_main" => CareerScreenKind.Main,

        "training_selection"
            or "training_result"
            or "rest_confirmation"
            or "rest_result"
            => CareerScreenKind.Turn,

        "career_races_ready"
            or "race_day"
            or "race_list"
            or "race_runner"
            or "race_details"
            or "race_attributes"
            or "race_playback"
            or "race_playback_settings"
            or "race_live"
            or "race_result"
            or "reward"
            or "reward_support"
            or "goal_update"
            or "goal_complete"
            => CareerScreenKind.Race,

        "career_intro_event"
            or "training_event"
            or "event_choice"
            or "scenario_event"
            => CareerScreenKind.Event,

        "complete_career"
            or "career_rank"
            or "career_result"
            or "rewards"
            or "sparks"
            or "sparks_confirmation"
            or "career_complete"
            => CareerScreenKind.Settlement,

        _ => CareerScreenKind.Unknown,
    };

    public static bool IsRuntimeScreen(string? screenId) =>
        Classify(screenId) is not CareerScreenKind.Unknown;
}
