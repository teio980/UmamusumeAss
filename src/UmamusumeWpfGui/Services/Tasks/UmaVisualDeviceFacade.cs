using System.IO;
using System.Diagnostics;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using TemplateMatchResult = UmamusumeWpfGui.Models.TemplateMatchResult;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Owns the small set of native frame leases that visual automation may use
/// for input. Recognition results carry only frame provenance, never a lease.
/// </summary>
public sealed class UmaVisualDeviceFacade : IUmaVisualDeviceFacade
{
    private const int NativeFrameCapacity = 4;
    private static readonly TimeSpan OrdinaryFrameLifetime = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private readonly IUmaService _service;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private readonly Dictionary<ulong, RetainedFrame> _ordinaryFrames = [];
    private readonly Dictionary<Guid, RetainedFrame> _retryFrames = [];
    private readonly ITimer _expiryTimer;
    private RetainedFrame? _heldFrame;
    private bool _heldTouchCleanupPending;
    private ulong? _lastReleasedHeldFrameId;
    private ulong? _knownSessionGeneration;
    private ulong? _knownGeometryGeneration;
    private Task? _disposeTask;
    private bool _disposing;
    private bool _disposed;

    public UmaVisualDeviceFacade(IUmaService service)
        : this(service, TimeProvider.System)
    {
    }

