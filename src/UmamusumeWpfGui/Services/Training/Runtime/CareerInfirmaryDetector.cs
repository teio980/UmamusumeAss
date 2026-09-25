using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal static class CareerInfirmaryDetector
{
    internal const string AvailableTemplatePath =
        "templates/career/turn/infirmary_available.png";
    internal const string UnavailableTemplatePath =
        "templates/career/turn/infirmary_unavailable.png";

    // Summer Camp places Infirmary one button-width to the right.
    private static readonly int[][] ButtonRois =
    [
        [130, 1410, 170, 62],
        [250, 1410, 170, 62],
    ];

    internal static bool IsAvailable(
        GrayImage frame,
        GrayImage availableTemplate,
        GrayImage unavailableTemplate,
        int referenceWidth,
        int referenceHeight)
    {
        foreach (var roi in ButtonRois)
        {
            var available = TemplateMatcher.FindColor(
                frame, availableTemplate, roi, 0.9,
                referenceWidth, referenceHeight);
            if (!available.Found)
                continue;

            var unavailable = TemplateMatcher.FindColor(
                frame, unavailableTemplate, roi, 0,
                referenceWidth, referenceHeight);
            if (available.Score >= unavailable.Score + 0.08)
                return true;
        }

        return false;
    }
}
