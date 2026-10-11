using System.Globalization;
using System.IO;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>UI projection of the native Independent Training catalog.</summary>
public sealed class IndependentTrainingCatalog
{
    public const int SkillPickerVisibleRows = 8;
    public const int SkillPickerFirstRowTop = 120;
    public const int SkillPickerRowHeight = 140;
    public const int SkillPickerCheckboxRoiLeft = 20;
    public const int SkillPickerCheckboxRoiWidth = 150;
    public const int SkillPickerCheckboxRoiHeight = 220;
    public const string RaceCardTemplateDirectory = "templates/independent/race_cards";
    private const string PackagedRaceCardTemplateDirectory = "resource/hachimi/career/modes/independent/templates/independent/race_cards";

    private IndependentTrainingCatalog(
        IReadOnlyList<IndependentTrainingRace> races,
        IReadOnlyList<IndependentTrainingSkill> skills,
        string raceSourceUrl,
        string skillSourceUrl,
        IndependentTrainingSkillSource skillSource)
    {
        Races = races;
        Skills = skills;
        RaceSourceUrl = raceSourceUrl;
        SkillSourceUrl = skillSourceUrl;
        SkillSource = skillSource;
    }

    public IReadOnlyList<IndependentTrainingRace> Races { get; }
    public IReadOnlyList<IndependentTrainingSkill> Skills { get; }
    public string RaceSourceUrl { get; }
    public string SkillSourceUrl { get; }
    public IndependentTrainingSkillSource SkillSource { get; }
    public bool IsAvailable => Races.Count > 0 && Skills.Count > 0;

    public static IndependentTrainingCatalog FromCoreCatalog(CoreCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var races = catalog.IndependentRaces.Select(row => new IndependentTrainingRace(
            String(row, "raceName"), String(row, "grade"), String(row, "year"),
            String(row, "turn"), String(row, "type"), String(row, "location"),
            String(row, "length"), String(row, "lengthM"), Integer(row, "raceId"),
            Integer(row, "sourceOrder"), String(row, "gameTrack"), Integer(row, "gameDistance"),
            String(row, "gameGround"), Boolean(row, "gameAvailable"), ScheduleInteger(row, "month"),
            ScheduleInteger(row, "day"), ScheduleString(row, "timeName"), Integer(row, "gameOrder")))
            .Where(item => !string.IsNullOrWhiteSpace(item.RaceName))
            .ToArray();
        var skills = catalog.Skills.Select(row => new IndependentTrainingSkill(
            Integer(row, "skillId"), String(row, "skillName"), String(row, "originalName"),
            Integer(row, "rarity"), Integer(row, "gradeValue"), String(row, "skillCategory"),
            String(row, "effectSummary"), Integer(row, "needSkillPoint"), String(row, "activationCondition"),
            Integer(row, "iconId"), String(row, "searchText"), Integer(row, "searchResultRow", 1),
            Boolean(row, "gameSearchMapped"), StringArray(row, "aliases"),
            Boolean(row, "availableInGlobal"), String(row, "availabilitySource"),
            Boolean(row, "singleModeEnabled"), Integer(row, "skillCategoryId"), String(row, "otherTiers")))
            .Where(item => item.SkillId > 0 && !string.IsNullOrWhiteSpace(item.SkillName))
            .ToArray();

        var raceSource = Metadata(catalog, "independent_race_metadata");
        var skillSource = Metadata(catalog, "skill_metadata");
        var skillSourceUrl = String(skillSource, "sourceUrl");
        var source = skillSource.ValueKind == JsonValueKind.Object
            && skillSource.TryGetProperty("source", out var sourceRow) ? sourceRow : skillSource;
        var sourceInfo = new IndependentTrainingSkillSource(
            String(source, "sourceName"), String(source, "masterSha256"),
            String(source, "clientVersion"), String(source, "region"), String(source, "sourceType"));
        return new IndependentTrainingCatalog(
            races, skills,
            FirstNonEmpty(String(raceSource, "sourceUrl"), "https://uma.guide/agenda-planner/"),
            FirstNonEmpty(skillSourceUrl, "https://uma.guide/skills/"), sourceInfo);
    }

