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

        // Right-click context menu with Open + Exit.
        //
        // H.NotifyIcon shows the ContextFlyout in an isolated popup
        // window that doesn't always route MenuFlyoutItem.Click back to
        // the App's UI thread reliably — the Command pattern bypasses
        // that and runs the delegate directly. Same reason we wire
        // LeftClickCommand below instead of using a Click event.
        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Command = new RelayCommand(_ => TogglePopup());
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Command = new RelayCommand(_ => ExitApp());
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
        // Application.Current.Exit() needs the message loop on the main
        // window thread to process its quit message — flaky in tray-only
        // apps where the only "window" is a hidden popup. Tear down
        // explicitly and fall back to Environment.Exit so the process
        // actually goes away when the user clicks Exit.
        try { _trayIcon?.Dispose(); } catch { }
        _trayIcon = null;
        try { _popup?.Close(); } catch { }
        try { Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
        Environment.Exit(0);
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
