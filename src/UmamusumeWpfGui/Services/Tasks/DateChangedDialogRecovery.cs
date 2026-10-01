using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

// Only automation call chains participate. Developer screenshots and connection
// probes remain observational, even when they run alongside an active task.
internal sealed class GameAutomationScope : IDisposable
{
    private static readonly AsyncLocal<GameAutomationScope?> Active = new();
    private readonly GameAutomationScope? _previous;
    internal static GameAutomationScope? Current => Active.Value;
    internal IGrassTaskLogSink? LogSink { get; }
    internal IHachimiTaskLogSink? TaskLogSink { get; }
    internal LastVerifiedConnection? Connection { get; }
    internal bool Recovering { get; set; }
    internal int Generation { get; set; }
    internal bool IndependentStartSubmitted { get; set; }
    internal HashSet<string> ConfirmedShopExchanges { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> PendingShopExchanges { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<HachimiTaskLogProfile, int> PendingRaceUnits { get; } = new();
    internal Dictionary<HachimiTaskLogProfile, int> ConfirmedRaceUnits { get; } = new();

    internal GameAutomationScope(IGrassTaskLogSink? logSink, IHachimiTaskLogSink? taskLogSink,
        LastVerifiedConnection? connection = null)
    {
        _previous = Active.Value;
        LogSink = logSink;
        TaskLogSink = taskLogSink;
        Connection = connection;
        Active.Value = this;
    }

    internal void Log(string message, LogEntryKind kind = LogEntryKind.Info)
    {
        LogSink?.Add("Date Changed Recovery", message, kind);
        TaskLogSink?.Add("Recovery", message, kind == LogEntryKind.Failure
            ? HachimiTaskLogEventKind.Failure : HachimiTaskLogEventKind.Info);
    }

    public void Dispose() => Active.Value = _previous;
}

internal sealed class DateChangedInterruptionException : Exception
{
    internal DateChangedInterruptionException() : base("Date Changed interrupted the current task.") { }
}

public sealed class DateChangedRecoveryException : Exception
{
    internal DateChangedRecoveryException(string message) : base(message) { }
}

/// <summary>Detects the specific daily-reset modal before automation uses a frame.</summary>
public sealed class DateChangedDialogGuard
{
    private const string Root = "resource/hachimi/common/date_changed/";
    private readonly IAdbRuntime _adb;
    private readonly IAsyncDelay _delay;
    private readonly ConcurrentDictionary<string, GrayImage> _templates = new();

    public DateChangedDialogGuard(IAdbRuntime adb, IAsyncDelay delay)
    {
        _adb = adb;
        _delay = delay;
    }

    internal TemplateMatchResult? Detect(GrayImage frame)
    {
        if ((frame.Width != 900 || frame.Height != 1600) && frame.RgbaPixels is { } pixels)
        {
            using var resized = Image.LoadPixelData<Rgba32>(pixels, frame.Width, frame.Height);
            resized.Mutate(operation => operation.Resize(900, 1600));
            var bytes = new byte[900 * 1600 * 4];
            resized.CopyPixelDataTo(bytes);
            var normalized = GrayImageCodec.FromScreenshot(new AdbScreenshotResult(
                AdbScreenshotMethod.Raw, [], TimeSpan.Zero, new AdbRawScreenshot(900, 1600, bytes)));
            var match = normalized is null ? null : Detect(normalized);
            return match is null ? null : new TemplateMatchResult(true, match.Score,
                (int)Math.Round((match.CenterX - match.Width / 2d) * frame.Width / 900d),
                (int)Math.Round((match.CenterY - match.Height / 2d) * frame.Height / 1600d),
                (int)Math.Round(match.Width * frame.Width / 900d),
                (int)Math.Round(match.Height * frame.Height / 1600d));
        }
        GrayImage Load(string name) => _templates.GetOrAdd(ResourcePathRuntime.Resolve(Root + name),
            path => GrayImageCodec.FromFile(path)
                ?? throw new DateChangedRecoveryException("Date Changed recognition material is missing: " + name));
        // Tight text crops retain their backgrounds. Grayscale correlation
        // measures the lettering; the complete colored button is a second gate.
        if (!TemplateMatcher.Find(frame, Load("title.png"), [310, 475, 280, 80], .92, 900, 1600, candidateStepOverride: 1).Found
            || !TemplateMatcher.Find(frame, Load("body.png"), [325, 730, 250, 85], .90, 900, 1600, candidateStepOverride: 1).Found)
            return null;
        var button = TemplateMatcher.FindColor(frame, Load("ok.png"),
            [250, 975, 400, 130], .92, 900, 1600);
        return button.Found ? button : null;
    }

    internal async Task<GrayImage?> CaptureAsync(LastVerifiedConnection connection, CancellationToken token)
    {
        var raw = await _adb.DecodeRawScreenshotAsync(connection.AdbPath, connection.Serial,
            cancellationToken: token).ConfigureAwait(false);
        return raw.Value is { } decoded
            ? GrayImageCodec.FromScreenshot(new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero, decoded))
            : null;
    }

    internal async Task<bool> InspectAsync(LastVerifiedConnection connection, GrayImage? frame,
        CancellationToken token)
    {
        var scope = GameAutomationScope.Current;
        if (scope is null || frame is null || Detect(frame) is null)
            return false;
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(120), token).ConfigureAwait(false);
        var confirmation = await CaptureAsync(connection, token).ConfigureAwait(false);
        if (confirmation is null || Detect(confirmation) is null)
            return false;
        scope.Log("Date Changed detected; interrupting the current step.");
        if (!scope.Recovering)
            throw new DateChangedInterruptionException();
        await DismissAsync(connection, token).ConfigureAwait(false);
        scope.Generation++;
        return true;
    }