    public async Task<DeviceOperationEvent> PackageOperationAsync(
        string adbPath,
        string serial,
        string packageId,
        string? activity,
        PackageOperationKind operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (operation == PackageOperationKind.StartActivity)
            ArgumentException.ThrowIfNullOrWhiteSpace(activity);
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));

        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool attempted = false;
        try
        {
            VerifyConnection(adbPath, serial);
            lock (_sync)
                ThrowIfDisposed();

            attempted = true;
            DeviceOperationEvent result = await _service.PackageOperationAsync(
                    packageId,
                    activity,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is PackageOperationCompletedEvent completed
                && completed.Operation != operation)
                throw new InvalidOperationException("Native package operation returned a mismatched operation kind.");
            if (result is not PackageOperationCompletedEvent and not DeviceOperationFailedEvent)
                throw new InvalidOperationException("Native package operation returned an unexpected event.");
            return result;
        }
        finally
        {
            if (attempted && operation != PackageOperationKind.QueryRunning)
            {
                lock (_sync)
                {
                    if (!_disposed)
                    {
                        InvalidateOrdinaryFramesLocked();
                        InvalidateRetryFramesLocked();
                    }
                }
            }
            _deviceGate.Release();
        }
    }

    internal UmaVisualDeviceFacade(IUmaService service, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _service = service;
        _timeProvider = timeProvider;
        _service.ConnectionEventReceived += OnConnectionEvent;
        _expiryTimer = _timeProvider.CreateTimer(
            static state => ((UmaVisualDeviceFacade)state!).ExpireFrames(),
            this,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public async Task<GrayImage?> CaptureGrayAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default)
    {
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureUnderGateAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public GrayImage? GetRetainedImage(FrameProvenance source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_sync)
        {
            ThrowIfDisposed();
            ExpireFramesLocked();
            if (_ordinaryFrames.TryGetValue(source.Metadata.FrameId, out RetainedFrame? ordinary)
                && SameSource(ordinary.Frame.Metadata, source.Metadata))
                return ordinary.Image;
            RetainedFrame? retry = _retryFrames.Values.FirstOrDefault(frame =>
                SameSource(frame.Frame.Metadata, source.Metadata));
            if (retry is not null)
                return retry.Image;
            if (_heldFrame is { } held && SameSource(held.Frame.Metadata, source.Metadata))
                return held.Image;
            return null;
        }
    }

    public async Task TapAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        FrameProvenance provenance = frame.SourceFrame
            ?? throw new InvalidOperationException("The tap coordinates have no source device frame; recognize the current screen first.");
        await TapAtSourceAsync(connection, provenance, x, y, cancellationToken).ConfigureAwait(false);
    }

    public async Task TapAtSourceAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TapProvenanceUnderGateAsync(
                connection, source, x, y, retryIntent: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public async Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        FrameRetryIntent? retryIntent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!match.Found)
            throw new InvalidOperationException("A missing template match cannot be tapped.");
        FrameProvenance provenance = match.SourceFrame
            ?? throw new InvalidOperationException("The match has no source device frame; capture and recognize again.");
        if (retryIntent is not null && !SameSource(provenance, retryIntent.Source))
            throw new InvalidOperationException("The retry intent belongs to a different recognition frame.");

        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TapProvenanceUnderGateAsync(
                connection,
                provenance,
                match.CenterX,
                match.CenterY,
                retryIntent,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public FrameRetryIntent RetainMatchForRetry(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        int maxTapAttempts,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!match.Found)
            throw new InvalidOperationException("A missing template match cannot be retained for retry.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTapAttempts);
        if (maxTapAttempts > 100)
            throw new ArgumentOutOfRangeException(nameof(maxTapAttempts), "A frame retry budget cannot exceed the existing task limit of 100 taps.");
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A retained retry frame must have a finite positive lifetime.");
        FrameProvenance provenance = match.SourceFrame
            ?? throw new InvalidOperationException("The retry source has no device frame provenance.");
        var canonicalTapPoint = provenance.MapToCanonical(match.CenterX, match.CenterY);
        DeviceSessionIdentity identity = VerifyConnection(connection);

        lock (_sync)
        {
            ThrowIfDisposed();
            ExpireFramesLocked();
            ValidateCurrentGeometry(provenance, identity);
            if (!_ordinaryFrames.TryGetValue(provenance.Metadata.FrameId, out RetainedFrame? retained)
                || !SameSource(retained.Frame.Metadata, provenance.Metadata))
            {
                throw new InvalidOperationException("The retry source frame has expired; recognize the current screen again.");
            }
            if (retained.ActiveUses != 0)
                throw new InvalidOperationException("The retry source frame is currently in use.");

            DateTimeOffset retainedAt = _timeProvider.GetUtcNow();
            if (lifetime > DateTimeOffset.MaxValue - retainedAt)
                throw new ArgumentOutOfRangeException(nameof(lifetime), "The retry expiry exceeds the supported clock range.");
            DateTimeOffset expiresAt = retainedAt + lifetime;
            Guid id = Guid.NewGuid();
            _ordinaryFrames.Remove(retained.Frame.FrameId);
            retained.ExpiresAt = expiresAt;
            retained.RemainingTapAttempts = maxTapAttempts;
            retained.CanonicalTapPoint = canonicalTapPoint;
            _retryFrames.Add(id, retained);
            return new FrameRetryIntent(id, provenance, canonicalTapPoint, ReleaseRetryIntent);
        }
    }

    public async Task TapCoordinatesAsync(
        LastVerifiedConnection connection,
        int x,
        int y,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default)
    {
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GrayImage frame = await CaptureUnderGateAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Native capture returned no image for coordinate input.");
            int mappedX = ScaleCoordinate(x, Math.Max(1, referenceWidth), frame.Width);
            int mappedY = ScaleCoordinate(y, Math.Max(1, referenceHeight), frame.Height);
            await TapProvenanceUnderGateAsync(
                connection,
                frame.SourceFrame!,
                mappedX,
                mappedY,
                retryIntent: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public async Task SwipeAsync(
        LastVerifiedConnection connection,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default)
    {
        if (coordinates is null || coordinates.Length < 5)
            throw new ArgumentException("A swipe requires startX, startY, endX, endY, and durationMs.", nameof(coordinates));

        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GrayImage frame = await CaptureUnderGateAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Native capture returned no image for swipe input.");
            await SwipeAtFrameUnderGateAsync(connection, frame, coordinates,
                referenceWidth, referenceHeight, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public async Task SwipeAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (coordinates is null || coordinates.Length < 5)
            throw new ArgumentException("A swipe requires startX, startY, endX, endY, and durationMs.", nameof(coordinates));

        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SwipeAtFrameUnderGateAsync(connection, frame, coordinates,
                referenceWidth, referenceHeight, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public Task SendKeyEventAsync(
        LastVerifiedConnection connection,
        string keyCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyCode);
        return RunTextInputAsync(
            connection,
            (frame, token) => _service.KeyEventAsync(frame, keyCode, token),
            "key event",
            cancellationToken);
    }

    public Task SendKeyEventAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        string keyCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyCode);
        return RunTextInputAtFrameAsync(
            connection,
            frame,
            (deviceFrame, token) => _service.KeyEventAsync(deviceFrame, keyCode, token),
            "key event",
            cancellationToken);
    }

    public Task InputTextAsync(
        LastVerifiedConnection connection,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return RunTextInputAsync(
            connection,
            (frame, token) => _service.InputTextAsync(frame, text, token),
            "text input",
            cancellationToken);
    }

    public Task InputTextAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrEmpty(text);
        return RunTextInputAtFrameAsync(
            connection,
            frame,
            (deviceFrame, token) => _service.InputTextAsync(deviceFrame, text, token),
            "text input",
            cancellationToken);
    }

    private async Task RunTextInputAsync(
        LastVerifiedConnection connection,
        Func<DeviceFrame, CancellationToken, Task<DeviceOperationEvent>> input,
        string operation,
        CancellationToken cancellationToken)
    {
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_heldFrame is not null)
                    throw new InvalidOperationException("Text or key input cannot run while a held touch is active.");
            }

            GrayImage source = await CaptureUnderGateAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Native capture returned no image for device input.");
            await RunTextInputOnFrameUnderGateAsync(
                connection, source, input, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    private async Task RunTextInputAtFrameAsync(
        LastVerifiedConnection connection,
        GrayImage source,
        Func<DeviceFrame, CancellationToken, Task<DeviceOperationEvent>> input,
        string operation,
        CancellationToken cancellationToken)
    {
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunTextInputOnFrameUnderGateAsync(
                connection, source, input, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    private async Task RunTextInputOnFrameUnderGateAsync(
        LastVerifiedConnection connection,
        GrayImage source,
        Func<DeviceFrame, CancellationToken, Task<DeviceOperationEvent>> input,
        string operation,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_heldFrame is not null)
                throw new InvalidOperationException("Text or key input cannot run while a held touch is active.");
        }

        FrameProvenance provenance = source.SourceFrame
            ?? throw new InvalidOperationException("Text or key input requires source-frame provenance.");
        DeviceFrame deviceFrame = AcquireOrdinaryFrame(connection, provenance);
        DeviceOperationEvent result;
        try
        {
            result = await input(deviceFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseUse(provenance.Metadata.FrameId);
            lock (_sync)
            {
                InvalidateOrdinaryFramesLocked();
                InvalidateRetryFramesLocked();
            }
        }

        EnsureInputSucceeded(result, operation);
    }

    private async Task SwipeAtFrameUnderGateAsync(
        LastVerifiedConnection connection,
        GrayImage frame,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken)
    {
        FrameProvenance provenance = frame.SourceFrame
            ?? throw new InvalidOperationException("Swipe coordinates have no source device frame.");
        var start = provenance.MapToCanonical(
            ScaleCoordinate(coordinates[0], Math.Max(1, referenceWidth), frame.Width),
            ScaleCoordinate(coordinates[1], Math.Max(1, referenceHeight), frame.Height));
        var end = provenance.MapToCanonical(
            ScaleCoordinate(coordinates[2], Math.Max(1, referenceWidth), frame.Width),
            ScaleCoordinate(coordinates[3], Math.Max(1, referenceHeight), frame.Height));
        DeviceFrame deviceFrame = AcquireOrdinaryFrame(connection, provenance);
        DeviceOperationEvent result;
        try
        {
            result = await _service.SwipeAsync(
                    deviceFrame,
                    start.X,
                    start.Y,
                    end.X,
                    end.Y,
                    Math.Clamp(coordinates[4], 0, 60_000),
                    extraSwipe: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseUse(provenance.Metadata.FrameId);
            lock (_sync)
                InvalidateOrdinaryFramesLocked();
        }

        EnsureInputSucceeded(result, "swipe");
    }

    public async Task PrepareHeldTouchAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default)
    {
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_heldFrame is not null)
                    throw new InvalidOperationException("A held touch is already active.");
            }

            GrayImage preparationFrame = await CaptureUnderGateAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Native capture returned no image for touch preparation.");
            FrameProvenance provenance = preparationFrame.SourceFrame!;
            DeviceFrame deviceFrame = AcquireOrdinaryFrame(connection, provenance);
            try
            {
                DeviceOperationEvent result = await _service.PrepareTouchAsync(deviceFrame, cancellationToken)
                    .ConfigureAwait(false);
                EnsureOperationSucceeded(result, "prepare touch");
            }
            finally
            {
                RetireOrdinaryFrame(provenance.Metadata.FrameId);
            }
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public async Task TouchDownAsync(
        LastVerifiedConnection connection,
        GrayImage source,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        FrameProvenance provenance = source.SourceFrame
            ?? throw new InvalidOperationException("Held touch DOWN requires a ready source frame.");
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_heldFrame is not null)
                    throw new InvalidOperationException("A held touch is already active.");
            }

            var point = provenance.MapToCanonical(x, y);
            DeviceFrame deviceFrame = AcquireOrdinaryFrame(connection, provenance);
            bool pinned = false;
            bool downAttempted = false;
            DeviceOperationEvent result;
            try
            {
                downAttempted = true;
                result = await _service.TouchDownAsync(
                        deviceFrame, point.X, point.Y, cancellationToken)
                    .ConfigureAwait(false);
                if (result is InputSucceededEvent or InputFailedEvent)
                {
                    // Transfer the lease while it is still active. If the TTL
                    // timer runs as DOWN completes, the source is already the
                    // held pin and cannot be retired before UP/CANCEL. Native
                    // DOWN failures may mean the dispatch reached Android and
                    // the native cleanup itself failed, so keep that exact
                    // source frame until a source-bound CANCEL completes.
                    PinHeldFrame(deviceFrame, cleanupPending: result is InputFailedEvent);
                    pinned = true;
                }
                EnsureInputSucceeded(result, "touch DOWN");
            }
            catch
            {
                if (downAttempted && !pinned)
                {
                    bool released = await BestEffortCancelDownAsync(deviceFrame).ConfigureAwait(false);
                    if (!released)
                    {
                        // A thrown DOWN can still have reached the device. If
                        // bounded cleanup also fails, preserve its exact lease
                        // so the caller can retry CANCEL with the original ID.
                        PinHeldFrame(deviceFrame, cleanupPending: true);
                        pinned = true;
                    }
                }
                throw;
            }
            finally
            {
                if (!pinned)
                {
                    ReleaseUse(provenance.Metadata.FrameId);
                    lock (_sync)
                        InvalidateOrdinaryFramesLocked();
                }
            }
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    public Task TouchUpAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        CancellationToken cancellationToken = default) =>
        FinishHeldTouchAsync(connection, source, cancel: false, cancellationToken);

    public Task TouchCancelAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        CancellationToken cancellationToken = default) =>
        FinishHeldTouchAsync(connection, source, cancel: true, cancellationToken);

    private async Task<GrayImage?> CaptureUnderGateAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        DeviceSessionIdentity identity = VerifyConnection(connection);
        lock (_sync)
        {
            ThrowIfDisposed();
            ExpireFramesLocked();
            EnsureCaptureCapacityLocked();
        }

        DeviceOperationEvent operation = await _service.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (operation is not FrameCapturedEvent { FrameLease: { } frame } captured)
        {
            EnsureOperationSucceeded(operation, "capture");
            throw new InvalidOperationException("Native capture completed without a retained frame.");
        }

        if (captured.Frame.SessionGeneration != identity.SessionGeneration)
        {
            frame.Dispose();
            throw new InvalidOperationException("The native capture belongs to a different device session.");
        }

        GrayImage? image;
        try
        {
            image = GrayImageCodec.FromDeviceFrame(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        if (image is null)
        {
            frame.Dispose();
            throw new InvalidOperationException("The native PNG frame could not be decoded without changing its pixels.");
        }

        bool registered = false;
        try
        {
            DeviceSessionIdentity currentIdentity = VerifyConnection(connection);
            if (currentIdentity.SessionGeneration != captured.Frame.SessionGeneration)
            {
                throw new InvalidOperationException("The device session changed while the frame was being decoded.");
            }

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_knownSessionGeneration != captured.Frame.SessionGeneration
                    || _knownGeometryGeneration != captured.Frame.GeometryGeneration)
                {
                    InvalidateOrdinaryFramesLocked();
                    InvalidateRetryFramesLocked();
                    // A successful capture with new geometry/session metadata
                    // means the native device session has already completed
                    // its held-touch cleanup before exposing this frame.
                    // Retire the stale pin here so it cannot block a new
                    // prepare/down cycle or linger until shutdown.
                    if (_heldFrame is { } held)
                    {
                        _lastReleasedHeldFrameId = held.Frame.FrameId;
                        held.Frame.Dispose();
                        _heldFrame = null;
                        _heldTouchCleanupPending = false;
                    }
                    _knownSessionGeneration = captured.Frame.SessionGeneration;
                    _knownGeometryGeneration = captured.Frame.GeometryGeneration;
                }

                ExpireFramesLocked();
                EnsureCaptureCapacityLocked();
                DateTimeOffset capturedAt = _timeProvider.GetUtcNow();
                _ordinaryFrames.Add(frame.FrameId, new RetainedFrame(
                    frame,
                    image,
                    capturedAt,
                    capturedAt + OrdinaryFrameLifetime));
                registered = true;
            }
        }
        finally
        {
            if (!registered)
                frame.Dispose();
        }

        return image;
    }

    private async Task TapProvenanceUnderGateAsync(
        LastVerifiedConnection connection,
        FrameProvenance provenance,
        double x,
        double y,
        FrameRetryIntent? retryIntent,
        CancellationToken cancellationToken)
    {
        DeviceFrame deviceFrame;
        bool isRetry = retryIntent is not null;
        DeviceSessionIdentity identity = VerifyConnection(connection);
        var point = provenance.MapToCanonical(x, y);
        if (retryIntent is not null && point != retryIntent.CanonicalTapPoint)
            throw new InvalidOperationException("A retry intent can only repeat the original source-frame tap point.");
        lock (_sync)
        {
            ThrowIfDisposed();
            ExpireFramesLocked();
            ValidateCurrentGeometry(provenance, identity);
            if (retryIntent is null)
            {
                deviceFrame = AcquireOrdinaryFrameLocked(provenance);
            }
            else
            {
                if (retryIntent.Source.Metadata.FrameId != provenance.Metadata.FrameId
                    || !_retryFrames.TryGetValue(retryIntent.Id, out RetainedFrame? retained)
                    || retained.RemainingTapAttempts <= 0
                    || !SameSource(retained.Frame.Metadata, provenance.Metadata)
                    || retained.CanonicalTapPoint != retryIntent.CanonicalTapPoint)
                {
                    throw new InvalidOperationException("The retry frame has expired or exhausted its task budget.");
                }

                retained.ActiveUses++;
                deviceFrame = retained.Frame;
            }
        }

        DeviceOperationEvent result;
        try
        {
            result = await _service.TapAsync(deviceFrame, point.X, point.Y, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseUse(provenance.Metadata.FrameId, retryIntent?.Id);
            lock (_sync)
            {
                InvalidateOrdinaryFramesLocked();
                if (isRetry && retryIntent is not null
                    && _retryFrames.TryGetValue(retryIntent.Id, out RetainedFrame? retained))
                {
                    retained.RemainingTapAttempts--;
                    if (retained.RemainingTapAttempts == 0)
                        RetireRetryFrameLocked(retryIntent.Id, retained);
                }
            }
        }

        EnsureInputSucceeded(result, "tap");
    }

    private async Task FinishHeldTouchAsync(
        LastVerifiedConnection connection,
        FrameProvenance source,
        bool cancel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_heldFrame is null
                    && _lastReleasedHeldFrameId == source.Metadata.FrameId)
                {
                    return;
                }
            }
            DeviceSessionIdentity identity = VerifyConnection(connection);
            RetainedFrame held;
            bool cleanupPending;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_heldFrame is null)
                {
                    if (_lastReleasedHeldFrameId == source.Metadata.FrameId)
                        return;
                    throw new InvalidOperationException("The supplied frame does not own the active held touch.");
                }

                if (!SameSource(_heldFrame.Frame.Metadata, source.Metadata)
                    || _heldFrame.Frame.Metadata.SessionGeneration != identity.SessionGeneration)
                {
                    throw new InvalidOperationException("The supplied frame does not own the active held touch.");
                }
                held = _heldFrame;
                cleanupPending = _heldTouchCleanupPending;
                held.ActiveUses++;
            }

            DeviceOperationEvent result;
            try
            {
                result = cancel
                    ? await _service.TouchCancelAsync(held.Frame, cancellationToken).ConfigureAwait(false)
                    : await _service.TouchUpAsync(held.Frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReleaseUse(held.Frame.FrameId);
            }

            bool pendingDownAlreadyReleased = cancel
                && cleanupPending
                && result is InputFailedEvent
                {
                    ErrorCode: ConnectionErrorCode.DeviceNotReady,
                    Phase: "touch_release",
                };
            if (!pendingDownAlreadyReleased)
                EnsureInputSucceeded(result, cancel ? "touch CANCEL" : "touch UP");
            lock (_sync)
            {
                if (ReferenceEquals(_heldFrame, held))
                {
                    _heldFrame = null;
                    _heldTouchCleanupPending = false;
                    _lastReleasedHeldFrameId = held.Frame.FrameId;
                    held.Frame.Dispose();
                }
                InvalidateOrdinaryFramesLocked();
            }
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    private DeviceFrame AcquireOrdinaryFrame(
        LastVerifiedConnection connection,
        FrameProvenance provenance)
    {
        DeviceSessionIdentity identity = VerifyConnection(connection);
        lock (_sync)
        {
            ThrowIfDisposed();
            ExpireFramesLocked();
            ValidateCurrentGeometry(provenance, identity);
            return AcquireOrdinaryFrameLocked(provenance);
        }
    }

    private DeviceFrame AcquireOrdinaryFrameLocked(FrameProvenance provenance)
    {
        if (!_ordinaryFrames.TryGetValue(provenance.Metadata.FrameId, out RetainedFrame? retained)
            || !SameSource(retained.Frame.Metadata, provenance.Metadata))
        {
            throw new InvalidOperationException("The recognition frame has expired; capture and recognize the current screen again.");
        }

        retained.ActiveUses++;
        return retained.Frame;
    }

    private void ValidateCurrentGeometry(FrameProvenance provenance, DeviceSessionIdentity identity)
    {
        if (provenance.Metadata.SessionGeneration != identity.SessionGeneration
            || (_knownSessionGeneration == identity.SessionGeneration
                && _knownGeometryGeneration is ulong geometry
                && provenance.Metadata.GeometryGeneration != geometry))
        {
            throw new InvalidOperationException("The recognition frame belongs to an old device session or geometry; recognize the current screen again.");
        }
    }

    private DeviceSessionIdentity VerifyConnection(LastVerifiedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return VerifyConnection(connection.AdbPath, connection.Serial);
    }

    private DeviceSessionIdentity VerifyConnection(string adbPath, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        DeviceSessionIdentity identity = _service.ActiveDeviceIdentity
            ?? throw new InvalidOperationException("The native core has no active device session.");
        string requestedPath = Path.GetFullPath(adbPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(requestedPath, identity.AdbPath, pathComparison)
            || !string.Equals(serial, identity.Serial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied connection does not match the active native ADB path and serial.");
        }

        return identity;
    }

    private void EnsureCaptureCapacityLocked()
    {
        int specialPins = _retryFrames.Count + (_heldFrame is null ? 0 : 1);
        int ordinaryLimit = Math.Min(3, NativeFrameCapacity - specialPins);
        while (_ordinaryFrames.Count >= ordinaryLimit)
        {
            RetainedFrame? oldest = _ordinaryFrames.Values
                .Where(frame => frame.ActiveUses == 0)
                .MinBy(frame => frame.CreatedAt);
            if (oldest is null)
                throw new InvalidOperationException("All native frame slots are pinned by an active press or bounded retry.");
            RetireOrdinaryFrameLocked(oldest.Frame.FrameId, oldest);
        }
    }

    private void ExpireFrames()
    {
        lock (_sync)
        {
            if (_disposed || _disposing)
                return;
            ExpireFramesLocked();
        }
    }

    private void ExpireFramesLocked()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (var pair in _ordinaryFrames.ToArray())
        {
            if (pair.Value.ExpiresAt <= now && pair.Value.ActiveUses == 0)
                RetireOrdinaryFrameLocked(pair.Key, pair.Value);
        }
        foreach (var pair in _retryFrames.ToArray())
        {
            if (pair.Value.ExpiresAt <= now && pair.Value.ActiveUses == 0)
                RetireRetryFrameLocked(pair.Key, pair.Value);
        }
    }

    private void ReleaseUse(ulong frameId, Guid? retryId = null)
    {
        lock (_sync)
        {
            RetainedFrame? retained = retryId is { } id && _retryFrames.TryGetValue(id, out var retry)
                ? retry
                : _heldFrame?.Frame.FrameId == frameId
                    ? _heldFrame
                    : _ordinaryFrames.GetValueOrDefault(frameId);
            if (retained is null)
                return;
            retained.ActiveUses = Math.Max(0, retained.ActiveUses - 1);
            if (retained.ActiveUses == 0)
                ExpireFramesLocked();
        }
    }

    private void InvalidateOrdinaryFramesLocked()
    {
        foreach (var pair in _ordinaryFrames.ToArray())
            RetireOrdinaryFrameLocked(pair.Key, pair.Value);
    }

    private void InvalidateRetryFramesLocked()
    {
        foreach (var pair in _retryFrames.ToArray())
            RetireRetryFrameLocked(pair.Key, pair.Value);
    }

    private void RetireOrdinaryFrame(ulong frameId)
    {
        lock (_sync)
        {
            if (_ordinaryFrames.TryGetValue(frameId, out RetainedFrame? retained))
                RetireOrdinaryFrameLocked(frameId, retained);
        }
    }

    private void RetireOrdinaryFrameLocked(ulong frameId, RetainedFrame retained)
    {
        _ordinaryFrames.Remove(frameId);
        retained.Frame.Dispose();
    }

    private void RetireRetryFrameLocked(Guid id, RetainedFrame retained)
    {
        _retryFrames.Remove(id);
        retained.Frame.Dispose();
    }

    private void ReleaseRetryIntent(Guid id)
    {
        lock (_sync)
        {
            if (_retryFrames.TryGetValue(id, out RetainedFrame? retained))
                RetireRetryFrameLocked(id, retained);
        }
    }

    private void OnConnectionEvent(ConnectionEvent connectionEvent)
    {
        bool applies = connectionEvent is ConnectionStartedEvent;
        lock (_sync)
        {
            applies |= connectionEvent switch
            {
                DeviceDisconnectedEvent disconnected =>
                    _knownSessionGeneration == disconnected.SessionGeneration
                    || _ordinaryFrames.Values.Any(frame =>
                        frame.Frame.Metadata.SessionGeneration == disconnected.SessionGeneration)
                    || _retryFrames.Values.Any(frame =>
                        frame.Frame.Metadata.SessionGeneration == disconnected.SessionGeneration)
                    || _heldFrame?.Frame.Metadata.SessionGeneration == disconnected.SessionGeneration,
                DeviceReconnectedEvent reconnected =>
                    _knownSessionGeneration == reconnected.PreviousSessionGeneration
                    || _ordinaryFrames.Values.Any(frame =>
                        frame.Frame.Metadata.SessionGeneration == reconnected.PreviousSessionGeneration)
                    || _retryFrames.Values.Any(frame =>
                        frame.Frame.Metadata.SessionGeneration == reconnected.PreviousSessionGeneration)
                    || _heldFrame?.Frame.Metadata.SessionGeneration == reconnected.PreviousSessionGeneration,
                _ => false,
            };
            if (!applies || _disposing || _disposed)
                return;
            InvalidateOrdinaryFramesLocked();
            InvalidateRetryFramesLocked();
            if (_heldFrame is { } held)
            {
                _lastReleasedHeldFrameId = held.Frame.FrameId;
                held.Frame.Dispose();
                _heldFrame = null;
            }
            _knownSessionGeneration = null;
            _knownGeometryGeneration = null;
        }
    }

    private static void EnsureInputSucceeded(DeviceOperationEvent result, string operation)
    {
        if (result is InputSucceededEvent)
            return;
        if (result is InputFailedEvent inputFailure)
            throw new InvalidOperationException($"Native {operation} failed in {inputFailure.Phase}: {inputFailure.Message}");
        EnsureOperationSucceeded(result, operation);
        throw new InvalidOperationException($"Native {operation} completed without input confirmation.");
    }

    private static void EnsureOperationSucceeded(DeviceOperationEvent result, string operation)
    {
        if (result is DeviceOperationFailedEvent failure)
            throw new InvalidOperationException($"Native {operation} failed in {failure.Phase}: {failure.Message}");
        if (result is GameVerificationFailedEvent gameFailure)
            throw new InvalidOperationException($"Native {operation} failed in {gameFailure.Phase}: {gameFailure.Message}");
    }

    private void PinHeldFrame(DeviceFrame source, bool cleanupPending)
    {
        lock (_sync)
        {
            if (_heldFrame is not null)
                throw new InvalidOperationException("A held touch is already active.");
            if (!_ordinaryFrames.Remove(source.FrameId, out RetainedFrame? retained))
            {
                throw new InvalidOperationException(
                    "The DOWN source frame expired before it could be pinned.");
            }

            retained.ActiveUses = Math.Max(0, retained.ActiveUses - 1);
            _heldFrame = retained;
            _heldTouchCleanupPending = cleanupPending;
            InvalidateOrdinaryFramesLocked();
        }
    }

    private async Task<bool> BestEffortCancelDownAsync(DeviceFrame source)
    {
        using var timeout = new CancellationTokenSource();
        const int timeoutMilliseconds = 2_000;
        try
        {
            timeout.CancelAfter(timeoutMilliseconds);
            DeviceOperationEvent result = await _service.TouchCancelAsync(source, timeout.Token)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), timeout.Token)
                .ConfigureAwait(false);
            return result is InputSucceededEvent
                || result is InputFailedEvent
                {
                    ErrorCode: ConnectionErrorCode.DeviceNotReady,
                    Phase: "touch_release",
                };
        }
        catch
        {
            return false;
        }
    }

    private static bool SameSource(DeviceFrameMetadata left, DeviceFrameMetadata right) =>
        left.FrameId == right.FrameId
        && left.SessionGeneration == right.SessionGeneration
        && left.GeometryGeneration == right.GeometryGeneration;

    private static bool SameSource(FrameProvenance left, FrameProvenance right) =>
        SameSource(left.Metadata, right.Metadata);

    private static int ScaleCoordinate(int value, int reference, int actual) =>
        Math.Clamp(
            (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference)),
            0,
            Math.Max(0, actual - 1));

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        long shutdownStarted = Stopwatch.GetTimestamp();
        TimeSpan shutdownBudget = TimeSpan.FromSeconds(2);
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposing = true;
        }

        _service.ConnectionEventReceived -= OnConnectionEvent;
        await _expiryTimer.DisposeAsync().ConfigureAwait(false);
        bool ownsDeviceGate = await _deviceGate.WaitAsync(shutdownBudget).ConfigureAwait(false);
        if (!ownsDeviceGate)
        {
            // Native service shutdown has its own bounded drain and SafeHandle
            // leases. Marking DeviceFrame disposed is safe during an in-flight
            // ABI use: DeviceFrame delays releasing its native lease until that
            // use exits. Do not wait forever on a stalled ADB/native operation.
            lock (_sync)
            {
                _disposed = true;
                InvalidateOrdinaryFramesLocked();
                InvalidateRetryFramesLocked();
                _heldFrame?.Frame.Dispose();
                _heldFrame = null;
            }
            return;
        }

        try
        {
            RetainedFrame? held;
            lock (_sync)
            {
                _disposed = true;
                held = _heldFrame;
            }

            if (held is not null && _service.ActiveDeviceIdentity is { } identity
                && identity.SessionGeneration == held.Frame.Metadata.SessionGeneration)
            {
                TimeSpan remaining = shutdownBudget - Stopwatch.GetElapsedTime(shutdownStarted);
                using var timeout = new CancellationTokenSource();
                try
                {
                    if (remaining > TimeSpan.Zero)
                    {
                        timeout.CancelAfter(remaining);
                        await _service.TouchCancelAsync(held.Frame, timeout.Token)
                            .WaitAsync(remaining, timeout.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // UmaService owns final native-session shutdown and detaches any
                    // remaining leases if this bounded best-effort cancellation times out.
                }
            }

            lock (_sync)
            {
                InvalidateOrdinaryFramesLocked();
                InvalidateRetryFramesLocked();
                if (_heldFrame is { } remaining)
                {
                    remaining.Frame.Dispose();
                    _heldFrame = null;
                }
            }
        }
        finally
        {
            _deviceGate.Release();
        }
    }

    private sealed class RetainedFrame(
        DeviceFrame frame,
        GrayImage image,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        internal DeviceFrame Frame { get; } = frame;
        internal GrayImage Image { get; } = image;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        internal int ActiveUses { get; set; }
        internal int RemainingTapAttempts { get; set; }
        internal (int X, int Y) CanonicalTapPoint { get; set; }
    }
}
