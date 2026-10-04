using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

/// <summary>Resolves built-in Career fixtures and catalog assets after the v2 resource split.</summary>
internal static class CareerTestResourceResolver
{
    private static readonly Lazy<UraScenarioPack> BuiltInPack = new(
        () => LoadBuiltInUraPackAsync().GetAwaiter().GetResult(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName, "resource", "hachimi", "ura", "manifest.json"))
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

    public static string ResolveUraVisualResource(UraScenarioPack pack, string idOrLegacyPath)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrLegacyPath);
        var resources = pack.ScreenProfile.VisualResources
            ?? throw new InvalidOperationException("The loaded URA profile has no visual catalog.");
        return resources.ResolveVisualResource(idOrLegacyPath);
    }

    public static string ResolveUraScreenTemplate(
        UraScenarioPack pack,
        UraScreenDefinition screen,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var resources = pack.VisualResources ?? pack.ScreenProfile.VisualResources
            ?? throw new InvalidOperationException("The loaded URA profile has no visual catalog.");
        return resources.ResolveScreenTemplate(screen, relativePath);
    }

    public static string ResolveBuiltInUraVisualResource(string idOrLegacyPath)
    {
        try
        {
            return ResolveUraVisualResource(BuiltInPack.Value, idOrLegacyPath);
        }
        catch (FileNotFoundException) when (
            idOrLegacyPath.StartsWith("templates/runtime_frames/", StringComparison.OrdinalIgnoreCase))
        {
            // Older evidence frames lived under the same path prefix as visual
            // templates. Keep those test-only captures outside the runtime
            // catalog and resolve them through the fixture roots instead.
            return FindUraCapture(FindWorkspaceRoot(), Path.GetFileName(idOrLegacyPath));
        }
    }

    public static Task<UraScenarioPack> LoadBuiltInUraPackAsync(
        CancellationToken cancellationToken = default) =>
        UraScenarioPackLoader.LoadAsync(
            Path.Combine(FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"),
            cancellationToken);
}
