using System;
using System.IO;
using System.Text.Json;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;

namespace Umamusume.AppCore.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public JsonSettingsService()
        : this(Path.Combine(PlatformPaths.GetDefaultUserDataDirectory(), "connection_settings.json"))
    {
    }

    public JsonSettingsService(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public ConnectionSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new ConnectionSettings();
                defaults.AdbPath = PlatformPaths.FindAdbExecutable();
                if (OperatingSystem.IsMacOS() && string.IsNullOrEmpty(defaults.ConnectAddress))
                {
                    defaults.ConnectAddress = "127.0.0.1:5555";
                }
                return defaults;
            }

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new ConnectionSettings();

            var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json, JsonOptions) ?? new ConnectionSettings();
            loaded.SynchronizeTaskQueue(preferWindowsField: true);
            if (string.IsNullOrWhiteSpace(loaded.AdbPath))
            {
                loaded.AdbPath = PlatformPaths.FindAdbExecutable();
            }
            if (OperatingSystem.IsMacOS() && string.IsNullOrWhiteSpace(loaded.ConnectAddress))
            {
                loaded.ConnectAddress = "127.0.0.1:5555";
            }
            return loaded;
        }
        catch
        {
            return new ConnectionSettings();
        }
    }

    public void Save(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SynchronizeTaskQueue();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
