using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.FlashDebug;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.World;
using System.Globalization;
using System.Text;

namespace Paradise.Game.Screens;

public sealed partial class DevMenu : GameModule
{
    const int VK_F1 = 0x70, VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27;
    const int VK_PGUP = 0x21, VK_PGDN = 0x22, VK_HOME = 0x24, VK_END = 0x23;
    const int VK_DELETE = 0x2E, VK_ENTER = 0x0D, VK_BACK = 0x08, VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    static readonly uint CYellow = ObjectVertex.PackColor(255, 230, 51, 255);
    static readonly uint CCyan = ObjectVertex.PackColor(77, 242, 255, 255);
    static readonly uint CGreen = ObjectVertex.PackColor(115, 255, 115, 255);
    static readonly uint CPurple = ObjectVertex.PackColor(217, 140, 255, 255);
    static readonly uint CBlue = ObjectVertex.PackColor(140, 179, 255, 255);
    static readonly uint CWhite = ObjectVertex.PackColor(235, 235, 235, 255);
    static readonly uint CGray = ObjectVertex.PackColor(150, 150, 150, 255);
    static readonly uint CBack = ObjectVertex.PackColor(0, 0, 0, 128);
    static readonly uint CPanel = ObjectVertex.PackColor(0, 0, 0, 184);
    static readonly uint COrange = ObjectVertex.PackColor(255, 170, 60, 255);

    sealed class Item
    {
        public string Label = "";
        public Func<string>? Value;
        public Action<int>? Change;
        public Action? Reset;
        public Action? Activate;
        public Func<List<Item>>? Sub;
        public bool Header;
        public bool Placeholder;
    }

    sealed class Page
    {
        public string Title = "";
        public Func<List<Item>> Build = () => new List<Item>();
        public int Sel, Top;
    }

    readonly IRenderer _r;
    readonly IGameWindow _window;
    readonly TerrainModule _terrain;
    readonly VehicleModule _vehicles;
    readonly LaunchOptions _opts;

    readonly List<Page> _stack = new();
    List<Item> _items = new();
    bool _open, _blockedByMe;
    /// <summary>Toggle Control: Menü bleibt sichtbar, Tastatur geht ans Spiel (Auto fahren). Umschalten mit Taste "dev_menuctrl" (Tab).</summary>
    bool _drive;
    double _time, _repeatAt;
    int _repeatKey;
    string? _toast;
    double _toastUntil;

    readonly List<ObjectVertex> _v = new(8192);
    readonly List<uint> _i = new(12288);
    FontAtlas? _font;
    bool _fontFailed;

    readonly Dictionary<string, object> _ph = new();

    float _fps, _ftAvg, _ftMin, _ftMax;
    int _sampleFrames;
    float _sampleTime, _sampleMin = float.MaxValue, _sampleMax;

    public DevMenu(IRenderer renderer, TerrainModule terrain, VehicleModule vehicles, IGameWindow window, LaunchOptions opts)
    {
        _r = renderer; _terrain = terrain; _vehicles = vehicles; _window = window; _opts = opts;
    }

    public bool IsOpen => _open;

