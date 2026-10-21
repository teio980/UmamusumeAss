using UmamusumeWpfGui.Models;
using Umamusume.CoreBridge;
using TemplateMatchResult = UmamusumeWpfGui.Models.TemplateMatchResult;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>Native, frame-bound device operations used by production visual automation.</summary>
public interface IUmaVisualDeviceFacade : IAsyncDisposable
{
    Task<DeviceOperationEvent> PackageOperationAsync(
        string adbPath,
        string serial,
        string packageId,
        string? activity,
        PackageOperationKind operation,
        CancellationToken cancellationToken = default);

    Task<GrayImage?> CaptureGrayAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default);

    GrayImage? GetRetainedImage(FrameProvenance source);

    Task TapAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        double x,
        double y,
        CancellationToken cancellationToken = default);

    Task TapAtSourceAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        double x,
        double y,
        CancellationToken cancellationToken = default);

    Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        FrameRetryIntent? retryIntent = null,
        CancellationToken cancellationToken = default);

    FrameRetryIntent RetainMatchForRetry(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        int maxTapAttempts,
        TimeSpan lifetime);

    Task TapCoordinatesAsync(
        LastVerifiedConnection connection,
        int x,
        int y,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default);

    Task SwipeAsync(
        LastVerifiedConnection connection,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default);

    Task SwipeAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default);

    Task SendKeyEventAsync(
        LastVerifiedConnection connection,
        string keyCode,
        CancellationToken cancellationToken = default);

    Task SendKeyEventAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        string keyCode,
        CancellationToken cancellationToken = default);

    Task InputTextAsync(
        LastVerifiedConnection connection,
        string text,
        CancellationToken cancellationToken = default);

    Task InputTextAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        string text,
        CancellationToken cancellationToken = default);

    Task PrepareHeldTouchAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default);

    Task TouchDownAsync(
        LastVerifiedConnection connection,
        GrayImage source,
        double x,
        double y,
        CancellationToken cancellationToken = default);

    Task TouchUpAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        CancellationToken cancellationToken = default);

    Task TouchCancelAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        CancellationToken cancellationToken = default);
}

/// <summary>A finite, explicit exception for a task that repeats one observed button tap.</summary>
public sealed class FrameRetryIntent : IDisposable
{
    private Action<Guid>? _release;

    internal FrameRetryIntent(
        Guid id,
        FrameProvenance source,
        (int X, int Y) canonicalTapPoint,
        Action<Guid> release)
    {
        Id = id;
        Source = source;
        CanonicalTapPoint = canonicalTapPoint;
        _release = release;
    }

    internal Guid Id { get; }
    public FrameProvenance Source { get; }
    internal (int X, int Y) CanonicalTapPoint { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(Id);
}
