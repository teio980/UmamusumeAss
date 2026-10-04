using System.Diagnostics;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Owns the remaining credits of the optional recreation crane game. A screen
/// observation only enters this flow; it must not restart a grab mid-animation.
/// </summary>
internal sealed class CareerClawMachineFlow
{
    private readonly IVisualPipelineRuntime _visual;
    private GrayImage? _creditLabelTemplate;

    public CareerClawMachineFlow(IVisualPipelineRuntime visual)
    {
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        var clawScreen = context.Pack.ScreenProfile.Find("claw_machine");
        var resultScreen = context.Pack.ScreenProfile.Find("claw_machine_result");
        var template = await LoadScreenTemplateAsync(
                context,
                clawScreen,
                context.CancellationToken).ConfigureAwait(false);
        if (template is null)
            return Fail("How to Play recognition template is missing.");
        var resultTemplate = await LoadScreenTemplateAsync(
                context,
                resultScreen,
                context.CancellationToken).ConfigureAwait(false);
        if (resultTemplate is null)
            return Fail("Claw result recognition template is missing.");
        var settings = context.Pack.ScreenProfile.ClawMachine;

        try
        {
            await _visual.PrepareHeldTouchAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            return Fail($"Held-touch input is unavailable: {error.Message}");
        }

        var observed = await WaitForNextAsync(context, template, resultTemplate, null,
            TimeSpan.FromMilliseconds(settings.ReadyTimeoutMs)).ConfigureAwait(false);
        if (observed is null)
            return Fail("Could not identify the claw control or the remaining CREDIT count.");

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (observed.Value.IsResult)
                return await HandleResultAsync(context).ConfigureAwait(false);
            var ready = observed.Value.Ready!.Value;
            var result = await PressControlAsync(context, ready).ConfigureAwait(false);
            if (result is not null)
                return result;

            context.LogSink?.Add("Career Training",
                "Claw attempt completed; waiting for a lower CREDIT count or the result screen.");
            observed = await WaitForNextAsync(context, template, resultTemplate, ready.Credit,
                TimeSpan.FromMilliseconds(settings.NextAttemptTimeoutMs)).ConfigureAwait(false);
            if (observed is null)
                return Fail("Could not prepare the next claw attempt; check the preceding Claw paused reason.");
        }

