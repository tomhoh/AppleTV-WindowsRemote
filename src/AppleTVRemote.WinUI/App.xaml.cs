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
using System.Runtime.InteropServices;
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
            ForceForeground(_popup);
        });
        flyout.Items.Add(pairItem);

        // "Manage pairings" submenu — Unpair per device + Clear all.
        // Kept secondary (nested under its own submenu) so the primary
        // tray flow (click device = quick-switch) stays simple.
        if (paired.Length > 0)
        {
            var manage = new MenuFlyoutSubItem { Text = "Manage pairings" };
            foreach (var dev in paired)
            {
                var name = string.IsNullOrWhiteSpace(dev.DisplayName) ? "Apple TV" : dev.DisplayName;
                var unpair = new MenuFlyoutItem { Text = $"Unpair {name}…" };
                var devId = dev.DeviceId;
                var devName = name;
                unpair.Command = new RelayCommand(_ => UnpairDevice(devId, devName));
                manage.Items.Add(unpair);
            }
            manage.Items.Add(new MenuFlyoutSeparator());
            var clearAll = new MenuFlyoutItem { Text = "Clear all pairings…" };
            clearAll.Command = new RelayCommand(_ => ClearAllPairings());
            manage.Items.Add(clearAll);
            flyout.Items.Add(manage);
        }

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

    private void UnpairDevice(string deviceId, string displayName)
    {
        DismissTrayFlyout();
        var confirm = MessageBoxW(IntPtr.Zero,
            $"Remove the pairing for \"{displayName}\" from this computer?\n\n" +
            "This deletes the stored credentials. You'll still need to remove\n" +
            "this computer from the Apple TV: Settings → Users and Accounts →\n" +
            "Allowed Devices.",
            "Unpair Apple TV", MB_OKCANCEL | MB_ICONWARNING);
        if (confirm != IDOK) return;

        // If this device is currently active, tear the session down
        // first so we don't keep sending frames to a credential that
        // no longer exists.
        if (_popup?.ActiveDeviceId == deviceId)
        {
            try { _popup.Close(); } catch { }
            _popup = null;
        }

        try { _store.Delete(deviceId); } catch { }

        MessageBoxW(IntPtr.Zero,
            "Pairing removed from this computer.\n\n" +
            "Open your Apple TV: Settings → Users and Accounts → Allowed Devices,\n" +
            "and delete the entry for this computer to finish cleanup.",
            "Unpair Apple TV", MB_OK | MB_ICONINFORMATION);
    }

    private void ClearAllPairings()
    {
        DismissTrayFlyout();
        var confirm = MessageBoxW(IntPtr.Zero,
            "Remove ALL stored Apple TV pairings from this computer?\n\n" +
            "This cannot be undone — you'll need to re-pair each Apple TV.\n" +
            "You should also remove this computer from each Apple TV:\n" +
            "Settings → Users and Accounts → Allowed Devices.",
            "Clear all pairings", MB_OKCANCEL | MB_ICONWARNING);
        if (confirm != IDOK) return;

        if (_popup is not null)
        {
            try { _popup.Close(); } catch { }
            _popup = null;
        }

        int removed = 0;
        try { removed = _store.DeleteAll(); } catch { }

        MessageBoxW(IntPtr.Zero,
            $"Cleared {removed} pairing(s) from this computer.\n\n" +
            "Open each Apple TV: Settings → Users and Accounts → Allowed Devices,\n" +
            "and remove any lingering entries for this computer.",
            "Clear all pairings", MB_OK | MB_ICONINFORMATION);
    }

    private const uint MB_OK = 0x0;
    private const uint MB_OKCANCEL = 0x1;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_ICONINFORMATION = 0x40;
    private const int  IDOK = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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
        ForceForeground(_popup);
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

    /// <summary>
    /// Force keyboard focus onto <paramref name="window"/> and bring it
    /// to the foreground. Window.Activate() alone isn't a reliable
    /// focus-steal on Win11 — in particular, when the user launches
    /// us from the shell's taskbar-overflow flyout (the "^" chevron),
    /// Activate() shows the window but leaves the shell's flyout as
    /// foreground, so the flyout stays on screen until the user clicks
    /// us. Combined workaround:
    ///   1. Synthesize an ALT keypress — Windows grants foreground
    ///      rights to a process that just received user input, so this
    ///      bypasses Win11's "focus stealing" protection that otherwise
    ///      silently blocks SetForegroundWindow from the shell flyout.
    ///   2. Attach our thread's input queue to the current foreground
    ///      thread's — classic workaround for the pre-Win11 case.
    ///   3. SetForegroundWindow + BringWindowToTop + ShowWindow restore.
    /// </summary>
    private static void ForceForeground(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;

            // Simulated ALT keypress — treated as user input, bypasses
            // Win11 focus-stealing protection. Down + up with no modifier
            // doesn't trigger menu bars on normal windows.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);

            uint fgTid = GetWindowThreadProcessId(fg, out _);
            uint myTid = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgTid != 0 && fgTid != myTid)
                    attached = AttachThreadInput(fgTid, myTid, true);
                ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(fgTid, myTid, false);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
        catch { /* best-effort */ }
    }

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private void TogglePopup()
    {
        if (_popup is null)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.PositionNearTray();      // anchor to tray-icon cursor location
        _popup.Activate();
        ForceForeground(_popup);
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
