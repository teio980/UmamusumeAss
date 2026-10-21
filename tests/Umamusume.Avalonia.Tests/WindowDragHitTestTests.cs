using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Umamusume.Avalonia.Views;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public class WindowDragHitTestTests
{
    [Fact]
    public void InteractiveControlsAreIdentifiedCorrectly()
    {
        var btn = new Button();
        var txt = new TextBlock { Text = "Click me" };
        btn.Content = txt;
        Assert.True(MainWindow.IsInsideInteractiveControl(btn));
        Assert.True(MainWindow.IsInsideInteractiveControl(txt));

        Assert.True(MainWindow.IsInsideInteractiveControl(new TextBox()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new ComboBox()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new CheckBox()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new ToggleSwitch()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new Slider()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new ListBox()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new ListBoxItem()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new GridSplitter()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new NumericUpDown()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new NavigationViewItem()));
        Assert.True(MainWindow.IsInsideInteractiveControl(new MenuItem()));
    }

    [Fact]
    public void NonInteractiveControlsAreIdentifiedCorrectly()
    {
        var grid = new Grid();
        var sp = new StackPanel();
        var border = new Border();
        var text = new TextBlock { Text = "Hachimi~" };
        var uc = new UserControl();

        grid.Children.Add(sp);
        sp.Children.Add(border);
        border.Child = text;
        uc.Content = grid;

        Assert.False(MainWindow.IsInsideInteractiveControl(text));
        Assert.False(MainWindow.IsInsideInteractiveControl(border));
        Assert.False(MainWindow.IsInsideInteractiveControl(sp));
        Assert.False(MainWindow.IsInsideInteractiveControl(grid));
        Assert.False(MainWindow.IsInsideInteractiveControl(uc));
    }
}
