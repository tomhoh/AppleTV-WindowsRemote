// SPDX-License-Identifier: MIT
//
// WinUI 3 mirror of AppleTVRemote.Windows/TextInputWindowManager.cs. Wires
// CompanionSession.KeyboardActiveChanged events to a floating
// TextInputWindow, positions it below the tray popup, and installs a
// low-level mouse hook so outside-app clicks dismiss both windows.

using System;
using AppleTVProtocol;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed class TextInputWindowManager : IDisposable
{
    private const int StripWidth  = 480;
    private const int StripHeight = 72;

    private readonly CompanionSession                 _session;
    private readonly Func<WindowEx?>                  _resolvePopup;
    private readonly Action                           _dismissPopup;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;
    private TextInputWindow?                          _window;
    private LowLevelMouseHook?                        _mouseHook;

    public TextInputWindowManager(CompanionSession session,
                                  Func<WindowEx?>  resolvePopup,
                                  Action           dismissPopup)
    {
        _session      = session;
        _resolvePopup = resolvePopup;
        _dismissPopup = dismissPopup;
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _session.KeyboardActiveChanged += OnKeyboardActiveChanged;
    }

    public void Dispose()
    {
        _session.KeyboardActiveChanged -= OnKeyboardActiveChanged;
        CloseWindow();
    }

    private void OnKeyboardActiveChanged(object? sender, KeyboardActiveEventArgs e)
    {
        _uiDispatcher?.TryEnqueue(() =>
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

        PositionBelowPopup(w);
        _window = w;
        w.Activate();

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

    private void PositionBelowPopup(WindowEx w)
    {
        // WinUI uses physical pixels for AppWindow.Move/Resize. Work-area
        // is fetched via DisplayArea.
        var display = DisplayArea.Primary;
        var workArea = display.WorkArea;
        int x = workArea.X + (workArea.Width - StripWidth) / 2;
        int y;
        var popup = _resolvePopup();
        if (popup is not null)
        {
            var popRect = popup.AppWindow.Position;
            var popSize = popup.AppWindow.Size;
            y = popRect.Y + popSize.Height + 20;
        }
        else
        {
            y = workArea.Y + (workArea.Height - StripHeight) / 2;
        }
        // Clamp so we don't fall off the screen.
        if (y + StripHeight > workArea.Y + workArea.Height)
            y = workArea.Y + workArea.Height - StripHeight - 20;

        w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, StripWidth, StripHeight));
    }

    private void OnGlobalMouseDown(int screenX, int screenY)
    {
        _uiDispatcher?.TryEnqueue(() =>
        {
            var strip = _window;
            var popup = _resolvePopup();
            if (strip is not null && Contains(strip, screenX, screenY)) return;
            if (popup is not null && Contains(popup, screenX, screenY)) return;

            CloseWindow();
            _dismissPopup();
        });
    }

    private static bool Contains(WindowEx window, int screenX, int screenY)
    {
        var pos = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        return screenX >= pos.X && screenX <= pos.X + size.Width
            && screenY >= pos.Y && screenY <= pos.Y + size.Height;
    }
}

/// <summary>
/// Same WH_MOUSE_LL hook as the WPF variant, kept here separately so the
/// two projects don't need to share a code file across csproj boundaries.
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private const int WH_MOUSE_LL     = 14;
    private const int WM_LBUTTONDOWN  = 0x0201;
    private const int WM_RBUTTONDOWN  = 0x0204;
    private const int WM_MBUTTONDOWN  = 0x0207;

    private readonly Action<int, int>       _onDown;
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
