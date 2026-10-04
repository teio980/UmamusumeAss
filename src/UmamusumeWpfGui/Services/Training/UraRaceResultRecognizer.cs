using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public sealed record UraRacePlacementObservation(
    string RaceId,
    int Placement,
    double Confidence,
    string Capture);

/// <summary>
/// Converts a visible race-result screen into a domain placement.
/// The scenario data supplies the result sample; the live frame must still
/// match that sample before the state machine is allowed to advance.
/// </summary>
public sealed class UraRaceResultRecognizer
{
    private readonly IVisualPipelineRuntime _visualRuntime;

    public UraRaceResultRecognizer(IVisualPipelineRuntime visualRuntime)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        _visualRuntime = visualRuntime;
    }

    public async Task<UraRacePlacementObservation?> RecognizeAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraRaceDefinition race,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(race);

        var observed = race.ObservedOutcome;
        if (observed is null || string.IsNullOrWhiteSpace(observed.Capture))
            return null;

        var runtimeFrameName = Path.GetFileNameWithoutExtension(observed.Capture);
        var runtimeFrameId = $"career.runtime_frame:{runtimeFrameName}";
        var resources = pack.VisualResources
            ?? throw new InvalidDataException("Career visual resources are not loaded.");
        var capturePath = resources.ResolveVisualResource(runtimeFrameId);
        resources.TryGetRegion("career.race.result.runtime_frame_match", out var matchRegion);
        if (matchRegion is null)
        {
            throw new InvalidDataException(
                "Career runtime race-result matching policy is missing from the visual catalog.");
        }
        var threshold = Math.Clamp(
            observed.Confidence,
            matchRegion.Threshold
                ?? throw new InvalidDataException("Career runtime frame threshold is missing."),
            GetMetadataDouble(matchRegion, "maximumConfidence"));

        var match = await _visualRuntime.WaitForMatchAsync(
                connection,
                capturePath,
                roi: matchRegion.Roi,
                threshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight,
                timeoutMilliseconds: GetMetadataInt(matchRegion, "waitTimeoutMs"),
                pollIntervalMilliseconds: GetMetadataInt(matchRegion, "pollIntervalMs"),
                taskName: $"race_result.{race.RaceId}.placement",
                baseDirectory: string.Empty,
                cancellationToken)
            .ConfigureAwait(false);

        if (match is not { Found: true })
            return null;

        return new UraRacePlacementObservation(
            race.RaceId,
            observed.Placement,
            Math.Min(match.Score, observed.Confidence),
            observed.Capture);
    }

    private static int GetMetadataInt(CareerVisualRegionDefinition region, string key) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind == System.Text.Json.JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException(
                $"Career runtime race-result policy is missing integer '{key}'.");

    private static double GetMetadataDouble(CareerVisualRegionDefinition region, string key) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble()
            : throw new InvalidDataException(
                $"Career runtime race-result policy is missing number '{key}'.");
}