    public override void Update(in GameTime time)
    {
        _time = time.TotalTime;
#if WINDOWS
        FlashBrowser.Pump();   // Flash-Browser (F1 > Flash Debug > Browse Flash) – auch bei geschlossenem Menü aktiv
#endif
        SampleFrame(time.DeltaTime);
        CollectCarDebug();   // Radstrahlen/Kräfte am Auto (Car > Physics debug), auch bei geschlossenem Menü
        CollectRaceDebug();  // gewählte Strecke (Race Debug), auch bei geschlossenem Menü
        UpdateTools(time.DeltaTime);   // Cutscene Tester / Animation Viewer

        if (TDU2Controls.RawKeyPressed(VK_F1))
        {
            if (_open) Close(); else Open();
            return;
        }
        if (!_open) return;

        if (ToggleKeyPressed()) SetDrive(!_drive);
        if (_drive)
        {
            // Steuerung beim Spiel: Menü nur anzeigen, keine Navigation
            if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
            Rebuild();
            return;
        }
        if (!TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }

        Rebuild();
        var p = _stack[^1];
        bool big = TDU2Controls.RawKeyDown(VK_SHIFT) || TDU2Controls.RawKeyDown(VK_LSHIFT) || TDU2Controls.RawKeyDown(VK_RSHIFT);
        if (KeyRepeat(VK_DOWN)) Move(p, 1);
        if (KeyRepeat(VK_UP)) Move(p, -1);
        if (KeyRepeat(VK_PGDN)) Move(p, 10);
        if (KeyRepeat(VK_PGUP)) Move(p, -10);
        if (TDU2Controls.RawKeyPressed(VK_HOME)) { p.Sel = 0; Move(p, 0); }
        if (TDU2Controls.RawKeyPressed(VK_END)) { p.Sel = _items.Count - 1; Move(p, 0); }
        var it = p.Sel >= 0 && p.Sel < _items.Count ? _items[p.Sel] : null;
        if (it != null && !it.Header)
        {
            if (KeyRepeat(VK_RIGHT)) it.Change?.Invoke(big ? 10 : 1);
            if (KeyRepeat(VK_LEFT)) it.Change?.Invoke(big ? -10 : -1);
            if (TDU2Controls.RawKeyPressed(VK_DELETE)) it.Reset?.Invoke();
            if (TDU2Controls.RawKeyPressed(VK_ENTER))
            {
                if (it.Sub != null) _stack.Add(new Page { Title = it.Label, Build = it.Sub });
                else if (it.Activate != null) it.Activate();
                else it.Change?.Invoke(1);
            }
        }
        if (TDU2Controls.RawKeyPressed(VK_BACK))
        {
            if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); else Close();
        }
    }

    /// <summary>Taste der Aktion "dev_menuctrl" (Game/Config/KeyConfig.cs, Standard Tab) – auch während das Menü sperrt.</summary>
    static bool ToggleKeyPressed()
    {
        var a = TDU2Controls.Get("dev_menuctrl");
        return a != null && (TDU2Controls.RawKeyPressed(a.Key1) || TDU2Controls.RawKeyPressed(a.Key2));
    }

    static string ToggleKeyName()
    {
        var a = TDU2Controls.Get("dev_menuctrl");
        return a != null && a.Key1 > 0 ? TDU2Controls.KeyName(a.Key1) : "-";
    }

    void SetDrive(bool on)
    {
        _drive = on;
        if (on) { if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; } }
        else if (_open && !TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
        Toast(on ? "Toggle Control: driving (menu stays open, " + ToggleKeyName() + " = back to the menu)" : "Toggle Control: menu navigation");
    }

    bool KeyRepeat(int vk)
    {
        if (TDU2Controls.RawKeyPressed(vk)) { _repeatKey = vk; _repeatAt = _time + 0.4; return true; }
        if (_repeatKey == vk && TDU2Controls.RawKeyDown(vk) && _time >= _repeatAt) { _repeatAt = _time + 0.06; return true; }
        return false;
    }

    void Move(Page p, int d)
    {
        if (_items.Count == 0) { p.Sel = 0; return; }
        int s = Math.Clamp(p.Sel + d, 0, _items.Count - 1);
        int dir = d >= 0 ? 1 : -1;
        while (s >= 0 && s < _items.Count && _items[s].Header) s += dir;
        if (s < 0 || s >= _items.Count) s = Math.Clamp(p.Sel, 0, _items.Count - 1);
        p.Sel = s;
    }

    void Open()
    {
        _open = true;
        _stack.Clear();
        _stack.Add(new Page { Title = "DEVELOPER MENU build: " + AppConfig.EngineVersion, Build = MainMenu });
        if (!_drive && !TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
        Rebuild(); Move(_stack[0], 0);
    }

    void Close()
    {
        _open = false;
        if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
    }

    void Rebuild()
    {
        var p = _stack[^1];
        try { _items = p.Build() ?? new List<Item>(); }
        catch (Exception ex) { _items = new List<Item> { Info("Error: " + ex.Message) }; }
        if (p.Sel >= _items.Count) p.Sel = Math.Max(0, _items.Count - 1);
        if (_items.Count > 0 && _items[p.Sel].Header) Move(p, 1);
    }

    void Toast(string s)
    {
        _toast = s; _toastUntil = _time + 3.0;
        Log.Info("[DevMenu] " + s);
    }

    public override void Unload()
    {
        CutsceneTester.Instance?.Unload(true);
        AnimViewer.Instance?.Close();
        Close();
        _font?.Dispose(); _font = null;
    }

    static Item Head(string t) => new Item { Label = t, Header = true };
    static Item Info(string label, Func<string>? v = null) => new Item { Label = label, Value = v, Header = true };

    static Item Bool(string label, Func<bool> get, Action<bool> set, bool def) => new Item
    {
        Label = label,
        Value = () => get() ? "ON" : "OFF",
        Change = _ => set(!get()),
        Reset = () => set(def),
    };

    static Item Float(string label, Func<float> get, Action<float> set, float step, float min, float max, float def, string fmt = "0.##", string unit = "") => new Item
    {
        Label = label,
        Value = () => get().ToString(fmt, CultureInfo.InvariantCulture) + unit,
        Change = d => set(Math.Clamp(get() + step * d, min, max)),
        Reset = () => set(def),
    };

    static Item Int(string label, Func<int> get, Action<int> set, int min, int max, int def) => new Item
    {
        Label = label,
        Value = () => get().ToString(CultureInfo.InvariantCulture),
        Change = d => set(Math.Clamp(get() + d, min, max)),
        Reset = () => set(def),
    };

    static Item Table(string label, string[] names, Func<int> get, Action<int> set, int def) => new Item
    {
        Label = label,
        Value = () => { int i = get(); return i >= 0 && i < names.Length ? names[i] : i.ToString(CultureInfo.InvariantCulture); },
        Change = d => set(((get() + Math.Sign(d)) % names.Length + names.Length) % names.Length),
        Reset = () => set(def),
    };

    static Item Action_(string label, Action a) => new Item { Label = label, Activate = a, Value = () => "[Enter]" };
    static Item Menu(string label, Func<List<Item>> sub) => new Item { Label = label, Sub = sub, Value = () => ">>" };

    static Item PhInfo(string label) => PhInfo(label, "n/a");

    static Item PhInfo(string label, string value) => new Item { Label = label, Value = () => value, Header = true, Placeholder = true };

    Item PhAction(string label, string? hint = null) => new Item
    {
        Label = label,
        Value = () => "[Enter]",
        Placeholder = true,
        Activate = () => Toast(hint ?? (label + ": not available in the engine yet")),
    };

    Item PhBool(string label, bool def) => new Item
    {
        Label = label,
        Placeholder = true,
        Value = () => PhGet(label, def) ? "ON" : "OFF",
        Change = _ => PhSet(label, !PhGet(label, def)),
        Reset = () => PhSet(label, def),
    };

    Item PhFloat(string label, float step, float min, float max, float def, string fmt = "0.##", string unit = "") => new Item
    {
        Label = label,
        Placeholder = true,
        Value = () => PhGet(label, def).ToString(fmt, CultureInfo.InvariantCulture) + unit,
        Change = d => PhSet(label, Math.Clamp(PhGet(label, def) + step * d, min, max)),
        Reset = () => PhSet(label, def),
    };

    Item PhInt(string label, int min, int max, int def) => new Item
    {
        Label = label,
        Placeholder = true,
        Value = () => PhGet(label, def).ToString(CultureInfo.InvariantCulture),
        Change = d => PhSet(label, Math.Clamp(PhGet(label, def) + d, min, max)),
        Reset = () => PhSet(label, def),
    };

    Item PhTable(string label, string[] names, int def) => new Item
    {
        Label = label,
        Placeholder = true,
        Value = () => names[Math.Clamp(PhGet(label, def), 0, names.Length - 1)],
        Change = d => { int i = PhGet(label, def); PhSet(label, ((i + Math.Sign(d)) % names.Length + names.Length) % names.Length); },
        Reset = () => PhSet(label, def),
    };

    static Item PhMenu(string label, string hint) => new Item
    {
        Label = label,
        Value = () => ">>",
        Placeholder = true,
        Sub = () => new List<Item> { Head(label.ToUpperInvariant()), PhInfo(hint) },
    };

    T PhGet<T>(string key, T def) => _ph.TryGetValue(key, out var v) && v is T t ? t : def;
    void PhSet(string key, object v) => _ph[key] = v;

    void SampleFrame(float dt)
    {
        if (dt <= 0f) return;
        _sampleFrames++; _sampleTime += dt;
        _sampleMin = MathF.Min(_sampleMin, dt); _sampleMax = MathF.Max(_sampleMax, dt);
        if (_sampleTime >= 0.5f)
        {
            _fps = _sampleFrames / _sampleTime;
            _ftAvg = _sampleTime / _sampleFrames * 1000f;
            _ftMin = _sampleMin * 1000f; _ftMax = _sampleMax * 1000f;
            _sampleFrames = 0; _sampleTime = 0f; _sampleMin = float.MaxValue; _sampleMax = 0f;
        }
    }

    public override void Render(in GameTime time)
    {
        RenderTools();   // Darsteller/Bodenplatte von Cutscene Tester und Animation Viewer (Welt)
        bool worldDebug = DebugLines.Labels.Count > 0 || Paradise.Game.Config.TrafficConfig.ShowDebug;
        if (!_open && !worldDebug && (_toast == null || _time > _toastUntil)) return;
        var f = Font();
        if (f == null || f.Material == null || _r.Width <= 0 || _r.Height <= 0) return;

        float k = _r.Height / 1080f;
        int fontSize = Math.Max(10, (int)MathF.Round(12f * k));

        // FontAtlas uses proportional glyphs. Scale from the actual font line height,
        // not from the atlas cell dimensions.
        float scale = fontSize / Math.Max(1f, f.LineHeight);
        float ch = f.LineHeight * scale;
        float lh = MathF.Max(ch * 1.35f, fontSize * 1.25f);

        // Kept for RenderWorldDebug's existing layout contract. Text() itself
        // uses each glyph's real width and advance.
        float cw = f.CellW * scale;

        _v.Clear(); _i.Clear();

        // Debug-Beschriftungen in der Welt / Verkehrstafel (unter dem Menü)
        RenderWorldDebug(f, cw, ch, lh, k);

        if (!_open)
        {
            if (_toast == null || _time > _toastUntil) goto draw;
            float tw = MathF.Min(_r.Width - 40f * k, TextWidth(f, _toast!, scale) + 20f);
            float tx = 20f * k, ty = _r.Height - lh - 28f * k;
            Quad(tx, ty, tx + tw, ty + lh + 8f, 0f, 0f, 0f, 0f, CBack);
            Text(f, tx + 8f, ty + 4f, _toast!, CYellow, tx + tw - 8f, cw, ch);
        }
        else
        {
            var p = _stack[^1];
            int rows = Math.Max(5, (int)MathF.Floor((_r.Height * 0.8f - lh * 3f) / lh));
            if (p.Sel < p.Top) p.Top = p.Sel;
            if (p.Sel >= p.Top + rows) p.Top = p.Sel - rows + 1;
            p.Top = Math.Clamp(p.Top, 0, Math.Max(0, _items.Count - rows));
            int shown = Math.Min(rows, _items.Count - p.Top);
            float w = MathF.Min(_r.Width - 40f * k, 1000f * k);
            float rx = 20f * k, ry = 20f * k;
            Quad(rx, ry, rx + w, ry + lh * (shown + 3) + 12f, 0f, 0f, 0f, 0f, CBack);

            float x = rx + 10f, y = ry + 6f, valX = rx + w * 0.55f;

            var path = new StringBuilder("TDU2 | ");
            for (int i = 0; i < _stack.Count; i++) { if (i > 0) path.Append(" > "); path.Append(_stack[i].Title); }
            Text(f, x, y, path.ToString().ToUpperInvariant(), CPurple, rx + w - 10f, cw, ch);
            if (_drive)
            {
                string dm = "[DRIVING - " + ToggleKeyName() + " = MENU]";
                float dmWidth = TextWidth(f, dm, scale);
                Text(f, rx + w - 10f - dmWidth, y, dm, COrange, rx + w - 10f, cw, ch);
            }
            y += lh * 1.4f;

            for (int i = p.Top; i < p.Top + shown; i++)
            {
                var it = _items[i];
                bool sel = i == p.Sel;
                if (it.Header)
                {
                    Text(f, x, y, (it.Value != null ? "   " : "") + it.Label, it.Value != null ? CWhite : CBlue, valX - 2f, cw, ch);
                    if (it.Value != null) Text(f, valX, y, Safe(it.Value), it.Placeholder ? CGray : CGreen, rx + w - 10f, cw, ch);
                }
                else
                {
                    bool mark = sel && !_drive;
                    Text(f, x, y, (mark ? "→ " : "  ") + it.Label, mark ? CCyan : CWhite, valX - 2f, cw, ch);
                    if (it.Value != null)
                    {
                        uint col = it.Placeholder ? CGray : sel ? CYellow : CGreen;
                        Text(f, valX, y, Safe(it.Value), col, rx + w - 10f, cw, ch);
                    }
                }
                y += lh;
            }

            Text(f, x, ry + lh * (shown + 3) + 12f - lh - 6f,
                 "Grey values: not available in the engine yet  |  " + ToggleKeyName() + ": Toggle Control (menu / drive)", CGray, rx + w - 10f, cw, ch);

            if (_toast != null && _time <= _toastUntil)
            {
                float tw = MathF.Min(w, TextWidth(f, _toast, scale) + 20f);
                float ty = ry + lh * (shown + 3) + 18f;
                Quad(rx, ty, rx + tw, ty + lh + 8f, 0f, 0f, 0f, 0f, CBack);
                Text(f, rx + 8f, ty + 4f, _toast, CYellow, rx + tw - 8f, cw, ch);
            }
        }

    draw:
        // Draw above the HUD and minimap using the top overlay pass.
        if (_i.Count > 0) _r.DrawOverlayTop(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_i), f.Material);
    }

    static string Safe(Func<string> f)
    {
        try { return f() ?? ""; } catch (Exception) { return "?"; }
    }

    FontAtlas? Font()
    {
        if (_font == null && !_fontFailed)
        {
            _font = FontAtlas.Create(_r, "Segoe UI", 22, 400);
            if (_font == null) _fontFailed = true;
        }
        return _font;
    }

    // Measures text using the same proportional advances used by Text().
    float TextWidth(FontAtlas f, string s, float scale)
        => f.Measure(s, scale);

    // Compatibility signature retained for RenderWorldDebug in the other DevMenu
    // partial. cw is only a legacy layout hint; glyph placement is proportional.
    void Text(FontAtlas f, float x, float y, string s, uint col, float maxX, float cw, float ch)
    {
        float scale = ch / Math.Max(1f, f.LineHeight);
        TextProportional(f, x, y, s, col, maxX, scale);
    }

    void TextProportional(FontAtlas f, float x, float y, string s, uint col, float maxX, float scale)
    {
        foreach (char c in s)
        {
            var glyph = f.GetGlyph(c);
            float advance = glyph.Advance * scale;

            // Do not draw empty glyphs (e.g. spaces), but always advance the pen.
            if (glyph.Width > 0f && glyph.Height > 0f)
            {
                float glyphWidth = glyph.Width * scale;
                float glyphHeight = glyph.Height * scale;

                if (x + glyphWidth > maxX)
                    break;

                Quad(
                    x, y,
                    x + glyphWidth, y + glyphHeight,
                    glyph.U0, glyph.V0, glyph.U1, glyph.V1,
                    col);
            }

            x += advance;
            if (x >= maxX)
                break;
        }
    }

    void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint col)
    {
        var f = _font!;
        if (u0 == 0f && v0 == 0f && u1 == 0f && v1 == 0f) { var wuv = f.WhiteUv(); u0 = wuv.u0; v0 = wuv.v0; u1 = wuv.u1; v1 = wuv.v1; }
        uint b = (uint)_v.Count;
        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x0, y0, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u0, v0) });
        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x1, y0, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u1, v0) });
        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x1, y1, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u1, v1) });
        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x0, y1, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u0, v1) });
        _i.Add(b); _i.Add(b + 1); _i.Add(b + 2); _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
    }
}


