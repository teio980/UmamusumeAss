using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;






public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;





    public JsonSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UmamusumeAss",
            "connection_settings.json"))
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
