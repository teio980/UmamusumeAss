using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Theme;

/// <summary>
/// Parses theme XAML files as XML to verify resource definitions.
/// Avoids WPF XamlReader StaticResource resolution issues at parse time.
/// </summary>
public sealed class ThemeResourceTests
{
    private static readonly XNamespace XamlNs =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ResDir => Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"..\..\..\..\..\src\UmamusumeWpfGui\Res"));

    private static string ThemesDir => Path.Combine(ResDir, "Themes");
    private static string StylesDir => Path.Combine(ResDir, "Styles");

    private static XDocument LoadXaml(string path)
    {
        Assert.True(File.Exists(path), $"File not found: {path}");
        return XDocument.Load(path);
    }

    /// <summary>
    /// Returns all top-level ResourceDictionary entries from a XAML file.
    /// </summary>
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

    // ================================================================
    // Light.xaml — palette brushes
    // ================================================================

    [Fact]
    public void LightXamlContainsPrimaryBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "PrimaryBrush");
        Assert.Equal("#E91E8C", brush.color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsWindowBackgroundBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "WindowBackgroundBrush");
        Assert.Equal("#FFF0F5", brush.color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsErrorBrush()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var brush = GetSolidColorBrush(resources, "ErrorBrush");
        Assert.Equal("#F44336", brush.color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsWindowBackgroundGradient()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var gradient = GetGradientBrush(resources, "WindowBackgroundGradient");

        Assert.Equal("0,0", gradient.startPoint);
        Assert.Equal("0,1", gradient.endPoint);
        Assert.Equal(3, gradient.stops.Count);

        Assert.Equal("#FFE4EC", gradient.stops[0].color, ignoreCase: true);
        Assert.Equal("#FFF0F5", gradient.stops[1].color, ignoreCase: true);
        Assert.Equal("#FFFFFF", gradient.stops[2].color, ignoreCase: true);
    }

    [Fact]
    public void LightXamlContainsAllPaletteBrushes()
    {
        var resources = GetResources(Path.Combine(ThemesDir, "Light.xaml"));
        var keys = resources
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(GetKey)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();

        var expected = new[]
        {
            "PrimaryBrush",
            "PrimaryLightBrush",
            "PrimaryLighterBrush",
            "PrimaryLightestBrush",
            "GoldBrush",
            "WindowBackgroundBrush",
            "CardBackgroundBrush",
            "TextPrimaryBrush",
            "TextSecondaryBrush",
            "TextOnPrimaryBrush",
            "DividerBrush",
            "SuccessBrush",
            "ErrorBrush",
            "WindowBackgroundGradient",
        };

        foreach (var key in expected)
            Assert.Contains(key, keys);
    }

    // ================================================================
    // Button.xaml — PrimaryButtonGradient and CornerRadius
    // ================================================================

    [Fact]
    public void ButtonXamlContainsPrimaryButtonGradient()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var gradient = GetGradientBrush(resources, "PrimaryButtonGradient");

        Assert.Equal("0,0", gradient.startPoint);
        Assert.Equal("0,1", gradient.endPoint);
        Assert.Equal(2, gradient.stops.Count);

        Assert.Equal("#FFE4EC", gradient.stops[0].color, ignoreCase: true);
        Assert.Equal("0.0", gradient.stops[0].offset);
        Assert.Equal("#E91E8C", gradient.stops[1].color, ignoreCase: true);
        Assert.Equal("1.0", gradient.stops[1].offset);
    }

    [Fact]
    public void ButtonXamlPrimaryButtonStyleHasGradientBackground()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "PrimaryButtonStyle");

        var bgSetter = style.setters.FirstOrDefault(s => s.property == "Background");
        Assert.NotEqual(default, bgSetter);
        Assert.Contains("PrimaryButtonGradient", bgSetter.value);
    }

    [Fact]
    public void ButtonXamlPrimaryButtonStyleHasCornerRadius6()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "PrimaryButtonStyle");

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("6", crSetter.value);
    }

    [Fact]
    public void ButtonXamlSecondaryButtonStyleHasCornerRadius6()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, "SecondaryButtonStyle");

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("6", crSetter.value);
    }

    [Fact]
    public void ButtonXamlDefaultButtonStyleHasCornerRadius6()
    {
        var resources = GetResources(Path.Combine(StylesDir, "Button.xaml"));
        var style = GetStyle(resources, null); // default (TargetType) style

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("6", crSetter.value);
    }

    // ================================================================
    // TextBox.xaml — CornerRadius = 4
    // ================================================================

    [Fact]
    public void TextBoxXamlHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "TextBox.xaml"));
        var style = GetStyle(resources, null); // implicit (TargetType) style

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }

    // ================================================================
    // ComboBox.xaml — CornerRadius = 4
    // ================================================================

    [Fact]
    public void ComboBoxXamlHasCornerRadius4()
    {
        var resources = GetResources(Path.Combine(StylesDir, "ComboBox.xaml"));
        var style = GetStyle(resources, null); // implicit (TargetType) style

        var crSetter = style.setters.FirstOrDefault(
            s => s.property.Contains("CornerRadius"));
        Assert.NotEqual(default, crSetter);
        Assert.Equal("4", crSetter.value);
    }

    // ================================================================
    // Style.xaml — Border CornerRadius = 8
    // ================================================================

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

    // ================================================================
    // XML parsing helpers
    // ================================================================

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