using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

internal readonly record struct HachimiTaskLogSemanticStep(
    string StartMessage,
    string CompletedMessage,
    string FailedMessage,
    string? SkippedMessage = null);

/// <summary>
/// Whitelist of user-facing JSON steps. The JSON graph remains responsible
/// for execution, but its implementation nodes are not automatically exposed
/// as UI log entries.
/// </summary>
internal static class HachimiTaskLogSemantics
{
    public static string ToUserFacingFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "The task could not be completed.";

        var value = message.Trim();
        if (value.StartsWith("Timed out waiting for JSON task '", StringComparison.Ordinal)
            || value.StartsWith("Timed out waiting for parallel monitor '", StringComparison.Ordinal))
        {
            return "The game did not show the required screen or action in time.";
        }

        if (value.StartsWith("Could not execute JSON task '", StringComparison.Ordinal))
            return "The required Career action could not be completed.";

        if (value.Contains("undefined task", StringComparison.OrdinalIgnoreCase)
            || value.Contains("unsupported action", StringComparison.OrdinalIgnoreCase)
            || value.Contains("exceeded the transition guard", StringComparison.OrdinalIgnoreCase))
        {
            return "The task flow configuration is invalid.";
        }

        if (value.StartsWith("Nested pipeline '", StringComparison.Ordinal))
            return "The optional shop flow could not be completed.";

