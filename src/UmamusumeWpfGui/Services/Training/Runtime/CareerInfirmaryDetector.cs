using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal static class CareerInfirmaryDetector
{
    private const string AvailableAssetId = "career.infirmary.available";
    private const string UnavailableAssetId = "career.infirmary.unavailable";
    private const string ButtonRegionId = "career.infirmary.button";

    internal sealed record VisualPolicy(
        string AvailableTemplatePath,
        string UnavailableTemplatePath,
        IReadOnlyList<int[]> ButtonRois,
        double Threshold,
        double ScoreGap);

    internal static bool TryGetVisualPolicy(
        UraScreenProfile profile,
        out VisualPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        policy = null;
        var resources = profile.VisualResources;
        if (resources is null
            || !resources.TryGetAsset(AvailableAssetId, out _)
            || !resources.TryGetAsset(UnavailableAssetId, out _)
            || !resources.TryGetRegion(ButtonRegionId, out var region)
            || region is null
            || region.Threshold is not double threshold
            || !TryGetMetadataDouble(region, "scoreGap", out var scoreGap)
            || !TryGetButtonRois(region, out var buttonRois))
        {
            return false;
        }

        policy = new VisualPolicy(
            resources.ResolveVisualResource(AvailableAssetId),
            resources.ResolveVisualResource(UnavailableAssetId),
            buttonRois,
            threshold,
            scoreGap);
        return true;
    }

    internal static bool IsAvailable(
        GrayImage frame,
        GrayImage availableTemplate,
        GrayImage unavailableTemplate,
        int referenceWidth,
        int referenceHeight,
        VisualPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        foreach (var roi in policy.ButtonRois)
        {
            var available = TemplateMatcher.FindColor(
                frame, availableTemplate, roi, policy.Threshold,
                referenceWidth, referenceHeight);
            if (!available.Found)
                continue;

            var unavailable = TemplateMatcher.FindColor(
                frame, unavailableTemplate, roi, 0,
                referenceWidth, referenceHeight);
            if (available.Score >= unavailable.Score + policy.ScoreGap)
                return true;
        }

        return false;
    }

    private static bool TryGetButtonRois(
        CareerVisualRegionDefinition region,
        out IReadOnlyList<int[]> buttonRois)
    {
        buttonRois = [];
        if (region.Metadata is null
            || !region.Metadata.TryGetValue("buttonRois", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<int[]>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array)
                return false;
            var roi = item.EnumerateArray().Select(value =>
            {
                if (!value.TryGetInt32(out var coordinate))
                    throw new InvalidDataException("Infirmary button ROI contains a non-integer coordinate.");
                return coordinate;
            }).ToArray();
            if (roi.Length != 4 || roi.Any(coordinate => coordinate < 0))
                return false;
            parsed.Add(roi);
        }

        if (parsed.Count == 0)
            return false;
        buttonRois = parsed;
        return true;
    }

    private static bool TryGetMetadataDouble(
        CareerVisualRegionDefinition region,
        string key,
        out double number)
    {
        number = 0;
        return region.Metadata is not null
            && region.Metadata.TryGetValue(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number)
            && double.IsFinite(number)
            && number >= 0;
    }
}
