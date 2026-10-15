using System.Globalization;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

internal readonly record struct HachimiTaskLogSemanticStep(
    string StartMessage,
    string CompletedMessage,
    string FailedMessage,
    string? SkippedMessage = null,
    string? SentMessage = null);

/// <summary>
/// Whitelist of user-facing JSON steps. The JSON graph remains responsible
/// for execution, but its implementation nodes are not automatically exposed
/// as UI log entries.
/// </summary>
internal static class HachimiTaskLogSemantics
{
    public static HachimiTaskLogSemanticStep Localize(
        HachimiTaskLogSemanticStep step,
        ILocalizationService? localizationService)
    {
        if (localizationService is null
            || !string.Equals(localizationService.CurrentCulture, "zh-CN", StringComparison.Ordinal))
            return step;

        return step with
        {
            StartMessage = LocalizeMessage(step.StartMessage, localizationService),
            CompletedMessage = LocalizeMessage(step.CompletedMessage, localizationService),
            FailedMessage = LocalizeMessage(step.FailedMessage, localizationService),
            SkippedMessage = step.SkippedMessage is null
                ? null
                : LocalizeMessage(step.SkippedMessage, localizationService),
            SentMessage = step.SentMessage is null
                ? null
                : LocalizeMessage(step.SentMessage, localizationService),
        };
    }

    private static string LocalizeMessage(string message, ILocalizationService localizationService)
    {
        // Prefer a curated translation for the exact semantic sentence before
        // decomposing it into a dynamic training/agenda/strategy template.
        var exactKey = "HachimiTaskLog_" + Regex.Replace(message, @"[^A-Za-z0-9]+", "_").Trim('_');
        var exactTranslation = GetLocalizedValue(localizationService, exactKey, message);
        if (!string.Equals(exactTranslation, message, StringComparison.Ordinal))
            return exactTranslation;

        var agendaSlot = Regex.Match(
            message,
            @"^Selecting the (first|second|third) year agenda slot (\d{2}) (\d{2})\.$");
        if (agendaSlot.Success)
        {
            var year = localizationService.GetString("HachimiTaskLog_Year_" + agendaSlot.Groups[1].Value);
            var month = agendaSlot.Groups[2].Value;
            var turn = agendaSlot.Groups[3].Value;
            return FormatLocalized(
                localizationService,
                "HachimiTaskLog_Selecting_Agenda_Slot",
                message,
                year == "HachimiTaskLog_Year_" + agendaSlot.Groups[1].Value ? agendaSlot.Groups[1].Value : year,
                month,
                turn);
        }

        var agendaYear = Regex.Match(message, @"^(?:Opening the|Selecting the) (first|second|third) year agenda\.$");
        if (agendaYear.Success)
        {
            var year = localizationService.GetString("HachimiTaskLog_Year_" + agendaYear.Groups[1].Value);
            if (year == "HachimiTaskLog_Year_" + agendaYear.Groups[1].Value)
                year = agendaYear.Groups[1].Value;
            return FormatLocalized(localizationService, "HachimiTaskLog_Opening_Agenda", message, year);
        }

        var strategy = Regex.Match(message, @"^Selecting the (.+) race strategy\.$");
        if (strategy.Success)
        {
            var strategyLabel = strategy.Groups[1].Value switch
            {
                "front" => "front",
                "pace" => "pace",
                "late" => "late",
                "end" => "end",
                var value => value,
            };
            var localizedStrategy = localizationService.GetString("HachimiTaskLog_Strategy_" + strategyLabel);
            if (localizedStrategy != "HachimiTaskLog_Strategy_" + strategyLabel)
                strategyLabel = localizedStrategy;
            return FormatLocalized(
                localizationService,
                "HachimiTaskLog_Selecting_Race_Strategy",
                message,
                strategyLabel);
        }

        var selectingTraining = Regex.Match(message, @"^Selecting (.+) training\.$");
        if (selectingTraining.Success)
        {
            var training = LocalizeTrainingName(selectingTraining.Groups[1].Value, localizationService);
            return FormatLocalized(localizationService, "HachimiTaskLog_Selecting_Training", message, training);
        }

        var trainingSelected = Regex.Match(message, @"^(.+) training selected\.$");
        if (trainingSelected.Success)
        {
            var training = LocalizeTrainingName(trainingSelected.Groups[1].Value, localizationService);
            return FormatLocalized(localizationService, "HachimiTaskLog_Training_Selected", message, training);
        }

        var key = "HachimiTaskLog_" + Regex.Replace(message, @"[^A-Za-z0-9]+", "_").Trim('_');
        return GetLocalizedValue(localizationService, key, message);
    }

