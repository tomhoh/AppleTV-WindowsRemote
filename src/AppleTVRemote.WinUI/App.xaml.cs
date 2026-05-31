// SPDX-License-Identifier: MIT
//
// WinUI 3 entry point.
//
// WinUI 3 has no equivalent of WPF's NotifyIcon, so we layer in:
//   - H.NotifyIcon.WinUI for the system-tray icon + context menu
//   - WinUIEx for window-management niceties on MainWindow
//
// The tray icon is held in code-behind here (rather than App.xaml) so
// the click + menu handlers can call back into App for popup toggling
// and clean shutdown.

using System;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace AppleTVRemote.WinUI;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow?  _popup;
    private readonly UISettings _uiSettings = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Apple TV Remote",
            NoLeftClickDelay = true,
            ContextMenuMode  = ContextMenuMode.PopupMenu,
            LeftClickCommand = new RelayCommand(_ => TogglePopup()),
        };
        // Without an icon set, H.NotifyIcon registers with the shell but
        // has nothing to draw — the process runs (visible in Task Manager)
        // but no tray icon appears.
        try
        {
            _trayIcon.Icon = TrayIconBuilder.Build();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrayIconBuilder failed: {ex}");
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        // Right-click context menu with Open + Exit. H.NotifyIcon takes a
        // standard MenuFlyout via ContextFlyout — same shape as any
        // WinUI control.
        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Click += (_, _) => TogglePopup();
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitApp();
        _trayIcon.ContextFlyout = new MenuFlyout
        {
            Items = { openItem, new MenuFlyoutSeparator(), exitItem },
        };

        _trayIcon.ForceCreate();
    }

    private void TogglePopup()
    {
        if (_popup is null)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.PositionNearTray();      // anchor to tray-icon cursor location
        _popup.Activate();
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _popup?.Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) { _execute = execute; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)   => _execute(parameter);
    }
}