    public bool TryGetAgendaPickerEntry(IndependentTrainingAgendaSelection selection, out IndependentTrainingRace race)
    {
        race = Races.FirstOrDefault(item => item.Year.Equals(selection.Year, StringComparison.OrdinalIgnoreCase)
            && item.Turn.Equals(selection.Turn, StringComparison.OrdinalIgnoreCase)
            && item.RaceName.Equals(selection.RaceName, StringComparison.OrdinalIgnoreCase)
            && item.IsGameAvailable)!;
        return race is not null;
    }

    public bool TryGetAgendaPickerOcrTarget(IndependentTrainingRace race, out string targetText)
    {
        ArgumentNullException.ThrowIfNull(race);
        targetText = string.Empty;
        var header = race.PickerHeaderTarget;
        if (!race.IsGameAvailable || race.RaceId <= 0 || string.IsNullOrWhiteSpace(header)) return false;
        var slotRaces = Races.Where(item => item.IsGameAvailable
            && item.Year.Equals(race.Year, StringComparison.OrdinalIgnoreCase)
            && item.Turn.Equals(race.Turn, StringComparison.OrdinalIgnoreCase));
        if (!slotRaces.Any(item => item.RaceId == race.RaceId
                && item.PickerHeaderTarget.Equals(header, StringComparison.OrdinalIgnoreCase))
            || slotRaces.Any(item => item.RaceId != race.RaceId
                && item.PickerHeaderTarget.Equals(header, StringComparison.OrdinalIgnoreCase))) return false;
        targetText = header;
        return true;
    }

    public static string GetRaceCardTemplatePath(int raceId) => Path.Combine(
        "templates", "independent", "race_cards", raceId.ToString(CultureInfo.InvariantCulture) + ".png");

    public static string? TryResolveRaceCardImagePath(IndependentTrainingRace race, string? baseDirectory = null)
    {
        if (race.RaceId <= 0) return null;
        var fileName = race.RaceId.ToString(CultureInfo.InvariantCulture) + ".png";
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            paths.Add(Path.Combine(baseDirectory, PackagedRaceCardTemplateDirectory.Replace('/', Path.DirectorySeparatorChar), fileName));
            paths.Add(Path.Combine(baseDirectory, GetRaceCardTemplatePath(race.RaceId)));
        }
        paths.Add(ResourcePathRuntime.Resolve(Path.Combine(PackagedRaceCardTemplateDirectory, fileName)));
        paths.Add(ResourcePathRuntime.Resolve(GetRaceCardTemplatePath(race.RaceId)));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private static JsonElement Metadata(CoreCatalog catalog, string key) =>
        catalog.AdditionalCategories.TryGetValue(key, out var value) ? value : default;

