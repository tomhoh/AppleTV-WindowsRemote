// SPDX-License-Identifier: MIT
//
// Mac-style Apple-TV remote popup.
//
// Visual layout (matches the SwiftUI MenuBarRemote sibling):
//   - 200x380 rounded body with aluminum gradient + soft top highlight
//   - Round 130pt clickpad with center select disc
//   - 5 click regions (top/bottom/left/right + center) detected via
//     mouse coords; drag-to-swipe routes to arrow commands too
//   - Pill button row: Menu / Home / Siri
//   - Bottom row: PlayPause + tall volume pill (top half = +, bottom = −)
//   - Power button in the top-right
//
// Connection lifecycle: lazy pair-verify on first click against the
// currently-selected stored device. Failures tear the session down so
// the next click reconnects clean.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppleTVProtocol;

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

    // Drag-to-swipe state.
    private bool _dragActive;
    private Point _dragBaseline;
    private double _totalDragDistance;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyTheme();
            RefreshDevices();
        };
        Closed += async (_, _) => await TeardownActiveAsync();
    }

    // ─── Tray-anchored popup behaviour ─────────────────────────────

    /// <summary>
    /// Called by App.OnTrayLeftClicked just before Show() — moves us
    /// next to the cursor (i.e. next to the tray icon).
    /// </summary>
    public void PositionNearTray() => TrayWindowPositioner.PositionNearCursor(this);

    /// <summary>
    /// macOS popovers dismiss when they lose focus. Do the same on
    /// Windows so the tray UX feels native.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    // ─── Theme adaptation ──────────────────────────────────────────

    private void ApplyTheme()
    {
        // Windows 10/11 dark-mode hint via AppsUseLightTheme (0 = dark).
        bool isDark = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) isDark = v == 0;
        }
        catch { /* ignore — stay on light */ }

        if (!isDark) return;

        // Swap to a dark aluminum gradient.
        var darkBody = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x52, 0x52, 0x52), 0),
                new GradientStop(Color.FromRgb(0x2E, 0x2E, 0x2E), 1),
            },
            new Point(0, 0), new Point(0, 1));
        var darkHighlight = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            },
            new Point(0, 0), new Point(0, 0.5));
        Resources["BodyGradient"]   = darkBody;
        Resources["BodyHighlight"]  = darkHighlight;
        Resources["ButtonGlyph"]    = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        Resources["ClickpadRing"]   = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        Resources["ButtonStroke"]   = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
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
    }

    private void OnClickpadMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive) return;
        var p = e.GetPosition(Clickpad);
        var dx = p.X - _dragBaseline.X;
        var dy = p.Y - _dragBaseline.Y;
        _totalDragDistance = Math.Max(_totalDragDistance, Math.Sqrt(dx * dx + dy * dy));
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeThresholdPx) return;

        HidCommand cmd = (Math.Abs(dx) >= Math.Abs(dy))
            ? (dx > 0 ? HidCommand.Right : HidCommand.Left)
            : (dy > 0 ? HidCommand.Down  : HidCommand.Up);
        _ = DispatchAsync(cmd);
        FlashQuadrant(cmd);

        // Reset baseline so each SwipeThreshold px fires once.
        _dragBaseline = p;
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
            // Pulse the center disc.
            var scale = (ScaleTransform)SelectDisc.RenderTransform;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.94, To = 1.0, Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            return;
        }

        // Build a wedge for the quadrant and fade it.
        var geo = new PathGeometry { Figures = { BuildQuadrantWedge(cmd) } };
        QuadrantHighlight.Data = geo;
        QuadrantHighlight.Visibility = Visibility.Visible;
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0, To = 0.0, Duration = TimeSpan.FromMilliseconds(220),
        };
        fade.Completed += (_, _) => QuadrantHighlight.Visibility = Visibility.Collapsed;
        QuadrantHighlight.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static PathFigure BuildQuadrantWedge(HidCommand cmd)
    {
        double r = ClickpadDiameter / 2;
        var center = new Point(r, r);

        double startDeg = cmd switch
        {
            HidCommand.Right => -45,
            HidCommand.Down  =>  45,
            HidCommand.Left  => 135,
            HidCommand.Up    => -135,
            _ => 0,
        };
        double endDeg = startDeg + 90;
        Point ArcPoint(double deg)
        {
            var rad = deg * Math.PI / 180.0;
            return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        }

        return new PathFigure
        {
            StartPoint = center,
            Segments =
            {
                new LineSegment(ArcPoint(startDeg), true),
                new ArcSegment(ArcPoint(endDeg), new Size(r, r), 0, false, SweepDirection.Clockwise, true),
                new LineSegment(center, true),
            },
            IsClosed = true,
        };
    }

    // ─── HID handlers ──────────────────────────────────────────────

    private async void OnHidClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        // The "Siri" pill maps to a hold-Siri gesture in pyatv (HID code Siri=10);
        // here we treat it as a tap that toggles dictation.
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            MessageBox.Show($"unknown HID command: {tag}");
            return;
        }
        await DispatchAsync(cmd);
    }

    private async void OnPowerClicked(object sender, RoutedEventArgs e)
    {
        // Pyatv doesn't expose a unique "Power" — Wake/Sleep are the
        // closest moral equivalents. Tap = Wake; long-press would be
        // Sleep but we keep it simple here.
        await DispatchAsync(HidCommand.Wake);
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

    // ─── Connection management ─────────────────────────────────────

    private async Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            MessageBox.Show(
                "No paired Apple TV. Run:\n\n" +
                "    atv discover\n    atv pair <host> <port>\n\n" +
                "from the CLI, then reopen the popup.");
            return null;
        }

        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;

        await TeardownActiveAsync();

        // TODO(Phase 7 iteration): persist host/port alongside credentials
        // and read them here. For now, MainWindow can't connect by itself —
        // the CLI is the canonical pair-flow entry point.
        MessageBox.Show(
            "TODO: connect-time host/port input not implemented yet.\n" +
            "For now, run `atv key <id> <host> <port> <button>` from the CLI.");
        return null;
    }

    private async Task TeardownActiveAsync()
    {
        if (_session is { } s) { try { await s.DisposeAsync(); } catch { } }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
    }
}
