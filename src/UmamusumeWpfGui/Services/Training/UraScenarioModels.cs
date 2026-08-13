using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class UraScenarioManifest
{
    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("gameVersionRange")]
    public string GameVersionRange { get; set; } = string.Empty;

    [JsonPropertyName("moduleType")]
    public string ModuleType { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("definition")]
    public string Definition { get; set; } = string.Empty;

    [JsonPropertyName("objectives")]
    public string Objectives { get; set; } = string.Empty;

    [JsonPropertyName("races")]
    public string Races { get; set; } = string.Empty;

    [JsonPropertyName("events")]
    public string Events { get; set; } = string.Empty;

    [JsonPropertyName("screens")]
    public string Screens { get; set; } = string.Empty;

    [JsonPropertyName("execution")]
    public string Execution { get; set; } = string.Empty;

    [JsonPropertyName("localization")]
    public string Localization { get; set; } = string.Empty;

    [JsonPropertyName("globalDatabase")]
    public string GlobalDatabase { get; set; } = string.Empty;
}

public sealed class UraScenarioDefinition
{
    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("entryState")]
    public string EntryState { get; set; } = string.Empty;

    [JsonPropertyName("turnModel")]
    public UraTurnModel TurnModel { get; set; } = new();

    [JsonPropertyName("phases")]
    public List<UraScenarioPhase> Phases { get; set; } = [];

    [JsonPropertyName("finalSeries")]
    public UraFinalSeries FinalSeries { get; set; } = new();

    [JsonPropertyName("stateFields")]
    public List<string> StateFields { get; set; } = [];

    [JsonPropertyName("hooks")]
    public UraScenarioHooks Hooks { get; set; } = new();
}

public sealed class UraTurnModel
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "turn";

    [JsonPropertyName("careerYears")]
    public List<string> CareerYears { get; set; } = [];

    [JsonPropertyName("actionsPerTurn")]
    public int ActionsPerTurn { get; set; } = 1;

    [JsonPropertyName("raceConsumesTurn")]
    public bool RaceConsumesTurn { get; set; } = true;

    [JsonPropertyName("finaleUsesTurnLabel")]
    public string FinaleUsesTurnLabel { get; set; } = string.Empty;
}

public sealed class UraScenarioPhase
{
    [JsonPropertyName("phaseId")]
    public string PhaseId { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("allowedActions")]
    public List<string> AllowedActions { get; set; } = [];

    [JsonPropertyName("exitWhen")]
    public string ExitWhen { get; set; } = string.Empty;

    [JsonPropertyName("terminal")]
    public bool Terminal { get; set; }
}

public sealed class UraScenarioHooks
{
    [JsonPropertyName("beforeAction")]
    public List<string> BeforeAction { get; set; } = [];

    [JsonPropertyName("afterAction")]
    public List<string> AfterAction { get; set; } = [];

    [JsonPropertyName("onRaceFinished")]
    public List<string> OnRaceFinished { get; set; } = [];

    [JsonPropertyName("onPhaseChanged")]
    public List<string> OnPhaseChanged { get; set; } = [];
}

public sealed class UraFinalSeries
{
    [JsonPropertyName("seriesId")]
    public string SeriesId { get; set; } = string.Empty;

    [JsonPropertyName("entryObjectiveId")]
    public string EntryObjectiveId { get; set; } = string.Empty;

    [JsonPropertyName("stages")]
    public List<string> Stages { get; set; } = [];

    [JsonPropertyName("requiresSequentialCompletion")]
    public bool RequiresSequentialCompletion { get; set; }

    [JsonPropertyName("stageResultPolicy")]
    public string StageResultPolicy { get; set; } = string.Empty;
}

public sealed class UraObjectiveDocument
{
    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("objectiveChainId")]
    public string ObjectiveChainId { get; set; } = string.Empty;

    [JsonPropertyName("completionPolicy")]
    public string CompletionPolicy { get; set; } = string.Empty;

    [JsonPropertyName("objectives")]
    public List<UraObjectiveDefinition> Objectives { get; set; } = [];

    public UraObjectiveDefinition? Find(string objectiveId) =>
        Objectives.FirstOrDefault(item =>
            string.Equals(item.ObjectiveId, objectiveId, StringComparison.OrdinalIgnoreCase));
}

public sealed class UraObjectiveDefinition
{
    [JsonPropertyName("objectiveId")]
    public string ObjectiveId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public UraObjectiveTarget Target { get; set; } = new();

