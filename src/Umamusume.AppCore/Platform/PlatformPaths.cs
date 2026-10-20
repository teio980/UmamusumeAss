using System;
using System.IO;

namespace Umamusume.AppCore.Platform;

public static class PlatformPaths
{
    private static readonly object ResourcePathLock = new();
    private static string? _activeResourceDirectory;
    private static string? _sourceResourceDirectory;

    /// <summary>The writable resource tree currently loaded by the native core.</summary>
    public static string GetActiveResourceDirectory()
    {
        lock (ResourcePathLock)
            return _activeResourceDirectory ?? GetDefaultResourceDirectory();
    }

    /// <summary>Returns the parent directory passed to CoreBridge.InitializeAsync.</summary>
    public static string GetActiveAppBaseDirectory() =>
        Directory.GetParent(GetActiveResourceDirectory())?.FullName ?? AppContext.BaseDirectory;

    public static void SetActiveResourceDirectory(string resourceDirectory, string? sourceResourceDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDirectory);
        lock (ResourcePathLock)
        {
            _activeResourceDirectory = Path.GetFullPath(resourceDirectory);
            _sourceResourceDirectory = string.IsNullOrWhiteSpace(sourceResourceDirectory)
                ? null
                : Path.GetFullPath(sourceResourceDirectory);
        }
    }

    /// <summary>
    /// Maps a bundled-resource path or resource-relative path into the active
    /// writable view. Paths outside the bundled and active resource trees pass
    /// through unchanged, which keeps user Save As locations external.
    /// </summary>
    public static string ResolveActiveResourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string activeRoot;
        string sourceRoot;
        lock (ResourcePathLock)
        {
            activeRoot = _activeResourceDirectory ?? GetDefaultResourceDirectory();
            sourceRoot = _sourceResourceDirectory ?? GetDefaultResourceDirectory();
        }

        if (!Path.IsPathFullyQualified(sourcePath))
        {
            var relative = sourcePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relative.StartsWith("resource" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                relative = relative[("resource" + Path.DirectorySeparatorChar).Length..];
            return Path.GetFullPath(Path.Combine(activeRoot, relative));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        if (TryGetRelativePath(activeRoot, fullPath, out _))
            return fullPath;
        if (TryGetRelativePath(sourceRoot, fullPath, out var resourceRelativePath))
            return Path.GetFullPath(Path.Combine(activeRoot, resourceRelativePath));
        return fullPath;
    }

    private static bool TryGetRelativePath(string root, string path, out string relativePath)
    {
        relativePath = Path.GetRelativePath(root, path);
        return relativePath != "."
            && !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string GetDefaultUserDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "UmamusumeAss");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UmamusumeAss");
        }

        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "UmamusumeAss");
    }

    public static string GetDefaultSettingsFilePath()
    {
        return Path.Combine(GetDefaultUserDataDirectory(), "config.json");
    }

    public static string GetDefaultLogFilePath()
    {
        return Path.Combine(GetDefaultUserDataDirectory(), "logs", "latest.log");
    }

    public static string GetDefaultResourceDirectory()
    {
        string baseDir = AppContext.BaseDirectory;

        // In macOS .app bundle, resources are in Contents/Resources
        if (OperatingSystem.IsMacOS())
        {
            string appBundleResources = Path.Combine(baseDir, "..", "Resources");
            if (Directory.Exists(appBundleResources) && Directory.Exists(Path.Combine(appBundleResources, "resource")))
            {
                return Path.GetFullPath(Path.Combine(appBundleResources, "resource"));
            }
        }

        for (DirectoryInfo? dir = new(baseDir); dir != null; dir = dir.Parent)
        {
            string resourceDir = Path.Combine(dir.FullName, "resource");
            if (Directory.Exists(resourceDir)
                && Directory.Exists(Path.Combine(resourceDir, "hachimi"))
                && File.Exists(Path.Combine(resourceDir, "connection.json")))
            {
                return resourceDir;
            }
        }

        return Path.Combine(baseDir, "resource");
    }

    public static string GetDefaultAppBaseDirectory()
    {
        string resourceDir = GetDefaultResourceDirectory();
        return Directory.GetParent(resourceDir)?.FullName ?? AppContext.BaseDirectory;
    }

    public static string FindAdbExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        if (OperatingSystem.IsMacOS())
        {
            // Typical BlueStacks Air paths on macOS
            string[] macCandidates =
            [
                "/Applications/BlueStacks.app/Contents/MacOS/hd-adb",
                "/Applications/BlueStacks Air multi-instance manager.app/Contents/MacOS/hd-adb",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Android/sdk/platform-tools/adb"),
                "/opt/homebrew/bin/adb",
                "/usr/local/bin/adb",
            ];

            foreach (var candidate in macCandidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            string[] winCandidates =
            [
                @"C:\Program Files\BlueStacks_nxt\HD-Adb.exe",
                @"C:\Program Files (x86)\BlueStacks_nxt\HD-Adb.exe",
                @"C:\Program Files\MuMuPlayer-12.0\shell\adb.exe",
                @"C:\leidian\LDPlayer9\adb.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
            ];

            foreach (var candidate in winCandidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        return "adb";
    }
}
