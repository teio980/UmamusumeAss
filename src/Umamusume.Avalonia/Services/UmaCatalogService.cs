using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Umamusume.AppCore.Platform;

namespace Umamusume.Avalonia.Services;

public sealed record TraineeCatalogItem(
    int TraineeId,
    int BaseCharacterId,
    string NameEn,
    string NameJp,
    int Rarity,
    string ImagePath,
    string FallbackImagePath)
{
    private Bitmap? _thumbnail;
    private bool _attemptedLoad;

    public bool IsAvailable { get; init; }
    public bool HasRunnerTemplate { get; init; }
    public string DisplayName => $"{NameEn} (#{TraineeId})";
    public string Subtitle => string.IsNullOrWhiteSpace(NameJp) ? $"#{TraineeId}" : $"{NameJp} · #{TraineeId}";

    public Bitmap? Thumbnail
    {
        get
        {
            if (_attemptedLoad)
                return _thumbnail;
            _attemptedLoad = true;
            try
            {
                if (File.Exists(ImagePath))
                    _thumbnail = DeveloperToolsImageCodec.LoadBitmap(ImagePath, 96);
                else if (File.Exists(FallbackImagePath))
                    _thumbnail = DeveloperToolsImageCodec.LoadBitmap(FallbackImagePath, 96);
            }
            catch
            {
                _thumbnail = null;
            }
            return _thumbnail;
        }
    }
}

public sealed record SupportCardCatalogItem(
    int SupportCardId,
    string NameEn,
    string Type,
    string Rarity,
    string ImagePath)
{
    private Bitmap? _thumbnail;
    private bool _attemptedLoad;

    public bool IsAvailable { get; init; }
    public string DisplayName => $"{NameEn} (#{SupportCardId})";
    public string DisplayType => string.IsNullOrWhiteSpace(Type) ? "Unknown" : Type;

    public Bitmap? Thumbnail
    {
        get
        {
            if (_attemptedLoad)
                return _thumbnail;
            _attemptedLoad = true;
            try
            {
                if (File.Exists(ImagePath))
                    _thumbnail = DeveloperToolsImageCodec.LoadBitmap(ImagePath, 100);
            }
            catch
            {
                _thumbnail = null;
            }
            return _thumbnail;
        }
    }
}

public sealed record SkillCatalogItem(
    int SkillId,
    string Name,
    string Type,
    int? NeedSkillPoint,
    string EffectSummary,
    string OriginalName = "",
    int Rarity = 0,
    int GradeValue = 0,
    string SkillCategory = "",
    string SearchText = "",
    int SearchResultRow = 0,
    bool GameSearchMapped = false,
    IReadOnlyList<string>? Aliases = null,
    bool AvailableInGlobal = false,
    string AvailabilitySource = "",
    bool SingleModeEnabled = false,
    int SkillCategoryId = 0,
    string OtherTiers = "")
{
    public string DisplayName => NeedSkillPoint is { } cost
        ? $"{Name} ({cost} Pt · #{SkillId})"
        : $"{Name} (#{SkillId})";

    public bool IsIndependentSelectable => AvailableInGlobal
        && SingleModeEnabled
        && NeedSkillPoint is > 0
        && !string.IsNullOrWhiteSpace(string.IsNullOrWhiteSpace(SearchText) ? Name : SearchText);

    public string IndependentPickerDisplayName => IsIndependentSelectable
        ? IndependentDisplayName
        : $"{IndependentDisplayName} · unavailable in Add Skills";

    public IEnumerable<string> SearchTerms => new[] { Name, OriginalName, SearchText }
        .Concat(Aliases ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public string IndependentDisplayName
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
            var details = new List<string> { Name, tier };
            if (NeedSkillPoint is > 0)
                details.Add($"{NeedSkillPoint} SP");
            if (!string.IsNullOrWhiteSpace(EffectSummary))
                details.Add(EffectSummary);
            return string.Join(" · ", details);
        }
    }
}