        static CareerTrainingResult Fail(string reason) =>
            CareerRuntimeResults.Failure(reason + " Automation paused safely.", "claw_machine");
    }

    public async Task<CareerTrainingResult?> HandleResultAsync(CareerFlowContext context)
    {
        var profile = context.Pack.ScreenProfile;
        var settings = profile.ClawMachine;
        var resultScreen = profile.Find("claw_machine_result");
        var recognition = resultScreen!.Recognition;
        var template = await LoadScreenTemplateAsync(
            context, resultScreen, context.CancellationToken).ConfigureAwait(false);
        if (template is null)
            return CareerRuntimeResults.Failure("Claw result template is missing; automation paused safely.", "claw_machine_result");
        var started = Stopwatch.GetTimestamp();
        var stable = 0;
        Rectangle? previous = null;
        Rectangle? button = null;
        GrayImage? lastFrame = null;
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(settings.ResultTimeoutMs))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            lastFrame = await _visual.CaptureGrayAsync(context.Connection, context.CancellationToken).ConfigureAwait(false);
            button = null;
            if (lastFrame is not null)
            {
                var marker = CareerClawVision.MatchMarker(lastFrame, template, profile, recognition);
                if (marker.Found)
                    button = FindResultOk(lastFrame, marker, settings);
            }
            stable = button is { } current && previous is { } last && current == last ? stable + 1
                : button is not null ? 1 : 0;
            previous = button;
            if (stable >= settings.StableSamples) break;
            await _visual.DelayAsync(settings.PollIntervalMs, context.CancellationToken).ConfigureAwait(false);
        }
        if (stable < settings.StableSamples || button is null || lastFrame is null)
            return CareerRuntimeResults.Failure("Claw result was not stable or its OK button was missing; automation paused safely.", "claw_machine_result");
        var bounds = button.Value;
        await _visual.TapAsync(context.Connection, bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height / 2, lastFrame.Width, lastFrame.Height,
            "claw_result_ok", context.CancellationToken).ConfigureAwait(false);
        var missing = 0;
        started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(settings.ExitTimeoutMs))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection, context.CancellationToken).ConfigureAwait(false);
            if (frame is not null)
            {
                missing = CareerClawVision.MatchMarker(frame, template, profile, recognition).Found ? 0 : missing + 1;
                if (missing >= settings.StableSamples)
                {
                    context.LogSink?.Add("Career Training", "Claw result closed; continuing Career screen recognition.");
                    return null;
                }
            }
            await _visual.DelayAsync(settings.PollIntervalMs, context.CancellationToken).ConfigureAwait(false);
        }
        return CareerRuntimeResults.Failure("Claw result OK did not close the result screen; automation paused safely.", "claw_machine_result");
    }

    private static Rectangle? FindResultOk(GrayImage frame, TemplateMatchResult marker,
        CareerClawMachineSettings settings)
    {
        var buttons = CareerClawVision.FindResultButtons(frame, marker, settings);
        return buttons.Count == 1 ? buttons[0] : null;
    }

    internal async Task<CareerTrainingResult?> PressControlAsync(CareerFlowContext context,
        ReadyFrame ready)
    {
        var fingerDown = false;
        var released = false;
        var control = ready.Control;
        var x = control.X + control.Width / 2;
        var y = control.Y + control.Height / 2;
        var holdMs = context.Pack.ScreenProfile.ClawMachine.HoldDurationMs;
        try
        {
            fingerDown = true;
            await _visual.TouchDownAsync(context.Connection, x, y, ready.Width, ready.Height,
                "claw_hold", context.CancellationToken).ConfigureAwait(false);
            context.LogSink?.Add("Career Training",
                $"Claw CREDIT {ready.Credit}: holding control for {holdMs} ms.");
            await _visual.DelayAsync(holdMs, context.CancellationToken).ConfigureAwait(false);
            await _visual.TouchUpAsync(context.Connection, x, y, ready.Width, ready.Height,
                "claw_release", context.CancellationToken).ConfigureAwait(false);
            released = true;
            context.LogSink?.Add("Career Training", "Claw control released.");
            return null;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            return CareerRuntimeResults.Failure($"Claw input failed: {error.Message}", "claw_machine");
        }
        finally
        {
            if (fingerDown && !released)
            {
                try
                {
                    await _visual.TouchCancelAsync(context.Connection, x, y, ready.Width, ready.Height,
                        "claw_cancel", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not DateChangedInterruptionException and not DateChangedRecoveryException)
                {
                    context.LogSink?.Add("Career Training", $"Could not release the claw control: {error.Message}");
                }
            }
        }
    }
    internal async Task<ClawObservation?> WaitForNextAsync(CareerFlowContext context,
        GrayImage gameTemplate, GrayImage resultTemplate, int? previousCredit, TimeSpan timeout)
    {
        var profile = context.Pack.ScreenProfile;
        var settings = profile.ClawMachine;
        var started = Stopwatch.GetTimestamp();
        ReadyFrame? previous = null;
        var stable = 0;
        var resultStable = 0;
        var lastIssue = "No stable crane frame was captured.";
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection, context.CancellationToken).ConfigureAwait(false);
            ReadyFrame? current = null;
            if (frame is null)
                lastIssue = "ADB screenshot was unavailable.";
            else
            {
                var marker = CareerClawVision.MatchMarker(frame, gameTemplate, profile, profile.Find("claw_machine")!.Recognition);
                if (!marker.Found)
                {
                    var result = CareerClawVision.MatchMarker(frame, resultTemplate, profile, profile.Find("claw_machine_result")!.Recognition);
                    resultStable = result.Found ? resultStable + 1 : 0;
                    if (resultStable >= settings.StableSamples) return new ClawObservation(null, true);
                    lastIssue = "Waiting for a recognizable crane or result screen.";
                }
                else
                {
                    resultStable = 0;
                    var credit = await ReadCreditAsync(frame, marker, context).ConfigureAwait(false);
                    if (credit is null)
                        lastIssue = "The CREDIT label and its remaining count could not be read.";
                    else if (credit <= 0 || (previousCredit is { } oldCredit && credit >= oldCredit))
                        lastIssue = $"Waiting for CREDIT to decrease from {previousCredit}; current count is {credit}.";
                    else if (!CareerClawVision.TryFindControl(frame, marker, settings, out var control))
                        lastIssue = "The claw control button could not be located.";
                    else
                        current = new ReadyFrame(credit.Value, control, frame.Width, frame.Height);
                }
            }
            if (current is { } now)
            {
                var tolerance = now.Control.Width * settings.StableToleranceControlRatio;
                stable = previous is { } last && last.Credit == now.Credit
                    && Math.Abs(last.Control.X - now.Control.X) <= tolerance
                    && Math.Abs(last.Control.Y - now.Control.Y) <= tolerance ? stable + 1 : 1;
                if (stable >= settings.StableSamples) return new ClawObservation(now, false);
                lastIssue = "Waiting for a stable CREDIT count and control button.";
            }
            else
                stable = 0;
            previous = current;
            if (frame is null) resultStable = 0;
            await _visual.DelayAsync(settings.PollIntervalMs, context.CancellationToken).ConfigureAwait(false);
        }
        context.LogSink?.Add("Career Training", $"Claw paused: {lastIssue}");
        return null;
    }

    private async Task<int?> ReadCreditAsync(GrayImage frame, TemplateMatchResult marker, CareerFlowContext context)
    {
        var profile = context.Pack.ScreenProfile;
        var settings = profile.ClawMachine;
        var resources = context.Pack.VisualResources
            ?? throw new InvalidDataException("Career visual resources are not loaded.");
        _creditLabelTemplate ??= await _visual.LoadTemplateAsync(
            resources.ResolveVisualResource("career.turn.claw.credit_label"),
            string.Empty,
            context.CancellationToken).ConfigureAwait(false);
        if (_creditLabelTemplate is null) return null;
        var label = CareerClawVision.MatchMarker(frame, _creditLabelTemplate, profile,
            new UraScreenRecognition { TemplateThreshold = settings.CreditLabelThreshold });
        if (!label.Found || label.Y + label.Height >= marker.Y) return null;
        var padding = (int)Math.Ceiling(label.Height * settings.CreditOcrPaddingLabelRatio);
        var left = Math.Max(0, label.X - padding);
        var top = Math.Max(0, label.Y - padding);
        var bottom = Math.Min(marker.Y, label.Y + label.Height + padding);
        var text = await ReadCropAsync(frame,
            new Rectangle(left, top, frame.Width - left, bottom - top),
            "claw_credit", context).ConfigureAwait(false);
        return ParseCredit(text);
    }

    internal static int? ParseCredit(ScreenTextRecognitionResult? text)
    {
        var labels = text?.Detections.Where(detection => Regex.IsMatch(detection.Text, @"\bCREDIT\b", RegexOptions.IgnoreCase)).ToArray() ?? [];
        if (labels.Length != 1) return null;
        var label = labels[0];
        var sameLine = Regex.Match(label.Text, @"\bCREDIT\s+(?<count>\d+)\b", RegexOptions.IgnoreCase);
        if (sameLine.Success && int.TryParse(sameLine.Groups["count"].Value, out var inline)) return inline;
        var candidates = text!.Detections.Where(detection =>
            detection.Bounds.X >= label.Bounds.Right
            && detection.Bounds.Y < label.Bounds.Bottom && detection.Bounds.Bottom > label.Bounds.Y
            && Regex.IsMatch(detection.Text.Trim(), @"^\d+$")).ToArray();
        return candidates.Length == 1 && int.TryParse(candidates[0].Text.Trim(), out var count) ? count : null;
    }

    private async Task<ScreenTextRecognitionResult?> ReadCropAsync(GrayImage frame, Rectangle bounds,
        string taskName, CareerFlowContext context)
    {
        if (frame.RgbaPixels is null || bounds.Width <= 0 || bounds.Height <= 0) return null;
        using var image = Image.LoadPixelData<Rgba32>(frame.RgbaPixels, frame.Width, frame.Height);
        image.Mutate(operation => operation.Crop(bounds));
        var scale = context.Pack.ScreenProfile.ClawMachine.OcrScale;
        image.Mutate(operation => operation.Resize(Math.Max(1, (int)(image.Width * scale)),
            Math.Max(1, (int)(image.Height * scale))));
        var pixels = new byte[image.Width * image.Height * 4]; image.CopyPixelDataTo(pixels);
        var textColor = context.Pack.ScreenProfile.ClawMachine.OcrTextColor;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var ink = textColor.Accepts(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
            var value = ink ? (byte)0 : (byte)255;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        var crop = new GrayImage(image.Width, image.Height, [], pixels);
        return await _visual.DetectTextAsync(crop, null, crop.Width, crop.Height,
            "en-US", taskName, context.CancellationToken).ConfigureAwait(false);
    }

    internal readonly record struct ReadyFrame(int Credit, Rectangle Control, int Width, int Height);
    internal readonly record struct ClawObservation(ReadyFrame? Ready, bool IsResult);

    private Task<GrayImage?> LoadScreenTemplateAsync(
        CareerFlowContext context,
        UraScreenDefinition? screen,
        CancellationToken cancellationToken)
    {
        if (screen is null || string.IsNullOrWhiteSpace(screen.Recognition.Template))
            return Task.FromResult<GrayImage?>(null);
        var path = context.Pack.VisualResources?.ResolveScreenTemplate(
            screen, screen.Recognition.Template)
            ?? throw new InvalidDataException("Career visual resources are not loaded.");
        return _visual.LoadTemplateAsync(path, string.Empty, cancellationToken);
    }
}
