using System.IO;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

internal sealed class LiveNativeDeviceTestHost : IAsyncDisposable
{
    private readonly UmaService _service;
    private readonly UmaVisualDeviceFacade _facade;

    private LiveNativeDeviceTestHost(
        UmaService service,
        UmaVisualDeviceFacade facade,
        LastVerifiedConnection connection)
    {
        _service = service;
        _facade = facade;
        Connection = connection;
    }

    internal IUmaVisualDeviceFacade Device => _facade;
    internal LastVerifiedConnection Connection { get; }

    internal static async Task<LiveNativeDeviceTestHost> ConnectAsync(
        string workspaceRoot,
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var service = new UmaService(new InlineEventDispatcher());
        try
        {
            await service.InitializeAsync(
                workspaceRoot,
                Path.Combine(Path.GetTempPath(), $"uma-live-core-{Guid.NewGuid():N}"),
                cancellationToken);
            var result = await service.ConnectAsync(adbPath, serial, "android", cancellationToken);
            if (result is not ConnectionSucceededEvent connected)
                throw new InvalidOperationException($"Native connection failed: {result}");

            var facade = new UmaVisualDeviceFacade(service);
            return new LiveNativeDeviceTestHost(service, facade, new LastVerifiedConnection(
                adbPath,
                connected.Serial,
                connected.AndroidId,
                connected.AndroidVersion,
                connected.Width,
                connected.Height,
                connected.PhysicalWidth,
                connected.PhysicalHeight,
                DateTimeOffset.UtcNow));
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _facade.DisposeAsync();
        await _service.DisposeAsync();
    }

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
