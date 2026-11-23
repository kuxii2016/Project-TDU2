using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using Paradise.Game.Utils;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Schönheitsklinik (Spots ESUR_A Ibiza/Hawaii, ESUR_CA Casino; Interieur interior/isur_a.bnk bzw. isur_ca.bnk) – Port aus der Original-exe,
/// GMSurgery.cpp (Code 0xBED040–0xBF2C00, VTable 0x14FD56C, GAME_MODE_SURGERY = 33).
///   Auswahlpunkt "Hostess_area" Typ 27 (Chirurg) -> <see cref="Open"/>. Oberfläche "SHOPSAVATAR" (frontend/&lt;res&gt;/shopsavatar.bnk), Label "surgeon"
///   (0xBEDCE0). Der Film meldet "surg:cine:intr" -> Kopfteile sichern (Verband), Helm/Brille ab, Kino C_OBSERVE; an dessen Ende (Ereignis 3,
///   0xBF0760) öffnet 0xBEEB60 das Menü: _root.openMenuSurgeon(), Kinos C_HEAD und C_360 (rechter Stick/Maus scrubbt C_360, 0xBF0EE9), C_LIFE.
///   Befehle (0xBF14E0 / 0xBF1090): "surg:edit:open|canc|vali|rand" und "surg:edit:&lt;menü&gt;:&lt;punkt&gt;[:scne|scpr]" mit den Menüs
///   cran, frhe, chbo, chee, lofa, chin, eyes, eybr, ears, nose, mout, skin; je Menü open (Regler zeigen), canc (zurück), vali (übernehmen).
///   Regler: der Film bewegt sie selbst (0..9, /:NbNotches = 9), das Spiel liest den Wert (/:HeadWidth …) und rechnet ihn über min/max des
///   FaceFX-Knotens auf den Gesichtsparameter um (0xBEE730: remap(x, 0, 9, min, max) x 100 -> i8).
///   Preis (0xBEEF10): Summe |Raste neu - Raste gespeichert| x DBSurgeryCost je Regler (ohne CustPos Asian/Afric), EYE_COLOR und
///   EYE_BROWS_TYPE pauschal, FRECKLES und TINT je Raste -> _root.TotalPriceStr; "evmo" -> /:EnoughMoney.
///   Kauf "vali" (0xBF137E): COLLECTION_SURGERY, NbSurgeries + 1, Geld ab, Geld-Anzeige läuft mit Preis/2 je Sekunde herunter (0xBEDAB0);
///   danach (0xBF1017) Verband anlegen (0xBF07C0, BandageUtils), neues Aussehen speichern, Ausgangs-Kino C_NEW_FACE_E (Exterior-Bank des
///   Spots, im Original über GAME_MODE_CINE 0x1F), dann Freifahrt.
///   "rand" (0xBEE2C0): Zufallsgesicht; "canc" (0xBF114F): Avatar zurück, Menü zu.
/// Mitspieler: das Aussehen (AvatarConfig mit Gesicht) geht mit AvatarConfigs.SetLocal an den Server und über die Spielerliste an alle.
/// </summary>
public class GAME_MODE_SURGERY : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_SURGERY;
    static readonly ScreenDef[] _screens = { ScreenDefs.Avatar };
    public override IReadOnlyList<ScreenDef> Screens => _phase == Phase.Outro ? Array.Empty<ScreenDef>() : _screens;
    protected override ScreenDef Screen => ScreenDefs.Avatar;

    // ================================================================== Regler (exe 0x1B20BE0 Arbeit / 0x1B20EF0 übernommen, je 16 Byte)
    struct Slot { public sbyte Value; public float Norm, Min, Max; }
    static readonly Slot[] _cur = new Slot[AvatarConfig.FaceParamCount], _work = new Slot[AvatarConfig.FaceParamCount];

    /// <summary>Raster der Regler (exe 0x14A9644 = 9.0, /:NbNotches).</summary>
    const float Notches = 9f;
    /// <summary>Gesichtsparameter mit vertauschtem min/max (exe Tabelle 0xBEE294, Index 0..30: Byte 0).</summary>
    static readonly HashSet<int> Swapped = new() { 0, 2, 11, 21, 22, 28, 30 };
    /// <summary>Alter = Regler "CustPos Older" (0xBEE816 / 0xBEE680).</summary>
    const int ParamOlder = 43;

    sealed record Item(string Tok, int Param, string Var);

    /// <summary>Menüs und Regler wie die Unter-Handler 0xBEF2B0…0xBF0300 (Reihenfolge = Reihenfolge von "open").</summary>
    static readonly Dictionary<string, Item[]> Menus = new(StringComparer.Ordinal)
    {
        ["cran"] = new[] { new Item("dept", 36, "/:HeadDepth"), new Item("heig", 37, "/:HeadHeight"), new Item("widt", 38, "/:HeadWidth") },
        ["frhe"] = new[] { new Item("brpu", 35, "/:BrowPushed") },
        ["chbo"] = new[] { new Item("size", 4, "/:CheekBonesSize"), new Item("heig", 31, "/:CheekBonesHeight") },
        ["chee"] = new[] { new Item("fath", 15, "/:FatOrThin") },
        ["lofa"] = new[] { new Item("heig", 22, "/:LowerFaceHeight"), new Item("thro", 29, "/:Throat"), new Item("jath", 0, "/:JawThin"), new Item("jasi", 16, "/:JawSize") },
        ["chin"] = new[] { new Item("size", 6, "/:ChinSize"), new Item("orie", 5, "/:ChinOrientation"), new Item("updo", 30, "/:ChinUpandDown"), new Item("slwi", 32, "/:ChinSlimorWide") },
        ["eyes"] = new[] { new Item("opng", 10, "/:EyesOpening"), new Item("type", 12, "/:EyesType"), new Item("rile", 11, "/:EyesRightandLeft"), new Item("updo", 13, "/:EyesUpandDown"), new Item("heig", 9, "/:EyesHeight"), new Item("widt", 14, "/:EyesWidth") },
        ["eybr"] = new[] { new Item("insi", 33, "/:EyebrowsInside"), new Item("ousi", 34, "/:EyebrowsOutside"), new Item("push", 35, "/:EyebrowsPushed"), new Item("rota", 39, "/:EyebrowsRotate"), new Item("size", 40, "/:EyebrowsSize") },
        ["ears"] = new[] { new Item("orie", 7, "/:EarOrientation"), new Item("size", 8, "/:EarSize") },
        ["nose"] = new[] { new Item("size", 26, "/:NoseSize"), new Item("shap", 25, "/:NoseShape"), new Item("tasm", 2, "/:NoseTallorSmall"), new Item("push", 1, "/:NosePushed"), new Item("thth", 27, "/:NoseThickandThin"), new Item("updo", 28, "/:NoseUpandDown") },
        ["mout"] = new[] { new Item("posi", 23, "/:MouthPosition"), new Item("size", 24, "/:MouthSize"), new Item("lisi", 20, "/:LipsSize"), new Item("lido", 19, "/:LipsDowninandOut"), new Item("liuo", 21, "/:LipsUpinandOut") },
        ["skin"] = new[] { new Item("sage", ParamOlder, "/:Age") },
    };

    // ================================================================== Augen / Brauen (Listen 0x18BD2B0 / 0x18BD2C0, je Geschlecht)
    /// <summary>
    /// Augenfarben: 25 Zellen des 5x5-Iris-Atlas je Geschlecht (EyeID = Geschlecht x 25 + Zelle, AvatarModel: EyeID % 25), Namen aus
    /// db_data (DBLoc 4062–4078, 2553–2560). Die exe füllt die Liste im verschlüsselten Teil – Zuordnung Zelle -> Name rekonstruiert (CHECKLIST).
    /// </summary>
    static readonly int[] EyeNames = { 4062, 4063, 4064, 4065, 4066, 4067, 4068, 4069, 4070, 4071, 4072, 4073, 4074, 4075, 4076, 4077, 4078, 2553, 2554, 2555, 2556, 2557, 2558, 2559, 2560 };
    /// <summary>Brauen: 4 Zellen des 2x4-Atlas je Geschlecht (EyebrowsID = Geschlecht x 4 + Typ), Namen "Augenbrauen 1–4" (2159, 1515–1517).</summary>
    static readonly int[] BrowNames = { 2159, 1515, 1516, 1517 };

    static int EyeId(int sex, int i) => sex * EyeNames.Length + i;
    static int BrowId(int sex, int i) => sex * BrowNames.Length + i;
    static int EyeIndex(int id) => id >= 0 ? id % EyeNames.Length : 0;
    static int BrowIndex(int id) => id >= 0 ? id % BrowNames.Length : 0;

    // ================================================================== Zustand
    enum Phase { Closed, Intro, Observe, Menu, Paying, Outro }
    static Phase _phase = Phase.Closed;
    static VmfPlayer? _sui;
    static AvatarConfig _orig = new(), _edit = new();
    static float _clock, _lastEsc = -10f;
    static int _eye, _eyeOk, _brow, _browOk;           // aktuell / übernommen (exe +0x1BB970 / +0x1BB974)
    static float _tint, _freckle;                       // übernommen (exe +0x1BB938 / +0x1BB93C)
    static int _price;                                  // exe +0x1BB944
    static float _rot;                                  // C_360-Anteil (exe +0x1BB940)
    static long _moneyTarget; static float _moneyShown, _moneyRate;   // exe +0x1BB948 / +0x1BB94C
    static bool _purchased;                             // exe +0x1BB978
    /// <summary>Spätester Start von "surg:cine:intr", falls der Film den Befehl nicht schickt (wie der Friseur sein Menü selbst startet).</summary>
    static float _introAt = -1f;
    const float IntroWait = 3f;
    static SpotCinematics? _cin, _ext;
    static bool _menuCin;                               // C_HEAD/C_360 laufen (sonst Ersatz: Anprobe-Avatar wie der Friseur)
    static AvatarPreview? _preview;
    static float _defaultTint = -1f;
    static Dictionary<string, int>? _costs;
    static Vector3 _camPos, _camFwd, _camUp; static float _camFov; static bool _camInit;

    internal static bool Active => _phase != Phase.Closed;

    // ================================================================== Öffnen (Auswahlpunkt Typ 27)
    /// <summary>Chirurg angesprochen (InteriorUtils.Activate, Typ 27).</summary>
    internal static bool Open()
    {
        if (!AvatarLibrary.Ready) { Log.Info("Schönheitsklinik: Avatar-Daten werden noch geladen …"); return false; }
        if (_t == null || _scene == null) return false;
        // Original: gesperrt, solange der Verband sitzt (Karte /:surgery_locked 0xBD7326, Text 4405)
        if (BandageUtils.Active) { BandageUtils.Notify(BandageUtils.TextSurgeryLocked, "You cannot go back into surgery until you have your bandages removed."); return false; }
        GameMode.Set(Gamemode.GAME_MODE_SURGERY);
        return true;
    }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE && _phase != Phase.Closed) return;   // Weiterspielen nach dem Pausemenü
        if (_phase == Phase.Outro) return;
        // Fehler beim Öffnen dürfen das Spiel nicht festhalten (Modus ohne Oberfläche und ohne Eingabe): zurück in den Raum
        try { OpenShop(ctx); }
        catch (Exception ex) { Log.Error("Schönheitsklinik: Öffnen fehlgeschlagen: " + ex); CloseState(); GameMode.Set(RoomMode); }
    }

    static void OpenShop(IGameModeContext ctx)
    {
        var ui = ctx.Open(ScreenDefs.Avatar);
        if (ui == null || _t == null) { Log.Warn("Schönheitsklinik: shopsavatar.bnk nicht ladbar"); GameMode.Set(RoomMode); return; }
        if (_sui != ui) { if (_sui != null) _sui.FSCommand -= OnCommand; _sui = ui; ui.FSCommand += OnCommand; }
        _orig = AvatarConfigs.Local.Clone();
        _edit = _orig.Clone();
        _purchased = false; _price = 0; _moneyTarget = 0; _moneyRate = 0f;
        _eye = _eyeOk = EyeIndex(_edit.EyeId); _brow = _browOk = BrowIndex(_edit.BrowId);
        _tint = TintOf(_edit); _freckle = _edit.Freckle;
        LoadSlots();
        var sg = TDU2Session.Save?.surgery;
        if (sg != null) sg.beenInSurgeryShop = true;
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        OpenCinematics();
        _camInit = false;
        _phase = Phase.Intro;
        ui.Visible = true;
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:ClubMoney", " ");
        ui.SetVariable("/:MyMoneyStr", Money(MoneyNow));
        ui.GotoLabel("/", "surgeon", true);   // 0xBEDD2C
        // Chirurg angesprochen wie im Spiel: _root.SelItemType = "surgeon", cursor.ItemAction("valid") -> _root.ItemType("valid")
        // (shopsavatar.vmf): Leiste zu, fscommand "surg:cine:intr", gotoAndStop "surgeon_block" (openMenuSurgeon nur bei _root.fake)
        ui.SetVariable("/:SelItemType", "surgeon");
        if (!ui.CallClip("/cursor", "ItemAction", "valid")) Log.Warn("Schönheitsklinik: cursor.ItemAction fehlt im Film");
        _introAt = _clock + IntroWait;
        Log.Info("Schönheitsklinik geöffnet");
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: Klinik bleibt offen
        CloseState();
        base.Exit(ctx, to);
    }

    static void CloseState()
    {
        if (_phase == Phase.Closed) return;
        _phase = Phase.Closed;
        _introAt = -1f;
        EndCinematics();
        _preview?.Dispose(); _preview = null;
        if (_sui != null) { _sui.FSCommand -= OnCommand; _sui = null; }
        if (_t != null) _t.NearClip = 0.05f;
        if (_costs != null) Log.Info("Schönheitsklinik geschlossen");
    }

    /// <summary>Zurück in den Modus des Raums (Laufen).</summary>
    static void Close()
    {
        CloseState();
        GameMode.Set(RoomMode);
    }

    // ================================================================== Kinos
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

    /// <summary>"surg:cine:intr" (0xBF1568): Kopfteile für den Verband sichern, Helm/Brille ab (0xBF0AE0), C_OBSERVE (sonst gleich das Menü).</summary>
    static void Intro()
    {
        if (_phase != Phase.Intro) return;
        _introAt = -1f;
        BandageUtils.SaveWorn(_orig);
        BandageUtils.TakeOffHeadwear(_edit);
        RefreshAvatar(true);
        if (_cin != null && _cin.PlayMain("C_OBSERVE")) { _phase = Phase.Observe; return; }
        OpenMenu();
    }

    /// <summary>0xBEEB60(1): Regler neu laden, _root.openMenuSurgeon(), C_HEAD + C_360 (C_360 auf 0,95 s gestellt), C_LIFE, Augen-/Brauenliste.</summary>
    static void OpenMenu()
    {
        var ui = _sui; if (ui == null) return;
        _cin?.StopMain();
        LoadSlots();
        _phase = Phase.Menu;
        _menuCin = false;
        // exe 0xBEEC65: C_360 gehalten (0,95 / Länge, Zeit = Anteil x Länge), dazu C_HEAD und C_LIFE
        if (_cin != null && ShopCinema.Start360(_cin, out _rot, "C_HEAD", "C_LIFE")) _menuCin = true;
        else ShowPreview();
        // Der Film setzt _root.Input erst im Bild "surgeon_stop" (Laufmodus); ItemType("valid") springt daran vorbei
        // nach "surgeon_block". Eingaben ans Menü wie beim Friseur freigeben.
        ui.SetValue("/:Input", true);
        if (!ui.CallClip("/", "openMenuSurgeon")) Log.Warn("Schönheitsklinik: _root.openMenuSurgeon fehlt im Film");
    }

    /// <summary>Ohne C_360 in der Bank: Anprobe-Avatar am Darsteller "Avatar" der HEAD-Szene, Kamera C_HEAD (wie der Friseur).</summary>
    static void ShowPreview()
    {
        _preview?.Dispose();
        _preview = new AvatarPreview(_v.Renderer);
        var act = InteriorNpcs.FindObject("Avatar", "HEAD");
        _preview.Place(act, act?.Clip, _edit.Sex, "hd_sitstand_loop-m", "co_stand-w");
        if (act == null && _t.Streamer != null && _t.Camera != null)
            _preview.Pose = Matrix4x4.CreateTranslation(Vector3.Transform(_t.Streamer.RenderToGame(_t.Camera.Position) + _t.Camera.Forward * 2f - Vector3.UnitY * 1.6f, _holderInv));
        _preview.Show(_edit, true);
    }

    /// <summary>Gesicht/Aussehen auf die Darsteller (Kino-Avatar bzw. Anprobe) – ohne Neuaufbau, solange sich nur das Gesicht ändert.</summary>
    static void RefreshAvatar(bool look)
    {
        if (look) _cin?.SetPlayer(_edit.Clone());
        else _cin?.UpdatePlayerFace(_edit);
        _preview?.Show(_edit, false);
    }

    // ================================================================== Regler-Daten (0xBEE0F0)
    static FaceFxActor? Actor => FaceFxLibrary.Actor(_edit.Sex == 1);

    /// <summary>Werte und min/max je Gesichtsparameter laden (min/max = Knoten des FaceFX-Darstellers, vertauscht nach 0xBEE294).</summary>
    static void LoadSlots()
    {
        var actor = Actor;
        for (int i = 0; i < AvatarConfig.FaceParamCount; i++)
        {
            var s = new Slot { Value = _edit.Face[i], Min = -1f, Max = 1f };
            int n = actor != null ? FaceFxLibrary.NodeOf(actor, i) : -1;
            if (n >= 0)
            {
                var node = actor!.Nodes[n];
                if (i <= 30 && Swapped.Contains(i)) { s.Min = node.Max; s.Max = node.Min; }
                else { s.Min = node.Min; s.Max = node.Max; }
            }
            s.Norm = s.Value / 100f;
            _cur[i] = s; _work[i] = s;
        }
        _tint = TintOf(_edit); _freckle = _edit.Freckle;
    }

    /// <summary>exe 0xDAF9A0: x von [a, b] linear auf [c, d].</summary>
    static float Remap(float x, float a, float b, float c, float d) => MathF.Abs(b - a) < 1e-9f ? c : c + (x - a) * (d - c) / (b - a);

    /// <summary>Raste eines Reglers 0..9 (0xBEDDE0: remap(norm, min, max, 0, 9), auf die nächste ganze Raste, ,5 abgerundet).</summary>
    static int Notch(float norm, float min, float max)
    {
        float f = Remap(norm, min, max, 0f, Notches);
        int i = (int)f;
        return f > i + 0.5f ? i + 1 : i;
    }

    /// <summary>MSVC _ftol (abschneiden), wie die Rasten von Tint/Sommersprossen (0xBF0553, 0xBEF130).</summary>
    static int Trunc(float f) => (int)f;

    static float TintOf(AvatarConfig c) => c.Tint >= 0f ? c.Tint : (_defaultTint >= 0f ? _defaultTint : 0f);

    /// <summary>"open" eines Menüs (0xBEDE60): Regler auf ihre Raste.</summary>
    static void ShowSlot(VmfPlayer ui, string var, int p) => ui.SetValue(var, Notch(_cur[p].Norm, _cur[p].Min, _cur[p].Max));

    /// <summary>Regler bewegt (0xBEE730): Wert aus dem Film, über min/max auf den Parameter, Alter mitziehen.</summary>
    static void StepSlot(VmfPlayer ui, string var, int p)
    {
        float x = ReadFloat(ui, var);
        var w = _cur[p];
        w.Norm = Remap(x, 0f, Notches, w.Min, w.Max);
        w.Value = (sbyte)System.Math.Clamp((int)(w.Norm * 100f), sbyte.MinValue, sbyte.MaxValue);
        _work[p] = w;
        _edit.Face[p] = w.Value;
        if (p == ParamOlder) _edit.Age = AgeOf(w);
        RefreshAvatar(false);
    }

    /// <summary>"vali" eines Menüs (0xBEED10): Arbeitswert übernehmen.</summary>
    static void AcceptSlot(int p) { _cur[p] = _work[p]; if (p == ParamOlder) _edit.Age = AgeOf(_cur[p]); }

    /// <summary>"canc" eines Menüs (0xBEEDD0): Avatar auf den übernommenen Wert zurück.</summary>
    static void CancelSlot(int p)
    {
        _work[p] = _cur[p];
        _edit.Face[p] = _cur[p].Value;
        if (p == ParamOlder) _edit.Age = AgeOf(_cur[p]);
    }

    /// <summary>Alter aus dem Regler "CustPos Older" (0xBEE680: remap(norm, min, max, 0, 1)).</summary>
    static float AgeOf(Slot s) => Remap(s.Norm, s.Min, s.Max, 0f, 1f);

    static float ReadFloat(VmfPlayer ui, string var)
    {
        var s = ui.GetVariable(var);
        return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }

    // ================================================================== Preis (0xBEEF10)
    /// <summary>DBSurgeryCost._cost je _surgname (0xBEEEB0, Vergleich ohne Groß-/Kleinschreibung); fehlt der Name: 500 $.</summary>
    static int Cost(string name)
    {
        if (_costs == null)
        {
            _costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(Path.Combine(FlashHost.BnkDir, "database", "db_data.cpr"))));
                foreach (var e in root.Get("DBSurgeryCost")?.Items ?? new List<XmbNode>())
                {
                    string n = e.S("_surgname", "") ?? "";
                    if (n.Length > 0 && !_costs.ContainsKey(n)) _costs[n] = e.I("_cost");
                }
                Log.Info("Schönheitsklinik: DBSurgeryCost " + _costs.Count + " Einträge");
            }
            catch (Exception ex) { Log.Warn("Schönheitsklinik: DBSurgeryCost nicht lesbar: " + ex.Message); }
        }
        return _costs.TryGetValue(name, out int c) ? c : 500;
    }

    static void UpdatePrice()
    {
        var ui = _sui; if (ui == null) return;
        int price = 0;
        for (int i = 0; i < AvatarConfig.FaceParamCount; i++)
        {
            if (i == 41 || i == 42) continue;   // CustPos Asian / Afric
            var w = _work[i];
            int now = Notch(w.Norm, w.Min, w.Max);
            int saved = Notch(_orig.Face[i] / 100f, _cur[i].Min, _cur[i].Max);
            price += System.Math.Abs(now - saved) * Cost(FaceFxLibrary.CustomNodes[i]);
        }
        if (EyeId(_edit.Sex, _eye) != EyeId(_orig.Sex, EyeIndex(_orig.EyeId))) price += Cost("EYE_COLOR");
        if (BrowId(_edit.Sex, _brow) != BrowId(_orig.Sex, BrowIndex(_orig.BrowId))) price += Cost("EYE_BROWS_TYPE");
        price += System.Math.Abs(Trunc(Remap(_edit.Freckle, 0f, 1f, 0f, Notches)) - Trunc(Remap(_orig.Freckle, 0f, 1f, 0f, Notches))) * Cost("FRECKLES");
        price += System.Math.Abs(Trunc(Remap(TintOf(_edit), 0f, 1f, 0f, Notches)) - Trunc(Remap(TintOf(_orig), 0f, 1f, 0f, Notches))) * Cost("TINT");
        _price = price;
        ui.SetVariable("/:TotalPriceStr", Money(price));
    }

    /// <summary>_root.LockSurgeryValid = Preis nicht &gt; 0 (0xBEDEB0).</summary>
    static void UpdateLock() => _sui?.SetValue("/:LockSurgeryValid", !(_price > 0));

    static long MoneyNow => TDU2Session.Save?.money ?? 0;
    static string Money(long v) => "$ " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number);

    // ================================================================== Befehle des Films
    static string Tok(string[] p, int i) => i < p.Length ? (p[i].Length > 4 ? p[i][..4] : p[i]) : "";

    static void OnCommand(string cmd, string args)
    {
        if (_phase == Phase.Closed) return;
        var ui = _sui; if (ui == null) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (FrontendSounds.Command(c)) return;
        switch (c)
        {
            case "dbxo": AnswerDialog(); return;
            case "moup": ui.SetVariable("/:MyMoneyStr", Money(MoneyNow)); return;
            case "evmo": ui.SetValue("/:EnoughMoney", _price <= MoneyNow ? 1 : 0); return;   // 0xBF1768
            case "gofr": case "wmon": if (_phase != Phase.Paying && _phase != Phase.Outro) Close(); return;
        }
        var p = c.Split(':');
        if (p.Length < 2 || Tok(p, 0) != "surg") { Log.Debug("Schönheitsklinik: " + c); return; }
        switch (Tok(p, 1))
        {
            case "cine": Intro(); return;
            case "edit": Edit(ui, p); UpdatePrice(); return;   // 0xBF1550 / 0xBF1558
        }
    }

    /// <summary>"surg:edit:…" (0xBF1090).</summary>
    static void Edit(VmfPlayer ui, string[] p)
    {
        if (_phase != Phase.Menu) return;
        string menu = Tok(p, 2), item = Tok(p, 3), dir = Tok(p, 4);
        switch (menu)
        {
            case "open":
                ui.SetVariable("/:MyMoneyStr", Money(MoneyNow));
                ui.SetVariable("/:TotalPriceStr", "0");
                UpdateLock();
                ui.SetValue("/:NbNotches", Notches);   // 0xBEDD50
                return;
            case "canc":   // 0xBF114F: Avatar zurück (0xBEDFA0), Regler neu, Menü zu (0xBEEB60(0))
                _edit = _orig.Clone();
                _eye = _eyeOk = EyeIndex(_edit.EyeId); _brow = _browOk = BrowIndex(_edit.BrowId);
                LoadSlots();
                RefreshAvatar(true);
                Close();
                return;
            case "vali": Purchase(); return;
            case "rand": Randomize(); UpdatePrice(); UpdateLock(); return;   // 0xBF147D
            case "skin": Skin(ui, item); return;
        }
        if (!Menus.TryGetValue(menu, out var items)) { Log.Debug("Schönheitsklinik: Menü " + menu); return; }
        switch (item)
        {
            case "open":
                foreach (var it in items) ShowSlot(ui, it.Var, it.Param);
                if (menu == "eyes") ui.SetVariable("/:EyesColor", EyeName(_eye));
                if (menu == "eybr") ui.SetVariable("/:EyebrowsType", BrowName(_brow));
                return;
            case "canc":
                foreach (var it in items) CancelSlot(it.Param);
                if (menu == "eyes") { _eye = _eyeOk; ApplyEye(); }
                if (menu == "eybr") { _brow = _browOk; ApplyBrow(); }
                RefreshAvatar(false);
                return;
            case "vali":
                foreach (var it in items) AcceptSlot(it.Param);
                if (menu == "eyes") _eyeOk = _eye;
                if (menu == "eybr") _browOk = _brow;
                return;
            case "colo" when menu == "eyes":   // 0xBEF911: scne nächste, scpr vorige Augenfarbe
                _eye = Cycle(_eye, EyeNames.Length, dir);
                ApplyEye();
                ui.SetVariable("/:EyesColor", EyeName(_eye));
                RefreshAvatar(false);
                return;
            case "type" when menu == "eybr":   // 0xBEFEBC: Brauentyp
                _brow = Cycle(_brow, BrowNames.Length, dir);
                ApplyBrow();
                ui.SetVariable("/:EyebrowsType", BrowName(_brow));
                RefreshAvatar(false);
                return;
        }
        foreach (var it in items)
            if (it.Tok == item) { StepSlot(ui, it.Var, it.Param); return; }
        Log.Debug($"Schönheitsklinik: {menu}:{item}");
    }

    /// <summary>Menü Haut (0xBF04A0): Alter (Regler 43), Sommersprossen und Tint (0..9 -> 0..1).</summary>
    static void Skin(VmfPlayer ui, string item)
    {
        switch (item)
        {
            case "open":
                ui.SetValue("/:Tint", Trunc(Remap(_tint, 0f, 1f, 0f, Notches)));
                ShowSlot(ui, "/:Age", ParamOlder);
                ui.SetValue("/:Freckle", Trunc(Remap(_freckle, 0f, 1f, 0f, Notches)));
                return;
            case "sage": StepSlot(ui, "/:Age", ParamOlder); return;
            case "frec": _edit.Freckle = Remap(ReadFloat(ui, "/:Freckle"), 0f, Notches, 0f, 1f); RefreshAvatar(false); return;
            case "tint": _edit.Tint = Remap(ReadFloat(ui, "/:Tint"), 0f, Notches, 0f, 1f); RefreshAvatar(false); return;
            case "canc":
                _edit.Tint = _tint;
                CancelSlot(ParamOlder);
                _edit.Freckle = _freckle;
                RefreshAvatar(false);
                return;
            case "vali":
                _tint = TintOf(_edit);
                AcceptSlot(ParamOlder);
                _freckle = _edit.Freckle;
                return;
        }
    }

    static int Cycle(int i, int n, string dir) => dir == "scpr" ? (i - 1 + n) % n : dir == "scne" ? (i + 1) % n : i;
    static void ApplyEye() => _edit.EyeId = EyeId(_edit.Sex, _eye);
    static void ApplyBrow() => _edit.BrowId = BrowId(_edit.Sex, _brow);
    static string EyeName(int i) => Lang(EyeNames[System.Math.Clamp(i, 0, EyeNames.Length - 1)], (i + 1).ToString(CultureInfo.InvariantCulture));
    static string BrowName(int i) => Lang(BrowNames[System.Math.Clamp(i, 0, BrowNames.Length - 1)], (i + 1).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// "rand" (0xBEE2C0): jeder Regler außer 41–48 = (Zufall(min,max) x 3) / 3 (drei Zufallswerte gemittelt), Augenfarbe und Brauen zufällig
    /// (0..100 Schritte durch die Liste), Sommersprossen und Tint zufällig 0..1. Werte gelten sofort als übernommen.
    /// </summary>
    static void Randomize()
    {
        var rnd = Random.Shared;
        float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
        for (int i = 0; i < AvatarConfig.FaceParamCount; i++)
        {
            var s = _cur[i];
            if (i < 41)
                s.Norm = (R(s.Min, s.Max) + R(s.Min, s.Max) + R(s.Min, s.Max)) / 3f;
            s.Value = (sbyte)System.Math.Clamp((int)(s.Norm * 100f), sbyte.MinValue, sbyte.MaxValue);
            _edit.Face[i] = s.Value;
            _cur[i] = s; _work[i] = s;
        }
        _eye = _eyeOk = (int)R(0f, 100f) % EyeNames.Length; ApplyEye();
        _brow = _browOk = (int)R(0f, 100f) % BrowNames.Length; ApplyBrow();
        _freckle = _edit.Freckle = R(0f, 1f);
        _tint = _edit.Tint = R(0f, 1f);
        RefreshAvatar(false);
    }

    // ================================================================== Kauf
    /// <summary>"surg:edit:vali" (0xBF137E): Sammlung, NbSurgeries, Geld, Geld-Anzeige; danach Verband und Ausgang (Run).</summary>
    static void Purchase()
    {
        var sv = TDU2Session.Save;
        if (_purchased || sv == null) return;
        if (_price > sv.money) { _sui?.SetValue("/:EnoughMoney", 0); return; }
        sv.surgery ??= new SurgeryData();
        sv.surgery.nbSurgeries++;                  // Profil +0xD8 (COLLECTION_SURGERY zählt darüber, Gameplay/Collectable)
        long before = sv.money;
        sv.money = System.Math.Max(0, sv.money - _price);   // 0xA32750: Grund 0x13
        _moneyShown = before;
        _moneyTarget = sv.money;
        _moneyRate = _price / 2f;                  // 0xBF1407: Preis / 2 je Sekunde
        _purchased = true;
        _phase = Phase.Paying;
        TDU2Session.NotifyChanged();
        Log.Info($"Schönheitsklinik: Operation gekauft, {_price} $ (Operationen: {sv.surgery.nbSurgeries})");
    }

    /// <summary>Geld-Anzeige herunterzählen (0xBEDAB0).</summary>
    static bool CountMoney(float dt)
    {
        var ui = _sui;
        _moneyShown = MathF.Max(_moneyTarget, _moneyShown - _moneyRate * dt);
        ui?.SetVariable("/:MyMoneyStr", Money((long)_moneyShown));
        return _moneyShown <= _moneyTarget || _moneyRate <= 0f;
    }

    /// <summary>
    /// Nach dem Bezahlen (0xBF1055 -> 0xBF0C80, Rückruf 0xBF07C0): Verband an, Aussehen speichern (Server, DATA, Mitspieler),
    /// Ausgangs-Kino C_NEW_FACE_E der Exterior-Bank (0xBEDFFD), danach Freifahrt.
    /// </summary>
    static void Finish()
    {
        var final = _edit.Clone();
        BandageUtils.PutOn(final);
        AvatarConfigs.SetLocal(final);
        _orig = final.Clone();
        Log.Info("Schönheitsklinik: neues Gesicht gespeichert, Verband an");
        _phase = Phase.Outro;
        if (_sui != null) { _sui.FSCommand -= OnCommand; _sui.Visible = false; _sui.MouseEnabled = false; }
        _cin?.Dispose(); _cin = null;
        _preview?.Dispose(); _preview = null;
        InteriorNpcs.Suppressed = null;
        var s = _spot;
        if (s != null && _v?.Renderer != null) _ext = SpotCinematics.OpenExterior(_v.Renderer, _t.Streamer, s);
        if (_ext != null && _ext.Find("C_NEW_FACE_E") != null)
        {
            _ext.SetPlayer(final);
            ClearScene();
            Paradise.Core.World.WorldWeather.Inside = false;
            _ext.PlayMain("C_NEW_FACE_E");
            return;
        }
        Leave();
    }

    static void Leave()
    {
        var car = _lastCar;
        CloseState();
        ExitInterior(car);
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Bestätigungsbox aus general.bnk wie im Spiel (FlashDialog), Antwort an _root.DialogBox.</summary>
    static void AnswerDialog()
    {
        var ui = _sui; if (ui == null) return;
        FlashDialog.Open(ui, ans =>
        {
            ui.SetVariable("/:PlayerAnswer", ans);
            var owners = ui.FunctionOwners("DialogBox");
            string target = owners.Contains("/") ? "/" : owners.Count > 0 ? owners[^1] : "/";
            ui.CallClip(target, "DialogBox");
        });
    }

    // ================================================================== Ablauf je Bild (0xBF0E10)
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        float dt = time.DeltaTime;
        // Ein Fehler je Bild darf das Spiel nicht festhalten: Klinik abbrechen (Avatar zurück), zurück in den Raum
        try { Tick(ctx, dt); }
        catch (Exception ex) { Log.Error("Schönheitsklinik: " + ex); _edit = _orig.Clone(); Close(); }
    }

    static void Tick(IGameModeContext ctx, float dt)
    {
        _clock += dt;
        if (_phase == Phase.Closed || _t == null) return;
        if (ctx.Paused) return;
        var sg = TDU2Session.Save?.surgery;
        if (sg != null) sg.timeSpentInSurgery += dt;   // StatisticsData._time_spent_in_surgery

        if (_phase == Phase.Outro)
        {
            var ext = _ext;
            if (ext == null) { Leave(); return; }
            ext.Update(dt);
            foreach (var n in ext.TakeEndEvents()) if (n == "C_NEW_FACE_E") { Leave(); return; }
            if (ShopCinema.SkipPressed()) { ext.StopMain(); Leave(); return; }   // überspringbar
            CinemaCamera(ext, dt, true);
            return;
        }

        _cin?.Update(dt);
        _preview?.Update(dt);
        if (_cin != null) InteriorNpcs.Suppressed = _cin.ActiveActors;   // Figur spielt im Kino, nicht zusätzlich im Raum
        if (_defaultTint < 0f) PollDefaultTint();

        if (_phase == Phase.Intro && _introAt > 0f && _clock >= _introAt)
        {
            Log.Warn("Schönheitsklinik: \"surg:cine:intr\" kam nicht vom Film – Intro selbst gestartet");
            Intro();
        }
        if (_phase == Phase.Observe && _cin != null)
        {
            foreach (var n in _cin.TakeEndEvents()) if (n == "C_OBSERVE") { OpenMenu(); break; }
            // überspringbar: Bestätigen beendet C_OBSERVE und öffnet das Menü (Zurück bricht wie bisher ab)
            if (_phase == Phase.Observe && TDU2Controls.ConfirmPressed()) { _cin.StopMain(); OpenMenu(); return; }
            if (_phase == Phase.Observe && !_cin.MainPlaying) OpenMenu();   // Kino weg ohne Ende-Ereignis: nicht hängen bleiben
        }

        if (_phase == Phase.Menu) Rotate(dt);
        if (_phase == Phase.Paying && CountMoney(dt)) { Finish(); return; }

        var ui = _sui;
        if (ui != null && _phase is (Phase.Intro or Phase.Observe or Phase.Menu) && !FlashDialog.Active)
        {
            // Menü-Tasten wie der Flash Player des Spiels (auch vor dem Menü, wie der Friseur); 2x Zurück: Abbruch (wie Friseur)
            if (FlashNav.Update(ui, out bool backHandled))
            {
                if (_phase != Phase.Menu || _clock - _lastEsc < 1.2f) { CancelAll(); return; }
                _lastEsc = _clock;
                if (!backHandled) { CancelAll(); return; }
            }
        }
        if (_cin != null && (_phase == Phase.Observe || _menuCin)) CinemaCamera(_cin, dt, false);
        else HeadCamera(dt);
    }

    static void CancelAll()
    {
        _edit = _orig.Clone();
        RefreshAvatar(true);
        Close();
    }

    /// <summary>Drehen mit rechtem Stick / Tasten / Maus (0xBF0EE9): gemeinsam mit dem Friseur (ShopCinema.Rotate).</summary>
    static void Rotate(float dt)
    {
        if (!_menuCin || _cin == null) return;
        ShopCinema.Rotate(_cin, ref _rot, dt, SurgeryConfig.MouseRotateScale);
    }

    /// <summary>ScatteringCoeff der 2DM (Haut), solange die Konfiguration keinen Tint hat (Preis-Raste des Originals).</summary>
    static void PollDefaultTint()
    {
        var m = _preview?.Avatar;
        if (m != null && m.Ready && m.SkinScattering >= 0f) { _defaultTint = m.SkinScattering; return; }
        float v = _cin?.PlayerSkinScattering ?? -1f;
        if (v >= 0f) _defaultTint = v;
        if (_defaultTint >= 0f && _edit.Tint < 0f) { _tint = _defaultTint; }
    }

    static void CinemaCamera(SpotCinematics cin, float dt, bool outdoor)
    {
        if (!ShopCinema.Camera(cin, outdoor)) { if (!outdoor) HeadCamera(dt); return; }
        _camInit = false;
    }

    /// <summary>Ersatz-Kamera: C_HEAD der Szene (sonst auf den Kopf des Anprobe-Avatars), weich nachgeführt (wie der Friseur).</summary>
    static void HeadCamera(float dt)
    {
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        Vector3 pos, fwd, up; float fov;
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
            var d = from - look; d = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.UnitZ;
            pos = look + d * 1.1f;
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
        var st = _t?.Streamer;
        if (_phase != Phase.Outro) base.Render(ctx, time);   // Interieur-Autos und Figuren
        if (st == null) return;
        _cin?.Render(st.RootMatrix);
        _ext?.Render(st.RootMatrix);
        if (_phase != Phase.Closed && _phase != Phase.Outro) _preview?.Render(_holder * st.RootMatrix);
    }
}
