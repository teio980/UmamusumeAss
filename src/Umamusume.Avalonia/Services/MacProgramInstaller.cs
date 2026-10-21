using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public sealed class MacProgramInstaller
{
    public static async Task BeginInstallAndExitAsync(
        StagedUpdate staged,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (!OperatingSystem.IsMacOS() || staged.Scope != UpdateScope.Program)
            throw new PlatformNotSupportedException("Automatic program installation is available only on macOS.");
        string appPath = FindAppBundlePath(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("The app must run from a .app bundle to install updates.");
        string parentPath = Path.GetDirectoryName(appPath)!;
        EnsureWritable(parentPath);
        byte[] manifestBytes = await File.ReadAllBytesAsync(staged.ManifestPath, cancellationToken).ConfigureAwait(false);
        byte[] signature = ManifestVerifier.DecodeSignature(await File.ReadAllTextAsync(staged.SignaturePath, cancellationToken).ConfigureAwait(false));
        var verifier = new ManifestVerifier();
        if (!verifier.Verify(manifestBytes, signature)) throw new CryptographicException("Staged update manifest signature is invalid.");
        var manifest = ManifestVerifier.Deserialize(manifestBytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, staged.Manifest.ReleaseTag, "macos", "arm64", allowDeltas: false)
            || !manifest.Kind.Equals("program", StringComparison.Ordinal)
            || !manifest.Version.Equals(staged.Manifest.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Staged update manifest is not the expected macOS program update.");
        var fullAsset = manifest.Assets.Single(asset => asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        const string helperRelativePath = "UmamusumeAss.app/Contents/Helpers/Umamusume.MacUpdater";
        var helperEntry = fullAsset.Files.SingleOrDefault(file => file.Path.Equals(helperRelativePath, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The signed file inventory does not contain the update helper.");
        string verifiedHelper = UpdatePathSafety.ResolveUnderRoot(staged.PayloadRoot, helperEntry.Path);
        if (!File.Exists(verifiedHelper)) throw new FileNotFoundException("The verified update package does not contain its installer helper.", verifiedHelper);
        if (new FileInfo(verifiedHelper).Length != helperEntry.Size
            || !ManifestVerifier.Sha256File(verifiedHelper).Equals(helperEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The update helper does not match its signed file inventory.");
        string operationRoot = Path.GetDirectoryName(staged.PackagePath)!;
        string helperPath = Path.Combine(operationRoot, "Umamusume.MacUpdater");
        File.Copy(verifiedHelper, helperPath, overwrite: true);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(helperPath, File.GetUnixFileMode(verifiedHelper));
        if (new FileInfo(helperPath).Length != helperEntry.Size
            || !ManifestVerifier.Sha256File(helperPath).Equals(helperEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The external update helper copy failed signed hash verification.");
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(staged.PackagePath)! };
        start.ArgumentList.Add("--install");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(appPath);
        start.ArgumentList.Add(staged.PayloadRoot);
        start.ArgumentList.Add(staged.PackagePath);
        start.ArgumentList.Add(staged.ManifestPath);
        start.ArgumentList.Add(staged.SignaturePath);
        start.ArgumentList.Add(operationRoot);
        start.ArgumentList.Add(staged.OperationId);
        start.ArgumentList.Add(nonce);

        var helper = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        bool handoffComplete = false;
        try
        {
            string startedPath = Path.Combine(operationRoot, "helper-started.json");
            string readyPath = Path.Combine(operationRoot, "ready.json");
            string resultPath = Path.Combine(operationRoot, "result.json");
            var timer = Stopwatch.StartNew();
            bool helperStarted = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(startedPath))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(startedPath, cancellationToken).ConfigureAwait(false));
                        var root = document.RootElement;
                        helperStarted = root.GetProperty("Started").GetBoolean()
                            && root.GetProperty("OperationId").GetString() == staged.OperationId
                            && root.GetProperty("Nonce").GetString() == nonce;
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
                    if (helperStarted) break;
                }
                await ThrowIfHelperFailedAsync(resultPath, staged.OperationId, nonce, cancellationToken).ConfigureAwait(false);
                if (helper.HasExited) throw new IOException($"The update helper exited with code {helper.ExitCode} before acknowledging startup.");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (!helperStarted) throw new TimeoutException("The update helper did not acknowledge startup within 10 seconds.");

            progress?.Report("The installer started. Verifying the signed package…");
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromMinutes(5))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(readyPath))
                {
                    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(readyPath, cancellationToken).ConfigureAwait(false));
                    var root = document.RootElement;
                    if (root.GetProperty("Ready").GetBoolean()
                        && root.GetProperty("OperationId").GetString() == staged.OperationId
                        && root.GetProperty("Nonce").GetString() == nonce)
                    {
                        await MacAppLifecycle.RequestUpdateShutdownAsync().ConfigureAwait(false);
                        handoffComplete = true;
                        return;
                    }
                }
                await ThrowIfHelperFailedAsync(resultPath, staged.OperationId, nonce, cancellationToken).ConfigureAwait(false);
                if (helper.HasExited) throw new IOException($"The update helper exited with code {helper.ExitCode} before becoming ready.");
                await ReportHelperProgressAsync(operationRoot, staged.OperationId, nonce, progress, cancellationToken).ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("The update helper did not finish preparing the application within 5 minutes.");
        }
        finally
        {
            if (!handoffComplete)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            helper.Dispose();
        }
    }

    internal static async Task ThrowIfHelperFailedAsync(
        string resultPath,
        string expectedOperationId,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(resultPath)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (!HasMatchingString(root, "OperationId", expectedOperationId)
                || !HasMatchingString(root, "Nonce", expectedNonce)) return;
            string? error = root.TryGetProperty("Error", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            throw new IOException(error ?? "The update helper could not prepare the application.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
    }

    private static bool HasMatchingString(JsonElement element, string propertyName, string expected) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static async Task ReportHelperProgressAsync(
        string operationRoot,
        string operationId,
        string nonce,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null) return;
        string path = Path.Combine(operationRoot, "progress.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.GetProperty("OperationId").GetString() == operationId
                && root.GetProperty("Nonce").GetString() == nonce
                && root.GetProperty("Stage").GetString() is { Length: > 0 } stage)
                progress.Report(stage);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    private static string? FindAppBundlePath(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return null;
    }

    private static void EnsureWritable(string directory)
    {
        string probe = Path.Combine(directory, ".uma-update-check-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } File.Delete(probe); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            string applications = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            string message = $"This application folder cannot be updated by the current user. Move UmamusumeAss.app into {applications} and try again. The updater will not request administrator privileges.";
            if (exception is UnauthorizedAccessException) throw new UnauthorizedAccessException(message, exception);
            throw new IOException(message, exception);
        }
    }
}
