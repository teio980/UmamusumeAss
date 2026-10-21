using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            ContentHost.AddHandler(InputElement.PointerPressedEvent, OnContentPointerPressed,
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
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

    private void OnContentPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ContentHost).Properties.IsLeftButtonPressed ||
            e.GetPosition(ContentHost).Y >= 38 ||
            IsInsideFocusableControl(e.Source as Visual))
        {
            return;
        }

        OnTitleBarPointerPressed(sender, e);
    }

    private static bool IsInsideFocusableControl(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is InputElement { Focusable: true }) return true;
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
