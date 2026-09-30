using System.Diagnostics;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Owns all three credits of the optional recreation crane game. A screen
/// observation only enters this flow; it must not restart a grab mid-animation.
/// </summary>
internal sealed class CareerClawMachineFlow
{
    private const int ReferenceWidth = 900;
    private const int ReferenceHeight = 1600;
    private const int ButtonX = 450;
    private const int ButtonY = 1405;
    private static readonly int[] HowToPlayRoi = [680, 275, 210, 65];
    private static readonly int[] CreditRoi = [555, 15, 325, 75];
    private readonly IVisualPipelineRuntime _visual;

    public CareerClawMachineFlow(IVisualPipelineRuntime visual)
    {
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        var template = await _visual.LoadTemplateAsync(
                "templates/career/turn/claw_how_to_play.png",
                context.Pack.ExecutionDefinition.BaseDirectory,
                context.CancellationToken).ConfigureAwait(false);
        if (template is null)
            return Fail("How to Play recognition template is missing.");

        try
        {
            await _visual.PrepareHeldTouchAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return Fail($"Held-touch input is unavailable: {error.Message}");
        }

        var ready = await WaitForReadyAsync(context, template, null,
            TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        if (ready is null)
            return Fail("Could not identify the crane, a standing plush, or the remaining credits.");

        var credit = ready.Value.Credit;
        while (credit > 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var result = await GrabAsync(context, ready.Value).ConfigureAwait(false);
            if (result is not null)
                return result;

            context.LogSink?.Add("Career Training",
                $"Claw attempt completed; waiting for credit {credit - 1} and a settled screen.");
            if (credit == 1)
            {
                if (!await WaitForExitAsync(context, template).ConfigureAwait(false))
                    return Fail("The crane result did not advance to the next Career screen.");
                return await HandleResultAsync(context).ConfigureAwait(false);
            }

            var next = await WaitForReadyAsync(context, template, credit - 1,
                TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            if (next is null)
                return Fail("The crane did not settle at the next credit.");
            ready = next;
            credit--;
        }

        return null;

        static CareerTrainingResult Fail(string reason) =>
            CareerRuntimeResults.Failure(reason + " Automation paused safely.", "claw_machine");
    }

    public async Task<CareerTrainingResult?> HandleResultAsync(
        CareerFlowContext context)
    {
        var template = await _visual.LoadTemplateAsync(
                "templates/career/turn/claw_result_cuties.png",
                context.Pack.ExecutionDefinition.BaseDirectory,
                context.CancellationToken).ConfigureAwait(false);
        if (template is null)
            return CareerRuntimeResults.Failure(
                "Claw result template is missing; automation paused safely.",
                "claw_machine_result");

        var started = Stopwatch.GetTimestamp();
        var readyFrames = 0;
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(25))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
            readyFrames = frame is not null && MatchesResult(frame, template)
                && HasResultOkButton(frame) ? readyFrames + 1 : 0;
            if (readyFrames >= 2)
                break;
            await _visual.DelayAsync(250, context.CancellationToken)
                .ConfigureAwait(false);
        }
        if (readyFrames < 2)
            return CareerRuntimeResults.Failure(
                "Claw result was not stable or its OK button was missing; automation paused safely.",
                "claw_machine_result");

        await _visual.TapAsync(context.Connection, 450, 1475,
            ReferenceWidth, ReferenceHeight, "claw_result_ok",
            context.CancellationToken).ConfigureAwait(false);
        var missing = 0;
        started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
            if (frame is not null)
            {
                missing = MatchesResult(frame, template) ? 0 : missing + 1;
                if (missing >= 2)
                {
                    context.LogSink?.Add("Career Training",
                        "Claw result closed; continuing Career screen recognition.");
                    return null;
                }
            }
            await _visual.DelayAsync(250, context.CancellationToken)
                .ConfigureAwait(false);
        }

