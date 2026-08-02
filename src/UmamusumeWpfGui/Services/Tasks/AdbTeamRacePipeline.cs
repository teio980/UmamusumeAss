using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Executes the Team Trials flow observed in the English 900x1600 client.
/// The business sequence remains explicit here; matching, tapping, timing and
/// screenshot handling are shared with other ordinary Hachimi pipelines.
/// </summary>
public sealed class AdbTeamRacePipeline : ITeamRacePipeline
{
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbTeamRacePipeline(IVisualPipelineRuntime visualRuntime)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        _visualRuntime = visualRuntime;
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
                return new TeamRacePipelineResult(false, 0, "A Team Race run is already in progress.");

            _runCancellation = linked;
        }

        try
        {
            var definition = await HachimiPipelineDefinitionLoader.LoadAsync(
                    definitionPath,
                    linked.Token)
                .ConfigureAwait(false);
            if (definition is null)
                return Fail(logSink, "The Team Race definition could not be loaded.");

            var requestedRaces = Math.Clamp(
                raceCount,
                TeamRaceTaskSettingsViewModel.MinimumRaceCount,
                TeamRaceTaskSettingsViewModel.MaximumRaceCount);
            var completed = 0;
            AddLog(logSink, "Team Race", $"Starting {requestedRaces} race(s).");

            await EnterTeamRaceAsync(
                    connection,
                    definition,
                    definitionPath,
                    logSink,
                    linked.Token)
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
                            linked.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await PrepareNextRaceAsync(
                            connection,
                            definition,
                            logSink,
                            linked.Token)
                        .ConfigureAwait(false);
                }

                await RunRaceAsync(
                        connection,
                        definition,
                        race + 1,
                        logSink,
                        linked.Token)
                    .ConfigureAwait(false);
                completed++;

                await TryOpenRandomShopAsync(
                        connection,
                        definition,
                        definitionPath,
                        logSink,
                        linked.Token)
                    .ConfigureAwait(false);

                if (race + 1 < requestedRaces)
                {
                    await ClickStepAsync(
                            connection,
                            definition,
                            definition.GetTask("ResultNext"),
                            "Next race",
                            logSink,
                            linked.Token)
                        .ConfigureAwait(false);
                    await _visualRuntime.DelayAsync(
                            definition.Timing.BetweenRacesMilliseconds,
                            linked.Token)
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
                    _runCancellation = null;
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
        HachimiPipelineDefinition definition,
        string definitionPath,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await _visualRuntime.SaveScreenshotAsync(
                connection,
                definitionPath,
                "before_team_race",
                cancellationToken)
            .ConfigureAwait(false);

        await ClickStepAsync(connection, definition, definition.GetTask("RaceTab"), "Race tab", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("TeamTrials"), "Team Trials", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("TeamRace"), "Team Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("Opponent"), "Opponent", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.TeamDownloadMilliseconds, cancellationToken).ConfigureAwait(false);

        AddLog(logSink, "Team Race", "Opened Team Race and selected an opponent.", LogEntryKind.Success);
    }

    private async Task PrepareFirstRaceAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await ClickStepAsync(connection, definition, definition.GetTask("MatchupNext"), "Matchup next", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);

        // The first run shows an optional item dialog. Tapping Race is safe on
        // the English client and returns to the matchup screen when no item is
        // selected.
        await ClickStepAsync(connection, definition, definition.GetTask("ItemRace"), "Item dialog Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("FirstUma"), "First Uma", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", "Selected the first Uma Musume.");
    }

    private async Task PrepareNextRaceAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await ClickStepAsync(connection, definition, definition.GetTask("ViewRace"), "View Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NextRaceLoadMilliseconds, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", "Loaded the next race and selected its Uma Musume.");
    }

    private async Task RunRaceAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        int raceNumber,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        await ClickStepAsync(connection, definition, definition.GetTask("DetailRace"), "Race detail", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("PlaybackOk"), "Playback OK", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.PlaybackLoadMilliseconds, cancellationToken).ConfigureAwait(false);
        await ClickStepAsync(connection, definition, definition.GetTask("PlaybackStart"), "Playback Race", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);

        await TryClickStepAsync(connection, definition, definition.GetTask("PlaybackSkip"), "Playback skip", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.SkipSettleMilliseconds, cancellationToken).ConfigureAwait(false);
        await TryClickStepAsync(connection, definition, definition.GetTask("PlaybackSpeed"), "Playback speed", logSink, cancellationToken)
            .ConfigureAwait(false);

        var resultMatch = await WaitForStepAsync(
                connection,
                definition,
                definition.GetTask("RaceResult"),
                $"Race {raceNumber} result",
                cancellationToken,
                definition.Templates.RaceResult,
                definition.Timing.RaceTimeoutMilliseconds)
            .ConfigureAwait(false);
        if (resultMatch is null)
            throw new InvalidOperationException($"Timed out waiting for the result of race {raceNumber}.");

        await ClickStepAsync(connection, definition, definition.GetTask("ResultClose"), "Result close", logSink, cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        AddLog(logSink, "Team Race", $"Race {raceNumber} finished.", LogEntryKind.Success);
    }

    private async Task TryOpenRandomShopAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string definitionPath,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Templates.RandomShop)
            || !definition.TryGetTask("RandomShop", out var shopTask)
            || shopTask is null)
        {
            return;
        }

        if (!await TryClickStepAsync(
                connection,
                definition,
                shopTask,
                "Random shop",
                logSink,
                cancellationToken,
                definition.Templates.RandomShop,
                definition.Timing.ShopProbeMilliseconds)
            .ConfigureAwait(false))
        {
            return;
        }

        await _visualRuntime.DelayAsync(definition.Timing.NavigationMilliseconds, cancellationToken).ConfigureAwait(false);
        await _visualRuntime.SaveScreenshotAsync(connection, definitionPath, "random_shop", cancellationToken)
            .ConfigureAwait(false);

        if (definition.TryGetTask("RandomShopClose", out var closeTask)
            && closeTask is not null)
        {
            await ClickStepAsync(connection, definition, closeTask, "Random shop close", logSink, cancellationToken)
                .ConfigureAwait(false);
        }

        AddLog(logSink, "Team Race", "Random shop detected and opened.", LogEntryKind.Success);
    }

    private async Task ClickStepAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        HachimiPipelineTask task,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForStepAsync(
                connection,
                definition,
                task,
                taskName,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
            throw new InvalidOperationException($"Timed out waiting for Team Race button '{taskName}'.");

        await _visualRuntime.TapMatchAsync(connection, match, taskName, cancellationToken).ConfigureAwait(false);
        AddLog(
            logSink,
            "Team Race",
            $"Clicked {taskName} by template at ({match.CenterX},{match.CenterY}), score={match.Score:0.000}.",
            LogEntryKind.Success);
    }

    private async Task<bool> TryClickStepAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        HachimiPipelineTask task,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        string? templateOverride = null,
        int? timeoutOverride = null)
    {
        var match = await WaitForStepAsync(
                connection,
                definition,
                task,
                taskName,
                cancellationToken,
                templateOverride,
                timeoutOverride)
            .ConfigureAwait(false);
        if (match is null)
        {
            AddLog(logSink, "Team Race", $"Optional button '{taskName}' was not visible.");
            return false;
        }

        await _visualRuntime.TapMatchAsync(connection, match, taskName, cancellationToken).ConfigureAwait(false);
        AddLog(
            logSink,
            "Team Race",
            $"Clicked optional {taskName} by template at ({match.CenterX},{match.CenterY}), score={match.Score:0.000}.",
            LogEntryKind.Success);
        return true;
    }

    private Task<TemplateMatchResult?> WaitForStepAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        HachimiPipelineTask task,
        string taskName,
        CancellationToken cancellationToken,
        string? templateOverride = null,
        int? timeoutOverride = null)
    {
        var template = templateOverride ?? task.Template;
        var timeout = timeoutOverride ?? task.TimeoutMilliseconds;
        var poll = task.PollIntervalMilliseconds > 0
            ? task.PollIntervalMilliseconds
            : definition.Timing.PollIntervalMilliseconds;
        return _visualRuntime.WaitForMatchAsync(
            connection,
            template,
            task.Roi,
            task.TemplateThreshold,
            definition.ReferenceWidth,
            definition.ReferenceHeight,
            timeout,
            poll,
            taskName,
            definition.BaseDirectory,
            cancellationToken);
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
}
