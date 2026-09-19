namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Shared data and semantic-action naming for the strategy dialog used by
/// both Career modes. Mode-specific screen tasks remain owned by each mode.
/// </summary>
public static class CareerStrategyCatalog
{
    public const string DefaultLineupStrategy = "pace";

    private static readonly Dictionary<string, string> StrategyTargets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["front"] = "Front",
            ["pace"] = "Pace",
            ["late"] = "Late",
            ["end"] = "End",
        };

    public static bool TryGetLineupStrategyUiMapping(
        string? strategy,
        out string targetText)
    {
        targetText = string.Empty;
        var normalized = Normalize(strategy);
        if (normalized.Length == 0
            || !StrategyTargets.TryGetValue(normalized, out var resolvedTarget))
            return false;

        targetText = resolvedTarget;
        return true;
    }

    public static bool TryGetLineupStrategySemanticAction(
        string? strategy,
        string mode,
        out string semanticAction,
        out string targetText)
    {
        semanticAction = string.Empty;
        targetText = string.Empty;
        var normalizedMode = Normalize(mode);
        var normalizedStrategy = Normalize(strategy);
        if (normalizedMode.Length == 0
            || !TryGetLineupStrategyUiMapping(normalizedStrategy, out targetText))
        {
            return false;
        }

        semanticAction = normalizedMode + ".strategy.option." + normalizedStrategy;
        return true;
    }

    public static string StrategyChangeSemanticAction(string mode) =>
        NormalizeMode(mode) + ".strategy.change";

    public static string StrategySaveSemanticAction(string mode) =>
        NormalizeMode(mode) + ".strategy.save";

    public static string StrategyReturnSemanticAction(string mode) =>
        NormalizeMode(mode) + ".strategy.return";

    private static string NormalizeMode(string mode)
    {
        var normalized = Normalize(mode);
        return normalized.Length == 0
            ? throw new ArgumentException("Career mode is required.", nameof(mode))
            : normalized;
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;
}