//using Paradise.Core.Diagnostics;
//using Paradise.Core.Engine;
//using Paradise.Core.Platform;
//using Paradise.Core.Render;
//using Paradise.Game.Input;
//using Paradise.Game.World;
//using System.Globalization;
//using System.Text;

//namespace Paradise.Game.Screens;

//public sealed partial class DevMenu : GameModule
//{
//    const int VK_F1 = 0x70, VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27;
//    const int VK_PGUP = 0x21, VK_PGDN = 0x22, VK_HOME = 0x24, VK_END = 0x23;
//    const int VK_DELETE = 0x2E, VK_ENTER = 0x0D, VK_BACK = 0x08, VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
//    static readonly uint CYellow = ObjectVertex.PackColor(255, 230, 51, 255);
//    static readonly uint CCyan = ObjectVertex.PackColor(77, 242, 255, 255);
//    static readonly uint CGreen = ObjectVertex.PackColor(115, 255, 115, 255);
//    static readonly uint CPurple = ObjectVertex.PackColor(217, 140, 255, 255);
//    static readonly uint CBlue = ObjectVertex.PackColor(140, 179, 255, 255);
//    static readonly uint CWhite = ObjectVertex.PackColor(235, 235, 235, 255);
//    static readonly uint CGray = ObjectVertex.PackColor(150, 150, 150, 255);
//    static readonly uint CBack = ObjectVertex.PackColor(0, 0, 0, 128);
//    static readonly uint CPanel = ObjectVertex.PackColor(0, 0, 0, 184);
//    static readonly uint COrange = ObjectVertex.PackColor(255, 170, 60, 255);

