// SPDX-License-Identifier: MIT
//
// WinUI 3 mirror of the WPF TrayIconBuilder. Builds a tray icon at
// runtime so we don't need to ship a separate .ico — H.NotifyIcon
// accepts a System.Drawing.Icon directly on its `Icon` property.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.Win32;

namespace AppleTVRemote.WinUI;

internal static class TrayIconBuilder
{
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
            if (key?.GetValue("SystemUsesLightTheme") is int v) return v == 0;
        }
        catch { }
        return true;
    }

    private static Bitmap DrawGlyph(int size, bool trayIsDark)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var fg = trayIsDark
            ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE6, 0x00, 0x00, 0x00);

        float inset  = size * 0.10f;
        float w      = size - inset * 2;
        float h      = size - inset * 2;
        float radius = Math.Max(2f, size * 0.18f);

        using var path = RoundedRect(inset, inset, w, h, radius);
        using var pen  = new Pen(fg, Math.Max(1f, size / 16f));
        g.DrawPath(pen, path);

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

    private static Icon BuildMultiResIcon(Bitmap big, Bitmap small)
    {
        byte[] bigPng;
        byte[] smallPng;
        using (var ms = new MemoryStream())
        { big.Save(ms, System.Drawing.Imaging.ImageFormat.Png); bigPng = ms.ToArray(); }
        using (var ms = new MemoryStream())
        { small.Save(ms, System.Drawing.Imaging.ImageFormat.Png); smallPng = ms.ToArray(); }

        using var ico = new MemoryStream();
        using var bw  = new BinaryWriter(ico);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)2);

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
        bw.Write((byte)(width  >= 256 ? 0 : width));
        bw.Write((byte)(height >= 256 ? 0 : height));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)payloadSize);
        bw.Write((uint)payloadOffset);
    }
}
