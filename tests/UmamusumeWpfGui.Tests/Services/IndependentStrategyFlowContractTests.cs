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

        var collapsePrepare = definition.GetTask("independent_lineup_collapse_prepare");
        Assert.Equal("lineup_details_header.png", Path.GetFileName(collapsePrepare.Template));
        Assert.Contains("independent_lineup_collapsed_probe", collapsePrepare.Next);

        var collapsedProbe = definition.GetTask("independent_lineup_collapsed_probe");
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(collapsedProbe.Template));
        Assert.Contains("independent_lineup_collapse_expanded_guard", collapsedProbe.Next);
        Assert.Contains("independent_lineup_collapse_expanded_probe", collapsedProbe.OnErrorNext);

        var expandedGuard = definition.GetTask("independent_lineup_collapse_expanded_guard");
        Assert.Contains("independent_lineup_collapse", expandedGuard.Next);
        Assert.Contains("independent_lineup_collapse_already_closed_verified", expandedGuard.OnErrorNext);

        var alreadyClosedVerified = definition.GetTask("independent_lineup_collapse_already_closed_verified");
        Assert.Equal("JustReturn", alreadyClosedVerified.Action, ignoreCase: true);
        Assert.True(alreadyClosedVerified.Success);
        Assert.Empty(alreadyClosedVerified.Next);

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

        var strategyGate = definition.GetTask("independent_lineup_strategy_precondition");
        Assert.Equal("MatchTemplate", strategyGate.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", strategyGate.Action, ignoreCase: true);
        Assert.Equal("lineup_closed_right.png", Path.GetFileName(strategyGate.Template));
        Assert.Equal([760, 390, 130, 150], strategyGate.Roi!);
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

        var option = definition.GetTask("independent_strategy_option");
        Assert.Equal("OcrText", option.Algorithm, ignoreCase: true);
        Assert.Equal("ClickText", option.Action, ignoreCase: true);

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
        Assert.All(
            StrategyValues,
            value => Assert.Equal(
                "independent_strategy_option",
                actions[$"independent.strategy.option.{value}"]));

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
            "Independent setup step 6/7 (scroll): returning Lineup Details to the top after Skills.",
            "Independent setup step 6/7 (locate/click): checking the already-closed probe, locating the expanded arrow, and collapsing Lineup Details.",
            "state.IndependentLineupCollapseVerifiedThisRun = true;",
            "Independent setup step 6/7 (verified): Lineup Details closed-right state was verified",
            "Independent Strategy gate (verified): current-run closed-right state confirmed; opening Change.",
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
            "\"independent.lineup.expand\"",
            modeCallPosition,
            StringComparison.Ordinal);
        Assert.True(preflightPosition >= 0);
        Assert.True(modeGuardPosition > preflightPosition);
        Assert.True(modeCallPosition > modeGuardPosition);
        Assert.True(lineupCallPosition > modeCallPosition);
    }

    [Fact]
    public async Task Strategy_gate_validation_accepts_terminal_stop_and_runner_blocks_expanded_state()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));

        var strategyPreflightActions = new[]
        {
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
        var visual = new StrategyGateVisualRuntime(expandedState: false);
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

        visual.ExpandedState = true;
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
                "independent_lineup_strategy_expanded_guard",
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

    private sealed class StrategyGateVisualRuntime : IVisualPipelineRuntime
    {
        public StrategyGateVisualRuntime(bool expandedState) => ExpandedState = expandedState;

        public bool ExpandedState { get; set; }

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
            var found = taskName.Equals(
                "independent_lineup_strategy_precondition",
                StringComparison.OrdinalIgnoreCase)
                || taskName.Equals(
                    "independent_lineup_strategy_expanded_guard",
                    StringComparison.OrdinalIgnoreCase)
                    && ExpandedState;
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
