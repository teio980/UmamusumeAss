using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Theme;





public sealed class ThemeResourceTests
{
    private static readonly XNamespace XamlNs =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ResDir => Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"..\..\..\..\..\src\UmamusumeWpfGui\Res"));

    private static string ResourcesDir => Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"..\..\..\..\..\src\UmamusumeWpfGui\Resources"));

    private static string ThemesDir => Path.Combine(ResDir, "Themes");
    private static string StylesDir => Path.Combine(ResDir, "Styles");

    private static XDocument LoadXaml(string path)
    {
        Assert.True(File.Exists(path), $"File not found: {path}");
        return XDocument.Load(path);
    }




    private static XElement[] GetResources(string path)
    {
        var doc = LoadXaml(path);
        var ns = doc.Root!.GetDefaultNamespace();
        var dict = doc.Root!;
        var resources = dict.Elements()
            .Concat(dict.Element(ns + "ResourceDictionary.Resources")?.Elements() ?? [])
            .Where(e => e.Name != ns + "ResourceDictionary.MergedDictionaries")
            .ToArray();
        return resources;
    }

    private static string? GetKey(XElement e)
    {
        return e.Attribute(XamlNs + "Key")?.Value
               ?? e.Attribute("Key")?.Value;
    }





    [Fact]
    public void LightXamlContainsAccentPrimaryBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "AccentPrimaryBrush");
        Assert.Equal("#7653A6", brush.color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsSurfaceCanvasBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "SurfaceCanvasBrush");
        Assert.Equal("#F6F5F8", brush.color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsStatusErrorBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "StatusErrorBrush");
        Assert.Equal("#B84B58", brush.color, ignoreCase: true);
    }





    private static readonly string[] DesignBrushKeys =
    [
        "SurfaceCanvasBrush",
        "SurfaceSidebarBrush",
        "SurfacePanelBrush",
        "SurfaceRaisedBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextDisabledBrush",
        "BorderDefaultBrush",
        "BorderSubtleBrush",
        "AccentPrimaryBrush",
        "AccentHoverBrush",
        "StatusSuccessBrush",
        "StatusWarningBrush",
        "StatusErrorBrush",
        "StatusInfoBrush",
    ];

    [Fact]
    public void LightXamlContainsAllDesignBrushKeys()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName is "SolidColorBrush")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        foreach (var key in DesignBrushKeys)
            Assert.Contains(key, keys);
    }

    [Fact]
    public void LightXamlNoLongerContainsPrimaryBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        Assert.DoesNotContain("PrimaryBrush", keys);
    }

    [Fact]
    public void LightXamlNoLongerContainsGoldBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        Assert.DoesNotContain("GoldBrush", keys);
    }

    [Fact]
    public void LightXamlNoLongerContainsWindowBackgroundGradient()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        Assert.DoesNotContain("WindowBackgroundGradient", keys);
    }





    private static readonly string[] OverviewStringKeys =
    [
        "NavOverview",
        "OverviewTitle",
        "OverviewConnectionStatus",
        "OverviewDevice",
        "OverviewCoreVersion",
        "OverviewNoConnection",
        "OverviewOpenSettings",
    ];

    [Fact]
    public void EnUsStringsContainsAllOverviewKeys()
    {
        var resources = GetResources(
            Path.Combine(ResourcesDir, "Strings.en-US.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName == "String")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        foreach (var key in OverviewStringKeys)
            Assert.Contains(key, keys);
    }

    [Fact]
    public void ZhCnStringsContainsAllOverviewKeys()
    {
        var resources = GetResources(
            Path.Combine(ResourcesDir, "Strings.zh-CN.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName == "String")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        foreach (var key in OverviewStringKeys)
            Assert.Contains(key, keys);
    }


    [Fact]
    public void ButtonXamlPrimaryButtonUsesAccentBrush()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "PrimaryButtonStyle");
        var background = style.setters.First(s => s.property == "Background");
        Assert.Equal("{DynamicResource AccentPrimaryBrush}", background.value);
    }

    [Fact]
    public void ButtonXamlPrimaryButtonHasCompactHeight()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "PrimaryButtonStyle");

        var height = style.setters.First(s => s.property == "Height");
        Assert.Equal("34", height.value);
    }

    [Fact]
    public void ButtonXamlPrimaryButtonStyleHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "PrimaryButtonStyle");

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }

    [Fact]
    public void ButtonXamlSecondaryButtonStyleHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "SecondaryButtonStyle");

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }

    [Fact]
    public void ButtonXamlPrimaryButtonHasHoverTrigger()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var content = File.ReadAllText(Path.Combine(StylesDir, "Button.xaml"));
        Assert.Contains("IsMouseOver", content);
        Assert.Contains("AccentHoverBrush", content);
    }





    [Fact]
    public void TextBoxXamlHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "TextBox.xaml"));
        var style = GetStyle(resources, null);

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }





    [Fact]
    public void ComboBoxXamlHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "ComboBox.xaml"));
        var style = GetStyle(resources, null);

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }





    [Fact]
    public void StyleXamlBorderHasCornerRadius8()
    {
        var resources = GetResources(Path.Combine(ResDir, "Style.xaml"));
        var style = GetStyle(resources, null, targetType: "Border");

        var crSetter = style.setters.FirstOrDefault(
            s => s.property == "CornerRadius");
        Assert.NotEqual(default, crSetter);
        Assert.Equal("8", crSetter.value);
    }

    [Fact]
    public void StyleXamlTabItemForegroundUsesDynamicThemeResource()
    {
        var resources = GetResources(Path.Combine(ResDir, "Style.xaml"));
        var style = GetStyle(resources, null, targetType: "TabItem");

        var foregroundSetter = style.setters.FirstOrDefault(
            setter => setter.property == "Foreground");

        Assert.Equal("{DynamicResource TextSecondaryBrush}", foregroundSetter.value);
    }

    [Fact]
    public void StyleXamlTextBlockForegroundUsesDynamicThemeResource()
    {
        var resources = GetResources(Path.Combine(ResDir, "Style.xaml"));
        var style = GetStyle(resources, null, targetType: "TextBlock");

        var foregroundSetter = style.setters.FirstOrDefault(
            setter => setter.property == "Foreground");

        Assert.Equal("{DynamicResource TextPrimaryBrush}", foregroundSetter.value);
    }

    [Fact]
    public void ThemePaletteResourcesUseDynamicLookupOutsidePaletteDefinition()
    {
        var sourceDir = Path.GetFullPath(
            Path.Combine(ResDir, ".."));
        var paletteKeys = DesignBrushKeys;

        foreach (var path in Directory.GetFiles(sourceDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(path), Path.Combine(ThemesDir, "Light.xaml"), StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            foreach (var key in paletteKeys)
            {
                Assert.DoesNotContain($"{{StaticResource {key}}}", content);
            }
        }
    }





    private static (string color, string? offset) GetSolidColorBrush(
        XElement[] resources, string key)
    {
        var el = FindResource(resources, key, "SolidColorBrush");
        var color = el.Attribute("Color")?.Value
                    ?? el.Element(el.GetDefaultNamespace() + "SolidColorBrush.Color")?.Value
                    ?? throw new KeyNotFoundException($"Color not found for {key}");
        return (color, offset: null);
    }

    private static (string startPoint, string endPoint,
        List<(string color, string offset)> stops) GetGradientBrush(
        XElement[] resources, string key)
    {
        var el = FindResource(resources, key, "LinearGradientBrush");
        var ns = el.GetDefaultNamespace();

        var startPoint = el.Attribute("StartPoint")?.Value ?? "";
        var endPoint = el.Attribute("EndPoint")?.Value ?? "";

        var stops = el.Elements(ns + "LinearGradientBrush.GradientStops")
            .Elements(ns + "GradientStop")
            .Select(gs => (
                color: gs.Attribute("Color")?.Value ?? "",
                offset: gs.Attribute("Offset")?.Value ?? ""))
            .ToList();

        if (stops.Count == 0)
        {
            stops = el.Elements(ns + "GradientStop")
                .Select(gs => (
                    color: gs.Attribute("Color")?.Value ?? "",
                    offset: gs.Attribute("Offset")?.Value ?? ""))
                .ToList();
        }

        return (startPoint, endPoint, stops);
    }

    private static (string? key, string? targetType,
        List<(string property, string value)> setters) GetStyle(
        XElement[] resources, string? key, string? targetType = null)
    {
        XElement el;
        if (key is not null)
        {
            el = FindResource(resources, key, "Style");
        }
        else
        {
            var matches = resources
                .Where(e => e.Name.LocalName == "Style")
                .Where(e => e.Attribute("TargetType")?.Value == targetType
                            || (targetType is null && GetKey(e) is null))
                .ToArray();

            el = targetType is not null
                ? matches.FirstOrDefault()
                  ?? throw new KeyNotFoundException($"Style with TargetType={targetType} not found")
                : matches.FirstOrDefault()
                  ?? throw new KeyNotFoundException("Default (implicit) Style not found");
        }

        var ns = el.GetDefaultNamespace();
        var setters = el.Elements(ns + "Style.Setters")
            .Elements(ns + "Setter")
            .Select(s => (
                property: s.Attribute("Property")?.Value ?? "",
                value: s.Attribute("Value")?.Value ?? ""))
            .Concat(el.Elements(ns + "Setter")
                .Select(s => (
                    property: s.Attribute("Property")?.Value ?? "",
                    value: s.Attribute("Value")?.Value ?? "")))
            .ToList();

        return (key, targetType, setters);
    }

    private static XElement FindResource(
        XElement[] resources, string key, string elementName)
    {
        var el = resources.FirstOrDefault(e =>
            e.Name.LocalName == elementName &&
            GetKey(e) == key);

        return el ?? throw new KeyNotFoundException(
            $"{elementName} with key '{key}' not found");
    }
}
