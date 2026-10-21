using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;

namespace Umamusume.AppCore.Services;

/// <summary>Applies the same runtime-only fields WPF adds before submitting tasks to Core.</summary>
public static class UmaTaskSettingsBuilder
{
    private static readonly JsonSerializerOptions ShopJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static JsonObject Build(
        ConnectionSettings connectionSettings,
        string taskId,
        JsonObject? profileSettings = null,
        string? appBaseDirectory = null,
        string? checkpointDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionSettings);
        var definition = UmaTaskCatalog.Get(taskId);
        var hasProfileSettings = profileSettings is not null;
        var settings = profileSettings is null
            ? UmaTaskCatalog.CreateDefaultSettings(definition.Id)
            : (JsonObject)profileSettings.DeepClone();

        ResolveConfiguredPath(settings, "definitionPath", appBaseDirectory);
        ResolveConfiguredPath(settings, "manifestPath", appBaseDirectory);

        var configuredPackageId = connectionSettings.TargetPackageIds
            .FirstOrDefault(package => !string.IsNullOrWhiteSpace(package))
            ?? "com.cygames.umamusume";

        if (definition.Id == "start-game")
        {
            if (!hasProfileSettings)
            {
                settings["packageId"] = configuredPackageId;
                settings["activityName"] = connectionSettings.TargetActivityName;
            }
            else
            {
                SetDefaultString(settings, "packageId", configuredPackageId);
                SetDefaultString(settings, "activityName", connectionSettings.TargetActivityName);
            }
        }
        else
        {
            settings["packageId"] = configuredPackageId;
            settings["activityName"] = connectionSettings.TargetActivityName;
        }

        if (definition.Id == "career-training")
        {
            settings["checkpointDirectory"] = checkpointDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UmamusumeAss",
                    "checkpoints");
        }
        else
        {
            settings["shop"] = JsonSerializer.SerializeToNode(
                connectionSettings.Hachimi.Shop,
                ShopJsonOptions);
        }

        return settings;
    }

    public static JsonObject Build(
        ConnectionSettings connectionSettings,
        HachimiTaskQueueItem task,
        string? appBaseDirectory = null,
        string? checkpointDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Build(
            connectionSettings,
            task.TaskId,
            task.Settings,
            appBaseDirectory,
            checkpointDirectory);
    }

    public static string BuildJson(
        ConnectionSettings connectionSettings,
        HachimiTaskQueueItem task,
        string? appBaseDirectory = null,
        string? checkpointDirectory = null) =>
        UmaTaskSettingsSerializer.Serialize(Build(
            connectionSettings,
            task,
            appBaseDirectory,
            checkpointDirectory));

    private static void ResolveConfiguredPath(JsonObject settings, string key, string? appBaseDirectory)
    {
        if (settings[key] is not JsonValue value
            || !value.TryGetValue<string>(out var configuredPath)
            || string.IsNullOrWhiteSpace(configuredPath))
            return;

        var path = configuredPath;
        if (!Path.IsPathFullyQualified(path))
        {
            var baseDirectory = appBaseDirectory ?? PlatformPaths.GetActiveAppBaseDirectory();
            path = Path.Combine(baseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        }
        settings[key] = Path.GetFullPath(path);
    }

    private static void SetDefaultString(JsonObject settings, string key, string value)
    {
        if (settings[key] is JsonValue current
            && current.TryGetValue<string>(out var existing)
            && !string.IsNullOrWhiteSpace(existing))
            return;

        settings[key] = value;
    }
}
