using Paradise.Core.Render;
using System.Runtime.InteropServices;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Monospace-Schriftatlas für die Debug-UI, zur Laufzeit mit GDI gerendert (Windows-Systemschrift, keine Spieldaten).
/// ASCII 32..126 in einem 16x6-Raster; Zelle 127 ist komplett weiß (für Hintergrund-Rechtecke).
/// </summary>
public sealed class FontAtlas : IDisposable
{
    public const int Columns = 16, Rows = 6, FirstChar = 32;
    public int CellW { get; private set; }
    public int CellH { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IGpuTexture? Texture { get; private set; }
    public IGpuMaterial? Material { get; private set; }

    public static FontAtlas? Create(IRenderer r, string face = "Consolas", int pixelHeight = 15)
    {
        try
        {
            var a = new FontAtlas();
            var rgba = a.Render(face, pixelHeight);
            if (rgba == null) return null;
            a.Texture = r.CreateTexture(new TextureUpload
            {
                Width = a.Width,
                Height = a.Height,
                Format = GpuTextureFormat.Rgba8,
                Mips = 1,
                Blocks = rgba,
                WrapU = TextureWrap.Clamp,
                WrapV = TextureWrap.Clamp,
                Name = "DebugFont",
            });
            a.Material = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Overlay, Name = "DebugFont", Main = a.Texture });
            return a;
        }
        catch (Exception ex)
        {
            Log.Warn("Debug-UI: Schriftatlas konnte nicht erzeugt werden: " + ex.Message);
            return null;
        }
    }

    /// <summary>UV-Rechteck eines Zeichens (u0, v0, u1, v1).</summary>
    public (float u0, float v0, float u1, float v1) Uv(char c)
    {
        int i = c - FirstChar;
        if (i < 0 || i >= Columns * Rows) i = '?' - FirstChar;
        int cx = i % Columns, cy = i / Columns;
        return (cx * CellW / (float)Width, cy * CellH / (float)Height, (cx + 1) * CellW / (float)Width, (cy + 1) * CellH / (float)Height);
    }

    public (float u0, float v0, float u1, float v1) WhiteUv()
    {
        int i = 127 - FirstChar;
        int cx = i % Columns, cy = i / Columns;
        // Zellmitte (kein Filter-Übersprechen)
        float u = (cx * CellW + CellW * 0.5f) / Width, v = (cy * CellH + CellH * 0.5f) / Height;
        return (u, v, u, v);
    }

    private byte[]? Render(string face, int pixelHeight)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        IntPtr font = IntPtr.Zero, bmp = IntPtr.Zero, oldFont = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            font = CreateFontW(-pixelHeight, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4 /*ANTIALIASED_QUALITY*/, 0x31, face);
            oldFont = SelectObject(dc, font);
            GetTextExtentPoint32W(dc, "M", 1, out var sz);
            CellW = System.Math.Max(6, sz.cx + 1);
            CellH = System.Math.Max(8, sz.cy + 1);
            Width = CellW * Columns;
            Height = CellH * Rows;

            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            bmp = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return null;
            oldBmp = SelectObject(dc, bmp);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00FFFFFF);
            for (int i = 0; i < Columns * Rows; i++)
            {
                char c = (char)(FirstChar + i);
                if (c >= 127) continue;
                TextOutW(dc, (i % Columns) * CellW, (i / Columns) * CellH, c.ToString(), 1);
            }
            GdiFlush();

            var src = new byte[Width * Height * 4];
            Marshal.Copy(bits, src, 0, src.Length);
            var rgba = new byte[src.Length];
            for (int p = 0; p < Width * Height; p++)
            {
                byte b = src[p * 4], g = src[p * 4 + 1], rr = src[p * 4 + 2];
                byte a = System.Math.Max(rr, System.Math.Max(g, b));
                rgba[p * 4] = 255; rgba[p * 4 + 1] = 255; rgba[p * 4 + 2] = 255; rgba[p * 4 + 3] = a;
            }
            // Zelle 127: weiß
            int wi = 127 - FirstChar, wx = (wi % Columns) * CellW, wy = (wi / Columns) * CellH;
            for (int y = wy; y < wy + CellH; y++)
                for (int x = wx; x < wx + CellW; x++) rgba[(y * Width + x) * 4 + 3] = 255;
            return rgba;
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(dc, oldBmp);
            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (font != IntPtr.Zero) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    public void Dispose()
    {
        Material?.Dispose(); Material = null;
        Texture?.Dispose(); Texture = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        // BITMAPINFO: ein (unbenutzter) Farbtabelleneintrag folgt
        public uint bmiColors;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int n);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE size);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
