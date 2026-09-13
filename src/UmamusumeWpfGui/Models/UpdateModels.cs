using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

public enum UpdateScope
{
    Program,
    Resource,
    All,
}

public enum UpdateAssetKind
{
    Full,
    Delta,
}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "UmamusumeAss";
    public string Kind { get; set; } = "program";
    public string Channel { get; set; } = "stable";
    public string TargetOs { get; set; } = "windows";
    public string TargetArch { get; set; } = "x64";
    public string Version { get; set; } = string.Empty;
    public string ReleaseTag { get; set; } = string.Empty;
    public string BundledResourceVersion { get; set; } = string.Empty;
    public string SupportedResourceSchema { get; set; } = "1";
    public string MinAppVersion { get; set; } = string.Empty;
    public string MaxAppVersion { get; set; } = string.Empty;
    public string ResourceSchema { get; set; } = "1";
    public List<UpdateAsset> Assets { get; set; } = [];
}

public sealed class UpdateAsset
{
    public string Type { get; set; } = "full";
    public string? FromVersion { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string TargetTreeSha256 { get; set; } = string.Empty;
    public string SourceTreeSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public List<UpdateFileEntry> Files { get; set; } = [];
    public List<string> Deletes { get; set; } = [];
}

public sealed class UpdateFileEntry
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record UpdateCheckResult(
    UpdateManifest? Program,
    UpdateManifest? Resource,
    string? Error = null)
{
    public bool HasUpdate => Program is not null || Resource is not null;
}

public sealed record UpdatePlan(
    UpdateManifest Manifest,
    UpdateAsset Asset,
    string ReleaseTag);

public sealed record StagedUpdate(
    string OperationId,
    UpdateScope Scope,
    UpdateManifest Manifest,
    UpdateAsset Asset,
    string PackagePath,
    string PayloadRoot,
    string ManifestPath,
    string SignaturePath);

public sealed record UpdateProgress(string Stage, long Completed, long Total);

public sealed record ActivitySnapshot(int ActiveCount, IReadOnlyDictionary<ActivityKind, int> Counts)
{
    public bool IsIdle => ActiveCount == 0;
}

public enum ActivityKind
{
    Queue,
    DeveloperPipeline,
    Connection,
    NativeOperation,
    ResourceReload,
    Shutdown,
}

public sealed record ResourceRevision(string Version, string BaseTreeSha256, string ViewTreeSha256);

public sealed record ResourceCandidate(
    ResourceRevision Revision,
    string BaseDirectory,
    string CompositeDirectory,
    string PointerPath);

public sealed class UpdateState
{
    public string OperationId { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string ManifestSha256 { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public bool ForceFull { get; set; }
    public string? Error { get; set; }
    public bool CurrentManifestSigned { get; set; }
    public string? SignedManifestPath { get; set; }
    public string? SignedSignaturePath { get; set; }
    public string? CurrentTreeSha256 { get; set; }
    public string? ResourceManifestSha256 { get; set; }
    public string? ResourceManifestPath { get; set; }
    public string? ResourceSignaturePath { get; set; }
    public string? StagedProgramOperationId { get; set; }
    public string? StagedProgramVersion { get; set; }
    public string? StagedProgramAssetName { get; set; }
}
