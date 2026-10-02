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
using System.Linq;
using AppleTVProtocol;
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
    private readonly CredentialStore _store = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Diagnostic log at %LOCALAPPDATA%\AppleTVRemote\session.log so
        // bug reports can include the on-wire trace. Cheap when nothing
        // hits the wire; rolled over at ~1 MB.
        AppleTVProtocol.CompanionTrace.Enable();
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Apple TV Remote",
            NoLeftClickDelay = true,
            ContextMenuMode  = ContextMenuMode.PopupMenu,
            LeftClickCommand = new RelayCommand(_ =>
            {
                // A context flyout lingering from a prior right-click
                // doesn't light-dismiss when the user left-clicks the
                // tray icon (H.NotifyIcon's hidden host owns both,
                // so Windows doesn't deactivate the flyout), so it
                // stays on screen until the user interacts with the
                // popup. Close it explicitly before toggling.
                DismissTrayFlyout();
                TogglePopup();
            }),
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

        // Right-click context menu — rebuilt every Opening so paired
        // devices and "Open at login" check state stay fresh.
        //
        // H.NotifyIcon shows the ContextFlyout in an isolated popup
        // window that doesn't always route MenuFlyoutItem.Click back to
        // the App's UI thread reliably — the Command pattern bypasses
        // that and runs the delegate directly. Same reason we wire
        // LeftClickCommand above instead of using a Click event.
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => RebuildTrayMenu(flyout);
        _trayIcon.ContextFlyout = flyout;
        RebuildTrayMenu(flyout);  // initial population

        _trayIcon.ForceCreate();
    }

    private void RebuildTrayMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        // Paired devices (quick-switch).
        StoredCredentials[] paired;
        try { paired = _store.LoadAll().ToArray(); }
        catch { paired = Array.Empty<StoredCredentials>(); }

        if (paired.Length > 0)
        {
            var activeId = _popup?.ActiveDeviceId;
            foreach (var dev in paired)
            {
                var isActive = activeId is not null && dev.DeviceId == activeId;
                var label = string.IsNullOrWhiteSpace(dev.DisplayName) ? "Apple TV" : dev.DisplayName;
                if (!string.IsNullOrWhiteSpace(dev.Host)) label += $"  ({dev.Host})";
                if (isActive) label += "  •  connected";
                var item = new MenuFlyoutItem { Text = label, IsEnabled = !isActive };
                var devId = dev.DeviceId;
                item.Command = new RelayCommand(_ => SwitchToDevice(devId));
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Command = new RelayCommand(_ =>
        {
            DismissTrayFlyout();
            TogglePopup();
        });
        flyout.Items.Add(openItem);

        // Pair-a-new-device moved off the popup into the tray menu —
        // keeps the popup layout lean and matches the tray-first
        // philosophy of the rest of the menu.
        var pairItem = new MenuFlyoutItem { Text = "Pair a new Apple TV…" };
        pairItem.Command = new RelayCommand(_ =>
        {
            DismissTrayFlyout();
            if (_popup is null)
            {
                _popup = new MainWindow();
                _popup.Closed += (_, _) => _popup = null;
            }
            _popup.OpenPairing();
        });
        flyout.Items.Add(pairItem);

        var startupItem = new ToggleMenuFlyoutItem
        {
            Text      = "Open at login",
            IsChecked = StartupRegistry.IsEnabled,
        };
        startupItem.Click += (_, _) =>
        {
            var exe = Environment.ProcessPath
                   ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(exe))
                StartupRegistry.Toggle(exe);
        };
        flyout.Items.Add(startupItem);

        flyout.Items.Add(new MenuFlyoutSeparator());
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Command = new RelayCommand(_ => ExitApp());
        flyout.Items.Add(exitItem);
    }

    private void SwitchToDevice(string deviceId)
    {
        DismissTrayFlyout();
        if (_popup is null)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.SelectStoredDevice(deviceId);
        _popup.PositionNearTray();
        _popup.Activate();
        // Fire pair-verify + session-init immediately so the user doesn't
        // need to press a remote button before the switch takes effect.
        // MainWindow.EnsureSessionAsync surfaces its own error dialogs.
        _ = _popup.ConnectSelectedAsync();
    }

    private void DismissTrayFlyout()
    {
        try
        {
            if (_trayIcon?.ContextFlyout is Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase fb)
                fb.Hide();
        }
        catch { /* best-effort — Hide can throw if the flyout is mid-close */ }
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
