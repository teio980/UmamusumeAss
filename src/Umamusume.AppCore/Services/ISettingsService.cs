using Umamusume.AppCore.Models;

namespace Umamusume.AppCore.Services;

public interface ISettingsService
{
    ConnectionSettings Load();
    void Save(ConnectionSettings settings);
}
