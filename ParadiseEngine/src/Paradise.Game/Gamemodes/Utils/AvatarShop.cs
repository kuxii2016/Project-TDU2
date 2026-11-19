using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Gemeinsamer Teil von Kleidungsladen (GAME_MODE_GOODIES) und Garderobe (GAME_MODE_DRESSING) – Port Unity TDU2ClothesShop:
/// Sortiment je Kategorie-Kamera (C_hats, C_head_d, C_tops, C_bottoms, C_shoes, C_bracelets), Anprobe-Avatar am Platz "Avatar" der
/// Cinematic-Szenen (Pose der Kategorie dr_*/clot_*), Kamera der Kategorie, Stückauswahl menu_item (Num_Item, prit/neit, Scroll),
/// Anziehen (seit) und gemeinsame FSCommands. Laden/Garderobe-spezifisch: Film und Startlabel, Sortiment, Kaufen.
/// </summary>
public abstract class AvatarShop : InteriorUtils
{
    // DBClothe._brand der Kollektionen
    protected const int BrandHighlife = 57, BrandStreetwear = 85, BrandSportswear = 84, BrandFashion = 83, BrandEden = 44;
    protected const int BrandCasino = 87, BrandPnj = 73, BrandDefault = 54, BrandHair = 55;

    /// <summary>Faktor für die Fov der Nahaufnahme-Kameras (C_hats, C_head_d, C_shoes …), wie Unity CloseUpFov.</summary>
    public static float CloseUpFov = 1.45f;

    protected sealed class Entry
    {
        public ClotheInfo? Item;     // null = "nichts" (Teil ablegen)
        public ClothePart Part;
        public int Color;
        public bool Owned;
        public int Price => Item != null ? Item.Price : 0;
    }

    protected VmfPlayer? _ui2;
    protected bool _open, _inItems;
    protected float _clock, _cinAt = -1f, _reopenAt = -1f, _lastEsc = -10f, _catInputAt = -1f, _muteBackUntil = -1f, _inputAt = -1f;
    protected string _cat = "C_main";
    protected readonly List<Entry> _items = new();
    protected int _index;
    protected AvatarConfig _base = new();
    private protected AvatarPreview? _preview;
    float _turn;
    Matrix4x4 _place = Matrix4x4.Identity; bool _hasPlace;
    Vector3 _camPos, _camFwd, _camUp; float _camFov; bool _camInit;

    /// <summary>Laden (true) oder Garderobe (false).</summary>
    protected abstract bool IsShop { get; }
    /// <summary>Standard-Pose ohne Clip in der Szene (Laden clot_standloop-m, Garderobe dr_standdressing-m).</summary>
    protected abstract string DefaultPose { get; }
    /// <summary>Sortiment der Kategorie.</summary>
    protected abstract List<Entry> BuildItems(string cam);
    /// <summary>Film bereit: Variablen gesetzt, hier ins Startlabel springen.</summary>
    protected abstract void StartFilm(VmfPlayer ui);
    /// <summary>Weitere FSCommands (Kaufen …); true = verarbeitet.</summary>
    protected virtual bool Command(string lc) => false;
    /// <summary>Nach dem Schließen (Garderobe: zurück ins Wohnzimmer).</summary>
    protected virtual void AfterClose() { }

