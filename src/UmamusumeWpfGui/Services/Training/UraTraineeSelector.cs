using System.IO;
using System.Globalization;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Selects the configured trainee on the URA trainee grid by matching the
/// global database reference image against each visible card. The grid
/// geometry is profile-owned here so a future scenario/profile can replace
/// this adapter without changing the career state machine.
/// </summary>
public sealed class UraTraineeSelector
{
    private const double MinimumMatchScore = 0.70;
    private static readonly double[] MatchScales =
        [0.70, 0.80, 0.90, 1.00, 1.10, 1.20, 1.35, 1.50, 1.70, 1.90];

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;

    public UraTraineeSelector(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
    }

    public async Task<UraTraineeSelectionResult> SelectAsync(
        LastVerifiedConnection connection,
        int traineeId,
        IReadOnlyList<int[]> cellRois,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cellRois);
        if (cellRois.Count == 0 || cellRois.Any(roi => roi is null || roi.Length != 4))
            return Failure("The JSON trainee selector has no valid candidate cell ROIs.");

        if (!_umaDatabase.TryGetTrainee(traineeId, out var trainee)
            || trainee is null
            || !trainee.Available)
        {
            return Failure(
                $"Configured trainee ID {traineeId.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.");
        }

        var templatePaths = GetTemplatePaths(trainee);
        if (templatePaths.Count == 0)
        {
            return Failure(
                $"No trainee reference image is available for {trainee.NameEn} "
                + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}).");
        }

        var screen = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screen is null)
            return Failure("Could not capture the URA trainee selection screen.");

        var bestScore = 0d;
        TemplateMatchResult? bestMatch = null;
        foreach (var path in templatePaths)
        {
            var template = await _visualRuntime.LoadTemplateAsync(
                    path,
                    string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                continue;

            foreach (var cell in cellRois)
            {
                var match = TemplateMatcher.FindScaled(
                    screen,
                    template,
                    cell,
                    threshold: 0,
                    referenceWidth: 900,
                    referenceHeight: 1600,
                    MatchScales);
                if (match.Score > bestScore)
                {
                    bestScore = match.Score;
                    bestMatch = match;
                }
            }
        }

        if (bestMatch is null || bestScore < MinimumMatchScore)
        {
            return Failure(
                $"Could not safely locate {trainee.NameEn} on the URA trainee grid "
                + $"(best score {bestScore:0.000} / required {MinimumMatchScore:0.000}).");
        }

        return new UraTraineeSelectionResult(
            true,
            $"Located {trainee.NameEn} on the URA trainee grid with score {bestScore:0.000}.",
            bestMatch);
    }

    private List<string> GetTemplatePaths(UmaTraineeRecord trainee)
    {
        var paths = new List<string>(capacity: 3);
        AddExisting(paths, _umaDatabase.GetMaintenanceTraineeReferenceImagePath(trainee.TraineeId));
        AddExisting(paths, _umaDatabase.GetTraineeReferenceImagePath(trainee.TraineeId));
        AddExisting(paths, _umaDatabase.GetTraineeImagePath(trainee.TraineeId));
        return paths;
    }

    private static void AddExisting(List<string> paths, string path)
    {
        if (File.Exists(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            paths.Add(path);
    }

    private static UraTraineeSelectionResult Failure(string message) =>
        new(false, message);
}

public sealed record UraTraineeSelectionResult(
    bool Succeeded,
    string Message,
    TemplateMatchResult? Match = null);
