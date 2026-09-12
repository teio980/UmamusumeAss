using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Update;

public sealed class ManifestVerifier
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // P-256 SPKI public key. Release signing is performed only by the
    // protected release environment; this value is never downloaded.
    private const string EmbeddedPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXSfrguQVvVr+DJuU3j8ZN0/mZgOvf94JVT9uCeuScWtBYZtubqOEJK0ESOP8PgCzSkih8yjGnWjY1W04//QxqQ==";

    private readonly byte[] _publicKey;

    public ManifestVerifier()
        : this(Convert.FromBase64String(EmbeddedPublicKey))
    {
    }

    internal ManifestVerifier(byte[] publicKey)
    {
        _publicKey = publicKey?.ToArray() ?? throw new ArgumentNullException(nameof(publicKey));
    }

    public bool Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
            return false;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_publicKey, out _);
        return ecdsa.VerifyData(
            manifestBytes.ToArray(),
            signature.ToArray(),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static byte[] DecodeSignature(string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);
        var bytes = Convert.FromBase64String(base64.Trim());
        if (bytes.Length != 64)
            throw new CryptographicException("ECDSA signature must be 64-byte P1363.");
        return bytes;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static bool IsCanonicalManifest(UpdateManifest manifest, string expectedTag)
    {
        var expectedKind = expectedTag.StartsWith("resource-v", StringComparison.Ordinal)
            ? "resource" : "program";
        if (manifest.SchemaVersion != 1
            || !manifest.Product.Equals("UmamusumeAss", StringComparison.Ordinal)
            || !manifest.Kind.Equals(expectedKind, StringComparison.Ordinal)
            || !manifest.Channel.Equals("stable", StringComparison.Ordinal)
            || !manifest.ReleaseTag.Equals(expectedTag, StringComparison.Ordinal)
            || !manifest.TargetOs.Equals("windows", StringComparison.OrdinalIgnoreCase)
            || !manifest.TargetArch.Equals("x64", StringComparison.OrdinalIgnoreCase)
            || manifest.Assets.Count == 0
            || (expectedKind == "program"
                ? !GitHubReleaseClient.IsProgramTag(expectedTag)
                : !GitHubReleaseClient.IsResourceTag(expectedTag)))
            return false;
        var seenAssets = new HashSet<string>(StringComparer.Ordinal);
        var fullCount = 0;
        foreach (var asset in manifest.Assets)
        {
            if (asset.Type is not ("full" or "delta")
                || string.IsNullOrWhiteSpace(asset.AssetName)
                || asset.AssetName.Contains('/') || asset.AssetName.Contains('\\')
                || !seenAssets.Add(asset.AssetName)
                || asset.Size < 0
                || asset.Size > UpdatePathSafety.MaxExpandedBytes
                || asset.Files.Count > UpdatePathSafety.MaxFiles
                || !IsSha256(asset.Sha256)
                || !IsSha256(asset.TargetTreeSha256))
                return false;
            if (asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase))
            {
                fullCount++;
                if (asset.Deletes.Count != 0) return false;
            }
            else if (string.IsNullOrWhiteSpace(asset.FromVersion)
                || (expectedKind == "program"
                    && !SemVersion.TryParse(asset.FromVersion, out _))
                || (expectedKind == "resource"
                    && !GitHubReleaseClient.IsResourceTag("resource-v" + asset.FromVersion)))
                return false;
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var file in asset.Files)
            {
                try { UpdatePathSafety.ValidateRelative(file.Path); }
                catch (InvalidDataException) { return false; }
                if (!seenPaths.Add(file.Path)
                    || file.Size < 0
                    || file.Size > UpdatePathSafety.MaxFileBytes
                    || !IsSha256(file.Sha256)
                    || file.Size > UpdatePathSafety.MaxExpandedBytes - total)
                    return false;
                total += file.Size;
            }
            var seenDeletes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deleted in asset.Deletes)
            {
                try { UpdatePathSafety.ValidateRelative(deleted); }
                catch (InvalidDataException) { return false; }
                if (!seenDeletes.Add(deleted)) return false;
            }
        }
        return fullCount == 1;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    public static UpdateManifest Deserialize(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, ManifestJsonOptions)
            ?? throw new InvalidDataException("Manifest was empty.");
        return manifest;
    }
}

public static class UpdatePathSafety
{
    public const int MaxFiles = 10_000;
    public const long MaxFileBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ValidateRelative(relativePath);
        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes update root: {relativePath}");
        RejectReparseComponents(canonicalRoot, candidate);
        return candidate;
    }

    public static void ValidateRelative(string relativePath)
    {
        if (relativePath.Contains('\0', StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.StartsWith('\\')
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Illegal update path: {relativePath}");
        }

        foreach (var component in relativePath.Replace('\\', '/').Split('/'))
        {
            if (component is "" or "." or "..")
                throw new InvalidDataException($"Illegal update path: {relativePath}");
        }
    }

    private static void RejectReparseComponents(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Reparse point in update path: {current}");
            }
        }
    }
}
