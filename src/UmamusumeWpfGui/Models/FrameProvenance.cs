using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Value-only origin for pixels copied from a native device frame. It never
/// owns the frame lease; the device facade retains leases in a bounded registry.
/// </summary>
public sealed record FrameProvenance(
    DeviceFrameMetadata Metadata,
    double SourceOffsetX = 0,
    double SourceOffsetY = 0,
    double SourceScaleX = 1,
    double SourceScaleY = 1)
{
    public FrameProvenance Crop(int x, int y) => this with
    {
        SourceOffsetX = SourceOffsetX + x * SourceScaleX,
        SourceOffsetY = SourceOffsetY + y * SourceScaleY,
    };

    public FrameProvenance Resize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        return this with
        {
            SourceScaleX = SourceScaleX * sourceWidth / targetWidth,
            SourceScaleY = SourceScaleY * sourceHeight / targetHeight,
        };
    }

    public (int X, int Y) MapToCanonical(double imageX, double imageY)
    {
        var transform = Metadata.Transform;
        if (!double.IsFinite(transform.Scale) || transform.Scale <= 0)
        {
            throw new InvalidOperationException(
                $"Native frame {Metadata.FrameId} has an invalid display scale.");
        }
        var sourceX = SourceOffsetX + imageX * SourceScaleX;
        var sourceY = SourceOffsetY + imageY * SourceScaleY;
        var logicalX = (sourceX - transform.OffsetX) / transform.Scale;
        var logicalY = (sourceY - transform.OffsetY) / transform.Scale;
        (double X, double Y) point = Metadata.Orientation switch
        {
            0 => (logicalX, logicalY),
            1 => (logicalY, transform.CanonicalHeight - 1d - logicalX),
            2 => (transform.CanonicalWidth - 1d - logicalX,
                transform.CanonicalHeight - 1d - logicalY),
            3 => (transform.CanonicalWidth - 1d - logicalY, logicalX),
            _ => throw new InvalidOperationException(
                $"Native frame {Metadata.FrameId} has unsupported orientation {Metadata.Orientation}."),
        };
        return (
            RoundClamp(point.X, transform.CanonicalWidth),
            RoundClamp(point.Y, transform.CanonicalHeight));
    }

    private static int RoundClamp(double value, int extent)
    {
        if (!double.IsFinite(value) || extent <= 0)
        {
            throw new InvalidOperationException("Native frame coordinates are not finite or the canonical extent is invalid.");
        }

        var rounded = Math.Round(value, MidpointRounding.ToEven);
        return (int)Math.Clamp(rounded, 0d, extent - 1d);
    }
}
