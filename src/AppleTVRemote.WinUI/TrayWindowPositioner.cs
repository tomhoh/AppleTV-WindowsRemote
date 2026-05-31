// SPDX-License-Identifier: MIT
//
// WinUI 3 version of the WPF TrayWindowPositioner — moves a WindowEx
// so it appears anchored to the tray icon (i.e. next to the current
// cursor, on the side of the screen opposite the taskbar).
//
// WinUI uses pixel coordinates throughout (no DPI-independent unit
// system like WPF), so the math is a bit simpler — but we need
// WinUIEx's AppWindow + MoveAndResize to actually reposition.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinUIEx;

namespace AppleTVRemote.WinUI;

internal static class TrayWindowPositioner
{
    public static void PositionNearCursor(WindowEx window)
    {
        if (!GetCursorPos(out var cursor)) return;
        var hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref info)) return;

        // WinUI sizes are in pixels; SetPosition takes pixels too.
        int width  = window.AppWindow.Size.Width;
        int height = window.AppWindow.Size.Height;
        const int margin = 8;

        bool tbBottom = info.rcWork.Bottom < info.rcMonitor.Bottom - 1;
        bool tbTop    = info.rcWork.Top    > info.rcMonitor.Top    + 1;
        bool tbLeft   = info.rcWork.Left   > info.rcMonitor.Left   + 1;
        bool tbRight  = info.rcWork.Right  < info.rcMonitor.Right  - 1;

        int left, top;
        if (tbBottom || (!tbTop && !tbLeft && !tbRight))
        {
            left = cursor.X - width + 16;
            top  = info.rcWork.Bottom - height - margin;
        }
        else if (tbTop)
        {
            left = cursor.X - width + 16;
            top  = info.rcWork.Top + margin;
        }
        else if (tbLeft)
        {
            left = info.rcWork.Left + margin;
            top  = cursor.Y - height + 16;
        }
        else
        {
            left = info.rcWork.Right - width - margin;
            top  = cursor.Y - height + 16;
        }

        // Clamp inside the work area.
        left = Math.Max(info.rcWork.Left + margin,
               Math.Min(left, info.rcWork.Right  - width  - margin));
        top  = Math.Max(info.rcWork.Top  + margin,
               Math.Min(top,  info.rcWork.Bottom - height - margin));

        window.AppWindow.Move(new Windows.Graphics.PointInt32(left, top));
    }

    // ─── P/Invoke ───

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
