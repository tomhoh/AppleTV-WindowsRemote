// SPDX-License-Identifier: MIT
//
// Position a WPF window so it appears to "drop out" of the tray icon —
// the visual equivalent of macOS's menu-bar popover.
//
// Approach: grab the cursor position (where the user just clicked the
// tray icon), figure out which monitor that's on, locate the taskbar
// edge, and place the window on the side of the cursor opposite the
// taskbar. So if the taskbar is at the bottom of the screen (the
// default), the window's bottom-right corner lines up just above and
// near the cursor.
//
// Why cursor and not real tray-icon rect? Because the cursor at the
// moment of click is essentially the icon's location, and grabbing the
// actual rect via Shell_NotifyIconGetRect requires the icon's NOTIFYICONDATA
// id (we'd have to bypass Hardcodet's wrapper). Cursor is good enough
// and feels exactly right in practice.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AppleTVRemote.Windows;

internal static class TrayWindowPositioner
{
    /// <summary>
    /// Move <paramref name="window"/> so it appears anchored to the
    /// tray-icon region the user just clicked (i.e. adjacent to the
    /// current mouse cursor, on the appropriate side of the taskbar).
    /// </summary>
    public static void PositionNearCursor(Window window)
    {
        if (!GetCursorPos(out var cursor)) return;

        // Get the work area of the monitor containing the cursor.
        // Work area excludes the taskbar — its boundary tells us which
        // edge the taskbar is on.
        var hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref info)) return;

        // Convert pixel coordinates to WPF device-independent units.
        var source = PresentationSource.FromVisual(window) ?? new HwndSource(new HwndSourceParameters());
        var matrix = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var dipCursor   = matrix.Transform(new Point(cursor.X, cursor.Y));
        var dipWorkArea = new Rect(
            matrix.Transform(new Point(info.rcWork.Left,  info.rcWork.Top)),
            matrix.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
        var dipMonitor  = new Rect(
            matrix.Transform(new Point(info.rcMonitor.Left,  info.rcMonitor.Top)),
            matrix.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom)));

        double margin = 8;   // gap between window edge and tray icon

        // Detect taskbar edge by comparing work area to full monitor.
        bool taskbarBottom = dipWorkArea.Bottom < dipMonitor.Bottom - 0.5;
        bool taskbarTop    = dipWorkArea.Top    > dipMonitor.Top    + 0.5;
        bool taskbarLeft   = dipWorkArea.Left   > dipMonitor.Left   + 0.5;
        bool taskbarRight  = dipWorkArea.Right  < dipMonitor.Right  - 0.5;

        double left, top;
        if (taskbarBottom || (!taskbarTop && !taskbarLeft && !taskbarRight))
        {
            // Most common: taskbar bottom. Anchor window's bottom-right
            // to just above the cursor.
            left = dipCursor.X - window.Width + 16;
            top  = dipWorkArea.Bottom - window.Height - margin;
        }
        else if (taskbarTop)
        {
            // Taskbar at top → drop down from below the cursor.
            left = dipCursor.X - window.Width + 16;
            top  = dipWorkArea.Top + margin;
        }
        else if (taskbarLeft)
        {
            // Vertical taskbar on left → window to the right of cursor.
            left = dipWorkArea.Left + margin;
            top  = dipCursor.Y - window.Height + 16;
        }
        else
        {
            // Vertical taskbar on right → window to the left of cursor.
            left = dipWorkArea.Right - window.Width - margin;
            top  = dipCursor.Y - window.Height + 16;
        }

        // Final clamp inside the work area so we don't render off-screen.
        left = Math.Max(dipWorkArea.Left + margin,
               Math.Min(left, dipWorkArea.Right  - window.Width  - margin));
        top  = Math.Max(dipWorkArea.Top  + margin,
               Math.Min(top,  dipWorkArea.Bottom - window.Height - margin));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top  = top;
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
