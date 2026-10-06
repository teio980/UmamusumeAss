using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Build the original configured confirmation tap from a fresh verified frame.</summary>
internal static class UraSmartTrainingConfirmationTap
{
    public static bool IsWinnerVerified(UraTrainingSelectionHeightResult selection, string type)
    {
        if (!selection.Succeeded || selection.ScreenChanged || selection.RaisedType != type
            || selection.Matches.Count != UraTrainingTypeCatalog.SupportedTypes.Count)
            return false;
        foreach (var expected in UraTrainingTypeCatalog.SupportedTypes)
            if (selection.Matches.Count(item => item.TrainingType == expected && item.Match.Found) != 1)
                return false;
        return UraTrainingSelectionHeightDetector.SelectHighest(selection.Matches).RaisedType == type;
    }

    public static TemplateMatchResult? Create(UraScenarioPack pack, LastVerifiedConnection connection,
        UraTrainingSelectionHeightResult selection, string type)
    {
        if (!IsWinnerVerified(selection, type))
            return null;
        var match = selection.Matches.Single(item => item.TrainingType == type).Match;
        var task = pack.ExecutionDefinition.GetTask($"training_selection_{type}_raised_click");
        if (match.Score < task.TemplateThreshold || task.Roi is not { Length: >= 4 } roi)
            return null;
        int ScaleX(int value) => (int)Math.Round(value * connection.Width / (double)pack.ExecutionDefinition.ReferenceWidth);
        int ScaleY(int value) => (int)Math.Round(value * connection.Height / (double)pack.ExecutionDefinition.ReferenceHeight);
        var left = ScaleX(roi[0]);
        var top = ScaleY(roi[1]);
        if (match.X < left || match.Y < top || match.X + match.Width > left + ScaleX(roi[2])
            || match.Y + match.Height > top + ScaleY(roi[3]))
            return null;
        var offset = task.ClickOffset;
        var tap = match with
        {
            X = match.X + (offset is { Length: >= 2 } ? ScaleX(offset[0]) : 0),
            Y = match.Y + (offset is { Length: >= 2 } ? ScaleY(offset[1]) : 0),
        };
        return tap.CenterX >= 0 && tap.CenterX < connection.Width && tap.CenterY >= 0
            && tap.CenterY < connection.Height ? tap : null;
    }
}
