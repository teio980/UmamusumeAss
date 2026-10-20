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

    public string DisplayName => $"{NameEn} (#{SupportCardId})";

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
    string EffectSummary)
{
    public string DisplayName => NeedSkillPoint is { } cost
        ? $"{Name} ({cost} Pt · #{SkillId})"
        : $"{Name} (#{SkillId})";
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
    string Track)
{
    public string DisplayName => $"{Year} · {Turn} · {RaceName} ({Grade}, {LengthM}, {Surface})";
    public string ShortDisplayName => $"{RaceName} ({Grade}) · {Track} {LengthM} {Surface}";
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

                results.Add(new TraineeCatalogItem(
                    traineeId,
                    baseCharId,
                    nameEn,
                    nameJp,
                    rarity,
                    primaryImg,
                    fallbackImg));
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
                string type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "Unknown" : "Unknown";
                string rarity = item.TryGetProperty("rarity", out var rarProp) ? rarProp.GetString() ?? "1" : "1";
                string imgPath = Path.Combine(root, "uma", "assets", "images", "global", "support_cards", $"{cardId}.webp");

                results.Add(new SupportCardCatalogItem(
                    cardId,
                    nameEn,
                    type,
                    rarity,
                    imgPath));
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
                string type = item.TryGetProperty("skillType", out var typeProp) ? typeProp.GetString() ?? "Normal" : "Normal";
                int? needPt = item.TryGetProperty("needSkillPoint", out var ptProp) && ptProp.TryGetInt32(out int pt) ? pt : null;
                string effect = item.TryGetProperty("effectSummary", out var effProp) ? effProp.GetString() ?? string.Empty : string.Empty;

                results.Add(new SkillCatalogItem(skillId, name, type, needPt, effect));
            }

            return results.OrderBy(s => s.Name).ToList();
        }
        catch
        {
            return [];
        }
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
                    track));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }
}