        return message;
    }

    public static bool TryDescribe(
        HachimiTaskLogProfile profile,
        string taskName,
        out HachimiTaskLogSemanticStep step)
    {
        var name = taskName.Trim();
        step = profile switch
        {
            HachimiTaskLogProfile.TeamRace => DescribeTeamRace(name),
            HachimiTaskLogProfile.DailyRace => DescribeDailyRace(name),
            HachimiTaskLogProfile.MailCollection => DescribeMailCollection(name),
            HachimiTaskLogProfile.MissionCollection => DescribeMissionCollection(name),
            HachimiTaskLogProfile.Shop => DescribeShop(name),
            HachimiTaskLogProfile.Career => DescribeCareer(name),
            _ => default,
        };
        return !string.IsNullOrWhiteSpace(step.StartMessage);
    }

    private static HachimiTaskLogSemanticStep DescribeTeamRace(string name) => name switch
    {
        "raceTab" => Step("Opening the Race menu", "Race menu opened"),
        "teamTrials" => Step("Opening Team Trials", "Team Trials opened"),
        "teamRace" => Step("Opening Team Race", "Team Race opened"),
        "opponent" => Step("Selecting the opponent", "Opponent selected"),
        "matchupNext" => Step("Opening the next matchup", "Matchup opened"),
        "itemRace" => Step("Opening the race selection", "Race selection opened"),
        "quickmode" => Step("Enabling Quick Mode", "Quick Mode enabled"),
        "seeallresult" => Step("Opening all race results", "All race results opened"),
        "raceagain" => Step("Starting the next race", "Next race started"),
        "newhighscore" => Step("Checking for a new high score", "High score check completed"),
        "finalNext" => Step("Continuing past the final results", "Final results closed"),
        _ => default,
    };

    private static HachimiTaskLogSemanticStep DescribeDailyRace(string name) => name switch
    {
        "raceTab" => Step("Opening the Race menu", "Race menu opened"),
        "dailyProgram" => Step("Opening Daily Program", "Daily Program opened"),
        "dailyRaces" => Step("Opening the Daily Race list", "Daily Race list opened"),
        "moniesMode" => Step("Selecting the Monies race mode", "Monies mode selected"),
        "supportPointMode" => Step("Selecting the Support Points race mode", "Support Points mode selected"),
        "moniesDifficulty" or "supportPointDifficulty" => Step(
            "Selecting the configured race difficulty",
            "Race difficulty selected"),
        "raceStart" => Step("Starting the Daily Race", "Daily Race started"),
        "runnerSortOpen" => Step("Opening the runner list", "Runner list opened"),
        "runnerConfirm" => Step("Confirming the runner", "Runner confirmed"),
        "multiRaceTicketConfirm" => Step("Confirming the race count", "Race count confirmed"),
        "multiRaceComplete" => Step("Waiting for the multi-race results", "Multi-race results ready"),
        "itemsViewResult" => Step("Opening the race results", "Race results opened"),
        "viewResultTap" => Step("Opening the detailed race result", "Detailed race result opened"),
        "racePlaybackResult" => Step("Opening the race playback", "Race playback opened"),
        "raceSkip" => Step("Skipping the race playback", "Race playback skipped"),
        "finalNext" or "finalNextSupport" => Step(
            "Continuing through the race results",
            "Race results continued"),
        "rewardNext" => Step("Continuing past the reward screen", "Reward screen closed"),
        "dailySaleProbe" => AvailabilityStep(
            "Checking for a Daily Race shop offer",
            "Daily Race shop offer found",
            "No Daily Race shop offer available"),
        "verifyDailyRaceReturn" => Step(
            "Returning to the Daily Race list",
            "Daily Race list restored"),
        _ => default,
    };

    private static HachimiTaskLogSemanticStep DescribeMailCollection(string name) => name switch
    {
        "giftBox" => Step("Opening the gift box", "Gift box opened"),
        "collectAll" => Step("Collecting all available mailbox rewards", "Mailbox rewards collected"),
        "rewardClose" or "close" => Step("Closing the reward screen", "Reward screen closed"),
        "homeVerify" => Step("Returning to the Home screen", "Home screen restored"),
        _ => default,
    };

    private static HachimiTaskLogSemanticStep DescribeMissionCollection(string name) => name switch
    {
        "missionIcon" => Step("Opening Missions", "Missions opened"),
        "dailySelected" or "dailyUnselected" => Step("Opening Daily Missions", "Daily Missions opened"),
        "dailyRed" => AvailabilityStep(
            "Checking Daily Mission rewards",
            "Daily Mission rewards found",
            "No Daily Mission rewards available"),
        "dailyCollectAll" => Step("Collecting Daily Mission rewards", "Daily Mission rewards collected"),
        "dailyClose" => Step("Closing Daily Mission rewards", "Daily Mission rewards closed"),
        "mainTab" => Step("Opening Main Missions", "Main Missions opened"),
        "mainRed" => AvailabilityStep(
            "Checking Main Mission rewards",
            "Main Mission rewards found",
            "No Main Mission rewards available"),
        "mainCollectAll" => Step("Collecting Main Mission rewards", "Main Mission rewards collected"),
        "mainClose" => Step("Closing Main Mission rewards", "Main Mission rewards closed"),
        "titlesTab" => Step("Opening Title Missions", "Title Missions opened"),
        "titlesRed" => AvailabilityStep(
            "Checking Title Mission rewards",
            "Title Mission rewards found",
            "No Title Mission rewards available"),
        "titlesCollectAll" => Step("Collecting Title Mission rewards", "Title Mission rewards collected"),
        "titlesClose" => Step("Closing Title Mission rewards", "Title Mission rewards closed"),
        "specialTab" => Step("Opening Special Missions", "Special Missions opened"),
        "specialRed" => AvailabilityStep(
            "Checking Special Mission rewards",
            "Special Mission rewards found",
            "No Special Mission rewards available"),
        "specialCollectAll" => Step("Collecting Special Mission rewards", "Special Mission rewards collected"),
        "specialClose" => Step("Closing Special Mission rewards", "Special Mission rewards closed"),
        "returnHome" => Step("Returning to the Home screen", "Home screen restored"),
        _ => default,
    };

    private static HachimiTaskLogSemanticStep DescribeShop(string name) => name switch
    {
        "specialShop" => Step("Opening Special Shop", "Special Shop opened"),
        "dailySales" => Step("Opening Daily Sales", "Daily Sales opened"),
        "shopProbe" => AvailabilityStep(
            "Checking for eligible shop items",
            "Eligible shop items found",
            "No eligible shop items available"),
        "shopSelectAll" => Step("Selecting all configured shop items", "Shop items selected"),
        "shopConfirm" => Step("Confirming the selected shop items", "Shop selection confirmed"),
        "shopExchangeConfirm" => Step("Confirming the purchase", "Purchase confirmed"),
        "shopExchangeComplete" => Step("Waiting for the purchase to complete", "Purchase completed"),
        "shopClose" => Step("Closing the purchase screen", "Purchase screen closed"),
        "shopBack" or "shopAndroidBack" => Step("Returning from the shop", "Shop closed"),
        "shopNoShopComplete" => Step(
            "Finishing because no shop items are available",
            "No shop items available"),
        _ => default,
    };

    public static bool TryDescribeIndependentAction(
        string action,
        out HachimiTaskLogSemanticStep step)
    {
        var name = action.Trim();
        if (name.StartsWith("independent.agenda.slot.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5)
            {
                var year = parts[3].Replace('_', ' ');
                var turn = parts[4].Replace('_', ' ');
                step = Step($"Selecting the {year} agenda slot {turn}", "Agenda slot selected");
                return true;
            }
        }

        if (name.StartsWith("independent.agenda.year.", StringComparison.OrdinalIgnoreCase))
        {
            var year = name["independent.agenda.year.".Length..].Replace('_', ' ');
            step = Step($"Opening the {year} agenda", "Agenda year opened");
            return true;
        }

        if (name.StartsWith("independent.strategy.option.", StringComparison.OrdinalIgnoreCase))
        {
            var strategy = name["independent.strategy.option.".Length..].Replace('_', ' ');
            step = Step($"Selecting the {strategy} race strategy", "Race strategy selected");
            return true;
        }

        step = name switch
        {
            "independent.select_mode" => Step("Selecting Independent Training mode", "Independent Training mode selected"),
            "independent.lineup.expand" => Step("Opening the training lineup", "Training lineup opened"),
            "independent.focus.stamina" => Step("Selecting the stamina focus", "Stamina focus selected"),
            "independent.focus.sprint" => Step("Selecting the sprint focus", "Sprint focus selected"),
            "independent.focus.balanced" => Step("Selecting the balanced focus", "Balanced focus selected"),
            "independent.strategy.change" => Step("Opening race strategy", "Race strategy opened"),
            "independent.strategy.save" => Step("Saving the race strategy", "Race strategy saved"),
            "independent.strategy.return" => Step("Returning from race strategy", "Race strategy closed"),
            "independent.agenda.open" => Step("Opening the training agenda", "Training agenda opened"),
            "independent.agenda.race" => Step("Selecting the configured agenda race", "Agenda race selected"),
            "independent.agenda.race.card" => Step("Selecting the configured agenda race", "Agenda race selected"),
            "independent.agenda.save" => Step("Saving the selected agenda", "Agenda saved"),
            "independent.skills.open" => Step("Opening the skill picker", "Skill picker opened"),
            "independent.skills.search.input" => Step("Searching for the configured skill", "Skill search entered"),
            "independent.skills.search.submit" => Step("Submitting the skill search", "Skill search submitted"),
            "independent.skills.search.checkbox" or "independent.skills.search.checkbox.fallback" => Step(
                "Selecting the matching skill",
                "Matching skill selected"),
            "independent.skills.save" => Step("Saving the selected skills", "Skills saved"),
            "independent.start" => Step("Starting Independent Training", "Independent Training started"),
            "independent.post_start.ok" => Step("Confirming the post-start dialog", "Post-start dialog confirmed"),
            "independent.post_start.menu" => Step("Handling the post-start menu", "Post-start menu handled"),
            "independent.post_start.to_home" => Step("Returning to Home after training", "Return to Home requested"),
            "independent.post_start.home_probe" => Step("Confirming the Home screen", "Home screen confirmed"),
            _ => default,
        };
        return !string.IsNullOrWhiteSpace(step.StartMessage);
    }

    private static HachimiTaskLogSemanticStep DescribeCareer(string name)
    {
        if (name.StartsWith("independent_", StringComparison.OrdinalIgnoreCase))
            return default;

        if (name.StartsWith("training_selection_training_", StringComparison.OrdinalIgnoreCase))
        {
            var training = name["training_selection_training_".Length..]
                .Replace('_', ' ');
            return Step($"Selecting {training} training", $"{training} training selected");
        }

        return name switch
        {
            "home_home_career" or "home_home_career_active" => Step(
                "Opening Career from Home",
                "Career entry opened"),
            "career_continue_resume" => Step(
                "Resuming the existing Career",
                "Existing Career selected"),
            "career_continue_delete" or "career_continue_delete_confirm" => Step(
                "Choosing a new Career",
                "New Career selected"),
            "scenario_select_scenario_next" or "scenario_select_scenario_next_card" => Step(
                "Selecting the Career scenario",
                "Career scenario selected"),
            "trainee_select_pick" or "trainee_select_trainee_next" => Step(
                "Selecting the trainee",
                "Trainee selected"),
            "support_select_support_auto_fill" => Step(
                "Filling the support deck",
                "Support deck filled"),
            "support_select_support_start" or "support_ready_support_start" => Step(
                "Starting Career setup",
                "Career setup confirmed"),
            "legacy_select_legacy_choose" => Step(
                "Selecting the legacy parents",
                "Legacy parents selected"),
            "career_intro_event_event_advance" => Step(
                "Continuing the Career introduction",
                "Career introduction continued"),
            "career_entry_career_start" => Step("Starting Career", "Career started"),
            "career_main_action_training" => Step(
                "Opening training selection",
                "Training selection opened"),
            "career_main_action_rest" => Step("Choosing rest", "Rest selected"),
            "career_main_action_finale_races" => Step(
                "Opening the finale races",
                "Finale races opened"),
            "career_main_goal_details" => Step(
                "Reviewing the Career objective",
                "Career objective reviewed"),
            "career_races_ready_action_races" => Step(
                "Checking the required Career races",
                "Required Career races checked"),
            "training_result_event_advance" or "training_event_event_advance" => Step(
                "Continuing the Career event",
                "Career event continued"),
            "event_choice_event_choice_first" => Step(
                "Choosing the configured event option",
                "Event option selected"),
            "rest_confirmation_rest_confirm" => Step("Confirming rest", "Rest confirmed"),
            "rest_result_event_advance" => Step("Continuing after rest", "Rest result continued"),
            "race_list_race_goal_entry" or "race_day_race_open_list" => Step(
                "Opening the goal race",
                "Goal race opened"),
            "race_day_skills_open" => Step("Opening race skills", "Race skills opened"),
            "race_details_race_confirm" => Step("Confirming the goal race", "Goal race confirmed"),
            "race_attributes_race_start_playback" => Step("Starting the goal race", "Goal race started"),
            "race_live_race_live_next" or "race_result_result_next" => Step(
                "Continuing past the race result",
                "Race result continued"),
            "reward_reward_next" or "reward_support_reward_next" => Step(
                "Continuing past the race reward",
                "Race reward continued"),
            "goal_update_goal_update_next" => Step(
                "Updating the Career goal",
                "Career goal updated"),
            "goal_complete_goal_next" => Step(
                "Confirming the completed Career goal",
                "Career goal completed"),
            "scenario_event_event_advance" => Step(
                "Continuing the scenario event",
                "Scenario event continued"),
            "complete_career_career_finish" or "career_complete_career_to_home" => Step(
                "Finishing Career",
                "Career finished"),
            "career_rank_career_next" or "career_result_career_next" => Step(
                "Continuing past the Career result",
                "Career result continued"),
            _ => default,
        };
    }

    private static HachimiTaskLogSemanticStep Step(
        string start,
        string completed) =>
        new(
            start + ".",
            completed + ".",
            "Could not complete: " + start.ToLowerInvariant() + ".");

    private static HachimiTaskLogSemanticStep AvailabilityStep(
        string start,
        string completed,
        string skipped) =>
        new(
            start + ".",
            completed + ".",
            "Could not check: " + start.ToLowerInvariant() + ".",
            skipped + ".");
}
