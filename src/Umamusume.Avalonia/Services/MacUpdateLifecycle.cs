using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Umamusume.AppCore.Platform;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public sealed record MacUpdateHealthRequest(string OperationRoot, string OperationId, string Nonce);

public static class MacUpdateHealth
{
    private static readonly JsonSerializerOptions Options = new();
    public static MacUpdateHealthRequest? Request { get; internal set; }

    public static void Initialize(string[] args)
    {
        if (args.Length != 4 || args[0] != "--update-health" || !Guid.TryParseExact(args[2], "N", out _)
            || args[3].Length < 32 || args[3].Length > 128) return;
        string root = Path.GetFullPath(args[1]);
        string updatesRoot = Path.GetFullPath(Path.Combine(PlatformPaths.GetDefaultUserDataDirectory(), "updates"));
        string relative = Path.GetRelativePath(updatesRoot, root);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        Request = new(root, args[2], args[3]);
    }

    public static async Task ReportAsync(bool healthy, string? error = null)
    {
        if (Request is not { } request) return;
        var path = Path.Combine(request.OperationRoot, "health.json");
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            OperationId = request.OperationId,
            Nonce = request.Nonce,
            Healthy = healthy,
            Error = error
        }, Options)).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public static class MacAppLifecycle
{
    public static Func<Task>? RequestShutdownAsync { get; set; }

    public static Task RequestUpdateShutdownAsync() =>
        RequestShutdownAsync?.Invoke() ?? Task.FromException(new InvalidOperationException("Application shutdown is not available."));
}

public static class MacAppInstanceGate
{
    private static FileStream? _instanceLock;
    public static string LockPath => Path.Combine(PlatformPaths.GetDefaultUserDataDirectory(), "app-instance.lock");

    public static bool TryAcquire()
    {
        if (!OperatingSystem.IsMacOS()) return true;
        try
        {
            string path = LockPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (IOException) { return false; }
    }

    public static void Release()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;
    }
}

public static class MacUpdateRecovery
{
    public static string? LastRecoveryError { get; private set; }

