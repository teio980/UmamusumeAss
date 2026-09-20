using System.Collections.Concurrent;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed record CareerStartupRecoveryRule(
    string RecognitionScreenId,
    string ResumeScreenId,
    CareerEntryNavigationStep? EntryStep,
    NormalCareerSetupStage SetupStage);

public sealed record CareerStartupRecoveryTarget(
    string RecognitionScreenId,
    string ResumeScreenId,
    CareerEntryNavigationStep? EntryStep,
    NormalCareerSetupStage SetupStage);

public sealed class CareerStartupRecoveryDetector
{
    private const double EarlyRecognitionThreshold = 0.985;

    private static readonly CareerStartupRecoveryRule[] Rules =
    [
        new(
            "career_continue",
            "career_continue",
            CareerEntryNavigationStep.Continue,
            NormalCareerSetupStage.EnterCareer),
        new(
            "normal_quick_mode_settings",
            "normal_quick_mode_settings",
            null,
            NormalCareerSetupStage.ConfigureQuickMode),
        new(
            "normal_scenario_select_startup",
            "scenario_select",
            CareerEntryNavigationStep.Scenario,
            NormalCareerSetupStage.EnterCareer),
        new(
            "normal_trainee_select_startup",
            "trainee_select",
            CareerEntryNavigationStep.Trainee,
            NormalCareerSetupStage.EnterCareer),
        new(
            "normal_legacy_select_startup",
            "legacy_select",
            CareerEntryNavigationStep.Legacy,
            NormalCareerSetupStage.EnterCareer),
        new(
            "normal_support_select_startup",
            "support_select",
            CareerEntryNavigationStep.Support,
            NormalCareerSetupStage.EnterCareer),
        new(
            "normal_final_confirmation_startup",
            "career_final_confirmation",
            null,
            NormalCareerSetupStage.ConfigureMode),
    ];

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);

    public CareerStartupRecoveryDetector(IVisualPipelineRuntime visualRuntime)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
    }

    public async Task<CareerStartupRecoveryTarget?> DetectAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CancellationToken cancellationToken)
    {
        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return null;

        foreach (var candidate in Rules)
        {
            var screen = pack.ScreenProfile.Find(candidate.RecognitionScreenId);
            if (screen is null || screen.Templates.Count != 1)
                continue;

            var template = await LoadTemplateCachedAsync(
                    UraScenarioResourceResolver.Resolve(pack, screen.Templates[0]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                continue;

            var match = TemplateMatcher.Find(
                frame,
                template,
                screen.Recognition.Roi,
                screen.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
            if (match.Found)
            {
                return new CareerStartupRecoveryTarget(
                    candidate.RecognitionScreenId,
                    candidate.ResumeScreenId,
                    candidate.EntryStep,
                    candidate.SetupStage);
            }
        }

        return null;
    }

    private Task<GrayImage?> LoadTemplateCachedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lazy = _templateCache.GetOrAdd(
            path,
            key => new Lazy<Task<GrayImage?>>(
                () => _visualRuntime.LoadTemplateAsync(
                    key,
                    string.Empty,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(cancellationToken);
    }
}
