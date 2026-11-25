using Paradise.Core.Flash;
using System.Numerics;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Anzeige während <see cref="ShaderPrecompile"/>: schwarzer Hintergrund, Titel, schmaler Fortschrittsbalken, Zähler und Hinweis
/// (Deutsch bei Sprache „ge“, sonst Englisch). Text über Windows-GDI (Segoe UI, geglättet) in Texturen gerastert.
/// Blendet nach Abschluss aus und entfernt sich selbst.
/// </summary>
public sealed class ShaderPrecompileScreen : FlashHost.IGuiLayer
{
    public int GuiDepth => -3000;

    static ShaderPrecompileScreen? _inst;
    IGpuTexture? _white;
    readonly Dictionary<string, IGpuTexture> _texts = new();
    float _alpha = 1f, _shown = -1f, _bar;

    /// <summary>Anzeige einschalten (nur wenn wirklich übersetzt wird).</summary>
    public static void Show()
    {
        if (_inst != null) return;
        _inst = new ShaderPrecompileScreen();
        FlashHost.AddLayer(_inst);
    }

    /// <summary>Jedes Bild aufrufen; true = Anzeige noch sichtbar (Spielstart warten lassen).</summary>
    public static bool Update()
    {
        var s = _inst;
        if (s == null) return false;
        float dt = System.Math.Min(Time.unscaledDeltaTime, 0.1f);
        if (s._shown < 0f) s._shown = Time.unscaledTime;
        s._bar += (ShaderPrecompile.Progress - s._bar) * System.Math.Min(1f, dt * 8f);   // weich nachziehen
        if (ShaderPrecompile.Finished && Time.unscaledTime - s._shown > 0.6f)
        {
            s._bar = 1f;
            s._alpha -= dt / 0.4f;
            if (s._alpha <= 0f) { FlashHost.RemoveLayer(s); s.Dispose(); _inst = null; return false; }
        }
        return true;
    }

    static bool German => string.Equals(FlashHost.Language, "ge", StringComparison.OrdinalIgnoreCase);