    // ================================================================== Öffnen / Schließen
    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE && _open) return;   // Weiterspielen nach dem Pausemenü
        var ui = ctx.Open(Screen);
        if (ui == null || _t == null) { Log.Warn(Mode + ": " + Screen.Bank + " nicht ladbar"); GameMode.Set(RoomMode); return; }
        if (_ui2 != ui) { if (_ui2 != null) _ui2.FSCommand -= OnCommand; _ui2 = ui; ui.FSCommand += OnCommand; }
        _base = AvatarConfigs.Local.Clone();
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        _cat = "C_main";
        _turn = 0f;
        _inItems = false;
        _cinAt = _reopenAt = _catInputAt = _inputAt = -1f;
        OnOpen();
        _preview?.Dispose();
        _preview = new AvatarPreview(_v.Renderer);
        PlaceAvatar();
        ApplyCategoryPose();
        _preview.Show(_base, true);
        _camInit = false;
        _open = true;
        ui.Visible = true;
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetValue("/:EnableItemBuyCin", false);
        ui.SetValue("/:DLC_Ok", 1);
        ui.SetVariable("/:ClubMoney", " ");
        SetCounts();
        UpdateMoney();
        StartFilm(ui);
        Log.Info(Mode + " geöffnet: " + (_base.Sex == 1 ? "Frau" : "Mann") + ", Avatar-Platz " + (_hasPlace ? "aus der Szene" : "vor der Kamera") + OpenInfo());
    }

    /// <summary>Vor dem Aufbau des Avatars (Laden: Kollektion, Verkäuferin).</summary>
    protected virtual void OnOpen() { }
    protected virtual string OpenInfo() => "";

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: bleibt offen
        CloseState();
        base.Exit(ctx, to);
    }

    protected virtual void CloseState()
    {
        if (!_open) return;
        _open = false; _inItems = false; _cinAt = _reopenAt = _catInputAt = _inputAt = -1f;
        _preview?.Dispose(); _preview = null;
        if (_ui2 != null) { _ui2.FSCommand -= OnCommand; _ui2 = null; }   // Sichtbarkeit regelt der Modus (home.bnk teilt sich die Garderobe mit dem Wohnbereich)
        if (_t != null) _t.NearClip = 0.05f;
        Log.Info(Mode + " geschlossen");
    }

    /// <summary>Unity Close: zurück in den Modus des Raums (Laufen).</summary>
    protected void Close()
    {
        CloseState();
        AfterClose();
        GameMode.Set(RoomMode);
    }

    // ================================================================== Sortiment
    protected static ClothePart[]? PartsFor(string cam, int sex) => (cam ?? "").ToLowerInvariant() switch
    {
        "c_hats" => new[] { ClothePart.Helmet },
        "c_head_d" => new[] { ClothePart.Glasses },
        "c_tops" => sex == 1 ? new[] { ClothePart.Top, ClothePart.Dress } : new[] { ClothePart.Top },
        "c_bottoms" => new[] { ClothePart.Bottom },
        "c_shoes" => new[] { ClothePart.Shoes },
        "c_bracelets" => new[] { ClothePart.Gloves },
        _ => null,
    };

    protected static bool Removable(ClothePart p) => p == ClothePart.Helmet || p == ClothePart.Glasses || p == ClothePart.Gloves;

    protected static int Order(ClotheInfo a, ClotheInfo b)
    {
        int r = a.Part.CompareTo(b.Part); if (r != 0) return r;
        r = a.SortIndex.CompareTo(b.SortIndex); if (r != 0) return r;
        r = a.Price.CompareTo(b.Price); if (r != 0) return r;
        return string.CompareOrdinal(a.File, b.File);
    }

    protected static bool Wears(AvatarConfig cfg, ClotheInfo? c) => c != null && cfg.Items.Contains(c.Hash);

    protected static HashSet<int> OwnedSet()
    {
        var s = new HashSet<int>();
        var sv = TDU2Session.Save;
        if (sv?.ownedClothes != null) foreach (int h in sv.ownedClothes) s.Add(h);
        return s;
    }

    protected void SetCounts()
    {
        var ui = _ui2; if (ui == null) return;
        int Count(string cam) => BuildItems(cam).Count;
        ui.SetValue("/:Nb_Hats", Count("C_hats"));
        ui.SetValue("/:Nb_Acc", Count("C_head_d"));
        ui.SetValue("/:Nb_Tops", Count("C_tops"));
        ui.SetValue("/:Nb_Bottoms", Count("C_bottoms"));
        ui.SetValue("/:Nb_Shoes", Count("C_shoes"));
        ui.SetValue("/:Nb_Hands", Count("C_bracelets"));
        foreach (var n in new[] { "Nb_Helmets", "Nb_BikeTops", "Nb_BikeBottoms", "Nb_Watches", "Nb_Rings", "Nb_Earrings", "Nb_Chokers", "Nb_Bracelets" }) ui.SetValue("/:" + n, 0);
        foreach (var l in new[] { "LockHats", "LockGlasses", "LockTops", "LockBottoms", "LockShoes", "LockGloves" }) ui.SetValue("/:" + l, false);
        foreach (var l in new[] { "LockHelmets", "LockBikeTops", "LockBikeBottoms" }) ui.SetValue("/:" + l, true);
        bool dress = false;
        foreach (int h in _base.Items) if (AvatarLibrary.Clothe(h)?.Part == ClothePart.Dress) dress = true;
        ui.SetValue("/:IsWearingDress", dress ? 1 : 0);
    }

    // ================================================================== Befehle des Films
    void OnCommand(string cmd, string args)
    {
        if (!_open || _ui2 == null) return;
        string lc = (cmd ?? "").Trim().ToLowerInvariant();
        if (lc == "sndp:back" && _clock < _muteBackUntil) return;   // eigenes Schließen nach dem Kauf: kein Zurück-Ton
        if (FrontendSounds.Command(lc)) return;
        if (Command(lc)) return;
        switch (lc)
        {
            case "came": OnCamera(_ui2.GetVariable("/:WhatCam")); return;
            case "prit": case "neit": SelectFromFilm(); return;
            case "seit": WearCurrent(); return;
            case "nsit": _inItems = false; _preview?.Show(_base, false); return;
            case "moup": UpdateMoney(); return;
            case "dbxo": AnswerDialog(); return;
            case "drin": case "clin": SetCounts(); return;
        }
    }

    void OnCamera(string? cam)
    {
        _cat = string.IsNullOrEmpty(cam) ? "C_main" : cam!;
        ApplyCategoryPose();
        var parts = PartsFor(_cat, _base.Sex);
        if (parts == null)
        {
            // Hauptmenü: Anprobe zurück auf das Getragene
            _inItems = false;
            _preview?.Show(_base, false);
            return;
        }
        _items.Clear();
        _items.AddRange(BuildItems(_cat));
        _inItems = true;
        // mit dem getragenen Stück der Kategorie beginnen
        _index = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            var e = _items[i];
            if (e.Item != null && Wears(_base, e.Item) && _base.ColorOf(e.Item.Hash) == e.Color) { _index = i; break; }
        }
        _ui2!.SetValue("/:Nb_Items", _items.Count);
        _ui2.SetValue("/:Num_Item", _index + 1);
        UpdateItem();
    }

    void SelectFromFilm()
    {
        if (!_inItems || _items.Count == 0 || _ui2 == null) return;
        if (!int.TryParse(_ui2.GetVariable("/:Num_Item") ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out int n)) n = 1;
        _index = System.Math.Clamp(n - 1, 0, _items.Count - 1);
        UpdateItem();
    }

    protected Entry? Current => _inItems && _index >= 0 && _index < _items.Count ? _items[_index] : null;

    protected void UpdateItem()
    {
        var e = Current; var ui = _ui2;
        if (e == null || ui == null) return;
        var sv = TDU2Session.Save;
        long money = sv?.money ?? 0;
        ui.SetVariable("/:ItemName", ItemName(e));
        ui.SetVariable("/:Item_brand", BrandLabel(e));
        ui.SetValue("/:Solded", IsShop && e.Owned);
        ui.SetValue("/:IsEnoughMoney", e.Owned || sv == null || money >= e.Price);
        ui.SetValue("/:DLC_Locked", false);
        string price = e.Owned || !IsShop ? " " : Money(e.Price);
        ui.SetVariable("/:CurrentPrice", price);
        ui.SetVariable("CurrentPrice", price);
        foreach (var o in ui.FunctionOwners("updateBrandLabel")) ui.CallClip(o, "updateBrandLabel");
        _preview?.Show(Apply(_base, e), false);
    }

    static string ItemName(Entry e)
    {
        if (e.Item == null) return PartName(e.Part) + " –";
        string? n = e.Item.NameHc != 0 ? FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, e.Item.NameHc) : null;
        if (string.IsNullOrEmpty(n)) n = Pretty(e.Item.File);
        int colors = System.Math.Max(1, e.Item.NbColors);
        return colors > 1 ? n + "  (" + (e.Color + 1) + "/" + colors + ")" : n!;
    }

    static string PartName(ClothePart p) => p switch
    {
        ClothePart.Helmet => "Hut",
        ClothePart.Glasses => "Brille",
        ClothePart.Gloves => "Handschuhe",
        _ => p.ToString(),
    };

    /// <summary>"M_SW_T_ShortSleeves_Long_01" -> "ShortSleeves Long 01".</summary>
    static string Pretty(string file)
    {
        var parts = (file ?? "").Split('_');
        int start = parts.Length > 3 ? 3 : 0;
        return string.Join(" ", parts, start, parts.Length - start);
    }

    /// <summary>Logo-Etikett im Film (menu_item/labels/etiquette: brand00..brand12, none) – über DBClothe._subbrand.</summary>
    static string BrandLabel(Entry e)
    {
        int sb = e.Item != null ? e.Item.SubBrand : -1;
        return sb >= 0 && sb <= 12 ? "brand" + sb.ToString("00", CultureInfo.InvariantCulture) : "none";
    }

    /// <summary>Aussehen mit dem Stück (Kleid ersetzt Ober- und Unterteil und umgekehrt; "nichts" legt das Teil ab).</summary>
    protected static AvatarConfig Apply(AvatarConfig cfg, Entry? e)
    {
        var c = cfg.Clone();
        if (e == null) return c;
        if (e.Item == null) { c.Remove(e.Part); return c; }
        if (e.Part == ClothePart.Dress) { c.Remove(ClothePart.Top); c.Remove(ClothePart.Bottom); }
        if (e.Part == ClothePart.Top || e.Part == ClothePart.Bottom)
        {
            bool hadDress = c.Worn(ClothePart.Dress) != null;
            c.Remove(ClothePart.Dress);
            // Kleid ausgezogen: das fehlende Gegenstück aus dem Standard-Aussehen ergänzen
            if (hadDress)
                foreach (int h in AvatarConfig.Default(c.Sex).Items)
                {
                    var ci = AvatarLibrary.Clothe(h);
                    if (ci != null && (ci.Part == ClothePart.Top || ci.Part == ClothePart.Bottom) && ci.Part != e.Part) c.Set(ci, 0);
                }
        }
        c.Set(e.Item, e.Color);
        return c;
    }

    /// <summary>Gekauftes bzw. bereits besessenes Stück anziehen (Spielstand + Server, Mitspieler).</summary>
    protected void Wear(Entry e)
    {
        _base = Apply(_base, e);
        AvatarConfigs.SetLocal(_base);
        _preview?.Show(_base, false);
        UpdateMoney();
    }

    protected void WearCurrent()
    {
        var e = Current;
        if (e != null && e.Owned) { Wear(e); FrontendSounds.Command("sndp:valid"); UpdateItem(); }
    }

    protected void UpdateMoney()
    {
        var sv = TDU2Session.Save;
        _ui2?.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : "");
    }

    protected static string Money(long v) => "   " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + "     ";

    /// <summary>Bestätigungsbox aus general.bnk wie im Spiel (FlashDialog), Antwort an _root.DialogBox.</summary>
    void AnswerDialog()
    {
        var ui = _ui2; if (ui == null) return;
        string kase = ui.GetVariable("/:DgbxCase") ?? "";
        FlashDialog.Open(ui, ans =>
        {
            Log.Debug(Mode + ": Dialog '" + kase + "' -> " + ans);
            ui.SetVariable("/:PlayerAnswer", ans);
            var owners = ui.FunctionOwners("DialogBox");
            ui.CallClip(owners.Count > 0 ? owners[^1] : "/", "DialogBox");
        });
    }

    /// <summary>
    /// Nach dem Kauf: Stückmenü wie mit B schließen (menu_item.B: close-Animation, nsit; Bild 38 springt bei WhatPage "clothes_ic"
    /// nach cl_in = Kategorien). Unity CloseItemsAfterBuy.
    /// </summary>
    protected void CloseItemsAfterBuy()
    {
        var ui = _ui2; if (ui == null) return;
        ui.SetValue("/:Input", true);   // B prüft Input
        string? owner = MenuItemOwner("B");
        _muteBackUntil = _clock + 0.3f;
        if (owner == null || !ui.CallClip(owner, "B"))
        {
            ui.GotoLabel("/menu_item", "close", true);
            _preview?.Show(_base, false);
        }
        _inItems = false;
        _inputAt = -1f;
        _catInputAt = _clock + 0.8f;   // Kategorien: Eingabe frei, falls cl_in sie nicht selbst freigibt
        UpdateMoney();
    }

    // ================================================================== Anprobe-Avatar (Platz aus den Cinematic-Szenen)
    static CineScene.Obj? SceneObject(string cat, bool camera)
    {
        foreach (var sc in InteriorNpcs.Cines.Values)
        {
            var cam = sc.Find(cat);
            if (cam == null) continue;   // Szene mit der Kamera dieser Kategorie
            if (camera) return cam;
            foreach (var o in sc.Objects) if (!o.IsCamera && o.HasPosition && o.Name.StartsWith("Avatar", StringComparison.OrdinalIgnoreCase)) return o;
        }
        return null;
    }

    void PlaceAvatar()
    {
        var place = SceneObject("C_main", false);
        _hasPlace = place != null;
        if (place != null) { _place = place.Matrix; return; }
        // Ersatz: vor der Kamera, mit Blick zu ihr (Unity PlaceHolder ohne Szene)
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) { _place = Matrix4x4.Identity; return; }
        var origin = st.RenderToGame(cam.Position);
        var fwd = cam.Forward; fwd.Y = 0f; fwd = fwd.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(fwd);
        var p = origin + fwd * 2.4f;
        float floor = origin.Y - 1.7f;
        if (Phys != null && Phys.Raycast(p + Vector3.UnitY * 0.5f, -Vector3.UnitY, 4f, out var hit)) floor = hit.Point.Y;
        p.Y = floor;
        var local = Vector3.Transform(p, _holderInv);
        var f = Vector3.TransformNormal(-fwd, _holderInv); f.Y = 0f;
        float yaw = f.LengthSquared() > 1e-4f ? MathF.Atan2(f.X, f.Z) : 0f;
        _place = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(local);
    }

    /// <summary>Pose der Kategorie (C_hats -> dr_hat-m …) aus der Szene; Mann/Frau passend getauscht.</summary>
    void ApplyCategoryPose()
    {
        if (_preview == null) return;
        var act = SceneObject(_cat, false) ?? SceneObject("C_main", false);
        string clip = !string.IsNullOrEmpty(act?.Clip) ? act!.Clip! : DefaultPose;
        bool woman = _base.Sex == 1;
        if (woman && clip.EndsWith("-m", StringComparison.Ordinal)) clip = clip[..^2] + "-w";
        else if (!woman && clip.EndsWith("-w", StringComparison.Ordinal)) clip = clip[..^2] + "-m";
        var c = InteriorNpcs.Clip(clip) ?? AvatarLibrary.Clip(clip) ?? AvatarLibrary.Clip(woman ? "co_stand-w" : "co_stand-m");
        if (c != _preview.Clip) { _preview.Clip = c; _preview.T = 0f; }
        if (act != null && _hasPlace) _place = act.Matrix;
    }

    // ================================================================== Ablauf je Bild
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        _clock += time.DeltaTime;
        var ui = _ui2;
        if (!_open || ui == null || _t == null) return;
        if (ctx.Paused) return;
        OnFrame(ui);
        // Scroll sperrt die Eingabe bis zum Ende der Blätter-Animation; falls die nicht zurückschaltet, nach kurzer Zeit wieder frei
        if (_inputAt > 0f && _clock >= _inputAt) { _inputAt = -1f; if (_inItems) ui.SetValue("/:Input", true); }
        if (_reopenAt > 0f && _clock >= _reopenAt) { _reopenAt = -1f; CloseItemsAfterBuy(); }
        if (_catInputAt > 0f && _clock >= _catInputAt) { _catInputAt = -1f; if (!_inItems) ui.SetValue("/:Input", true); }
        if (!FlashDialog.Active) HandleInput(ui, time.DeltaTime);   // Bestätigungsbox offen: Tasten gehören ihr
        if (!_open) return;
        // Avatar drehen: Q/E, rechte Maustaste, rechter Stick (Unity)
        var input = _t.Input;
        float dt = time.DeltaTime;
        if (input.IsKeyDown(0x51)) _turn -= 90f * dt;   // Q
        if (input.IsKeyDown(0x45)) _turn += 90f * dt;   // E
        if (input.RightMouseDown) _turn += input.MouseDeltaX * 0.4f;
        _turn += TDU2InputDevices.Look.X * 120f * dt;
        if (_preview != null)
        {
            _preview.Pose = Matrix4x4.CreateRotationY(_turn * MathF.PI / 180f) * _place;
            _preview.Update(dt);
        }
        UpdateCamera(dt);
    }

    /// <summary>Zeitgesteuerte Schritte der Unterklasse (Laden: verzögerter Sprung nach cl_in).</summary>
    protected virtual void OnFrame(VmfPlayer ui) { }

    /// <summary>
    /// Tasten wie Unity TDU2ClothesShop.Update: in der Stückauswahl (menu_item) nur links/rechts über menu_item.Scroll – hoch/runter
    /// erreichte sonst das Kategorie-Menü dahinter; sonst wie üblich über den globalen Flash-Handler (FlashNav).
    /// </summary>
    void HandleInput(VmfPlayer ui, float dt)
    {
        if (!_inItems)
        {
            if (FlashNav.Pressed(FlashKey.Up)) FlashNav.Send(ui, FlashKey.Up);
            if (FlashNav.Pressed(FlashKey.Down)) FlashNav.Send(ui, FlashKey.Down);
        }
        if (FlashNav.Pressed(FlashKey.Left)) MenuInput(ui, FlashKey.Left);
        if (FlashNav.Pressed(FlashKey.Right)) MenuInput(ui, FlashKey.Right);
        if (FlashNav.Pressed(FlashKey.Confirm))
        {
            // Laden: bereits gekauft -> der Film macht nichts ("Solded"), direkt anziehen
            var e = Current;
            if (IsShop && e != null && e.Owned && _reopenAt < 0f) WearCurrent();
            else MenuInput(ui, FlashKey.Confirm);
        }
        if (FlashNav.Pressed(FlashKey.Back))
        {
            // doppeltes Zurück: sicher verlassen (falls der Film hängt)
            if (_clock - _lastEsc < 1.2f) { Close(); return; }
            _lastEsc = _clock;
            if (!MenuInput(ui, FlashKey.Back)) Close();
        }
    }

    string? MenuItemOwner(string fn)
    {
        string? best = null;
        foreach (var o in _ui2!.FunctionOwners(fn))
            if (o.IndexOf("menu_item", StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || o.Length < best.Length)) best = o;
        return best;
    }

    /// <summary>Eingabe an das geöffnete Menü: in der Stückauswahl an den Clip "menu_item", sonst über FlashNav (Fokus-Menü).</summary>
    bool MenuInput(VmfPlayer ui, FlashKey key)
    {
        if (_inItems)
        {
            // menu_item kennt kein Move: das Spiel ruft für links/rechts menu_item.Scroll("left"/"right") auf
            if (key == FlashKey.Left || key == FlashKey.Right)
            {
                var sc = MenuItemOwner("Scroll");
                if (sc != null) { ui.CallClip(sc, "Scroll", key == FlashKey.Left ? "left" : "right"); _inputAt = _clock + 0.7f; return true; }
                return false;   // nie an menu_square (würde die Kategorie wechseln)
            }
            string fn = key == FlashKey.Confirm ? "A" : key == FlashKey.Back ? "B" : "";
            var best = fn.Length > 0 ? MenuItemOwner(fn) : null;
            if (best != null && ui.CallClip(best, fn)) return true;
        }
        return FlashNav.Send(ui, key);
    }

    /// <summary>Kamera der Kategorie aus der Szene (C_main, C_hats …: Position, Blick, Fov); ohne Szene um den Avatar.</summary>
    void UpdateCamera(float dt)
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        Vector3 pos, fwd, up; float fov;
        var c = SceneObject(_cat, true) ?? SceneObject("C_main", true);
        if (c != null && c.HasPosition)
        {
            var m = c.Matrix * _holder;
            pos = m.Translation;
            fwd = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, m));
            up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, m));
            fov = System.Math.Clamp(c.Fov, 10f, 90f);
            // Nahaufnahmen (Hüte, Brillen, Schuhe …): das Original zeigt sie in 4:3 – hier etwas weiter, damit Kopf bzw. Füße ganz im Bild sind
            if (fov < 30f) fov = MathF.Min(fov * CloseUpFov, 40f);
        }
        else
        {
            var spine = _preview?.BoneInScene("SPINE1");
            var look = spine.HasValue ? Vector3.Transform(spine.Value, _holder) : Vector3.Transform(new Vector3(0f, 1f, 0f), _place * _holder);
            var from = st.RenderToGame(cam.Position);
            var front = from - look; front.Y = 0f;
            front = front.LengthSquared() < 1e-4f ? Vector3.UnitZ : Vector3.Normalize(front);
            pos = look + front * 2.8f + Vector3.UnitY * 0.3f;
            fwd = Vector3.Normalize(look - pos); up = Vector3.UnitY;
            fov = 45f;
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
