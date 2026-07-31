namespace UmamusumeWpfGui.Helper;




public sealed class FileSystem : IFileSystem
{
    public bool FileExists(string path) => System.IO.File.Exists(path);
}
