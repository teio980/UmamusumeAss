using System.IO;
using System.Text.Json;

namespace UmamusumeWpfGui.Tests.Services;

/// <summary>Resolves packaged visual fixtures directly from the Core-owned catalog data.</summary>
internal static class CareerTestResourceResolver
{
    public static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json"))
                && (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json"))
                    || File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Career resource manifest.");
    }

    public static string FindUraCapture(string workspaceRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "resource", "hachimi", "ura", "testdata",
                "captures", "runtime_frames", fileName),
            Path.Combine(workspaceRoot, "resource", "hachimi", "career", "race",
                "templates", "runtime_frames", fileName),
            Path.Combine(workspaceRoot, "resource", "hachimi", "ura", "screens",
                "captures", fileName),
            Path.Combine(workspaceRoot, "testdata", "hachimi", "ura", "captures", fileName),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Could not find URA test capture '{fileName}' in the migrated fixture roots.",
                candidates[0]);
    }

    public static string ResolveBuiltInUraVisualResource(string idOrLegacyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrLegacyPath);
        var root = FindWorkspaceRoot();
        var careerRoot = Path.Combine(root, "resource", "hachimi", "career");
        if (Path.IsPathRooted(idOrLegacyPath))
            return Path.GetFullPath(idOrLegacyPath);

        var catalogPath = Path.Combine(careerRoot, "catalog.json");
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var catalogRoot = catalog.RootElement;
        if (TryGet(catalogRoot, "assets", out var assets)
            && TryGet(assets, idOrLegacyPath, out var asset)
            && TryGet(asset, "path", out var assetPath)
            && assetPath.ValueKind == JsonValueKind.String)
        {
            return ResolveUnderCareer(careerRoot, assetPath.GetString()!);
        }

        if (TryGet(catalogRoot, "aliases", out var aliases)
            && TryGet(aliases, idOrLegacyPath, out var alias)
            && alias.ValueKind == JsonValueKind.String)
        {
            return ResolveUnderCareer(careerRoot, alias.GetString()!);
        }

        var separator = idOrLegacyPath.IndexOf(':');
        if (separator > 0
            && TryGet(catalogRoot, "collections", out var collections)
            && TryGet(collections, idOrLegacyPath[..separator], out var collection))
        {
            var collectionRoot = TryGet(collection, "root", out var rootValue)
                ? rootValue.GetString() ?? string.Empty
                : string.Empty;
            var pattern = TryGet(collection, "pattern", out var patternValue)
                ? patternValue.GetString() ?? "{id}"
                : "{id}";
            var fileName = pattern.Replace("{id}", idOrLegacyPath[(separator + 1)..], StringComparison.Ordinal);
            return ResolveUnderCareer(careerRoot, Path.Combine(collectionRoot, fileName));
        }

        if (idOrLegacyPath.StartsWith("templates/runtime_frames/", StringComparison.OrdinalIgnoreCase)
            && TryGet(catalogRoot, "collections", out collections)
            && TryGet(collections, "career.runtime_frame", out collection))
        {
            var name = Path.GetFileName(idOrLegacyPath);
            var collectionRoot = TryGet(collection, "root", out var rootValue)
                ? rootValue.GetString() ?? string.Empty
                : string.Empty;
            var pattern = TryGet(collection, "pattern", out var patternValue)
                ? patternValue.GetString() ?? "{name}.png"
                : "{name}.png";
            return ResolveUnderCareer(careerRoot,
                Path.Combine(collectionRoot, pattern.Replace("{name}", Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal)));
        }

        var candidate = ResolveUnderCareer(careerRoot, idOrLegacyPath);
        if (File.Exists(candidate))
            return candidate;
        var fileNameOnly = Path.GetFileName(idOrLegacyPath);
        var matches = Directory.EnumerateFiles(careerRoot, fileNameOnly, SearchOption.AllDirectories).ToArray();
        if (matches.Length == 1)
            return matches[0];
        throw new FileNotFoundException($"Could not resolve Career visual resource '{idOrLegacyPath}'.", candidate);
    }

    private static string ResolveUnderCareer(string careerRoot, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(careerRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(careerRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A visual fixture path escaped the Career resource directory.");
        return path;
    }

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
