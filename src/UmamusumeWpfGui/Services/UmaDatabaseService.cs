using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public sealed class UmaDatabaseService : IUmaDatabaseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _sync = new();
    private Dictionary<int, UmaBaseCharacterRecord> _baseCharacters = [];
    private Dictionary<int, UmaTraineeRecord> _trainees = [];
    private Dictionary<int, UmaSupportCardRecord> _supportCards = [];
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

    public IReadOnlyCollection<UmaSupportCardRecord> SupportCards => Snapshot(_supportCards);

    public async Task LoadAsync(
        string resourceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);

        var databaseDirectory = Path.Combine(resourceRoot, "uma", "database", "global");
        var metaPath = Path.Combine(databaseDirectory, "meta.json");
        var baseCharactersPath = Path.Combine(databaseDirectory, "base_characters.json");
        var traineesPath = Path.Combine(databaseDirectory, "trainees.json");
        var supportCardsPath = Path.Combine(databaseDirectory, "support_cards.json");

        var meta = await ReadJsonAsync<UmaDatabaseMeta>(metaPath, cancellationToken)
            .ConfigureAwait(false) ?? new UmaDatabaseMeta();
        var baseCharacters = await ReadJsonAsync<List<UmaBaseCharacterRecord>>(
                baseCharactersPath,
                cancellationToken)
            .ConfigureAwait(false) ?? [];
        var trainees = await ReadJsonAsync<List<UmaTraineeRecord>>(
                traineesPath,
                cancellationToken)
            .ConfigureAwait(false) ?? [];
        var supportCards = await ReadJsonAsync<List<UmaSupportCardRecord>>(
                supportCardsPath,
                cancellationToken)
            .ConfigureAwait(false) ?? [];

        var baseCharacterIndex = CreateUniqueIndex<int, UmaBaseCharacterRecord>(
            baseCharacters,
            item => item.BaseCharacterId,
            "base character");
        var traineeIndex = CreateUniqueIndex<int, UmaTraineeRecord>(
            trainees,
            item => item.TraineeId,
            "trainee");
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
            _supportCards = supportCardIndex;
        }

        DatabaseLoaded?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetTrainee(int traineeId, out UmaTraineeRecord? trainee)
    {
        lock (_sync)
            return _trainees.TryGetValue(traineeId, out trainee);
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

    public string GetTraineeReferenceImageDirectory() =>
        Path.Combine(GetUmaResourceDirectory(), "system_reference");

    public string GetTraineeReferenceImagePath(int traineeId) =>
        Path.Combine(
            GetTraineeReferenceImageDirectory(),
            traineeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".webp");

    public string GetSupportCardTemplateDirectory(int supportCardId) =>
        GetTemplateDirectory("support_cards", supportCardId);

    private string GetUmaResourceDirectory()
    {
        lock (_sync)
        {
            var resourceRoot = _resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "resource");
            return Path.Combine(resourceRoot, "uma");
        }
    }

    private string GetImageDirectory(string category)
    {
        lock (_sync)
        {
            var resourceRoot = _resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "resource");
            return Path.Combine(
                resourceRoot,
                "uma",
                "assets",
                "images",
                "global",
                category);
        }
    }

    private string GetTemplateDirectory(string category, int id)
    {
        lock (_sync)
        {
            var resourceRoot = _resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "resource");
            return Path.Combine(
                resourceRoot,
                "uma",
                "assets",
                "templates",
                "global",
                category,
                id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Uma database file was not found.", path);

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

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
