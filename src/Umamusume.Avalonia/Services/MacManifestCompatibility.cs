using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public static class MacManifestCompatibility
{
    public static string CurrentAppVersion()
    {
        string? path = MacBundleFiles.FindResourceFile(AppContext.BaseDirectory, "version.txt");
        if (path is not null) return File.ReadAllText(path).Trim();
        return typeof(MacManifestCompatibility).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static bool IsCompatible(UpdateManifest manifest, string appVersion)
    {
        if (!Version.TryParse(manifest.MinOsVersion, out var minimumOs)
            || minimumOs < new Version(14, 0)
            || !OperatingSystem.IsMacOSVersionAtLeast(minimumOs.Major, minimumOs.Minor))
            return false;
        if (!SemVersion.TryParse(appVersion, out var currentApp)
            || !SemVersion.TryParse(manifest.MinAppVersion, out var minimumApp)
            || currentApp < minimumApp)
            return false;
        if (!string.Equals(manifest.ResourceSchema, "1", StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(manifest.MaxAppVersion))
        {
            if (!SemVersion.TryParse(manifest.MaxAppVersion, out var maximumApp)
                || currentApp > maximumApp
                || minimumApp > maximumApp)
                return false;
        }
        return true;
    }
}

internal static class MacBundleFiles
{
    public static string? FindResourceFile(string applicationBaseDirectory, string fileName)
    {
        string legacyPath = Path.Combine(applicationBaseDirectory, fileName);
        if (File.Exists(legacyPath)) return legacyPath;
        string bundleResourcesPath = Path.GetFullPath(Path.Combine(applicationBaseDirectory, "..", "Resources", fileName));
        return File.Exists(bundleResourcesPath) ? bundleResourcesPath : null;
    }
}
