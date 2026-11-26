using Paradise.Core.Render;
using System.Runtime.InteropServices;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Dynamically generated proportional font atlas using Windows GDI.
/// Supports ASCII, Unicode arrows and additional symbols.
/// Glyphs use individual advance widths instead of fixed-width cells.
/// </summary>
public sealed class FontAtlas : IDisposable
{
    private const int Columns = 16;
    private const int Padding = 3;

    private static readonly char[] Characters = BuildCharacterSet();

    public int CellW { get; private set; }
    public int CellH { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public int LineHeight { get; private set; }

    public IGpuTexture? Texture { get; private set; }
    public IGpuMaterial? Material { get; private set; }

    private readonly Dictionary<char, Glyph> _glyphs = new();

    public readonly struct Glyph
    {
        public readonly float U0, V0, U1, V1;
        public readonly float Width;
        public readonly float Height;
        public readonly float Advance;

        public Glyph(float u0, float v0, float u1, float v1, float width, float height, float advance)
        {
            U0 = u0;
            V0 = v0;
            U1 = u1;
            V1 = v1;
            Width = width;
            Height = height;
            Advance = advance;
        }
    }

    private static char[] BuildCharacterSet()
    {
        const string extra =
            "←↑→↓↖↗↘↙↔↕" +
            "⇐⇑⇒⇓⇔⇕" +
            "▲▼◀▶◆◇" +
            "●○■□" +
            "✓✗⚠" +
            "°±×÷≤≥" +
            "…·•";

        return (Enumerable.Range(32, 95).Select(x => (char)x).Concat(extra).Distinct().ToArray());
    }

    public static FontAtlas? Create(IRenderer renderer, string face = "Segoe UI", int pixelHeight = 16, int weight = 400)
    {
        try
        {
            var atlas = new FontAtlas();
            var rgba = atlas.Render(face, pixelHeight, weight);

            if (rgba == null)
                return null;

            atlas.Texture = renderer.CreateTexture(new TextureUpload
            {
                Width = atlas.Width,
                Height = atlas.Height,
                Format = GpuTextureFormat.Rgba8,
                Mips = 1,
                Blocks = rgba,
                WrapU = TextureWrap.Clamp,
                WrapV = TextureWrap.Clamp,
                Name = "DebugFont"
            });

            atlas.Material = renderer.CreateMaterial(
                new MaterialDesc
                {
                    Kind = MaterialKind.Overlay,
                    Name = "DebugFont",
                    Main = atlas.Texture
                });

            return atlas;
        }
        catch (Exception ex)
        {
            Log.Error("Debug overlay font creation failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns glyph information for a character.
    /// Unsupported characters fall back to '?'.
    /// </summary>
    public Glyph GetGlyph(char c)
    {
        if (!_glyphs.TryGetValue(c, out var glyph))
            glyph = _glyphs['?'];

        return glyph;
    }

    /// <summary>
    /// Returns the UV coordinates of a glyph.
    /// </summary>
    public (float u0, float v0, float u1, float v1) Uv(char c)
    {
        var g = GetGlyph(c);
        return (g.U0, g.V0, g.U1, g.V1);
    }

    /// <summary>
    /// Returns the center UV coordinate of a white atlas pixel.
    /// Used for overlay background rectangles.
    /// </summary>
    public (float u0, float v0, float u1, float v1) WhiteUv()
    {
        // WICHTIG: Texelzentrum, nicht der Rand! Der Sampler ist linear (GetSampler):
        // (Width-1)/Width läge genau auf der Grenze zum Nachbartexel (Alpha 0) und
        // würde die weiße Zelle zur Hälfte ausblenden -> alle Panel-Flächen werden
        // halbtransparent, obwohl ihre Vertexfarbe Alpha 255 hat.
        float u = (Width - 0.5f) / Width;
        float v = (Height - 0.5f) / Height;

        return (u, v, u, v);
    }

    /// <summary>
    /// Measures a complete string using proportional glyph widths.
    /// </summary>
    public float Measure(string text, float scale = 1f)
    {
        float width = 0f;

        foreach (char c in text)
            width += GetGlyph(c).Advance * scale;

        return width;
    }

    private byte[]? Render(string face, int pixelHeight, int weight)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);

        if (dc == IntPtr.Zero)
            return null;

        IntPtr font = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;

        IntPtr oldFont = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            font = CreateFontW(-pixelHeight, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0x31, face);

            if (font == IntPtr.Zero)
                return null;

            oldFont = SelectObject(dc, font);

            if (!GetTextMetricsW(dc, out var metrics))
                return null;

            LineHeight = metrics.tmHeight + 2;

            // Determine maximum glyph width.
            int maxWidth = 1;

            foreach (char c in Characters)
            {
                char actual = HasGlyph(dc, c) ? c : '?';

                GetTextExtentPoint32W(dc, actual.ToString(), 1, out var size);

                maxWidth = System.Math.Max(maxWidth, size.cx);
            }

            CellW = maxWidth + Padding * 2;
            CellH = LineHeight + Padding * 2;

            int rows = (Characters.Length + Columns - 1) / Columns;

            Width = CellW * Columns;
            Height = CellH * rows;

            var bitmapInfo = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            bitmap = CreateDIBSection(dc, ref bitmapInfo, 0, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                return null;

            oldBitmap = SelectObject(dc, bitmap);

            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00FFFFFF);

            var src = new byte[Width * Height * 4];

            // Render all glyphs into individual atlas cells.
            for (int i = 0; i < Characters.Length; i++)
            {
                char c = Characters[i];

                char actual = HasGlyph(dc, c) ? c : '?';

                GetTextExtentPoint32W(dc, actual.ToString(), 1, out var size);

                int x = (i % Columns) * CellW;
                int y = (i / Columns) * CellH;

                int glyphWidth = System.Math.Max(1, size.cx);

                TextOutW(dc, x + Padding, y + Padding, actual.ToString(), 1);

                float u0 = (x + Padding) / (float)Width;
                float v0 = (y + Padding) / (float)Height;

                float u1 = (x + Padding + glyphWidth) / (float)Width;
                float v1 = (y + Padding + LineHeight) / (float)Height;

                float advance = size.cx;

                // Store only the first occurrence of a character.
                if (!_glyphs.ContainsKey(c))
                {
                    _glyphs[c] = new Glyph(u0, v0, u1, v1, glyphWidth, LineHeight, advance);
                }
            }

            GdiFlush();

            Marshal.Copy(bits, src, 0, src.Length);

            var rgba = new byte[src.Length];

            // Extract antialiased glyph coverage from GDI RGB.
            for (int p = 0; p < Width * Height; p++)
            {
                byte b = src[p * 4];
                byte g = src[p * 4 + 1];
                byte r = src[p * 4 + 2];

                byte alpha = System.Math.Max(r, System.Math.Max(g, b));

                rgba[p * 4] = 255;
                rgba[p * 4 + 1] = 255;
                rgba[p * 4 + 2] = 255;
                rgba[p * 4 + 3] = alpha;
            }

            // Dedicated white pixel for background rectangles.
            int white = (Height - 1) * Width + (Width - 1);

            rgba[white * 4] = 255;
            rgba[white * 4 + 1] = 255;
            rgba[white * 4 + 2] = 255;
            rgba[white * 4 + 3] = 255;

            return rgba;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(dc, oldBitmap);

            if (oldFont != IntPtr.Zero)
                SelectObject(dc, oldFont);

            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);

            if (font != IntPtr.Zero)
                DeleteObject(font);

            DeleteDC(dc);
        }
    }

