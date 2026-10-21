using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Umamusume.AppCore.Models;

namespace Umamusume.AppCore.Services;

/// <summary>Catalog and defaults for the task routes implemented by Core.</summary>
public static class UmaTaskCatalog
{
    private static readonly ReadOnlyCollection<UmaTaskDefinition> Definitions = Array.AsReadOnly(new UmaTaskDefinition[]
    {
        new("start-game", "Start game", "Launch the configured Android game"),
        new("team-race", "Team Race", "Run the Team Race queue task"),
        new("daily-race", "Daily Race", "Run Daily Race for Monies or Support Points"),
        new("career-training", "Career Training", "Run a Core career training task"),
        new("mail-collection", "Mail collection", "Collect available presents from the in-game mailbox"),
        new("mission-collection", "Mission collection", "Collect available mission rewards"),
        new("shop-purchase", "Friends & Shop", "Open the shop and purchase configured items"),
    });

    public static IReadOnlyList<UmaTaskDefinition> All => Definitions;

    public static UmaTaskDefinition Get(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        foreach (var definition in Definitions)
        {
            if (string.Equals(definition.Id, taskId, StringComparison.Ordinal))
                return definition;
        }

        throw new ArgumentOutOfRangeException(nameof(taskId), taskId, "Unknown Core task id.");
    }

    /// <summary>Creates a fresh editable profile with defaults matching WPF task modules.</summary>
    public static HachimiTaskQueueItem CreateDefaultTask(
        string taskId,
        ConnectionSettings? connectionSettings = null)
    {
        var definition = Get(taskId);
        var settings = CreateDefaultSettings(definition.Id);
        if (definition.Id == "start-game" && connectionSettings is not null)
        {
            var packageId = connectionSettings.TargetPackageIds
                .Find(package => !string.IsNullOrWhiteSpace(package));
            settings["packageId"] = string.IsNullOrWhiteSpace(packageId)
                ? "com.cygames.umamusume"
                : packageId;
            settings["activityName"] = connectionSettings.TargetActivityName;
        }

        return new HachimiTaskQueueItem
        {
            TaskId = definition.Id,
            IsEnabled = definition.IsEnabledByDefault,
            Settings = settings,
        };
    }

    public static JsonObject CreateDefaultSettings(string taskId) => taskId switch
    {
        "start-game" => new JsonObject
        {
            ["packageId"] = "com.cygames.umamusume",
            ["activityName"] = "",
        },
        "career-training" => new JsonObject
        {
            ["manifestPath"] = "resource/hachimi/ura/manifest.json",
            ["traineeId"] = 100601,
            ["careerMode"] = "independent",
            ["runCount"] = 1,
            ["normalLineupStrategy"] = "front",
            ["normalTrainingRatio"] = new JsonObject
            {
                ["speed"] = 2,
                ["stamina"] = 1,
                ["power"] = 0,
                ["guts"] = 0,
                ["wit"] = 0,
            },
            ["retryFailedRaceWithAlarmClock"] = false,
            ["normalEventHandling"] = "default",
            ["normalSkillIds"] = new JsonArray(),
            ["independentTrainingFocus"] = "balanced",
            ["independentLineupStrategy"] = "pace",
            ["independentAgendaSelections"] = new JsonArray(),
            ["independentSkillIds"] = new JsonArray(),
            ["deleteExistingCareerData"] = false,
            ["autoRestoreTp"] = false,
            ["continueExistingCareer"] = true,
            ["supportDeckMode"] = "auto",
            ["supportDeckPreset"] = "custom",
            ["friendSupportCardId"] = null,
            ["supportCardIds"] = new JsonArray(),
            ["strategyId"] = "default-speed-medium",
            ["pauseOnUnknownOutcome"] = true,
            ["allowOptionalRaces"] = false,
            ["legacySelectionMode"] = "auto",
            ["useLegacyGuest"] = false,
            ["useCachedLegacy"] = true,
            ["legacyAttributeSparks"] = new JsonArray(),
            ["legacyAptitudeSparks"] = new JsonArray(),
        },
        "daily-race" => new JsonObject
        {
            ["definitionPath"] = "resource/hachimi/pipelines/daily_race.json",
            ["mode"] = "monies",
            ["difficulty"] = "veryhard",
            ["raceCount"] = 1,
            ["traineeId"] = null,
        },
        "team-race" => new JsonObject
        {
            ["definitionPath"] = "resource/hachimi/pipelines/team_race.json",
            ["raceCount"] = 3,
            ["stopWhenTicketsEmpty"] = true,
        },
        "mail-collection" => new JsonObject
        {
            ["definitionPath"] = "resource/hachimi/pipelines/mail_collection.json",
        },
        "mission-collection" => new JsonObject
        {
            ["definitionPath"] = "resource/hachimi/pipelines/mission_collection.json",
        },
        "shop-purchase" => new JsonObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(taskId), taskId, "Unknown Core task id."),
    };

    public static List<HachimiTaskQueueItem> CreateDefaultQueue(ConnectionSettings? connectionSettings = null)
    {
        var result = new List<HachimiTaskQueueItem>(Definitions.Count);
        foreach (var definition in Definitions)
            result.Add(CreateDefaultTask(definition.Id, connectionSettings));
        return result;
    }
}
