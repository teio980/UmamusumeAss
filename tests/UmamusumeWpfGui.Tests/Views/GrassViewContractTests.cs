using System.IO;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class GrassViewContractTests
{
    private static string GrassViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "GrassView.xaml"));

    [Fact]
    public void GrassView_ContainsMaaInspiredThreeColumnStructure()
    {
        var content = File.ReadAllText(GrassViewPath);

        Assert.Contains("Width=\"1.05*\"", content);
        Assert.Contains("Width=\"1.15*\"", content);
        Assert.Contains("Width=\"1.2*\"", content);
        Assert.Contains("GrassTaskQueue", content);
        Assert.Contains("GrassSettings", content);
        Assert.Contains("GrassLogs", content);
        Assert.Contains("ScriptLogs", content);
        Assert.Contains("ScriptLogListBox", content);
        Assert.DoesNotContain("ItemsSource=\"{Binding Logs}\"", content);
        Assert.Contains("GrassAddTask", content);
        Assert.Contains("GrassGeneralSettings", content);
        Assert.Contains("GrassTodayHint", content);
    }

    [Fact]
    public void GrassView_UsesIndependentTaskSettingsTemplates()
    {
        var content = File.ReadAllText(GrassViewPath);

        Assert.Contains("SelectedTask.Settings", content);
        Assert.Contains("StartGameTaskSettingsViewModel", content);
        Assert.Contains("StartGameTaskSettingsView", content);
        Assert.Contains("Command=\"{Binding StartCommand}\"", content);
        Assert.Contains("Command=\"{Binding StopCommand}\"", content);
        Assert.Contains("CanStartQueue", content);
        Assert.Contains("CanStopQueue", content);
        Assert.DoesNotContain("Content=\"{DynamicResource GrassStartGame}\"", content);
    }
}