    [JsonPropertyName("raceId")]
    public string? RaceId { get; set; }

    [JsonPropertyName("observedRaceIds")]
    public List<string> ObservedRaceIds { get; set; } = [];

    [JsonPropertyName("nextObjectiveId")]
    public string? NextObjectiveId { get; set; }
}

public sealed class UraObjectiveTarget
{
    [JsonPropertyName("placement")]
    public int? Placement { get; set; }

    [JsonPropertyName("placementAtMost")]
    public int? PlacementAtMost { get; set; }

    [JsonPropertyName("minimum")]
    public int? Minimum { get; set; }

    [JsonPropertyName("raceGrade")]
    public string? RaceGrade { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("allPreviousObjectives")]
    public bool? AllPreviousObjectives { get; set; }

    [JsonPropertyName("finalSeriesComplete")]
    public bool? FinalSeriesComplete { get; set; }
}

public sealed class UraRaceDocument
{
    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("sourcePolicy")]
    public string SourcePolicy { get; set; } = string.Empty;

    [JsonPropertyName("races")]
    public List<UraRaceDefinition> Races { get; set; } = [];

    public UraRaceDefinition? Find(string raceId) =>
        Races.FirstOrDefault(item =>
            string.Equals(item.RaceId, raceId, StringComparison.OrdinalIgnoreCase));
}

public sealed class UraRaceDefinition
{
    [JsonPropertyName("raceId")]
    public string RaceId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public string Grade { get; set; } = string.Empty;

    [JsonPropertyName("course")]
    public UraRaceCourse Course { get; set; } = new();

    [JsonPropertyName("fieldSize")]
    public int? FieldSize { get; set; }

    [JsonPropertyName("held")]
    public UraRaceSchedule Held { get; set; } = new();

    [JsonPropertyName("rewardFans")]
    public int? RewardFans { get; set; }

    [JsonPropertyName("advancesTo")]
    public string? AdvancesTo { get; set; }

    [JsonPropertyName("retryPolicy")]
    public UraRetryPolicy RetryPolicy { get; set; } = new();

    [JsonPropertyName("objectiveReference")]
    public string? ObjectiveReference { get; set; }

    [JsonPropertyName("observedOutcome")]
    public UraRaceObservedOutcome? ObservedOutcome { get; set; }
}

public sealed class UraRaceObservedOutcome
{
    [JsonPropertyName("placement")]
    public int Placement { get; set; }

    [JsonPropertyName("capture")]
    public string? Capture { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.95;
}

public sealed class UraRaceCourse
{
    [JsonPropertyName("surface")]
    public string Surface { get; set; } = string.Empty;

    [JsonPropertyName("distance")]
    public int Distance { get; set; }

    [JsonPropertyName("distanceBand")]
    public string DistanceBand { get; set; } = string.Empty;

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    [JsonPropertyName("venue")]
    public string Venue { get; set; } = string.Empty;
}

public sealed class UraRaceSchedule
{
    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }
}

public sealed class UraRetryPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("observedAttempts")]
    public int? ObservedAttempts { get; set; }

    [JsonPropertyName("maxRetryCount")]
    public int? MaxRetryCount { get; set; }
}

public sealed class UraEventDocument
{
    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("events")]
    public List<UraEventDefinition> Events { get; set; } = [];
}

public sealed class UraEventDefinition
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public UraEventTrigger Trigger { get; set; } = new();

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("nextState")]
    public string? NextState { get; set; }
}

public sealed class UraEventTrigger
{
    [JsonPropertyName("afterObjectiveId")]
    public string? AfterObjectiveId { get; set; }

    [JsonPropertyName("beforeRaceId")]
    public string? BeforeRaceId { get; set; }

    [JsonPropertyName("afterRaceId")]
    public string? AfterRaceId { get; set; }
}

public sealed class UraScreenProfile
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = string.Empty;

    [JsonPropertyName("referenceWidth")]
    public int ReferenceWidth { get; set; } = 900;

    [JsonPropertyName("referenceHeight")]
    public int ReferenceHeight { get; set; } = 1600;

    [JsonPropertyName("screens")]
    public List<UraScreenDefinition> Screens { get; set; } = [];

    public UraScreenDefinition? Find(string screenId) =>
        Screens.FirstOrDefault(item =>
            string.Equals(item.ScreenId, screenId, StringComparison.OrdinalIgnoreCase));
}

