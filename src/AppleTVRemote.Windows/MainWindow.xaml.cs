// SPDX-License-Identifier: MIT
//
// Mac-style Apple-TV remote popup (WPF).
//
// All theme-sensitive brushes are seeded as light-mode defaults in XAML
// and re-applied at runtime by ApplyTheme() — wired to live
// SystemEvents.UserPreferenceChanged so toggling Windows light/dark
// updates the popup instantly without a restart.

using System;
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

        Loaded += (_, _) =>
        {
            ApplyTheme();
            RefreshDevices();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        };
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
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
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
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            MessageBox.Show($"unknown HID command: {tag}");
            return;
        }
        await DispatchAsync(cmd);
    }

    private async void OnPowerClicked(object sender, RoutedEventArgs e)
        => await DispatchAsync(HidCommand.Wake);

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