//    sealed class Item
//    {
//        public string Label = "";
//        public Func<string>? Value;
//        public Action<int>? Change;
//        public Action? Reset;
//        public Action? Activate;
//        public Func<List<Item>>? Sub;
//        public bool Header;
//        public bool Placeholder;
//    }

//    sealed class Page
//    {
//        public string Title = "";
//        public Func<List<Item>> Build = () => new List<Item>();
//        public int Sel, Top;
//    }

//    readonly IRenderer _r;
//    readonly IGameWindow _window;
//    readonly TerrainModule _terrain;
//    readonly VehicleModule _vehicles;
//    readonly LaunchOptions _opts;

//    readonly List<Page> _stack = new();
//    List<Item> _items = new();
//    bool _open, _blockedByMe;
//    /// <summary>Toggle Control: Menü bleibt sichtbar, Tastatur geht ans Spiel (Auto fahren). Umschalten mit Taste "dev_menuctrl" (Tab).</summary>
//    bool _drive;
//    double _time, _repeatAt;
//    int _repeatKey;
//    string? _toast;
//    double _toastUntil;

//    readonly List<ObjectVertex> _v = new(8192);
//    readonly List<uint> _i = new(12288);
//    FontAtlas? _font;
//    bool _fontFailed;

//    readonly Dictionary<string, object> _ph = new();

