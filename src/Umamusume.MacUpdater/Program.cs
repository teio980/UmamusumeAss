using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

internal static partial class Program
{
    private const uint RenameSwap = 2;
    private const int AtCurrentWorkingDirectory = -2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record InstallRequest(
        int ParentPid,
        string AppPath,
        string PayloadRoot,
        string PackagePath,
        string ManifestPath,
        string SignaturePath,
        string OperationRoot,
        string OperationId,
        string Nonce);

    private sealed record RecoveryRequest(int ParentPid, string OperationRoot, string OperationId, string Nonce);

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) return 64;
        if (args.Length == 5 && args[0] == "--recover"
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int recoveryParentPid)
            && recoveryParentPid > 0)
        {
            var recovery = new RecoveryRequest(recoveryParentPid, args[2], args[3], args[4]);
            try { return await RunRecoveryAsync(recovery).ConfigureAwait(false); }
            catch (Exception exception)
            {
                await WriteResultAsync(recovery.OperationRoot,
                    new MacHealthResult(recovery.OperationId, recovery.Nonce, false, exception.Message)).ConfigureAwait(false);
                return 1;
            }
        }
        if (args.Length != 10 || args[0] != "--install"
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid)
            || parentPid <= 0) return 64;
        var request = new InstallRequest(parentPid, args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]);
        try
        {
            return await RunInstallAsync(request).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteResultAsync(request.OperationRoot, new MacHealthResult(request.OperationId, request.Nonce, false, exception.Message))
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunRecoveryAsync(RecoveryRequest request)
    {
        string operationRoot = Path.GetFullPath(request.OperationRoot);
        string journalPath = Path.Combine(operationRoot, "journal.json");
        MacUpdateJournal journal = JsonSerializer.Deserialize<MacUpdateJournal>(await File.ReadAllTextAsync(journalPath).ConfigureAwait(false))
            ?? throw new InvalidDataException("Update recovery journal is invalid.");
        if (!journal.OperationId.Equals(request.OperationId, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(journal.Nonce), Encoding.UTF8.GetBytes(request.Nonce))
            || journal.Stage is not ("swapping" or "swapped" or "health-check"))
            throw new InvalidDataException("Update recovery request does not match its journal.");
        string appPath = Path.GetFullPath(journal.AppPath);
        string appParent = Path.GetDirectoryName(appPath)!;
        using var processLock = AcquireLock(Path.Combine(appParent, ".umamusume-update.lock"));
        if (!await WaitForExitAsync(request.ParentPid, TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            throw new TimeoutException("The application did not exit for interrupted-update recovery.");

        string appInstanceLockPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "UmamusumeAss", "app-instance.lock");
        using var appLock = AcquireLock(appInstanceLockPath);
        string manifestPath = Path.Combine(operationRoot, "manifest.json");
        string signaturePath = Path.Combine(operationRoot, "manifest.sig");
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        byte[] signature = ManifestVerifier.DecodeSignature(await File.ReadAllTextAsync(signaturePath).ConfigureAwait(false));
        var verifier = new ManifestVerifier();
        if (!verifier.Verify(manifestBytes, signature)) throw new CryptographicException("Recovery manifest signature is invalid.");
        var manifest = ManifestVerifier.Deserialize(manifestBytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag, "macos", "arm64", allowDeltas: false)
            || !manifest.Kind.Equals("program", StringComparison.Ordinal)
            || !manifest.Version.Equals(journal.TargetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery manifest does not describe the target application.");
        var asset = manifest.Assets.Single(item => item.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        string packagePath = Path.Combine(operationRoot, asset.AssetName);
        string payloadRoot = Path.Combine(operationRoot, "payload");
        await VerifyZipAndPayloadAsync(packagePath, payloadRoot, asset).ConfigureAwait(false);
        await VerifyAppInventoryAsync(appPath, asset).ConfigureAwait(false);
        string helperRelativePath = "UmamusumeAss.app/Contents/Helpers/Umamusume.MacUpdater";
        var helperEntry = asset.Files.SingleOrDefault(file => file.Path.Equals(helperRelativePath, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Signed manifest does not include the recovery helper.");
        string runningHelper = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate recovery helper executable.");
        if (new FileInfo(runningHelper).Length != helperEntry.Size
            || !ManifestVerifier.Sha256File(runningHelper).Equals(helperEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Recovery helper does not match the signed program inventory.");

        string candidateApp = Path.GetFullPath(journal.CandidatePath);
        if (!candidateApp.Equals(Path.Combine(appPath + ".update-new", "UmamusumeAss.app"), StringComparison.Ordinal))
            throw new InvalidDataException("Recovery candidate path is outside the expected staging directory.");
        var transactionHost = new ProcessMacBundleSwapHost(appLock);
        await new MacBundleSwapTransaction(transactionHost)
            .RecoverInterruptedSwapAsync(journalPath, operationRoot, journal).ConfigureAwait(false);
        try { if (Directory.Exists(Path.GetDirectoryName(candidateApp)!)) Directory.Delete(Path.GetDirectoryName(candidateApp)!, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return 2;
    }

    private static async Task<int> RunInstallAsync(InstallRequest request)
    {
        Directory.CreateDirectory(request.OperationRoot);
        EnsurePrivateDirectory(request.OperationRoot);
        await WriteJsonAsync(Path.Combine(request.OperationRoot, "helper-started.json"),
            new { request.OperationId, request.Nonce, Started = true }).ConfigureAwait(false);
        using var processLock = AcquireLock(Path.Combine(Path.GetDirectoryName(request.AppPath)!, ".umamusume-update.lock"));
        await WriteProgressAsync(request, "Verifying signed package and preparing the new app…").ConfigureAwait(false);
        var manifestBytes = await File.ReadAllBytesAsync(request.ManifestPath).ConfigureAwait(false);
        var signature = ManifestVerifier.DecodeSignature(await File.ReadAllTextAsync(request.SignaturePath).ConfigureAwait(false));
        var verifier = new ManifestVerifier();
        if (!verifier.Verify(manifestBytes, signature)) throw new CryptographicException("Update manifest signature is invalid.");
        var manifest = ManifestVerifier.Deserialize(manifestBytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag, "macos", "arm64", allowDeltas: false)
            || !manifest.Kind.Equals("program", StringComparison.Ordinal))
            throw new InvalidDataException("The package is not a signed macOS arm64 program update.");
        var asset = manifest.Assets.Single(item => item.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        if (new FileInfo(request.PackagePath).Length != asset.Size
            || !ManifestVerifier.Sha256File(request.PackagePath).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Update package hash does not match its signed manifest.");
        await VerifyZipAndPayloadAsync(request.PackagePath, request.PayloadRoot, asset).ConfigureAwait(false);
        await WriteProgressAsync(request, "Checking the app signature and replacement volume…").ConfigureAwait(false);

        string appPath = Path.GetFullPath(request.AppPath);
        string appParent = Path.GetDirectoryName(appPath)!;
        string candidatePath = appPath + ".update-new";
        string backupPath = appPath + ".previous";
        EnsureWritableDirectory(appParent);
        if (!Directory.Exists(appPath)) throw new DirectoryNotFoundException("The running application bundle was not found.");
        ProbeSwapSupport(appParent);
        string oldVersion = ReadBundleVersion(appPath);
        if (!SemVersion.TryParse(oldVersion, out var current) || !SemVersion.TryParse(manifest.Version, out var target) || target <= current)
            throw new InvalidDataException("The installed bundle is not older than the verified update.");

        if (Directory.Exists(candidatePath)) Directory.Delete(candidatePath, recursive: true);
        await WriteProgressAsync(request, "Copying the verified app bundle…").ConfigureAwait(false);
        await CopyTreeAsync(request.PayloadRoot, candidatePath).ConfigureAwait(false);
        string candidateApp = Path.Combine(candidatePath, "UmamusumeAss.app");
        if (!Directory.Exists(candidateApp) || !ReadBundleVersion(candidateApp).Equals(manifest.Version, StringComparison.Ordinal))
            throw new InvalidDataException("The candidate app bundle version does not match the signed manifest.");
        await VerifyAppInventoryAsync(candidateApp, asset).ConfigureAwait(false);
        await VerifyCodesignAsync(candidateApp).ConfigureAwait(false);
        await WriteProgressAsync(request, "The verified app is ready. Preparing a safe restart…").ConfigureAwait(false);

        var journal = new MacUpdateJournal(request.OperationId, request.Nonce, "prepared", appPath, candidateApp,
            backupPath, manifest.Version, oldVersion, ManifestVerifier.Sha256Bytes(manifestBytes),
            asset.TargetTreeSha256, request.ParentPid);
        string journalPath = Path.Combine(request.OperationRoot, "journal.json");
        await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(request.OperationRoot, "ready.json"), new { request.OperationId, request.Nonce, Ready = true }).ConfigureAwait(false);

        if (!await WaitForExitAsync(request.ParentPid, TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            throw new TimeoutException("Umamusume Assistant did not exit within 30 seconds.");

        using var appLock = AcquireLock(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "UmamusumeAss", "app-instance.lock"));
        var transactionHost = new ProcessMacBundleSwapHost(appLock);
        return await new MacBundleSwapTransaction(transactionHost).InstallAsync(
            journalPath, request.OperationRoot, journal, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
    }

    private static async Task VerifyZipAndPayloadAsync(string packagePath, string payloadRoot, UpdateAsset asset)
    {
        var expected = asset.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > UpdatePathSafety.MaxFiles * 2) throw new InvalidDataException("Package contains too many ZIP entries.");
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (name.Length == 0) continue;
            UpdatePathSafety.ValidateRelative(name);
            int mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            int type = mode & 0xF000;
            bool directory = entry.FullName.EndsWith('/');
            if (type is not (0 or 0x4000 or 0x8000) || (directory && type == 0x8000) || (!directory && type == 0x4000))
                throw new InvalidDataException($"Package contains a link or special ZIP entry: {name}");
            if (directory) continue;
            if (!seen.Add(name) || !expected.TryGetValue(name, out var signedFile))
                throw new InvalidDataException($"Package contains an unsigned or duplicate file: {name}");
            if (entry.Length != signedFile.Size) throw new InvalidDataException($"Package size mismatch: {name}");
            await using var stream = entry.Open();
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            if (!hash.Equals(signedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Package file hash mismatch: {name}");
        }
        if (!seen.SetEquals(expected.Keys)) throw new InvalidDataException("Package file set does not match its signed manifest.");

        string[] actual = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payloadRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected.Keys.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Staged payload file set does not match its signed manifest.");
        foreach (var pair in expected)
        {
            string path = UpdatePathSafety.ResolveUnderRoot(payloadRoot, pair.Key);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Staged payload contains a link.");
            using var stream = File.OpenRead(path);
            if (stream.Length != pair.Value.Size
                || !Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Staged payload hash mismatch: {pair.Key}");
        }
    }

    private static async Task VerifyAppInventoryAsync(string appPath, UpdateAsset asset)
    {
        if ((File.GetAttributes(appPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The active application bundle is a link.");
        const string prefix = "UmamusumeAss.app/";
        var expected = asset.Files.ToDictionary(file =>
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Program inventory contains a path outside the app bundle.");
            return file.Path[prefix.Length..];
        }, StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(appPath, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installed app bundle contains a linked directory.");
        string[] actual = Directory.EnumerateFiles(appPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(appPath, file).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected.Keys.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Installed app file set does not match its signed inventory.");

        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in expected.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string path = UpdatePathSafety.ResolveUnderRoot(appPath, pair.Key);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installed app bundle contains a linked file.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != pair.Value.Size)
                throw new InvalidDataException($"Installed app file size does not match its signed inventory: {pair.Key}");
            tree.AppendData(Encoding.UTF8.GetBytes(prefix + pair.Key));
            tree.AppendData([0]);
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                fileHash.AppendData(buffer, 0, read);
                tree.AppendData(buffer, 0, read);
            }
            string hash = Convert.ToHexString(fileHash.GetHashAndReset());
            if (!hash.Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Installed app file hash does not match its signed inventory: {pair.Key}");
        }
        if (!Convert.ToHexString(tree.GetHashAndReset()).Equals(asset.TargetTreeSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Installed app tree hash does not match its signed inventory.");
    }

    private static async Task VerifyCodesignAsync(string appPath)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/codesign")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "--verify", "--deep", "--strict", appPath }
        }) ?? throw new IOException("Could not start codesign verification.");
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException($"Candidate app signature validation failed: {error}");
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try { using var process = Process.GetProcessById(pid); if (process.HasExited) return true; }
            catch (ArgumentException) { return true; }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<MacHealthResult?> WaitForHealthAsync(
        string path, string operationId, string nonce, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<MacHealthResult>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                    if (result is { OperationId: not null, Nonce: not null }
                        && result.OperationId.Equals(operationId, StringComparison.Ordinal)
                        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(result.Nonce), Encoding.UTF8.GetBytes(nonce)))
                        return result;
                }
                catch (Exception exception) when (exception is IOException or JsonException) { }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string ReadBundleVersion(string appPath)
    {
        var plist = XDocument.Load(Path.Combine(appPath, "Contents", "Info.plist"));
        var key = plist.Descendants("key").FirstOrDefault(element => element.Value == "CFBundleShortVersionString");
        return key?.ElementsAfterSelf("string").FirstOrDefault()?.Value
            ?? throw new InvalidDataException("App bundle has no CFBundleShortVersionString.");
    }

    private static async Task CopyTreeAsync(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private static bool TrySwap(string first, string second, out int error)
    {
        int result = renameatx_np(AtCurrentWorkingDirectory, first, AtCurrentWorkingDirectory, second, RenameSwap);
        error = Marshal.GetLastPInvokeError();
        return result == 0;
    }

    internal sealed class ProcessMacBundleSwapHost(FileStream appLock) : IMacBundleSwapHost
    {
        public string ReadBundleVersion(string appPath) => Program.ReadBundleVersion(appPath);
        public bool TrySwap(string first, string second, out int error) => Program.TrySwap(first, second, out error);
        public Task VerifyCodesignAsync(string appPath) => Program.VerifyCodesignAsync(appPath);
        public Task SaveJournalAsync(string path, MacUpdateJournal journal) => Program.SaveJournalAsync(path, journal);
        public Task WriteResultAsync(string operationRoot, MacHealthResult result) => Program.WriteResultAsync(operationRoot, result);

        public Task<IMacLaunchedApplication> LaunchUpdatedAppAsync(
            string appPath, string operationRoot, string operationId, string nonce)
        {
            appLock.Dispose();
            string executable = Path.Combine(appPath, "Contents", "MacOS", "Umamusume.Avalonia");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                ArgumentList = { "--update-health", operationRoot, operationId, nonce },
            };
            var process = Process.Start(start) ?? throw new IOException("Could not launch the updated application.");
            return Task.FromResult<IMacLaunchedApplication>(new ProcessMacLaunchedApplication(
                process, Path.Combine(operationRoot, "health.json"), operationId, nonce));
        }

        public Task RestartPreviousAppAsync(string appPath)
        {
            appLock.Dispose();
            string executable = Path.Combine(appPath, "Contents", "MacOS", "Umamusume.Avalonia");
            _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
                ?? throw new IOException("Could not restart the previous application.");
            return Task.CompletedTask;
        }

        public Task PreservePreviousBundleAsync(string candidatePath, string backupPath)
        {
            string backupStaging = backupPath + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.Move(candidatePath, backupStaging);
            try
            {
                if (Directory.Exists(backupPath)) Directory.Delete(backupPath, recursive: true);
                Directory.Move(backupStaging, backupPath);
            }
            finally
            {
                if (Directory.Exists(backupStaging) && !Directory.Exists(backupPath))
                    Directory.Move(backupStaging, backupPath);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessMacLaunchedApplication(
        Process process, string healthPath, string operationId, string nonce) : IMacLaunchedApplication
    {
        public Task<MacHealthResult?> WaitForHealthAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Program.WaitForHealthAsync(healthPath, operationId, nonce, timeout, cancellationToken);

        public async Task StopAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static void ProbeSwapSupport(string parent)
    {
        string root = Path.Combine(parent, ".uma-swap-test-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "left");
        string right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            if (!TrySwap(left, right, out int error))
                throw new IOException($"This volume does not support atomic app replacement (renameatx_np error {error}). Move UmamusumeAss.app into ~/Applications and try again.");
            if (!TrySwap(left, right, out error)) throw new IOException($"Could not restore the update capability probe (renameatx_np error {error}).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SaveJournalAsync(string path, MacUpdateJournal journal) => await WriteJsonAsync(path, journal).ConfigureAwait(false);
    private static async Task WriteResultAsync(string root, MacHealthResult result) => await WriteJsonAsync(Path.Combine(root, "result.json"), result).ConfigureAwait(false);

    private static Task WriteProgressAsync(InstallRequest request, string stage) =>
        WriteJsonAsync(Path.Combine(request.OperationRoot, "progress.json"), new { request.OperationId, request.Nonce, Stage = stage });

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    private static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        int descriptor = open(path, 0);
        if (descriptor < 0) throw new IOException($"Could not open journal directory for flush (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (fsync(descriptor) != 0) throw new IOException($"Could not flush journal directory (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { _ = close(descriptor); }
    }

    private static FileStream AcquireLock(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return stream;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureWritableDirectory(string path)
    {
        string probe = Path.Combine(path, ".uma-write-test-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", SetLastError = true, EntryPoint = "renameatx_np", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int renameatx_np(
        int fromDirectory,
        string fromPath,
        int toDirectory,
        string toPath,
        uint flags);

    [LibraryImport("/usr/lib/libSystem.B.dylib", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int close(int descriptor);
}
