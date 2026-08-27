using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Immutable, offline data used by the Independent Training setup editor.
/// The race catalog is a checked-in guide snapshot.  The skill catalog is
/// generated from the installed Global client's master.mdb and is shipped with
/// the application so a run never depends on a live web page.
/// </summary>
public sealed class IndependentTrainingCatalog
{
    public const int SkillPickerVisibleRows = 8;
    public const int SkillPickerFirstRowTop = 120;
    public const int SkillPickerRowHeight = 140;
    public const int SkillPickerCheckboxRoiLeft = 20;
    public const int SkillPickerCheckboxRoiWidth = 150;
    public const int SkillPickerCheckboxRoiHeight = 220;

    public const string DefaultRacePath =
        "resource/hachimi/ura/independent_training/races.global.json";
    public const string DefaultSkillPath =
        "resource/hachimi/ura/independent_training/skills.global.json";

    public const string RaceCardTemplateDirectory =
        "templates/independent/race_cards";

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

    /// <summary>
    /// Resolves the configured year/turn/race name to the catalog row whose
    /// Race ID supplies the visual card identity.  No row number or page
    /// position is part of this lookup.
    /// </summary>
    public bool TryGetAgendaPickerEntry(
        IndependentTrainingAgendaSelection selection,
        out IndependentTrainingRace race)
    {
        race = Races.FirstOrDefault(item =>
            item.Year.Equals(selection.Year, StringComparison.OrdinalIgnoreCase)
            && item.Turn.Equals(selection.Turn, StringComparison.OrdinalIgnoreCase)
            && item.RaceName.Equals(selection.RaceName, StringComparison.OrdinalIgnoreCase)
            && item.IsGameAvailable)!;
        return race is not null;
    }

    public static string GetRaceCardTemplatePath(int raceId) =>
        Path.Combine(
            RaceCardTemplateDirectory,
            raceId.ToString(CultureInfo.InvariantCulture) + ".png");

