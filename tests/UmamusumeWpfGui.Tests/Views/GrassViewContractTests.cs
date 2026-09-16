using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class GrassViewContractTests
{
    private static string GrassViewPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "UmamusumeWpfGui", "Views", "GrassView.xaml"));

    [Fact]
    public void GrassView_KeepsOnlyTheTaskQueueWithoutAnInlineLogColumn()
    {
        var content = File.ReadAllText(GrassViewPath);

        Assert.Contains("Width=\"*\"", content);
        var document = XDocument.Parse(content);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var workspace = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "HachimiWorkspace");
        var columns = workspace.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Count();
        Assert.Equal(1, columns);
        Assert.Contains("GrassTaskQueue", content);
        Assert.DoesNotContain("GrassLogs", content);
        Assert.DoesNotContain("ScriptLogListBox", content);
        Assert.DoesNotContain("LogColorConverter", content);
        Assert.DoesNotContain("LogViewModel", content);
        Assert.Contains("GrassAddTask", content);
        Assert.Contains("Value=\"0,2,10,2\"", content);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", content);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", content);
        Assert.Contains("HachimiTaskSettingsView", content);
        Assert.Contains("IsTaskSettingsPage", content);
        Assert.DoesNotContain("GrassSettingsMode", content);
        Assert.DoesNotContain("GrassGlobalSettings", content);
        Assert.DoesNotContain("HachimiShopSettingsViewModel", content);
        Assert.DoesNotContain("GrassTaskSettingsEmpty", content);
        Assert.DoesNotContain("Choose a task to configure", content);
        Assert.DoesNotContain("ShopTaskSettingsViewModel", content);
        Assert.DoesNotContain("GrassTodayHint", content);
        Assert.DoesNotContain("SelectedTaskDescription", content);
        Assert.DoesNotContain("GrassPlaceholderMessage", content);
    }

    [Fact]
    public void GrassView_UsesIndependentTaskSettingsTemplates()
    {
        var content = File.ReadAllText(GrassViewPath);

        var settingsView = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(GrassViewPath)!, "HachimiTaskSettingsView.xaml"));
        Assert.Contains("SelectedTask.Settings", settingsView);
        Assert.Contains("StartGameTaskSettingsViewModel", settingsView);
        Assert.Contains("StartGameTaskSettingsView", settingsView);
        Assert.Contains("Command=\"{Binding StartCommand}\"", content);
        Assert.Contains("Command=\"{Binding StopCommand}\"", content);
        Assert.Contains("CanStartQueue", content);
        Assert.Contains("CanStopQueue", content);
        Assert.DoesNotContain("Content=\"{DynamicResource GrassStartGame}\"", content);
    }

    [Fact]
    public void GrassView_ClickAndDragPathsRemainMutuallyExclusive()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(GrassViewPath)!, "GrassView.xaml.cs"));

        Assert.Contains("if (!_taskDragInProgress)", codeBehind);
        Assert.Contains("viewModel.OpenTaskSettings(sourceTask)", codeBehind);
        Assert.Contains("IsInteractiveTaskRowDragSource", codeBehind);
        Assert.Contains("ButtonBase", codeBehind);
        Assert.Contains("viewModel.MoveTask(sourceTask, targetIndex)", codeBehind);
    }
}
