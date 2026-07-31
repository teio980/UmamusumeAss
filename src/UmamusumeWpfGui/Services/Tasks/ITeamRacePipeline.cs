using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Execution seam for the Team Race state machine.
/// The JSON definition is intentionally separate from this UI task module so
/// the game-specific executor can be implemented and tested independently.
/// </summary>
public interface ITeamRacePipeline
{
    Task<TeamRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        int raceCount,
        bool stopWhenTicketsEmpty,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);

    Task<TeamRacePipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record TeamRacePipelineResult(
    bool Succeeded,
    int RacesCompleted,
    string Message);

/// <summary>
/// Safe default until the game-specific executor is implemented.
/// Replace this binding with the real pipeline implementation.
/// </summary>
public sealed class TeamRacePipelinePlaceholder : ITeamRacePipeline
{
    public const string NotImplementedMessage =
        "Team Race JSON is ready, but its executor has not been implemented yet.";

    public Task<TeamRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        int raceCount,
        bool stopWhenTicketsEmpty,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TeamRacePipelineResult(false, 0, NotImplementedMessage));

    public Task<TeamRacePipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TeamRacePipelineResult(false, 0, NotImplementedMessage));
}