    public void Render(IRenderer r)
    {
        float sw = Screen.width, sh = Screen.height;
        if (sw <= 0 || sh <= 0) return;
        _white ??= r.CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 255, 255, 255, 255 }, Name = "ShaderCache-Weiss", Linear = true });
        float a = System.Math.Clamp(_alpha, 0f, 1f);
        Quad(r, _white, 0, 0, sw, sh, new Vector4(0.02f, 0.02f, 0.025f, a));

        float k = sh / 1080f;
        float barW = sw * 0.36f, barH = System.Math.Max(4f, 6f * k), cx = sw * 0.5f, by = sh * 0.56f;
        string title = German ? "Shader werden vorbereitet" : "Preparing shaders";
        int pct = (int)System.MathF.Round(ShaderPrecompile.Progress * 100f);
        string count = $"{ShaderPrecompile.Completed:N0} / {System.Math.Max(ShaderPrecompile.Total, 1):N0}   ·   {pct} %";
        string hint = German ? "Nur beim ersten Start oder nach einem Update. Das dauert einen Moment."
                             : "Only on first launch or after an update. This will take a moment.";

        DrawText(r, title, (int)(40 * k), 600, cx, by - 44f * k, new Vector4(1f, 1f, 1f, a));
        // Balken: Spur + Füllung (abgerundete Enden als halbe Höhe überstehende Kappen)
        Quad(r, _white, cx - barW * 0.5f, by, barW, barH, new Vector4(1f, 1f, 1f, 0.12f * a));
        float fw = barW * System.Math.Clamp(_bar, 0f, 1f);
        if (fw > 0.5f) Quad(r, _white, cx - barW * 0.5f, by, fw, barH, new Vector4(0.96f, 0.62f, 0.16f, a));
        DrawText(r, count, (int)(22 * k), 400, cx, by + barH + 26f * k, new Vector4(0.85f, 0.85f, 0.85f, a));
        DrawText(r, hint, (int)(18 * k), 400, cx, sh - 70f * k, new Vector4(0.55f, 0.55f, 0.55f, a));
    }

    void DrawText(IRenderer r, string text, int px, int weight, float cx, float cy, Vector4 color)
    {
        px = System.Math.Max(10, px);
        string key = px + "|" + weight + "|" + text;
        if (!_texts.TryGetValue(key, out var tex))
        {
            // alte Zählerstände wegwerfen (der Text ändert sich laufend)
            if (_texts.Count > 24) { foreach (var t in _texts.Values) t.Dispose(); _texts.Clear(); }
            var img = GdiText.Render(text, px, weight, out int w, out int h);
            if (img == null) return;
            tex = r.CreateTexture(new TextureUpload { Width = w, Height = h, Format = GpuTextureFormat.Rgba8, Blocks = img, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "ShaderCache-Text", Linear = true });
            _texts[key] = tex;
        }
        Quad(r, tex, System.MathF.Round(cx - tex.Width * 0.5f), System.MathF.Round(cy - tex.Height * 0.5f), tex.Width, tex.Height, color);
    }

    static readonly ObjectVertex[] _q = new ObjectVertex[4];
    static void Quad(IRenderer r, IGpuTexture t, float x, float y, float w, float h, Vector4 mul)
    {
        _q[0] = new ObjectVertex { Position = new Vector3(x, y, 0), Uv0 = new Vector2(0, 0) };
        _q[1] = new ObjectVertex { Position = new Vector3(x + w, y, 0), Uv0 = new Vector2(1, 0) };
        _q[2] = new ObjectVertex { Position = new Vector3(x + w, y + h, 0), Uv0 = new Vector2(1, 1) };
        _q[3] = new ObjectVertex { Position = new Vector3(x, y + h, 0), Uv0 = new Vector2(0, 1) };
        var p = new FlashQuadParams { Mul = mul, Add = Vector4.Zero, Clip = new Vector4(-1e7f, -1e7f, 1e7f, 1e7f), MaskRect = Vector4.Zero, UnityGui = true };
        r.DrawFlash(_q, t, null, p);
    }

    void Dispose()
    {
        foreach (var t in _texts.Values) t.Dispose();
        _texts.Clear();
        _white?.Dispose(); _white = null;
    }

    /// <summary>Text mit Windows-GDI rastern: weiß auf schwarz, Deckung = Helligkeit -> RGBA (weiß, Alpha = Deckung).</summary>
    static class GdiText
    {
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int len, out SIZE size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string s, int len, ref RECT r, uint format);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        public static byte[]? Render(string text, int px, int weight, out int w, out int h)
        {
            w = h = 0;
            if (!OperatingSystem.IsWindows()) return null;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr font = CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
            IntPtr oldFont = SelectObject(dc, font);
            try
            {
                GetTextExtentPoint32W(dc, text, text.Length, out var sz);
                w = System.Math.Max(1, sz.cx + 4); h = System.Math.Max(1, sz.cy + 2);
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) return null;
                IntPtr oldBmp = SelectObject(dc, bmp);
                SetBkMode(dc, 1);
                SetTextColor(dc, 0x00FFFFFF);
                var rc = new RECT { left = 0, top = 0, right = w, bottom = h };
                DrawTextW(dc, text, -1, ref rc, 0x1 | 0x4 | 0x20 | 0x800);   // CENTER | VCENTER | SINGLELINE | NOPREFIX
                GdiFlush();
                var bgra = new byte[w * h * 4];
                Marshal.Copy(bits, bgra, 0, bgra.Length);
                SelectObject(dc, oldBmp);
                DeleteObject(bmp);
                var rgba = new byte[bgra.Length];
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    byte cov = System.Math.Max(bgra[i], System.Math.Max(bgra[i + 1], bgra[i + 2]));
                    rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255; rgba[i + 3] = cov;
                }
                return rgba;
            }
            finally
            {
                SelectObject(dc, oldFont);
                DeleteObject(font);
                DeleteDC(dc);
            }
        }
    }
}