public sealed class UraScreenDefinition
{
    [JsonPropertyName("screenId")]
    public string ScreenId { get; set; } = string.Empty;

    [JsonPropertyName("recognition")]
    public UraScreenRecognition Recognition { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<UraScreenAction> Actions { get; set; } = [];

    public IReadOnlyList<string> Templates => Recognition.GetTemplates();

    public UraScreenAction? FindAction(string semanticId) =>
        Actions.FirstOrDefault(item =>
            string.Equals(item.SemanticId, semanticId, StringComparison.OrdinalIgnoreCase));
}

public sealed class UraScreenRecognition
{
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("templates")]
    public List<string> AlternativeTemplates { get; set; } = [];

    [JsonPropertyName("stable")]
    public bool Stable { get; set; } = true;

    public IReadOnlyList<string> GetTemplates()
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(Template))
            result.Add(Template);
        foreach (var path in AlternativeTemplates)
        {
            if (!string.IsNullOrWhiteSpace(path)
                && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result;
    }
}

public sealed class UraScreenAction
{
    [JsonPropertyName("semanticId")]
    public string SemanticId { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    public string Task { get; set; } = string.Empty;
}

public sealed record UraScenarioPack(
    string ManifestPath,
    string RootDirectory,
    string GlobalDatabaseDirectory,
    string LocalizationDirectory,
    UraScenarioManifest Manifest,
    UraScenarioDefinition Definition,
    UraObjectiveDocument Objectives,
    UraRaceDocument Races,
    UraEventDocument Events,
    UraScreenProfile ScreenProfile,
    HachimiPipelineDefinition ExecutionDefinition);

public static class UraScenarioResourceResolver
{
    public static string Resolve(UraScenarioPack pack, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return Resolve(pack.RootDirectory, relativePath);
    }

