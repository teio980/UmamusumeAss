using System.IO;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public sealed class UmaDatabaseService : IUmaDatabaseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly object _sync = new();
    private Dictionary<int, UmaBaseCharacterRecord> _baseCharacters = [];
    private Dictionary<int, UmaTraineeRecord> _trainees = [];
    private Dictionary<int, UmaCareerRaceRecord> _races = [];
    private Dictionary<int, UmaSupportCardRecord> _supportCards = [];
    private CoreCatalog _catalog = CoreCatalog.Empty;
    private string? _resourceRoot;
    private string? _region;

    public event EventHandler? DatabaseLoaded;

    public bool IsLoaded
    {
        get
        {
            lock (_sync)
                return _trainees.Count > 0 || _supportCards.Count > 0;
        }
    }

    public CoreCatalog Catalog
    {
        get
        {
            lock (_sync)
                return _catalog;
        }
    }

    public string? Region
    {
        get
        {
            lock (_sync)
                return _region;
        }
    }

    public IReadOnlyCollection<UmaBaseCharacterRecord> BaseCharacters => Snapshot(_baseCharacters);

    public IReadOnlyCollection<UmaTraineeRecord> Trainees => Snapshot(_trainees);

    public IReadOnlyCollection<UmaCareerRaceRecord> Races => Snapshot(_races);

    public IReadOnlyCollection<UmaSupportCardRecord> SupportCards => Snapshot(_supportCards);

    public void LoadFromCoreCatalog(CoreCatalog catalog, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
        var baseCharacters = DeserializeRows<UmaBaseCharacterRecord>(catalog.BaseCharacters);
        var trainees = DeserializeRows<UmaTraineeRecord>(catalog.Characters);
        var races = DeserializeRows<UmaCareerRaceRecord>(catalog.Races);
        var supportCards = DeserializeRows<UmaSupportCardRecord>(catalog.SupportCards);
        var meta = catalog.AdditionalCategories.TryGetValue("database_meta", out var metadata)
            ? JsonSerializer.Deserialize<UmaDatabaseMeta>(metadata.GetRawText(), JsonOptions)
            : null;
        meta ??= new UmaDatabaseMeta();

        var baseCharacterIndex = CreateUniqueIndex<int, UmaBaseCharacterRecord>(
            baseCharacters,
            item => item.BaseCharacterId,
            "base character");
        var traineeIndex = CreateUniqueIndex<int, UmaTraineeRecord>(
            trainees,
            item => item.TraineeId,
            "trainee");
        var raceIndex = CreateUniqueIndex<int, UmaCareerRaceRecord>(
            races,
            item => item.RaceId,
            "race");
        var supportCardIndex = CreateUniqueIndex<int, UmaSupportCardRecord>(
            supportCards,
            item => item.SupportCardId,
            "support card");

        lock (_sync)
        {
            _resourceRoot = Path.GetFullPath(resourceRoot);
            _region = meta.Region;
            _baseCharacters = baseCharacterIndex;
            _trainees = traineeIndex;
            _races = raceIndex;
            _supportCards = supportCardIndex;
            _catalog = catalog;
        }

        DatabaseLoaded?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetTrainee(int traineeId, out UmaTraineeRecord? trainee)
    {
        lock (_sync)
            return _trainees.TryGetValue(traineeId, out trainee);
    }

    public bool TryGetRace(int raceId, out UmaCareerRaceRecord? race)
    {
        lock (_sync)
            return _races.TryGetValue(raceId, out race);
    }

    public bool TryGetSupportCard(int supportCardId, out UmaSupportCardRecord? supportCard)
    {
        lock (_sync)
            return _supportCards.TryGetValue(supportCardId, out supportCard);
    }

    public IReadOnlyList<UmaTraineeRecord> FindTraineesByName(string name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
            return [];

        lock (_sync)
        {
            return _trainees.Values
                .Where(item => Normalize(item.NameEn).Contains(normalized, StringComparison.Ordinal)
                    || Normalize(item.NameJp).Contains(normalized, StringComparison.Ordinal))
                .OrderBy(item => item.TraineeId)
                .ToArray();
        }
    }

    public IReadOnlyList<UmaSupportCardRecord> FindSupportCardsByName(string name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
            return [];

        lock (_sync)
        {
            return _supportCards.Values
                .Where(item => Normalize(item.NameEn).Contains(normalized, StringComparison.Ordinal)
                    || Normalize(item.FeaturedCharacterNameEn).Contains(normalized, StringComparison.Ordinal))
                .OrderBy(item => item.SupportCardId)
                .ToArray();
        }
    }

    public IReadOnlyList<UmaSupportCardRecord> GetSupportCardsForCharacter(int baseCharacterId)
    {
        lock (_sync)
        {
            return _supportCards.Values
                .Where(item => item.FeaturedCharacterId == baseCharacterId)
                .OrderBy(item => item.SupportCardId)
                .ToArray();
        }
    }

    public string GetTraineeTemplateDirectory(int traineeId) =>
        GetTemplateDirectory("trainees", traineeId);

    public string GetTraineeImageDirectory() =>
        GetImageDirectory("trainees");

    public string GetTraineeImagePath(int traineeId) =>
        Path.Combine(
            GetTraineeImageDirectory(),
            traineeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".webp");

    public string GetTraineeLiveOutfitImageDirectory() =>
        GetImageDirectory("live_outfits");

    public string GetTraineeLiveOutfitImagePath(int baseCharacterId) =>
        Path.Combine(
            GetTraineeLiveOutfitImageDirectory(),
            baseCharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");

    public string GetTraineeLiveOutfitReferenceImagePath(int baseCharacterId) =>
        Path.Combine(
            GetMaintenanceTraineeReferenceImageDirectory(),
            baseCharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_live.webp");

    public string GetSupportCardImagePath(int supportCardId) =>
        Path.Combine(
            GetImageDirectory("support_cards"),
            supportCardId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".webp");

    public string GetTraineeReferenceImageDirectory() =>
        Path.Combine(GetUmaResourceDirectory(), "system_reference");

    public string GetTraineeReferenceImagePath(int traineeId) =>
        Path.Combine(
            GetTraineeReferenceImageDirectory(),
            traineeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".webp");

    public string GetMaintenanceTraineeReferenceImageDirectory() =>
        Path.Combine(GetMaintenanceUmaResourceDirectory(), "system_reference");

    public string GetMaintenanceTraineeReferenceImagePath(int traineeId) =>
        Path.Combine(
            GetMaintenanceTraineeReferenceImageDirectory(),
            traineeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".webp");

    public string GetSupportCardTemplateDirectory(int supportCardId) =>
        GetTemplateDirectory("support_cards", supportCardId);

    private string GetUmaResourceDirectory()
    {
        return Path.Combine(GetResourceRoot(), "uma");
    }

    private string GetMaintenanceUmaResourceDirectory()
    {
        var resourceRoot = GetResourceRoot();
        return Path.Combine(ResolveMaintenanceResourceRoot(resourceRoot), "uma");
    }

    private string GetResourceRoot()
    {
        lock (_sync)
        {
            return _resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "resource");
        }
    }

    private string GetImageDirectory(string category)
    {
        return Path.Combine(
            GetResourceRoot(),
            "uma",
            "assets",
            "images",
            "global",
            category);
    }

    private string GetTemplateDirectory(string category, int id)
    {
        return Path.Combine(
            GetResourceRoot(),
            "uma",
            "assets",
            "templates",
            "global",
            category,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string ResolveMaintenanceResourceRoot(string runtimeResourceRoot)
    {
        var runtimeBaseDirectory = Directory.GetParent(runtimeResourceRoot)?.FullName
            ?? runtimeResourceRoot;
        for (var directory = new DirectoryInfo(runtimeBaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourceResourceRoot = Path.Combine(directory.FullName, "resource");
            var sourceDatabaseDirectory = Path.Combine(
                sourceResourceRoot,
                "uma",
                "database",
                "global");
            var sourceProjectDirectory = Path.Combine(
                directory.FullName,
                "src",
                "UmamusumeWpfGui");
            if (Directory.Exists(sourceDatabaseDirectory)
                && Directory.Exists(sourceProjectDirectory))
            {
                return sourceResourceRoot;
            }
        }

        return runtimeResourceRoot;
    }

    private static List<T> DeserializeRows<T>(IReadOnlyList<JsonElement> rows) => rows
        .Select(row => JsonSerializer.Deserialize<T>(row.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException($"Core catalog contained a null {typeof(T).Name} row."))
        .ToList();

    private static Dictionary<TKey, TValue> CreateUniqueIndex<TKey, TValue>(
        IEnumerable<TValue> items,
        Func<TValue, TKey> keySelector,
        string kind)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var item in items)
        {
            var key = keySelector(item);
            if (!result.TryAdd(key, item))
                throw new InvalidDataException($"Duplicate {kind} ID '{key}'.");
        }

        return result;
    }

    private static T[] Snapshot<TKey, T>(Dictionary<TKey, T> source)
        where TKey : notnull
    {
        lock (source)
            return source.Values.ToArray();
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToUpperInvariant() ?? string.Empty;
}
