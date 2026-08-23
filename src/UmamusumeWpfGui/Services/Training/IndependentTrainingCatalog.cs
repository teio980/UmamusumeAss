using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Immutable, offline data used by the Independent Training setup editor.
/// The JSON files are generated from uma.guide's Global data exports and are
/// shipped with the application so a run never depends on a live web page.
/// </summary>
public sealed class IndependentTrainingCatalog
{
    public const int AgendaPickerVisibleRows = 6;
    public const int SkillPickerVisibleRows = 8;

    public const string DefaultRacePath =
        "resource/hachimi/ura/independent_training/races.global.json";
    public const string DefaultSkillPath =
        "resource/hachimi/ura/independent_training/skills.global.json";
    public const string DefaultExecutionAssetPath =
        "resource/hachimi/ura/independent_training/execution_assets.global.json";

    private IndependentTrainingCatalog(
        IReadOnlyList<IndependentTrainingRace> races,
        IReadOnlyList<IndependentTrainingSkill> skills,
        IReadOnlyList<IndependentAgendaExecutionAsset> agendaAssets,
        IReadOnlyList<IndependentSkillExecutionAsset> skillAssets,
        string raceSourceUrl,
        string skillSourceUrl)
    {
        Races = races;
        Skills = skills;
        AgendaExecutionAssets = agendaAssets;
        SkillExecutionAssets = skillAssets;
        RaceSourceUrl = raceSourceUrl;
        SkillSourceUrl = skillSourceUrl;
    }

    public IReadOnlyList<IndependentTrainingRace> Races { get; }

    public IReadOnlyList<IndependentTrainingSkill> Skills { get; }

    public IReadOnlyList<IndependentAgendaExecutionAsset> AgendaExecutionAssets { get; }

    public IReadOnlyList<IndependentSkillExecutionAsset> SkillExecutionAssets { get; }

    public string RaceSourceUrl { get; }

    public string SkillSourceUrl { get; }

    public bool IsAvailable => Races.Count > 0 && Skills.Count > 0;

    public bool TryGetAgendaExecution(
        IndependentTrainingAgendaSelection selection,
        out IndependentAgendaExecutionAsset asset)
    {
        asset = AgendaExecutionAssets.FirstOrDefault(item =>
            item.Year.Equals(selection.Year, StringComparison.OrdinalIgnoreCase)
            && item.Turn.Equals(selection.Turn, StringComparison.OrdinalIgnoreCase)
            && item.RaceName.Equals(selection.RaceName, StringComparison.OrdinalIgnoreCase))!;
        return asset is not null;
    }

    /// <summary>
    /// Resolves a catalog race to the stable row order observed in the
    /// Global Independent picker. This intentionally does not consult the
    /// reviewed screenshot registry: row/page navigation is generic JSON
    /// behavior and covers every mapped catalog entry.
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

    public bool TryGetSkillExecution(int skillId, out IndependentSkillExecutionAsset asset)
    {
        asset = SkillExecutionAssets.FirstOrDefault(item => item.SkillId == skillId)!;
        return asset is not null;
    }

    /// <summary>
    /// Every agenda race uses the same OCR task.  The caller supplies the
    /// catalog-derived visible picker header as a runtime target override;
    /// no race name or screenshot/template identity is encoded in the
    /// semantic action.
    /// </summary>
    public static string AgendaSemanticAction(IndependentTrainingAgendaSelection selection) =>
        "independent.agenda.race";

    public static string AgendaRaceVerifySemanticAction() =>
        "independent.agenda.race.verify";

    // Every agenda cell has its own JSON task/ROI.  The task still uses the
    // real plus template and ClickSelf; this semantic id only selects the
    // JSON-declared cell, so the caller never invents a coordinate.
    public static string AgendaSlotSemanticAction(IndependentTrainingAgendaSelection selection) =>
        $"independent.agenda.slot.{SemanticSlug(selection.Year)}.{SemanticSlug(selection.Turn)}";

    public static string AgendaYearSemanticAction() =>
        "independent.agenda.year";

    public static string AgendaRaceSemanticAction() =>
        "independent.agenda.race";

    public static string AgendaScrollSemanticAction() =>
        "independent.agenda.scroll";

