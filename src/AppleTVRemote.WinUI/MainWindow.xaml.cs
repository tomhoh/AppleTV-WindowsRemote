// SPDX-License-Identifier: MIT
//
// WinUI 3 mirror of the WPF Mac-style remote. Same geometry, same
// gesture logic; only the framework primitives differ.
//   - WindowEx (WinUIEx)        — sizing / no-min/max controls
//   - MicaBackdrop              — frosted aluminum-feel background
//   - Pointer events on Grid    — clickpad hit-detection + drag-swipe

using System;
using System.Threading.Tasks;
using AppleTVProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;

    // Clickpad geometry — must match the XAML.
    private const double ClickpadDiameter      = 130;
    private const double ClickpadInnerDiameter = 72;
    private const double SwipeThresholdPx      = 24;
    private const double TapMaxTotalPx         = 8;

    private bool _dragActive;
    private Point _dragBaseline;
    private double _totalDragDistance;

    public MainWindow()
    {
        InitializeComponent();
        RefreshDevices();
        Activated += (_, args) =>
        {
            // macOS-style: dismiss popover when it loses focus.
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                this.Hide();
        };
        Closed += async (_, _) => await TeardownActiveAsync();
    }

    public void PositionNearTray() => TrayWindowPositioner.PositionNearCursor(this);

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
        if (!_dragActive) return;
        var p = e.GetCurrentPoint(Clickpad).Position;
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
            _ = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { SelectScale.ScaleX = 1.0; SelectScale.ScaleY = 1.0; });
            return;
        }
        var geo = new PathGeometry();
        geo.Figures.Add(BuildQuadrantWedge(cmd));
        QuadrantHighlight.Data = geo;
        QuadrantHighlight.Opacity = 1.0;
        QuadrantHighlight.Visibility = Visibility.Visible;
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(220).ContinueWith(_ =>
            dq.TryEnqueue(() => { QuadrantHighlight.Visibility = Visibility.Collapsed; }));
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
        var fig = new PathFigure { StartPoint = center, IsClosed = true };
        fig.Segments.Add(new LineSegment  { Point = ArcPoint(startDeg) });
        fig.Segments.Add(new ArcSegment   { Point = ArcPoint(endDeg), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment  { Point = center });
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

    // ─── Connection management ─────────────────────────────────────

    private async Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            await ShowMessageAsync(
                "No paired Apple TV. Run:\n\n" +
                "    atv discover\n    atv pair <host> <port>\n\n" +
                "from the CLI, then reopen the popup.");
            return null;
        }
        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;
        await TeardownActiveAsync();
        await ShowMessageAsync(
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

    private async Task ShowMessageAsync(string text)
    {
        if (Content?.XamlRoot is null) return;
        var dlg = new ContentDialog
        {
            Title           = "Apple TV Remote",
            Content         = text,
            CloseButtonText = "OK",
            XamlRoot        = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
