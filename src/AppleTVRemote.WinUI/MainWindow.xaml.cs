// SPDX-License-Identifier: MIT
//
// WinUI 3 mirror of the WPF Mac-style remote.
//
// - Borderless window (OverlappedPresenter with HasTitleBar=false +
//   HasBorder=true), with a custom close-X in the top-left of the UI.
// - Mica BaseAlt backdrop adapts to system theme automatically; for
//   our own brushes (button material, click ring, etc.) we listen to
//   ActualThemeChanged and re-seed the Grid resources.
// - Soft hover quadrant + center-disc hover feedback on the clickpad.

using System;
using System.Linq;
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
using Windows.UI;
using Windows.UI.ViewManagement;   // UISettings — canonical system-theme source on Windows
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;
    private TextInputWindowManager? _textInput;

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
    /// <summary>
    /// Global mouse-down hook that dismisses the popup on any click
    /// outside its screen rect. Set up in the ctor and torn down on
    /// Closed. This replaces the Activated/Deactivated auto-hide,
    /// which fires unreliably for topmost windows on Win11.
    /// </summary>
    private LowLevelMouseHook? _outsideClickHook;
    /// <summary>
    /// UTC timestamp of the most recent activation. The mouse hook
    /// ignores clicks that fall within a short grace window after
    /// this — otherwise the very click that OPENS the popup (on the
    /// tray icon, which is outside the popup's rect) races through the
    /// hook and dismisses the popup before it can even take focus.
    /// </summary>
    private DateTime _lastActivatedAt = DateTime.MinValue;
    private const int OutsideClickGraceMs = 300;

    public MainWindow()
    {
        InitializeComponent();

        ApplyBorderlessPresenter();
        ApplyTheme();
        RefreshDevices();

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // UISettings.ColorValuesChanged is the canonical signal for the
        // user toggling system light/dark — fires off the UI thread, so
        // marshal back via the dispatcher.
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Uninstall the hook while hidden so it can't fire
                // spuriously between Show cycles.
                _outsideClickHook?.Dispose();
                _outsideClickHook = null;
                this.Hide();
            }
            else
            {
                _lastActivatedAt = DateTime.UtcNow;
                ApplyTheme();
                ReinforceBorderlessTopmost();
                // Install the hook only after the popup is actually
                // active. Combined with the grace window in
                // OnGlobalMouseDown this prevents the click that OPENED
                // the popup (typically on the tray icon) from being
                // seen by the hook and immediately dismissing us.
                _outsideClickHook ??= new LowLevelMouseHook(OnGlobalMouseDown);
            }
        };
        Closed += async (_, _) =>
        {
            _outsideClickHook?.Dispose();
            _outsideClickHook = null;
            _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
            await TeardownActiveAsync();
        };
    }

    private void OnSystemColorsChanged(UISettings sender, object args)
    {
        _dispatcher?.TryEnqueue(ApplyTheme);
    }

    /// <summary>
    /// Fires on the mouse-hook thread for every mouse-down anywhere on
    /// screen. If the popup is visible and the click is outside its
    /// screen rect, hide it. Marshals to the UI thread first because
    /// AppWindow.Position/Size and Hide() must be UI-thread calls.
    /// </summary>
    private void OnGlobalMouseDown(int screenX, int screenY)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            try
            {
                if (!AppWindow.IsVisible) return;
                // Ignore clicks within the activation grace window —
                // catches the click that just opened the popup (tray
                // icon lives outside the popup rect, so the hook would
                // otherwise fire Hide() on the very click that showed us).
                if ((DateTime.UtcNow - _lastActivatedAt).TotalMilliseconds < OutsideClickGraceMs)
                    return;
                var pos  = AppWindow.Position;
                var size = AppWindow.Size;
                bool insideX = screenX >= pos.X && screenX <= pos.X + size.Width;
                bool insideY = screenY >= pos.Y && screenY <= pos.Y + size.Height;
                if (!(insideX && insideY))
                    this.Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnGlobalMouseDown: {ex}");
            }
        });
    }

    public void PositionNearTray() => TrayWindowPositioner.PositionNearCursor(this);

    /// <summary>
    /// Re-run WinUI3AppBar's StripFrameStyles pattern and pin the
    /// window to Z-order top via <c>SetWindowPos(HWND_TOPMOST)</c>.
    /// Called from Activated on every show cycle because Win11 re-adds
    /// the frame styles and drops the topmost bit on visibility
    /// changes. The XAML `IsAlwaysOnTop="True"` on WindowEx handles
    /// the initial state, but doesn't reliably re-assert after
    /// Show/Hide cycles.
    /// </summary>
    private void ReinforceBorderlessTopmost()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Strip the border-drawing bits again — Win11 sneaks them
            // back on after certain state transitions (DPI change,
            // taskbar-autohide toggle, focus changes, etc.).
            long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            style &= ~((long)(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME));
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);
            // Force the OS to recompute the non-client rect AND lift
            // the window to the topmost band. SWP_NOMOVE/NOSIZE keeps
            // the position PositionNearTray already set; SWP_NOACTIVATE
            // so we don't cause a spurious deactivation cycle.
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReinforceBorderlessTopmost: {ex}");
        }
    }

    private void ApplyBorderlessPresenter()
    {
        // Deliberately NOT calling AppWindow.SetPresenter here.
        // Doing so replaces the WindowEx-managed OverlappedPresenter
        // with a fresh instance, disconnecting WindowEx's own hooks
        // (which is how IsAlwaysOnTop / IsShownInSwitchers / IsResizable
        // etc. — all set in XAML on <winuiex:WindowEx> — actually take
        // effect). WinUI3AppBar demonstrates this: it configures those
        // properties in XAML and never touches SetPresenter.
        //
        // The frame-strip below is what makes the window borderless.
        // Style removal + SWP_FRAMECHANGED is enough; no presenter
        // swap needed.
        // Force DWM to use its largest rounded-corner preference (~8 px).
        // SetWindowRgn was tried for a bigger 22 px radius, but it fought
        // with the system-drawn border and left a white frame behind —
        // dropped that approach. Going larger than DWM's max would
        // require ripping out the entire system chrome (no Mica, custom
        // shadow/border), which isn't worth the complexity right now.
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Keep the modern Win11 rounded corners. On Win11 this is
            // the default when a window has a border, so setting it
            // explicitly is belt-and-braces.
            int pref = 2;   // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref pref, sizeof(int));
            // NOTE: v0.3.4-v0.3.6 also set DWMWA_NCRENDERING_POLICY
            // = DWMNCRP_DISABLED and a custom DWMWA_BORDER_COLOR to try
            // to hide the frame. Both are removed — turning off DWM
            // non-client rendering was what caused the thick white
            // classic-style frame with missing corners on some builds.
            // We now let Windows draw its standard WinUI popup border.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DwmSetWindowAttribute failed: {ex}");
        }
    }

    private static class NativeMethods
    {
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        // Window-style constants + SetWindowPos flags used by the
        // StripFrameStyles routine, mirroring the WinUI3AppBar sample.
        internal const int  GWL_STYLE          = -16;
        internal const int  WS_CAPTION         = 0x00C00000;   // covers WS_BORDER | WS_DLGFRAME
        internal const int  WS_THICKFRAME      = 0x00040000;
        internal const uint SWP_NOSIZE         = 0x0001;
        internal const uint SWP_NOMOVE         = 0x0002;
        internal const uint SWP_NOZORDER       = 0x0004;
        internal const uint SWP_NOACTIVATE     = 0x0010;
        internal const uint SWP_FRAMECHANGED   = 0x0020;
        // Special hWndInsertAfter value for SetWindowPos — pins the
        // window above all non-topmost windows in the Z-order.
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        // GetWindowLongPtr / SetWindowLongPtr are the 64-bit-safe
        // variants — WinUI 3 apps run on x64/arm64 so we can't use the
        // 32-bit-only GetWindowLong / SetWindowLong.
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
    }

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

    /// <summary>
    /// Public hook for the tray-icon quick-switch menu. Refreshes the
    /// stored-device list and pre-selects the entry with this DeviceId.
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

    /// <summary>Device id of the currently-live session, or null if idle.</summary>
    public string? ActiveDeviceId => _activeDevice?.DeviceId;

    /// <summary>
    /// Tear down any active session and open a new one against whatever
    /// <see cref="DeviceCombo"/> currently has selected. Called from the
    /// tray quick-switch menu so picking a paired device reconnects
    /// silently in the background — no need for the user to click a
    /// remote button just to trigger the switch.
    /// </summary>
    public Task ConnectSelectedAsync() => EnsureSessionAsync();

    // ─── Theme adaptation ──────────────────────────────────────────

    private void ApplyTheme()
    {
        if (IsSystemDark())
            ApplyDarkBrushes();
        else
            ApplyLightBrushes();
        // PaintBrushConsumers (called from ApplyLight/DarkBrushes) walks
        // the visual tree and re-paints brushes that were originally set
        // via StaticResource — WinUI's StaticResource bindings don't
        // re-resolve when the underlying dictionary entry is replaced.
    }

    /// <summary>
    /// Canonical system-theme check on Windows. UISettings's Background
    /// colour is the OS-active window background: black in dark mode,
    /// white in light mode. This is what Microsoft's own apps use, and
    /// is more reliable than ActualTheme on a Mica-backdrop window
    /// (which doesn't always propagate the system theme into the
    /// element-theme chain).
    /// </summary>
    private bool IsSystemDark()
    {
        var bg = _uiSettings.GetColorValue(UIColorType.Background);
        return (bg.R + bg.G + bg.B) < 384;   // dark → near 0, light → near 765
    }

    private void ApplyLightBrushes()
    {
        // Neutral near-white background to match system light-mode
        // windows — no Mica tint. Matches WPF sibling's #FFF8F8F8.
        Root.Background = Solid(0xFF, 0xF8, 0xF8, 0xF8);
        Root.Resources["ButtonMaterial"]    = Solid(0x33, 0x00, 0x00, 0x00);
        Root.Resources["ButtonMaterialHv"]  = Solid(0x52, 0x00, 0x00, 0x00);
        Root.Resources["ButtonMaterialDn"]  = Solid(0x72, 0x00, 0x00, 0x00);
        Root.Resources["ButtonGlyph"]       = Solid(0xE6, 0x00, 0x00, 0x00);
        Root.Resources["ButtonStroke"]      = Solid(0x40, 0x00, 0x00, 0x00);
        Root.Resources["ClickpadRing"]      = Solid(0x26, 0x00, 0x00, 0x00);
        Root.Resources["ClickpadHover"]     = Solid(0x1E, 0x00, 0x00, 0x00);
        Root.Resources["ClickpadPressed"]   = Solid(0x33, 0x00, 0x00, 0x00);
        Root.Resources["SelectFill"]        = Solid(0x80, 0xFF, 0xFF, 0xFF);
        Root.Resources["SelectHoverFill"]   = Solid(0xAA, 0xFF, 0xFF, 0xFF);
        Root.RequestedTheme = ElementTheme.Light;
        PaintBrushConsumers();
    }

    private void ApplyDarkBrushes()
    {
        // Neutral near-black background to match system dark-mode
        // windows — no Mica blue-ish tint. Matches WPF sibling's #FF1F1F1F.
        Root.Background = Solid(0xFF, 0x1F, 0x1F, 0x1F);
        Root.Resources["ButtonMaterial"]    = Solid(0x26, 0xFF, 0xFF, 0xFF);
        Root.Resources["ButtonMaterialHv"]  = Solid(0x40, 0xFF, 0xFF, 0xFF);
        Root.Resources["ButtonMaterialDn"]  = Solid(0x5C, 0xFF, 0xFF, 0xFF);
        Root.Resources["ButtonGlyph"]       = Solid(0xE6, 0xFF, 0xFF, 0xFF);
        Root.Resources["ButtonStroke"]      = Solid(0x33, 0xFF, 0xFF, 0xFF);
        Root.Resources["ClickpadRing"]      = Solid(0x1A, 0xFF, 0xFF, 0xFF);
        Root.Resources["ClickpadHover"]     = Solid(0x1A, 0xFF, 0xFF, 0xFF);
        Root.Resources["ClickpadPressed"]   = Solid(0x33, 0xFF, 0xFF, 0xFF);
        Root.Resources["SelectFill"]        = Solid(0x66, 0xFF, 0xFF, 0xFF);
        Root.Resources["SelectHoverFill"]   = Solid(0x80, 0xFF, 0xFF, 0xFF);
        Root.RequestedTheme = ElementTheme.Dark;
        PaintBrushConsumers();
    }

    /// <summary>
    /// WinUI XAML's StaticResource bindings don't re-resolve when the
    /// underlying ResourceDictionary entry is replaced. Touch the
    /// consumers explicitly so the new brushes take effect.
    /// </summary>
    private void PaintBrushConsumers()
    {
        var material  = (Brush)Root.Resources["ButtonMaterial"];
        var stroke    = (Brush)Root.Resources["ButtonStroke"];
        var glyph     = (Brush)Root.Resources["ButtonGlyph"];
        var ring      = (Brush)Root.Resources["ClickpadRing"];
        var hover     = (Brush)Root.Resources["ClickpadHover"];
        var pressed   = (Brush)Root.Resources["ClickpadPressed"];
        var selectFill= (Brush)Root.Resources["SelectFill"];

        SelectDisc.Fill   = selectFill;
        SelectDisc.Stroke = stroke;
        HoverQuadrant.Fill     = hover;
        QuadrantHighlight.Fill = pressed;

        VolumeRocker.Background  = material;
        VolumeRocker.BorderBrush = stroke;
        VolUpGlyph.Foreground    = glyph;
        VolDownGlyph.Foreground  = glyph;

        ApplyToButton(PowerBtn, material, stroke, glyph);

        // The other pill buttons live inside Grids by index; walk Root.
        WalkButtons(Root, material, stroke, glyph);

        // Outer ring of the clickpad is the first Ellipse child.
        if (Clickpad.Children.Count > 0 && Clickpad.Children[0] is Ellipse outer)
        {
            outer.Fill   = ring;
            outer.Stroke = stroke;
        }
    }

    private static void ApplyToButton(Button b, Brush bg, Brush stroke, Brush glyph)
    {
        b.Background  = bg;
        b.BorderBrush = stroke;
        b.Foreground  = glyph;
    }

    private static void WalkButtons(DependencyObject root, Brush bg, Brush stroke, Brush glyph)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b && b.Name is not "PowerBtn")
                ApplyToButton(b, bg, stroke, glyph);
            WalkButtons(child, bg, stroke, glyph);
        }
    }

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    // ─── Clickpad gestures ─────────────────────────────────────────

    private void OnClickpadPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = true;
        _dragBaseline = e.GetCurrentPoint(Clickpad).Position;
        _totalDragDistance = 0;
        Clickpad.CapturePointer(e.Pointer);
        // If anything yanks our pointer capture (focus theft, popup
        // dismissal, …), clear the drag state so subsequent hovers
        // don't fire commands.
        Clickpad.PointerCaptureLost -= OnClickpadPointerCaptureLost;
        Clickpad.PointerCaptureLost += OnClickpadPointerCaptureLost;
    }

    private void OnClickpadPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = false;
        _hoveredRegion = null;
        ApplyHoverRegion(null);
    }

    private async void OnClickpadPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(Clickpad);
        var p = pp.Position;

        // Defensive: if we somehow held a stale drag flag (capture
        // broke, releaseed event was missed), clear it before
        // treating this move as a drag. Hovering with _dragActive
        // stuck-on is exactly the 'controls fire without clicking'
        // bug — the move dispatches HID commands against a stale
        // baseline and the highlight doesn't track the cursor.
        if (_dragActive && !pp.Properties.IsLeftButtonPressed)
        {
            _dragActive = false;
            try { Clickpad.ReleasePointerCapture(e.Pointer); } catch { }
        }

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
            ? (Brush)Root.Resources["SelectHoverFill"]
            : (Brush)Root.Resources["SelectFill"];

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
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Delay(180).ContinueWith(_ => dq.TryEnqueue(() =>
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
        var dq2 = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(220).ContinueWith(_ =>
            dq2.TryEnqueue(() => { QuadrantHighlight.Visibility = Visibility.Collapsed; }));
    }

    private static PathFigure BuildQuadrantWedge(HidCommand cmd)
    {
        // Ring-shaped wedge — covers ONLY the outer quadrant, never bleeds
        // into the area under the center select disc.
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

    // Home: tap → home screen, hold → tvOS app-switcher. A DispatcherQueueTimer
    // started on press fires at the threshold; if it fires while still held,
    // that's a long-press → app-switcher (fixed 1 s programmatic HID hold,
    // matching the Swift port). Release before the timer = a tap.
    private static readonly TimeSpan HomeLongPressThreshold = TimeSpan.FromMilliseconds(500);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _homeHoldTimer;
    private bool _homeLongFired;

    private void OnHomeDown(object sender, PointerRoutedEventArgs e)
    {
        _homeLongFired = false;
        _homeHoldTimer?.Stop();
        _homeHoldTimer = DispatcherQueue.CreateTimer();
        _homeHoldTimer.Interval = HomeLongPressThreshold;
        _homeHoldTimer.IsRepeating = false;
        _homeHoldTimer.Tick += async (_, _) =>
        {
            _homeLongFired = true;
            try
            {
                var session = await EnsureSessionAsync();
                if (session is null) return;
                await session.LongPressAsync(HidCommand.Home, 1000);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"App switcher failed: {ex.Message}");
                await TeardownActiveAsync();
            }
        };
        _homeHoldTimer.Start();
    }

    private async void OnHomeUp(object sender, PointerRoutedEventArgs e)
    {
        _homeHoldTimer?.Stop();
        if (_homeLongFired) { _homeLongFired = false; return; }  // long-press already handled
        await DispatchAsync(HidCommand.Home);                    // short tap → go home
    }

    private void OnHomeLeave(object sender, PointerRoutedEventArgs e)
    {
        _homeHoldTimer?.Stop();
        _homeLongFired = false;
    }

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

    private PairingWindow? _pairingWindow;

    /// <summary>
    /// Open the standalone pair-setup window. Public so the tray
    /// context menu (App.xaml.cs) can invoke it — the old in-popup
    /// "pair" button was removed to keep the popup lean.
    /// </summary>
    public void OpenPairing()
    {
        // PairingWindow used to live as a ContentDialog inside this
        // popup, but a ContentDialog is bounded by the popup's tiny
        // XamlRoot (200×380) and content got clipped. It's a standalone
        // WindowEx now; we listen for its Closed event to refresh the
        // device picker if the user successfully paired.
        if (_pairingWindow is not null)
        {
            _pairingWindow.Activate();
            return;
        }

        var pw = new PairingWindow();
        _pairingWindow = pw;
        pw.Closed += (_, _) =>
        {
            _pairingWindow = null;
            if (pw.PairedCredentials is { } c)
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
        };
        pw.Activate();
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
            // If the TV closes the socket (decryption failure, idle timeout,
            // reboot) drop our cached session so the next button press
            // rebuilds it instead of timing out against a dead connection.
            conn.Closed += (_, _) =>
            {
                var ti = _textInput;
                _textInput = null;
                _session   = null;
                _conn      = null;
                _activeDevice = null;
                if (ti is not null)
                    DispatcherQueue?.TryEnqueue(() => { try { ti.Dispose(); } catch { } });
            };
            await conn.ConnectAsync();
            await new CompanionPairVerify(conn).RunAsync(selected.Credentials);
            _conn          = conn;
            _session       = new CompanionSession(conn);
            _session.NowPlayingUpdated += OnNowPlayingUpdated;
            _activeDevice  = selected;
            _textInput     = new TextInputWindowManager(
                _session,
                resolvePopup: () => this,
                // WindowEx.Hide() returns bool, so it can't be passed as a
                // method-group to TryEnqueue (which wants Action) — wrap
                // in a discard lambda.
                dismissPopup: () => DispatcherQueue?.TryEnqueue(() => this.Hide()));

            // Run the Companion session-init handshake.
            // The v0.1.20–22 failures were the pair-verify M4 race (fixed in
            // v0.1.24), not this code. HID commands happen to work without it,
            // but app-launcher RPCs (FetchLaunchableApplicationsEvent,
            // _launchApp) silently time out and the TV closes the socket
            // ~35 s after the missing _touchStart — exactly the symptom in
            // the user-supplied wire log after v0.1.26.
            var clientIdString = System.Text.Encoding.ASCII.GetString(selected.Credentials.ClientIdentifier);
            // Send whatever controller name was registered with the ATV
            // during pair-setup M5 (StoredCredentials.ControllerName).
            // Older credentials paired before that field existed have it
            // empty; fall back to the machine name so the picker still
            // shows something identifiable.
            var controllerName = string.IsNullOrEmpty(selected.ControllerName)
                ? Environment.MachineName
                : selected.ControllerName;
            await _session.SendSessionInitAsync(clientIdString, controllerName);
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
        if (_textInput is { } ti) { try { ti.Dispose(); } catch { } }
        _textInput = null;
        if (_session is { } s)
        {
            try { s.NowPlayingUpdated -= OnNowPlayingUpdated; } catch { }
            try { await s.DisposeAsync(); } catch { }
        }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
        DispatcherQueue?.TryEnqueue(() => NowPlayingBanner.Visibility = Visibility.Collapsed);
    }

    // ─── Now-Playing banner ─────────────────────────────────────────

    private void OnNowPlayingUpdated(object? sender, NowPlayingUpdate update)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!update.HasDisplayableMetadata)
            {
                NowPlayingBanner.Visibility = Visibility.Collapsed;
                return;
            }
            NowPlayingTitle.Text  = update.Title ?? string.Empty;
            var sub = string.Join(" · ", new[] { update.Artist, update.Album }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            NowPlayingArtist.Text = sub;
            NowPlayingBanner.Visibility = Visibility.Visible;
        });
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

    // ─── App launcher (slides out to the left) ─────────────────────

    private const int LauncherPanelWidth = 320;     // 4 tiles wide @ 68 px ItemWidth + scrollbar/margin
    private const int RemoteWidth        = 200;
    private static readonly TimeSpan LauncherAnimDuration = TimeSpan.FromMilliseconds(220);

    private System.Collections.Generic.IReadOnlyDictionary<string, string>? _cachedApps;
    private string? _launcherFilter;
    private bool    _launcherOpen;

    private async void OnAppsClicked(object sender, RoutedEventArgs e)
    {
        if (_launcherOpen) { CloseLauncher(); return; }

        var session = await EnsureSessionAsync();
        if (session is null) return;

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
        // Snap the window to its open size in ONE step — per-frame
        // AppWindow.MoveAndResize for transparent + acrylic popups is
        // brutally jerky on the desktop compositor.
        var size = AppWindow.Size;
        var pos  = AppWindow.Position;
        double scale = Width > 0 ? size.Width / Width : 1.0;
        int colPx = (int)Math.Round(LauncherPanelWidth * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(pos.X - colPx, pos.Y, size.Width + colPx, size.Height));
        LauncherCol.Width = new GridLength(LauncherPanelWidth);

        // Now animate JUST the TranslateTransform on the panel — Microsoft.UI
        // .Composition handles this on the compositor thread, GPU-accelerated.
        LauncherPanel.Visibility = Visibility.Visible;
        LauncherSlide.X = -LauncherPanelWidth;
        AnimateSlide(0);
    }

    private void CloseLauncher()
    {
        _launcherOpen = false;
        AnimateSlide(-LauncherPanelWidth, onCompleted: () =>
        {
            LauncherPanel.Visibility = Visibility.Collapsed;
            LauncherCol.Width = new GridLength(0);
            LauncherSlide.X = 0;

            // Now snap window back to remote-only.
            var size = AppWindow.Size;
            var pos  = AppWindow.Position;
            double scale = Width > 0 ? size.Width / Width : 1.0;
            int colPx = (int)Math.Round(LauncherPanelWidth * scale);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(pos.X + colPx, pos.Y, size.Width - colPx, size.Height));
        });
    }

    /// <summary>
    /// Animate the launcher panel's TranslateTransform.X — driven by the
    /// WinUI Composition layer (compositor thread, no per-frame UI work)
    /// for buttery slide. Uses Storyboard so we get proper Completed
    /// callback support, with a 220 ms cubic ease-out matching the WPF
    /// sibling.
    /// </summary>
    private void AnimateSlide(double targetX, Action? onCompleted = null)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = targetX,
            Duration = new Microsoft.UI.Xaml.Duration(LauncherAnimDuration),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, LauncherSlide);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        if (onCompleted is not null)
            sb.Completed += (_, _) => onCompleted();
        sb.Begin();
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
    /// arrives, so re-opens / scroll find everything already warmed in
    /// <see cref="AppIconCache"/>. Concurrency capped at 8 to stay polite.
    /// </summary>
    private static void PrefetchIcons(IEnumerable<string> bundleIds)
    {
        _ = Task.Run(async () =>
        {
            using var gate = new System.Threading.SemaphoreSlim(8);
            var tasks = new System.Collections.Generic.List<Task>();
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
        var filtered = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
        foreach (var kv in _cachedApps)
        {
            var name = kv.Value ?? string.Empty;
            if (q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase))
                filtered.Add(kv);
        }
        filtered.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));

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
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint   = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = top    },
                    new GradientStop { Offset = 1, Color = bottom },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };
        _ = LoadIconAsync(tile, bundleId);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 10,
            Foreground = (Brush)Root.Resources["ButtonGlyph"],
            TextAlignment  = TextAlignment.Center,
            TextWrapping   = TextWrapping.Wrap,
            TextTrimming   = TextTrimming.CharacterEllipsis,
            MaxHeight      = 26,
            Margin         = new Thickness(2, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var cell = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(cell, $"{name}\n{bundleId}");
        cell.Children.Add(tile);
        cell.Children.Add(label);
        cell.PointerReleased += async (_, _) => await LaunchAppAsync(bundleId, name);
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
            await ShowMessageAsync($"Launching {displayName} failed: {ex.Message}");
            await TeardownActiveAsync();
        }
    }

    private async Task LoadIconAsync(Border tile, string bundleId)
    {
        try
        {
            var path = await AppIconCache.GetCachedPathAsync(bundleId).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path)) return;

            // WinUI's DispatcherQueue is the equivalent of WPF's Dispatcher.
            var dq = DispatcherQueue;
            if (dq is null) return;
            dq.TryEnqueue(() =>
            {
                try
                {
                    var img = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                    tile.Background = null;
                    tile.Child = new Image
                    {
                        Source  = img,
                        Stretch = Stretch.UniformToFill,
                    };
                }
                catch { /* leave the letter placeholder visible */ }
            });
        }
        catch { /* network errors are already swallowed in AppIconCache */ }
    }

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
        return Color.FromArgb(0xFF, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
