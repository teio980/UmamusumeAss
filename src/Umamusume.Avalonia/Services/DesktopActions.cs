using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Umamusume.Avalonia.Services;

internal static class DesktopActions
{
    public static async Task<string?> PickFileAsync(string title, bool folder = false)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } window)
            return null;
        if (folder)
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } window) return null;
        return (await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, ShowOverwritePrompt = true }))?.TryGetLocalPath();
    }

    public static void Open(string path)
    {
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("open")
            : OperatingSystem.IsWindows()
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open");
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(path);
        using var process = Process.Start(start);
    }

    public static void StartEmulator(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            throw new FileNotFoundException("请选择有效的模拟器应用或可执行文件。", path);
        if (OperatingSystem.IsMacOS() && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var start = new ProcessStartInfo("open");
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(path);
            using var app = Process.Start(start);
        }
        else
        {
            using var app = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