    internal async Task CheckBeforeInputAsync(LastVerifiedConnection connection, CancellationToken token)
    {
        if (GameAutomationScope.Current is not null)
        {
            if (await InspectAsync(connection, await CaptureAsync(connection, token).ConfigureAwait(false), token)
                .ConfigureAwait(false))
                throw new DateChangedInterruptionException(); // Restart startup recognition, never reuse this tap.
        }
    }

    internal async Task DelayAsync(LastVerifiedConnection connection, TimeSpan duration, CancellationToken token)
    {
        // Long navigation/loading delays must still notice daily resets. Short
        // polling delays already lead straight to another guarded screenshot.
        while (duration > TimeSpan.FromSeconds(1))
        {
            await _delay.DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            await CheckBeforeInputAsync(connection, token).ConfigureAwait(false);
            duration -= TimeSpan.FromSeconds(1);
        }
        await _delay.DelayAsync(duration, token).ConfigureAwait(false);
    }

    internal async Task DismissAsync(LastVerifiedConnection connection, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var boundedToken = deadline.Token;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var taps = 0;
            var seen = false;
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                boundedToken.ThrowIfCancellationRequested();
                var frame = await CaptureAsync(connection, boundedToken).ConfigureAwait(false);
                var button = frame is null ? null : Detect(frame);
                if (frame is not null && button is null)
                {
                    if (seen)
                        GameAutomationScope.Current?.Log("Date Changed OK disappeared; starting Home recovery.");
                    return;
                }
                if (button is not null && taps < 3)
                {
                    seen = true;
                    var tap = await _adb.TapAsync(connection.AdbPath, connection.Serial,
                        button.CenterX, button.CenterY, cancellationToken: boundedToken).ConfigureAwait(false);
                    if (tap.Error is not null || tap.TimedOut || tap.ExitCode != 0)
                        throw new DateChangedRecoveryException("Date Changed recovery failed while clicking OK: " + tap.Stderr);
                    taps++;
                    await _delay.DelayAsync(TimeSpan.FromMilliseconds(1000), boundedToken).ConfigureAwait(false);
                }
                else
                    await _delay.DelayAsync(TimeSpan.FromMilliseconds(250), boundedToken).ConfigureAwait(false);
            }
            throw new DateChangedRecoveryException("Date Changed OK did not disappear within ten seconds (at most three taps).");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new DateChangedRecoveryException("Date Changed OK did not disappear within ten seconds (at most three taps).");
        }
    }
}

/// <summary>Runs the existing startup workflow before the owning task resumes.</summary>
public sealed class DateChangedDialogRecovery
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks = new();
    private readonly DateChangedDialogGuard _guard;
    private readonly IGameLauncher _launcher;
    private readonly IStartGamePipeline _startup;
    private readonly ISettingsService _settings;
    internal DateChangedDialogGuard Guard => _guard;

    public DateChangedDialogRecovery(DateChangedDialogGuard guard, IGameLauncher launcher,
        IStartGamePipeline startup, ISettingsService settings)
    {
        _guard = guard;
        _launcher = launcher;
        _startup = startup;
        _settings = settings;
    }

    internal async Task RecoverAsync(LastVerifiedConnection connection, CancellationToken token)
    {
        var scope = GameAutomationScope.Current
            ?? throw new DateChangedRecoveryException("Date Changed has no owning automation task.");
        var gate = DeviceLocks.GetOrAdd(connection.Serial, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        var wasRecovering = scope.Recovering;
        var stage = "closing Date Changed OK";
        try
        {
            scope.Recovering = true;
            await _guard.DismissAsync(connection, token).ConfigureAwait(false);
            var settings = _settings.Load();
            var package = settings.TargetPackageIds.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? StartGameTaskSettingsViewModel.DefaultPackageId;
            scope.Log("Running StartGame to restore the game Home screen.");
            stage = "launching the game";
            var launch = await _launcher.StartAsync(connection.AdbPath, connection.Serial,
                package, settings.TargetActivityName, token).ConfigureAwait(false);
            if (!launch.Succeeded || !launch.ProcessDetected)
                throw new DateChangedRecoveryException("Date Changed recovery could not start the game: " + launch.Message);
            stage = "running StartGame and confirming Home";
            var startup = await _startup.RunAsync(connection, package, scope.LogSink, scope.TaskLogSink, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!startup.Succeeded || !startup.HomeDetected)
                throw new DateChangedRecoveryException("Date Changed recovery could not confirm Home: " + startup.Message);
            scope.Generation++;
            scope.Log("Home confirmed; continuing the interrupted task.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DateChangedRecoveryException)
        {
            throw new DateChangedRecoveryException($"Date Changed recovery failed while {stage}: {exception.Message}");
        }
        finally
        {
            scope.Recovering = wasRecovering;
            gate.Release();
        }
    }
}
