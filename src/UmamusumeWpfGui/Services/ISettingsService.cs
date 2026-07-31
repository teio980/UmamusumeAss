using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;




public interface ISettingsService
{

    ConnectionSettings Load();


    void Save(ConnectionSettings settings);
}
