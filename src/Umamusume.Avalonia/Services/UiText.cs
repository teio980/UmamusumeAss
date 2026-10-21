using Umamusume.AppCore.Services;

namespace Umamusume.Avalonia.Services;

internal static class UiText
{
    public static bool IsEnglish(ILocalizationService? localizationService) =>
        localizationService?.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    public static string Get(ILocalizationService? localizationService, string chinese, string english) =>
        IsEnglish(localizationService) ? english : chinese;
}
