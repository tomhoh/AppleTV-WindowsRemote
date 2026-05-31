// SPDX-License-Identifier: MIT
//
// WPF entry point + tray-icon glue.
//
// The app runs headless from a Windows perspective — there's no
// "always-visible" window. Instead a NotifyIcon (tray icon) hangs off
// Application.Resources, and clicking it shows/hides the MainWindow as
// a popup near the cursor.

using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace AppleTVRemote.Windows;

public partial class App : Application
{
    private MainWindow? _popup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // MainWindow is created lazily on first click so the tray icon
        // shows up immediately without paying the WPF window startup
        // cost upfront.
    }

    private void OnTrayLeftClicked(object sender, RoutedEventArgs e)  => TogglePopup();
    private void OnOpenClicked    (object sender, RoutedEventArgs e)  => TogglePopup();

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        var trayIcon = (TaskbarIcon?)Resources["TrayIcon"];
        trayIcon?.Dispose();
        Shutdown();
    }

    private void TogglePopup()
    {
        if (_popup is null || !_popup.IsLoaded)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
            _popup.PositionNearTray();      // anchor to cursor before first show
            _popup.Show();
            _popup.Activate();
        }
        else if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.PositionNearTray();      // re-anchor (cursor might be elsewhere now)
            _popup.Show();
            _popup.Activate();
        }
    }
}
