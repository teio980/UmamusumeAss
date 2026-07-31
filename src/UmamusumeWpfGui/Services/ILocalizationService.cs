namespace UmamusumeWpfGui.Services;





public interface ILocalizationService
{

    string CurrentCulture { get; }


    event EventHandler<string>? LanguageChanged;





    void Initialize();






    void SwitchLanguage(string culture);





    string GetString(string key);
}