using System.Windows;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;







public sealed class LocalizationService : ILocalizationService
{
    private readonly ISettingsService _settingsService;
    private readonly ResourceDictionary _appResources;
    private readonly Func<string, ResourceDictionary> _loadDictionary;
    private ResourceDictionary? _currentStringDictionary;
    private string _currentCulture = "en-US";





    public LocalizationService(ISettingsService settingsService)
        : this(settingsService,
              Application.Current?.Resources ?? new ResourceDictionary(),
              culture => new ResourceDictionary
              {
                  Source = new Uri($"Resources/Strings.{culture}.xaml", UriKind.Relative)
              })
    {
    }









    internal LocalizationService(
        ISettingsService settingsService,
        ResourceDictionary appResources,
        Func<string, ResourceDictionary> loadDictionary)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(appResources);
        ArgumentNullException.ThrowIfNull(loadDictionary);

        _settingsService = settingsService;
        _appResources = appResources;
        _loadDictionary = loadDictionary;
    }


    public string CurrentCulture => _currentCulture;


    public event EventHandler<string>? LanguageChanged;


    public void Initialize()
    {
        var settings = _settingsService.Load();
        var culture = settings.Language;

        if (IsValidCulture(culture))
        {
            ApplyCultureInternal(culture);
        }
    }


    public void SwitchLanguage(string culture)
    {
        if (!IsValidCulture(culture))
            culture = "en-US";

        if (_currentCulture == culture)
            return;

        ApplyCultureInternal(culture);
        PersistLanguage(culture);
        LanguageChanged?.Invoke(this, culture);
    }


    public string GetString(string key)
    {
        if (_currentStringDictionary is not null && _currentStringDictionary.Contains(key))
            return _currentStringDictionary[key] as string ?? key;

        return key;
    }





    private void ApplyCultureInternal(string culture)
    {
        _currentCulture = culture;
        ReplaceStringDictionary(culture);
    }

    private void PersistLanguage(string culture)
    {
        var settings = _settingsService.Load();
        settings.Language = culture;
        _settingsService.Save(settings);
    }

    private void ReplaceStringDictionary(string culture)
    {

        if (_currentStringDictionary is not null)
            _appResources.MergedDictionaries.Remove(_currentStringDictionary);


        _currentStringDictionary = _loadDictionary(culture);
        _appResources.MergedDictionaries.Add(_currentStringDictionary);
    }

    private static bool IsValidCulture(string culture)
    {
        return culture is "en-US" or "zh-CN";
    }
}