using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Globalization;
using FV2 = Paradise.Core.Flash.Vector2;
using M4 = System.Numerics.Matrix4x4;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Sticker-Shop (Port Unity TDU2StickerShop): Spot ESTICK_*, Interieur istick_b, Verkäuferin = Auswahlpunkt Typ 28
/// "StickersShop_area" oder Klick auf das eigene Auto. Raum-Modus (Laufen) wie bisher aus InteriorUtils; das Shop-Menü läuft im
/// Original-Film frontend/&lt;res&gt;/shopscar.bnk (Labels st_open … st_close, WhatPage "stickers"):
///   _root.StickersMenu()  Hauptmenü menu_st: Lackieren / Bekleben / Entfernen / Abbrechen / Bestätigen &amp; bezahlen
///   FSCommands "stsh:*" (wie MngCarPainting im Spiel):
///     pain:colo:open|vali|canc|clos|type:next|type:prev|bion|biof|bich, pain:myco:init|vali|fill|eras|clos|clea
///     stic:list:open|prca|neca|vali|canc|clos, stic:move|rota|scal|alph|colo, stic:appl, stic:flip,
///     stic:symn|symf, stic:uscn|uscf, stic:hist:undo|redo|open|canc, stic:colo:myco:*, save:open|vali, clea, canc,
///     mcin/mcou, menu:enbl|dsbl; außerdem shko (Menü verlassen), dbxo (Dialog), gofr (Shop verlassen).
/// Ergebnis: <see cref="CarLivery"/> lokal gespeichert (<see cref="Liveries"/>). Steuerung: Maus = Sticker auf dem Auto platzieren,
/// Klick = aufkleben, rechte Maustaste = Kamera drehen, Mausrad = Werkzeug (Drehen/Größe/Transparenz) bzw. Zoom, Pfeile/Enter/
/// Rücktaste über den globalen Flash-Handler, X/Y = Tasten X/Y, F = spiegeln, Q/E drehen, +/- Größe.
/// </summary>
public class GAME_MODE_STICKERS : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_STICKERS;
    static readonly ScreenDef[] _screens = { ScreenDefs.Dealer };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Dealer;

    /// <summary>Grundgröße eines Stickers (halbe Breite in Metern bei 100 %), Unity BaseHalfSize.</summary>
    public static float BaseHalfSize = 0.25f;

    enum SMode { Menu, Paint, List, Place }

    static bool _open, _hooked;
    static SMode _mode;
    static DisplayCar? _dc;
    static LiveryView? _view;
    static CarLivery _saved = new(), _edit = new();
    static V3 _center, _extents;
    static float _lastEsc = -10f, _clock;

    // Kamera
    static float _yaw, _pitch = 12f, _dist = 6f;

    // Lack
    static bool _pBackupHas; static Rgba32 _pBackup, _pBackup2; static PaintFinish _fBackup; static bool _bBackup;
    static int _curColor = 1;          // 1 = Farbe 1, 2 = Farbe 2 (Bicolor)
    static Texture2D? _grad;
    static V2 _pickPos = new(0.5f, 0.5f);

    // Sticker
    static int _catIndex;
    static readonly List<StickerLibrary.Board> _cats = new();
    static StickerLibrary.Board? _board;
    static int _stickerIndex;
    static string _tool = "move";
    static float _rot, _sx = 1f, _sy = 1f, _alpha = 1f;
    static bool _uniform = true, _symmetry, _flip;
    static Rgba32 _tint = new(255, 255, 255, 255), _tintBackup;
    static readonly List<int> _history = new();
    static readonly List<List<StickerPlacement>> _redo = new();
    static bool _listPending;
    static float _listSince = -1f;
    static readonly HashSet<int> _newBoards = new();
    static readonly Dictionary<string, Rgba32> _tints = new();

    /// <summary>Shop-Menü offen: kein Laufen/Umsehen, Maus frei, Kamera und Tasten gehören dem Shop (InteriorUtils.UpdateInside).</summary>
    internal static bool ShopOpen => _open;

    // ================================================================== Raum-Modus
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (!_open) { base.Run(ctx, time); return; }
        // Shop offen: wie InteriorUtils.Run, aber ESC/Rücktaste gehören dem Film (kein Pausemenü)
        _ctx = ctx;
        if (_t == null || !UpdateInside(time.DeltaTime, ctx.Paused))
        {
            Close(false);
            GameMode.Set(GAME_MODE_TESTCAR.TestDriveActive ? Gamemode.GAME_MODE_TESTCAR : Gamemode.GAME_MODE_FREERIDE);
        }
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to != Gamemode.GAME_MODE_PAUSE && _open) Close(false);
        base.Exit(ctx, to);
    }

    // ================================================================== Laufmodus-Oberfläche
    /// <summary>
    /// Sticker-Shop betreten (Unity TDU2Interiors.LoadShopUi, Original: shopscar.vmf ab Label "st_open" – WhatPage "stickers",
    /// Titel m_StickersShop_, CurrentAction walk_mode): im Laufmodus nur Fadenkreuz + Titel; Menü/Lack/Sticker/Rechnung öffnet
    /// <see cref="OpenShop"/> (Verkäuferin Typ 28 oder das Auto).
    /// </summary>
    internal static void LoadShopUi()
    {
        var ui = _ui; if (ui == null) return;
        var texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language);
        FlashLocalization.Apply(ui, texts);
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.GotoLabel("/", "st_open", true);
        foreach (var d in ui.Root.List.Values) d.Visible = d.Name != null && (d.Name == "cursor" || d.Name == "title_bar");
        string title = texts != null && texts.TryGetValue("m_StickersShop_", out var t) && !string.IsNullOrEmpty(t) ? t : TitleFor(Kind.StickerShop);
        ui.SetVariable("/:Title", title);
        var sv = TDU2Session.Save;
        ui.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : " ");
        ui.SetVariable("/:ClubMoney", " ");
        ui.GotoLabel("/cursor", "valid", true);
        ui.SetVisible("/cursor/low_bar/top_bar", false);
        ResetUiAim();
        Log.Info("Sticker-Shop: Oberfläche st_open (" + title + ")");
    }

    // ================================================================== Öffnen / Schließen
    /// <summary>Shop-Menü öffnen (Verkäuferin Typ 28 oder Auto angeklickt; dc = null: das eigene Auto im Shop).</summary>
    internal static bool OpenShop(DisplayCar? dc)
    {
        if (_open) return true;
        dc ??= _cars.Count > 0 ? _cars[0] : null;
        var ui = _ui;
        if (ui == null || dc?.Car == null || dc.Model == null) { Log.Warn("Sticker-Shop: kein Auto im Shop"); return false; }
        _dc = dc;
        if (!_hooked) { ui.FSCommand += OnCommand; _hooked = true; }
        UpdateUiAim(null);
        ui.MouseEnabled = true;
        var vm = Vm;
        StickerLibrary.Init(FlashHost.BnkDir, vm?.Database?.Root, _v.Renderer);
        try { FlashLocalization.Apply(ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }

        _view = LiveryView.For(dc.Model, dc.Assets, _v.Renderer);
        if (_view == null) { Log.Warn("Sticker-Shop: keine Lack-Materialien"); return false; }
        _saved = Liveries.Get(dc.Car.FileName)?.Clone() ?? new CarLivery();
        _edit = _saved.Clone();
        _view.Show(_edit);
        _history.Clear(); _redo.Clear(); _newBoards.Clear(); _tints.Clear();

        // Kollision für die Sticker-Platzierung: Lackflächen (nur im Shop); die Kollisionsbox des Autos stört Strahl und Kamera nicht
        if (dc.Col != null) dc.Col.IgnoreRays = true;
        BuildPickMeshes();
        _center = (dc.BoxMin + dc.BoxMax) * 0.5f;
        _extents = (dc.BoxMax - dc.BoxMin) * 0.5f;

        // Kamera um das Auto (Unity: vom Standpunkt aus, Abstand nach Größe)
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam != null && st != null)
        {
            var d = _center - st.RenderToGame(cam.Position); d.Y = 0f;
            _yaw = d.LengthSquared() > 1e-4f ? MathF.Atan2(d.X, d.Z) * 180f / MathF.PI : 135f;
            _dist = System.Math.Clamp(_extents.Length() * 2.2f, 4f, 9f);
            _pitch = 14f;
        }
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        _savedFov = _t.FieldOfView;

        _open = true;
        _mode = SMode.Menu;
        foreach (var n in new[] { "menu_st", "price", "invoice", "stickers", "paint", "help" }) ui.Show("/" + n);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:WhatPage", "stickers");
        SetMenuVars();
        UpdatePrices();
        ui.SetValue("/:Input", true);
        ui.CallClip("/", "StickersMenu");
        int p2 = 0; foreach (var m in dc.Model.PaintMaterials) if (CarModel.IsPaint2(m)) p2++;
        Log.Info("Sticker-Shop geöffnet: " + dc.Car.FileName + " (" + _edit.Stickers.Count + " Sticker, Lack-Materialien " + dc.Model.PaintMaterials.Count
                 + ", davon Paint2: " + p2 + ", Strahl-Dreiecke " + PickTriangles() + ")");
        return true;
    }

    static void SetMenuVars()
    {
        var ui = _ui!;
        bool factoryOn = _view != null && _view.HasFactory && !_edit.ClearFactory;
        ui.SetValue("/:IsVehiclePaintable", !factoryOn);
        ui.SetValue("/:IsVehicleStickable", true);
        ui.SetValue("/:IsBike", false);
        ui.SetValue("/:HasModifiedVehicle", Modified());
        ui.SetValue("/:CanUndo", _history.Count > 0);
    }

    /// <summary>Shop-Menü schließen. keep = Änderungen behalten (gespeichert), sonst gespeicherten Stand zeigen.</summary>
    static void Close(bool keep)
    {
        if (!_open) return;
        _open = false;
        FlashDialog.Cancel();
        if (_view != null && _dc != null)
        {
            _view.Preview.Clear();
            _view.Show(keep ? Liveries.Get(_dc.Car.FileName) : _saved);
        }
        _pick.Clear(); _tints.Clear();
        if (_dc?.Col != null) _dc.Col.IgnoreRays = false;
        // Laufmodus: Startblick zurück, Kamera folgt wieder dem Walker (ApplyCamera)
        if (_t != null) { _t.FieldOfView = _savedFov > 0f ? _savedFov : _t.FieldOfView; if (_t.Camera != null) _t.Camera.Override = null; }
        var ui = _ui;
        if (ui != null)
        {
            foreach (var n in new[] { "menu_st", "price", "invoice", "stickers", "paint" }) ui.Hide("/" + n);
            ui.GotoLabel("/", "st_stop", false);
            ui.SetValue("/:Input", true);
            ui.SetVariable("/:CurrentAction", "walk_mode");
        }
        Log.Info("Sticker-Shop geschlossen" + (keep ? " (gespeichert)" : ""));
    }

    static float _savedFov = -1f;

    // ================================================================== Befehle des Films
    static void OnCommand(string cmd, string args)
    {
        if (!_open || _ui == null || _view == null) return;
        var ui0 = _ui;
        string lc = (cmd ?? "").Trim().ToLowerInvariant();
        if (lc == "dbxo") { FlashDialog.Open(ui0); return; }   // Bestätigungsbox aus general.bnk (Original), Antwort kommt danach
        if (lc == "shko" || lc == "wmon" || lc == "liin") { Close(false); return; }   // Menü verlassen -> Laufmodus
        if (lc == "gofr") { Leave(); return; }
        if (!lc.StartsWith("stsh:", StringComparison.Ordinal)) return;
        string s = lc[5..];
        Log.Debug("Sticker-Shop: " + s);
        var ui = _ui;
        switch (s)
        {
            case "mcin": case "mcou": case "menu:enbl": case "menu:dsbl": return;

            // ---------------- Lackieren
            case "pain:colo:open": BeginPaint(); return;
            case "pain:colo:vali": _edit.HasPaint = true; if (_edit.Finish == PaintFinish.Original) _edit.Finish = PaintFinish.Gloss; _mode = SMode.Menu; Changed(); return;
            case "pain:colo:canc":
                _edit.HasPaint = _pBackupHas; _edit.Paint = _pBackup; _edit.Paint2 = _pBackup2; _edit.Finish = _fBackup; _edit.Bicolour = _bBackup;
                _mode = SMode.Menu; _view.Show(_edit); return;
            case "pain:colo:clos": _mode = SMode.Menu; return;
            case "pain:colo:type:next": CycleFinish(1); return;
            case "pain:colo:type:prev": CycleFinish(-1); return;
            case "pain:colo:bion": _edit.Bicolour = true; _curColor = 1; _view.Show(_edit); return;
            case "pain:colo:biof": _edit.Bicolour = false; _curColor = 1; _view.Show(_edit); return;
            case "pain:colo:bich":
                _curColor = ui.GetVariable("/:CurrentColor") == "2" ? 2 : (_curColor == 1 ? 2 : 1);
                _edit.Bicolour = true;
                _pickPos = FindInGradient(CurrentPaint());
                PlaceCursor("/paint/color_box/color_picker");
                Swatches("/paint/color_box/color_picker");
                _view.Show(_edit);
                return;
            case "pain:myco:init": FillMyColors("/paint/my_colors_box"); return;
            case "pain:myco:clea": return;

            // ---------------- Sticker-Liste
            case "stic:list:open": _mode = SMode.List; _view.Preview.Clear(); _view.Refresh(); OpenList(); return;
            case "stic:list:prca": case "stic:list:neca": FillCategory(); return;
            case "stic:list:vali": PickSticker(); return;
            case "stic:list:canc": case "stic:list:clos": if (_mode == SMode.List) _mode = SMode.Menu; return;

            // ---------------- Platzieren
            case "stic:move":
            case "stic:rota":
            case "stic:scal":
            case "stic:alph":
            case "stic:colo":
                _tool = s[5..]; if (_tool == "colo") BeginStickerColor(); UpdateInfos(); return;
            case "stic:appl": Apply(); return;
            case "stic:flip": _flip = !_flip; return;
            case "stic:symn": _symmetry = true; return;
            case "stic:symf": _symmetry = false; return;
            case "stic:uscn": _uniform = true; _sy = _sx; UpdateInfos(); return;
            case "stic:uscf": _uniform = false; return;
            case "stic:hist:undo": Undo(); return;
            case "stic:hist:redo": Redo(); return;
            case "stic:hist:open": case "stic:hist:canc": return;

            // ---------------- Bezahlen / Entfernen / Abbrechen
            case "save:open": UpdatePrices(); return;
            case "save:vali": Save(); return;
            case "clea":
                _edit.Stickers.Clear(); _edit.ClearFactory = true; _history.Clear(); _redo.Clear();
                Changed(); return;
            case "canc": _edit = _saved.Clone(); _view.Show(_edit); _history.Clear(); _redo.Clear(); _newBoards.Clear(); UpdatePrices(); return;
        }
        // "Meine Farben" (stPart = pain bzw. stic:colo): <part>:myco:vali|fill|eras|clos
        int k = s.IndexOf("myco:", StringComparison.Ordinal);
        if (k >= 0)
        {
            bool paint = s.StartsWith("pain", StringComparison.Ordinal);
            string box = paint ? "/paint/my_colors_box" : "/stickers/colors/my_colors_box";
            string op = s[(k + 5)..];
            int.TryParse(ui.GetVariable(box + ":num_c") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot);
            if (op == "fill") { Liveries.SetFavorite(slot, ToRgb(paint ? CurrentPaint() : _tint)); FillMyColors(box); }
            else if (op == "eras") { Liveries.SetFavorite(slot, -1); FillMyColors(box); }
            else if (op == "vali")
            {
                int rgb = Liveries.Favorite(slot);
                if (rgb >= 0) { var col = FromRgb(rgb); if (paint) SetPaint(col); else SetTint(col); }
            }
            else if (op == "init") FillMyColors(box);
        }
        else if (s.StartsWith("stic:colo:", StringComparison.Ordinal)) { if (_mode == SMode.Place) _tool = "colo"; }
    }

    static void Leave()
    {
        var car = _dc?.Car;
        Close(true);
        ExitInterior(_lastCar ?? car);
    }

    // ================================================================== Lack
    static void BeginPaint()
    {
        var ui = _ui!; var view = _view!;
        _mode = SMode.Paint;
        _pBackupHas = _edit.HasPaint; _pBackup = _edit.Paint; _pBackup2 = _edit.Paint2; _fBackup = _edit.Finish; _bBackup = _edit.Bicolour;
        if (!_edit.HasPaint)
        {
            _edit.Paint = ToRgba(view.OriginalPaint);
            _edit.Paint2 = ToRgba(view.OriginalPaint2);
            _edit.Bicolour = false;
        }
        if (_edit.Finish == PaintFinish.Original) _edit.Finish = PaintFinish.Gloss;
        _edit.HasPaint = true;   // Vorschau sofort (Abbrechen stellt den alten Stand wieder her)
        _curColor = 1;
        ui.SetValue("/:CurrentColor", 1);
        ui.SetValue("/:BicolourPaint", _edit.Bicolour);
        ui.SetVariable("/:ColorType", FinishName(_edit.Finish));
        const string picker = "/paint/color_box/color_picker";
        ui.SetClipTexture(picker + "/lure_grad", Gradient());
        _pickPos = FindInGradient(_edit.Paint);
        PlaceCursor(picker);
        Swatches(picker);
        view.Show(_edit);
        UpdatePrices();
    }

    static Rgba32 ToRgba(System.Numerics.Vector4 c) => new(B(c.X), B(c.Y), B(c.Z), 255);
    static byte B(float v) => (byte)System.Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    static Rgba32 CurrentPaint() => _curColor == 2 ? _edit.Paint2 : _edit.Paint;

    static void SetPaint(Rgba32 c)
    {
        c.A = 255;
        if (_curColor == 2) { _edit.Paint2 = c; _edit.Bicolour = true; } else _edit.Paint = c;
        _edit.HasPaint = true;
        Swatches("/paint/color_box/color_picker");
        _view!.Show(_edit);
    }

    static void CycleFinish(int dir)
    {
        int f = (int)_edit.Finish; if (f < 1 || f > 3) f = 1;
        f += dir; if (f > 3) f = 1; if (f < 1) f = 3;
        _edit.Finish = (PaintFinish)f;
        _ui!.SetVariable("/:ColorType", FinishName(_edit.Finish));
        _view!.Show(_edit);
        _ui.SetValue("/:Input", true);
    }

    static string FinishName(PaintFinish f)
    {
        bool de = string.Equals(FlashHost.Language, "ge", StringComparison.OrdinalIgnoreCase);
        return f switch
        {
            PaintFinish.Metallic => "Metallic",
            PaintFinish.Matt => de ? "Matt" : "Matte",
            _ => de ? "Glänzend" : "Glossy",
        };
    }

    static void Swatches(string picker)
    {
        bool paint = picker.StartsWith("/paint", StringComparison.Ordinal);
        Tint(picker + "/color1/cube/color", paint ? _edit.Paint : _tint);
        if (paint) Tint(picker + "/color2/cube/color", _edit.Paint2);
    }

    // Farbfelder (color1/2/cube/color, my_colors c0..c5): der Film legt die Clips bei jedem Labelwechsel neu an -> jedes Bild neu einfärben
    static void Tint(string path, Rgba32 c) { _tints[path] = c; ApplyTint(path, c); }

    static void ApplyTint(string path, Rgba32 c)
    {
        var d = _ui?.Find(path);
        if (d == null) return;
        d.Cx = new ColorTransform { Mul = new Paradise.Core.Flash.Vector4(0f, 0f, 0f, 1f), Add = new Paradise.Core.Flash.Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 0f) };
        d.ScriptCx = true;
    }

    static void FillMyColors(string box)
    {
        var ui = _ui!;
        for (int i = 0; i < 6; i++)
        {
            int rgb = Liveries.Favorite(i);
            ui.SetValue(box + "/c" + i + ":isColor", rgb >= 0);
            ui.GotoLabel(box + "/c" + i + "/cube", rgb >= 0 ? "default" : "empty", false);
            if (rgb >= 0) Tint(box + "/c" + i + "/cube/color", FromRgb(rgb));
        }
        Tint(box + "/picked_color/color", box.StartsWith("/paint", StringComparison.Ordinal) ? CurrentPaint() : _tint);
    }

    static int ToRgb(Rgba32 c) => (c.R << 16) | (c.G << 8) | c.B;
    static Rgba32 FromRgb(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);

    // ---- Farbverlauf (lure_grad): x = Farbton, y = oben Weiß -> Mitte reine Farbe -> unten Schwarz
    static Texture2D Gradient()
    {
        if (_grad != null && _grad.Gpu != null) return _grad;
        const int W = 256, H = 128;
        _grad = new Texture2D(W, H);
        var px = new Color32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var g = GradColor(x / (W - 1f), y / (H - 1f));   // Zeile 0 = oben (Clip-Texturen wie .2db)
                px[y * W + x] = new Color32(g.R, g.G, g.B, 255);
            }
        _grad.SetPixels32(px);
        _grad.Apply();
        return _grad;
    }

    static Rgba32 GradColor(float x, float y)
    {
        // rechter Rand: Graustufen
        V3 hue = x > 0.94f ? new V3(0.5f) : HsvToRgb(System.Math.Clamp(x / 0.94f, 0f, 1f));
        V3 c = y < 0.5f ? V3.Lerp(V3.One, hue, y * 2f) : V3.Lerp(hue, V3.Zero, (y - 0.5f) * 2f);
        if (x > 0.94f) c = V3.Lerp(V3.One, V3.Zero, y);
        return new Rgba32(B(c.X), B(c.Y), B(c.Z), 255);
    }

    /// <summary>Color.HSVToRGB(h, 1, 1).</summary>
    static V3 HsvToRgb(float h)
    {
        float h6 = (h % 1f) * 6f; int i = (int)MathF.Floor(h6); float f = h6 - i;
        return i switch
        {
            0 => new V3(1f, f, 0f),
            1 => new V3(1f - f, 1f, 0f),
            2 => new V3(0f, 1f, f),
            3 => new V3(0f, 1f - f, 1f),
            4 => new V3(f, 0f, 1f),
            _ => new V3(1f, 0f, 1f - f),
        };
    }

    static V2 FindInGradient(Rgba32 c)
    {
        float best = float.MaxValue; var bp = new V2(0.5f, 0.5f);
        for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 64; x++)
            {
                var p = new V2(x / 64f, y / 32f);
                var g = GradColor(p.X, p.Y);
                float d = (g.R - c.R) * (g.R - c.R) + (g.G - c.G) * (g.G - c.G) + (g.B - c.B) * (g.B - c.B);
                if (d < best) { best = d; bp = p; }
            }
        return bp;
    }

    static void PlaceCursor(string picker)
    {
        var ui = _ui!;
        var r = ui.ScreenRect(picker + "/lure_grad");
        if (!r.HasValue) return;
        var gui = new FV2(Lerp(r.Value.xMin, r.Value.xMax, _pickPos.X), Lerp(r.Value.yMin, r.Value.yMax, _pickPos.Y));
        var local = ui.GuiToLocal(picker, gui);
        if (local.HasValue) ui.SetPosition(picker + (_curColor == 2 ? "/cursor_2" : "/cursor"), local.Value);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static float InvLerp(float a, float b, float v) => System.Math.Abs(b - a) < 1e-6f ? 0f : System.Math.Clamp((v - a) / (b - a), 0f, 1f);

    /// <summary>Maus im Farbverlauf: Farbe wählen (true, wenn die Maus darin liegt und gedrückt ist).</summary>
    static bool PickWithMouse(string picker, Action<Rgba32> set)
    {
        if (!FlashInput.GetMouseButton(0)) return false;
        var r = _ui!.ScreenRect(picker + "/lure_grad");
        if (!r.HasValue) return false;
        var mp = MousePixel();
        if (mp.X < r.Value.xMin || mp.X > r.Value.xMax || mp.Y < r.Value.yMin || mp.Y > r.Value.yMax) return false;
        _pickPos = new V2(InvLerp(r.Value.xMin, r.Value.xMax, mp.X), InvLerp(r.Value.yMin, r.Value.yMax, mp.Y));
        set(GradColor(_pickPos.X, _pickPos.Y));
        PlaceCursor(picker);
        return true;
    }

    static void MovePick(string picker, V2 delta, Action<Rgba32> set)
    {
        _pickPos = new V2(System.Math.Clamp(_pickPos.X + delta.X, 0f, 1f), System.Math.Clamp(_pickPos.Y + delta.Y, 0f, 1f));
        set(GradColor(_pickPos.X, _pickPos.Y));
        PlaceCursor(picker);
    }

    /// <summary>Mauspixel, Ursprung oben links (wie GAME_MODE_MAP.MousePixel).</summary>
    static V2 MousePixel()
    {
        var m = FlashInput.mousePosition;   // Unity: y von unten
        return new V2(m.x, Paradise.Core.Flash.Screen.height - m.y);
    }

    // ================================================================== Sticker-Liste
    static void OpenList()
    {
        var ui = _ui!;
        _cats.Clear();
        foreach (var b in StickerLibrary.Boards) if (b.Type == 1 || b.Type == 4 || b.Type == 5) _cats.Add(b);
        if (_cats.Count == 0 && !StickerLibrary.Ready) _listPending = true;
        if (_board != null) { int i = _cats.IndexOf(_board); if (i >= 0) _catIndex = i; }
        ui.SetValue("/:CurrentStickersCat", System.Math.Clamp(_catIndex, 0, System.Math.Max(0, _cats.Count - 1)));
        FillCategory();
    }

    static void FillCategory()
    {
        var ui = _ui!;
        int nb = _cats.Count;
        int.TryParse(ui.GetVariable("/:CurrentStickersCat") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int cur);
        if (nb > 0) cur = ((cur % nb) + nb) % nb;
        _catIndex = cur;
        ui.SetValue("/:NbStickersCat", System.Math.Max(1, nb));
        ui.SetValue("/:CurrentStickersCat", cur);
        var b = nb > 0 ? _cats[cur] : null;
        if (b == null)
        {
            ui.SetValue("/:NbStickersCurPage", 0);
            ui.SetVariable("/:StickersCatStr", StickerLibrary.Ready ? "-" : "…");
            ui.SetValue("/:CategoryLoaded", StickerLibrary.Ready);
            return;
        }
        bool owned = b.Price <= 0 || Liveries.HasBoard(b.Id) || _newBoards.Contains(b.Id);
        bool locked = (b.Type == 4 || b.Type == 5) && !Liveries.HasBoard(b.Id);
        string name = b.NameHc != 0 ? FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, (int)b.NameHc) : "";
        ui.SetVariable("/:StickersCatStr", string.IsNullOrEmpty(name) ? b.BankFile : name);
        ui.SetVariable("/:PriceCat", owned ? "" : Money(b.Price));
        ui.SetValue("/:IsCategoryBought", owned);
        ui.SetValue("/:StickLocked", locked);
        ui.SetValue("/:DLC_Locked", false);
        ui.SetValue("/:CanColourSticker", b.Mono);
        bool ready = StickerLibrary.Request(b);
        // Planche fehlerhaft oder lädt zu lange: Liste trotzdem freigeben (sonst läuft die Ladeanimation endlos)
        bool give = ready || b.Failed || (_listSince > 0f && _clock - _listSince > 6f);
        if (!ready && !give && _listSince < 0f) _listSince = _clock;
        int n = ready ? b.Count : (b.Failed ? 0 : 24);
        ui.SetValue("/:NbStickersCurPage", System.Math.Min(24, n));
        ui.SetValue("/:CategoryLoaded", give);
        _listPending = !ready && !b.Failed;
        if (give) _listSince = ready || b.Failed ? -1f : _listSince;
        Log.Debug("Sticker-Liste: Planche " + (cur + 1) + "/" + nb + " " + b.BankFile + (ready ? " (" + b.Count + " Sticker)" : b.Failed ? " (Fehler)" : " (lädt …)"));
        for (int i = 0; i < 24; i++)
        {
            var st = ready && b.Stickers != null && i < b.Count ? b.Stickers[i] : null;
            ui.SetClipTexture("/stickers/list/container/st" + i + "/lure", FlashTex(st));
        }
    }

    // Flash-Hülle je Sticker (gleiche GPU-Textur, kein eigener Besitz)
    static readonly Dictionary<StickerLibrary.Sticker, Texture> _flashTex = new();
    static Texture? FlashTex(StickerLibrary.Sticker? st)
    {
        if (st?.Gpu == null) return null;
        if (!_flashTex.TryGetValue(st, out var t)) { t = Texture.Wrap(st.Gpu, st.Width, st.Height); _flashTex[st] = t; }
        return t;
    }

    static void PickSticker()
    {
        var ui = _ui!;
        if (_catIndex < 0 || _catIndex >= _cats.Count) return;
        var b = _cats[_catIndex];
        int.TryParse(ui.GetVariable("/:SelectedSticker") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx);
        if (!b.Loaded || idx < 0 || idx >= b.Count) return;
        _board = b; _stickerIndex = idx;
        _mode = SMode.Place;
        _tool = "move";
        _rot = 0f; _sx = _sy = 1f; _alpha = 1f; _flip = false;
        _uniform = true; ui.SetValue("/:IsUniformScale", true);
        _symmetry = false; ui.SetValue("/:UseSymmetry", false);
        if (!b.Mono) _tint = new Rgba32(255, 255, 255, 255);
        ui.SetValue("/:CanColourSticker", b.Mono);
        if (b.Price > 0 && !Liveries.HasBoard(b.Id)) _newBoards.Add(b.Id);
        UpdateInfos();
        UpdatePrices();
    }

    static void BeginStickerColor()
    {
        const string picker = "/stickers/colors/color_box/color_picker";
        _tintBackup = _tint;
        _ui!.SetClipTexture(picker + "/lure_grad", Gradient());
        _curColor = 1;
        _pickPos = FindInGradient(_tint);
        PlaceCursor(picker);
        Swatches(picker);
        FillMyColors("/stickers/colors/my_colors_box");
    }

    static void SetTint(Rgba32 c) { c.A = 255; _tint = c; Swatches("/stickers/colors/color_box/color_picker"); }

    static void UpdateInfos()
    {
        var ui = _ui!;
        var ci = Paradise.Core.Util.GermanFormat.Number;
        ui.SetVariable("/:StickerRotateAngleStr", (((_rot % 360f) + 360f) % 360f).ToString("0.0", ci) + "°");
        ui.SetVariable("/:StickerXScaleStr", (_sx * 100f).ToString("0", ci) + " %");
        ui.SetVariable("/:StickerYScaleStr", (_sy * 100f).ToString("0", ci) + " %");
        ui.SetVariable("/:StickerAlphaStr", (_alpha * 100f).ToString("0", ci) + " %");
    }

    // ================================================================== Platzieren (Strahl auf die Lackflächen, UV-Satz 1)
    /// <summary>Unity MeshCollider je Lack-Mesh: alle Teile eines Knotens mit Lack-Material (Glas/Felge davor verdecken).</summary>
    sealed class PickMesh
    {
        public CarNode Node = null!;
        public MeshData Md = null!;
        public int[] Tris = Array.Empty<int>();
        public bool[] Paint = Array.Empty<bool>();   // je Dreieck
        public List<System.Numerics.Vector2> Uv = null!;
    }
    static readonly List<PickMesh> _pick = new();

    static int PickTriangles() { int n = 0; foreach (var p in _pick) n += p.Paint.Length; return n; }

    static void BuildPickMeshes()
    {
        _pick.Clear();
        if (_dc == null) return;
        var paint = new HashSet<CarMaterial>(_dc.Model.PaintMaterials);
        void Walk(CarNode n)
        {
            if (!n.Active) return;
            if (n.RendererEnabled && n.Parts.Count > 0)
            {
                bool has = false;
                foreach (var p in n.Parts) if (paint.Contains(p.Material)) { has = true; break; }
                if (has)
                {
                    // je Quell-Mesh ein Strahl-Netz (alle Submeshes, die dieser Knoten zeichnet)
                    var byMd = new Dictionary<MeshData, (List<int> tris, List<bool> paint)>();
                    foreach (var p in n.Parts)
                    {
                        var md = p.Source;
                        if (md == null || p.SubMesh < 0 || p.SubMesh >= md.SubMeshes.Count) continue;
                        if (!byMd.TryGetValue(md, out var e)) { e = (new List<int>(), new List<bool>()); byMd[md] = e; }
                        var sm = md.SubMeshes[p.SubMesh];
                        bool isPaint = paint.Contains(p.Material);
                        for (int i = 0; i + 2 < sm.Count; i += 3) { e.tris.Add(sm[i]); e.tris.Add(sm[i + 1]); e.tris.Add(sm[i + 2]); e.paint.Add(isPaint); }
                    }
                    foreach (var kv in byMd)
                    {
                        var md = kv.Key;
                        var uv = CarModel.StickerUvSet == 0 || !md.HasUV1 || md.UV1.Count != md.Positions.Count ? md.UV0 : md.UV1;
                        if (uv.Count != md.Positions.Count) continue;
                        _pick.Add(new PickMesh { Node = n, Md = md, Tris = kv.Value.tris.ToArray(), Paint = kv.Value.paint.ToArray(), Uv = uv });
                    }
                }
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_dc.Model.Visual);
    }

    struct CarHit { public PickMesh M; public int Tri; public float Dist, Bu, Bv; public V3 Point, Normal; }

    /// <summary>Strahl (Render-Raum) gegen die Lack-Meshes; erste Fläche zählt (Glas/Felge davor -> keine Platzierung).</summary>
    static bool CastCar(V3 origin, V3 dir, out CarHit hit)
    {
        hit = default; bool any = false; float best = 40f;
        foreach (var pm in _pick)
        {
            var w = pm.Node.World;
            var pos = pm.Md.Positions;
            var t = pm.Tris;
            for (int k = 0, tri = 0; k + 2 < t.Length; k += 3, tri++)
            {
                var p0 = V3.Transform(pos[t[k]], w); var p1 = V3.Transform(pos[t[k + 1]], w); var p2 = V3.Transform(pos[t[k + 2]], w);
                // Möller-Trumbore, beidseitig (MeshCollider trifft beide Seiten nicht – Unity: nur Vorderseite; hier nach Normale gerichtet)
                var e1 = p1 - p0; var e2 = p2 - p0;
                var pv = V3.Cross(dir, e2);
                float det = V3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var tv = origin - p0;
                float u = V3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) continue;
                var qv = V3.Cross(tv, e1);
                float v = V3.Dot(dir, qv) * inv; if (v < 0f || u + v > 1f) continue;
                float d = V3.Dot(e2, qv) * inv;
                if (d <= 1e-4f || d >= best) continue;
                var n = V3.Normalize(V3.Cross(e1, e2));
                if (V3.Dot(n, dir) > 0f) n = -n;
                best = d; any = true;
                hit = new CarHit { M = pm, Tri = tri, Dist = d, Bu = u, Bv = v, Point = origin + dir * d, Normal = n };
            }
        }
        return any && hit.M.Paint[hit.Tri];
    }

    /// <summary>Weltvektor in der Tangentialebene des Treffers -> UV-Vektor (über dP/du, dP/dv des Dreiecks).</summary>
    static bool Jacobian(in CarHit h, out V3 dPdu, out V3 dPdv, out V2 uvHit)
    {
        dPdu = dPdv = V3.Zero; uvHit = default;
        var pm = h.M; var w = pm.Node.World; int k = h.Tri * 3;
        int i0 = pm.Tris[k], i1 = pm.Tris[k + 1], i2 = pm.Tris[k + 2];
        V3 p0 = V3.Transform(pm.Md.Positions[i0], w), p1 = V3.Transform(pm.Md.Positions[i1], w), p2 = V3.Transform(pm.Md.Positions[i2], w);
        V2 u0 = pm.Uv[i0], u1 = pm.Uv[i1], u2 = pm.Uv[i2];
        uvHit = u0 * (1f - h.Bu - h.Bv) + u1 * h.Bu + u2 * h.Bv;   // RaycastHit.textureCoord2
        V3 e1 = p1 - p0, e2 = p2 - p0;
        float du1 = u1.X - u0.X, dv1 = u1.Y - u0.Y, du2 = u2.X - u0.X, dv2 = u2.Y - u0.Y;
        float det = du1 * dv2 - du2 * dv1;
        if (MathF.Abs(det) < 1e-12f) return false;
        dPdu = (e1 * dv2 - e2 * dv1) / det;
        dPdv = (e2 * du1 - e1 * du2) / det;
        return true;
    }

    static V2 ToUv(V3 w, V3 T, V3 B)
    {
        float a = V3.Dot(T, T), b = V3.Dot(T, B), c = V3.Dot(B, B);
        float det = a * c - b * b;
        if (MathF.Abs(det) < 1e-12f) return V2.Zero;
        float rt = V3.Dot(T, w), rb = V3.Dot(B, w);
        return new V2((c * rt - b * rb) / det, (a * rb - b * rt) / det);
    }

    /// <summary>Lage des Autos im Render-Raum (Spiegeln an der Längsachse: lokales X).</summary>
    static M4 CarMatrix() => _dc!.Pose * (_t.Streamer?.RootMatrix ?? M4.Identity);
    static V3 MirrorPoint(V3 p) { var m = CarMatrix(); M4.Invert(m, out var inv); var l = V3.Transform(p, inv); l.X = -l.X; return V3.Transform(l, m); }
    static V3 MirrorDir(V3 v) { var m = CarMatrix(); M4.Invert(m, out var inv); var l = V3.TransformNormal(v, inv); l.X = -l.X; return V3.TransformNormal(l, m); }

    static StickerPlacement? MakePlacement(in CarHit hit, V3 right, V3 up)
    {
        if (_board == null || !Jacobian(hit, out var T, out var Bt, out var uv)) return null;
        float hw = BaseHalfSize * _sx, hh = BaseHalfSize * _sy;
        var a = ToUv(right * hw * (_flip ? -1f : 1f), T, Bt);
        var b = ToUv(up * hh, T, Bt);
        if (a.LengthSquared() > 1f || b.LengthSquared() > 1f) return null;   // entartet (Naht)
        return new StickerPlacement
        {
            Board = (byte)_board.Id,
            Index = (byte)_stickerIndex,
            U = uv.X,
            V = uv.Y,
            A = a,
            B = b,
            Tint = new Rgba32(_tint.R, _tint.G, _tint.B, (byte)System.Math.Clamp((int)MathF.Round(_alpha * 255f), 0, 255)),
            Flags = (byte)(_flip ? 1 : 0),
        };
    }

    /// <summary>Vorschau-Sticker unter der Maus (+ Spiegelbild bei Symmetrie).</summary>
    static List<StickerPlacement> PlacementsAtMouse()
    {
        var res = new List<StickerPlacement>();
        var cam = _t.Camera;
        if (cam == null || _board == null || _ui == null || _ui.MouseOverButton) return res;
        var mp = MousePixel();
        if (!_t.ScreenRay(mp.X, mp.Y, out var origin, out var dir)) return res;
        if (!CastCar(origin, dir, out var hit)) return res;
        var n = hit.Normal;
        // Bildschirm-Rechts/-Oben (Unity cam.transform.right/up) aus der Projektion: Strahlen ein Stück rechts bzw. oberhalb
        if (!_t.ScreenRay(mp.X + 8f, mp.Y, out _, out var dr) || !_t.ScreenRay(mp.X, mp.Y - 8f, out _, out var du)) return res;
        var camRight = V3.Normalize(dr - dir); var camUp = V3.Normalize(du - dir);
        var right = camRight - n * V3.Dot(camRight, n);
        if (right.LengthSquared() < 1e-6f) right = camUp - n * V3.Dot(camUp, n);
        right = V3.Normalize(right);
        // Unity: up = Cross(right, n) – in der Tangentialebene senkrecht zu rechts, zur Bildschirm-Oberseite
        var up = V3.Normalize(V3.Cross(n, right));
        if (V3.Dot(up, camUp) < 0f) up = -up;
        var q = System.Numerics.Quaternion.CreateFromAxisAngle(n, _rot * MathF.PI / 180f);
        right = V3.Transform(right, q); up = V3.Transform(up, q);
        var p = MakePlacement(hit, right, up);
        if (p == null) return res;
        res.Add(p);
        if (_symmetry)
        {
            var mo = MirrorPoint(origin); var md = MirrorDir(dir);
            if (CastCar(mo, V3.Normalize(md), out var h2) && (h2.Point - MirrorPoint(hit.Point)).LengthSquared() < 0.15f * 0.15f)
            {
                var p2 = MakePlacement(h2, MirrorDir(right), MirrorDir(up));
                if (p2 != null && new V2(p2.U - p.U, p2.V - p.V).LengthSquared() > 1e-5f) res.Add(p2);
            }
        }
        return res;
    }

    static void Apply()
    {
        if (_mode != SMode.Place || _board == null) return;
        var list = PlacementsAtMouse();
        if (list.Count == 0) return;
        if (_edit.Stickers.Count + list.Count > CarLivery.MaxStickers) { Log.Warn("Sticker-Shop: maximal " + CarLivery.MaxStickers + " Sticker"); return; }
        _edit.Stickers.AddRange(list);
        _history.Add(list.Count);
        _redo.Clear();
        Changed();
    }

    static void Undo()
    {
        if (_history.Count == 0) return;
        int n = _history[^1]; _history.RemoveAt(_history.Count - 1);
        n = System.Math.Min(n, _edit.Stickers.Count);
        var removed = _edit.Stickers.GetRange(_edit.Stickers.Count - n, n);
        _edit.Stickers.RemoveRange(_edit.Stickers.Count - n, n);
        _redo.Add(removed);
        Changed();
    }

    static void Redo()
    {
        if (_redo.Count == 0) return;
        var l = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        _edit.Stickers.AddRange(l);
        _history.Add(l.Count);
        Changed();
    }

    // ================================================================== Preise / Speichern
    static bool PaintChanged() => _edit.HasPaint != _saved.HasPaint || (_edit.HasPaint && (!Same(_edit.Paint, _saved.Paint) || !Same(_edit.Paint2, _saved.Paint2) || _edit.Finish != _saved.Finish || _edit.Bicolour != _saved.Bicolour));
    static bool Same(Rgba32 a, Rgba32 b) => a.R == b.R && a.G == b.G && a.B == b.B;
    static bool StickersChanged()
    {
        if (_edit.Stickers.Count != _saved.Stickers.Count || _edit.ClearFactory != _saved.ClearFactory) return true;
        for (int i = 0; i < _edit.Stickers.Count; i++) if (!ReferenceEquals(_edit.Stickers[i], _saved.Stickers[i]) && _edit.Stickers[i].U != _saved.Stickers[i].U) return true;
        return false;
    }
    static bool Modified() => PaintChanged() || StickersChanged();

    static void Changed()
    {
        var ui = _ui!; var view = _view!;
        view.Show(_edit);
        ui.SetValue("/:HasModifiedVehicle", Modified());
        ui.SetValue("/:CanUndo", _history.Count > 0);
        bool factoryOn = view.HasFactory && !_edit.ClearFactory;
        ui.SetValue("/:IsVehiclePaintable", !factoryOn);
        UpdatePrices();
    }

    static long Total(out long labour, out long paint, out long stickers)
    {
        bool p = PaintChanged(), s = StickersChanged();
        labour = p || s ? StickerLibrary.LabourPrice : 0;
        paint = p ? StickerLibrary.PaintPrice : 0;
        stickers = 0;
        var used = new HashSet<int>();
        foreach (var st in _edit.Stickers) used.Add(st.Board);
        foreach (var id in used)
        {
            if (Liveries.HasBoard(id)) continue;
            var b = StickerLibrary.ById(id);
            if (b != null && b.Price > 0) stickers += b.Price;
        }
        if (_edit.ClearFactory && !_saved.ClearFactory) stickers += StickerLibrary.ErasePrice;
        return labour + paint + stickers;
    }

    static void UpdatePrices()
    {
        var ui = _ui; if (ui == null) return;
        long total = Total(out long labour, out long paint, out long stickers);
        var sv = TDU2Session.Save;
        long money = sv != null ? sv.money : 0;
        bool enough = sv == null || money >= total;
        ui.SetVariable("/:LabourCostStr", Money(labour));
        ui.SetVariable("/:PaintCostStr", Money(paint));
        ui.SetVariable("/:StickersCostStr", Money(stickers));
        ui.SetVariable("/:TotalPriceStr", Money(total));
        ui.SetVariable("/:current_price", Money(total));
        ui.SetVariable("/:MyMoneyStr", sv != null ? Money(money) : " ");
        ui.SetVariable("/:ClubMoney", " ");
        ui.SetValue("/:EnoughMoney", enough);
    }

    static string Money(long v) => "$ " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number);

    static void Save()
    {
        if (_dc == null) return;
        long total = Total(out _, out _, out _);
        var sv = TDU2Session.Save;
        if (sv != null) sv.money = System.Math.Max(0, sv.money - total);
        var boards = new List<int>();
        foreach (var st in _edit.Stickers) if (!Liveries.HasBoard(st.Board)) boards.Add(st.Board);
        if (boards.Count > 0) Liveries.AddBoards(boards);
        Liveries.Set(_dc.Car.FileName, _edit);   // speichert (lokal + Spielstand) und meldet die Änderung
        _saved = _edit.Clone();
        Log.Info("Sticker-Shop: gespeichert " + _dc.Car.FileName + " für $" + total + " (" + _edit.Stickers.Count + " Sticker)");
    }

    // ================================================================== Eingabe + Kamera (aus InteriorUtils.UpdateInside)
    /// <summary>Jedes Bild bei offenem Shop-Menü (Unity Update + LateUpdate). Kamera setzt der Shop selbst.</summary>
    internal static void UpdateShop(float dt)
    {
        var ui = _ui;
        if (!_open || ui == null || _view == null || _dc == null) return;
        _clock += dt;
        StickerLibrary.Update();
        // Bestätigungsbox offen (Bezahlen, Entfernen, Abbrechen, Verlassen): Tasten gehören der Box
        if (FlashDialog.Active) { foreach (var kv in _tints) ApplyTint(kv.Key, kv.Value); PlaceCamera(); return; }
        if (_listPending && _mode == SMode.List && StickerLibrary.Ready)
        {
            var cb = _cats.Count > 0 ? _cats[System.Math.Clamp(_catIndex, 0, _cats.Count - 1)] : null;
            if (_cats.Count == 0) OpenList();
            else if (cb == null || cb.Loaded || cb.Failed || (_listSince > 0f && _clock - _listSince > 6f)) FillCategory();
        }
        // ---- Film-Tasten (globaler Flash-Handler)
        bool myColors = (_mode == SMode.Paint && IsOpenLabel("/paint/my_colors_box")) || (_mode == SMode.Place && _tool == "colo" && IsOpenLabel("/stickers/colors/my_colors_box"));
        bool arrowsToFilm = _mode == SMode.Menu || _mode == SMode.List || myColors || _mode == SMode.Place;
        if (arrowsToFilm) FlashNav.Directions(ui);
        bool q = TDU2Controls.RawKeyPressed(0x51) || TDU2Controls.RawKeyPressed(0x21) || TDU2Nav.LB();
        bool e = TDU2Controls.RawKeyPressed(0x45) || TDU2Controls.RawKeyPressed(0x22) || TDU2Nav.RB();
        if (_mode == SMode.List)
        {
            if (q) ui.CallClip("/stickers/list", "changeCategory", "left");
            if (e) ui.CallClip("/stickers/list", "changeCategory", "right");
        }
        if (_mode == SMode.Paint && !myColors)
        {
            if (q) ui.CallClip("/paint", "changeType", "left");
            if (e) ui.CallClip("/paint", "changeType", "right");
            var dv = V2.Zero;
            if (TDU2Nav.LeftHeld()) dv.X -= 1f; if (TDU2Nav.RightHeld()) dv.X += 1f;
            if (TDU2Nav.UpHeld()) dv.Y -= 1f; if (TDU2Nav.DownHeld()) dv.Y += 1f;
            if (dv != V2.Zero) MovePick("/paint/color_box/color_picker", dv * dt * 0.35f, SetPaint);
            PickWithMouse("/paint/color_box/color_picker", SetPaint);
        }
        if (_mode == SMode.Place && _tool == "colo" && !myColors) PickWithMouse("/stickers/colors/color_box/color_picker", SetTint);
        if (FlashNav.Pressed(FlashKey.Confirm)) FlashNav.Send(ui, FlashKey.Confirm);
        if (FlashNav.Pressed(FlashKey.X)) FlashNav.Send(ui, FlashKey.X);
        if (FlashNav.Pressed(FlashKey.Y) || TDU2Controls.RawKeyPressed(0x5A)) FlashNav.Send(ui, FlashKey.Y);
        // Zurück gehört dem Film (B: Ebene zurück; im Hauptmenü fragt er "cancel_st" und meldet dann shko/st_quit) – wie im Spiel
        // schließt das Spiel das Menü nicht selbst
        if (FlashNav.Pressed(FlashKey.Back)) FlashNav.Send(ui, FlashKey.Back);
        if (!_open) return;

        // ---- Platzieren
        var input = _t.Input;
        float wheel = FlashInput.mouseScrollDelta * 0.1f;   // Unity "Mouse ScrollWheel": 0,1 je Raste
        if (_mode == SMode.Place && _board != null)
        {
            if (TDU2Controls.RawKeyPressed(0x46)) _flip = !_flip;   // F
            bool ctrl = TDU2Controls.RawKeyDown(0x11);
            if (MathF.Abs(wheel) > 1e-4f && !ctrl && _tool != "move" && _tool != "colo")
            {
                float st = MathF.Sign(wheel);
                if (_tool == "rota") _rot += st * 5f;
                else if (_tool == "scal") { _sx = System.Math.Clamp(_sx * (1f + st * 0.06f), 0.1f, 6f); if (_uniform) _sy = _sx; }
                else if (_tool == "alph") _alpha = System.Math.Clamp(_alpha + st * 0.05f, 0.1f, 1f);
                UpdateInfos();
                wheel = 0f;
            }
            if (TDU2Controls.RawKeyDown(0x51)) { _rot -= 90f * dt; UpdateInfos(); }   // Q
            if (TDU2Controls.RawKeyDown(0x45)) { _rot += 90f * dt; UpdateInfos(); }   // E
            if (TDU2Controls.RawKeyDown(0x6B) || TDU2Controls.RawKeyDown(0xBB)) { _sx = MathF.Min(6f, _sx * (1f + dt)); if (_uniform) _sy = _sx; UpdateInfos(); }
            if (TDU2Controls.RawKeyDown(0x6D) || TDU2Controls.RawKeyDown(0xBD)) { _sx = MathF.Max(0.1f, _sx * (1f - dt)); if (_uniform) _sy = _sx; UpdateInfos(); }
            bool pu = TDU2Controls.RawKeyDown(0x21), pd = TDU2Controls.RawKeyDown(0x22);
            if (!_uniform && (pu || pd)) { _sy = System.Math.Clamp(_sy * (1f + (pu ? 1f : -1f) * dt), 0.1f, 6f); UpdateInfos(); }
            if (MathF.Abs(wheel) > 1e-4f) _dist = System.Math.Clamp(_dist * (1f - wheel * 1.5f), 2.5f, 14f);

            _view.Preview.Clear();
            if (_tool != "colo" || !ui.MouseOverButton) _view.Preview.AddRange(PlacementsAtMouse());
            _view.Refresh();
            if (FlashInput.GetMouseButtonDown(0) && !ui.MouseOverButton && _tool != "colo" && _view.Preview.Count > 0) FlashNav.Send(ui, FlashKey.Confirm);   // Werkzeugleiste: A = aufkleben (stsh:stic:appl)
        }
        else
        {
            if (_view.Preview.Count > 0) { _view.Preview.Clear(); _view.Refresh(); }
            if (MathF.Abs(wheel) > 1e-4f && !ui.MouseOverButton) _dist = System.Math.Clamp(_dist * (1f - wheel * 1.5f), 2.5f, 14f);
        }
        if (input.RightMouseDown)
        {
            // Unity GetAxis("Mouse X/Y") ≈ Pixel × 0,1
            _yaw += input.MouseDeltaX * 0.1f * 3f;
            _pitch = System.Math.Clamp(_pitch - (-input.MouseDeltaY) * 0.1f * 3f, -5f, 70f);
        }

        // ---- LateUpdate: Farbfelder, Kamera
        foreach (var kv in _tints) ApplyTint(kv.Key, kv.Value);
        PlaceCamera();
    }

    static bool IsOpenLabel(string path)
    {
        var l = _ui!.Label(path);
        return l == "open" || l == "stop";
    }

    /// <summary>Kamera kreist um das Auto (Unity LateUpdate): nicht durch Wände/Boden des Shops.</summary>
    static void PlaceCamera()
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        var target = _center;
        float yaw = _yaw * MathF.PI / 180f, pitch = _pitch * MathF.PI / 180f;
        // Quaternion.Euler(pitch, yaw, 0) * forward
        var fwd = new V3(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var pos = target - fwd * _dist;
        var phys = Phys;
        if (phys != null && phys.Raycast(target, -fwd, _dist, out var hit)) pos = hit.Point + fwd * 0.3f;
        var look = V3.Normalize(target - pos);
        _t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(pos);
        cam.Override = (look, V3.UnitY);
        _t.FieldOfView = 50f;
    }
}