    private static bool HasGlyph(IntPtr dc, char c)
    {
        if (c < 32)
            return false;

        uint result = GetGlyphIndicesW(dc, c.ToString(), 1, out ushort glyph, 1);

        return result != 0xFFFFFFFF && glyph != 0xFFFF;
    }

    public void Dispose()
    {
        Material?.Dispose();
        Material = null;

        Texture?.Dispose();
        Texture = null;

        _glyphs.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TEXTMETRIC
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;

        public ushort tmFirstChar;
        public ushort tmLastChar;
        public ushort tmDefaultChar;
        public ushort tmBreakChar;

        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int n);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, out ushort glyphIndices, uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}

#region Old Font Backup
//using Paradise.Core.Render;
//using System.Runtime.InteropServices;

//namespace Paradise.Core.Diagnostics;

///// <summary>
///// Monospace-Schriftatlas für die Debug-UI, zur Laufzeit mit GDI gerendert (Windows-Systemschrift, keine Spieldaten).
///// ASCII 32..126 in einem 16x6-Raster; Zelle 127 ist komplett weiß (für Hintergrund-Rechtecke).
///// </summary>
//public sealed class FontAtlas : IDisposable
//{
//    public const int Columns = 16, Rows = 6, FirstChar = 32;
//    public int CellW { get; private set; }
//    public int CellH { get; private set; }
//    public int Width { get; private set; }
//    public int Height { get; private set; }
//    public IGpuTexture? Texture { get; private set; }
//    public IGpuMaterial? Material { get; private set; }

