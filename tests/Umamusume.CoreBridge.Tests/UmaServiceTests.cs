using Umamusume.CoreBridge.Tests.Fakes;

namespace Umamusume.CoreBridge.Tests;

public sealed class UmaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uma-bridge-{Guid.NewGuid():N}");

    public UmaServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InitializeCallsNativeApiInRequiredOrder()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string appData = Path.Combine(_root, "app-data");

        await service.InitializeAsync(_root, appData);

        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
        Assert.Equal("0.1.0", service.CoreVersion);
        Assert.True(Directory.Exists(appData));
    }

    [Theory]
    [InlineData("relative", true)]
    [InlineData("relative", false)]
    public async Task InitializeRejectsRelativePaths(string path, bool useAsBase)
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string basePath = useAsBase ? path : _root;
        string appData = useAsBase ? Path.Combine(_root, "data") : path;

        await Assert.ThrowsAsync<ArgumentException>(() => service.InitializeAsync(basePath, appData));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsMissingBaseDirectory()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.InitializeAsync(Path.Combine(_root, "missing"), Path.Combine(_root, "data")));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeStopsWhenSetUserDirFails()
    {
        var native = new FakeUmaNativeApi { SetUserDirResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeStopsWhenLoadResourceFails()
    {
        var native = new FakeUmaNativeApi { LoadResourceResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeRejectsEmptyVersion()
    {
        var native = new FakeUmaNativeApi { Version = "" };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion"], native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsInvalidHandle()
    {
        var native = new FakeUmaNativeApi { CreateInvalidHandle = true };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
    }

    [Fact]
    public async Task InitializeCannotRunTwice()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize(service));
    }

    [Fact]
    public async Task ConnectBeforeInitializationIsRejected()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task Initialize(UmaService service) =>
        service.InitializeAsync(_root, Path.Combine(_root, "app-data"));

    private static UmaService CreateService(FakeUmaNativeApi native) =>
        new(native, new InlineEventDispatcher());

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