    public static string Resolve(string scenarioRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"URA resource '{relativePath}' must be relative.");

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var path = normalized.StartsWith(
                "screens" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(scenarioRoot, normalized)
            : Path.Combine(scenarioRoot, "screens", normalized);
        var fullPath = Path.GetFullPath(path);
        var sharedRoot = Directory.GetParent(Path.GetFullPath(scenarioRoot))?.FullName;
        if (!IsWithin(fullPath, scenarioRoot)
            && (sharedRoot is null || !IsWithin(fullPath, sharedRoot)))
        {
            throw new InvalidDataException(
                $"URA resource '{relativePath}' escapes the scenario/shared resource roots.");
        }

        return fullPath;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class UraScenarioPackLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<UraScenarioPack> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedManifestPath = ResolvePath(manifestPath);
        var rootDirectory = Path.GetDirectoryName(resolvedManifestPath)
            ?? throw new InvalidDataException("URA manifest has no parent directory.");
        var manifest = await ReadAsync<UraScenarioManifest>(
                resolvedManifestPath,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA manifest is empty.");

        Require(manifest.ScenarioId, "ura", "scenarioId");
        Require(manifest.ModuleType, "builtin:ura", "moduleType");
        var globalDatabaseDirectory = ResolveSharedReference(
            rootDirectory,
            manifest.GlobalDatabase,
            "global database");
        var localizationDirectory = RequireDirectory(rootDirectory, manifest.Localization);
        var definition = await ReadAsync<UraScenarioDefinition>(
                RequireFile(rootDirectory, manifest.Definition),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA scenario definition is empty.");
        var objectives = await ReadAsync<UraObjectiveDocument>(
                RequireFile(rootDirectory, manifest.Objectives),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA objectives are empty.");
        var races = await ReadAsync<UraRaceDocument>(
                RequireFile(rootDirectory, manifest.Races),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA races are empty.");
        var events = await ReadAsync<UraEventDocument>(
                RequireFile(rootDirectory, manifest.Events),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA events are empty.");
        var profilePath = RequireFile(rootDirectory, manifest.Screens);
        var profile = await ReadAsync<UraScreenProfile>(profilePath, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA screen profile is empty.");
        var executionPath = RequireFile(rootDirectory, manifest.Execution);
        var execution = await HachimiPipelineDefinitionLoader.LoadAsync(
                executionPath,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("URA execution pipeline is empty or invalid.");

        RequireMatchingScenario(definition.ScenarioId, manifest.ScenarioId, "definition");
        RequireMatchingScenario(objectives.ScenarioId, manifest.ScenarioId, "objectives");
        RequireMatchingScenario(races.ScenarioId, manifest.ScenarioId, "races");
        RequireMatchingScenario(events.ScenarioId, manifest.ScenarioId, "events");
        RequireMatchingScenario(profile.ScenarioId, manifest.ScenarioId, "screen profile");
        if (execution.ReferenceWidth != profile.ReferenceWidth
            || execution.ReferenceHeight != profile.ReferenceHeight)
        {
            throw new InvalidDataException(
                "URA screen profile and execution pipeline use different reference resolutions.");
        }

        ValidateObjectives(objectives, definition, races);
        ValidateRaces(races, objectives, definition, rootDirectory);
        ValidateEvents(events, definition, objectives, races);
        ValidateScreens(
            profile,
            execution,
            rootDirectory,
            Path.GetDirectoryName(profilePath)!,
            Path.GetDirectoryName(executionPath)!);

        return new UraScenarioPack(
            resolvedManifestPath,
            rootDirectory,
            globalDatabaseDirectory,
            localizationDirectory,
            manifest,
            definition,
            objectives,
            races,
            events,
            profile,
            execution);
    }

    private static void ValidateScreens(
        UraScreenProfile profile,
        HachimiPipelineDefinition execution,
        string scenarioRoot,
        string profileDirectory,
        string executionDirectory)
    {
        var screenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in profile.Screens)
        {
            if (string.IsNullOrWhiteSpace(screen.ScreenId)
                || !screenIds.Add(screen.ScreenId))
            {
                throw new InvalidDataException(
                    $"URA screen profile contains a missing or duplicate screen ID '{screen.ScreenId}'.");
            }

            if (screen.Templates.Count == 0)
                throw new InvalidDataException($"Screen '{screen.ScreenId}' has no recognition template.");
            foreach (var template in screen.Templates)
                RequireFile(profileDirectory, template);

            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in screen.Actions)
            {
                if (string.IsNullOrWhiteSpace(action.SemanticId)
                    || !actionIds.Add(action.SemanticId)
                    || string.IsNullOrWhiteSpace(action.Task)
                    || !execution.Tasks.ContainsKey(action.Task))
                {
                    throw new InvalidDataException(
                        $"Screen '{screen.ScreenId}' contains an invalid action mapping '{action.SemanticId}'.");
                }
            }
        }

        foreach (var task in execution.Tasks.Values)
        {
            if (!string.IsNullOrWhiteSpace(task.Template))
            {
                var templatePath = ResolveExecutionResource(
                    scenarioRoot,
                    executionDirectory,
                    task.Template);
                if (!File.Exists(templatePath))
                {
                    throw new FileNotFoundException(
                        "URA execution template was not found.",
                        templatePath);
                }
            }
        }
    }

    private static void ValidateObjectives(
        UraObjectiveDocument objectives,
        UraScenarioDefinition definition,
        UraRaceDocument races)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var objective in objectives.Objectives)
        {
            if (string.IsNullOrWhiteSpace(objective.ObjectiveId) || !ids.Add(objective.ObjectiveId))
                throw new InvalidDataException("URA objectives contain a missing or duplicate objective ID.");
            if (objective.NextObjectiveId is not null && objectives.Find(objective.NextObjectiveId) is null)
                throw new InvalidDataException(
                    $"Objective '{objective.ObjectiveId}' points to missing '{objective.NextObjectiveId}'.");
            if (objective.RaceId is not null && races.Find(objective.RaceId) is null)
                throw new InvalidDataException(
                    $"Objective '{objective.ObjectiveId}' points to missing race '{objective.RaceId}'.");
        }

        if (definition.FinalSeries.Stages.Any(stage => objectives.Find(stage) is null))
            throw new InvalidDataException("URA final series references a missing objective.");
    }

    private static void ValidateRaces(
        UraRaceDocument races,
        UraObjectiveDocument objectives,
        UraScenarioDefinition definition,
        string scenarioRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var race in races.Races)
        {
            if (string.IsNullOrWhiteSpace(race.RaceId) || !ids.Add(race.RaceId))
                throw new InvalidDataException("URA races contain a missing or duplicate race ID.");
            if (race.AdvancesTo is not null
                && races.Find(race.AdvancesTo) is null
                && objectives.Find(race.AdvancesTo) is null)
                throw new InvalidDataException(
                    $"Race '{race.RaceId}' advances to missing race '{race.AdvancesTo}'.");

            if (race.ObservedOutcome is not null)
            {
                if (race.ObservedOutcome.Placement < 1
                    || race.FieldSize is int fieldSize && race.ObservedOutcome.Placement > fieldSize)
                {
                    throw new InvalidDataException(
                        $"Race '{race.RaceId}' has an invalid observed placement.");
                }

                if (string.IsNullOrWhiteSpace(race.ObservedOutcome.Capture))
                    throw new InvalidDataException(
                        $"Race '{race.RaceId}' has an observed placement without a capture.");

                var capturePath = UraScenarioResourceResolver.Resolve(
                    scenarioRoot,
                    race.ObservedOutcome.Capture);
                if (!File.Exists(capturePath))
                {
                    throw new InvalidDataException(
                        $"Race '{race.RaceId}' references missing result capture "
                        + $"'{race.ObservedOutcome.Capture}'.");
                }

                if (race.ObservedOutcome.Confidence is < 0 or > 1)
                    throw new InvalidDataException(
                        $"Race '{race.RaceId}' has an observed confidence outside [0,1].");
            }
        }

        if (definition.FinalSeries.Stages.Count == 0)
            throw new InvalidDataException("URA final series must contain at least one stage.");
    }

    private static void ValidateEvents(
        UraEventDocument events,
        UraScenarioDefinition definition,
        UraObjectiveDocument objectives,
        UraRaceDocument races)
    {
        var phaseIds = definition.Phases.Select(item => item.PhaseId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events.Events)
        {
            if (string.IsNullOrWhiteSpace(item.EventId) || !eventIds.Add(item.EventId))
                throw new InvalidDataException("URA events contain a missing or duplicate event ID.");
            if (!phaseIds.Contains(item.Phase))
                throw new InvalidDataException(
                    $"URA event '{item.EventId}' references missing phase '{item.Phase}'.");
            if (item.Trigger.AfterObjectiveId is not null
                && objectives.Find(item.Trigger.AfterObjectiveId) is null)
            {
                throw new InvalidDataException(
                    $"URA event '{item.EventId}' references missing objective.");
            }
            if (item.Trigger.BeforeRaceId is not null && races.Find(item.Trigger.BeforeRaceId) is null
                || item.Trigger.AfterRaceId is not null && races.Find(item.Trigger.AfterRaceId) is null)
            {
                throw new InvalidDataException(
                    $"URA event '{item.EventId}' references missing race.");
            }
        }
    }

    private static async Task<T?> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);
        return Path.GetFullPath(resolved);
    }

