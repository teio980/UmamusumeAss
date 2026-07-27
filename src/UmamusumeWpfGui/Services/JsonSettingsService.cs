using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// JSON-based settings persistence using System.Text.Json.
/// Stores settings at the specified file path. Missing or malformed files
/// return safe defaults.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    /// <summary>
    /// Creates a service with the default path:
    /// <c>%APPDATA%/UmamusumeAss/connection_settings.json</c>
    /// </summary>
    public JsonSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UmamusumeAss",
            "connection_settings.json"))
    {
    }

    /// <summary>
    /// Creates a service with an explicit file path (test injection).
    /// </summary>
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
                return new ConnectionSettings();

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new ConnectionSettings();

            return JsonSerializer.Deserialize<ConnectionSettings>(json) ?? new ConnectionSettings();
        }
        catch (JsonException)
        {
            return new ConnectionSettings();
        }
        catch (IOException)
        {
            return new ConnectionSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new ConnectionSettings();
        }
    }

    public void Save(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
