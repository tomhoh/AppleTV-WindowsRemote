// SPDX-License-Identifier: MIT
//
// Bridges CompanionSession.KeyboardActiveChanged events to a floating
// TextInputWindow. Direct port of the Mac sibling's TextInputWindow.swift
// manager class.
//
// Ownership: App.xaml.cs constructs a single instance per session and
// hooks it up when the popup opens a connection. The manager owns the
// (transient) TextInputWindow itself, positions it screen-centered
// (macOS-style), and installs a global mouse-hook so an outside-app
// click dismisses both the strip and the popup.

using System;
using System.Windows;
using System.Windows.Threading;
using AppleTVProtocol;

namespace AppleTVRemote.Windows;

/// <summary>
/// Owns the transient <see cref="TextInputWindow"/> and wires its
/// lifecycle to <see cref="CompanionSession.KeyboardActiveChanged"/>.
/// </summary>
public sealed class TextInputWindowManager : IDisposable
{
    private readonly CompanionSession        _session;
    private readonly Func<Window?>           _resolvePopup;
    private readonly Action                  _dismissPopup;
    private readonly Dispatcher              _uiDispatcher;
    private TextInputWindow?                 _window;
    private LowLevelMouseHook?               _mouseHook;

    /// <param name="session">The live Companion session this manager listens to.</param>
    /// <param name="resolvePopup">Returns the tray-anchored popup window
    ///   if it's currently visible, or null. Used to position the input
    ///   strip below it.</param>
    /// <param name="dismissPopup">Called when a click outside our app
    ///   dismisses the strip — the popup should go away too.</param>
    public TextInputWindowManager(CompanionSession session,
                                  Func<Window?>    resolvePopup,
                                  Action           dismissPopup)
    {
        _session      = session;
        _resolvePopup = resolvePopup;
        _dismissPopup = dismissPopup;
        _uiDispatcher = Application.Current.Dispatcher;
        _session.KeyboardActiveChanged += OnKeyboardActiveChanged;
    }

    public void Dispose()
    {
        _session.KeyboardActiveChanged -= OnKeyboardActiveChanged;
        CloseWindow();
    }

    private void OnKeyboardActiveChanged(object? sender, KeyboardActiveEventArgs e)
    {
        _uiDispatcher.Invoke(() =>
        {
            if (e.Active) OpenWindow();
            else          CloseWindow();
        });
    }

    private void OpenWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var w = new TextInputWindow(_session, CloseWindow);
        w.Closed += (_, _) =>
        {
            _mouseHook?.Dispose();
            _mouseHook = null;
            _window = null;
        };

        PositionCentered(w);
        _window = w;
        w.Show();
        w.Activate();

        // Global low-level mouse hook: any click that lands outside our
        // strip AND outside the popup should dismiss both.
        _mouseHook = new LowLevelMouseHook(OnGlobalMouseDown);
    }

    private void CloseWindow()
    {
        if (_window is { } w)
        {
            _window = null;
            try { w.Close(); } catch { }
        }
        _mouseHook?.Dispose();
        _mouseHook = null;
    }

    private void PositionCentered(Window w)
    {
        // Center the pill on the primary display's work area, slightly
        // above vertical center for an "attention" feel (macOS Spotlight
        // sits around 35% from the top). WPF Window.Left/Top are DIPs,
        // matching SystemParameters.WorkArea's units.
        var workArea = SystemParameters.WorkArea;
        w.Left = workArea.Left + (workArea.Width  - w.Width)  / 2;
        w.Top  = workArea.Top  + (workArea.Height - w.Height) * 0.35;
    }

    private void OnGlobalMouseDown(int screenX, int screenY)
    {
        // Marshal onto UI thread; hook callbacks fire on the hook thread.
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            var strip = _window;
            var popup = _resolvePopup();
            if (strip is not null && Contains(strip, screenX, screenY)) return;
            if (popup is not null && Contains(popup, screenX, screenY)) return;

            // Click outside both windows → dismiss.
            CloseWindow();
            _dismissPopup();
        }));
    }

    private static bool Contains(Window window, int screenX, int screenY)
    {
        // Window.Left/Top/Width/Height are in DIPs; the hook gives us
        // physical pixels. Convert via PresentationSource.
        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null) return false;
        var topLeft = transform.Value.Transform(new System.Windows.Point(screenX, screenY));
        return topLeft.X >= window.Left && topLeft.X <= window.Left + window.Width
            && topLeft.Y >= window.Top  && topLeft.Y <= window.Top  + window.Height;
    }
}

// ─── Low-level Win32 mouse hook ────────────────────────────────────────

/// <summary>
/// Installs a WH_MOUSE_LL hook to observe mouse-down events anywhere on
/// screen — including clicks in other apps. Used to auto-dismiss the
/// input strip when the user clicks outside our windows.
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private const int WH_MOUSE_LL     = 14;
    private const int WM_LBUTTONDOWN  = 0x0201;
    private const int WM_RBUTTONDOWN  = 0x0204;
    private const int WM_MBUTTONDOWN  = 0x0207;

    private readonly Action<int, int> _onDown;
    private readonly NativeMethods.HookProc _proc;
    private IntPtr _hookHandle;

    public LowLevelMouseHook(Action<int, int> onDown)
    {
        _onDown = onDown;
        _proc = HookCallback;
        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _proc, moduleHandle, 0);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                var mhs = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                _onDown(mhs.pt.x, mhs.pt.y);
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static class NativeMethods
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint  mouseData;
            public uint  flags;
            public uint  time;
            public IntPtr dwExtraInfo;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
