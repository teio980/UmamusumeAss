using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Executes the Team Trials flow observed in the English 900x1600 client.
/// Every interaction is an ROI-scoped template match followed by a click at
/// the matched rectangle, following MAA's MatchTemplate/ClickSelf model.
/// </summary>
public sealed class AdbTeamRacePipeline : ITeamRacePipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAdbRuntime _adbRuntime;
    private readonly IAsyncDelay _asyncDelay;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbTeamRacePipeline(IAdbRuntime adbRuntime, IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
    }

    public async Task<TeamRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        int raceCount,
        bool stopWhenTicketsEmpty,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
            {
                return new TeamRacePipelineResult(false, 0, "A Team Race run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            var definition = await LoadDefinitionAsync(definitionPath, linked.Token).ConfigureAwait(false);
            if (definition is null)
            {
                return Fail(logSink, "The Team Race definition could not be loaded.");
            }

            var requestedRaces = Math.Clamp(
                raceCount,
                TeamRaceTaskSettingsViewModel.MinimumRaceCount,
                TeamRaceTaskSettingsViewModel.MaximumRaceCount);
            var completed = 0;
            AddLog(logSink, "Team Race", $"Starting {requestedRaces} race(s).");

            await EnterTeamRaceAsync(connection, definition, definitionPath, logSink, linked.Token)
                .ConfigureAwait(false);

            for (var race = 0; race < requestedRaces; race++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (race == 0)
                {
                    await PrepareFirstRaceAsync(
                        connection,
                        definition,
                        logSink,
                        linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await PrepareNextRaceAsync(
                        connection,
                        definition,
                        logSink,
                        linked.Token).ConfigureAwait(false);
                }

                await RunRaceAsync(
                    connection,
                    definition,
                    race + 1,
                    logSink,
                    linked.Token).ConfigureAwait(false);
                completed++;

                await TryOpenRandomShopAsync(
                    connection,
                    definition,
                    definitionPath,
                    logSink,
                    linked.Token).ConfigureAwait(false);

                if (race + 1 < requestedRaces)
                {
                    await ClickStepAsync(
                            connection,
                            definition,
                            definition.Steps.ResultNext,
                            "Next race",
                            logSink,
                            linked.Token)
                        .ConfigureAwait(false);
                    await DelayAsync(definition.Timing.BetweenRacesMs, linked.Token)
                        .ConfigureAwait(false);
                }
            }

            AddLog(logSink, "Team Race", $"Completed {completed} race(s).", LogEntryKind.Success);
            return new TeamRacePipelineResult(true, completed, $"Completed {completed} Team Race race(s).");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Team Race", "Team Race was stopped.", LogEntryKind.Failure);
            return new TeamRacePipelineResult(false, 0, "Team Race was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, "Team Race", ex.Message, LogEntryKind.Failure);
            return new TeamRacePipelineResult(false, 0, $"Team Race failed: {ex.Message}");
        }
        finally
        {
            lock (_runLock)
            {
                if (ReferenceEquals(_runCancellation, linked))
                {
                    _runCancellation = null;
                }
            }
        }
    }

    public Task<TeamRacePipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        AddLog(logSink, "Team Race", "Stop requested.");
        return Task.FromResult(new TeamRacePipelineResult(true, 0, "Stop requested."));
    }

    private async Task EnterTeamRaceAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        string definitionPath,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await SaveScreenshotAsync(
            connection,
            definitionPath,
            "before_team_race",
            cancellationToken).ConfigureAwait(false);

        await ClickStepAsync(connection, definition, definition.Steps.RaceTab, "Race tab", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.Steps.TeamTrials, "Team Trials", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.Steps.TeamRace, "Team Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.Steps.Opponent, "Opponent", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.TeamDownloadMs, cancellationToken).ConfigureAwait(false);

        AddLog(logSink, "Team Race", "Opened Team Race and selected an opponent.", LogEntryKind.Success);
    }

    private async Task PrepareFirstRaceAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await ClickStepAsync(connection, definition, definition.Steps.MatchupNext, "Matchup next", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);

        // The first run shows an optional item dialog. Tapping Race is safe on
        // the English client and returns to the matchup screen when no item is
        // selected.
        await ClickStepAsync(connection, definition, definition.Steps.ItemRace, "Item dialog Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.Steps.FirstUma, "First Uma", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", "Selected the first Uma Musume.");
    }

    private async Task PrepareNextRaceAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await ClickStepAsync(connection, definition, definition.Steps.ViewRace, "View Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NextRaceLoadMs, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", "Loaded the next race and selected its Uma Musume.");
    }

    private async Task RunRaceAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        int raceNumber,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        // Detail page -> playback settings dialog.
        await ClickStepAsync(connection, definition, definition.Steps.DetailRace, "Race detail", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.Steps.PlaybackOk, "Playback OK", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.PlaybackLoadMs, cancellationToken).ConfigureAwait(false);

        // Participant list -> actual playback.
        await ClickStepAsync(connection, definition, definition.Steps.PlaybackStart, "Playback Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);

        // Skip the presentation when the control is available, then enable 2x
        // playback. Both taps are harmless if the client has already advanced.
        await TryClickStepAsync(connection, definition, definition.Steps.PlaybackSkip, "Playback skip", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.SkipSettleMs, cancellationToken).ConfigureAwait(false);
        await TryClickStepAsync(connection, definition, definition.Steps.PlaybackSpeed, "Playback speed", logSink, cancellationToken)
            .ConfigureAwait(false);

        var resultStep = definition.Steps.RaceResult with
        {
            Template = definition.Templates.RaceResult,
            TimeoutMs = definition.Timing.RaceTimeoutMs,
        };
        var resultMatch = await WaitForStepAsync(
                connection,
                definition,
                resultStep,
                $"Race {raceNumber} result",
                cancellationToken)
            .ConfigureAwait(false);
        if (resultMatch is null)
        {
            throw new InvalidOperationException($"Timed out waiting for the result of race {raceNumber}.");
        }

        await ClickStepAsync(connection, definition, definition.Steps.ResultClose, "Result close", logSink, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", $"Race {raceNumber} finished.", LogEntryKind.Success);
    }

    private async Task TryOpenRandomShopAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        string definitionPath,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Templates.RandomShop)
            || definition.Steps.RandomShop is null)
        {
            return;
        }

        var shopStep = definition.Steps.RandomShop with
        {
            Template = definition.Templates.RandomShop,
            TimeoutMs = definition.Timing.ShopProbeMs,
        };
        if (!await TryClickStepAsync(
                connection,
                definition,
                shopStep,
                "Random shop",
                logSink,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await SaveScreenshotAsync(
            connection,
            definitionPath,
            "random_shop",
            cancellationToken).ConfigureAwait(false);
        if (definition.Steps.RandomShopClose is not null)
        {
            await ClickStepAsync(
                    connection,
                    definition,
                    definition.Steps.RandomShopClose,
                    "Random shop close",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        AddLog(logSink, "Team Race", "Random shop detected and opened.", LogEntryKind.Success);
    }

    private async Task ClickStepAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        TeamRaceStepDefinition step,
        string stepName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForStepAsync(
                connection,
                definition,
                step,
                stepName,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            throw new InvalidOperationException($"Timed out waiting for Team Race button '{stepName}'.");
        }

        await TapMatchAsync(connection, match, stepName, cancellationToken).ConfigureAwait(false);
        AddLog(
            logSink,
            "Team Race",
            $"Clicked {stepName} by template at ({match.CenterX},{match.CenterY}), score={match.Score:0.000}.",
            LogEntryKind.Success);
    }

    private async Task<bool> TryClickStepAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        TeamRaceStepDefinition step,
        string stepName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForStepAsync(
                connection,
                definition,
                step,
                stepName,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            AddLog(logSink, "Team Race", $"Optional button '{stepName}' was not visible.");
            return false;
        }

        await TapMatchAsync(connection, match, stepName, cancellationToken).ConfigureAwait(false);
        AddLog(
            logSink,
            "Team Race",
            $"Clicked optional {stepName} by template at ({match.CenterX},{match.CenterY}), score={match.Score:0.000}.",
            LogEntryKind.Success);
        return true;
    }

    private async Task<TemplateMatchResult?> WaitForStepAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        TeamRaceStepDefinition step,
        string stepName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Template))
            throw new InvalidOperationException($"Team Race button '{stepName}' has no template.");

        var template = await LoadTemplateAsync(
                step.Template,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
            throw new InvalidOperationException($"The template for Team Race button '{stepName}' could not be loaded.");

        var timeoutMs = Math.Clamp(step.TimeoutMs, 0, 10 * 60 * 1000);
        var pollIntervalMs = Math.Clamp(
            step.PollIntervalMs > 0 ? step.PollIntervalMs : definition.Timing.PollIntervalMs,
            50,
            10_000);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await CaptureScreenshotAsync(connection, cancellationToken).ConfigureAwait(false);
            var screen = screenshot is null ? null : GrayImageCodec.FromScreenshot(screenshot);
            if (screen is not null)
            {
                var match = TemplateMatcher.Find(
                    screen,
                    template,
                    step.Roi,
                    step.Threshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight);
                if (match.Found)
                    return match;
            }

            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(timeoutMs))
                return null;

            await DelayAsync(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        string stepName,
        CancellationToken cancellationToken)
    {
        var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                match.CenterX,
                match.CenterY,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"ADB template click failed for '{stepName}': {result.Stderr}");
    }

    private static async Task<GrayImage?> LoadTemplateAsync(
        string? templatePath,
        string definitionPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            return null;
        }

        var fullPath = Path.IsPathRooted(templatePath)
            ? templatePath
            : Path.Combine(definitionPath, templatePath);
        return await Task.Run(() => GrayImageCodec.FromFile(fullPath), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AdbScreenshotResult?> CaptureScreenshotAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        var raw = await _adbRuntime.DecodeRawScreenshotAsync(
            connection.AdbPath,
            connection.Serial,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return raw.Value is { } decoded
            ? new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero, decoded)
            : null;
    }

    private async Task SaveScreenshotAsync(
        LastVerifiedConnection connection,
        string directoryOrDefinitionPath,
        string name,
        CancellationToken cancellationToken)
    {
        var screenshot = await CaptureScreenshotAsync(connection, cancellationToken).ConfigureAwait(false);
        if (screenshot is null)
        {
            return;
        }

        var directory = Directory.Exists(directoryOrDefinitionPath)
            ? directoryOrDefinitionPath
            : Path.Combine(Path.GetDirectoryName(directoryOrDefinitionPath) ?? AppContext.BaseDirectory, "debug");
        var path = Path.Combine(directory, $"{name}.png");
        await Task.Run(() => GrayImageCodec.SaveScreenshot(screenshot, path), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        _asyncDelay.DelayAsync(
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)),
            cancellationToken);

    private static async Task<TeamRaceDefinition?> LoadDefinitionAsync(
        string definitionPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(definitionPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(definitionPath);
            var definition = await JsonSerializer.DeserializeAsync<TeamRaceDefinition>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (definition is not null)
            {
                definition.BaseDirectory = Path.GetDirectoryName(definitionPath) ?? AppContext.BaseDirectory;
                definition.DebugDirectory = Path.Combine(definition.BaseDirectory, "debug");
            }

            return definition;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static TeamRacePipelineResult Fail(IGrassTaskLogSink? logSink, string message)
    {
        AddLog(logSink, "Team Race", message, LogEntryKind.Failure);
        return new TeamRacePipelineResult(false, 0, message);
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string type,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add(type, details, kind);

    private sealed class TeamRaceDefinition
    {
        [JsonPropertyName("referenceWidth")]
        public int ReferenceWidth { get; set; } = 900;

        [JsonPropertyName("referenceHeight")]
        public int ReferenceHeight { get; set; } = 1600;

        [JsonPropertyName("templates")]
        public TeamRaceTemplates Templates { get; set; } = new();

        [JsonPropertyName("steps")]
        public TeamRaceSteps Steps { get; set; } = new();

        [JsonPropertyName("timing")]
        public TeamRaceTiming Timing { get; set; } = new();

        [JsonIgnore]
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        [JsonIgnore]
        public string DebugDirectory { get; set; } = AppContext.BaseDirectory;
    }

    private sealed class TeamRaceTemplates
    {
        public string? RaceResult { get; set; }
        public string? RandomShop { get; set; }
    }

    private sealed class TeamRaceSteps
    {
        public TeamRaceStepDefinition RaceTab { get; set; } = new();
        public TeamRaceStepDefinition TeamTrials { get; set; } = new();
        public TeamRaceStepDefinition TeamRace { get; set; } = new();
        public TeamRaceStepDefinition Opponent { get; set; } = new();
        public TeamRaceStepDefinition MatchupNext { get; set; } = new();
        public TeamRaceStepDefinition ItemRace { get; set; } = new();
        public TeamRaceStepDefinition FirstUma { get; set; } = new();
        public TeamRaceStepDefinition ViewRace { get; set; } = new();
        public TeamRaceStepDefinition DetailRace { get; set; } = new();
        public TeamRaceStepDefinition PlaybackOk { get; set; } = new();
        public TeamRaceStepDefinition PlaybackStart { get; set; } = new();
        public TeamRaceStepDefinition PlaybackSkip { get; set; } = new();
        public TeamRaceStepDefinition PlaybackSpeed { get; set; } = new();
        public TeamRaceStepDefinition RaceResult { get; set; } = new();
        public TeamRaceStepDefinition ResultClose { get; set; } = new();
        public TeamRaceStepDefinition ResultNext { get; set; } = new();
        public TeamRaceStepDefinition? RandomShop { get; set; }
        public TeamRaceStepDefinition? RandomShopClose { get; set; }
    }

    private sealed record TeamRaceStepDefinition
    {
        public string? Template { get; init; }
        public int[]? Roi { get; init; }
        public double Threshold { get; init; } = 0.86;
        public int TimeoutMs { get; init; } = 20_000;
        public int PollIntervalMs { get; init; }
    }

    private sealed class TeamRaceTiming
    {
        public int NavigationMs { get; set; } = 1200;
        public int TeamDownloadMs { get; set; } = 10_000;
        public int NextRaceLoadMs { get; set; } = 10_000;
        public int PlaybackLoadMs { get; set; } = 20_000;
        public int SkipSettleMs { get; set; } = 2500;
        public int RaceTimeoutMs { get; set; } = 60_000;
        public int ShopProbeMs { get; set; } = 1500;
        public int PollIntervalMs { get; set; } = 1000;
        public int BetweenRacesMs { get; set; } = 1200;
    }
}
