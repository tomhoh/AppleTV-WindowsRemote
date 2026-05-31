// SPDX-License-Identifier: MIT
//
// Builds a tray icon at runtime so we don't have to ship a separate
// .ico file alongside the EXE / MSIX. Draws a small rounded "screen"
// rectangle that adapts to the system light/dark tray colour — close
// enough to look intentional, not so detailed that it pixelates.
//
// We render twice (32 px and 16 px) and pack both into a multi-resolution
// .ico via Icon.FromHandle on a hand-built ICO blob. That gives the
// shell crisp icons on both 100% and HiDPI tray scales.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.Win32;

namespace AppleTVRemote.Windows;

internal static class TrayIconBuilder
{
    /// <summary>
    /// Build a tray icon. The colour adapts to the current Windows
    /// tray ("system") theme — light theme paints the glyph dark,
    /// dark theme paints it light.
    /// </summary>
    public static Icon Build()
    {
        bool trayIsDark = IsSystemTrayDark();
        var bmp32 = DrawGlyph(32, trayIsDark);
        var bmp16 = DrawGlyph(16, trayIsDark);
        return BuildMultiResIcon(bmp32, bmp16);
    }

    private static bool IsSystemTrayDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme controls the taskbar / tray; 0 = dark.
            if (key?.GetValue("SystemUsesLightTheme") is int v) return v == 0;
        }
        catch { }
        return true;   // default to dark taskbar (Win11 default)
    }

    /// <summary>
    /// Hand-draw the glyph: a rounded screen rect with a small dot —
    /// a simple "remote-pointing-at-TV" suggestion at small sizes.
    /// </summary>
    private static Bitmap DrawGlyph(int size, bool trayIsDark)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Glyph colour: light on dark taskbars, dark on light taskbars.
        var fg = trayIsDark
            ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE6, 0x00, 0x00, 0x00);

        // Inset slightly so the rounded rect doesn't kiss the edges.
        float inset = size * 0.10f;
        float w     = size - inset * 2;
        float h     = size - inset * 2;
        float radius = Math.Max(2f, size * 0.18f);

        using var path = RoundedRect(inset, inset, w, h, radius);
        using var pen  = new Pen(fg, Math.Max(1f, size / 16f));
        g.DrawPath(pen, path);

        // Small center dot — "select" button.
        float dotSize = Math.Max(2f, size * 0.18f);
        using var dotBrush = new SolidBrush(fg);
        g.FillEllipse(dotBrush,
            (size - dotSize) / 2f,
            (size - dotSize) / 2f,
            dotSize, dotSize);

        return bmp;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x,           y,           r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r*2, y,           r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r*2, y + h - r*2, r * 2, r * 2,   0, 90);
        p.AddArc(x,           y + h - r*2, r * 2, r * 2,  90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// Pack <paramref name="big"/> + <paramref name="small"/> into a
    /// minimal multi-resolution .ico stream and instantiate an Icon
    /// over it. Yields crisp tray icons at both 100% and HiDPI.
    /// </summary>
    private static Icon BuildMultiResIcon(Bitmap big, Bitmap small)
    {
        // Encode each bitmap as PNG (the modern ICO format allows PNG payloads).
        byte[] bigPng;
        byte[] smallPng;
        using (var ms = new MemoryStream())
        {
            big.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            bigPng = ms.ToArray();
        }
        using (var ms = new MemoryStream())
        {
            small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            smallPng = ms.ToArray();
        }

        using var ico = new MemoryStream();
        using var bw  = new BinaryWriter(ico);
        // ICONDIR
        bw.Write((ushort)0);   // reserved
        bw.Write((ushort)1);   // type = icon
        bw.Write((ushort)2);   // image count

        // Two ICONDIRENTRYs (16 bytes each), then both PNG payloads.
        const int headerSize = 6;
        const int entrySize  = 16;
        int offset1 = headerSize + entrySize * 2;
        int offset2 = offset1 + smallPng.Length;

        WriteEntry(bw, small.Width, small.Height, smallPng.Length, offset1);
        WriteEntry(bw, big.Width,   big.Height,   bigPng.Length,   offset2);
        bw.Write(smallPng);
        bw.Write(bigPng);

        ico.Position = 0;
        return new Icon(ico);
    }

    private static void WriteEntry(BinaryWriter bw, int width, int height, int payloadSize, int payloadOffset)
    {
        bw.Write((byte)(width  >= 256 ? 0 : width));   // ICO uses 0 for 256
        bw.Write((byte)(height >= 256 ? 0 : height));
        bw.Write((byte)0);    // palette count (0 = none)
        bw.Write((byte)0);    // reserved
        bw.Write((ushort)1);  // color planes
        bw.Write((ushort)32); // bits per pixel
        bw.Write((uint)payloadSize);
        bw.Write((uint)payloadOffset);
    }
}
