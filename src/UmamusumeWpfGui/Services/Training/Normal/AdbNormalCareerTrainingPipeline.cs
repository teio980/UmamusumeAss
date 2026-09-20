using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class AdbNormalCareerTrainingPipeline : ICareerTrainingPipeline
{
    private readonly CareerTrainingEngine _engine;

    public AdbNormalCareerTrainingPipeline(CareerTrainingEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public AdbNormalCareerTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        HachimiJsonPipelineRunner jsonRunner)
        : this(new CareerTrainingEngine(
            visualRuntime,
            umaDatabase,
            entryNavigator,
            jsonRunner))
    {
    }

    public Task<CareerTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default) =>
        _engine.RunAsync(connection, settings, logSink, taskLogSink, cancellationToken);

    public Task<CareerTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default) =>
        _engine.StopAsync(connection, logSink, taskLogSink, cancellationToken);

    internal static bool IsRuntimeCareerScreen(string screenId) =>
        CareerTrainingEngine.IsRuntimeCareerScreen(screenId);

    internal static CareerScreenKind ClassifyRuntimeScreen(string? screenId) =>
        CareerScreenClassification.Classify(screenId);

    internal static bool IsCareerStartTransitionExpected(
        UraCareerSessionState state) =>
        CareerTrainingEngine.IsCareerStartTransitionExpected(state);
}
