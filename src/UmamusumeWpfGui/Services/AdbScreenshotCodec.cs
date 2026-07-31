using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;






public static class AdbScreenshotCodec
{
    public static bool TryDecodeRaw(
        ReadOnlySpan<byte> data,
        bool gzip,
        out AdbRawScreenshot? screenshot)
    {
        screenshot = null;
        var payload = gzip ? TryDecompress(data) : data.ToArray();
        if (payload is null || !TryDecodePayload(payload, out screenshot))
        {
            var normalized = RemoveCarriageReturns(payload ?? []);
            if (normalized.SequenceEqual(payload ?? [])
                || !TryDecodePayload(normalized, out screenshot))
            {
                screenshot = null;
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodePayload(
        ReadOnlySpan<byte> data,
        out AdbRawScreenshot? screenshot)
    {
        screenshot = null;
        if (data.Length < 8)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var pixelBytes = checked(width * height * 4);
        var headerBytes = data.Length - pixelBytes;
        if (headerBytes < 8 || data.Length < pixelBytes)
        {
            return false;
        }

        var rgba = data.Slice(headerBytes, pixelBytes).ToArray();
        if (rgba.Length < 4 || rgba[^1] != 255)
        {
            return false;
        }

        screenshot = new AdbRawScreenshot(width, height, rgba);
        return true;
    }

    private static byte[]? TryDecompress(ReadOnlySpan<byte> data)
    {
        try
        {
            using var source = new MemoryStream(data.ToArray());
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[] RemoveCarriageReturns(byte[] data)
    {
        if (data.Length < 2 || !data.Contains((byte)'\r'))
        {
            return data;
        }

        var output = new List<byte>(data.Length);
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] == '\r'
                && index + 1 < data.Length
                && data[index + 1] == '\n')
            {
                continue;
            }

            output.Add(data[index]);
        }

        return output.ToArray();
    }
}
