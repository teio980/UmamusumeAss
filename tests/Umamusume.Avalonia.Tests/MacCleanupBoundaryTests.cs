using Umamusume.Avalonia.Services;
using Umamusume.AppCore.Platform;
using UmamusumeWpfGui.Models;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacCleanupBoundaryTests
{
    [Fact]
    public async Task RunningAppInsideUpdateCacheKeepsItsEntireOperation()
    {
        using var fixture = new Fixture();
        string running = Path.Combine(fixture.Data, "updates", Guid.NewGuid().ToString("N"));
        string applicationBase = Path.Combine(running, "payload", "UmamusumeAss.app", "Contents", "MacOS");
        Directory.CreateDirectory(applicationBase);
        await File.WriteAllTextAsync(Path.Combine(applicationBase, "app-marker"), "running app");
        string unused = Path.Combine(fixture.Data, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unused);
        var cleaner = new MacUpdateCacheCleanupService(fixture.Data, applicationBase,
            Path.Combine(fixture.Data, "missing.lock"));

        var result = await cleaner.ClearAsync(null);

        Assert.Equal(1, result.PreservedOperations);
        Assert.True(File.Exists(Path.Combine(applicationBase, "app-marker")));
        Assert.False(Directory.Exists(unused));
    }

    [Fact]
    public async Task CurrentWorkspaceSourceInsideCacheKeepsItsOperationAndView()
    {
        using var fixture = new Fixture();
        string operation = Path.Combine(fixture.Data, "updates", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(operation, "payload");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "resource.json"), "active source");
        var workspace = await ResourceWorkspace.PrepareAsync(source, fixture.Data);

        var result = await fixture.Cleaner.ClearAsync(workspace);

        Assert.Equal(1, result.PreservedOperations);
        Assert.True(File.Exists(Path.Combine(source, "resource.json")));
        Assert.True(Directory.Exists(workspace.ActiveBaseDirectory));
    }

    [Theory]
    [InlineData("updates")]
    [InlineData("resources")]
    [InlineData("runtime")]
    public async Task LinkedCacheParentKeepsExternalFiles(string linkedDirectory)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        string relative = linkedDirectory switch
        {
            "updates" => Path.Combine(Guid.NewGuid().ToString("N"), "payload", "marker.txt"),
            "resources" => Path.Combine("revisions", "2026.10.20.1", "payload", "marker.txt"),
            _ => Path.Combine("views", "old-view", "resource", "marker.txt")
        };
        string marker = Path.Combine(fixture.External, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "external resource");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Data, linkedDirectory), fixture.External);

        await fixture.Cleaner.ClearAsync(null);

        Assert.Equal("external resource", await File.ReadAllTextAsync(marker));
        Assert.NotNull(new DirectoryInfo(Path.Combine(fixture.Data, linkedDirectory)).LinkTarget);
    }

    [Fact]
    public void RestoringStagedUpdatesThroughLinkedRootKeepsExternalReference()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        string reference = Path.Combine(fixture.External, "staged-program.json");
        const string original = "{invalid external reference";
        File.WriteAllText(reference, original);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Data, "updates"), fixture.External);
        var updates = new MacUpdateService(userDataRoot: fixture.Data);

        var restored = updates.RestoreStaged(UpdateScope.Program, "0.3.0", "0.0.0.0");

        Assert.Null(restored);
        Assert.Equal(original, File.ReadAllText(reference));
    }

    [Fact]
    public async Task RemovingOperationUnlinksNestedDirectoryWithoutDeletingItsTarget()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        string operation = Path.Combine(fixture.Data, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(operation, "payload"));
        string marker = Path.Combine(fixture.External, "marker.txt");
        await File.WriteAllTextAsync(marker, "external resource");
        Directory.CreateSymbolicLink(Path.Combine(operation, "payload", "linked"), fixture.External);

        var result = await fixture.Cleaner.ClearAsync(null);

        Assert.Equal(1, result.RemovedOperations);
        Assert.False(Directory.Exists(operation));
        Assert.Equal("external resource", await File.ReadAllTextAsync(marker));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uma cleanup boundary " + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Data = Path.Combine(_root, "data");
            External = Path.Combine(_root, "external");
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(External);
            Cleaner = new MacUpdateCacheCleanupService(Data,
                Path.Combine(_root, "UmamusumeAss.app", "Contents", "MacOS"),
                Path.Combine(_root, "installer.lock"));
        }

        public string Data { get; }
        public string External { get; }
        public MacUpdateCacheCleanupService Cleaner { get; }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
