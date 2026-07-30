using System.IO;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Resolves the same ABI priority used by MAA when selecting a minitouch
/// executable. The locator does not copy or download binaries; the caller
/// decides which licensed resource directory is supplied.
/// </summary>
public static class MinitouchProgramLocator
{
    private static readonly string[] AbiPriority =
    [
        "x86_64",
        "x86",
        "arm64-v8a",
        "armeabi-v7a",
        "armeabi"
    ];

    public static string? Resolve(
        string resourceRoot,
        string abiList,
        bool useMaaTouch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(abiList);

        if (useMaaTouch)
        {
            var maaTouch = Path.Combine(resourceRoot, "maatouch", "minitouch");
            return File.Exists(maaTouch) ? maaTouch : null;
        }

        foreach (var abi in AbiPriority)
        {
            if (!abiList.Contains(abi, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Path.Combine(resourceRoot, abi, "minitouch");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
