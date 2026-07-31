using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Executes the Team Trials flow observed in the English 900x1600 client.
/// The navigation points live in team_race.json so a client layout change does
/// not require changing the executor itself.
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

            var requestedRaces = Math.Clamp(raceCount, 1, 999);
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
                    await TapAsync(connection, definition.Coordinates.ResultNext, linked.Token)
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

        await TapAsync(connection, definition.Coordinates.RaceTab, cancellationToken).ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.TeamTrials, cancellationToken).ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.TeamRace, cancellationToken).ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.Opponent, cancellationToken).ConfigureAwait(false);
        await DelayAsync(definition.Timing.TeamDownloadMs, cancellationToken).ConfigureAwait(false);

        AddLog(logSink, "Team Race", "Opened Team Race and selected an opponent.", LogEntryKind.Success);
    }

    private async Task PrepareFirstRaceAsync(
        LastVerifiedConnection connection,
        TeamRaceDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await TapAsync(connection, definition.Coordinates.MatchupNext, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);

        // The first run shows an optional item dialog. Tapping Race is safe on
        // the English client and returns to the matchup screen when no item is
        // selected.
        await TapAsync(connection, definition.Coordinates.ItemRace, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.FirstUma, cancellationToken)
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
        await TapAsync(connection, definition.Coordinates.ViewRace, cancellationToken)
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
        await TapAsync(connection, definition.Coordinates.DetailRace, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.PlaybackOk, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.PlaybackLoadMs, cancellationToken).ConfigureAwait(false);

        // Participant list -> actual playback.
        await TapAsync(connection, definition.Coordinates.PlaybackStart, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);

        // Skip the presentation when the control is available, then enable 2x
        // playback. Both taps are harmless if the client has already advanced.
        await TapAsync(connection, definition.Coordinates.PlaybackSkip, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.SkipSettleMs, cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.PlaybackSpeed, cancellationToken)
            .ConfigureAwait(false);

        var resultTemplate = await LoadTemplateAsync(
            definition.Templates.RaceResult,
            definitionPath: definition.BaseDirectory,
            cancellationToken).ConfigureAwait(false);
        var found = await WaitForTemplateAsync(
            connection,
            resultTemplate,
            definition.Timing.RaceTimeoutMs,
            definition.Timing.PollIntervalMs,
            cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            throw new InvalidOperationException($"Timed out waiting for the result of race {raceNumber}.");
        }

        await SaveScreenshotAsync(
            connection,
            definition.DebugDirectory,
            $"race_{raceNumber}_result",
            cancellationToken).ConfigureAwait(false);
        await TapAsync(connection, definition.Coordinates.ResultClose, cancellationToken)
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
            || definition.Coordinates.RandomShop is not { Length: >= 2 })
        {
            return;
        }

        var template = await LoadTemplateAsync(
            definition.Templates.RandomShop,
            definition.BaseDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!await WaitForTemplateAsync(
                connection,
                template,
                definition.Timing.ShopProbeMs,
                definition.Timing.PollIntervalMs,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await TapAsync(connection, definition.Coordinates.RandomShop, cancellationToken)
            .ConfigureAwait(false);
        await DelayAsync(definition.Timing.NavigationMs, cancellationToken).ConfigureAwait(false);
        await SaveScreenshotAsync(
            connection,
            definitionPath,
            "random_shop",
            cancellationToken).ConfigureAwait(false);
        if (definition.Coordinates.RandomShopClose is { Length: >= 2 })
        {
            await TapAsync(connection, definition.Coordinates.RandomShopClose, cancellationToken)
                .ConfigureAwait(false);
        }

        AddLog(logSink, "Team Race", "Random shop detected and opened.", LogEntryKind.Success);
    }

    private async Task<bool> WaitForTemplateAsync(
        LastVerifiedConnection connection,
        GrayImage? template,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        if (template is null)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await CaptureScreenshotAsync(connection, cancellationToken).ConfigureAwait(false);
            var screen = screenshot is null ? null : GrayImageCodec.FromScreenshot(screenshot);
            if (screen is not null)
            {
                var match = TemplateMatcher.Find(
                    screen,
                    template,
                    roi: null,
                    threshold: 0.80,
                    referenceWidth: screen.Width,
                    referenceHeight: screen.Height);
                if (match.Found)
                {
                    return true;
                }
            }

            await DelayAsync(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<GrayImage?> LoadTemplateAsync(
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

    private async Task TapAsync(
        LastVerifiedConnection connection,
        int[] point,
        CancellationToken cancellationToken)
    {
        if (point is not { Length: >= 2 })
        {
            throw new InvalidOperationException("A Team Race tap point is missing.");
        }

        var x = Scale(point[0], connection.Width, 900);
        var y = Scale(point[1], connection.Height, 1600);
        var result = await _adbRuntime.TapAsync(
            connection.AdbPath,
            connection.Serial,
            x,
            y,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ADB tap failed: {result.Stderr}");
        }
    }

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        _asyncDelay.DelayAsync(
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)),
            cancellationToken);

    private static int Scale(int value, int actual, int reference) =>
        (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));

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
        [JsonPropertyName("templates")]
        public TeamRaceTemplates Templates { get; set; } = new();

        [JsonPropertyName("coordinates")]
        public TeamRaceCoordinates Coordinates { get; set; } = new();

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

    private sealed class TeamRaceCoordinates
    {
        public int[] RaceTab { get; set; } = [675, 1535];
        public int[] TeamTrials { get; set; } = [270, 1060];
        public int[] TeamRace { get; set; } = [450, 1035];
        public int[] Opponent { get; set; } = [450, 780];
        public int[] MatchupNext { get; set; } = [450, 1350];
        public int[] ItemRace { get; set; } = [650, 1135];
        public int[] FirstUma { get; set; } = [110, 880];
        public int[] ViewRace { get; set; } = [330, 1470];
        public int[] DetailRace { get; set; } = [450, 1470];
        public int[] PlaybackOk { get; set; } = [650, 1040];
        public int[] PlaybackStart { get; set; } = [450, 1470];
        public int[] PlaybackSkip { get; set; } = [700, 1535];
        public int[] PlaybackSpeed { get; set; } = [335, 1535];
        public int[] ResultClose { get; set; } = [450, 1150];
        public int[] ResultNext { get; set; } = [450, 1470];
        public int[]? RandomShop { get; set; }
        public int[]? RandomShopClose { get; set; }
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
