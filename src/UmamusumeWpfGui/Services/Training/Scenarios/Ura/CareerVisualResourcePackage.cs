using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>A declared visual asset and the matching metadata authored beside it.</summary>
public sealed record CareerVisualResourceDefinition(
    string Id,
    string RelativePath,
    string Path,
    int[]? Roi = null,
    double? Threshold = null,
    string? SourceFile = null);

/// <summary>Named OCR or coordinate region with its authored geometry and policy.</summary>
public sealed record CareerVisualRegionDefinition(
    string Id,
    int[]? Roi = null,
    int[]? FocusedRoi = null,
    double? Threshold = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    string? SourceFile = null);

/// <summary>A data-driven family of visuals whose concrete files are selected at runtime.</summary>
public sealed record CareerVisualResourceCollectionDefinition(
    string Id,
    string RootDirectory,
    string Pattern,
    IReadOnlyList<string> LegacyPatterns);

/// <summary>
/// Runtime view of the Career visual package. Fragment paths and source maps
/// stay here rather than being serialized into runtime task definitions.
/// </summary>
public sealed class CareerVisualResourcePackage
{
    private readonly string _sharedRoot;
    private readonly string _scenarioRoot;
    private readonly Dictionary<string, CareerVisualResourceDefinition> _assets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _legacyPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousLegacyPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitLegacyPathKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CareerVisualRegionDefinition> _regions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CareerVisualResourceCollectionDefinition> _collections =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _screenSourceFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _taskSourceFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _taskTemplatePaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _originalTaskTemplates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _originalTaskTransitionTemplates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _resolvedTaskTransitionTemplates =
        new(StringComparer.OrdinalIgnoreCase);

    internal CareerVisualResourcePackage(string sharedRoot, string scenarioRoot)
    {
        _sharedRoot = Path.GetFullPath(sharedRoot);
        _scenarioRoot = Path.GetFullPath(scenarioRoot);
    }

    public IReadOnlyDictionary<string, CareerVisualResourceDefinition> Assets => _assets;

    public IReadOnlyDictionary<string, CareerVisualRegionDefinition> Regions => _regions;

    public IReadOnlyDictionary<string, string> NamedAssets =>
        _assets.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, CareerVisualResourceCollectionDefinition> DynamicCollections => _collections;

    /// <summary>Screen ID to its originating profile fragment.</summary>
    public IReadOnlyDictionary<string, string> ScreenSourceFiles => _screenSourceFiles;

    /// <summary>Task ID to its originating execution fragment.</summary>
    public IReadOnlyDictionary<string, string> TaskSourceFiles => _taskSourceFiles;

    /// <summary>Resolves a declared ID or an older resource-relative path.</summary>
    public string ResolveVisualResource(string idOrLegacyRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrLegacyRelativePath);
        var key = NormalizeKey(idOrLegacyRelativePath);
        if (_assets.TryGetValue(key, out var asset))
            return asset.Path;
        if (_legacyPaths.TryGetValue(key, out var resolved))
            return resolved;
        if (_ambiguousLegacyPaths.Contains(key))
            throw new InvalidDataException(
                $"Career visual resource alias '{idOrLegacyRelativePath}' is ambiguous across source fragments.");

        foreach (var collection in _collections.Values)
        {
            if (TryResolveCollection(collection, key, out resolved))
            {
                if (!File.Exists(resolved))
                    throw new FileNotFoundException(
                        $"Career visual collection '{collection.Id}' has no match for '{idOrLegacyRelativePath}'.",
                        resolved);
                return resolved;
            }
        }