    public static string? TryResolveRaceCardImagePath(
        IndependentTrainingRace race,
        string? baseDirectory = null)
    {
        if (race.RaceId <= 0)
            return null;

        var relativePath = GetRaceCardTemplatePath(race.RaceId);
        var packagedRelativePath = Path.Combine(
            "resource",
            "hachimi",
            "ura",
            "screens",
            relativePath);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            candidates.Add(Path.Combine(baseDirectory, relativePath));
            candidates.Add(Path.Combine(baseDirectory, packagedRelativePath));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, packagedRelativePath));
        candidates.Add(Path.GetFullPath(packagedRelativePath));

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, packagedRelativePath));
            directory = directory.Parent;
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    public static string AgendaRaceCardSemanticAction() =>
        "independent.agenda.race.card";

    public static string AgendaRaceCardVerifySemanticAction() =>
        "independent.agenda.race.card.verify";

    public static string LineupScrollTopSemanticAction() =>
        "independent.lineup.scroll.top";

    public static string LineupCollapseSemanticAction() =>
        "independent.lineup.collapse";

    public static string LineupClosedVerifySemanticAction() =>
        "independent.lineup.closed.verify";

    public static string StrategyChangeSemanticAction() =>
        "independent.strategy.change";

    public static string StrategySaveSemanticAction() =>
        "independent.strategy.save";

    public static string StrategyOptionSemanticAction() =>
        "independent.strategy.option";

    public static string StrategyReturnSemanticAction() =>
        "independent.strategy.return";

    /// <summary>
    /// Maps the existing IndependentLineupStrategy setting to the text shown
    /// in the game's Strategy dialog.  This is deliberately separate from
    /// StrategyId, which selects the offline URA turn strategy and must never
    /// drive this dialog.
    /// </summary>
    public static bool TryGetLineupStrategyUiMapping(
        string? strategy,
        out string targetText)
    {
        return TryGetLineupStrategyUiMapping(strategy, out _, out targetText);
    }

    public static bool TryGetLineupStrategyUiMapping(
        string? strategy,
        out string semanticAction,
        out string targetText)
    {
        (semanticAction, targetText) = strategy?.Trim().ToLowerInvariant() switch
        {
            // The setting labels are the full strategy names, but the live
            // Strategy dialog renders its four selectable buttons as the
            // short labels End, Late, Pace and Front.
            "front" => ("independent.strategy.option.front", "Front"),
            "pace" => ("independent.strategy.option.pace", "Pace"),
            "late" => ("independent.strategy.option.late", "Late"),
            "end" => ("independent.strategy.option.end", "End"),
            _ => (string.Empty, string.Empty),
        };
        return semanticAction.Length > 0 && targetText.Length > 0;
    }

    // Every agenda cell has its own JSON task/ROI.  The task still uses the
    // real plus template and ClickSelf; this semantic id only selects the
    // JSON-declared cell, so the caller never invents a coordinate.
    public static string AgendaSlotSemanticAction(IndependentTrainingAgendaSelection selection) =>
        $"independent.agenda.slot.{SemanticSlug(selection.Year)}.{SemanticSlug(selection.Turn)}";

    public static string AgendaYearSemanticAction() =>
        "independent.agenda.year";

    public static string SkillSearchResetSemanticAction() =>
        "independent.skills.search.reset";

    public static string SkillSearchFocusSemanticAction() =>
        "independent.skills.search.focus";

    public static string SkillSearchInputSemanticAction() =>
        "independent.skills.search.input";

    public static string SkillSearchSubmitSemanticAction() =>
        "independent.skills.search.submit";

    public static string SkillSearchCheckboxSemanticAction() =>
        "independent.skills.search.checkbox";

    public static string SkillSearchCheckboxFallbackSemanticAction() =>
        "independent.skills.search.checkbox.fallback";

    /// <summary>
    /// Returns the verified page/visible-row position that may be used only
    /// after the OCR checkbox lookup has failed. The caller must still run
    /// the OCR action first; this helper only exposes explicit catalog data.
    /// </summary>
    public static bool TryGetVerifiedSkillFallback(
        IndependentTrainingSkill skill,
        out int page,
        out int pickerRow)
    {
        page = 0;
        pickerRow = 0;
        if (!skill.IsGameSearchMapped
            || skill.SearchResultRow <= 0
            || string.IsNullOrWhiteSpace(skill.EffectiveSearchText))
        {
            return false;
        }

        page = skill.SearchResultPage;
        pickerRow = skill.SearchResultPickerRow;
        return true;
    }

    /// <summary>
    /// Builds the checkbox search ROI for one verified visible picker row.
    /// The base task remains JSON-owned; this is only a row-specific ROI
    /// override for the verified OCR fallback path.
    /// </summary>
    public static int[] GetSkillPickerCheckboxFallbackRoi(int pickerRow)
    {
        var row = Math.Clamp(pickerRow, 0, SkillPickerVisibleRows - 1);
        return
        [
            SkillPickerCheckboxRoiLeft,
            SkillPickerFirstRowTop + row * SkillPickerRowHeight,
            SkillPickerCheckboxRoiWidth,
            SkillPickerCheckboxRoiHeight,
        ];
    }

    public static string SkillSearchScrollSemanticAction() =>
        "independent.skills.search.scroll";

    public static string PostStartMenuSemanticAction() =>
        "independent.post_start.menu";

    public static string PostStartToHomeSemanticAction() =>
        "independent.post_start.to_home";

    public static string PostStartHomeProbeSemanticAction() =>
        "independent.post_start.home_probe";

    private static string SemanticSlug(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        return slug.Length == 0 ? "unknown" : slug;
    }

    public static IndependentTrainingCatalog Load(string? baseDirectory = null)
    {
        var racePath = ResolvePath(DefaultRacePath, baseDirectory);
        var skillPath = ResolvePath(DefaultSkillPath, baseDirectory);
        var races = ReadRaces(racePath, out var raceSourceUrl);
        var skills = ReadSkills(skillPath, out var skillSourceUrl, out var skillSource);
        return new IndependentTrainingCatalog(
            races,
            skills,
            raceSourceUrl,
            skillSourceUrl,
            skillSource);
    }

    private static string ResolvePath(string relativePath, string? baseDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            candidates.Add(Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        candidates.Add(Path.GetFullPath(relativePath));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IndependentTrainingRace[] ReadRaces(
        string path,
        out string sourceUrl)
    {
        sourceUrl = "https://uma.guide/agenda-planner/";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("sourceUrl", out var source)
                && source.ValueKind == JsonValueKind.String)
            {
                sourceUrl = source.GetString() ?? sourceUrl;
            }

            if (!root.TryGetProperty("races", out var races)
                || races.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return races.EnumerateArray()
                .Select(item => new IndependentTrainingRace(
                    ReadString(item, "raceName"),
                    ReadString(item, "grade"),
                    ReadString(item, "year"),
                    ReadString(item, "turn"),
                    ReadString(item, "type"),
                    ReadString(item, "location"),
                    ReadString(item, "length"),
                    ReadString(item, "lengthM"),
                    ReadInt(item, "raceId"),
                    ReadInt(item, "sourceOrder"),
                    ReadString(item, "gameTrack"),
                    ReadInt(item, "gameDistance"),
                    ReadString(item, "gameGround"),
                    ReadBool(item, "gameAvailable")))
                .Where(item => !string.IsNullOrWhiteSpace(item.RaceName))
                .ToArray();
        }
        catch (Exception) when (FileNotFoundOrInvalid(path))
        {
            return [];
        }
    }

    private static IndependentTrainingSkill[] ReadSkills(
        string path,
        out string sourceUrl,
        out IndependentTrainingSkillSource sourceMetadata)
    {
        sourceUrl = "https://uma.guide/skills/";
        sourceMetadata = IndependentTrainingSkillSource.Unknown;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("sourceUrl", out var source)
                && source.ValueKind == JsonValueKind.String)
            {
                sourceUrl = source.GetString() ?? sourceUrl;
            }

            if (root.TryGetProperty("source", out var sourceObject)
                && sourceObject.ValueKind == JsonValueKind.Object)
            {
                sourceUrl = ReadString(sourceObject, "sourceUrl");
                sourceMetadata = new IndependentTrainingSkillSource(
                    ReadString(sourceObject, "sourceName"),
                    ReadString(sourceObject, "masterSha256"),
                    ReadString(sourceObject, "clientVersion"),
                    ReadString(sourceObject, "region"),
                    ReadString(sourceObject, "sourceType"));
            }

            if (string.IsNullOrWhiteSpace(sourceMetadata.Region)
                && root.TryGetProperty("region", out var region)
                && region.ValueKind == JsonValueKind.String)
            {
                sourceMetadata = sourceMetadata with { Region = region.GetString() ?? string.Empty };
            }

            if (!root.TryGetProperty("skills", out var skills)
                || skills.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return skills.EnumerateArray()
                .Select(item => new IndependentTrainingSkill(
                    ReadInt(item, "skillId"),
                    ReadString(item, "skillName"),
                    ReadString(item, "originalName"),
                    ReadInt(item, "rarity"),
                    ReadInt(item, "gradeValue"),
                    ReadString(item, "skillCategory"),
                    ReadString(item, "effectSummary"),
                    ReadInt(item, "needSkillPoint"),
                    ReadString(item, "activationCondition"),
                    ReadInt(item, "iconId"),
                    ReadString(item, "searchText"),
                    ReadInt(item, "searchResultRow"),
                    ReadBool(item, "gameSearchMapped"),
                    ReadStringArray(item, "aliases"),
                    ReadBool(item, "availableInGlobal"),
                    ReadString(item, "availabilitySource"),
                    ReadBool(item, "singleModeEnabled"),
                    ReadInt(item, "skillCategoryId")))
                // A legacy snapshot may still be present on a user's disk.
                // Its rows remain parseable for old settings, but are not
                // exposed as selectable Global skills without an explicit
                // availability declaration from the Global client.
                .Where(item => item.SkillId > 0
                    && !string.IsNullOrWhiteSpace(item.SkillName)
                    && item.AvailableInGlobal
                    && item.SingleModeEnabled)
                .ToArray();
        }
        catch (Exception) when (FileNotFoundOrInvalid(path))
        {
            return [];
        }
    }

    private static bool FileNotFoundOrInvalid(string path) =>
        !File.Exists(path);

    private static string ReadString(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static bool ReadBool(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out var value)
               && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : false;
    }

    private static string[] ReadStringArray(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()?.Trim() ?? string.Empty)
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
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
    bool IsGameAvailable = false)
{
    public string Key => $"{Year}|{Turn}|{RaceName}";

    public string DisplayLabel =>
        $"{Year} · {Turn} · {RaceName} ({Grade}, {Type} {LengthM})";

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
    int SkillCategoryId = 0)
{
    public IReadOnlyList<string> SearchAliases => Aliases ?? [];

    public string EffectiveSearchText => string.IsNullOrWhiteSpace(SearchText)
        ? SkillName
        : SearchText;

    public string OcrTargetText => SkillName;

    public IEnumerable<string> SearchTerms =>
        new[] { SkillName, OriginalName, SearchText }
            .Concat(SearchAliases)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public int SearchResultPage => Math.Max(0, SearchResultRow - 1)
        / IndependentTrainingCatalog.SkillPickerVisibleRows;

    public int SearchResultPickerRow => Math.Max(0, SearchResultRow - 1)
        % IndependentTrainingCatalog.SkillPickerVisibleRows;

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(EffectSummary)
            ? $"{SkillName} · {SkillId}"
            : $"{SkillName} · {EffectSummary}";
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
