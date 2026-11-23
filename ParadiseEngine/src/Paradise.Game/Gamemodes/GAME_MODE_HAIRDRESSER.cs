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

    static SpotCinematics? _cin, _ext;
    static bool _menuCin;
    /// <summary>Anteil der gehaltenen C_360 (exe +0x1BB92C), Kauf erfolgt (+0x1BBA58), Kauf-Kino läuft.</summary>
    static float _rot;
    static bool _bought, _outro;

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

    // ------------------------------------------------------------------ Augenbrauen (exe 0x675260, 0xBA11B0 … 0xBA1990)
    /// <summary>Augenbrauen-Typen je Geschlecht (exe 0x675260): Id = Zelle des 2x4-Atlas (u = Id/4 x 0,5, v = Id%4 x 0,25), Text-Hash.</summary>
    static readonly int[] BrowTextMan = { 4080, 4082, 4084, 4086, 4081, 4083, 4085, 4087 };
    static readonly int[] BrowTextWoman = { 4088, 4090, 4092, 4094, 4089, 4091, 4093, 4095 };
    /// <summary>Augenbrauen-Farben (exe @0x1675590, 0x00BBGGRR) und ihre Texte (@0x1675520: 5414 … 5427), Index 0 … 13 (exe +0x1BB96C = 13).</summary>
    static readonly int[] BrowColors = { 0x000000, 0x465a6e, 0x5a6478, 0x505a64, 0x464664, 0x141428, 0x0a1e46, 0x1e3c5a, 0x0a1432, 0x1e4664, 0x28466e, 0x143282, 0x32508c, 0x14003c };
    const int BrowColorText0 = 5414, BrowPrice = 25;   // exe 0xBA14D6: +0x1BBA68 = 25
    static int _browIdx, _browColor;

    static int BrowColorIndex(AvatarConfig c)
    {
        if (!c.HasBrowColor) return 0;
        for (int i = 0; i < BrowColors.Length; i++)
        {
            var v = BrowColorOf(i);
            if (MathF.Abs(v.X - c.BrowColor.X) < 0.003f && MathF.Abs(v.Y - c.BrowColor.Y) < 0.003f && MathF.Abs(v.Z - c.BrowColor.Z) < 0.003f) return i;
        }
        return 0;
    }

    /// <summary>Farbe i als COLOR wie exe 0x67CE12 (Byte / 255, R = niedrigstes Byte).</summary>
    static System.Numerics.Vector4 BrowColorOf(int i)
    {
        int v = BrowColors[System.Math.Clamp(i, 0, BrowColors.Length - 1)];
        return new System.Numerics.Vector4((v & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, 1f);
    }

    /// <summary>exe 0xBA147C: Typ-Id oder Farbindex weicht von den getragenen Augenbrauen ab.</summary>
    static bool BrowChanged => System.Math.Max(0, _edit.BrowId) != System.Math.Max(0, _orig.BrowId) || BrowColorIndex(_edit) != BrowColorIndex(_orig);

    /// <summary>Auswahlpunkt Typ 29 (InteriorUtils.Activate): Friseur öffnen.</summary>
    internal static bool Open()
    {
        if (!AvatarLibrary.Ready) { Log.Info("Friseur: Avatar-Daten werden noch geladen …"); return false; }
        if (_t == null || _scene == null) return false;
        // Original: Friseur gesperrt, solange der Verband der Schönheitsklinik sitzt (Text 7169, Karte /:hairdresser_locked 0xBD7364)
        if (Paradise.Game.Utils.BandageUtils.Active)
        {
            var msg = Lang(7169, "You cannot go to the hairdresser until you have your bandages removed.");
            Paradise.Game.Utils.BandageUtils.Notify(Paradise.Game.Utils.BandageUtils.TextHairdresserLocked, msg);
            return false;
        }
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
        _bought = false; _outro = false; _menuCin = false;
        // exe 0xB9FD25: Kinos der Bank, C_LIFE beim Betreten (der Spieler-Darsteller sitzt im Kino); ohne C_360 der Anprobe-Avatar
        OpenCinematics();
        _cin?.PlayBackground("C_LIFE", true);
        _preview?.Dispose(); _preview = null;
        if (_cin?.Find("C_360") == null)
        {
            _preview = new AvatarPreview(_v.Renderer);
            var act = InteriorNpcs.FindObject("Avatar", "HEAD");
            if (act == null) Log.Warn("Friseur: Darsteller \"Avatar\" der HEAD-Szene fehlt");
            _preview.Place(act, act?.Clip, _orig.Sex, "hd_sitstand_loop-m", "co_stand-w");
            if (act == null) _preview.Pose = Matrix4x4.CreateTranslation(_t.Streamer != null && _t.Camera != null
                ? Vector3.Transform(_t.Streamer.RenderToGame(_t.Camera.Position) + _t.Camera.Forward * 2f - Vector3.UnitY * 1.6f, _holderInv) : Vector3.Zero);
            _preview.Show(_edit, true);
        }
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
        _bought = false; _outro = false;
        EndCinematics();
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
        ui.SetValue("/:LockEyebrows", false);   // Augenbrauen: Typ (8 je Geschlecht) und Farbe (14), exe 0xBA11B0
        ui.SetValue("/:LockHairCancel", false);
        ui.SetValue("/:LockHairValid", false);
        ui.SetValue("/:Input", true);
        UpdatePrice();
        // exe 0xBA1CD0: C_LIFE aus, C_360 gehalten (0,95 / Länge) + C_HEAD; ohne C_360 bleibt der Anprobe-Avatar
        if (_cin == null) OpenCinematics();
        if (_cin != null && ShopCinema.Start360(_cin, out _rot, "C_HEAD")) { _cin.StopBackground("C_LIFE"); _menuCin = true; }
        if (!ui.CallClip("/", "openMenuHairDresser"))
        {
            ui.GotoLabel("/", "hairdresser_edit", false);
            ui.CallClip("/", "setSquareMenu");
        }
    }

    static void OpenCinematics()
    {
        EndCinematics();
        if (_v?.Renderer == null) return;
        _cin = SpotCinematics.Open(_v.Renderer, _pendingBank);
        if (_cin == null) return;
        _cin.Frame = _holder;
        _cin.Indoor = true;
        _cin.SetPlayer(_edit.Clone());
    }

    static void EndCinematics()
    {
        InteriorNpcs.Suppressed = null;
        _menuCin = false;
        _cin?.Dispose(); _cin = null;
        _ext?.Dispose(); _ext = null;
    }

    /// <summary>Aussehen auf den Anprobe-Avatar bzw. den Spieler-Darsteller des Kinos.</summary>
    static void ShowEdit()
    {
        _preview?.Show(_edit, false);
        _cin?.SetPlayer(_edit.Clone());
    }

    /// <summary>
    /// Schließen (exe 0xB9FDD0): Kinos neu – C_LIFE, nach einem Kauf C_BUY_HAIR als Haupt-Kino (mit Kamera); danach zurück in den Raum.
    /// </summary>
    static void Leave()
    {
        if (_bought && _cin != null && _cin.Find("C_BUY_HAIR") != null)
        {
            _menuCin = false;
            _cin.StopMain();
            _cin.StopBackground();
            _cin.SetPlayer(_orig.Clone());
            _cin.PlayBackground("C_LIFE", true);
            _cin.PlayMain("C_BUY_HAIR");
            _outro = true;
            if (_hui != null) { _hui.Visible = false; _hui.MouseEnabled = false; }
            return;
        }
        Close();
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
        bool woman = _edit.Sex == 1;   // exe 0xBA00A0: Frauen "w_short" / "w_afro"
        return st == StyleLong ? "long" : st == StyleAfro ? (woman ? "w_afro" : "afro") : st == StyleAttached ? "w_attached" : woman ? "w_short" : "short";
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
        ShowEdit();
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
        ui.CallClip("/hair_menu", "updateParameters");   // exe 0xBA21E4 (sperrt Schnitt/Farbe bei "shave")
    }

    static void UpdateBrowTexts()
    {
        var ui = _hui; if (ui == null) return;
        var names = _edit.Sex == 1 ? BrowTextWoman : BrowTextMan;
        int i = System.Math.Clamp(_browIdx, 0, names.Length - 1);
        string type = Lang(names[i], "Eyebrows " + (i + 1));
        // exe 0xBA1247: getragene (gekaufte) Augenbrauen mit " (Aktuell)"
        if (i == System.Math.Max(0, _orig.BrowId) % names.Length) type += " (" + Lang(110, "Current") + ")";
        ui.SetVariable("/:EyebrowsType", type);
        ui.SetVariable("/:EyebrowsColor", Lang(BrowColorText0 + System.Math.Clamp(_browColor, 0, BrowColors.Length - 1), (_browColor + 1).ToString()));
        ui.CallClip("/eyebrows_menu", "updateParameters");   // exe 0xBA178A
    }

    static void ApplyBrow()
    {
        _edit.BrowId = _browIdx;
        _edit.BrowColor = BrowColorOf(_browColor);
        UpdateBrowTexts();
        UpdatePrice();
        ShowEdit();
    }

    /// <summary>
    /// Preis wie exe 0xBA3FF9: Frisur (+0x1BBA60) + Bart (+0x1BBA64) + Augenbrauen (+0x1BBA68); je Teil der Preis des gewählten Stücks,
    /// wenn es (oder seine Farbe) vom Getragenen abweicht; rasieren kostet nichts (exe 0xBA0360), Augenbrauen 25.
    /// </summary>
    static int Price()
    {
        int p = 0;
        foreach (var part in new[] { ClothePart.Hair, ClothePart.Beard })
        {
            var a = _orig.Worn(part); var b = _edit.Worn(part);
            if (a == b && (b == null || _orig.ColorOf(b.Hash) == _edit.ColorOf(b.Hash))) continue;
            if (b == null || b.IsNothing) continue;
            if (b.Price < 999999) p += b.Price;
        }
        if (BrowChanged) p += BrowPrice;
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
        if (c == "hadr:clos" || c == "gofr" || c == "wmon") { Leave(); return; }
        if (c == "hadr:edit:vali") { Commit(); return; }
        if (c == "hadr:edit:canc") { _edit = _orig.Clone(); ShowEdit(); return; }
        if (c == "hadr:open" || c == "hadr:edit:open" || c == "hadr:edit:chpa") return;
        if (!c.StartsWith("hadr:edit:", StringComparison.Ordinal)) { Log.Debug("Friseur: " + c); return; }
        var p = c.Substring(10).Split(':');   // maha:type:scne
        if (p.Length < 2) return;
        string menu = p[0];
        if (menu == "eybr") { BrowCommand(p); return; }
        if (p[1] == "open") { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); UpdateTexts(); return; }
        if (_menu != menu || _types == null) { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); }
        if (p[1] == "vali")
        {
            // exe 0xBA02B0 / 0xBA0400: unverändert (Stück + Farbe wie getragen) -> "Bestätigen" gesperrt
            var part = menu == "pilo" ? ClothePart.Beard : ClothePart.Hair;
            var a = _orig.Worn(part); var b = _edit.Worn(part);
            bool same = a == b && (b == null || _orig.ColorOf(b.Hash) == _edit.ColorOf(b.Hash));
            _menuBackup = null; UpdatePrice();
            _hui?.SetValue("/:LockHairValid", same && Price() == 0);
            return;
        }
        if (p[1] == "canc") { if (_menuBackup != null) _edit = _menuBackup; _menuBackup = null; UpdateTexts(); UpdatePrice(); ShowEdit(); return; }
        int d = p.Length > 2 && p[2] == "scpr" ? -1 : 1;
        switch (p[1])
        {
            case "type":
                {
                    // Die Symbol-Auswahl (choicebox_ic) stellt _root.HairType/PilosityType selbst um und schickt den Befehl ohne Richtung;
                    // wie exe 0xBA2240 / 0xBA2DE0 den Wert zurücklesen ("beard", "moustache", "goatee", "shave", "short" …)
                    string? v = _hui?.GetVariable(menu == "pilo" ? "/:PilosityType" : "/:HairType");
                    int k = -1;
                    if (!string.IsNullOrEmpty(v))
                        for (int i = 0; i < _types!.Length; i++) if (StyleName(_types[i], menu == "pilo") == v) { k = i; break; }
                    _typeIdx = k >= 0 ? k : (_typeIdx + d + _types!.Length) % _types.Length;
                    _cutIdx = 0;
                    break;
                }
            case "cuts":
                { int n = System.Math.Max(1, CurList.Count); _cutIdx = (_cutIdx + d + n) % n; break; }
            case "colo":
                { var cc = CurCut; int n = cc != null ? System.Math.Max(1, cc.NbColors) : 1; _color = (_color + d + n) % n; break; }
            default: return;
        }
        ApplySelection();
    }

    /// <summary>
    /// Augenbrauen-Menü (eyebrows_menu, FSCommands "hadr:edit:eybr:&lt;open|type|colo|vali|canc&gt;[:scne|scpr]"):
    /// open 0xBA11B0 (Sicherung, Texte), type 0xBA17E0/0xBA1870 (nächster/vorheriger Typ des Geschlechts), colo 0xBA1900/0xBA1990
    /// (Farbe 0 … 13 im Kreis), vali 0xBA1430 (unverändert -> Bestätigen gesperrt, sonst 25), canc 0xBA04B0 (Sicherung zurück).
    /// </summary>
    static void BrowCommand(string[] p)
    {
        int n = BrowTextMan.Length;
        switch (p[1])
        {
            case "open":
                _menu = "eybr"; _menuBackup = _edit.Clone();
                _browIdx = System.Math.Max(0, _edit.BrowId) % n;
                _browColor = BrowColorIndex(_edit);
                UpdateBrowTexts();
                return;
            case "vali":
                {
                    _menuBackup = null;
                    UpdatePrice();
                    _hui?.SetValue("/:LockHairValid", !BrowChanged && Price() == 0);
                    return;
                }
            case "canc":
                if (_menuBackup != null) _edit = _menuBackup;
                _menuBackup = null;
                _browIdx = System.Math.Max(0, _edit.BrowId) % n;
                _browColor = BrowColorIndex(_edit);
                UpdateBrowTexts(); UpdatePrice(); ShowEdit();
                return;
        }
        if (_menu != "eybr") { _menu = "eybr"; _menuBackup = _edit.Clone(); _browIdx = System.Math.Max(0, _edit.BrowId) % n; _browColor = BrowColorIndex(_edit); }
        int d = p.Length > 2 && p[2] == "scpr" ? -1 : 1;
        if (p[1] == "type") _browIdx = (_browIdx + d + n) % n;
        else if (p[1] == "colo") _browColor = (_browColor + d + BrowColors.Length) % BrowColors.Length;
        else return;
        ApplyBrow();
    }

    static void Commit()
    {
        int price = Price();
        var sv = TDU2Session.Save;
        if (sv != null)
        {
            sv.money = System.Math.Max(0, sv.money - price);
            // Gekaufte Frisuren/Bärte sind DBClothe-Teile und gehören in die Besitzliste:
            // der Avatar speichert nur das aktuell Getragene, sonst zählt die Collection
            // nach mehreren Friseurbesuchen weiterhin nur eine Frisur.
            sv.ownedClothes ??= new List<int>();
            foreach (var part in new[] { ClothePart.Hair, ClothePart.Beard })
            {
                var a = _orig.Worn(part);
                var b = _edit.Worn(part);
                if (b == null || b.IsNothing || (a != null && a.Hash == b.Hash)) continue;
                if (!sv.ownedClothes.Contains(b.Hash)) sv.ownedClothes.Add(b.Hash);
            }
        }
        _orig = _edit.Clone();
        _bought = true;   // exe +0x1BBA58: beim Schließen C_BUY_HAIR
        AvatarConfigs.SetLocal(_orig);   // schreibt Avatar und speichert (Kopie enthält Geld + Besitzliste)
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
        float dt = time.DeltaTime;
        ShopCinema.Update(_cin, dt);
        if (_outro)
        {
            // Kauf-Kino C_BUY_HAIR: Kamera des Kinos, danach zurück in den Raum
            // überspringbar (Bestätigen/Zurück)
            if (_cin == null || !_cin.MainPlaying || ShopCinema.SkipPressed()) { _cin?.StopMain(); _outro = false; Close(); return; }
            if (!ShopCinema.Camera(_cin, false)) UpdateCamera(dt);
            return;
        }
        if (_startAt > 0f && _clock >= _startAt) { _startAt = -1f; StartMenu(); }
        _preview?.Update(dt);
        if (_menuCin && _cin != null) ShopCinema.Rotate(_cin, ref _rot, dt, Paradise.Game.Config.HairdresserConfig.MouseRotateScale);
        if (FlashDialog.Active) { CameraStep(dt); return; }   // Bestätigungsbox offen: Tasten gehören ihr
        // Menü-Tasten wie der Flash Player des Spiels (Tasten-Buttons der Auswahlboxen, sonst Move/A/B): Gamemodes/Utils/FlashNav
        if (FlashNav.Update(ui, out bool backHandled))
        {
            if (_clock - _lastEsc < 1.2f) { _edit = _orig.Clone(); Leave(); return; }   // 2x Zurück: Abbruch
            _lastEsc = _clock;
            if (!backHandled) { Leave(); return; }
        }
        CameraStep(dt);
    }

    /// <summary>Menü mit Kino: Kamera der gehaltenen C_360; sonst C_HEAD der Szene.</summary>
    static void CameraStep(float dt)
    {
        if (_menuCin && _cin != null && ShopCinema.Camera(_cin, false)) { _camInit = false; return; }
        UpdateCamera(dt);
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
        if (_open && st != null) { _cin?.Render(st.RootMatrix); _preview?.Render(_holder * st.RootMatrix); }
    }
}
