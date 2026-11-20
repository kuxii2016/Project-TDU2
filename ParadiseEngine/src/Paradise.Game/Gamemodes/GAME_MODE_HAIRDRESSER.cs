using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Friseur (Port Unity TDU2Hairdresser): Spots EHD_*, Interieur ihd_b bzw. ihair_ca, Auswahlpunkt "Hairdresser_area" Typ 29.
/// Original-Film frontend/&lt;res&gt;/shopsavatar.bnk ab Label "hairdresser": openMenuHairDresser() -> hairdresser_edit, menu_sq mit
/// Frisur (manhaircut/womanhaircut), Bart (pilosity), Augenbrauen, Abbrechen (hd_cancel), Bestätigen &amp; bezahlen (hd_valid).
/// Untermenü hair_menu: FSCommands "hadr:edit:&lt;maha|woha|pilo&gt;:&lt;type|cuts|colo&gt;:scne|scpr", Anzeige über _root.HairType/HairCut/
/// HairColor (bzw. Pilosity*), A = ":vali", B = ":canc". Bezahlen: "evmo" (EnoughMoney), Dialog hadr_valid -> "hadr:edit:vali", "moup",
/// "hadr:clos"; Abbruch -> "hadr:edit:canc", "hadr:clos". Frisuren aus DBClothe (Teil 3, _haircutstyle 1 kurz, 2 lang, 4 Afro,
/// 9 zusammengebunden), Bärte (Teil 9: 8 Kinnbart, 7 Schnurrbart, 6 Vollbart, M_NO_BEARD rasiert). Avatar im Friseurstuhl
/// (Darsteller "Avatar" der HEAD-Szene, hd_sitstand_loop), Kamera C_HEAD, die Friseurin an ihrem M_HEAD-Platz.
/// Interieur bleibt geladen (InteriorUtils); beim Schließen zurück in den Modus des Raums.
/// </summary>
public class GAME_MODE_HAIRDRESSER : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_HAIRDRESSER;
    static readonly ScreenDef[] _screens = { ScreenDefs.Avatar };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Avatar;

    const int StyleShort = 1, StyleLong = 2, StyleAfro = 4, StyleAttached = 9;
    const int BeardFull = 6, BeardMoustache = 7, BeardGoatee = 8;

    static VmfPlayer? _hui;
    static bool _open;
    static float _startAt = -1f, _lastEsc = -10f, _clock;
    static AvatarConfig _orig = new(), _edit = new();
    static AvatarConfig? _menuBackup;
    static string? _menu;   // "maha", "woha", "pilo"
    static InteriorNpcs.Npc? _npc;
    static AvatarPreview? _preview;

    static int[]? _types;
    static readonly Dictionary<int, List<ClotheInfo>> _byStyle = new();
    static int _typeIdx, _cutIdx, _color;

    static Vector3 _camPos, _camFwd, _camUp; static float _camFov; static bool _camInit;

    /// <summary>Auswahlpunkt Typ 29 (InteriorUtils.Activate): Friseur öffnen.</summary>
    internal static bool Open()
    {
        if (!AvatarLibrary.Ready) { Log.Info("Friseur: Avatar-Daten werden noch geladen …"); return false; }
        if (_t == null || _scene == null) return false;
        GameMode.Set(Gamemode.GAME_MODE_HAIRDRESSER);
        return true;
    }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE && _open) return;   // Weiterspielen nach dem Pausemenü
        var ui = ctx.Open(ScreenDefs.Avatar);
        if (ui == null || _t == null) { Log.Warn("Friseur: shopsavatar.bnk nicht ladbar"); GameMode.Set(RoomMode); return; }
        if (_hui != ui) { if (_hui != null) _hui.FSCommand -= OnCommand; _hui = ui; ui.FSCommand += OnCommand; }
        _orig = AvatarConfigs.Local.Clone();
        _edit = _orig.Clone();
        _menu = null; _types = null; _menuBackup = null;
        _npc = InteriorNpcs.First("Hair") ?? InteriorNpcs.First(null);
        if (_npc != null) { var a = InteriorNpcs.FindActor(_npc.Hc, "HEAD"); if (a != null) InteriorNpcs.SetPose(_npc.ConfigName, a); }
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        _preview?.Dispose();
        _preview = new AvatarPreview(_v.Renderer);
        var act = InteriorNpcs.FindObject("Avatar", "HEAD");
        if (act == null) Log.Warn("Friseur: Darsteller \"Avatar\" der HEAD-Szene fehlt");
        _preview.Place(act, act?.Clip, _orig.Sex, "hd_sitstand_loop-m", "co_stand-w");
        if (act == null) _preview.Pose = Matrix4x4.CreateTranslation(_t.Streamer != null && _t.Camera != null
            ? Vector3.Transform(_t.Streamer.RenderToGame(_t.Camera.Position) + _t.Camera.Forward * 2f - Vector3.UnitY * 1.6f, _holderInv) : Vector3.Zero);
        _preview.Show(_edit, true);
        _camInit = false;
        _open = true;
        ui.Visible = true;
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:ClubMoney", " ");
        UpdateMoney();
        ui.GotoLabel("/", "hairdresser", true);
        _startAt = _clock + 0.6f;
        Log.Info("Friseur geöffnet");
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: Friseur bleibt offen
        CloseState();
        base.Exit(ctx, to);
    }

    static void CloseState()
    {
        if (!_open) return;
        _open = false; _startAt = -1f;
        _preview?.Dispose(); _preview = null;
        if (_npc != null) InteriorNpcs.ResetPose(_npc);
        _npc = null;
        if (_hui != null) { _hui.FSCommand -= OnCommand; _hui = null; }
        if (_t != null) _t.NearClip = 0.05f;
        Log.Info("Friseur geschlossen");
    }

    /// <summary>Unity Close: zurück in den Modus des Raums (Laufen).</summary>
    static void Close()
    {
        CloseState();
        GameMode.Set(RoomMode);
    }

    void StartMenu()
    {
        var ui = _hui!;
        bool woman = _orig.Sex == 1;
        ui.SetVariable("/:SelItemType", "hairdresser");
        ui.SetValue("/:isWoman", woman);
        ui.SetValue("/:LockHair", false);
        ui.SetValue("/:LockPilosity", woman);
        ui.SetValue("/:LockEyebrows", true);   // Augenbrauen-Varianten nicht nachgebaut (wie Unity)
        ui.SetValue("/:LockHairCancel", false);
        ui.SetValue("/:LockHairValid", false);
        ui.SetValue("/:Input", true);
        UpdatePrice();
        if (!ui.CallClip("/", "openMenuHairDresser")) { ui.GotoLabel("/", "hairdresser_edit", false); ui.CallClip("/", "setSquareMenu"); }
    }

    // ================================================================== Auswahl
    static void BuildLists(string menu)
    {
        _byStyle.Clear();
        bool beard = menu == "pilo";
        bool woman = _edit.Sex == 1;
        _types = beard ? new[] { BeardGoatee, BeardMoustache, BeardFull, 0 }
                       : woman ? new[] { StyleShort, StyleAttached, StyleAfro } : new[] { StyleShort, StyleLong, StyleAfro };
        foreach (int t in _types) _byStyle[t] = new List<ClotheInfo>();
        var part = beard ? ClothePart.Beard : ClothePart.Hair;
        foreach (var c in AvatarLibrary.AllClothes)
        {
            if (c.Part != part || !c.Valid || c.Brand == 73) continue;   // 73 = Figuren (PNJ)
            if (woman ? !c.ForWoman : !c.ForMan) continue;
            if (c.File.EndsWith("_HELMET", StringComparison.OrdinalIgnoreCase)) continue;
            int st = c.HairCutStyle;
            if (beard && c.IsNothing) st = 0;
            else if (c.IsNothing || !AvatarLibrary.HasBank(c)) continue;
            if (!_byStyle.ContainsKey(st)) { if (!beard && st != 0) _byStyle[StyleShort].Add(c); continue; }
            _byStyle[st].Add(c);
        }
        foreach (var l in _byStyle.Values) l.Sort((a, b) => a.SortIndex != b.SortIndex ? a.SortIndex.CompareTo(b.SortIndex) : string.CompareOrdinal(a.File, b.File));
        // Auswahl auf das Getragene
        var w = _edit.Worn(part);
        _typeIdx = 0; _cutIdx = 0; _color = w != null ? _edit.ColorOf(w.Hash) : 0;
        for (int i = 0; i < _types.Length; i++)
        {
            int k = w != null ? _byStyle[_types[i]].IndexOf(w) : -1;
            if (k >= 0) { _typeIdx = i; _cutIdx = k; break; }
        }
    }

    static List<ClotheInfo> CurList => _types != null && _byStyle.TryGetValue(_types[_typeIdx], out var l) ? l : new List<ClotheInfo>();
    static ClotheInfo? CurCut { get { var l = CurList; return l.Count > 0 ? l[System.Math.Clamp(_cutIdx, 0, l.Count - 1)] : null; } }

    static string StyleName(int st, bool beard)
    {
        if (beard) return st == BeardGoatee ? "goatee" : st == BeardMoustache ? "moustache" : st == BeardFull ? "beard" : "shave";
        return st == StyleLong ? "long" : st == StyleAfro ? "afro" : st == StyleAttached ? "w_attached" : "short";
    }

    static string CutName(ClotheInfo? c)
    {
        if (c == null) return "-";
        string? n = c.NameHc != 0 ? FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, c.NameHc) : null;
        if (string.IsNullOrEmpty(n)) { var p = c.File.Split('_'); int s = System.Math.Min(3, p.Length - 1); n = string.Join(" ", p, s, p.Length - s); }
        return n;
    }

    static void ApplySelection()
    {
        bool beard = _menu == "pilo";
        var part = beard ? ClothePart.Beard : ClothePart.Hair;
        var c = CurCut;
        _edit.Remove(part);
        if (c != null && !c.IsNothing) _edit.Set(c, System.Math.Clamp(_color, 0, System.Math.Max(0, c.NbColors - 1)));
        UpdateTexts();
        UpdatePrice();
        _preview?.Show(_edit, false);
    }

    static void UpdateTexts()
    {
        var ui = _hui;
        if (_types == null || ui == null) return;
        bool beard = _menu == "pilo";
        var c = CurCut;
        string type = StyleName(_types[_typeIdx], beard);
        string cut = CutName(c) + "  (" + (System.Math.Clamp(_cutIdx, 0, System.Math.Max(0, CurList.Count - 1)) + 1) + "/" + System.Math.Max(1, CurList.Count) + ")";
        int nc = c != null ? System.Math.Max(1, c.NbColors) : 1;
        string col = (System.Math.Clamp(_color, 0, nc - 1) + 1) + "/" + nc;
        if (beard) { ui.SetVariable("/:PilosityType", type); ui.SetVariable("/:PilosityCut", cut); ui.SetVariable("/:PilosityColor", col); }
        else { ui.SetVariable("/:HairType", type); ui.SetVariable("/:HairCut", cut); ui.SetVariable("/:HairColor", col); }
    }

    static int Price()
    {
        int p = 0;
        foreach (var part in new[] { ClothePart.Hair, ClothePart.Beard })
        {
            var a = _orig.Worn(part); var b = _edit.Worn(part);
            if (a == b && (b == null || _orig.ColorOf(b.Hash) == _edit.ColorOf(b.Hash))) continue;
            if (b != null && b.Price < 999999) p += b.Price;
            else if (b == null) p += 100;
            else p += 300;
        }
        return p;
    }

    static void UpdatePrice()
    {
        var ui = _hui; if (ui == null) return;
        int p = Price();
        var sv = TDU2Session.Save;
        string s = Money(p);
        ui.SetVariable("/:CurrentPrice", s);
        ui.SetVariable("CurrentPrice", s);
        ui.SetVariable("/:HairPrice", s);
        ui.SetValue("/:EnoughMoney", sv == null || sv.money >= p ? 1 : 0);
    }

    static void UpdateMoney()
    {
        var sv = TDU2Session.Save;
        _hui?.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : " ");
    }

    static string Money(long v) => "$ " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number);

    // ================================================================== Befehle
    static void OnCommand(string cmd, string args)
    {
        if (!_open) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (FrontendSounds.Command(c)) return;
        if (c == "dbxo") { AnswerDialog(); return; }
        if (c == "evmo") { UpdatePrice(); return; }
        if (c == "moup") { UpdateMoney(); return; }
        if (c == "hadr:clos" || c == "gofr" || c == "wmon") { Close(); return; }
        if (c == "hadr:edit:vali") { Commit(); return; }
        if (c == "hadr:edit:canc") { _edit = _orig.Clone(); _preview?.Show(_edit, false); return; }
        if (c == "hadr:open" || c == "hadr:edit:open" || c == "hadr:edit:chpa") return;
        if (!c.StartsWith("hadr:edit:", StringComparison.Ordinal)) { Log.Debug("Friseur: " + c); return; }
        var p = c.Substring(10).Split(':');   // maha:type:scne
        if (p.Length < 2) return;
        string menu = p[0];
        if (menu == "eybr") return;
        if (p[1] == "open") { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); UpdateTexts(); return; }
        if (_menu != menu || _types == null) { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); }
        if (p[1] == "vali") { _menuBackup = null; UpdatePrice(); return; }
        if (p[1] == "canc") { if (_menuBackup != null) _edit = _menuBackup; _menuBackup = null; UpdateTexts(); UpdatePrice(); _preview?.Show(_edit, false); return; }
        int d = p.Length > 2 && p[2] == "scpr" ? -1 : 1;
        switch (p[1])
        {
            case "type":
                _typeIdx = (_typeIdx + d + _types!.Length) % _types.Length; _cutIdx = 0;
                break;
            case "cuts":
                { int n = System.Math.Max(1, CurList.Count); _cutIdx = (_cutIdx + d + n) % n; break; }
            case "colo":
                { var cc = CurCut; int n = cc != null ? System.Math.Max(1, cc.NbColors) : 1; _color = (_color + d + n) % n; break; }
            default: return;
        }
        ApplySelection();
    }

    static void Commit()
    {
        int price = Price();
        var sv = TDU2Session.Save;
        if (sv != null) sv.money = System.Math.Max(0, sv.money - price);
        _orig = _edit.Clone();
        AvatarConfigs.SetLocal(_orig);
        UpdateMoney();
        Log.Info("Friseur: neue Frisur gespeichert, bezahlt $" + price);
    }

    /// <summary>Bestätigungsbox aus general.bnk wie im Spiel (FlashDialog), Antwort an _root.DialogBox.</summary>
    static void AnswerDialog()
    {
        var ui = _hui; if (ui == null) return;
        FlashDialog.Open(ui, ans =>
        {
            ui.SetVariable("/:PlayerAnswer", ans);
            var owners = ui.FunctionOwners("DialogBox");
            string target = owners.Contains("/") ? "/" : owners.Count > 0 ? owners[^1] : "/";
            ui.CallClip(target, "DialogBox");
        });
    }

    // ================================================================== Ablauf je Bild
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        _clock += time.DeltaTime;
        var ui = _hui;
        if (!_open || ui == null || _t == null) return;
        if (ctx.Paused) return;
        if (_startAt > 0f && _clock >= _startAt) { _startAt = -1f; StartMenu(); }
        _preview?.Update(time.DeltaTime);
        if (FlashDialog.Active) { UpdateCamera(time.DeltaTime); return; }   // Bestätigungsbox offen: Tasten gehören ihr
        // Menü-Tasten wie der Flash Player des Spiels (Tasten-Buttons der Auswahlboxen, sonst Move/A/B): Gamemodes/Utils/FlashNav
        if (FlashNav.Update(ui, out bool backHandled))
        {
            if (_clock - _lastEsc < 1.2f) { _edit = _orig.Clone(); Close(); return; }   // 2x Zurück: Abbruch
            _lastEsc = _clock;
            if (!backHandled) { Close(); return; }
        }
        UpdateCamera(time.DeltaTime);
    }

    /// <summary>Kamera C_HEAD der Szene (sonst auf den Kopf des Avatars), weich nachgeführt (Unity LateUpdate).</summary>
    static void UpdateCamera(float dt)
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        Vector3 pos, fwd, up; float fov = 22f;
        var c = InteriorNpcs.FindObject("C_HEAD");
        if (c != null)
        {
            var m = c.Matrix * _holder;
            pos = m.Translation;
            fwd = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, m));
            up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, m));
            fov = System.Math.Clamp(c.Fov, 10f, 90f);
        }
        else
        {
            var head = _preview?.BoneInScene("HEAD");
            var look = head.HasValue ? Vector3.Transform(head.Value, _holder) : Vector3.Transform(new Vector3(0f, 1.2f, 0f), (_preview?.Pose ?? Matrix4x4.Identity) * _holder);
            var from = st.RenderToGame(cam.Position);
            var dir = from - look; dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitZ;
            pos = look + dir * 1.1f;
            fwd = Vector3.Normalize(look - pos); up = Vector3.UnitY;
            fov = 35f;
        }
        if (!_camInit) { _camPos = st.RenderToGame(cam.Position); _camFwd = cam.Override?.forward ?? cam.Forward; _camUp = cam.Override?.up ?? Vector3.UnitY; _camFov = _t.FieldOfView; _camInit = true; }
        float k = 1f - MathF.Exp(-dt * 4f);
        _camPos = Vector3.Lerp(_camPos, pos, k);
        _camFwd = Vector3.Normalize(Vector3.Lerp(_camFwd, fwd, k));
        _camUp = Vector3.Normalize(Vector3.Lerp(_camUp, up, k));
        _camFov += (fov - _camFov) * k;
        _t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(_camPos);
        cam.Override = (_camFwd, _camUp);
        _t.FieldOfView = _camFov;
        _t.NearClip = 0.03f;
    }

    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        base.Render(ctx, time);   // Interieur-Autos und Figuren
        var st = _t?.Streamer;
        if (_open && st != null) _preview?.Render(_holder * st.RootMatrix);
    }
}
