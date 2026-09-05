using System.Collections.ObjectModel;
using System.IO;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Canonical locations for the Hachimi resources that are shipped with the
/// application.  These paths are intentionally kept as forward-slash
/// relative paths because they are persisted in task profiles and are also
/// used by the command-line diagnostics entry point.
/// </summary>
public static class HachimiResourcePaths
{
    public const string ResourceRoot = "resource/hachimi";
    public const string PipelinesRoot = ResourceRoot + "/pipelines";
    public const string UraManifest = ResourceRoot + "/ura/manifest.json";
    public const string UraExecution = ResourceRoot + "/ura/screens/execution.json";

    public const string DailyRaceDefinition = PipelinesRoot + "/daily_race.json";
    public const string MailCollectionDefinition = PipelinesRoot + "/mail_collection.json";
    public const string MissionCollectionDefinition = PipelinesRoot + "/mission_collection.json";
    public const string ShopDefinition = PipelinesRoot + "/shop.json";
    public const string ShopTaskDefinition = PipelinesRoot + "/shop_task.json";
    public const string StartGameDefinition = PipelinesRoot + "/start_game.json";
    public const string TeamRaceDefinition = PipelinesRoot + "/team_race.json";

    private static readonly IReadOnlyDictionary<string, string> LegacyDefinitionPaths =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ResourceRoot + "/daily_race.json"] = DailyRaceDefinition,
                [ResourceRoot + "/mail_collection.json"] = MailCollectionDefinition,
                [ResourceRoot + "/mission_collection.json"] = MissionCollectionDefinition,
                [ResourceRoot + "/shop.json"] = ShopDefinition,
                [ResourceRoot + "/shop_task.json"] = ShopTaskDefinition,
                [ResourceRoot + "/start_game.json"] = StartGameDefinition,
                [ResourceRoot + "/team_race.json"] = TeamRaceDefinition,
            });

    /// <summary>
    /// Migrates only the exact old built-in definition locations.  A custom
    /// path remains untouched, including custom absolute paths.
    /// </summary>
    public static string MigrateLegacyDefinitionPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
            return path;

        var normalized = trimmed.Replace('\\', '/');
        foreach (var pair in LegacyDefinitionPaths)
        {
            if (normalized.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return path;
    }

    public static string GetDebugDirectory(string pipelineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss",
            "debug",
            "hachimi",
            pipelineName);
    }
}
