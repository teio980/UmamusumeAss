using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

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

        var match = await _visualRuntime.WaitForMatchAsync(
                connection,
                UraScenarioResourceResolver.Resolve(pack, observed.Capture),
                roi: null,
                threshold: Math.Clamp(observed.Confidence, 0.80, 0.99),
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight,
                timeoutMilliseconds: 2_500,
                pollIntervalMilliseconds: 250,
                taskName: $"race_result.{race.RaceId}.placement",
                baseDirectory: pack.RootDirectory,
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
}