        return CareerRuntimeResults.Failure(
            "Claw result OK did not close the result screen; automation paused safely.",
            "claw_machine_result");
    }

    internal async Task<CareerTrainingResult?> GrabAsync(CareerFlowContext context,
        ReadyFrame ready)
    {
        var initialGap = ready.Target.X - ready.Claw.X;
        if (initialGap < 65)
            return Failure("The selected plush is already behind the claw.");

        var fingerDown = false;
        var released = false;
        try
        {
            var downStarted = Stopwatch.GetTimestamp();
            fingerDown = true;
            await _visual.TouchDownAsync(context.Connection, ButtonX, ButtonY,
                ReferenceWidth, ReferenceHeight, "claw_hold",
                context.CancellationToken).ConfigureAwait(false);
            var downCompleted = Stopwatch.GetTimestamp();
            context.LogSink?.Add("Career Training",
                $"Claw credit {ready.Credit}: target x={ready.Target.X}, claw x={ready.Claw.X}; holding control.");

            var captureStarted = Stopwatch.GetTimestamp();
            var frame = await _visual.CaptureGrayAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
            var captureEnded = Stopwatch.GetTimestamp();
            if (frame is null
                || !CareerClawVision.TryFindTarget(frame, ready.Target, out var target)
                || !CareerClawVision.TryFindClaw(frame, ready.Claw, out var claw))
                return Failure("The plush or claw could not be tracked during the hold.");

            var observedGap = target.X - claw.X;
            var captureMidpoint = captureStarted
                + (captureEnded - captureStarted) / 2;
            // Input is applied during the ADB command, before its process
            // returns. Midpoints avoid charging the full command latency to
            // movement and the full screenshot latency to the next wait.
            var downMidpoint = downStarted
                + (downCompleted - downStarted) / 2;
            var observedSeconds = Stopwatch.GetElapsedTime(downMidpoint,
                    captureMidpoint)
                .TotalSeconds;
            var pixelsPerSecond = (initialGap - observedGap) / observedSeconds;
            context.LogSink?.Add("Career Training",
                $"Claw tracking: gap {initialGap}->{observedGap} px in {observedSeconds:0.000}s; "
                + $"target x={target.X}, claw x={claw.X}.");
            if (observedSeconds <= 0 || pixelsPerSecond is < 45 or > 1800)
                return Failure("The claw movement speed could not be measured reliably.");

            // The screenshot describes the midpoint of its ADB capture, not
            // the time at which the bytes finished arriving on the host.
            var elapsedSinceFrame = Stopwatch.GetElapsedTime(captureMidpoint)
                .TotalSeconds;
            var remainingPixels = observedGap - pixelsPerSecond * elapsedSinceFrame;
            if (remainingPixels < -25)
                return Failure("The claw passed the selected plush before a safe release.");

            // Reserve the observed ADB command latency for the UP injection.
            var commandLatencySeconds = Math.Clamp(
                Stopwatch.GetElapsedTime(downStarted, downCompleted)
                    .TotalSeconds / 2,
                0.025, 0.25);
            var waitSeconds = remainingPixels / pixelsPerSecond
                - commandLatencySeconds;
            if (waitSeconds > 0)
                await _visual.DelayAsync((int)Math.Round(waitSeconds * 1000),
                    context.CancellationToken).ConfigureAwait(false);
            await _visual.TouchUpAsync(context.Connection, ButtonX, ButtonY,
                ReferenceWidth, ReferenceHeight, "claw_release",
                context.CancellationToken).ConfigureAwait(false);
            released = true;
            context.LogSink?.Add("Career Training",
                $"Released claw toward x={target.X}; measured speed {pixelsPerSecond:0} px/s.");
            return null;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return Failure($"Claw input failed: {error.Message}");
        }
        finally
        {
            if (fingerDown && !released)
            {
                try
                {
                    await _visual.TouchCancelAsync(context.Connection, ButtonX,
                        ButtonY, ReferenceWidth, ReferenceHeight,
                        "claw_cancel", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    context.LogSink?.Add("Career Training",
                        $"Could not cancel the held claw control: {error.Message}");
                }
            }
        }

        static CareerTrainingResult Failure(string reason) =>
            CareerRuntimeResults.Failure(reason + " Automation paused safely.", "claw_machine");
    }

    private async Task<ReadyFrame?> WaitForReadyAsync(CareerFlowContext context,
        GrayImage template, int? expectedCredit, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        ReadyFrame? previous = null;
        var lastIssue = "No stable crane frame was captured.";
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                previous = null;
                lastIssue = "ADB screenshot was unavailable.";
            }
            else if (!MatchesHowToPlay(frame, template))
            {
                previous = null;
                lastIssue = "How to Play button was not visible.";
            }
            else if (!CareerClawVision.TryFindTarget(frame, null, out var target))
            {
                previous = null;
                lastIssue = "No nearby standing plush with an exposed face was visible.";
            }
            else if (!CareerClawVision.TryFindClaw(frame, null, out var claw))
            {
                previous = null;
                lastIssue = "The claw arms could not be located.";
            }
            else
            {
                var credit = await ReadCreditAsync(frame, context).ConfigureAwait(false);
                if (credit is >= 1 and <= 3
                    && (expectedCredit is null || credit == expectedCredit))
                {
                    var current = new ReadyFrame(credit.Value, target, claw);
                    if (previous is { } last
                        && last.Credit == current.Credit
                        && Math.Abs(last.Target.X - target.X) <= 18
                        && Math.Abs(last.Claw.X - claw.X) <= 18)
                        return current;
                    previous = current;
                    lastIssue = "The target or claw did not stay still across two frames.";
                }
                else
                {
                    previous = null;
                    lastIssue = credit is null
                        ? "The remaining credit number could not be read."
                        : $"Expected credit {expectedCredit}, but the screen showed {credit}.";
                }
            }
            await _visual.DelayAsync(200, context.CancellationToken)
                .ConfigureAwait(false);
        }

        context.LogSink?.Add("Career Training", $"Claw paused: {lastIssue}");
        return null;
    }

    private async Task<int?> ReadCreditAsync(GrayImage frame,
        CareerFlowContext context)
    {
        if (frame.RgbaPixels is null)
            return null;
        var bounds = new Rectangle(
            frame.Width * CreditRoi[0] / ReferenceWidth,
            frame.Height * CreditRoi[1] / ReferenceHeight,
            frame.Width * CreditRoi[2] / ReferenceWidth,
            frame.Height * CreditRoi[3] / ReferenceHeight);
        using var image = Image.LoadPixelData<Rgba32>(frame.RgbaPixels,
            frame.Width, frame.Height);
        image.Mutate(operation => operation.Crop(bounds)
            .Resize(bounds.Width * 2, bounds.Height * 2));
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        var enlarged = new GrayImage(image.Width, image.Height, [], pixels);
        var text = await _visual.DetectTextAsync(enlarged, null,
            image.Width, image.Height, "en-US", "claw_credit",
            context.CancellationToken).ConfigureAwait(false);
        foreach (var detection in text?.Detections ?? [])
        {
            var match = Regex.Match(detection.Text, @"\b[0-3]\b");
            if (match.Success && int.TryParse(match.Value, out var credit))
                return credit;
        }

        return null;
    }

    private async Task<bool> WaitForExitAsync(CareerFlowContext context,
        GrayImage template)
    {
        var started = Stopwatch.GetTimestamp();
        var missingCount = 0;
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(45))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(context.Connection,
                context.CancellationToken).ConfigureAwait(false);
            if (frame is not null)
            {
                missingCount = MatchesHowToPlay(frame, template)
                    ? 0 : missingCount + 1;
                if (missingCount >= 2)
                    return true;
            }
            await _visual.DelayAsync(300, context.CancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static bool MatchesHowToPlay(GrayImage frame,
        GrayImage template) =>
        TemplateMatcher.FindColor(frame, template, HowToPlayRoi, 0.94,
            ReferenceWidth, ReferenceHeight, requireTextContrast: true).Found;

    private static bool MatchesResult(GrayImage frame, GrayImage template) =>
        TemplateMatcher.FindColor(frame, template, [450, 430, 300, 70], 0.92,
            ReferenceWidth, ReferenceHeight, requireTextContrast: true).Found;

    private static bool HasResultOkButton(GrayImage frame)
    {
        var rgba = frame.RgbaPixels;
        if (rgba is null)
            return false;
        var left = frame.Width * 300 / ReferenceWidth;
        var right = frame.Width * 600 / ReferenceWidth;
        var top = frame.Height * 1440 / ReferenceHeight;
        var bottom = frame.Height * 1510 / ReferenceHeight;
        var green = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var offset = (y * frame.Width + x) * 4;
                if (rgba[offset] is >= 60 and <= 190
                    && rgba[offset + 1] >= 150
                    && rgba[offset + 2] <= 120)
                    green++;
                sampled++;
            }
        }
        return sampled > 0 && green >= sampled / 4;
    }

    internal readonly record struct ReadyFrame(int Credit,
        CareerClawVision.Point Target, CareerClawVision.Point Claw);
}
