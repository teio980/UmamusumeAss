using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal static class CareerMainScreenDetector
{
    // A shared Career title is not enough: Race Day and turn animations also
    // retain it. Both markers must match the caller's same captured frame.
    public static async Task<TemplateMatchResult?> MatchAsync(
        GrayImage frame,
        UraScenarioPack pack,
        Func<string, CancellationToken, Task<GrayImage?>> loadTemplateAsync,
        CancellationToken cancellationToken)
    {
        var main = pack.ScreenProfile.Find("career_main");
        if (main is null || string.IsNullOrWhiteSpace(main.Recognition.RequiredTemplate))
            return null;

        TemplateMatchResult? headerMatch = null;
        foreach (var path in main.Templates)
        {
            var header = await loadTemplateAsync(
                    UraScenarioResourceResolver.Resolve(pack, main, path), cancellationToken)
                .ConfigureAwait(false);
            if (header is null)
                continue;
            var match = TemplateMatcher.Find(frame, header, main.Recognition.Roi,
                main.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight);
            if (match.Found && (headerMatch is null || match.Score > headerMatch.Score))
                headerMatch = match;
        }
        if (headerMatch is null)
            return null;

        var training = await loadTemplateAsync(
                UraScenarioResourceResolver.Resolve(pack, main, main.Recognition.RequiredTemplate),
                cancellationToken)
            .ConfigureAwait(false);
        if (training is null)
            return null;
        var trainingMatch = TemplateMatcher.Find(frame, training, main.Recognition.RequiredTemplateRoi,
            main.Recognition.RequiredTemplateThreshold,
            pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight);
        return trainingMatch.Found ? headerMatch : null;
    }
}
