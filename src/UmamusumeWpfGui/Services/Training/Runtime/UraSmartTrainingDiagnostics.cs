using System.Globalization;
using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Save the first unreadable preview per type/run using existing Career diagnostics.</summary>
internal sealed class UraSmartTrainingDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HashSet<string> _savedTypes = new(StringComparer.OrdinalIgnoreCase);
    private UraSmartTrainingStrategy? _strategy;
    private string _runId = "";

    public async Task SaveAsync(
        CareerFlowContext context, UraSmartTrainingStrategy strategy, string trainingType,
        GrayImage? firstFrame, GrayImage? secondFrame,
        UraTrainingCandidate first, UraTrainingCandidate second, UraTrainingCandidate merged,
        IReadOnlyDictionary<string, UraTrainingOcrFieldEvidence>? firstReadings,
        IReadOnlyDictionary<string, UraTrainingOcrFieldEvidence>? secondReadings)
    {
        // The engine creates a strategy per run; a reused dispatcher must get a fresh limit.
        if (!ReferenceEquals(_strategy, strategy))
        {
            _strategy = strategy;
            _savedTypes.Clear();
            _runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8];
        }
        if (!_savedTypes.Add(trainingType))
            return;

        var directory = Path.Combine(HachimiResourcePaths.GetDebugDirectory("career"),
            "smart-training-" + _runId, trainingType);
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            if (firstFrame is not null)
                GrayImageCodec.SaveFrame(firstFrame, Path.Combine(directory, "sample-1.png"));
            if (secondFrame is not null)
                GrayImageCodec.SaveFrame(secondFrame, Path.Combine(directory, "sample-2.png"));
            var diagnostics = new
            {
                context.State.TurnIndex,
                context.State.TurnPositionLabel,
                TrainingType = trainingType,
                First = first,
                Second = second,
                Merged = merged,
                FirstReadings = firstReadings,
                SecondReadings = secondReadings,
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "recognition.json"),
                JsonSerializer.Serialize(diagnostics, JsonOptions), context.CancellationToken)
                .ConfigureAwait(false);
            context.LogSink?.Add("URA Strategy", $"Unreadable training evidence: '{directory}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            context.LogSink?.Add("URA Strategy", $"Could not save training evidence: {exception.Message}");
        }
    }
}