public sealed record IndependentRaceCatalogItem(
    string Key,
    string RaceName,
    string Grade,
    string Year,
    string Turn,
    string Surface,
    string Distance,
    string LengthM,
    string Track,
    string Location = "",
    int RaceId = 0,
    int Month = 0,
    int Day = 0,
    string TimeName = "",
    string HalfName = "",
    bool IsGameAvailable = false,
    int GameDistance = 0,
    string GameGround = "")
{
    public string DisplayName => $"{Year} · {Turn} · {RaceName} ({Grade}, {LengthM}, {Surface})";
    public string ShortDisplayName => $"{RaceName} ({Grade}) · {Track} {LengthM} {Surface}";
    public string EffectiveSurface => string.IsNullOrWhiteSpace(GameGround) ? Surface.Trim() : GameGround.Trim();
    public string Half => Turn.EndsWith("_02", StringComparison.OrdinalIgnoreCase)
        ? "Second Half"
        : Turn.EndsWith("_01", StringComparison.OrdinalIgnoreCase)
            ? "First Half"
            : string.Empty;
    public string Direction => Location.Contains('\u21d2')
        ? "Right"
        : Location.Contains('\u21d0')
            ? "Left"
            : "Unknown";
    public string PickerHeaderTarget
    {
        get
        {
            var track = Track.Trim();
            var ground = EffectiveSurface;
            if (track.Length == 0 || ground.Length == 0 || GameDistance <= 0 || Direction == "Unknown")
                return string.Empty;

            var distance = GameDistance.ToString(CultureInfo.InvariantCulture) + "m";
            var length = string.IsNullOrWhiteSpace(Distance) ? string.Empty : $" ({Distance.Trim()})";
            return $"{track} {ground} {distance}{length} {Direction}";
        }
    }
}

public static class UmaCatalogService
{
    private static readonly object SyncLock = new();
    private static IReadOnlyList<TraineeCatalogItem>? _trainees;
    private static IReadOnlyList<SupportCardCatalogItem>? _supportCards;
    private static IReadOnlyList<SkillCatalogItem>? _skills;
    private static IReadOnlyList<IndependentRaceCatalogItem>? _independentRaces;

    public static IReadOnlyList<TraineeCatalogItem> Trainees
    {
        get
        {
            lock (SyncLock)
            {
                return _trainees ??= LoadTrainees();
            }
        }
    }

    public static IReadOnlyList<SupportCardCatalogItem> SupportCards
    {
        get
        {
            lock (SyncLock)
            {
                return _supportCards ??= LoadSupportCards();
            }
        }
    }

    public static IReadOnlyList<SkillCatalogItem> Skills
    {
        get
        {
            lock (SyncLock)
            {
                return _skills ??= LoadSkills();
            }
        }
    }

    public static IReadOnlyList<IndependentRaceCatalogItem> IndependentRaces
    {
        get
        {
            lock (SyncLock)
            {
                return _independentRaces ??= LoadIndependentRaces();
            }
        }
    }