//    float _fps, _ftAvg, _ftMin, _ftMax;
//    int _sampleFrames;
//    float _sampleTime, _sampleMin = float.MaxValue, _sampleMax;

//    public DevMenu(IRenderer renderer, TerrainModule terrain, VehicleModule vehicles, IGameWindow window, LaunchOptions opts)
//    {
//        _r = renderer; _terrain = terrain; _vehicles = vehicles; _window = window; _opts = opts;
//    }

//    public bool IsOpen => _open;

//    public override void Update(in GameTime time)
//    {
//        _time = time.TotalTime;
//        SampleFrame(time.DeltaTime);
//        CollectCarDebug();   // Radstrahlen/Kräfte am Auto (Car > Physics debug), auch bei geschlossenem Menü
//        CollectRaceDebug();  // gewählte Strecke (Race Debug), auch bei geschlossenem Menü
//        UpdateTools(time.DeltaTime);   // Cutscene Tester / Animation Viewer

//        if (TDU2Controls.RawKeyPressed(VK_F1))
//        {
//            if (_open) Close(); else Open();
//            return;
//        }
//        if (!_open) return;

//        if (ToggleKeyPressed()) SetDrive(!_drive);
//        if (_drive)
//        {
//            // Steuerung beim Spiel: Menü nur anzeigen, keine Navigation
//            if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
//            Rebuild();
//            return;
//        }
//        if (!TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }

//        Rebuild();
//        var p = _stack[^1];
//        bool big = TDU2Controls.RawKeyDown(VK_SHIFT) || TDU2Controls.RawKeyDown(VK_LSHIFT) || TDU2Controls.RawKeyDown(VK_RSHIFT);
//        if (KeyRepeat(VK_DOWN)) Move(p, 1);
//        if (KeyRepeat(VK_UP)) Move(p, -1);
//        if (KeyRepeat(VK_PGDN)) Move(p, 10);
//        if (KeyRepeat(VK_PGUP)) Move(p, -10);
//        if (TDU2Controls.RawKeyPressed(VK_HOME)) { p.Sel = 0; Move(p, 0); }
//        if (TDU2Controls.RawKeyPressed(VK_END)) { p.Sel = _items.Count - 1; Move(p, 0); }
//        var it = p.Sel >= 0 && p.Sel < _items.Count ? _items[p.Sel] : null;
//        if (it != null && !it.Header)
//        {
//            if (KeyRepeat(VK_RIGHT)) it.Change?.Invoke(big ? 10 : 1);
//            if (KeyRepeat(VK_LEFT)) it.Change?.Invoke(big ? -10 : -1);
//            if (TDU2Controls.RawKeyPressed(VK_DELETE)) it.Reset?.Invoke();
//            if (TDU2Controls.RawKeyPressed(VK_ENTER))
//            {
//                if (it.Sub != null) _stack.Add(new Page { Title = it.Label, Build = it.Sub });
//                else if (it.Activate != null) it.Activate();
//                else it.Change?.Invoke(1);
//            }
//        }
//        if (TDU2Controls.RawKeyPressed(VK_BACK))
//        {
//            if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); else Close();
//        }
//    }

//    /// <summary>Taste der Aktion "dev_menuctrl" (Game/Config/KeyConfig.cs, Standard Tab) – auch während das Menü sperrt.</summary>
//    static bool ToggleKeyPressed()
//    {
//        var a = TDU2Controls.Get("dev_menuctrl");
//        return a != null && (TDU2Controls.RawKeyPressed(a.Key1) || TDU2Controls.RawKeyPressed(a.Key2));
//    }

//    static string ToggleKeyName()
//    {
//        var a = TDU2Controls.Get("dev_menuctrl");
//        return a != null && a.Key1 > 0 ? TDU2Controls.KeyName(a.Key1) : "-";
//    }

//    void SetDrive(bool on)
//    {
//        _drive = on;
//        if (on) { if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; } }
//        else if (_open && !TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
//        Toast(on ? "Toggle Control: driving (menu stays open, " + ToggleKeyName() + " = back to the menu)" : "Toggle Control: menu navigation");
//    }

//    bool KeyRepeat(int vk)
//    {
//        if (TDU2Controls.RawKeyPressed(vk)) { _repeatKey = vk; _repeatAt = _time + 0.4; return true; }
//        if (_repeatKey == vk && TDU2Controls.RawKeyDown(vk) && _time >= _repeatAt) { _repeatAt = _time + 0.06; return true; }
//        return false;
//    }

//    void Move(Page p, int d)
//    {
//        if (_items.Count == 0) { p.Sel = 0; return; }
//        int s = Math.Clamp(p.Sel + d, 0, _items.Count - 1);
//        int dir = d >= 0 ? 1 : -1;
//        while (s >= 0 && s < _items.Count && _items[s].Header) s += dir;
//        if (s < 0 || s >= _items.Count) s = Math.Clamp(p.Sel, 0, _items.Count - 1);
//        p.Sel = s;
//    }

//    void Open()
//    {
//        _open = true;
//        _stack.Clear();
//        _stack.Add(new Page { Title = "DEVELOPER MENU", Build = MainMenu });
//        if (!_drive && !TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
//        Rebuild(); Move(_stack[0], 0);
//    }

//    void Close()
//    {
//        _open = false;
//        if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
//    }

//    void Rebuild()
//    {
//        var p = _stack[^1];
//        try { _items = p.Build() ?? new List<Item>(); }
//        catch (Exception ex) { _items = new List<Item> { Info("Error: " + ex.Message) }; }
//        if (p.Sel >= _items.Count) p.Sel = Math.Max(0, _items.Count - 1);
//        if (_items.Count > 0 && _items[p.Sel].Header) Move(p, 1);
//    }

//    void Toast(string s)
//    {
//        _toast = s; _toastUntil = _time + 3.0;
//        Log.Info("[DevMenu] " + s);
//    }

//    public override void Unload()
//    {
//        CutsceneTester.Instance?.Unload(true);
//        AnimViewer.Instance?.Close();
//        Close();
//        _font?.Dispose(); _font = null;
//    }

//    static Item Head(string t) => new Item { Label = t, Header = true };
//    static Item Info(string label, Func<string>? v = null) => new Item { Label = label, Value = v, Header = true };

//    static Item Bool(string label, Func<bool> get, Action<bool> set, bool def) => new Item
//    {
//        Label = label,
//        Value = () => get() ? "ON" : "OFF",
//        Change = _ => set(!get()),
//        Reset = () => set(def),
//    };

//    static Item Float(string label, Func<float> get, Action<float> set, float step, float min, float max, float def, string fmt = "0.##", string unit = "") => new Item
//    {
//        Label = label,
//        Value = () => get().ToString(fmt, CultureInfo.InvariantCulture) + unit,
//        Change = d => set(Math.Clamp(get() + step * d, min, max)),
//        Reset = () => set(def),
//    };

//    static Item Int(string label, Func<int> get, Action<int> set, int min, int max, int def) => new Item
//    {
//        Label = label,
//        Value = () => get().ToString(CultureInfo.InvariantCulture),
//        Change = d => set(Math.Clamp(get() + d, min, max)),
//        Reset = () => set(def),
//    };

