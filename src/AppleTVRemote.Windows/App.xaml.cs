// SPDX-License-Identifier: MIT
//
// WPF entry point + tray-icon glue.
//
// The app runs headless from a Windows perspective — there's no
// "always-visible" window. Instead a NotifyIcon (tray icon) hangs off
// Application.Resources, and clicking it shows/hides the MainWindow as
// a popup near the cursor.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AppleTVProtocol;
using Hardcodet.Wpf.TaskbarNotification;

namespace AppleTVRemote.Windows;

public partial class App : Application
{
    private MainWindow? _popup;
    private readonly CredentialStore _store = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Diagnostic log at %LOCALAPPDATA%\AppleTVRemote\session.log so
        // bug reports can include the on-wire trace. Cheap when nothing
        // hits the wire; rolled over at ~1 MB.
        CompanionTrace.Enable();
        // Give the TaskbarIcon a real glyph. Without an Icon /
        // IconSource, Hardcodet's NotifyIcon silently registers itself
        // with the shell but has nothing to draw — the process runs
        // (visible in Task Manager) but no tray icon appears.
        if (Resources["TrayIcon"] is TaskbarIcon tray)
        {
            try
            {
                tray.Icon = TrayIconBuilder.Build();
            }
            catch (Exception ex)
            {
                // Last-ditch fallback: use the generic application icon.
                System.Diagnostics.Debug.WriteLine($"TrayIconBuilder failed: {ex}");
                tray.Icon = System.Drawing.SystemIcons.Application;
            }
        }
        // MainWindow is created lazily on first click so the tray icon
        // shows up immediately without paying the WPF window startup
        // cost upfront.
    }

    private void OnTrayLeftClicked(object sender, RoutedEventArgs e)
    {
        // A context menu lingering from a prior right-click doesn't
        // light-dismiss when the user left-clicks the tray icon
        // (Hardcodet.NotifyIcon treats the icon area as part of its
        // own popup chrome), so it stays on screen until the user
        // interacts with the newly-opened popup. Close it explicitly.
        DismissTrayMenu();
        TogglePopup();
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        DismissTrayMenu();
        TogglePopup();
    }

    private void DismissTrayMenu()
    {
        if (Resources["TrayIcon"] is TaskbarIcon tray &&
            tray.ContextMenu is { IsOpen: true } cm)
        {
            cm.IsOpen = false;
        }
    }

    /// <summary>
    /// Rebuilt every time the user right-clicks the tray icon so the
    /// paired-device list and "Open at login" check state are always
    /// fresh. Hardcodet's TaskbarIcon raises the underlying ContextMenu
    /// Opened event whenever the menu is about to display.
    /// </summary>
    private void OnTrayMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();

        // ── Paired devices (quick-switch). Each row makes the popup
        //    select that device on next open, so a user with multiple
        //    Apple TVs can flip between them without opening the popup.
        StoredCredentials[] paired;
        try { paired = _store.LoadAll().ToArray(); }
        catch { paired = Array.Empty<StoredCredentials>(); }

        if (paired.Length > 0)
        {
            var activeId = _popup?.ActiveDeviceId;
            foreach (var dev in paired)
            {
                var isActive = activeId is not null && dev.DeviceId == activeId;
                var baseName = string.IsNullOrWhiteSpace(dev.DisplayName) ? "Apple TV" : dev.DisplayName;
                var item = new MenuItem
                {
                    Header           = isActive ? baseName + "  •  connected" : baseName,
                    InputGestureText = string.IsNullOrWhiteSpace(dev.Host) ? "" : dev.Host,
                    Tag              = dev,
                    IsChecked        = isActive,
                    IsEnabled        = !isActive,
                };
                item.Click += OnDeviceQuickSwitchClicked;
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "Open", Command = null }.WithClick(OnOpenClicked));
        // Pair-a-new-device belongs in the tray menu for parity with
        // the WinUI variant — matches the "no chrome on the popup"
        // direction. Reuses MainWindow's existing pair flow.
        menu.Items.Add(new MenuItem { Header = "Pair a new Apple TV…" }.WithClick(OnPairNewClicked));

        // "Manage pairings" submenu — Unpair per device + Clear all.
        // Kept secondary (nested) so the primary tray flow (click device
        // = quick-switch) stays simple.
        if (paired.Length > 0)
        {
            var manage = new MenuItem { Header = "Manage pairings" };
            foreach (var dev in paired)
            {
                var name = string.IsNullOrWhiteSpace(dev.DisplayName) ? "Apple TV" : dev.DisplayName;
                var unpair = new MenuItem { Header = $"Unpair {name}…", Tag = dev };
                unpair.Click += OnUnpairDeviceClicked;
                manage.Items.Add(unpair);
            }
            manage.Items.Add(new Separator());
            var clearAll = new MenuItem { Header = "Clear all pairings…" };
            clearAll.Click += OnClearAllPairingsClicked;
            manage.Items.Add(clearAll);
            menu.Items.Add(manage);
        }

        var startup = new MenuItem
        {
            Header      = "Open at login",
            IsCheckable = true,
            IsChecked   = StartupRegistry.IsEnabled,
        };
        startup.Click += OnToggleStartupClicked;
        menu.Items.Add(startup);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit" }.WithClick(OnExitClicked));
    }

    private void OnPairNewClicked(object sender, RoutedEventArgs e)
    {
        DismissTrayMenu();
        EnsurePopup();
        _popup!.PositionNearTray();
        _popup.Show();
        _popup.Activate();
        ForceForeground(_popup);
        _popup.OpenPairing();
    }

    private void OnDeviceQuickSwitchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not StoredCredentials dev) return;
        // Surface the popup pointing at the chosen device and immediately
        // fire the pair-verify + session-init handshake so the user
        // doesn't have to press a remote button just to switch devices.
        // Fire-and-forget: MainWindow.EnsureSessionAsync surfaces its own
        // error dialogs.
        DismissTrayMenu();
        EnsurePopup();
        _popup!.SelectStoredDevice(dev.DeviceId);
        _popup.PositionNearTray();
        _popup.Show();
        _popup.Activate();
        ForceForeground(_popup);
        _ = _popup.ConnectSelectedAsync();
    }

    private void OnUnpairDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not StoredCredentials dev) return;
        DismissTrayMenu();
        var displayName = string.IsNullOrWhiteSpace(dev.DisplayName) ? "Apple TV" : dev.DisplayName;
        var confirm = MessageBox.Show(
            $"Remove the pairing for \"{displayName}\" from this computer?\n\n" +
            "This deletes the stored credentials. You'll still need to remove " +
            "this computer from the Apple TV: Settings → Users and Accounts → " +
            "Allowed Devices.",
            "Unpair Apple TV",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        // If this device is currently active, close the popup first so
        // we don't keep sending frames to a credential that no longer
        // exists.
        if (_popup?.ActiveDeviceId == dev.DeviceId)
        {
            try { _popup.Close(); } catch { }
            _popup = null;
        }

        try { _store.Delete(dev.DeviceId); } catch { }

        MessageBox.Show(
            "Pairing removed from this computer.\n\n" +
            "Open your Apple TV: Settings → Users and Accounts → Allowed Devices, " +
            "and delete the entry for this computer to finish cleanup.",
            "Unpair Apple TV",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClearAllPairingsClicked(object sender, RoutedEventArgs e)
    {
        DismissTrayMenu();
        var confirm = MessageBox.Show(
            "Remove ALL stored Apple TV pairings from this computer?\n\n" +
            "This cannot be undone — you'll need to re-pair each Apple TV. " +
            "You should also remove this computer from each Apple TV: " +
            "Settings → Users and Accounts → Allowed Devices.",
            "Clear all pairings",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (_popup is not null)
        {
            try { _popup.Close(); } catch { }
            _popup = null;
        }

        int removed = 0;
        try { removed = _store.DeleteAll(); } catch { }

        MessageBox.Show(
            $"Cleared {removed} pairing(s) from this computer.\n\n" +
            "Open each Apple TV: Settings → Users and Accounts → Allowed Devices, " +
            "and remove any lingering entries for this computer.",
            "Clear all pairings",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnToggleStartupClicked(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath
               ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(exe)) return;
        StartupRegistry.Toggle(exe);
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        var trayIcon = (TaskbarIcon?)Resources["TrayIcon"];
        trayIcon?.Dispose();
        Shutdown();
    }

    private void EnsurePopup()
    {
        if (_popup is null || !_popup.IsLoaded)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
        }
    }

    private void TogglePopup()
    {
        EnsurePopup();
        if (_popup!.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.PositionNearTray();
            _popup.Show();
            _popup.Activate();
            ForceForeground(_popup);
        }
    }

    /// <summary>
    /// Force keyboard focus onto <paramref name="window"/> and bring
    /// it to the foreground. Window.Activate() alone isn't a reliable
    /// focus-steal on Win11 — in particular, when the user launches
    /// us from the shell's taskbar-overflow flyout (the "^" chevron),
    /// Activate() shows the window but leaves the shell's flyout as
    /// foreground, so the flyout stays on screen until the user
    /// clicks us. Combined workaround: synthesize an ALT keypress
    /// (counts as user input, bypasses Win11 focus-stealing
    /// protection), attach our thread's input queue to the foreground
    /// thread's, then BringWindowToTop + SetForegroundWindow.
    /// </summary>
    private static void ForceForeground(System.Windows.Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;

            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero); // ALT down

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
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // ALT up
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
}

internal static class MenuItemExtensions
{
    public static MenuItem WithClick(this MenuItem item, RoutedEventHandler handler)
    {
        item.Click += handler;
        return item;
    }
}
