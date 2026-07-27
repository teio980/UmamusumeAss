namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Abstraction over file-system existence checks, so emulator discovery
/// can be tested without real files.
/// </summary>
public interface IFileSystem
{
    /// <summary>Returns true when a file exists at the given absolute path.</summary>
    bool FileExists(string path);
}
