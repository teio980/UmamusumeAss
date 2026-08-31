using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentStrategyFlowContractTests
{
    private static readonly string[] StrategyValues = ["front", "pace", "late", "end"];

    [Theory]
    [InlineData("front", "independent.strategy.option.front", "Front")]
    [InlineData("pace", "independent.strategy.option.pace", "Pace")]
    [InlineData("late", "independent.strategy.option.late", "Late")]
    [InlineData("end", "independent.strategy.option.end", "End")]
    public void Existing_lineup_strategy_setting_maps_to_one_strategy_option(
        string setting,
        string semanticAction,
        string targetText)
    {
        Assert.True(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                setting,
                out var actualSemanticAction,
                out var actualTargetText));
        Assert.Equal(semanticAction, actualSemanticAction);
        Assert.Equal(targetText, actualTargetText);
    }

    [Fact]
    public void Invalid_lineup_strategy_setting_is_rejected_before_strategy_actions()
    {
        Assert.False(
            IndependentTrainingCatalog.TryGetLineupStrategyUiMapping(
                "not-a-strategy",
                out _,
                out _));
    }

    [Fact]
    public async Task Independent_agenda_race_picker_uses_ocr_before_card_fallback()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));
        var finalConfirmation = pack.ScreenProfile.Find("career_final_confirmation");

        Assert.NotNull(finalConfirmation);
        var ocrFind = finalConfirmation!.FindAction(
            IndependentTrainingCatalog.AgendaRaceSemanticAction());
        var ocrVerify = finalConfirmation.FindAction(
            IndependentTrainingCatalog.AgendaRaceVerifySemanticAction());
        var cardFind = finalConfirmation.FindAction(
            IndependentTrainingCatalog.AgendaRaceCardSemanticAction());
        var cardVerify = finalConfirmation.FindAction(
            IndependentTrainingCatalog.AgendaRaceCardVerifySemanticAction());

        Assert.Equal("independent_agenda_race_find", ocrFind?.Task);
        Assert.Equal("independent_agenda_race_verify", ocrVerify?.Task);
        Assert.Equal("independent_agenda_race_card_find", cardFind?.Task);
        Assert.Equal("independent_agenda_race_card_verify", cardVerify?.Task);

        Assert.Equal("OcrText", pack.ExecutionDefinition.GetTask(ocrFind!.Task).Algorithm);
        Assert.Equal("ClickText", pack.ExecutionDefinition.GetTask(ocrFind.Task).Action);
        Assert.Equal("OcrText", pack.ExecutionDefinition.GetTask(ocrVerify!.Task).Algorithm);
        Assert.Equal("FindText", pack.ExecutionDefinition.GetTask(ocrVerify.Task).Action);
        foreach (var taskName in new[] { ocrFind.Task, ocrVerify.Task })
        {
            var task = pack.ExecutionDefinition.GetTask(taskName);
            Assert.Equal("tokenCoverage", task.OcrMatchMode);
            Assert.Equal(1d, task.FuzzyThreshold);
            Assert.True(task.OcrRequireAllTokens);
            Assert.True(task.Unique);
        }
        Assert.Equal("MatchTemplateScaled", pack.ExecutionDefinition.GetTask(cardFind!.Task).Algorithm);
        Assert.Equal("ClickSelf", pack.ExecutionDefinition.GetTask(cardFind.Task).Action);
        // Give five template scrolls enough time without weakening recognition.
        Assert.Equal(0.62d, pack.ExecutionDefinition.GetTask(cardFind.Task).TemplateThreshold);
        Assert.Equal(60_000, pack.ExecutionDefinition.GetTask(cardFind.Task).TimeoutMilliseconds);
        Assert.Equal("MatchTemplateScaled", pack.ExecutionDefinition.GetTask(cardVerify!.Task).Algorithm);
        Assert.Equal("JustReturn", pack.ExecutionDefinition.GetTask(cardVerify.Task).Action);

        var pipelineSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Services",
            "Training",
            "AdbCareerTrainingPipeline.cs"));
        // Check the execution branch, not the earlier preflight declarations.
        var lookupIndex = pipelineSource.IndexOf("var ocrRaceResult =", StringComparison.Ordinal);
        Assert.True(lookupIndex >= 0);
        var ocrIndex = pipelineSource.IndexOf(
            "IndependentTrainingCatalog.AgendaRaceSemanticAction()",
            lookupIndex, StringComparison.Ordinal);
        var cardIndex = pipelineSource.IndexOf(
            "IndependentTrainingCatalog.AgendaRaceCardSemanticAction()",
            lookupIndex, StringComparison.Ordinal);
        Assert.True(ocrIndex >= 0);
        Assert.True(cardIndex > ocrIndex);
        var verificationFailure = pipelineSource.IndexOf(
            "return ocrVerifyResult;", ocrIndex, StringComparison.Ordinal);
        var rewindIndex = pipelineSource.IndexOf(
            "\"independent.agenda.race.scroll.top\"", ocrIndex, StringComparison.Ordinal);
        Assert.InRange(verificationFailure, ocrIndex, cardIndex);
        Assert.InRange(rewindIndex, ocrIndex, cardIndex);

        var rewindAction = finalConfirmation.FindAction("independent.agenda.race.scroll.top");
        Assert.NotNull(rewindAction);
        Assert.True(AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
            pack, "independent.agenda.race.scroll.top", out var rewindError), rewindError);
        var rewind = pack.ExecutionDefinition.GetTask(rewindAction!.Task);
        Assert.Equal("JustReturn", rewind.Algorithm);
        Assert.Equal("Swipe", rewind.Action);
        Assert.Equal([840, 500, 840, 1200, 600], rewind.Swipe!);
        Assert.Equal([rewindAction.Task], rewind.Next);
        Assert.True(rewind.MaxTimes >= pack.ExecutionDefinition.GetTask(ocrFind.Task).MaxScrolls);
        var done = pack.ExecutionDefinition.GetTask(Assert.Single(rewind.ExceededNext));
        Assert.Equal("JustReturn", done.Action);
        Assert.True(done.Success);
        Assert.Empty(done.Next);
    }

    [Theory]
    [InlineData("OCR target 'race' was not found before timeout.", true)]
    [InlineData("Could not execute JSON task 'find': OCR target 'race' matched 2 candidates.", true)]
    [InlineData("OCR screenshot could not be captured for 'find'.", false)]
    [InlineData("OCR task 'find' failed: ADB swipe failed.", false)]
    [InlineData("OCR task 'find' requires targetText or a runtime target override.", false)]
    [InlineData("Screen action is missing from screen_profile.json.", false)]
    [InlineData("OCR target 'another race' was not found before timeout.", false)]
    public void Agenda_fallback_only_accepts_recognition_misses(string message, bool expected)
    {
        Assert.Equal(expected, AdbCareerTrainingPipeline.IsAgendaOcrRecognitionMiss(message, "race"));
    }

    [Fact]
    public void Independent_agenda_race_ocr_target_uses_picker_header_text()
    {
        var race = new IndependentTrainingRace(
            "Saudi Arabia Royal Cup",
            "G3",
            "First Year",
            "10_01",
            "Turf",
            "Tokyo \u21d0",
            "Mile",
            "1600m",
            RaceId: 3057,
            GameTrack: "Tokyo",
            GameDistance: 1600,
            GameGround: "Turf",
            IsGameAvailable: true);

        Assert.Equal("Tokyo Turf 1600m (Mile) Left", race.PickerHeaderTarget);
    }

    [Fact]
    public async Task Profile_and_pipeline_contract_keeps_skills_collapse_strategy_save_start_order()
    {
        var root = FindSolutionRoot();
        var executionPath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json");
        var profilePath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(executionPath);
        Assert.NotNull(definition);

        var collapseScroll = definition!.GetTask("independent_lineup_scroll_to_top");
        Assert.Equal("Swipe", collapseScroll.Action, ignoreCase: true);
        Assert.Equal([840, 500, 840, 1200, 600], collapseScroll.Swipe!);
        Assert.Contains("independent_lineup_scroll_header_probe", collapseScroll.Next);

        var collapsePrepare = definition.GetTask("independent_lineup_collapse_prepare");
        Assert.Equal("lineup_details_header.png", Path.GetFileName(collapsePrepare.Template));
        Assert.Contains("independent_lineup_collapsed_probe", collapsePrepare.Next);

        var collapsedProbe = definition.GetTask("independent_lineup_collapsed_probe");
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(collapsedProbe.Template));
        Assert.Contains("independent_lineup_collapse_expanded_guard", collapsedProbe.Next);
        Assert.Contains("independent_lineup_collapse_expanded_probe", collapsedProbe.OnErrorNext);

        var expandedGuard = definition.GetTask("independent_lineup_collapse_expanded_guard");
        Assert.Equal("lineup_open_down.png", Path.GetFileName(expandedGuard.Template));
        Assert.Contains("independent_lineup_collapse_invalid_state", expandedGuard.Next);
        Assert.Contains("independent_lineup_collapse_already_collapsed_verified", expandedGuard.OnErrorNext);

        var alreadyCollapsedVerified = definition.GetTask("independent_lineup_collapse_already_collapsed_verified");
        Assert.Equal("JustReturn", alreadyCollapsedVerified.Action, ignoreCase: true);
        Assert.True(alreadyCollapsedVerified.Success);
        Assert.Empty(alreadyCollapsedVerified.Next);

        Assert.Contains(
            "independent_lineup_collapse_invalid_state",
            definition.GetTask("independent_lineup_collapse_expanded_probe").OnErrorNext);

        var collapseClick = definition.GetTask("independent_lineup_collapse");
        Assert.Equal("ClickSelf", collapseClick.Action, ignoreCase: true);
        Assert.Equal("lineup_open_down.png", Path.GetFileName(collapseClick.Template));
        Assert.Contains("independent_lineup_collapse_post_probe", collapseClick.Next);

        var postProbe = definition.GetTask("independent_lineup_collapse_post_probe");
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(postProbe.Template));
        Assert.Contains("independent_lineup_collapse_post_expanded_guard", postProbe.Next);
        Assert.Contains("independent_lineup_collapse_invalid_state", postProbe.OnErrorNext);

        var postExpandedGuard = definition.GetTask("independent_lineup_collapse_post_expanded_guard");
        Assert.Contains("independent_lineup_collapse_invalid_state", postExpandedGuard.Next);
        Assert.Contains("independent_lineup_collapse_post_verified", postExpandedGuard.OnErrorNext);
        Assert.True(definition.GetTask("independent_lineup_collapse_post_verified").Success);

        var expandPrepare = definition.GetTask("independent_lineup_expand_prepare");
        Assert.Contains("independent_lineup_expanded_probe", expandPrepare.Next);
        var expandProbe = definition.GetTask("independent_lineup_expanded_probe");
        Assert.Equal("lineup_open_down.png", Path.GetFileName(expandProbe.Template));
        Assert.Contains("independent_lineup_expand_collapsed_guard", expandProbe.Next);
        Assert.Contains("independent_lineup_expand_collapsed_probe", expandProbe.OnErrorNext);
        var expandClick = definition.GetTask("independent_lineup_expand");
        Assert.Equal("ClickSelf", expandClick.Action, ignoreCase: true);
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(expandClick.Template));
        var expandPostProbe = definition.GetTask("independent_lineup_expand_post_probe");
        Assert.Equal("lineup_open_down.png", Path.GetFileName(expandPostProbe.Template));
        Assert.Contains("independent_lineup_expand_post_collapsed_guard", expandPostProbe.Next);
        Assert.True(definition.GetTask("independent_lineup_expand_post_verified").Success);

        var strategyGate = definition.GetTask("independent_lineup_strategy_precondition");
        Assert.Equal("MatchTemplate", strategyGate.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", strategyGate.Action, ignoreCase: true);
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(strategyGate.Template));
        Assert.Equal([760, 390, 130, 150], strategyGate.Roi!);
        Assert.Equal(0.90, strategyGate.TemplateThreshold);
        Assert.Contains("independent_lineup_strategy_precondition_failed", strategyGate.OnErrorNext);
        Assert.Contains("independent_lineup_strategy_precondition_verified", definition.GetTask("independent_lineup_strategy_expanded_guard").OnErrorNext);

        var modeProbe = definition.GetTask("independent_mode_selected_probe");
        Assert.Equal("MatchTemplate", modeProbe.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", modeProbe.Action, ignoreCase: true);
        Assert.Empty(modeProbe.Next);
        Assert.Equal(["independent_mode_select"], modeProbe.OnErrorNext);
        var modeClick = definition.GetTask("independent_mode_select");
        Assert.Equal("MatchTemplate", modeClick.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", modeClick.Action, ignoreCase: true);
        Assert.False(modeClick.Success);
        Assert.Empty(modeClick.OnErrorNext);
        Assert.Equal(["independent_mode_select_confirm"], modeClick.Next);

        var modeConfirm = definition.GetTask("independent_mode_select_confirm");
        Assert.Equal("MatchTemplate", modeConfirm.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", modeConfirm.Action, ignoreCase: true);
        Assert.Equal("mode_independent_selected.png", Path.GetFileName(modeConfirm.Template));
        Assert.Equal([450, 240, 430, 75], modeConfirm.Roi!);
        Assert.Equal(0.82, modeConfirm.TemplateThreshold);
        Assert.Equal(10_000, modeConfirm.TimeoutMilliseconds);
        Assert.Equal(250, modeConfirm.PollIntervalMilliseconds);
        Assert.True(modeConfirm.Success);
        Assert.Empty(modeConfirm.Next);
        Assert.Empty(modeConfirm.OnErrorNext);

        var agendaRaceCardVerify = definition.GetTask("independent_agenda_race_card_verify");
        Assert.Equal("MatchTemplateScaled", agendaRaceCardVerify.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", agendaRaceCardVerify.Action, ignoreCase: true);
        Assert.True(agendaRaceCardVerify.Success);
        Assert.Empty(agendaRaceCardVerify.Next);
        Assert.Empty(agendaRaceCardVerify.OnErrorNext);

        var strategyExpandedGuard = definition.GetTask("independent_lineup_strategy_expanded_guard");
        Assert.Equal([760, 390, 130, 150], strategyExpandedGuard.Roi!);
        Assert.Contains("independent_lineup_strategy_precondition_failed", strategyExpandedGuard.Next);
        Assert.Contains("independent_lineup_strategy_precondition_verified", strategyExpandedGuard.OnErrorNext);

        var strategyStop = definition.GetTask("independent_lineup_strategy_precondition_failed");
        Assert.Equal("Stop", strategyStop.Action, ignoreCase: true);
        Assert.Empty(strategyStop.Next);
        Assert.Empty(strategyStop.OnErrorNext);

        var change = definition.GetTask("independent_strategy_change");
        Assert.Equal("MatchTemplate", change.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", change.Action, ignoreCase: true);
        Assert.Equal("strategy_change.png", Path.GetFileName(change.Template));
        Assert.Equal([620, 600, 250, 180], change.Roi!);

        var strategyFixture = GrayImageCodec.FromFile(Path.Combine(
            root,
            "tests",
            "UmamusumeWpfGui.Tests",
            "Fixtures",
            "strategy_live_current.png"));
        Assert.NotNull(strategyFixture);
        Assert.Equal(900, strategyFixture!.Width);
        Assert.Equal(1600, strategyFixture.Height);
        var strategyTemplateDefinitions = new Dictionary<string, (string Template, int[] Roi)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["front"] = ("strategy_option_front.png", [670,930,120,85]),
            ["pace"] = ("strategy_option_pace.png", [480,930,110,85]),
            ["late"] = ("strategy_option_late.png", [290,930,100,85]),
            ["end"] = ("strategy_option_end.png", [100,930,130,85]),
        };
        var selectedCorner = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "templates",
            "independent",
            "strategy_selected_corner.png"));
        Assert.NotNull(selectedCorner);
        var selectedCornerRois = new Dictionary<string, int[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["front"] = [645,895,60,60],
            ["pace"] = [450,895,60,60],
            ["late"] = [260,895,60,60],
            ["end"] = [70,895,60,60],
        };
        foreach (var (strategy, definitionInfo) in strategyTemplateDefinitions)
        {
            var templatePath = Path.Combine(
                root,
                "resource",
                "hachimi",
                "ura",
                "screens",
                "templates",
                "independent",
                definitionInfo.Template);
            var template = GrayImageCodec.FromFile(templatePath);
            Assert.NotNull(template);
            Assert.True(
                template!.Width > 0 && template.Height > 0,
                $"{strategy} strategy template is empty.");
            var match = TemplateMatcher.Find(
                strategyFixture,
                template,
                definitionInfo.Roi,
                threshold: 0.90,
                referenceWidth: 900,
                referenceHeight: 1600);
            Assert.True(
                match.Found,
                $"{strategy} template did not match the live Strategy dialog: {match.Score:0.000}.");

            var taskPrefix = $"independent_strategy_option_{strategy}";
            var pre = definition.GetTask($"{taskPrefix}_pre");
            var click = definition.GetTask($"{taskPrefix}_click");
            var post = definition.GetTask($"{taskPrefix}_post");
            Assert.Equal("MatchTemplate", pre.Algorithm, ignoreCase: true);
            Assert.Equal("JustReturn", pre.Action, ignoreCase: true);
            Assert.Equal("strategy_selected_corner.png", Path.GetFileName(pre.Template));
            Assert.Equal(selectedCornerRois[strategy], pre.Roi!);
            Assert.True(pre.Success);
            Assert.Equal($"{taskPrefix}_click", Assert.Single(pre.OnErrorNext));
            Assert.Equal("MatchTemplate", click.Algorithm, ignoreCase: true);
            Assert.Equal("ClickSelf", click.Action, ignoreCase: true);
            Assert.Equal(definitionInfo.Template, Path.GetFileName(click.Template));
            Assert.Equal(definitionInfo.Roi, click.Roi!);
            Assert.Equal($"{taskPrefix}_post", Assert.Single(click.Next));
            Assert.Equal("MatchTemplate", post.Algorithm, ignoreCase: true);
            Assert.Equal("JustReturn", post.Action, ignoreCase: true);
            Assert.Equal("strategy_selected_corner.png", Path.GetFileName(post.Template));
            Assert.Equal(selectedCornerRois[strategy], post.Roi!);
            Assert.True(post.Success);
        }

        var selectedMatch = TemplateMatcher.Find(
            strategyFixture,
            selectedCorner!,
            selectedCornerRois["late"],
            threshold: 0.90,
            referenceWidth: 900,
            referenceHeight: 1600);
        Assert.True(selectedMatch.Found);
        foreach (var strategy in new[] { "front", "pace", "end" })
        {
            var notSelectedMatch = TemplateMatcher.Find(
                strategyFixture,
                selectedCorner!,
                selectedCornerRois[strategy],
                threshold: 0.90,
                referenceWidth: 900,
                referenceHeight: 1600);
            Assert.False(
                notSelectedMatch.Found,
                $"Selected-corner marker falsely matched {strategy}: {notSelectedMatch.Score:0.000}.");
        }

        var save = definition.GetTask("independent_strategy_save");
        Assert.Equal("MatchTemplate", save.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", save.Action, ignoreCase: true);
        Assert.Equal("strategy_confirm.png", Path.GetFileName(save.Template));
        Assert.Equal([430, 1070, 440, 220], save.Roi!);

        var returnProbe = definition.GetTask("independent_strategy_return_probe");
        Assert.Equal("MatchTemplate", returnProbe.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", returnProbe.Action, ignoreCase: true);
        Assert.Empty(returnProbe.Next);
        Assert.Empty(returnProbe.OnErrorNext);
        Assert.True(returnProbe.Success);

        using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var actions = profile.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "career_final_confirmation")
            .GetProperty("actions")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("semanticId").GetString()!,
                item => item.GetProperty("task").GetString()!,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "independent_lineup_expand_prepare",
            actions["independent.lineup.expand"]);
        Assert.Equal(
            "independent_lineup_scroll_to_top",
            actions["independent.lineup.scroll.top"]);
        Assert.Equal(
            "independent_lineup_collapse_prepare",
            actions["independent.lineup.collapse"]);
        Assert.Equal(
            "independent_lineup_strategy_precondition",
            actions["independent.lineup.closed.verify"]);
        Assert.Equal(
            "independent_mode_selected_probe",
            actions["independent.select_mode"]);
        Assert.Equal("independent_strategy_change", actions["independent.strategy.change"]);
        Assert.Equal("independent_strategy_save", actions["independent.strategy.save"]);
        Assert.Equal(
            "independent_strategy_option_front_pre",
            actions["independent.strategy.option.front"]);
        Assert.Equal(
            "independent_strategy_option_pace_pre",
            actions["independent.strategy.option.pace"]);
        Assert.Equal(
            "independent_strategy_option_late_pre",
            actions["independent.strategy.option.late"]);
        Assert.Equal(
            "independent_strategy_option_end_pre",
            actions["independent.strategy.option.end"]);

        var pipelineSource = await File.ReadAllTextAsync(
            Path.Combine(
                root,
                "src",
                "UmamusumeWpfGui",
                "Services",
                "Training",
                "AdbCareerTrainingPipeline.cs"));
        var orderMarkers = new[]
        {
            "state.IndependentSkillsConfigured = true;",
            "Independent setup step 6/7 (scroll): returning Lineup Details to the top after its settings.",
            "Independent setup step 6/7 (close): closing the open-down Lineup Details section.",
            "state.IndependentLineupCollapseVerifiedThisRun = true;",
            "Independent setup step 6/7 (verified): Lineup Details closed-right state confirmed.",
            "Independent Strategy gate (verified): closed-right state confirmed; opening Change.",
            "Independent setup strategy: open Change.",
            "Independent setup step 7/7: selecting Strategy",
            "Independent setup strategy: save and return.",
            "var strategyReturnResult",
            "\"independent.start\"",
        };
        var positions = orderMarkers
            .Select(marker => pipelineSource.IndexOf(marker, StringComparison.Ordinal))
            .ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.True(
            positions.SequenceEqual(positions.OrderBy(value => value)),
            string.Join(" -> ", positions));

        var preflightPosition = pipelineSource.IndexOf(
            "TryValidateIndependentTemplateAction(",
            StringComparison.Ordinal);
        var modeGuardPosition = pipelineSource.IndexOf(
            "if (!state.IndependentModeSelected)",
            StringComparison.Ordinal);
        var modeCallPosition = pipelineSource.IndexOf(
            "\"independent.select_mode\"",
            modeGuardPosition,
            StringComparison.Ordinal);
        var lineupCallPosition = pipelineSource.IndexOf(
            "IndependentTrainingCatalog.LineupExpandSemanticAction()",
            modeCallPosition,
            StringComparison.Ordinal);
        Assert.True(preflightPosition >= 0);
        Assert.True(modeGuardPosition > preflightPosition);
        Assert.True(modeCallPosition > modeGuardPosition);
        Assert.True(lineupCallPosition > modeCallPosition);
    }

    [Fact]
    public async Task Lineup_collapse_action_transitions_open_down_to_closed_right()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));
        var connection = new LastVerifiedConnection(
            "adb", "emulator-5554", "android", "test",
            900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        var visual = new LineupStateVisualRuntime(isOpen: true);
        var runner = new HachimiJsonPipelineRunner(
            new UmamusumeWpfGui.Services.AdbRuntime(
                new NoOpAdbRunner(),
                new UmamusumeWpfGui.Helper.AsyncDelay()),
            visual,
            new UmamusumeWpfGui.Services.JsonSettingsService(
                Path.Combine(Path.GetTempPath(), $"lineup-collapse-{Guid.NewGuid():N}.json")));

        var result = await runner.RunAsync(
            connection,
            pack.ExecutionDefinition,
            "independent_lineup_collapse_prepare");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(visual.IsOpen);
        Assert.Equal(
            [
                "independent_lineup_collapse_prepare",
                "independent_lineup_collapsed_probe",
                "independent_lineup_collapse_expanded_probe",
                "independent_lineup_collapse",
                "independent_lineup_collapse_post_probe",
                "independent_lineup_collapse_post_expanded_guard",
            ],
            visual.WaitedTaskNames);
        Assert.Equal(["independent_lineup_collapse"], visual.TappedTaskNames);
    }

    [Fact]
    public async Task Lineup_expand_action_transitions_closed_right_to_open_down()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));
        var connection = new LastVerifiedConnection(
            "adb", "emulator-5554", "android", "test",
            900, 1600, 900, 1600, DateTimeOffset.UtcNow);
        var visual = new LineupStateVisualRuntime(isOpen: false);
        var runner = new HachimiJsonPipelineRunner(
            new UmamusumeWpfGui.Services.AdbRuntime(
                new NoOpAdbRunner(),
                new UmamusumeWpfGui.Helper.AsyncDelay()),
            visual,
            new UmamusumeWpfGui.Services.JsonSettingsService(
                Path.Combine(Path.GetTempPath(), $"lineup-expand-{Guid.NewGuid():N}.json")));

        var result = await runner.RunAsync(
            connection,
            pack.ExecutionDefinition,
            "independent_lineup_expand_prepare");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(visual.IsOpen);
        Assert.Equal(
            [
                "independent_lineup_expand_prepare",
                "independent_lineup_expanded_probe",
                "independent_lineup_expand_collapsed_probe",
                "independent_lineup_expand",
                "independent_lineup_expand_post_probe",
                "independent_lineup_expand_post_collapsed_guard",
            ],
            visual.WaitedTaskNames);
        Assert.Equal(["independent_lineup_expand"], visual.TappedTaskNames);
    }

    [Fact]
    public async Task Strategy_gate_validation_accepts_terminal_stop_and_runner_blocks_open_state()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));

        var strategyPreflightActions = new[]
        {
            IndependentTrainingCatalog.LineupExpandSemanticAction(),
            IndependentTrainingCatalog.LineupCollapseSemanticAction(),
            IndependentTrainingCatalog.LineupClosedVerifySemanticAction(),
            IndependentTrainingCatalog.StrategyChangeSemanticAction(),
            IndependentTrainingCatalog.StrategySaveSemanticAction(),
            IndependentTrainingCatalog.StrategyReturnSemanticAction(),
        };
        foreach (var strategyAction in strategyPreflightActions)
        {
            Assert.True(
                AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                    pack,
                    strategyAction,
                    out var validationError),
                $"{strategyAction}: {validationError}");
        }
        foreach (var strategyValue in StrategyValues)
        {
            Assert.True(
                AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                    pack,
                    $"independent.strategy.option.{strategyValue}",
                    out var validationError),
                $"independent.strategy.option.{strategyValue}: {validationError}");
        }

        var agendaRaceCardVerifyAction =
            IndependentTrainingCatalog.AgendaRaceCardVerifySemanticAction();
        Assert.True(
            AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                pack,
                agendaRaceCardVerifyAction,
                out var agendaRaceCardVerifyError),
            $"{agendaRaceCardVerifyAction}: {agendaRaceCardVerifyError}");

        var finalConfirmation = pack.ScreenProfile.Find("career_final_confirmation");
        Assert.NotNull(finalConfirmation);
        finalConfirmation!.Actions.Add(
            new UraScreenAction
            {
                SemanticId = "test.pure.success",
                Task = "independent_lineup_strategy_precondition_verified",
            });
        Assert.True(
            AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                pack,
                "test.pure.success",
                out var terminalSuccessError),
            terminalSuccessError);

        pack.ExecutionDefinition.Tasks["test.pure.failure"] = new HachimiPipelineTask
        {
            Algorithm = "JustReturn",
            Action = "JustReturn",
        };
        finalConfirmation.Actions.Add(
            new UraScreenAction
            {
                SemanticId = "test.pure.failure",
                Task = "test.pure.failure",
            });
        Assert.False(
            AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                pack,
                "test.pure.failure",
                out _));

        pack.ExecutionDefinition.Tasks["test.pure.success.with.stop"] = new HachimiPipelineTask
        {
            Algorithm = "JustReturn",
            Action = "JustReturn",
            Success = true,
            Next = ["independent_lineup_strategy_precondition_failed"],
        };
        finalConfirmation.Actions.Add(
            new UraScreenAction
            {
                SemanticId = "test.pure.success.with.stop",
                Task = "test.pure.success.with.stop",
            });
        Assert.False(
            AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                pack,
                "test.pure.success.with.stop",
                out _));

        var connection = new UmamusumeWpfGui.Models.LastVerifiedConnection(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);
        var visual = new LineupStateVisualRuntime(isOpen: false);
        var runner = new HachimiJsonPipelineRunner(
            new UmamusumeWpfGui.Services.AdbRuntime(
                new NoOpAdbRunner(),
                new UmamusumeWpfGui.Helper.AsyncDelay()),
            visual,
            new UmamusumeWpfGui.Services.JsonSettingsService(
                Path.Combine(Path.GetTempPath(), "independent-strategy-flow-tests.json")));
        var successLog = new RecordingLogSink();

        var verified = await runner.RunAsync(
            connection,
            pack.ExecutionDefinition,
            "independent_lineup_strategy_precondition",
            logSink: successLog);

        Assert.True(verified.Succeeded, verified.Message);
        Assert.Equal(
            [
                "independent_lineup_strategy_precondition",
                "independent_lineup_strategy_expanded_guard",
            ],
            visual.WaitedTaskNames);
        Assert.Empty(visual.TappedTaskNames);
        Assert.Contains(
            successLog.Entries,
            entry => entry.Details.Contains(
                "following onErrorNext 'independent_lineup_strategy_precondition_verified'",
                StringComparison.Ordinal));

        visual.IsOpen = true;
        visual.WaitedTaskNames.Clear();
        visual.TappedTaskNames.Clear();
        var blockedLog = new RecordingLogSink();
        var blocked = await runner.RunAsync(
            connection,
            pack.ExecutionDefinition,
            "independent_lineup_strategy_precondition",
            logSink: blockedLog);

        Assert.False(blocked.Succeeded);
        Assert.Contains("requested pipeline stop", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [
                "independent_lineup_strategy_precondition",
            ],
            visual.WaitedTaskNames);
        Assert.Empty(visual.TappedTaskNames);
        Assert.Contains(
            blockedLog.Entries,
            entry => entry.Details.Contains(
                "independent_lineup_strategy_precondition_failed",
                StringComparison.Ordinal));
    }

    private sealed class RecordingLogSink : IGrassTaskLogSink
    {
        public List<(string Type, string Details, LogEntryKind Kind)> Entries { get; } = [];

        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Entries.Add((type, details, kind));
    }

    private sealed class NoOpAdbRunner : UmamusumeWpfGui.Helper.IAdbRunner
    {
        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(
            string adbPath) => (string.Empty, string.Empty, 0, false, null);
    }

    private sealed class LineupStateVisualRuntime : IVisualPipelineRuntime
    {
        public LineupStateVisualRuntime(bool isOpen) => IsOpen = isOpen;

        public bool IsOpen { get; set; }

        public List<string> WaitedTaskNames { get; } = [];

        public List<string> TappedTaskNames { get; } = [];

        public Task<UmamusumeWpfGui.Models.GrayImage?> CaptureGrayAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.GrayImage?>(null);

        public Task<UmamusumeWpfGui.Models.GrayImage?> LoadTemplateAsync(
            string? templatePath,
            string baseDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.GrayImage?>(null);

        public Task<UmamusumeWpfGui.Models.TemplateMatchResult?> WaitForMatchAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            CancellationToken cancellationToken = default)
        {
            WaitedTaskNames.Add(taskName);
            var found = taskName.ToLowerInvariant() switch
            {
                "independent_lineup_collapse_prepare" => true,
                "independent_lineup_expand_prepare" => true,
                "independent_lineup_scroll_header_probe" => true,
                "independent_lineup_collapsed_probe" => !IsOpen,
                "independent_lineup_collapse_post_probe" => !IsOpen,
                "independent_lineup_expand_collapsed_guard" => !IsOpen,
                "independent_lineup_expand_collapsed_probe" => !IsOpen,
                "independent_lineup_strategy_precondition" => !IsOpen,
                "independent_lineup_collapse_expanded_guard" => IsOpen,
                "independent_lineup_collapse_expanded_probe" => IsOpen,
                "independent_lineup_collapse_post_expanded_guard" => IsOpen,
                "independent_lineup_expanded_probe" => IsOpen,
                "independent_lineup_expand_post_probe" => IsOpen,
                "independent_lineup_strategy_expanded_guard" => IsOpen,
                "independent_lineup_collapse" => IsOpen,
                "independent_lineup_expand" => !IsOpen,
                _ => false,
            };
            return Task.FromResult<UmamusumeWpfGui.Models.TemplateMatchResult?>(
                new(found, found ? 1d : 0d, 0, 0, 10, 10));
        }

        public Task<UmamusumeWpfGui.Models.TemplateMatchResult?> WaitForMatchScaledAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            IReadOnlyList<double> scaleCandidates,
            CancellationToken cancellationToken = default) =>
            WaitForMatchAsync(
                connection,
                templatePath,
                roi,
                threshold,
                referenceWidth,
                referenceHeight,
                timeoutMilliseconds,
                pollIntervalMilliseconds,
                taskName,
                baseDirectory,
                cancellationToken);

        public Task<UmamusumeWpfGui.Models.TemplateMatchResult?> WaitForMatchInRoisAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string? templatePath,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            IReadOnlyList<int[]> searchRois,
            double minimumScoreGap,
            CancellationToken cancellationToken = default) =>
            WaitForMatchAsync(
                connection,
                templatePath,
                null,
                threshold,
                referenceWidth,
                referenceHeight,
                timeoutMilliseconds,
                pollIntervalMilliseconds,
                taskName,
                baseDirectory,
                cancellationToken);

        public Task<UmamusumeWpfGui.Models.ScreenTextRecognitionResult?> DetectTextAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            int[]? roi,
            int referenceWidth,
            int referenceHeight,
            string? language,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.ScreenTextRecognitionResult?>(null);

        public Task<UmamusumeWpfGui.Models.ScreenTextQueryResult?> FindTextAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string targetText,
            int[]? roi,
            double fuzzyThreshold,
            bool unique,
            int referenceWidth,
            int referenceHeight,
            string? language,
            string taskName,
            string? matchMode = null,
            int groupRowHeight = 0,
            int rowGap = 0,
            bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.ScreenTextQueryResult?>(null);

        public Task<UmamusumeWpfGui.Models.ScreenTextQueryResult?> WaitForTextAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string targetText,
            int[]? roi,
            double fuzzyThreshold,
            bool unique,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string? language,
            string taskName,
            string? matchMode = null,
            int groupRowHeight = 0,
            int rowGap = 0,
            bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.ScreenTextQueryResult?>(null);

        public Task TapTextAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            UmamusumeWpfGui.Models.ScreenTextCandidate match,
            int[]? clickOffset,
            int[]? rowExpansion,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TapMatchAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            UmamusumeWpfGui.Models.TemplateMatchResult match,
            string taskName,
            CancellationToken cancellationToken = default)
        {
            TappedTaskNames.Add(taskName);
            if (taskName.Equals("independent_lineup_collapse", StringComparison.OrdinalIgnoreCase))
                IsOpen = false;
            else if (taskName.Equals("independent_lineup_expand", StringComparison.OrdinalIgnoreCase))
                IsOpen = true;
            return Task.CompletedTask;
        }

        public Task TapAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            int x,
            int y,
            int referenceWidth,
            int referenceHeight,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<UmamusumeWpfGui.Models.HsvColorProbeResult?> ProbeHsvAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            int centerXReference,
            int centerYReference,
            int[]? offsetReference,
            int radiusReference,
            int referenceWidth,
            int referenceHeight,
            double hueMin,
            double hueMax,
            double saturationMin,
            double saturationMax,
            double valueMin,
            double valueMax,
            double minimumMatchRatio,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UmamusumeWpfGui.Models.HsvColorProbeResult?>(null);

        public Task SwipeAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            int[] coordinates,
            int referenceWidth,
            int referenceHeight,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveScreenshotAsync(
            UmamusumeWpfGui.Models.LastVerifiedConnection connection,
            string definitionPath,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            int milliseconds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