//    static Item Table(string label, string[] names, Func<int> get, Action<int> set, int def) => new Item
//    {
//        Label = label,
//        Value = () => { int i = get(); return i >= 0 && i < names.Length ? names[i] : i.ToString(CultureInfo.InvariantCulture); },
//        Change = d => set(((get() + Math.Sign(d)) % names.Length + names.Length) % names.Length),
//        Reset = () => set(def),
//    };

//    static Item Action_(string label, Action a) => new Item { Label = label, Activate = a, Value = () => "[Enter]" };
//    static Item Menu(string label, Func<List<Item>> sub) => new Item { Label = label, Sub = sub, Value = () => ">>" };

//    static Item PhInfo(string label) => PhInfo(label, "n/a");

//    static Item PhInfo(string label, string value) => new Item { Label = label, Value = () => value, Header = true, Placeholder = true };

//    Item PhAction(string label, string? hint = null) => new Item
//    {
//        Label = label,
//        Value = () => "[Enter]",
//        Placeholder = true,
//        Activate = () => Toast(hint ?? (label + ": not available in the engine yet")),
//    };

//    Item PhBool(string label, bool def) => new Item
//    {
//        Label = label,
//        Placeholder = true,
//        Value = () => PhGet(label, def) ? "ON" : "OFF",
//        Change = _ => PhSet(label, !PhGet(label, def)),
//        Reset = () => PhSet(label, def),
//    };

//    Item PhFloat(string label, float step, float min, float max, float def, string fmt = "0.##", string unit = "") => new Item
//    {
//        Label = label,
//        Placeholder = true,
//        Value = () => PhGet(label, def).ToString(fmt, CultureInfo.InvariantCulture) + unit,
//        Change = d => PhSet(label, Math.Clamp(PhGet(label, def) + step * d, min, max)),
//        Reset = () => PhSet(label, def),
//    };

//    Item PhInt(string label, int min, int max, int def) => new Item
//    {
//        Label = label,
//        Placeholder = true,
//        Value = () => PhGet(label, def).ToString(CultureInfo.InvariantCulture),
//        Change = d => PhSet(label, Math.Clamp(PhGet(label, def) + d, min, max)),
//        Reset = () => PhSet(label, def),
//    };

//    Item PhTable(string label, string[] names, int def) => new Item
//    {
//        Label = label,
//        Placeholder = true,
//        Value = () => names[Math.Clamp(PhGet(label, def), 0, names.Length - 1)],
//        Change = d => { int i = PhGet(label, def); PhSet(label, ((i + Math.Sign(d)) % names.Length + names.Length) % names.Length); },
//        Reset = () => PhSet(label, def),
//    };

//    static Item PhMenu(string label, string hint) => new Item
//    {
//        Label = label,
//        Value = () => ">>",
//        Placeholder = true,
//        Sub = () => new List<Item> { Head(label.ToUpperInvariant()), PhInfo(hint) },
//    };

//    T PhGet<T>(string key, T def) => _ph.TryGetValue(key, out var v) && v is T t ? t : def;
//    void PhSet(string key, object v) => _ph[key] = v;

//    void SampleFrame(float dt)
//    {
//        if (dt <= 0f) return;
//        _sampleFrames++; _sampleTime += dt;
//        _sampleMin = MathF.Min(_sampleMin, dt); _sampleMax = MathF.Max(_sampleMax, dt);
//        if (_sampleTime >= 0.5f)
//        {
//            _fps = _sampleFrames / _sampleTime;
//            _ftAvg = _sampleTime / _sampleFrames * 1000f;
//            _ftMin = _sampleMin * 1000f; _ftMax = _sampleMax * 1000f;
//            _sampleFrames = 0; _sampleTime = 0f; _sampleMin = float.MaxValue; _sampleMax = 0f;
//        }
//    }

//    public override void Render(in GameTime time)
//    {
//        RenderTools();   // Darsteller/Bodenplatte von Cutscene Tester und Animation Viewer (Welt)
//        bool worldDebug = DebugLines.Labels.Count > 0 || Paradise.Game.Config.TrafficConfig.ShowDebug;
//        if (!_open && !worldDebug && (_toast == null || _time > _toastUntil)) return;
//        var f = Font();
//        if (f == null || f.Material == null || _r.Width <= 0 || _r.Height <= 0) return;

//        float k = _r.Height / 1080f;
//        int fontSize = Math.Max(10, (int)MathF.Round(12f * k));
//        float lh = fontSize * 1.35f;
//        float scale = fontSize / (float)f.CellH;
//        float cw = f.CellW * scale, ch = f.CellH * scale;

//        _v.Clear(); _i.Clear();

//        // Debug-Beschriftungen in der Welt / Verkehrstafel (unter dem Menü)
//        RenderWorldDebug(f, cw, ch, lh, k);