//    public static FontAtlas? Create(IRenderer r, string face = "Consolas", int pixelHeight = 15)
//    {
//        try
//        {
//            var a = new FontAtlas();
//            var rgba = a.Render(face, pixelHeight);
//            if (rgba == null) return null;
//            a.Texture = r.CreateTexture(new TextureUpload
//            {
//                Width = a.Width,
//                Height = a.Height,
//                Format = GpuTextureFormat.Rgba8,
//                Mips = 1,
//                Blocks = rgba,
//                WrapU = TextureWrap.Clamp,
//                WrapV = TextureWrap.Clamp,
//                Name = "DebugFont",
//            });
//            a.Material = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Overlay, Name = "DebugFont", Main = a.Texture });
//            return a;
//        }
//        catch (Exception ex)
//        {
//            Log.Warn("Debug-UI: Schriftatlas konnte nicht erzeugt werden: " + ex.Message);
//            return null;
//        }
//    }

//    /// <summary>UV-Rechteck eines Zeichens (u0, v0, u1, v1).</summary>
//    public (float u0, float v0, float u1, float v1) Uv(char c)
//    {
//        int i = c - FirstChar;
//        if (i < 0 || i >= Columns * Rows) i = '?' - FirstChar;
//        int cx = i % Columns, cy = i / Columns;
//        return (cx * CellW / (float)Width, cy * CellH / (float)Height, (cx + 1) * CellW / (float)Width, (cy + 1) * CellH / (float)Height);
//    }

//    public (float u0, float v0, float u1, float v1) WhiteUv()
//    {
//        int i = 127 - FirstChar;
//        int cx = i % Columns, cy = i / Columns;
//        // Zellmitte (kein Filter-Übersprechen)
//        float u = (cx * CellW + CellW * 0.5f) / Width, v = (cy * CellH + CellH * 0.5f) / Height;
//        return (u, v, u, v);
//    }

//    private byte[]? Render(string face, int pixelHeight)
//    {
//        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
//        if (dc == IntPtr.Zero) return null;
//        IntPtr font = IntPtr.Zero, bmp = IntPtr.Zero, oldFont = IntPtr.Zero, oldBmp = IntPtr.Zero;
//        try
//        {
//            font = CreateFontW(-pixelHeight, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4 /*ANTIALIASED_QUALITY*/, 0x31, face);
//            oldFont = SelectObject(dc, font);
//            GetTextExtentPoint32W(dc, "M", 1, out var sz);
//            CellW = System.Math.Max(6, sz.cx + 1);
//            CellH = System.Math.Max(8, sz.cy + 1);
//            Width = CellW * Columns;
//            Height = CellH * Rows;

//            var bi = new BITMAPINFOHEADER
//            {
//                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
//                biWidth = Width,
//                biHeight = -Height,
//                biPlanes = 1,
//                biBitCount = 32,
//                biCompression = 0,
//            };
//            bmp = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
//            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return null;
//            oldBmp = SelectObject(dc, bmp);
//            SetBkMode(dc, 1);
//            SetTextColor(dc, 0x00FFFFFF);
//            for (int i = 0; i < Columns * Rows; i++)
//            {
//                char c = (char)(FirstChar + i);
//                if (c >= 127) continue;
//                TextOutW(dc, (i % Columns) * CellW, (i / Columns) * CellH, c.ToString(), 1);
//            }
//            GdiFlush();

//            var src = new byte[Width * Height * 4];
//            Marshal.Copy(bits, src, 0, src.Length);
//            var rgba = new byte[src.Length];
//            for (int p = 0; p < Width * Height; p++)
//            {
//                byte b = src[p * 4], g = src[p * 4 + 1], rr = src[p * 4 + 2];
//                byte a = System.Math.Max(rr, System.Math.Max(g, b));
//                rgba[p * 4] = 255; rgba[p * 4 + 1] = 255; rgba[p * 4 + 2] = 255; rgba[p * 4 + 3] = a;
//            }
//            // Zelle 127: weiß
//            int wi = 127 - FirstChar, wx = (wi % Columns) * CellW, wy = (wi / Columns) * CellH;
//            for (int y = wy; y < wy + CellH; y++)
//                for (int x = wx; x < wx + CellW; x++) rgba[(y * Width + x) * 4 + 3] = 255;
//            return rgba;
//        }
//        finally
//        {
//            if (oldBmp != IntPtr.Zero) SelectObject(dc, oldBmp);
//            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
//            if (bmp != IntPtr.Zero) DeleteObject(bmp);
//            if (font != IntPtr.Zero) DeleteObject(font);
//            DeleteDC(dc);
//        }
//    }

//    public void Dispose()
//    {
//        Material?.Dispose(); Material = null;
//        Texture?.Dispose(); Texture = null;
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private struct BITMAPINFOHEADER
//    {
//        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
//        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
//        // BITMAPINFO: ein (unbenutzter) Farbtabelleneintrag folgt
//        public uint bmiColors;
//    }
//    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

//    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
//    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
//    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
//    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
//    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
//    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
//    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
//    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
//    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
//    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int n);
//    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE size);
//    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
//}
#endregion