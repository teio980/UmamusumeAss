using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace Umamusume.Avalonia.Services;

public enum UmaDatabaseImageKind
{
    RaceOutfit,
    LiveOutfit,
}

public sealed record UmaDatabaseImageItem(
    int? TraineeId,
    int BaseCharacterId,
    string Name,
    string ImagePath,
    string ReferencePath,
    UmaDatabaseImageKind Kind)
{
    public Bitmap? Thumbnail { get; init; }
    public bool IsLiveOutfit => Kind == UmaDatabaseImageKind.LiveOutfit;
    public string Key => IsLiveOutfit ? $"live:{BaseCharacterId}" : $"race:{TraineeId}";
    public string VariantDisplayName => IsLiveOutfit ? "Live outfit" : "Race outfit";
    public string DisplayName => IsLiveOutfit
        ? $"{Name} ({BaseCharacterId}) · {VariantDisplayName}"
        : $"{Name} ({TraineeId}) · {VariantDisplayName}";
}

/// <summary>Reads the same UmaDatabase metadata and image locations used by the Windows developer tools.</summary>
public sealed class UmaDatabaseImageService
{
    public static IReadOnlyList<UmaDatabaseImageItem> LoadImages(string resourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
        string root = Path.GetFullPath(resourceRoot);
        string umaRoot = Path.Combine(root, "uma");
        var traineeNames = ReadNames(Path.Combine(umaRoot, "database", "global", "trainees.json"), "trainee_id");
        var traineeBases = ReadIds(Path.Combine(umaRoot, "database", "global", "trainees.json"), "trainee_id", "base_character_id");
        var baseNames = ReadNames(Path.Combine(umaRoot, "database", "global", "base_characters.json"), "base_character_id");
        var images = new List<UmaDatabaseImageItem>();
        string traineeDirectory = Path.Combine(umaRoot, "assets", "images", "global", "trainees");
        string liveDirectory = Path.Combine(umaRoot, "assets", "images", "global", "live_outfits");
        string referenceDirectory = Path.Combine(umaRoot, "system_reference");

        if (Directory.Exists(traineeDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(traineeDirectory).Where(IsSupportedImage))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out int traineeId))
                    continue;
                int baseId = traineeBases.GetValueOrDefault(traineeId, traineeId / 100);
                string name = traineeNames.GetValueOrDefault(traineeId, traineeId.ToString(CultureInfo.InvariantCulture));
                images.Add(new UmaDatabaseImageItem(traineeId, baseId, name, Path.GetFullPath(path),
                    Path.Combine(referenceDirectory, traineeId.ToString(CultureInfo.InvariantCulture) + ".webp"),
                    UmaDatabaseImageKind.RaceOutfit));
            }
        }

        if (Directory.Exists(liveDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(liveDirectory).Where(IsSupportedImage))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out int baseId))
                    continue;
                string name = baseNames.GetValueOrDefault(baseId, baseId.ToString(CultureInfo.InvariantCulture));
                images.Add(new UmaDatabaseImageItem(null, baseId, name, Path.GetFullPath(path),
                    Path.Combine(referenceDirectory, baseId.ToString(CultureInfo.InvariantCulture) + "_live.webp"),
                    UmaDatabaseImageKind.LiveOutfit));
            }
        }

        return images.OrderBy(item => item.BaseCharacterId)
            .ThenBy(item => item.IsLiveOutfit ? 1 : 0)
            .ThenBy(item => item.TraineeId)
            .ToArray();
    }

    private static Dictionary<int, string> ReadNames(string path, string idProperty)
    {
        var result = new Dictionary<int, string>();
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty(idProperty, out var idElement) || !idElement.TryGetInt32(out int id)) continue;
            string name = ReadString(row, "name_en") ?? ReadString(row, "name_jp") ?? id.ToString(CultureInfo.InvariantCulture);
            result[id] = name;
        }
        return result;
    }

    private static Dictionary<int, int> ReadIds(string path, string keyProperty, string valueProperty)
    {
        var result = new Dictionary<int, int>();
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty(keyProperty, out var key) && key.TryGetInt32(out int id)
                && row.TryGetProperty(valueProperty, out var value) && value.TryGetInt32(out int mapped))
                result[id] = mapped;
        }
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".webp" or ".png" or ".jpg" or ".jpeg";
}
