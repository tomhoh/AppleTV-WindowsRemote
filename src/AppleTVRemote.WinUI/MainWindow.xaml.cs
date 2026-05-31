// SPDX-License-Identifier: MIT
//
// WinUI 3 Mac-style remote.
//
// Theme handling is intentionally minimal: brushes live in
// Application.Resources.ThemeDictionaries (Light/Dark) and consumers
// bind via {ThemeResource ...}. All we do here is set
// Root.RequestedTheme to match the system, and WinUI re-resolves
// every ThemeResource consumer for us — no manual brush walking.

using System;
using System.Threading.Tasks;
using AppleTVProtocol;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;

    private const double ClickpadDiameter      = 130;
    private const double ClickpadInnerDiameter = 72;
    private const double SwipeThresholdPx      = 24;
    private const double TapMaxTotalPx         = 8;

    private bool _dragActive;
    private Point _dragBaseline;
    private double _totalDragDistance;
    private HidCommand? _hoveredRegion;

    private readonly UISettings _uiSettings = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public MainWindow()
    {
        InitializeComponent();

        ApplyBorderlessPresenter();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ApplyTheme();
        RefreshDevices();

        // UISettings.ColorValuesChanged is the canonical signal for the
        // user toggling system light/dark — fires off the UI thread.
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                this.Hide();
            }
            else
            {
                // Re-check on each show in case theme flipped while hidden.
                ApplyTheme();
            }
        };
        Closed += async (_, _) =>
        {
            _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
            await TeardownActiveAsync();
        };
    }

    public void PositionNearTray() => TrayWindowPositioner.PositionNearCursor(this);

    private void ApplyBorderlessPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable   = false;
        }
    }

    // ─── Theme adaptation ──────────────────────────────────────────

    private void OnSystemColorsChanged(UISettings sender, object args)
        => _dispatcher?.TryEnqueue(ApplyTheme);

    /// <summary>
    /// Flip Root.RequestedTheme based on UISettings — the canonical
    /// Windows source of truth. {ThemeResource ...} consumers in the
    /// XAML re-resolve automatically.
    /// </summary>
    private void ApplyTheme()
    {
        Root.RequestedTheme = IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light;
    }

    private bool IsSystemDark()
    {
        var bg = _uiSettings.GetColorValue(UIColorType.Background);
        return (bg.R + bg.G + bg.B) < 384;   // dark→≈0, light→≈765
    }

    /// <summary>Public so PairingDialog can inherit the same theme.</summary>
    public ElementTheme CurrentTheme => Root.RequestedTheme;

    // ─── Device list / connection management ───────────────────────

    private void RefreshDevices()
    {
        DeviceCombo.ItemsSource = null;
        var creds = _store.LoadAll();
        DeviceCombo.DisplayMemberPath = nameof(StoredCredentials.DisplayName);
        DeviceCombo.ItemsSource = creds;
        if (creds.Count > 0)
        {
            DeviceCombo.SelectedIndex = 0;
            DeviceCombo.Visibility = Visibility.Visible;
        }
        else
        {
            DeviceCombo.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Clickpad gestures ─────────────────────────────────────────

    private void OnClickpadPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = true;
        _dragBaseline = e.GetCurrentPoint(Clickpad).Position;
        _totalDragDistance = 0;
        Clickpad.CapturePointer(e.Pointer);
    }

    private async void OnClickpadPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Clickpad).Position;

        if (_dragActive)
        {
            var dx = p.X - _dragBaseline.X;
            var dy = p.Y - _dragBaseline.Y;
            _totalDragDistance = Math.Max(_totalDragDistance, Math.Sqrt(dx * dx + dy * dy));
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeThresholdPx) return;

            HidCommand cmd = (Math.Abs(dx) >= Math.Abs(dy))
                ? (dx > 0 ? HidCommand.Right : HidCommand.Left)
                : (dy > 0 ? HidCommand.Down  : HidCommand.Up);
            FlashQuadrant(cmd);
            await DispatchAsync(cmd);
            _dragBaseline = p;
            return;
        }

        var region = HitRegion(p);
        if (region != _hoveredRegion)
        {
            _hoveredRegion = region;
            ApplyHoverRegion(region);
        }
    }

    private void OnClickpadPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragActive) return;
        _hoveredRegion = null;
        ApplyHoverRegion(null);
    }

    private async void OnClickpadPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        Clickpad.ReleasePointerCapture(e.Pointer);
        _dragActive = false;

        if (_totalDragDistance < TapMaxTotalPx)
        {
            var cmd = HitRegion(e.GetCurrentPoint(Clickpad).Position);
            FlashQuadrant(cmd);
            await DispatchAsync(cmd);
        }
    }

    private void ApplyHoverRegion(HidCommand? region)
    {
        SelectDisc.Fill = (region == HidCommand.Select)
            ? (Brush)Application.Current.Resources["SelectHoverFill"]
            : (Brush)Application.Current.Resources["SelectFill"];

        if (region is null or HidCommand.Select)
        {
            HoverQuadrant.Visibility = Visibility.Collapsed;
            return;
        }
        var geo = new PathGeometry();
        geo.Figures.Add(BuildQuadrantWedge(region.Value));
        HoverQuadrant.Data = geo;
        HoverQuadrant.Opacity = 1.0;
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
            SelectScale.ScaleX = 0.94; SelectScale.ScaleY = 0.94;
            _ = Task.Delay(180).ContinueWith(_ =>
                _dispatcher?.TryEnqueue(() =>
                {
                    SelectScale.ScaleX = 1.0; SelectScale.ScaleY = 1.0;
                }));
            return;
        }
        var geo = new PathGeometry();
        geo.Figures.Add(BuildQuadrantWedge(cmd));
        QuadrantHighlight.Data = geo;
        QuadrantHighlight.Opacity = 1.0;
        QuadrantHighlight.Visibility = Visibility.Visible;
        _ = Task.Delay(220).ContinueWith(_ =>
            _dispatcher?.TryEnqueue(() =>
            {
                QuadrantHighlight.Visibility = Visibility.Collapsed;
            }));
    }

    private static PathFigure BuildQuadrantWedge(HidCommand cmd)
    {
        // Donut slice — outer arc, radial line, inner arc, close. Never
        // crosses into the area under the center select disc.
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
        var fig = new PathFigure { StartPoint = Polar(rOuter, startDeg), IsClosed = true };
        fig.Segments.Add(new ArcSegment  { Point = Polar(rOuter, endDeg),   Size = new Size(rOuter, rOuter), SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment { Point = Polar(rInner, endDeg) });
        fig.Segments.Add(new ArcSegment  { Point = Polar(rInner, startDeg), Size = new Size(rInner, rInner), SweepDirection = SweepDirection.Counterclockwise });
        fig.Segments.Add(new LineSegment { Point = Polar(rOuter, startDeg) });
        return fig;
    }

    // ─── HID handlers ──────────────────────────────────────────────

    private async void OnHidClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            await ShowMessageAsync($"Unknown HID command: {tag}");
            return;
        }
        await DispatchAsync(cmd);
    }

    private async void OnPowerClicked(object sender, RoutedEventArgs e)
        => await DispatchAsync(HidCommand.Wake);

    private void OnCloseClicked(object sender, RoutedEventArgs e) => this.Hide();

    private async void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b) return;
        var p = e.GetCurrentPoint(b).Position;
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
            await ShowMessageAsync($"{cmd} failed: {ex.Message}");
            await TeardownActiveAsync();
        }
    }

    // ─── Pairing entry point ───────────────────────────────────────

    private async void OnOpenPairClicked(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null) return;
        var dlg = new PairingDialog
        {
            XamlRoot      = Content.XamlRoot,
            RequestedTheme = Root.RequestedTheme,   // match popup theme
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary && dlg.PairedCredentials is { } c)
        {
            RefreshDevices();
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

    // ─── Connection management ─────────────────────────────────────

    private async Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            await ShowMessageAsync("No paired Apple TV. Click the + button to pair one.");
            return null;
        }
        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;
        await TeardownActiveAsync();

        if (string.IsNullOrEmpty(selected.Host) || selected.Port <= 0)
        {
            await ShowMessageAsync(
                "These credentials were created by an older build and don't include the device's host / port.\n" +
                "Click the + button and re-pair this device to update the stored entry.");
            return null;
        }

        try
        {
            var conn = new CompanionConnection(selected.Host, selected.Port);
            await conn.ConnectAsync();
            await new CompanionPairVerify(conn).RunAsync(selected.Credentials);
            _conn          = conn;
            _session       = new CompanionSession(conn);
            _activeDevice  = selected;
            return _session;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"Failed to connect to {selected.Host}:{selected.Port} — {ex.Message}");
            return null;
        }
    }

    private async Task TeardownActiveAsync()
    {
        if (_session is { } s) { try { await s.DisposeAsync(); } catch { } }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
    }

    private async Task ShowMessageAsync(string text)
    {
        if (Content?.XamlRoot is null) return;
        var dlg = new ContentDialog
        {
            Title           = "Apple TV Remote",
            Content         = text,
            CloseButtonText = "OK",
            XamlRoot        = Content.XamlRoot,
            RequestedTheme  = Root.RequestedTheme,
        };
        await dlg.ShowAsync();
    }
}
