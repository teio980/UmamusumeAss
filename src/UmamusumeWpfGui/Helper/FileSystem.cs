namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Real implementation of <see cref="IFileSystem"/> that delegates to <see cref="System.IO.File.Exists"/>.
/// </summary>
public sealed class FileSystem : IFileSystem
{
    public bool FileExists(string path) => System.IO.File.Exists(path);
}
