namespace UmamusumeWpfGui.Services;

/// <summary>Process-wide root for the materialized, immutable resource view.</summary>
public static class ResourcePathRuntime
{
    private static readonly object Sync = new();
    private static string _baseDirectory = AppContext.BaseDirectory;
    private static string? _overrideRoot;

    public static string BaseDirectory
    {
        get { lock (Sync) return _baseDirectory; }
    }

    public static void SetBaseDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        lock (Sync) _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public static void SetOverrideRoot(string overrideRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideRoot);
        lock (Sync) _overrideRoot = Path.GetFullPath(overrideRoot);
    }

    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathFullyQualified(path))
            return Path.GetFullPath(path);
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(BaseDirectory, normalized));
    }

    public static string ResolveWritePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        string viewRoot;
        string? overrideRoot;
        lock (Sync)
        {
            viewRoot = Path.GetFullPath(Path.Combine(_baseDirectory, "resource"));
            overrideRoot = _overrideRoot;
        }
        if (overrideRoot is null) return full;
        var prefix = viewRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        return Path.Combine(overrideRoot, "resource", Path.GetRelativePath(viewRoot, full));
    }
}
