using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Persistence contract for <see cref="ConnectionSettings"/>.
/// </summary>
public interface ISettingsService
{
    /// <summary>Loads settings from persistent storage. Returns defaults if storage is missing or corrupt.</summary>
    ConnectionSettings Load();

    /// <summary>Saves settings to persistent storage.</summary>
    void Save(ConnectionSettings settings);
}