//        if (!_open)
//        {
//            if (_toast == null || _time > _toastUntil) goto draw;
//            float tw = MathF.Min(_r.Width - 40f * k, _toast!.Length * cw + 20f);
//            float tx = 20f * k, ty = _r.Height - lh - 28f * k;
//            Quad(tx, ty, tx + tw, ty + lh + 8f, 0f, 0f, 0f, 0f, CBack);
//            Text(f, tx + 8f, ty + 4f, _toast!, CYellow, tx + tw - 8f, cw, ch);
//        }
//        else
//        {
//            var p = _stack[^1];
//            int rows = Math.Max(5, (int)MathF.Floor((_r.Height * 0.8f - lh * 3f) / lh));
//            if (p.Sel < p.Top) p.Top = p.Sel;
//            if (p.Sel >= p.Top + rows) p.Top = p.Sel - rows + 1;
//            p.Top = Math.Clamp(p.Top, 0, Math.Max(0, _items.Count - rows));
//            int shown = Math.Min(rows, _items.Count - p.Top);
//            float w = MathF.Min(_r.Width - 40f * k, 1000f * k);
//            float rx = 20f * k, ry = 20f * k;
//            Quad(rx, ry, rx + w, ry + lh * (shown + 3) + 12f, 0f, 0f, 0f, 0f, CBack);

//            float x = rx + 10f, y = ry + 6f, valX = rx + w * 0.55f;

//            var path = new StringBuilder("TDU2 | ");
//            for (int i = 0; i < _stack.Count; i++) { if (i > 0) path.Append(" > "); path.Append(_stack[i].Title); }
//            Text(f, x, y, path.ToString().ToUpperInvariant(), CPurple, rx + w - 10f, cw, ch);
//            if (_drive)
//            {
//                string dm = "[DRIVING - " + ToggleKeyName() + " = MENU]";
//                Text(f, rx + w - 10f - dm.Length * cw, y, dm, COrange, rx + w - 10f, cw, ch);
//            }
//            y += lh * 1.4f;

//            for (int i = p.Top; i < p.Top + shown; i++)
//            {
//                var it = _items[i];
//                bool sel = i == p.Sel;
//                if (it.Header)
//                {
//                    Text(f, x, y, (it.Value != null ? "   " : "") + it.Label, it.Value != null ? CWhite : CBlue, valX - 2f, cw, ch);
//                    if (it.Value != null) Text(f, valX, y, Safe(it.Value), it.Placeholder ? CGray : CGreen, rx + w - 10f, cw, ch);
//                }
//                else
//                {
//                    bool mark = sel && !_drive;
//                    Text(f, x, y, (mark ? "-> " : "   ") + it.Label, mark ? CCyan : CWhite, valX - 2f, cw, ch);
//                    if (it.Value != null)
//                    {
//                        uint col = it.Placeholder ? CGray : sel ? CYellow : CGreen;
//                        Text(f, valX, y, Safe(it.Value), col, rx + w - 10f, cw, ch);
//                    }
//                }
//                y += lh;
//            }

//            Text(f, x, ry + lh * (shown + 3) + 12f - lh - 6f,
//                 "Grey values: not available in the engine yet  |  " + ToggleKeyName() + ": Toggle Control (menu / drive)", CGray, rx + w - 10f, cw, ch);

//            if (_toast != null && _time <= _toastUntil)
//            {
//                float tw = MathF.Min(w, _toast.Length * cw + 20f);
//                float ty = ry + lh * (shown + 3) + 18f;
//                Quad(rx, ty, rx + tw, ty + lh + 8f, 0f, 0f, 0f, 0f, CBack);
//                Text(f, rx + 8f, ty + 4f, _toast, CYellow, rx + tw - 8f, cw, ch);
//            }
//        }

//    draw:
//        // über allem, auch über HUD/Minimap (Flash): eigener Overlay-Durchgang nach der Flash-Oberfläche
//        if (_i.Count > 0) _r.DrawOverlayTop(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_i), f.Material);
//    }

//    static string Safe(Func<string> f)
//    {
//        try { return f() ?? ""; } catch (Exception) { return "?"; }
//    }

//    FontAtlas? Font()
//    {
//        if (_font == null && !_fontFailed)
//        {
//            _font = FontAtlas.Create(_r, "Consolas", 22);
//            if (_font == null) _fontFailed = true;
//        }
//        return _font;
//    }

//    void Text(FontAtlas f, float x, float y, string s, uint col, float maxX, float cw, float ch)
//    {
//        foreach (char c in s)
//        {
//            if (x + cw > maxX) break;
//            if (c != ' ')
//            {
//                var uv = f.Uv(c);
//                Quad(x, y, x + cw, y + ch, uv.u0, uv.v0, uv.u1, uv.v1, col);
//            }
//            x += cw;
//        }
//    }

//    void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint col)
//    {
//        var f = _font!;
//        if (u0 == 0f && v0 == 0f && u1 == 0f && v1 == 0f) { var wuv = f.WhiteUv(); u0 = wuv.u0; v0 = wuv.v0; u1 = wuv.u1; v1 = wuv.v1; }
//        uint b = (uint)_v.Count;
//        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x0, y0, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u0, v0) });
//        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x1, y0, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u1, v0) });
//        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x1, y1, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u1, v1) });
//        _v.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x0, y1, 0), Normal = System.Numerics.Vector3.UnitZ, Color = col, Uv0 = new System.Numerics.Vector2(u0, v1) });
//        _i.Add(b); _i.Add(b + 1); _i.Add(b + 2); _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
//    }
//}