    public static TraineeCatalogItem? FindTrainee(string? idOrText)
    {
        if (string.IsNullOrWhiteSpace(idOrText))
            return null;
        if (int.TryParse(idOrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return Trainees.FirstOrDefault(item => item.TraineeId == id);
        return Trainees.FirstOrDefault(item => item.DisplayName.Equals(idOrText, StringComparison.OrdinalIgnoreCase)
            || item.NameEn.Equals(idOrText, StringComparison.OrdinalIgnoreCase));
    }

    public static SupportCardCatalogItem? FindSupportCard(string? idOrText)
    {
        if (string.IsNullOrWhiteSpace(idOrText))
            return null;
        if (int.TryParse(idOrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return SupportCards.FirstOrDefault(item => item.SupportCardId == id);
        return SupportCards.FirstOrDefault(item => item.DisplayName.Equals(idOrText, StringComparison.OrdinalIgnoreCase));
    }

    public static SkillCatalogItem? FindSkill(string? idOrText)
    {
        if (string.IsNullOrWhiteSpace(idOrText))
            return null;
        if (int.TryParse(idOrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return Skills.FirstOrDefault(item => item.SkillId == id);
        return Skills.FirstOrDefault(item => item.Name.Equals(idOrText, StringComparison.OrdinalIgnoreCase));
    }

    private static List<TraineeCatalogItem> LoadTrainees()
    {
        try
        {
            var root = PlatformPaths.GetActiveResourceDirectory();
            var jsonPath = Path.Combine(root, "uma", "database", "global", "trainees.json");
            if (!File.Exists(jsonPath))
                return [];

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var maintenanceUmaRoot = Path.Combine(ResolveMaintenanceResourceRoot(root), "uma");
            var activeUmaRoot = Path.Combine(root, "uma");
            var results = new List<TraineeCatalogItem>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("trainee_id", out var idProp) || !idProp.TryGetInt32(out int traineeId))
                    continue;

                int baseCharId = item.TryGetProperty("base_character_id", out var baseProp) && baseProp.TryGetInt32(out int b)
                    ? b
                    : traineeId / 100;
                string nameEn = item.TryGetProperty("name_en", out var enProp) ? enProp.GetString() ?? string.Empty : string.Empty;
                string nameJp = item.TryGetProperty("name_jp", out var jpProp) ? jpProp.GetString() ?? string.Empty : string.Empty;
                int rarity = item.TryGetProperty("rarity", out var rarProp) && rarProp.TryGetInt32(out int r) ? r : 3;

                string primaryImg = Path.Combine(root, "uma", "assets", "images", "global", "trainees", $"{traineeId}.webp");
                string fallbackImg = Path.Combine(root, "uma", "system_reference", $"{traineeId}.webp");
                if (!File.Exists(fallbackImg))
                    fallbackImg = Path.Combine(root, "uma", "system_reference", $"{baseCharId}_live.webp");

                var hasRunnerTemplate = File.Exists(Path.Combine(maintenanceUmaRoot, "system_reference", $"{traineeId}.webp"))
                    || File.Exists(Path.Combine(activeUmaRoot, "system_reference", $"{traineeId}.webp"))
                    || File.Exists(primaryImg)
                    || File.Exists(Path.Combine(maintenanceUmaRoot, "system_reference", $"{baseCharId}_live.webp"))
                    || File.Exists(Path.Combine(root, "uma", "assets", "images", "global", "live_outfits", $"{baseCharId}.png"));

                results.Add(new TraineeCatalogItem(
                    traineeId,
                    baseCharId,
                    nameEn,
                    nameJp,
                    rarity,
                    primaryImg,
                    fallbackImg)
                {
                    IsAvailable = ReadBool(item, "available"),
                    HasRunnerTemplate = hasRunnerTemplate,
                });
            }

            return results.OrderBy(t => t.BaseCharacterId).ThenBy(t => t.TraineeId).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<SupportCardCatalogItem> LoadSupportCards()
    {
        try
        {
            var root = PlatformPaths.GetActiveResourceDirectory();
            var jsonPath = Path.Combine(root, "uma", "database", "global", "support_cards.json");
            if (!File.Exists(jsonPath))
                return [];

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<SupportCardCatalogItem>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("support_card_id", out var idProp) || !idProp.TryGetInt32(out int cardId))
                    continue;

                string nameEn = item.TryGetProperty("name_en", out var enProp) ? enProp.GetString() ?? string.Empty : string.Empty;
                string type = item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                string rarity = item.TryGetProperty("rarity", out var rarProp) ? rarProp.GetString() ?? "1" : "1";
                string imgPath = Path.Combine(root, "uma", "assets", "images", "global", "support_cards", $"{cardId}.webp");

                results.Add(new SupportCardCatalogItem(
                    cardId,
                    nameEn,
                    type,
                    rarity,
                    imgPath)
                {
                    IsAvailable = ReadBool(item, "available"),
                });
            }

            return results.OrderBy(c => c.SupportCardId).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<SkillCatalogItem> LoadSkills()
    {
        try
        {
            var root = PlatformPaths.GetActiveResourceDirectory();
            var jsonPath = Path.Combine(root, "uma", "database", "global", "skills.json");
            if (!File.Exists(jsonPath))
                return [];

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("skills", out var skillsArray) || skillsArray.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<SkillCatalogItem>();
            foreach (var item in skillsArray.EnumerateArray())
            {
                if (!item.TryGetProperty("skillId", out var idProp) || !idProp.TryGetInt32(out int skillId))
                    continue;

                string name = item.TryGetProperty("skillName", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                string originalName = ReadString(item, "originalName");
                string type = item.TryGetProperty("skillType", out var typeProp) ? typeProp.GetString() ?? "Normal" : "Normal";
                // Unique skills can have a null cost; TryGetInt32 requires a JSON number.
                int? needPt = item.TryGetProperty("needSkillPoint", out var ptProp)
                    && ptProp.ValueKind == JsonValueKind.Number
                    && ptProp.TryGetInt32(out int pt) ? pt : null;
                string effect = item.TryGetProperty("effectSummary", out var effProp) ? effProp.GetString() ?? string.Empty : string.Empty;
                int rarity = ReadInt(item, "rarity");
                int gradeValue = ReadInt(item, "gradeValue");
                string skillCategory = ReadString(item, "skillCategory");
                string searchText = ReadString(item, "searchText");
                int searchResultRow = ReadInt(item, "searchResultRow");
                bool gameSearchMapped = ReadBool(item, "gameSearchMapped");
                bool availableInGlobal = ReadBool(item, "availableInGlobal");
                string availabilitySource = ReadString(item, "availabilitySource");
                bool singleModeEnabled = ReadBool(item, "singleModeEnabled");
                int skillCategoryId = ReadInt(item, "skillCategoryId");
                string otherTiers = ReadString(item, "otherTiers");
                var aliases = item.TryGetProperty("aliases", out var aliasesElement)
                    && aliasesElement.ValueKind == JsonValueKind.Array
                    ? aliasesElement.EnumerateArray()
                        .Where(alias => alias.ValueKind == JsonValueKind.String)
                        .Select(alias => alias.GetString()?.Trim() ?? string.Empty)
                        .Where(alias => alias.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : [];

                results.Add(new SkillCatalogItem(
                    skillId, name, type, needPt, effect, originalName, rarity, gradeValue,
                    skillCategory, searchText, searchResultRow, gameSearchMapped, aliases,
                    availableInGlobal, availabilitySource, singleModeEnabled, skillCategoryId, otherTiers));
            }

            return results.OrderBy(s => s.Name).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ResolveMaintenanceResourceRoot(string runtimeResourceRoot)
    {
        var runtimeBaseDirectory = Directory.GetParent(runtimeResourceRoot)?.FullName
            ?? runtimeResourceRoot;
        for (var directory = new DirectoryInfo(runtimeBaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourceResourceRoot = Path.Combine(directory.FullName, "resource");
            var sourceDatabaseDirectory = Path.Combine(sourceResourceRoot, "uma", "database", "global");
            var sourceProjectDirectory = Path.Combine(directory.FullName, "src", "UmamusumeWpfGui");
            if (Directory.Exists(sourceDatabaseDirectory) && Directory.Exists(sourceProjectDirectory))
                return sourceResourceRoot;
        }

        return runtimeResourceRoot;
    }

    private static List<IndependentRaceCatalogItem> LoadIndependentRaces()
    {
        try
        {
            var root = PlatformPaths.GetActiveResourceDirectory();
            var jsonPath = Path.Combine(root, "hachimi", "ura", "independent_training", "races.global.json");
            if (!File.Exists(jsonPath))
                return [];

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("races", out var racesArray) || racesArray.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<IndependentRaceCatalogItem>();
            foreach (var item in racesArray.EnumerateArray())
            {
                string raceName = item.TryGetProperty("raceName", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                string grade = item.TryGetProperty("grade", out var gradeProp) ? gradeProp.GetString() ?? "OP" : "OP";
                string year = item.TryGetProperty("year", out var yrProp) ? yrProp.GetString() ?? string.Empty : string.Empty;
                string turn = item.TryGetProperty("turn", out var turnProp) ? turnProp.GetString() ?? string.Empty : string.Empty;
                string surface = item.TryGetProperty("type", out var surfProp) ? surfProp.GetString() ?? "Turf" : "Turf";
                string distance = item.TryGetProperty("length", out var distProp) ? distProp.GetString() ?? "Medium" : "Medium";
                string lengthM = item.TryGetProperty("lengthM", out var lenProp) ? lenProp.GetString() ?? string.Empty : string.Empty;
                string track = item.TryGetProperty("gameTrack", out var trProp) ? trProp.GetString() ?? string.Empty : string.Empty;
                string location = item.TryGetProperty("location", out var locationProp) ? locationProp.GetString() ?? string.Empty : string.Empty;
                int raceId = item.TryGetProperty("raceId", out var raceIdProp) && raceIdProp.TryGetInt32(out var parsedRaceId)
                    ? parsedRaceId : 0;
                bool gameAvailable = item.TryGetProperty("gameAvailable", out var availableProp)
                    && availableProp.ValueKind == JsonValueKind.True;
                int gameDistance = ReadInt(item, "gameDistance");
                string gameGround = ReadString(item, "gameGround");
                int month = ScheduleInteger(item, "month");
                int day = ScheduleInteger(item, "day");
                string timeName = ScheduleString(item, "timeName");
                string halfName = ScheduleString(item, "halfName");

                string key = $"{year}|{turn}|{raceName}";
                results.Add(new IndependentRaceCatalogItem(
                    key,
                    raceName,
                    grade,
                    year,
                    turn,
                    surface,
                    distance,
                    lengthM,
                    track,
                    location,
                    raceId,
                    month,
                    day,
                    timeName,
                    halfName,
                    gameAvailable,
                    gameDistance,
                    gameGround));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    public static string? FindIndependentRaceCardPath(IndependentRaceCatalogItem race)
    {
        if (race.RaceId <= 0)
            return null;

        var path = Path.Combine(
            PlatformPaths.GetActiveResourceDirectory(),
            "hachimi", "career", "modes", "independent", "templates", "independent", "race_cards",
            $"{race.RaceId.ToString(CultureInfo.InvariantCulture)}.png");
        return File.Exists(path) ? path : null;
    }

    public static bool IsIndependentRaceExecutable(IndependentRaceCatalogItem race)
    {
        if (!race.IsGameAvailable || race.RaceId <= 0 || string.IsNullOrWhiteSpace(race.PickerHeaderTarget))
            return false;

        var slotRaces = IndependentRaces.Where(item => item.IsGameAvailable
            && item.Year.Equals(race.Year, StringComparison.OrdinalIgnoreCase)
            && item.Turn.Equals(race.Turn, StringComparison.OrdinalIgnoreCase));
        return slotRaces.Any(item => item.RaceId == race.RaceId
                && item.PickerHeaderTarget.Equals(race.PickerHeaderTarget, StringComparison.OrdinalIgnoreCase))
            && !slotRaces.Any(item => item.RaceId != race.RaceId
                && item.PickerHeaderTarget.Equals(race.PickerHeaderTarget, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScheduleInteger(JsonElement race, string property)
    {
        if (!race.TryGetProperty("gameSchedules", out var schedules) || schedules.ValueKind != JsonValueKind.Array)
            return 0;

        foreach (var schedule in schedules.EnumerateArray())
        {
            if (schedule.TryGetProperty(property, out var value)
                && value.TryGetInt32(out var result)
                && (property == "month" ? result is >= 1 and <= 12 : result > 0))
                return result;
        }

        return 0;
    }

    private static string ScheduleString(JsonElement race, string property)
    {
        if (!race.TryGetProperty("gameSchedules", out var schedules) || schedules.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var schedule in schedules.EnumerateArray())
        {
            if (schedule.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        }

        return string.Empty;
    }
}
