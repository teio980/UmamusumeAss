using System.Reflection;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

internal static class TestDeviceFacade
{
    internal static IUmaVisualDeviceFacade Unexpected() =>
        DispatchProxy.Create<IUmaVisualDeviceFacade, UnexpectedDeviceFacadeProxy>();

    internal static IUmaVisualDeviceFacade WithFrame(GrayImage frame)
    {
        var facade = DispatchProxy.Create<IUmaVisualDeviceFacade, SingleFrameDeviceFacadeProxy>();
        ((SingleFrameDeviceFacadeProxy)(object)facade).Frame = frame;
        return facade;
    }
}

public class UnexpectedDeviceFacadeProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IAsyncDisposable.DisposeAsync))
            return ValueTask.CompletedTask;
        throw new InvalidOperationException(
            $"Unexpected native device operation in an offline visual test: {targetMethod?.Name}");
    }
}

public class SingleFrameDeviceFacadeProxy : DispatchProxy
{
    private ulong _frameId;
    private readonly Dictionary<ulong, GrayImage> _retained = [];
    internal GrayImage Frame { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IUmaVisualDeviceFacade.CaptureGrayAsync):
            {
                var id = ++_frameId;
                GrayImage image = Frame with
                {
                    SourceFrame = new FrameProvenance(new DeviceFrameMetadata(
                        id, 1, 1, Frame.Width, Frame.Height, 0,
                        (ulong)checked(Frame.Width * Frame.Height * 4),
                        new DeviceDisplayTransform(Frame.Width, Frame.Height, 0,
                            Frame.Width, Frame.Height, 1, 0, 0, Frame.Width, Frame.Height))),
                };
                _retained[id] = image;
                return Task.FromResult<GrayImage?>(image);
            }
            case nameof(IUmaVisualDeviceFacade.GetRetainedImage):
            {
                var source = (FrameProvenance)args![0]!;
                return _retained.GetValueOrDefault(source.Metadata.FrameId);
            }
            case nameof(IAsyncDisposable.DisposeAsync):
                return ValueTask.CompletedTask;
            default:
                throw new InvalidOperationException(
                    $"Unexpected native device operation in a single-frame visual test: {targetMethod?.Name}");
        }
    }
}
