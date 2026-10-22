using System.Text.Json;
using System.Security.Cryptography;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Update;

public sealed class UpdateSettings
{
    public bool StartupCheck { get; set; } = true;
    public string Channel { get; set; } = "stable";
    public string? SkippedProgramVersion { get; set; }
}

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public UpdateStateStore(string? appDataRoot = null)
    {
        var root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "update-state.json");
    }

    public UpdateState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_path), Options)
                ?? new UpdateState();
        }
    catch (Exception)
        {
            return new UpdateState();
        }
    }

    public void Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        if (File.Exists(_path)) File.Replace(temporary, _path, null, true);
        else File.Move(temporary, _path);
    }

    public UpdateState Update(Action<UpdateState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var state = Load();
        update(state);
        Save(state);
        return state;
    }
}

public static class UpdateSelector
{
    public static UpdatePlan? Select(
        UpdateManifest manifest,
        string currentVersion,
        string? currentTreeHash,
        string? currentManifestHash,
        bool currentManifestSigned,
        bool forceFull = false,
        bool allowDelta = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!SemVersion.TryParse(currentVersion, out var current)
            || !SemVersion.TryParse(manifest.Version, out var target)
            || target <= current)
            return null;

        var full = manifest.Assets.FirstOrDefault(asset =>
            asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        if (full is null) return null;
        var delta = allowDelta && !forceFull && currentManifestSigned && !string.IsNullOrWhiteSpace(currentTreeHash)
            ? manifest.Assets.FirstOrDefault(asset =>
                asset.Type.Equals("delta", StringComparison.OrdinalIgnoreCase)
                && asset.FromVersion is not null
                && asset.FromVersion.Equals(currentVersion, StringComparison.Ordinal)
                && asset.SourceTreeSha256.Equals(currentTreeHash, StringComparison.OrdinalIgnoreCase)
                && asset.SourceManifestSha256.Equals(currentManifestHash, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.SourceTreeSha256)
                && !string.IsNullOrWhiteSpace(asset.SourceManifestSha256))
            : null;
        var selected = delta ?? full;
        return new UpdatePlan(manifest, selected, manifest.ReleaseTag);
    }

    /// Verifies only the files owned by a signed full inventory. Files that
    /// are not in that inventory are deliberately ignored so user-created
    /// files neither block a delta nor become deletion candidates.
    public static bool VerifyManagedTree(
        string root,
        UpdateAsset fullAsset,
        string expectedTreeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(fullAsset);
        if (string.IsNullOrWhiteSpace(expectedTreeHash)
            || fullAsset.Files.Count > UpdatePathSafety.MaxFiles)
            return false;

        try
        {
            using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var entry in fullAsset.Files.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                UpdatePathSafety.ValidateRelative(entry.Path);
                var path = UpdatePathSafety.ResolveUnderRoot(root, entry.Path);
                if (!File.Exists(path)
                    || new FileInfo(path).Length != entry.Size
                    || !ManifestVerifier.Sha256File(path).Equals(
                        entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                tree.AppendData(System.Text.Encoding.UTF8.GetBytes(
                    entry.Path.Replace('\\', '/')));
                tree.AppendData([0]);
                using var stream = File.OpenRead(path);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    tree.AppendData(buffer.AsSpan(0, read));
            }
            return Convert.ToHexString(tree.GetHashAndReset()).Equals(
                expectedTreeHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