    private static string LocalizeTrainingName(string training, ILocalizationService localizationService)
    {
        var key = training.ToLowerInvariant() switch
        {
            "speed" => "speed",
            "stamina" => "stamina",
            "power" => "power",
            "guts" => "guts",
            "wit" or "intelligence" => "wit",
            "sprint" or "sprint / power" => "sprint",
            var value => value,
        };
        var localized = localizationService.GetString("HachimiTaskLog_Training_" + key);
        return string.Equals(localized, "HachimiTaskLog_Training_" + key, StringComparison.Ordinal)
            ? training
            : localized;
    }

    private static string FormatLocalized(
        ILocalizationService localizationService,
        string key,
        string fallback,
        params object[] args)
    {
        var template = localizationService.GetString(key);
        if (string.Equals(template, key, StringComparison.Ordinal))
            return fallback;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static string GetLocalizedValue(
        ILocalizationService localizationService,
        string key,
        string fallback)
    {
        var localized = localizationService.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? fallback : localized;
    }

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
            HachimiTaskLogProfile.StartGame => DescribeStartGame(name),
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

    private static HachimiTaskLogSemanticStep DescribeStartGame(string name) => name switch
    {
        "CheckTapToStart" => Step("Tapping to start the game", "Game title screen tapped"),
        "CheckGameHome" => Step("Confirming game home screen", "Game home screen confirmed"),
        "CheckHomeTabOnOtherPage" => Step("Returning to game home screen", "Game home screen returned"),
        "CheckNotices" => Step("Closing game notices", "Game notices closed"),
        "CheckPromoSkip" => Step("Skipping game promotion", "Game promotion skipped"),
        "CheckDataDownload" => Step("Confirming data download", "Data download confirmed"),
        "CheckStartNoticeSkip" => Step("Skipping startup notice", "Startup notice skipped"),
        "CheckLogoSkip" => Step("Skipping startup logo", "Startup logo skipped"),
        "StartupMonitor" => Step("Monitoring game startup", "Game startup completed"),
        _ => default,
    };

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
        "shopSelectAll" => Step("Using the shop's Select All button", "All shop items selected"),
        "shopSelectItems" => Step("Finding configured shop items by name", "Configured shop items selected"),
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

        if (name.StartsWith("normal.strategy.option.", StringComparison.OrdinalIgnoreCase))
        {
            var strategy = name["normal.strategy.option.".Length..].Replace('_', ' ');
            step = Step($"Selecting the {strategy} race strategy", "Race strategy selected");
            return true;
        }

        step = name switch
        {
            "normal.select_mode" => Step("Selecting Normal Career mode", "Normal Career mode selected"),
            "normal.strategy.change" => Step("Opening race strategy", "Race strategy opened"),
            "normal.strategy.save" => Step("Saving the race strategy", "Race strategy saved"),
            "normal.strategy.return" => Step("Returning from race strategy", "Race strategy closed"),
            "normal.start" => Step("Starting Normal Career", "Normal Career started"),
            "normal.post_start.skip" => Step(
                "Skipping the opening Career introduction",
                "Opening Career introduction skipped"),
            "normal.quick_mode.shorten" => Step(
                "Selecting Shorten all events",
                "Shorten all events selected"),
            "normal.quick_mode.skip" => Step(
                "Setting Skip to the fastest speed",
                "Fastest Skip speed selected"),
            "normal.quick_mode.confirm" => Step(
                "Confirming Quick Mode settings",
                "Quick Mode settings confirmed"),
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

    /// <summary>
    /// Maps native Career coordinator event names, which combine the observed
    /// screen ID and the domain action semantic (for example
    /// <c>career_main.training.select</c>), to localized UI log text.
    /// </summary>
    public static bool TryDescribeCareerAction(
        string action,
        out HachimiTaskLogSemanticStep step)
    {
        var name = action.Trim();
        step = name switch
        {
            var value when value.EndsWith(".training.select", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Selecting Career training", "Career training action sent",
                    "Career training confirmed", "Could not send the Career training action"),
            var value when value.EndsWith(".rest.select", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Choosing rest", "Rest action sent", "Rest confirmed", "Could not send the Rest action"),
            var value when value.EndsWith(".infirmary.select", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Choosing the infirmary", "Infirmary action sent",
                    "Infirmary confirmed", "Could not send the infirmary action"),
            var value when value.EndsWith(".recreation.select", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Choosing recreation", "Recreation action sent",
                    "Recreation confirmed", "Could not send the recreation action"),
            var value when value.EndsWith(".event.choose", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Choosing the Career event option", "Career event option action sent",
                    "Career event option confirmed", "Could not send the Career event option"),
            var value when value.EndsWith(".race.progress", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Continuing the Career race", "Career race action sent",
                    "Career race continuation confirmed", "Could not send the Career race action"),
            var value when value.EndsWith(".skill.learn", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Learning a Career skill", "Career skill action sent",
                    "Career skill learning confirmed", "Could not send the Career skill action"),
            var value when value.EndsWith(".wait", StringComparison.OrdinalIgnoreCase) =>
                ActionStep("Continuing the Career", "Career continuation action sent",
                    "Career continuation confirmed", "Could not send the Career continuation action"),
            _ => default,
        };
        return !string.IsNullOrWhiteSpace(step.StartMessage);
    }

    private static HachimiTaskLogSemanticStep DescribeCareer(string name)
    {
        if (TryDescribeCareerAction(name, out var careerAction))
            return careerAction;

        if (TryDescribeNativeIndependentTask(name, out var independentTask))
            return independentTask;

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
            "normal_mode_selected_probe" or "normal_mode_already_selected" => Step(
                "Checking the Career mode",
                "Normal Career mode confirmed"),
            "normal_mode_select" => Step(
                "Selecting Normal Career mode",
                "Normal Career mode selected"),
            "normal_post_start_skip" => Step(
                "Skipping the opening Career introduction",
                "Opening Career introduction skipped"),
            "normal_post_start_skip_confirm" => Step(
                "Confirming the opening Career introduction skip",
                "Opening Career introduction skip confirmed"),
            "normal_quick_mode_shorten_probe" or "normal_quick_mode_shorten_click" => Step(
                "Selecting Shorten all events",
                "Shorten all events selected"),
            "normal_quick_mode_shorten_confirm" => Step(
                "Checking Shorten all events",
                "Shorten all events confirmed"),
            "normal_quick_mode_skip_two_probe" or "normal_quick_mode_skip_one_probe" => Step(
                "Checking the Skip speed",
                "Skip speed checked"),
            "normal_quick_mode_skip_one_click" or "normal_quick_mode_skip_off_click" => Step(
                "Advancing Skip speed",
                "Skip speed advanced"),
            "normal_quick_mode_confirm" => Step(
                "Confirming Quick Mode settings",
                "Quick Mode settings confirmed"),
            "home_home_career" => Step(
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
                "Choosing the first green event option",
                "First green event option selected"),
            "rest_confirmation_rest_confirm" => Step("Confirming rest", "Rest confirmed"),
            "race_list_race_goal_entry" or "race_day_race_open_list" => Step(
                "Opening the goal race",
                "Goal race opened"),
            "race_day_skills_open" => Step("Opening race skills", "Race skills opened"),
            "race_details_race_confirm" => Step("Confirming the goal race", "Goal race confirmed"),
            "race_runner_entry_start" or "race_runner_playback_start" => Step("Starting the goal race", "Goal race started"),
            "race_runner_playback_skip" => Step("Skipping race playback", "Race playback skipped"),
            "race_runner_result_next" or "race_runner_last_next" => Step(
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
            "complete_career_entry_open" => Step(
                "Opening the Complete Career confirmation",
                "Complete Career confirmation opened"),
            "complete_career_career_finish" or "career_complete_career_to_home" or "career_complete_close" => Step(
                "Finishing Career",
                "Career finished"),
            "career_rank_career_next" or "career_result_career_next" => Step(
                "Continuing past the Career result",
                "Career result continued"),
            "career_rating_record_updated_next" => Step(
                "Continuing past the Career rating update",
                "Career rating update continued"),
            "event_reward_next" => Step(
                "Continuing past the Event Reward",
                "Event Reward continued"),
            "career_story_unlocked_close" => Step(
                "Closing the unlocked Career story",
                "Unlocked Career story closed"),
            "career_story_unlocked_to_home" => Step(
                "Returning to Home after the unlocked Career story",
                "Return to Home requested after the unlocked Career story"),
            _ => default,
        };
    }

    private static bool TryDescribeNativeIndependentTask(
        string taskName,
        out HachimiTaskLogSemanticStep step)
    {
        if (taskName.StartsWith("independent_strategy_option_", StringComparison.OrdinalIgnoreCase)
            && taskName.EndsWith("_click", StringComparison.OrdinalIgnoreCase))
        {
            var strategy = taskName["independent_strategy_option_".Length..^6];
            if (strategy is not ("front" or "pace" or "late" or "end"))
            {
                step = default;
                return false;
            }
            return TryDescribeIndependentAction("independent.strategy.option." + strategy, out step);
        }

        if (taskName.StartsWith("independent_agenda_year_", StringComparison.OrdinalIgnoreCase)
            && taskName.EndsWith("_click", StringComparison.OrdinalIgnoreCase))
        {
            var year = taskName["independent_agenda_year_".Length..^6];
            if (year is not ("first_year" or "second_year" or "third_year"))
            {
                step = default;
                return false;
            }
            return TryDescribeIndependentAction("independent.agenda.year." + year, out step);
        }

        const string agendaSlotPrefix = "independent_agenda_slot_";
        if (taskName.StartsWith(agendaSlotPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var slot = taskName[agendaSlotPrefix.Length..];
            foreach (var year in new[] { "first_year", "second_year", "third_year" })
            {
                var yearPrefix = year + "_";
                if (!slot.StartsWith(yearPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var turn = slot[yearPrefix.Length..];
                if (turn.Length == 5 && turn[2] == '_'
                    && char.IsAsciiDigit(turn[0]) && char.IsAsciiDigit(turn[1])
                    && char.IsAsciiDigit(turn[3]) && char.IsAsciiDigit(turn[4]))
                {
                    var month = int.Parse(turn[..2], CultureInfo.InvariantCulture);
                    var agendaTurn = turn[3..];
                    if (month is < 1 or > 12 || (agendaTurn != "01" && agendaTurn != "02"))
                    {
                        step = default;
                        return false;
                    }
                    return TryDescribeIndependentAction(
                        $"independent.agenda.slot.{year}.{turn}", out step);
                }
                break;
            }
            step = default;
            return false;
        }

        step = taskName switch
        {
            "independent_mode_selected_probe" => Step(
                "Checking Independent Training mode", "Independent Training mode confirmed"),
            "independent_mode_select" => Step(
                "Selecting Independent Training mode", "Independent Training mode selected"),
            "independent_lineup_expand" => Step(
                "Opening the training lineup", "Training lineup opened"),
            "independent_lineup_collapse" => Step(
                "Closing the training lineup", "Training lineup closed"),
            "independent_focus_balanced_click" => Step(
                "Selecting the balanced training focus", "Balanced training focus selected"),
            "independent_focus_stamina_click" => Step(
                "Selecting the stamina training focus", "Stamina training focus selected"),
            "independent_focus_sprint_click" => Step(
                "Selecting the sprint training focus", "Sprint training focus selected"),
            "independent_strategy_change" => Step(
                "Opening race strategy", "Race strategy opened"),
            "independent_strategy_save" => Step(
                "Saving the race strategy", "Race strategy saved"),
            "independent_agenda_open" => Step(
                "Opening the training agenda", "Training agenda opened"),
            "independent_agenda_reset" => Step(
                "Resetting the training agenda", "Training agenda reset"),
            "independent_agenda_reset_confirm" => Step(
                "Confirming the training agenda reset", "Training agenda reset confirmed"),
            "independent_agenda_save" => Step(
                "Saving the selected agenda", "Agenda saved"),
            "independent_agenda_close" => Step(
                "Closing the training agenda", "Training agenda closed"),
            "independent_agenda_race_find" or "independent_agenda_race_card_find" => Step(
                "Selecting the configured agenda race", "Agenda race selected"),
            "independent_skills_open" => Step(
                "Opening the skill picker", "Skill picker opened"),
            "independent_skills_search_focus" => Step(
                "Opening skill search", "Skill search opened"),
            "independent_skills_search_input" => Step(
                "Searching for the configured skill", "Skill search entered"),
            "independent_skills_search_submit" => Step(
                "Submitting the skill search", "Skill search submitted"),
            "independent_skills_search_checkbox_ocr" or "independent_skills_search_checkbox" => Step(
                "Selecting the matching skill", "Matching skill selected"),
            "independent_skills_save" => Step(
                "Saving the selected skills", "Skills saved"),
            "independent_training_start" => Step(
                "Starting Independent Training", "Independent Training started"),
            "independent_post_start_ok" => Step(
                "Confirming the post-start dialog", "Post-start dialog confirmed"),
            "independent_post_start_menu" => Step(
                "Handling the post-start menu", "Post-start menu handled"),
            "independent_post_start_to_home" => Step(
                "Returning to Home after training", "Return to Home requested"),
            "independent_post_start_home_probe" => Step(
                "Confirming the Home screen", "Home screen confirmed"),
            _ => default,
        };
        return !string.IsNullOrWhiteSpace(step.StartMessage);
    }

    private static HachimiTaskLogSemanticStep Step(
        string start,
        string completed) =>
        new(
            start + ".",
            completed + ".",
            "Could not complete: " + start.ToLowerInvariant() + ".");

    private static HachimiTaskLogSemanticStep ActionStep(
        string start,
        string sent,
        string completed,
        string failed) =>
        new(
            start + ".",
            completed + ".",
            failed + ".",
            SentMessage: sent + ".");

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
