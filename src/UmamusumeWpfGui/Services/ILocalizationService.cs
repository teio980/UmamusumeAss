namespace UmamusumeWpfGui.Services;

/// <summary>
/// Localization contract. Manages UI culture and provides string lookup
/// from WPF resource dictionaries.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Current culture code (e.g. "en-US", "zh-CN").</summary>
    string CurrentCulture { get; }

    /// <summary>Raised when the active culture changes. Argument is the new culture code.</summary>
    event EventHandler<string>? LanguageChanged;

    /// <summary>
    /// Loads the persisted culture from <see cref="ISettingsService"/>
    /// and applies it at startup.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Switches the active culture, replaces string resources,
    /// persists the choice, and fires <see cref="LanguageChanged"/>.
    /// Invalid, null, or empty values fall back to "en-US".
    /// </summary>
    void SwitchLanguage(string culture);

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>.
    /// Returns <paramref name="key"/> itself when the key is not found.
    /// </summary>
    string GetString(string key);
}