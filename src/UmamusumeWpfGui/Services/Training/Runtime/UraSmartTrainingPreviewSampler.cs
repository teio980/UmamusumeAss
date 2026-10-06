using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed record UraSmartTrainingPreviewFrame(
    GrayImage? Frame, UraTrainingSelectionHeightResult Selection);

/// <summary>Smart-only frame reuse and bounded preview switching; never confirms training.</summary>
internal sealed class UraSmartTrainingPreviewSampler(
    IVisualPipelineRuntime runtime,
    Func<GrayImage, UraScenarioPack, CancellationToken, Task<UraTrainingSelectionHeightResult>> detect)
{
    public int CaptureCount { get; private set; }
    public int PreviewTapCount { get; private set; }

    public async Task<UraSmartTrainingPreviewFrame> CaptureAsync(
        LastVerifiedConnection connection, UraScenarioPack pack, CancellationToken cancellationToken)
    {
        CaptureCount++;
        var frame = await runtime.CaptureGrayAsync(connection, cancellationToken).ConfigureAwait(false);
        var selection = frame is null
            ? new UraTrainingSelectionHeightResult(null, [], "Could not capture the smart training preview.")
            : await detect(frame, pack, cancellationToken).ConfigureAwait(false);
        return new(frame, selection);
    }

    public async Task<UraSmartTrainingPreviewFrame> SelectAsync(
        LastVerifiedConnection connection, UraScenarioPack pack, string trainingType,
        UraSmartTrainingPreviewFrame current, CancellationToken cancellationToken)
    {
        if (!current.Selection.Succeeded && !current.Selection.ScreenChanged)
            current = await CaptureAsync(connection, pack, cancellationToken).ConfigureAwait(false);
        if (!current.Selection.Succeeded || string.Equals(current.Selection.RaisedType, trainingType,
                StringComparison.OrdinalIgnoreCase))
            return current;
        var target = current.Selection.Matches.SingleOrDefault(item => string.Equals(
            item.TrainingType, trainingType, StringComparison.OrdinalIgnoreCase));
        if (target?.Match.Found != true)
            return new(current.Frame, new(null, [], $"No verified logo for '{trainingType}'."));

        // Tap the position already found by the original all-five comparison. Re-matching
        // a flat-only ROI can select an unrelated icon during the button animation.
        await runtime.TapMatchAsync(connection, target.Match,
            $"training_selection_{trainingType}_preview", cancellationToken).ConfigureAwait(false);
        PreviewTapCount++;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await runtime.DelayAsync(120, cancellationToken).ConfigureAwait(false);
            current = await CaptureAsync(connection, pack, cancellationToken).ConfigureAwait(false);
            if (current.Selection.ScreenChanged || string.Equals(current.Selection.RaisedType,
                    trainingType, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return current;
    }
}
