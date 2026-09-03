using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Data-driven Independent action contracts kept outside the normal URA
/// pipeline. These helpers validate semantic JSON graphs without executing
/// them or relying on a turn-session type.
/// </summary>
public static class IndependentTrainingContracts
{
    public static string ResolveCareerFinalConfirmationFirstSemanticAction(string careerMode) =>
        careerMode.Equals("independent", StringComparison.OrdinalIgnoreCase)
            ? "independent.select_mode"
            : "start";

    public static bool IsAgendaOcrRecognitionMiss(string message, string targetText) =>
        message.EndsWith(
            $"OCR target '{targetText}' was not found before timeout.",
            StringComparison.Ordinal)
        || (message.Contains($"OCR target '{targetText}' matched ", StringComparison.Ordinal)
            && message.EndsWith(" candidates.", StringComparison.Ordinal));

    public static bool TryValidateIndependentTemplateAction(
        UraScenarioPack pack,
        string actionId,
        out string error)
    {
        var screen = pack.ScreenProfile.Find("career_final_confirmation")
            ?? pack.ScreenProfile.Find("career_entry");
        if (screen is null)
        {
            error = "screen 'career_final_confirmation' is missing";
            return false;
        }

        var action = screen.FindAction(actionId);
        if (action is null || string.IsNullOrWhiteSpace(action.Task))
        {
            error = $"semantic action '{actionId}' is not mapped";
            return false;
        }

        var pending = new Queue<string>([action.Task]);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasTemplateProbe = false;
        while (pending.Count > 0)
        {
            var taskName = pending.Dequeue();
            if (!visited.Add(taskName))
                continue;
            if (!pack.ExecutionDefinition.TryGetTask(taskName, out var task) || task is null)
            {
                error = $"task '{taskName}' is not defined";
                return false;
            }

            if (task.Action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
            {
                if (task.Next.Count > 0 || task.OnErrorNext.Count > 0 || task.ExceededNext.Count > 0)
                {
                    error = $"task '{taskName}' uses Stop but has transitions";
                    return false;
                }
                continue;
            }

            if (IsTemplateAlgorithm(task.Algorithm)
                && task.Action.Equals("ClickSelf", StringComparison.OrdinalIgnoreCase)
                && task.SpecificRect is not { Length: > 0 })
            {
                error = string.Empty;
                return true;
            }

            var isSwipe = string.Equals(task.Algorithm, "JustReturn", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.Action, "Swipe", StringComparison.OrdinalIgnoreCase);
            var isJustReturn = string.Equals(task.Algorithm, "JustReturn", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.Action, "JustReturn", StringComparison.OrdinalIgnoreCase);
            var isTemplateProbe = IsTemplateAlgorithm(task.Algorithm)
                && string.Equals(task.Action, "JustReturn", StringComparison.OrdinalIgnoreCase);
            if (!isSwipe && !isJustReturn && !isTemplateProbe)
            {
                error = $"task '{taskName}' is not a template ClickSelf action";
                return false;
            }
            if (isSwipe && task.Swipe is not { Length: >= 5 })
            {
                error = $"task '{taskName}' uses Swipe but has no valid coordinates";
                return false;
            }

            var transitions = task.Next
                .Concat(task.OnErrorNext)
                .Concat(task.ExceededNext)
                .Where(next => !string.IsNullOrWhiteSpace(next))
                .ToArray();
            if (transitions.Length == 0)
            {
                if (task.Success)
                {
                    error = string.Empty;
                    return true;
                }
                error = $"task '{taskName}' is a non-success terminal action";
                return false;
            }
            if (isTemplateProbe && task.Success)
                hasTemplateProbe = true;
            foreach (var next in transitions)
                pending.Enqueue(next);
        }

        error = hasTemplateProbe
            ? string.Empty
            : $"task graph for '{actionId}' has no template ClickSelf action";
        return hasTemplateProbe;
    }

    private static bool IsTemplateAlgorithm(string? algorithm) =>
        string.Equals(algorithm, "MatchTemplate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(algorithm, "MatchTemplateScaled", StringComparison.OrdinalIgnoreCase);
}