        if (Path.IsPathRooted(idOrLegacyRelativePath))
            return ValidateAllowed(Path.GetFullPath(idOrLegacyRelativePath), idOrLegacyRelativePath);
        throw new FileNotFoundException(
            $"Career visual resource '{idOrLegacyRelativePath}' is not declared in the package catalog.");
    }

    public bool TryGetAsset(string id, out CareerVisualResourceDefinition? definition)
    {
        if (_assets.TryGetValue(id, out var found))
        {
            definition = found;
            return true;
        }

        definition = null;
        return false;
    }

    public bool TryGetRegion(string id, out CareerVisualRegionDefinition? definition)
    {
        if (_regions.TryGetValue(id, out var found))
        {
            definition = found;
            return true;
        }

        definition = null;
        return false;
    }

    public string ResolveScreenTemplate(UraScreenDefinition screen, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var baseDirectory = screen.SourceDirectory
            ?? Path.Combine(_scenarioRoot, "screens");
        return ResolveFromOrigin(baseDirectory, relativePath, $"screen '{screen.ScreenId}'");
    }

    public string ResolveTaskTemplate(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (_taskTemplatePaths.TryGetValue(taskId, out var path))
            return path;
        throw new KeyNotFoundException($"Career visual task '{taskId}' has no template resource.");
    }

    public bool TryGetResolvedTaskTemplate(string taskId, out string? path)
    {
        if (_taskTemplatePaths.TryGetValue(taskId, out var found))
        {
            path = found;
            return true;
        }

        path = null;
        return false;
    }

    public string? GetOriginalTaskTemplate(string taskId) =>
        _originalTaskTemplates.TryGetValue(taskId, out var path) ? path : null;

    public IReadOnlyList<string> GetOriginalTaskTransitionTemplates(string taskId) =>
        _originalTaskTransitionTemplates.TryGetValue(taskId, out var paths) ? paths : [];

    public IReadOnlyList<string> GetResolvedTaskTransitionTemplates(string taskId) =>
        _resolvedTaskTransitionTemplates.TryGetValue(taskId, out var paths) ? paths : [];

    internal void AddScreenSource(string screenId, string fragmentPath)
    {
        if (!_screenSourceFiles.TryAdd(screenId, Path.GetFullPath(fragmentPath)))
            throw new InvalidDataException($"Duplicate Career screen source mapping for '{screenId}'.");
    }

    internal void AddTaskSource(
        string taskId,
        string fragmentPath,
        string? originalTemplate,
        string? resolvedTemplate,
        IReadOnlyList<string>? originalTransitionTemplates = null,
        IReadOnlyList<string>? resolvedTransitionTemplates = null)
    {
        if (!_taskSourceFiles.TryAdd(taskId, Path.GetFullPath(fragmentPath)))
            throw new InvalidDataException($"Duplicate Career task source mapping for '{taskId}'.");
        if (originalTemplate is not null)
            _originalTaskTemplates[taskId] = originalTemplate;
        if (resolvedTemplate is not null)
            _taskTemplatePaths[taskId] = resolvedTemplate;
        if (originalTransitionTemplates is not null)
            _originalTaskTransitionTemplates[taskId] = originalTransitionTemplates.ToArray();
        if (resolvedTransitionTemplates is not null)
            _resolvedTaskTransitionTemplates[taskId] = resolvedTransitionTemplates.ToArray();
    }

    internal void AddAlias(string legacyPath, string path)
    {
        var key = NormalizeKey(legacyPath);
        var value = ValidateAllowed(Path.GetFullPath(path), legacyPath);
        if (_legacyPaths.TryGetValue(key, out var existing)
            && !existing.Equals(value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Career resource alias '{legacyPath}' resolves to more than one asset.");
        }
        _ambiguousLegacyPaths.Remove(key);
        _legacyPaths[key] = value;
        _explicitLegacyPathKeys.Add(key);
    }

    /// <summary>
    /// Adds a compatibility path authored relative to one source fragment.
    /// Such paths are only safe while they identify one asset in the package;
    /// ambiguous names are omitted so callers must resolve against their
    /// screen or task origin instead of silently selecting the wrong file.
    /// </summary>
    internal void AddSourceAlias(string sourceRelativePath, string path)
    {
        var key = NormalizeKey(sourceRelativePath);
        var value = ValidateAllowed(Path.GetFullPath(path), sourceRelativePath);
        if (_explicitLegacyPathKeys.Contains(key) || _ambiguousLegacyPaths.Contains(key))
            return;
        if (_legacyPaths.TryGetValue(key, out var existing))
        {
            if (!existing.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                _legacyPaths.Remove(key);
                _ambiguousLegacyPaths.Add(key);
            }
            return;
        }
        _legacyPaths.Add(key, value);
    }

    internal void AddAsset(CareerVisualResourceDefinition asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Id)
            || !_assets.TryAdd(asset.Id, asset with
            {
                Path = ValidateAllowed(Path.GetFullPath(asset.Path), asset.Id),
            }))
        {
            throw new InvalidDataException($"Duplicate or empty Career visual asset ID '{asset.Id}'.");
        }
    }

    internal void AddRegion(CareerVisualRegionDefinition region)
    {
        if (string.IsNullOrWhiteSpace(region.Id) || !_regions.TryAdd(region.Id, region))
            throw new InvalidDataException($"Duplicate or empty Career visual region ID '{region.Id}'.");
    }

    internal void AddCollection(CareerVisualResourceCollectionDefinition collection)
    {
        if (string.IsNullOrWhiteSpace(collection.Id)
            || !_collections.TryAdd(collection.Id, collection with
            {
                RootDirectory = ValidateAllowed(Path.GetFullPath(collection.RootDirectory), collection.Id),
            }))
        {
            throw new InvalidDataException(
                $"Duplicate or empty Career visual collection ID '{collection.Id}'.");
        }
    }

    internal string ResolveFromOrigin(string originDirectory, string relativePath, string description)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Career {description} resource '{relativePath}' must be relative.");
        var path = Path.GetFullPath(Path.Combine(
            originDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return ValidateAllowed(path, relativePath);
    }

    internal string ValidateAllowed(string path, string description)
    {
        if (!IsWithin(path, _sharedRoot) && !IsWithin(path, _scenarioRoot))
            throw new InvalidDataException(
                $"Career resource '{description}' escapes the shared or scenario resource roots.");
        return path;
    }

    private bool TryResolveCollection(
        CareerVisualResourceCollectionDefinition collection,
        string candidate,
        out string path)
    {
        foreach (var pattern in collection.LegacyPatterns)
        {
            if (TryMatch(pattern, candidate, out var values))
            {
                var expanded = collection.Pattern;
                foreach (var pair in values)
                    expanded = expanded.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
                path = Path.GetFullPath(Path.Combine(collection.RootDirectory,
                    expanded.Replace('/', Path.DirectorySeparatorChar)));
                path = ValidateAllowed(path, candidate);
                return true;
            }
        }

        if (candidate.StartsWith(collection.Id + ":", StringComparison.OrdinalIgnoreCase))
        {
            var value = candidate[(collection.Id.Length + 1)..];
            var variable = FirstVariable(collection.Pattern);
            if (variable is not null)
            {
                if (string.IsNullOrWhiteSpace(value)
                    || value is "." or ".."
                    || value.Contains('/')
                    || value.Contains('\\'))
                {
                    throw new InvalidDataException(
                        $"Career visual collection '{collection.Id}' requires one non-empty path segment.");
                }
                path = Path.GetFullPath(Path.Combine(collection.RootDirectory,
                    collection.Pattern.Replace("{" + variable + "}", value, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', Path.DirectorySeparatorChar)));
                path = ValidateAllowed(path, candidate);
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static bool TryMatch(string pattern, string candidate, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = pattern.Split('{');
        if (parts.Length == 1)
            return false;

        var cursor = 0;
        foreach (var part in parts)
        {
            var close = part.IndexOf('}');
            var literal = close < 0 ? part : part[(close + 1)..];
            if (close >= 0)
            {
                var name = part[..close];
                var end = literal.Length == 0
                    ? candidate.Length
                    : candidate.IndexOf(literal, cursor, StringComparison.OrdinalIgnoreCase);
                if (end < cursor)
                    return false;
                values[name] = candidate[cursor..end];
                cursor = end;
            }

            if (literal.Length > 0)
            {
                if (!candidate.AsSpan(cursor).StartsWith(literal, StringComparison.OrdinalIgnoreCase))
                    return false;
                cursor += literal.Length;
            }
        }

        return cursor == candidate.Length;
    }

    private static string? FirstVariable(string pattern)
    {
        var start = pattern.IndexOf('{');
        var end = pattern.IndexOf('}', start + 1);
        return start >= 0 && end > start ? pattern[(start + 1)..end] : null;
    }

    private static string NormalizeKey(string key) =>
        key.Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class CareerVisualCatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("assets")]
    public Dictionary<string, CareerVisualCatalogAsset> Assets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("regions")]
    public Dictionary<string, CareerVisualCatalogRegion> Regions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("collections")]
    public Dictionary<string, CareerVisualCatalogCollection> Collections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CareerVisualCatalogAsset
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("roi")]
    public int[]? Roi { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }
}

internal sealed class CareerVisualCatalogRegion
{
    [JsonPropertyName("roi")]
    public int[]? Roi { get; set; }

    [JsonPropertyName("focusedRoi")]
    public int[]? FocusedRoi { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement> Metadata { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CareerVisualCatalogCollection
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("legacyPatterns")]
    public List<string> LegacyPatterns { get; set; } = [];
}
