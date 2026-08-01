using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Collects all presents from the game's mailbox and closes the result page.
/// The home tab is template-matched and clicked before every run so the task
/// starts from a known page even when a previous task left the game elsewhere.
/// </summary>
public sealed class AdbMailCollectionPipeline : IMailCollectionPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAdbRuntime _adbRuntime;
    private readonly IAsyncDelay _asyncDelay;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbMailCollectionPipeline(IAdbRuntime adbRuntime, IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
    }

    public async Task<MailCollectionPipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
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
                return new MailCollectionPipelineResult(
                    false,
                    "An email collection run is already in progress.");
            }

            _runCancellation = linked;
        }

        try
        {
            var definition = await LoadDefinitionAsync(
                definitionPath,
                linked.Token).ConfigureAwait(false);
            if (definition is null)
            {
                return Fail(logSink, "The email collection definition could not be loaded.");
            }

            AddLog(logSink, "Mail Collection", "Starting email collection.");

            var homeMatch = await WaitForHomeAsync(
                    connection,
                    definition,
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            if (homeMatch is null)
            {
                return Fail(logSink, "Timed out waiting for the game home tab.");
            }

            await TapMatchAsync(
                    connection,
                    homeMatch,
                    "Game home",
                    linked.Token)
                .ConfigureAwait(false);
            AddLog(logSink, "Mail Collection", "Returned to the game home tab.", LogEntryKind.Success);
            await DelayAsync(definition.Timing.NavigationMs, linked.Token).ConfigureAwait(false);

            await ClickTemplateStepAsync(
                    connection,
                    definition,
                    definition.Steps.GiftBox,
                    "Gift box",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await DelayAsync(definition.Timing.MailboxLoadMs, linked.Token).ConfigureAwait(false);

            await ClickTemplateStepAsync(
                    connection,
                    definition,
                    definition.Steps.CollectAll,
                    "Collect All",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await DelayAsync(definition.Timing.CollectionSettleMs, linked.Token).ConfigureAwait(false);

            await ClickTemplateStepAsync(
                    connection,
                    definition,
                    definition.Steps.Close,
                    "Close",
                    logSink,
                    linked.Token)
                .ConfigureAwait(false);
            await DelayAsync(definition.Timing.NavigationMs, linked.Token).ConfigureAwait(false);

            if (!await IsHomeVisibleAsync(
                    connection,
                    definition,
                    definition.Timing.HomeVerifyTimeoutMs,
                    linked.Token).ConfigureAwait(false))
            {
                return Fail(logSink, "Mailbox was closed, but the game home tab was not detected.");
            }

            AddLog(logSink, "Mail Collection", "Email collection completed.", LogEntryKind.Success);
            return new MailCollectionPipelineResult(true, "Email collection completed.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            AddLog(logSink, "Mail Collection", "Email collection was stopped.", LogEntryKind.Failure);
            return new MailCollectionPipelineResult(false, "Email collection was stopped.");
        }
        catch (Exception ex)
        {
            AddLog(logSink, "Mail Collection", ex.Message, LogEntryKind.Failure);
            return new MailCollectionPipelineResult(false, $"Email collection failed: {ex.Message}");
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

    public Task<MailCollectionPipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        AddLog(logSink, "Mail Collection", "Stop requested.");
        return Task.FromResult(new MailCollectionPipelineResult(true, "Stop requested."));
    }

    private async Task<TemplateMatchResult?> WaitForHomeAsync(
        LastVerifiedConnection connection,
        MailCollectionDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForTemplateAsync(
                connection,
                definition,
                definition.Steps.Home,
                definition.Timing.HomeTimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is not null)
            return match;

        // A previous task may have left a modal or a sub-page open. Back out
        // a few times, checking the real home-tab template after each step.
        for (var attempt = 0; attempt < definition.Timing.BackAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var back = await _adbRuntime.BackAsync(
                    connection.AdbPath,
                    connection.Serial,
                    cancellationToken)
                .ConfigureAwait(false);
            if (back.Error is not null || back.TimedOut || back.ExitCode != 0)
            {
                AddLog(logSink, "Mail Collection", $"ADB Back failed: {back.Stderr}");
                break;
            }

            await DelayAsync(definition.Timing.BackSettleMs, cancellationToken).ConfigureAwait(false);
            match = await WaitForTemplateAsync(
                    connection,
                    definition,
                    definition.Steps.Home,
                    definition.Timing.HomeRetryTimeoutMs,
                    cancellationToken)
                .ConfigureAwait(false);
            if (match is not null)
                return match;
        }

        return null;
    }

    private async Task<bool> IsHomeVisibleAsync(
        LastVerifiedConnection connection,
        MailCollectionDefinition definition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var match = await WaitForTemplateAsync(
                connection,
                definition,
                definition.Steps.Home,
                timeoutMs,
                cancellationToken)
            .ConfigureAwait(false);
        return match is not null;
    }

    private async Task<TemplateMatchResult?> WaitForTemplateAsync(
        LastVerifiedConnection connection,
        MailCollectionDefinition definition,
        MailCollectionStep step,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(
                step.Template,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
            throw new InvalidOperationException($"Template '{step.Template}' could not be loaded.");

        var started = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 0, 10 * 60 * 1000));
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            step.PollIntervalMs > 0 ? step.PollIntervalMs : definition.Timing.PollIntervalMs,
            50,
            10_000));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await CaptureScreenshotAsync(connection, cancellationToken)
                .ConfigureAwait(false);
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

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return null;
            await DelayAsync((int)poll.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ClickTemplateStepAsync(
        LastVerifiedConnection connection,
        MailCollectionDefinition definition,
        MailCollectionStep step,
        string stepName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var match = await WaitForTemplateAsync(
                connection,
                definition,
                step,
                step.TimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
            throw new InvalidOperationException(
                $"Timed out waiting for the '{stepName}' template (threshold {step.Threshold:0.000}).");

        await TapMatchAsync(
                connection,
                match,
                stepName,
                cancellationToken)
            .ConfigureAwait(false);
        AddLog(
            logSink,
            "Mail Collection",
            $"Clicked {stepName} by template match at ({match.CenterX},{match.CenterY}), score {match.Score:0.000}.",
            LogEntryKind.Success);
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
            throw new InvalidOperationException($"ADB template tap failed for '{stepName}': {result.Stderr}");
    }

    private async Task<AdbScreenshotResult?> CaptureScreenshotAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        var raw = await _adbRuntime.DecodeRawScreenshotAsync(
                connection.AdbPath,
                connection.Serial,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return raw.Value is { } decoded
            ? new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero, decoded)
            : null;
    }

    private static async Task<GrayImage?> LoadTemplateAsync(
        string? templatePath,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
            return null;

        var fullPath = Path.IsPathRooted(templatePath)
            ? templatePath
            : Path.Combine(baseDirectory, templatePath);
        return await Task.Run(
                () => GrayImageCodec.FromFile(fullPath),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MailCollectionDefinition?> LoadDefinitionAsync(
        string definitionPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(definitionPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(definitionPath);
            var definition = await JsonSerializer.DeserializeAsync<MailCollectionDefinition>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (definition is not null)
                definition.BaseDirectory = Path.GetDirectoryName(definitionPath) ?? AppContext.BaseDirectory;
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

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        _asyncDelay.DelayAsync(
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)),
            cancellationToken);

    private static MailCollectionPipelineResult Fail(IGrassTaskLogSink? logSink, string message)
    {
        AddLog(logSink, "Mail Collection", message, LogEntryKind.Failure);
        return new MailCollectionPipelineResult(false, message);
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string type,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add(type, details, kind);

    private sealed class MailCollectionDefinition
    {
        [JsonPropertyName("referenceWidth")]
        public int ReferenceWidth { get; set; } = 900;

        [JsonPropertyName("referenceHeight")]
        public int ReferenceHeight { get; set; } = 1600;

        [JsonPropertyName("steps")]
        public MailCollectionSteps Steps { get; set; } = new();

        [JsonPropertyName("timing")]
        public MailCollectionTiming Timing { get; set; } = new();

        [JsonIgnore]
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;
    }

    private sealed class MailCollectionSteps
    {
        public MailCollectionStep Home { get; set; } = new();
        public MailCollectionStep GiftBox { get; set; } = new();
        public MailCollectionStep CollectAll { get; set; } = new();
        public MailCollectionStep Close { get; set; } = new();
    }

    private sealed class MailCollectionStep
    {
        public string? Template { get; set; }
        public int[]? Roi { get; set; }
        public double Threshold { get; set; } = 0.86;
        public int TimeoutMs { get; set; } = 10_000;
        public int PollIntervalMs { get; set; }
    }

    private sealed class MailCollectionTiming
    {
        public int NavigationMs { get; set; } = 1200;
        public int MailboxLoadMs { get; set; } = 1800;
        public int CollectionSettleMs { get; set; } = 1200;
        public int HomeTimeoutMs { get; set; } = 5000;
        public int HomeRetryTimeoutMs { get; set; } = 2500;
        public int HomeVerifyTimeoutMs { get; set; } = 3000;
        public int BackAttempts { get; set; } = 3;
        public int BackSettleMs { get; set; } = 600;
        public int PollIntervalMs { get; set; } = 300;
    }
}