    private static string FirstNonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string String(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty
                : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : string.Empty
            : string.Empty;

    private static int Integer(JsonElement row, string property, int fallback = 0)
    {
        var value = String(row, property);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    }

    private static bool Boolean(JsonElement row, string property) =>
        String(row, property).Equals("true", StringComparison.OrdinalIgnoreCase)
        || String(row, property) == "1";

    private static int ScheduleInteger(JsonElement row, string property)
    {
        if (!row.TryGetProperty("gameSchedules", out var schedules) || schedules.ValueKind != JsonValueKind.Array) return 0;
        foreach (var schedule in schedules.EnumerateArray())
        {
            var value = Integer(schedule, property);
            if (property == "month" ? value is >= 1 and <= 12 : value > 0) return value;
        }
        return 0;
    }

    private static string ScheduleString(JsonElement row, string property)
    {
        if (!row.TryGetProperty("gameSchedules", out var schedules) || schedules.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var schedule in schedules.EnumerateArray())
            if (String(schedule, property) is { Length: > 0 } value) return value;
        return string.Empty;
    }

    private static string[] StringArray(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim() ?? string.Empty)
                .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
}
public sealed record IndependentTrainingRace(
    string RaceName,
    string Grade,
    string Year,
    string Turn,
    string Type,
    string Location,
    string Length,
    string LengthM,
    int RaceId = 0,
    int SourceOrder = -1,
    string GameTrack = "",
    int GameDistance = 0,
    string GameGround = "",
    bool IsGameAvailable = false,
    int Month = 0,
    int Day = 0,
    string TimeName = "",
    int GameOrder = -1)
{
    public string Key => $"{Year}|{Turn}|{RaceName}";

    public string DisplayLabel =>
        $"{Year} · {Turn} · {RaceName} ({Grade}, {Type} {LengthM})";

    public string Surface => string.IsNullOrWhiteSpace(GameGround)
        ? Type.Trim()
        : GameGround.Trim();

    public string DistanceCategory => Length.Trim();

    public string Direction => Location.Contains('\u21d2')
        ? "Right"
        : Location.Contains('\u21d0')
            ? "Left"
            : "Unknown";

    public string Half => Turn.EndsWith("_02", StringComparison.OrdinalIgnoreCase)
        ? "Second Half"
        : Turn.EndsWith("_01", StringComparison.OrdinalIgnoreCase)
            ? "First Half"
            : string.Empty;

    public string PickerHeaderTarget
    {
        get
        {
            var track = GameTrack.Trim();
            var ground = string.IsNullOrWhiteSpace(GameGround)
                ? Type.Trim()
                : GameGround.Trim();
            var direction = Location.Contains('\u21d2')
                ? "Right"
                : Location.Contains('\u21d0')
                    ? "Left"
                    : string.Empty;
            if (track.Length == 0
                || ground.Length == 0
                || GameDistance <= 0
                || direction.Length == 0)
            {
                return string.Empty;
            }

            var distance = GameDistance.ToString(CultureInfo.InvariantCulture) + "m";
            var length = string.IsNullOrWhiteSpace(Length)
                ? string.Empty
                : $" ({Length.Trim()})";
            return $"{track} {ground} {distance}{length} {direction}";
        }
    }

    public string RaceCardTemplatePath =>
        RaceId > 0
            ? IndependentTrainingCatalog.GetRaceCardTemplatePath(RaceId)
            : string.Empty;

}

public sealed record IndependentTrainingSkill(
    int SkillId,
    string SkillName,
    string OriginalName,
    int Rarity,
    int GradeValue,
    string SkillCategory,
    string EffectSummary,
    int NeedSkillPoint,
    string ActivationCondition,
    int IconId,
    string SearchText = "",
    int SearchResultRow = 1,
    bool IsGameSearchMapped = false,
    IReadOnlyList<string>? Aliases = null,
    bool AvailableInGlobal = false,
    string AvailabilitySource = "",
    bool SingleModeEnabled = false,
    int SkillCategoryId = 0,
    string OtherTiers = "")
{
    public IReadOnlyList<string> SearchAliases => Aliases ?? [];

    public string EffectiveSearchText => string.IsNullOrWhiteSpace(SearchText)
        ? SkillName
        : SearchText;

    public string OcrTargetText => SkillName;

    public bool IsSelectable => AvailableInGlobal
        && SingleModeEnabled
        && NeedSkillPoint > 0
        && !string.IsNullOrWhiteSpace(EffectiveSearchText);

    public IEnumerable<string> SearchTerms =>
        new[] { SkillName, OriginalName, SearchText }
            .Concat(SearchAliases)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public int SearchResultPage => Math.Max(0, SearchResultRow - 1)
        / IndependentTrainingCatalog.SkillPickerVisibleRows;

    public int SearchResultPickerRow => Math.Max(0, SearchResultRow - 1)
        % IndependentTrainingCatalog.SkillPickerVisibleRows;

    public string DisplayLabel
    {
        get
        {
            var tier = Rarity switch
            {
                1 => "Normal",
                2 => "Rare",
                3 => "Unique",
                _ => "Skill",
            };
            var details = new List<string> { SkillName, tier };
            if (NeedSkillPoint > 0)
                details.Add($"{NeedSkillPoint} SP");
            if (!string.IsNullOrWhiteSpace(EffectSummary))
                details.Add(EffectSummary);
            return string.Join(" · ", details);
        }
    }
}

public sealed record IndependentTrainingAgendaSelection(
    string Year,
    string Turn,
    string RaceName)
{
    public string Key => $"{Year}|{Turn}|{RaceName}";
}

public sealed record IndependentTrainingSkillSource(
    string Provenance,
    string SourceVersion,
    string ClientVersion,
    string Region,
    string SourceType)
{
    public static IndependentTrainingSkillSource Unknown { get; } = new(
        "",
        "",
        "",
        "",
        "");

    public bool IsExplicitGlobalClient =>
        Region.Equals("global", StringComparison.OrdinalIgnoreCase)
        && SourceType.Equals(
            "game-client-master-db",
            StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SourceVersion);
}
