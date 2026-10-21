using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views;

public partial class MainWindow : AppWindow
{
    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome |
                                          ExtendClientAreaChromeHints.OSXThickTitleBar;
            ExtendClientAreaTitleBarHeightHint = -1;
            AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        Opened += (_, _) => RemoveMacTitleBarSeparator();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;

        Loaded += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                UpdateSettingsLabel(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SettingsNavigationLabel)) UpdateSettingsLabel(vm);
                };
            }
            if (NavView.MenuItems is System.Collections.IList { Count: > 0 } list)
            {
                NavView.SelectedItem = list[0];
            }
        };
    }

    private void UpdateSettingsLabel(MainWindowViewModel vm)
    {
        if (NavView.SettingsItem is NavigationViewItem item) item.Content = vm.SettingsNavigationLabel;
    }

    private void RemoveMacTitleBarSeparator()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
            return;

        MacWindowChrome.RemoveTitleBarSeparator(handle.Handle);
    }

    private static class MacWindowChrome
    {
        private const string ObjectiveCRuntime = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjectiveCRuntime, EntryPoint = "sel_registerName")]
        private static extern IntPtr RegisterSelectorNative(IntPtr selectorName);

        [DllImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
        private static extern byte SendBoolReturningMessage(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
        private static extern void SendIntegerMessage(IntPtr receiver, IntPtr selector, nint value);

        private static IntPtr RegisterSelector(string selectorName)
        {
            var utf8Name = Marshal.StringToCoTaskMemUTF8(selectorName);
            try
            {
                return RegisterSelectorNative(utf8Name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8Name);
            }
        }

        public static void RemoveTitleBarSeparator(IntPtr window)
        {
            var respondsToSelector = RegisterSelector("respondsToSelector:");
            var setSeparatorStyle = RegisterSelector("setTitlebarSeparatorStyle:");

            if (SendBoolReturningMessage(window, respondsToSelector, setSeparatorStyle) != 0)
                SendIntegerMessage(window, setSeparatorStyle, 1); // NSTitlebarSeparatorStyleNone
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var paneWidth = NavView.IsPaneOpen ? NavView.OpenPaneLength : NavView.CompactPaneLength;

        // In the workspace content area: only allow dragging in the top titlebar / header area (<= 75 DIPs)
        if (pos.X >= paneWidth && (pos.Y < 0 || pos.Y > 75))
        {
            return;
        }

        // Do not drag if clicking inside an interactive control (button, textbox, combobox, etc.)
        if (IsInsideInteractiveControl(e.Source as Visual))
        {
            return;
        }

        OnTitleBarPointerPressed(sender, e);
    }

    internal static bool IsInsideInteractiveControl(Visual? source)
    {
        for (var visual = source; visual is not null && visual is not MainWindow; visual = visual.GetVisualParent() ?? (visual as ILogical)?.LogicalParent as Visual)
        {
            if (visual is Button
                or TextBox
                or ComboBox
                or CheckBox
                or ToggleSwitch
                or Slider
                or ListBox
                or ListBoxItem
                or TabItem
                or ScrollBar
                or GridSplitter
                or NumericUpDown
                or NavigationViewItem
                or MenuItem)
            {
                return true;
            }

            if (visual is InputElement { Focusable: true }
                and not (Window or AppWindow or UserControl or ContentControl or Panel or Border))
            {
                return true;
            }
        }

        return false;
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.IsSettingsSelected)
        {
            vm.Navigate("Settings");
        }
        else if (e.SelectedItemContainer is NavigationViewItem nvi && nvi.Tag is string tag)
        {
            vm.Navigate(tag);
        }
    }
}