    private static string RequireFile(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("URA manifest contains an empty file reference.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"URA resource '{relativePath}' must be relative.");
        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"URA scenario resource '{relativePath}' escapes its pack directory.");
        if (!File.Exists(path))
            throw new FileNotFoundException("URA scenario resource was not found.", path);
        return path;
    }

    private static string RequireDirectory(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("URA manifest contains an empty directory reference.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"URA resource '{relativePath}' must be relative.");

        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"URA directory reference '{relativePath}' escapes the scenario pack.");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"URA directory was not found: {path}");
        return path;
    }

    private static string ResolveExecutionResource(
        string scenarioRoot,
        string executionDirectory,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("URA execution resource reference is empty.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException(
                $"URA execution resource '{relativePath}' must be relative.");

        var fullPath = Path.GetFullPath(Path.Combine(
            executionDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var sharedRoot = Directory.GetParent(
                Path.GetFullPath(scenarioRoot))?.FullName;
        if (!IsWithin(fullPath, executionDirectory)
            && !IsWithin(fullPath, scenarioRoot)
            && (sharedRoot is null || !IsWithin(fullPath, sharedRoot)))
        {
            throw new InvalidDataException(
                $"URA execution resource '{relativePath}' escapes the scenario/shared resource roots.");
        }

        return fullPath;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSharedReference(
        string rootDirectory,
        string relativePath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"URA manifest contains an empty {description} reference.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"URA resource '{relativePath}' must be relative.");

        return Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
    }

    private static void RequireMatchingScenario(
        string actual,
        string expected,
        string file)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"URA {file} scenario ID does not match the manifest.");
    }

    private static void Require(string value, string expected, string field)
    {
        if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"URA manifest field '{field}' must be '{expected}', got '{value}'.");
    }
}