    /// <summary>
    /// Starts the staged helper to reconcile an orphaned swap before the app
    /// initializes the native core. The helper decides from signed inventory
    /// and bundle versions whether a swap happened, then restores the old app.
    /// </summary>
    public static bool TryStartRecoveryIfNeeded()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        string? appPath = FindAppBundlePath(AppContext.BaseDirectory);
        if (appPath is null) return false;
        string updatesRoot = Path.GetFullPath(Path.Combine(PlatformPaths.GetDefaultUserDataDirectory(), "updates"));
        if (!Directory.Exists(updatesRoot)) return false;
        string currentVersion;
        try { currentVersion = ReadBundleVersion(appPath); }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException or InvalidDataException)
        { return false; }

        foreach (string journalPath in Directory.EnumerateFiles(updatesRoot, "journal.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
                var root = document.RootElement;
                string journalApp = Path.GetFullPath(root.GetProperty("AppPath").GetString()!);
                string stage = root.GetProperty("Stage").GetString() ?? string.Empty;
                string targetVersion = root.GetProperty("TargetVersion").GetString() ?? string.Empty;
                string oldVersion = root.GetProperty("OldVersion").GetString() ?? string.Empty;
                string candidate = Path.GetFullPath(root.GetProperty("CandidatePath").GetString()!);
                string operationId = root.GetProperty("OperationId").GetString() ?? string.Empty;
                string nonce = root.GetProperty("Nonce").GetString() ?? string.Empty;
                if (!journalApp.Equals(Path.GetFullPath(appPath), StringComparison.Ordinal)
                    || stage is not ("swapping" or "swapped" or "health-check")
                    || !currentVersion.Equals(targetVersion, StringComparison.Ordinal)
                    || !Guid.TryParseExact(operationId, "N", out _)
                    || nonce.Length != 64
                    || !IsContainedPath(updatesRoot, journalPath)
                    || !IsContainedPath(appPath + ".update-new", candidate)
                    || !Directory.Exists(candidate)
                    || !ReadBundleVersion(candidate).Equals(oldVersion, StringComparison.Ordinal)) continue;

                string operationRoot = Path.GetDirectoryName(journalPath)!;
                string updaterLock = Path.Combine(Path.GetDirectoryName(appPath)!, ".umamusume-update.lock");
                if (IsLockHeld(updaterLock)) return false;
                if (!TryVerifyRecoveryHelper(operationRoot, targetVersion, out string helperPath, out string validationError))
                {
                    LastRecoveryError = validationError;
                    continue;
                }

                var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, WorkingDirectory = operationRoot };
                start.ArgumentList.Add("--recover");
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(operationRoot);
                start.ArgumentList.Add(operationId);
                start.ArgumentList.Add(nonce);
                _ = Process.Start(start) ?? throw new IOException("Could not start the interrupted-update recovery helper.");
                return true;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException
                                               or InvalidDataException or FormatException or ArgumentException
                                               or System.Xml.XmlException or CryptographicException)
            {
                LastRecoveryError = exception.Message;
            }
        }
        return false;
    }

    public static void TryInitializeHealthFromJournal()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string? appPath = FindAppBundlePath(AppContext.BaseDirectory);
        if (appPath is null) return;
        string updatesRoot = Path.Combine(PlatformPaths.GetDefaultUserDataDirectory(), "updates");
        if (!Directory.Exists(updatesRoot)) return;
        string currentVersion = ReadBundleVersion(appPath);
        foreach (string journalPath in Directory.EnumerateFiles(updatesRoot, "journal.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
                var root = document.RootElement;
                string journalApp = Path.GetFullPath(root.GetProperty("AppPath").GetString()!);
                string stage = root.GetProperty("Stage").GetString() ?? string.Empty;
                string targetVersion = root.GetProperty("TargetVersion").GetString() ?? string.Empty;
                string candidate = root.GetProperty("CandidatePath").GetString() ?? string.Empty;
                string oldVersion = root.GetProperty("OldVersion").GetString() ?? string.Empty;
                string operationId = root.GetProperty("OperationId").GetString() ?? string.Empty;
                string nonce = root.GetProperty("Nonce").GetString() ?? string.Empty;
                if (!Path.GetFullPath(journalApp).Equals(Path.GetFullPath(appPath), StringComparison.Ordinal)
                    || stage is not ("swapping" or "swapped" or "health-check")
                    || !currentVersion.Equals(targetVersion, StringComparison.Ordinal)
                    || !Guid.TryParseExact(operationId, "N", out _)
                    || nonce.Length < 32
                    || !Directory.Exists(candidate)
                    || !ReadBundleVersion(candidate).Equals(oldVersion, StringComparison.Ordinal)
                    || !IsLockHeld(Path.Combine(Path.GetDirectoryName(appPath)!, ".umamusume-update.lock"))) continue;
                MacUpdateHealth.Request = new(Path.GetDirectoryName(journalPath)!, operationId, nonce);
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private static bool TryVerifyRecoveryHelper(string operationRoot, string targetVersion, out string helperPath, out string error)
    {
        helperPath = Path.Combine(operationRoot, "Umamusume.MacUpdater");
        error = string.Empty;
        byte[] manifestBytes = File.ReadAllBytes(Path.Combine(operationRoot, "manifest.json"));
        byte[] signature = ManifestVerifier.DecodeSignature(File.ReadAllText(Path.Combine(operationRoot, "manifest.sig")));
        var verifier = new ManifestVerifier();
        if (!verifier.Verify(manifestBytes, signature)) { error = "The pending update manifest signature is invalid."; return false; }
        var manifest = ManifestVerifier.Deserialize(manifestBytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag, "macos", "arm64", allowDeltas: false)
            || !manifest.Kind.Equals("program", StringComparison.Ordinal)
            || !manifest.Version.Equals(targetVersion, StringComparison.Ordinal))
        { error = "The pending update manifest does not match the installed candidate."; return false; }
        var asset = manifest.Assets.Single(item => item.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        var helper = asset.Files.SingleOrDefault(item => item.Path.Equals(
            "UmamusumeAss.app/Contents/Helpers/Umamusume.MacUpdater", StringComparison.Ordinal));
        if (helper is null || !File.Exists(helperPath)
            || new FileInfo(helperPath).Length != helper.Size
            || !ManifestVerifier.Sha256File(helperPath).Equals(helper.Sha256, StringComparison.OrdinalIgnoreCase))
        { error = "The pending update recovery helper does not match its signed inventory."; return false; }
        return true;
    }

    private static bool IsLockHeld(string path)
    {
        try { using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private static bool IsContainedPath(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? FindAppBundlePath(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return null;
    }

    private static string ReadBundleVersion(string appPath)
    {
        var plist = System.Xml.Linq.XDocument.Load(Path.Combine(appPath, "Contents", "Info.plist"));
        var key = plist.Descendants("key").FirstOrDefault(element => element.Value == "CFBundleShortVersionString");
        return key?.ElementsAfterSelf("string").FirstOrDefault()?.Value ?? string.Empty;
    }
}