    public static string AgendaRowSemanticAction(int rowIndex) =>
        $"independent.agenda.row.{Math.Clamp(rowIndex, 0, AgendaPickerVisibleRows - 1) + 1}";

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
        var assetPath = ResolvePath(DefaultExecutionAssetPath, baseDirectory);
        var races = ReadRaces(racePath, out var raceSourceUrl);
        var skills = ReadSkills(skillPath, out var skillSourceUrl);
        ReadExecutionAssets(assetPath, out var agendaAssets, out var skillAssets);
        return new IndependentTrainingCatalog(
            races,
            skills,
            agendaAssets,
            skillAssets,
            raceSourceUrl,
            skillSourceUrl);
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
                    ReadInt(item, "gameOrder"),
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
        out string sourceUrl)
    {
        sourceUrl = "https://uma.guide/skills/";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("sourceUrl", out var source)
                && source.ValueKind == JsonValueKind.String)
            {
                sourceUrl = source.GetString() ?? sourceUrl;
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
                    ReadBool(item, "gameSearchMapped")))
                .Where(item => item.SkillId > 0 && !string.IsNullOrWhiteSpace(item.SkillName))
                .ToArray();
        }
        catch (Exception) when (FileNotFoundOrInvalid(path))
        {
            return [];
        }
    }

    private static void ReadExecutionAssets(
        string path,
        out IReadOnlyList<IndependentAgendaExecutionAsset> agendaAssets,
        out IReadOnlyList<IndependentSkillExecutionAsset> skillAssets)
    {
        agendaAssets = [];
        skillAssets = [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("agenda", out var agenda)
                && agenda.ValueKind == JsonValueKind.Array)
            {
                agendaAssets = agenda.EnumerateArray()
                    .Select(item => new IndependentAgendaExecutionAsset(
                        ReadString(item, "year"),
                        ReadString(item, "turn"),
                        ReadString(item, "raceName"),
                        ReadString(item, "slotAction"),
                        ReadString(item, "template")))
                    .Where(item => !string.IsNullOrWhiteSpace(item.RaceName)
                        && !string.IsNullOrWhiteSpace(item.SlotAction)
                        && !string.IsNullOrWhiteSpace(item.Template))
                    .ToArray();
            }

            if (root.TryGetProperty("skills", out var skills)
                && skills.ValueKind == JsonValueKind.Array)
            {
                skillAssets = skills.EnumerateArray()
                    .Select(item => new IndependentSkillExecutionAsset(
                        ReadInt(item, "skillId"),
                        ReadString(item, "template")))
                    .Where(item => item.SkillId > 0 && !string.IsNullOrWhiteSpace(item.Template))
                    .ToArray();
            }
        }
        catch (Exception) when (FileNotFoundOrInvalid(path))
        {
            // An absent registry is a safe, empty executable set. The UI and
            // pipeline then reject selected entries explicitly.
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
    int GameOrder = -1,
    string GameTrack = "",
    int GameDistance = 0,
    string GameGround = "",
    bool IsGameAvailable = false)
{
    public string Key => $"{Year}|{Turn}|{RaceName}";

    /// <summary>
    /// Text rendered in the picker header for this catalog row.  The race
    /// name is normally rendered inside a decorative image, which is not a
    /// reliable OCR source; the adjacent course header is ordinary text and
    /// carries the same race identity through track, surface, distance,
    /// length and direction.
    /// </summary>
    public string PickerHeaderTarget
    {
        get
        {
            var track = GameTrack.Trim();
            var ground = string.IsNullOrWhiteSpace(GameGround)
                ? Type.Trim()
                : GameGround.Trim();
            var direction = Location.Contains('⇒')
                ? "Right"
                : Location.Contains('⇐')
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

    public string DisplayLabel =>
        $"{Year} · {Turn} · {RaceName} ({Grade}, {Type} {LengthM})";

    public int PickerPage => GameOrder < 0
        ? -1
        : GameOrder / IndependentTrainingCatalog.AgendaPickerVisibleRows;

    public int PickerRow => GameOrder < 0
        ? -1
        : GameOrder % IndependentTrainingCatalog.AgendaPickerVisibleRows;
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
    bool IsGameSearchMapped = false)
{
    public string EffectiveSearchText => string.IsNullOrWhiteSpace(SearchText)
        ? SkillName
        : SearchText;

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

public sealed record IndependentAgendaExecutionAsset(
    string Year,
    string Turn,
    string RaceName,
    string SlotAction,
    string Template)
{
    public string Key => $"{Year}|{Turn}|{RaceName}";
}

public sealed record IndependentSkillExecutionAsset(int SkillId, string Template);
