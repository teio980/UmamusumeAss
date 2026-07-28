using System.IO;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests;

public sealed class AppBootstrapperTests
{
    private static readonly string AppXamlPath = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"..\..\..\..\..\src\UmamusumeWpfGui\App.xaml"));

    [Fact]
    public void AppXaml_UsesStyletApplicationLoaderWithBootstrapper()
    {
        var document = XDocument.Load(AppXamlPath);
        var loader = document.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "ApplicationLoader");

        Assert.NotNull(loader);
        Assert.Contains(loader!.Descendants(),
            element => element.Name.LocalName == "Bootstrapper");
    }
}
