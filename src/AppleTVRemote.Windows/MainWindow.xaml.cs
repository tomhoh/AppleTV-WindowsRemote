// SPDX-License-Identifier: MIT
//
// Mac-style Apple-TV remote popup (WPF).
//
// All theme-sensitive brushes are seeded as light-mode defaults in XAML
// and re-applied at runtime by ApplyTheme() — wired to live
// SystemEvents.UserPreferenceChanged so toggling Windows light/dark
// updates the popup instantly without a restart.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppleTVProtocol;
using Microsoft.Win32;

namespace AppleTVRemote.Windows;

public partial class MainWindow : Window
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;

    // Clickpad geometry: must match the XAML.
    private const double ClickpadDiameter      = 130;
    private const double ClickpadInnerDiameter = 72;
    private const double SwipeThresholdPx      = 24;
    private const double TapMaxTotalPx         = 8;

    private bool _dragActive;
    private Point _dragBaseline;
    private double _totalDragDistance;
    private HidCommand? _hoveredRegion;

    public MainWindow()
    {
        InitializeComponent();

        // Apply theme BEFORE Loaded so the very first paint is correct,
        // not a flash of the light defaults.
        ApplyTheme();

        Loaded += (_, _) =>
        {
            ApplyTheme();   // re-apply after the visual tree is realised
            RefreshDevices();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        };
        Activated += (_, _) => ApplyTheme();   // re-check whenever the popup re-opens
        Closed += async (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            await TeardownActiveAsync();
        };
    }

    // ─── Tray-anchored popup behaviour ─────────────────────────────

    public void PositionNearTray() => TrayWindowPositioner.PositionNearCursor(this);

    /// <summary>Click-outside dismissal: same UX as a macOS menu-bar popover.</summary>
    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    // ─── Theme adaptation ──────────────────────────────────────────

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // The General category fires when Apps light/dark mode flips.
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.Invoke(ApplyTheme);
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Prefer AppsUseLightTheme (per-app); fall back to
            // SystemUsesLightTheme (taskbar) if the per-app key is missing.
            if (key?.GetValue("AppsUseLightTheme")   is int a) return a == 0;
            if (key?.GetValue("SystemUsesLightTheme") is int s) return s == 0;
        }
        catch { /* fall through */ }
        return false;
    }

    private void ApplyTheme()
    {
        if (IsSystemDark())
            ApplyDarkBrushes();
        else
            ApplyLightBrushes();
    }

    /// <summary>
    /// Light-mode palette. Buttons use semi-transparent BLACK over the
    /// aluminum gradient — that's where the readable contrast comes
    /// from. White-on-aluminum reads too washed out.
    /// </summary>
    private void ApplyLightBrushes()
    {
        Resources["BodyGradient"]      = MakeVerticalGradient("#D6D6D6", "#A8A8A8");
        Resources["BodyHighlight"]     = MakeTopHighlight("#59FFFFFF", "#00FFFFFF");

        Resources["ButtonMaterial"]    = Solid("#33000000");
        Resources["ButtonMaterialHv"]  = Solid("#52000000");
        Resources["ButtonMaterialDn"]  = Solid("#72000000");
        Resources["ButtonGlyph"]       = Solid("#E6000000");
        Resources["ButtonStroke"]      = Solid("#40000000");

        Resources["ClickpadRing"]      = Solid("#26000000");
        Resources["ClickpadHover"]     = Solid("#1E000000");
        Resources["ClickpadPressed"]   = Solid("#33000000");
        Resources["SelectFill"]        = Solid("#80FFFFFF");
        Resources["SelectHoverFill"]   = Solid("#AAFFFFFF");
    }

    /// <summary>
    /// Dark-mode palette. Body goes deep aluminum; button materials
    /// flip to semi-transparent WHITE so they read against the dark.
    /// </summary>
    private void ApplyDarkBrushes()
    {
        Resources["BodyGradient"]      = MakeVerticalGradient("#525252", "#2E2E2E");
        Resources["BodyHighlight"]     = MakeTopHighlight("#1AFFFFFF", "#00FFFFFF");

        Resources["ButtonMaterial"]    = Solid("#26FFFFFF");
        Resources["ButtonMaterialHv"]  = Solid("#40FFFFFF");
        Resources["ButtonMaterialDn"]  = Solid("#5CFFFFFF");
        Resources["ButtonGlyph"]       = Solid("#E6FFFFFF");
        Resources["ButtonStroke"]      = Solid("#33FFFFFF");

        Resources["ClickpadRing"]      = Solid("#1AFFFFFF");
        Resources["ClickpadHover"]     = Solid("#1AFFFFFF");
        Resources["ClickpadPressed"]   = Solid("#33FFFFFF");
        Resources["SelectFill"]        = Solid("#66FFFFFF");
        Resources["SelectHoverFill"]   = Solid("#80FFFFFF");
    }

    private static SolidColorBrush Solid(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush MakeVerticalGradient(string topHex, string bottomHex)
    {
        var b = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(topHex)!,
            (Color)ColorConverter.ConvertFromString(bottomHex)!,
            new Point(0, 0), new Point(0, 1));
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush MakeTopHighlight(string topHex, string fadeHex)
    {
        var b = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString(topHex)!,   0.0),
            new GradientStop((Color)ColorConverter.ConvertFromString(fadeHex)!, 1.0),
        }, new Point(0, 0), new Point(0, 0.5));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Public hook for the tray-icon quick-switch menu: refresh the
    /// stored-device list and pre-select the entry with this DeviceId.
    /// Called from App.OnDeviceQuickSwitchClicked so the popup opens
    /// already pointing at the device the user picked from the tray.
    /// </summary>
    public void SelectStoredDevice(string deviceId)
    {
        RefreshDevices();
        foreach (var item in DeviceCombo.Items)
        {
            if (item is StoredCredentials sc && sc.DeviceId == deviceId)
            {
                DeviceCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void RefreshDevices()
    {
        DeviceCombo.ItemsSource = null;
        var creds = _store.LoadAll();
        DeviceCombo.ItemsSource = creds;
        DeviceCombo.DisplayMemberPath = nameof(StoredCredentials.DisplayName);
        if (creds.Count > 0)
        {
            DeviceCombo.SelectedIndex = 0;
            DeviceCombo.Visibility = Visibility.Visible;
        }
    }

    // ─── Clickpad gestures ─────────────────────────────────────────

    private void OnClickpadDown(object sender, MouseButtonEventArgs e)
    {
        _dragActive = true;
        _dragBaseline = e.GetPosition(Clickpad);
        _totalDragDistance = 0;
        Clickpad.CaptureMouse();
        // Belt-and-suspenders: if anything (focus theft, another control
        // grabbing capture, the window losing activation) yanks our
        // mouse capture, clear the drag state so the next hover doesn't
        // dispatch.
        Clickpad.LostMouseCapture -= OnClickpadLostCapture;
        Clickpad.LostMouseCapture += OnClickpadLostCapture;
    }

    private void OnClickpadLostCapture(object sender, MouseEventArgs e)
    {
        _dragActive = false;
        _hoveredRegion = null;
        ApplyHoverRegion(null);
    }

    private void OnClickpadMove(object sender, MouseEventArgs e)
    {
        // Defensive: if we lost the up event (mouse left the window
        // mid-drag, focus was stolen, capture was broken by another
        // control, …), _dragActive can linger. Any subsequent hover
        // would then enter the drag branch below, dispatch HID
        // commands, and flash quadrants based on a stale baseline —
        // exactly the symptom 'hover fires controls / highlight
        // doesn't follow the pointer'. Clear the flag whenever the
        // button isn't actually pressed.
        if (_dragActive && e.LeftButton != MouseButtonState.Pressed)
        {
            _dragActive = false;
            if (Clickpad.IsMouseCaptured) Clickpad.ReleaseMouseCapture();
        }

        var p = e.GetPosition(Clickpad);

        if (_dragActive)
        {
            var dx = p.X - _dragBaseline.X;
            var dy = p.Y - _dragBaseline.Y;
            _totalDragDistance = Math.Max(_totalDragDistance, Math.Sqrt(dx * dx + dy * dy));
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeThresholdPx) return;

            HidCommand cmd = (Math.Abs(dx) >= Math.Abs(dy))
                ? (dx > 0 ? HidCommand.Right : HidCommand.Left)
                : (dy > 0 ? HidCommand.Down  : HidCommand.Up);
            _ = DispatchAsync(cmd);
            FlashQuadrant(cmd);

            _dragBaseline = p;
            return;
        }

        // Not dragging — update hover highlight.
        var region = HitRegion(p);
        if (region != _hoveredRegion)
        {
            _hoveredRegion = region;
            ApplyHoverRegion(region);
        }
    }

    private void OnClickpadLeave(object sender, MouseEventArgs e)
    {
        if (_dragActive) return;
        _hoveredRegion = null;
        ApplyHoverRegion(null);
    }

    private void OnClickpadUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive) return;
        Clickpad.ReleaseMouseCapture();
        _dragActive = false;

        if (_totalDragDistance < TapMaxTotalPx)
        {
            var p = e.GetPosition(Clickpad);
            var cmd = HitRegion(p);
            _ = DispatchAsync(cmd);
            FlashQuadrant(cmd);
        }
    }

    private void ApplyHoverRegion(HidCommand? region)
    {
        // Center select disc: swap fill on hover for the soft highlight.
        SelectDisc.Fill = (region == HidCommand.Select)
            ? (Brush)Resources["SelectHoverFill"]
            : (Brush)Resources["SelectFill"];

        // Outer ring: draw a wedge for the hovered quadrant.
        if (region is null or HidCommand.Select)
        {
            HoverQuadrant.Visibility = Visibility.Collapsed;
            return;
        }
        var geo = new PathGeometry { Figures = { BuildQuadrantWedge(region.Value) } };
        HoverQuadrant.Data = geo;
        HoverQuadrant.Opacity = 1.0;
        HoverQuadrant.BeginAnimation(UIElement.OpacityProperty, null);   // cancel any fade
        HoverQuadrant.Visibility = Visibility.Visible;
    }

    private static HidCommand HitRegion(Point p)
    {
        double cx = ClickpadDiameter / 2, cy = ClickpadDiameter / 2;
        double dx = p.X - cx, dy = p.Y - cy;
        double d  = Math.Sqrt(dx * dx + dy * dy);
        if (d <= ClickpadInnerDiameter / 2) return HidCommand.Select;
        double angle = Math.Atan2(dy, dx);
        if (angle >= -Math.PI / 4 && angle <= Math.PI / 4)         return HidCommand.Right;
        if (angle >  Math.PI / 4  && angle <= 3 * Math.PI / 4)     return HidCommand.Down;
        if (angle >= -3 * Math.PI / 4 && angle < -Math.PI / 4)     return HidCommand.Up;
        return HidCommand.Left;
    }

    private void FlashQuadrant(HidCommand cmd)
    {
        if (cmd == HidCommand.Select)
        {
            var scale = (ScaleTransform)SelectDisc.RenderTransform;
            var anim = new DoubleAnimation
            {
                From = 0.94, To = 1.0, Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase(),
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            return;
        }

        var geo = new PathGeometry { Figures = { BuildQuadrantWedge(cmd) } };
        QuadrantHighlight.Data = geo;
        QuadrantHighlight.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation
        {
            From = 1.0, To = 0.0, Duration = TimeSpan.FromMilliseconds(220),
        };
        fade.Completed += (_, _) => QuadrantHighlight.Visibility = Visibility.Collapsed;
        QuadrantHighlight.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static PathFigure BuildQuadrantWedge(HidCommand cmd)
    {
        // Ring-shaped wedge: covers the OUTER quadrant of the clickpad
        // only, not the area under the center select button. Walks
        //   outer-start → outer-end (clockwise arc on the outer circle)
        //   outer-end   → inner-end (radial line)
        //   inner-end   → inner-start (counter-clockwise arc on the inner)
        //   inner-start → outer-start (radial line, closed)
        double rOuter = ClickpadDiameter      / 2;
        double rInner = ClickpadInnerDiameter / 2;
        var center = new Point(rOuter, rOuter);

        double startDeg = cmd switch
        {
            HidCommand.Right => -45,
            HidCommand.Down  =>  45,
            HidCommand.Left  => 135,
            HidCommand.Up    => -135,
            _ => 0,
        };
        double endDeg = startDeg + 90;
        Point Polar(double r, double deg)
        {
            var rad = deg * Math.PI / 180.0;
            return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        }

        return new PathFigure
        {
            StartPoint = Polar(rOuter, startDeg),
            Segments =
            {
                new ArcSegment(Polar(rOuter, endDeg),   new Size(rOuter, rOuter), 0, false, SweepDirection.Clockwise,        true),
                new LineSegment(Polar(rInner, endDeg),  true),
                new ArcSegment(Polar(rInner, startDeg), new Size(rInner, rInner), 0, false, SweepDirection.Counterclockwise, true),
                new LineSegment(Polar(rOuter, startDeg), true),
            },
            IsClosed = true,
        };
    }

    // ─── HID handlers ──────────────────────────────────────────────

    private async void OnHidClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            MessageBox.Show($"unknown HID command: {tag}");
            return;
        }
        await DispatchAsync(cmd);
    }

    private async void OnPowerClicked(object sender, RoutedEventArgs e)
        => await DispatchAsync(HidCommand.Wake);

    // Home: send HID down on press, up on release — that way the TV sees the
    // user's actual hold duration. A short tap goes to the home screen; a
    // hold of ~0.7 s+ pops the tvOS app-switcher. PressAsync would send down
    // + up back-to-back and we'd lose the hold-to-app-switcher gesture.
    private async void OnHomeDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var session = await EnsureSessionAsync();
            if (session is null) return;
            await session.SendHidAsync(HidCommand.Home, down: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Home failed: {ex.Message}", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await TeardownActiveAsync();
        }
    }

    private async void OnHomeUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_session is null) return;   // down failed already; nothing to release
            await _session.SendHidAsync(HidCommand.Home, down: false);
        }
        catch { /* swallow release errors; the next button press will rebuild */ }
    }

    private async void OnVolumeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        var p = e.GetPosition(b);
        var cmd = (p.Y < b.ActualHeight / 2) ? HidCommand.VolumeUp : HidCommand.VolumeDown;
        await DispatchAsync(cmd);
    }

    private async Task DispatchAsync(HidCommand cmd)
    {
        try
        {
            var session = await EnsureSessionAsync();
            if (session is null) return;
            await session.PressAsync(cmd);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{cmd} failed: {ex.Message}", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await TeardownActiveAsync();
        }
    }

    // ─── Pairing entry point ───────────────────────────────────────

    private void OnOpenPairClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new PairingWindow { Owner = this };
        // The popup is Topmost; the pairing window needs to be on top of THAT.
        dlg.Topmost = true;
        if (dlg.ShowDialog() == true)
        {
            RefreshDevices();
            // Pre-select the newly-paired device.
            if (dlg.PairedCredentials is { } c)
            {
                foreach (var item in DeviceCombo.Items)
                {
                    if (item is StoredCredentials sc && sc.DeviceId == c.DeviceId)
                    {
                        DeviceCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }
    }

    // ─── Connection management ─────────────────────────────────────

    private async Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            MessageBox.Show("No paired Apple TV. Click the + button to pair one.");
            return null;
        }

        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;

        await TeardownActiveAsync();

        if (string.IsNullOrEmpty(selected.Host) || selected.Port <= 0)
        {
            MessageBox.Show(
                "These credentials were created by an older build and don't include the device's host / port.\n" +
                "Click the + button and re-pair this device to update the stored entry.");
            return null;
        }

        try
        {
            var conn = new CompanionConnection(selected.Host, selected.Port);
            // If the TV closes the socket (decryption failure, idle timeout,
            // reboot) drop our cached session so the next button press
            // rebuilds it instead of timing out against a dead connection.
            conn.Closed += (_, _) =>
            {
                _session = null;
                _conn = null;
                _activeDevice = null;
            };
            await conn.ConnectAsync();
            await new CompanionPairVerify(conn).RunAsync(selected.Credentials);
            _conn          = conn;
            _session       = new CompanionSession(conn);
            _session.NowPlayingUpdated += OnNowPlayingUpdated;
            _activeDevice  = selected;

            // Run the Companion session-init handshake.
            // The v0.1.20–22 failures were the pair-verify M4 race (fixed in
            // v0.1.24), not this code. HID commands happen to work without it,
            // but app-launcher RPCs (FetchLaunchableApplicationsEvent,
            // _launchApp) silently time out and the TV closes the socket
            // ~35 s after the missing _touchStart — exactly the symptom in
            // the user-supplied wire log after v0.1.26.
            var clientIdString = System.Text.Encoding.ASCII.GetString(selected.Credentials.ClientIdentifier);
            // Match Swift sibling's _systemInfo: it sends "Mac Remote" as the
            // device name. Pair-setup M5 also hardcodes the same name in v0.1.35.
            await _session.SendSessionInitAsync(clientIdString, "Mac Remote");
            return _session;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to connect to {selected.Host}:{selected.Port} — {ex.Message}",
                "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private async Task TeardownActiveAsync()
    {
        if (_session is { } s)
        {
            try { s.NowPlayingUpdated -= OnNowPlayingUpdated; } catch { }
            try { await s.DisposeAsync(); } catch { }
        }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
        _ = Dispatcher.BeginInvoke(() => NowPlayingBanner.Visibility = Visibility.Collapsed);
    }

    // ─── Now-Playing banner ─────────────────────────────────────────

    private void OnNowPlayingUpdated(object? sender, NowPlayingUpdate update)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!update.HasDisplayableMetadata)
            {
                NowPlayingBanner.Visibility = Visibility.Collapsed;
                return;
            }
            NowPlayingTitle.Text  = update.Title  ?? string.Empty;
            NowPlayingArtist.Text = string.Join(" · ", new[] { update.Artist, update.Album }
                .Where(s => !string.IsNullOrWhiteSpace(s))!);
            NowPlayingBanner.Visibility = Visibility.Visible;
        });
    }

    // ─── App launcher (slides out to the left) ─────────────────────

    private const double LauncherPanelWidth = 320;     // 4 tiles wide @ 68 px ItemWidth + scrollbar/margin
    private const double RemoteWidth        = 200;
    private static readonly TimeSpan LauncherAnimDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>Last-fetched app list, cached so opening the launcher is instant after the first session.</summary>
    private IReadOnlyDictionary<string, string>? _cachedApps;
    private string? _launcherFilter;
    private bool    _launcherOpen;


    private async void OnAppsClicked(object sender, RoutedEventArgs e)
    {
        if (_launcherOpen) { CloseLauncher(); return; }

        var session = await EnsureSessionAsync();
        if (session is null) return;   // EnsureSessionAsync already showed the error

        OpenLauncher();
        if (_cachedApps is null) await FetchAppsAsync(session);
    }

    private async void OnLauncherRefreshClicked(object sender, RoutedEventArgs e)
    {
        var session = await EnsureSessionAsync();
        if (session is null) return;
        _cachedApps = null;
        await FetchAppsAsync(session);
    }

    private void OnLauncherCloseClicked(object sender, RoutedEventArgs e) => CloseLauncher();

    private void OnLauncherSearchChanged(object sender, TextChangedEventArgs e)
    {
        _launcherFilter = LauncherSearch.Text?.Trim();
        RenderApps();
    }

    private void OpenLauncher()
    {
        _launcherOpen = true;
        // 1. Snap the window to its open size in ONE step (Win32 SetWindowPos
        //    is brutal for transparent + drop-shadow popups when called per
        //    frame — that's what made the previous animation jerky).
        //    Right edge stays anchored: shift Left back by the launcher width,
        //    grow Width by the same amount.
        Left -= LauncherPanelWidth;
        Width = RemoteWidth + LauncherPanelWidth;
        LauncherCol.Width = new GridLength(LauncherPanelWidth);

        // 2. Show the panel parked off-screen-left and animate just the
        //    TranslateTransform — render-thread, GPU-composited, smooth.
        LauncherPanel.Visibility = Visibility.Visible;
        LauncherSlide.X = -LauncherPanelWidth;
        AnimateSlide(0);
    }

    private void CloseLauncher()
    {
        _launcherOpen = false;
        AnimateSlide(-LauncherPanelWidth, onCompleted: () =>
        {
            // Snap window back to remote-only AFTER the inner slide finishes.
            LauncherPanel.Visibility = Visibility.Collapsed;
            LauncherCol.Width = new GridLength(0);
            Width = RemoteWidth;
            Left += LauncherPanelWidth;
            LauncherSlide.X = 0;
        });
    }

    private void AnimateSlide(double targetX, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = targetX,
            Duration = new Duration(LauncherAnimDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (onCompleted is not null)
            anim.Completed += (_, _) => onCompleted();
        LauncherSlide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private async Task FetchAppsAsync(CompanionSession session)
    {
        LauncherStatus.Text = "Loading apps…";
        LauncherStatus.Visibility = Visibility.Visible;
        LauncherList.Visibility   = Visibility.Collapsed;
        try
        {
            var apps = await session.ListAppsAsync();
            _cachedApps = apps;
            RenderApps();
            PrefetchIcons(apps.Keys);
        }
        catch (Exception ex)
        {
            LauncherStatus.Text = $"Couldn't load apps: {ex.Message}";
        }
    }

    /// <summary>
    /// Kick off iTunes-Search lookups for every bundle id the moment the list
    /// arrives, so re-opening the launcher (or scrolling) finds everything
    /// already warmed in <see cref="AppIconCache"/>. Concurrency capped at 8
    /// to stay polite to iTunes. Errors are swallowed by AppIconCache itself.
    /// </summary>
    private static void PrefetchIcons(IEnumerable<string> bundleIds)
    {
        _ = Task.Run(async () =>
        {
            using var gate = new System.Threading.SemaphoreSlim(8);
            var tasks = new List<Task>();
            foreach (var id in bundleIds)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try { await AppIconCache.GetAsync(id).ConfigureAwait(false); }
                    catch { /* AppIconCache already swallows individual errors */ }
                    finally { gate.Release(); }
                }));
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* aggregate exceptions ignored — best effort */ }
        });
    }

    private void RenderApps()
    {
        if (_cachedApps is null) return;
        var q = _launcherFilter ?? string.Empty;
        var filtered = _cachedApps
            .Where(kv => string.IsNullOrEmpty(q) ||
                         (kv.Value ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LauncherList.Items.Clear();
        foreach (var kv in filtered)
            LauncherList.Items.Add(BuildAppTile(kv.Key, kv.Value));

        if (filtered.Count == 0)
        {
            LauncherStatus.Text = _cachedApps.Count == 0 ? "No apps reported by Apple TV"
                                                         : "No matches";
            LauncherStatus.Visibility = Visibility.Visible;
            LauncherList.Visibility   = Visibility.Collapsed;
        }
        else
        {
            LauncherStatus.Visibility = Visibility.Collapsed;
            LauncherList.Visibility   = Visibility.Visible;
        }
    }

    private FrameworkElement BuildAppTile(string bundleId, string name)
    {
        var (top, bottom) = ColorPairFor(bundleId);
        var initial = string.IsNullOrWhiteSpace(name) ? "?"
                    : char.ToUpperInvariant(name.Trim()[0]).ToString();

        // Letter placeholder paints instantly; real icon swaps in async when
        // iTunes Search returns (≈ one HTTP round-trip per uncached app).
        var tile = new Border
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 4, 0, 2),
            ClipToBounds = true,
            Background = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1)),
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };
        _ = LoadIconAsync(tile, bundleId);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 10,
            Foreground = (Brush)FindResource("ButtonGlyph"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 28,
            Margin = new Thickness(2, 2, 2, 0),
        };

        var cell = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = $"{name}\n{bundleId}",
        };
        cell.Children.Add(tile);
        cell.Children.Add(label);
        cell.MouseLeftButtonUp += async (_, _) => await LaunchAppAsync(bundleId, name);
        return cell;
    }

    private async Task LaunchAppAsync(string bundleId, string displayName)
    {
        try
        {
            var session = await EnsureSessionAsync();
            if (session is null) return;
            await session.LaunchAppAsync(bundleId);
            CloseLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Launching {displayName} failed: {ex.Message}",
                "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Error);
            await TeardownActiveAsync();
        }
    }

    private async Task LoadIconAsync(Border tile, string bundleId)
    {
        try
        {
            var bytes = await AppIconCache.GetAsync(bundleId).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(bytes);
                    img.BeginInit();
                    img.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    // Swap the placeholder for the real artwork.
                    tile.Background = null;
                    tile.Child = new System.Windows.Controls.Image
                    {
                        Source  = img,
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                    };
                }
                catch { /* leave the letter placeholder visible */ }
            });
        }
        catch { /* network errors are already swallowed in AppIconCache */ }
    }

    /// <summary>Deterministic gradient per bundle id — same app always gets the same tile colour.</summary>
    private static (Color top, Color bottom) ColorPairFor(string bundleId)
    {
        int hash = 0;
        foreach (var c in bundleId) hash = unchecked(hash * 31 + c);
        var hue = (Math.Abs(hash) % 360) / 360.0;
        return (HsvToRgb(hue, 0.55, 0.82), HsvToRgb(hue, 0.70, 0.55));
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        int hi = (int)Math.Floor(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = hi switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
