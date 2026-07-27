using System.Windows;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Localization service that manages WPF resource dictionaries for UI strings.
/// Switches between culture-specific XAML dictionaries at runtime,
/// persists the selected culture via <see cref="ISettingsService"/>,
/// and fires <see cref="LanguageChanged"/> on switch.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private readonly ISettingsService _settingsService;
    private readonly ResourceDictionary _appResources;
    private readonly Func<string, ResourceDictionary> _loadDictionary;
    private ResourceDictionary? _currentStringDictionary;
    private string _currentCulture = "en-US";

    /// <summary>
    /// Production constructor. Uses <c>Application.Current.Resources</c>
    /// and loads dictionaries from <c>Resources/Strings.{culture}.xaml</c>.
    /// </summary>
    public LocalizationService(ISettingsService settingsService)
        : this(settingsService,
              Application.Current?.Resources ?? new ResourceDictionary(),
              culture => new ResourceDictionary
              {
                  Source = new Uri($"Resources/Strings.{culture}.xaml", UriKind.Relative)
              })
    {
    }

    /// <summary>
    /// Test constructor with full seam injection.
    /// </summary>
    /// <param name="settingsService">Settings persistence.</param>
    /// <param name="appResources">The application-level <see cref="ResourceDictionary"/>
    /// whose <c>MergedDictionaries</c> will be manipulated.</param>
    /// <param name="loadDictionary">Factory that returns a populated
    /// <see cref="ResourceDictionary"/> for the given culture code.</param>
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

    /// <inheritdoc />
    public string CurrentCulture => _currentCulture;

    /// <inheritdoc />
    public event EventHandler<string>? LanguageChanged;

    /// <inheritdoc />
    public void Initialize()
    {
        var settings = _settingsService.Load();
        var culture = settings.Language;

        if (IsValidCulture(culture))
        {
            ApplyCultureInternal(culture);
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public string GetString(string key)
    {
        if (_currentStringDictionary is not null && _currentStringDictionary.Contains(key))
            return _currentStringDictionary[key] as string ?? key;

        return key;
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

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
        // Remove the previously loaded string dictionary, if any.
        if (_currentStringDictionary is not null)
            _appResources.MergedDictionaries.Remove(_currentStringDictionary);

        // Load and add the new culture's dictionary.
        _currentStringDictionary = _loadDictionary(culture);
        _appResources.MergedDictionaries.Add(_currentStringDictionary);
    }

    private static bool IsValidCulture(string culture)
    {
        return culture is "en-US" or "zh-CN";
    }
}