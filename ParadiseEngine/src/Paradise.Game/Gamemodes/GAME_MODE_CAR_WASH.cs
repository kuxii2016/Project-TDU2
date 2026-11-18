using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Waschanlage (Spots ECW_*, GAME_MODE_CAR_WASH 36) – Port aus der Original-exe (Klasse um 0xBFC010, Cinematics der Exterior-Bank
/// islands/&lt;insel&gt;/exterior/ecw_a.bnk über <see cref="SpotCinematics"/>):
///   Einfahrt: C_INTRO_E_1 (exe 0xAB24C0: C_INTRO_E_2 nur mit Spot-Merkmal +0x140 und Fahrzeug-Merkmal +0x5F5 – Bedeutung offen).
///   Menü (0xBFC280): /:MyMoneyStr, /:wash_price = DBEnvironnement.WashingPrice, /:CanWash = Geld &gt;= Preis, Label "car_wash"
///     (shopscar.bnk), dazu C_LOOP (Schleife).
///   "cwok" (0xBFC160): Fahrzeug _islightcarwashable -> C_OUTRO_FERRARI, sonst C_WASHING; Geld -= WashingPrice.
///   B im Menü -> Film "car_wash_out" -> "gofr" -> C_OUTRO (ohne Wäsche).
///   Kino-Ende (0xBFCE90): alle CF_-Requisiten aus; C_WASHING -> C_INCAR (+ Babes C_BABE_01..03, 0xBFC780) ; C_INCAR (Start:
///     Innenkamera des Autos, HUD an) -> Ende: Babes aus (0xBFBF80), C_OUTRO; C_OUTRO/C_OUTRO_FERRARI -> Freifahrt (Modus 1).
///   Schmutz: nach der Wäsche 0 (CarDirt, Spielstand). Danach steht das Auto an der Ausfahrt des Spots (Position2/Richtung2).
/// Nicht portiert (CHECKLIST): Musik/Fx der .cin, Platzierung der Babes am Auto (0xBFC780 rechnet Plätze aus der Autolänge),
///   C_INTRO_E_2-Bedingung, Requisiten in der Hand der Babes (Schwamm/Schlauch/Radio).
/// </summary>
public class GAME_MODE_CAR_WASH : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_CAR_WASH;
    static readonly ScreenDef[] _screens = { ScreenDefs.Dealer };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    enum Phase { None, Loading, Intro, Menu, Washing, InCar, Outro }
    static Phase _phase;
    static Spot? _spot;
    static CarPhysics? _car;
    static SpotCinematics? _cin;
    static VmfPlayer? _ui;
    static bool _hooked, _paid, _menu;
    static float _loadT;
    static long _price = 1500;
    static IGameModeContext? _ctx;

    static readonly string[] Babes = { "C_BABE_01", "C_BABE_02", "C_BABE_03" };

    /// <summary>Waschanlage läuft (Einfahrt erkennt keine Spots, kein Pausemenü über die Kinos).</summary>
    internal static bool Active => _phase != Phase.None;

    // ================================================================== Start (aus InteriorUtils an der Einfahrt)
    internal static bool Begin(IGameModeContext ctx, Spot s)
    {
        if (Active || !InteriorUtils.Init(ctx)) return false;
        var vm = InteriorUtils.Vm;
        if (vm?.Current == null) { Log.Warn("Waschanlage: nur mit Auto"); return false; }
        var t = InteriorUtils._t;
        string? bank = ExteriorBank(s);
        var cin = SpotCinematics.Open(InteriorUtils._v.Renderer, bank);
        if (cin == null || cin.Find("C_WASHING") == null) { cin?.Dispose(); Log.Warn("Waschanlage: Bank/Kinos fehlen für " + s.Code); return false; }
        _ctx = ctx; _spot = s; _car = vm.Current.Car; _cin = cin; _paid = false; _menu = false; _loadT = 0f;
        cin.Frame = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, s.Value1)) * Matrix4x4.CreateTranslation(s.Position);
        if (t.Streamer != null && bank != null) cin.AttachProps(t.Streamer, bank, s.Code);
        cin.LoadCar(vm, _car);
        _price = WashingPrice(vm);
        vm.Despawn();
        InteriorUtils._v.Suspended = true;
        _phase = Phase.Loading;
        LoadingScreen.Show("Waschanlage " + s.Code, () => _phase != Phase.Loading, 0.5f, false);
        Log.Info($"Waschanlage {s.Code}: Auto {_car.FileName}, Schmutz {CarDirt.GetSaved(_car):0.00}, Preis {_price} $");
        GameMode.Set(Gamemode.GAME_MODE_CAR_WASH);
        return true;
    }

    static string? ExteriorBank(Spot s)
    {
        var dir = InteriorUtils._t.Streamer?.Index?.ExteriorDir;
        if (dir == null || string.IsNullOrEmpty(s.Code)) return null;
        var f = Path.Combine(dir, s.Code.ToLowerInvariant() + ".bnk");
        return Paradise.Core.Files.GameFiles.Exists(f) ? f : null;
    }

    /// <summary>DBEnvironnement.WashingPrice (exe: Spielwert "WashingPrice", 0xBFC2E2).</summary>
    static long WashingPrice(VehicleManager vm)
    {
        var env = vm.Database?.Root?.Get("DBEnvironnement");
        if (env != null)
            foreach (var r in env.Items)
                if (string.Equals(r.S("_variable", ""), "WashingPrice", StringComparison.OrdinalIgnoreCase)) return (long)r.F("_value", 1500f);
        return 1500;
    }

    // ================================================================== Spielmodus
    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        _ui = ctx.Open(ScreenDefs.Dealer);
        if (_ui != null)
        {
            if (!_hooked) { _ui.FSCommand += OnCommand; _hooked = true; }
            if (!_menu) { _ui.MouseEnabled = false; foreach (var d in _ui.Root.List.Values) d.Visible = false; }
        }
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;
        if (_phase != Phase.None) Finish(false);
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        var cin = _cin;
        if (_phase == Phase.None || cin == null) { GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        if (ctx.Paused) return;
        float dt = time.DeltaTime;
        cin.Update(dt);
        switch (_phase)
        {
            case Phase.Loading:
                _loadT += dt;
                var st = InteriorUtils._t.Streamer;
                bool worldOk = st == null || st.ReadyAround(st.GameToRender(_spot!.Position), 200f);
                if ((cin.CarLoading || !cin.AudioReady || !worldOk) && _loadT < 20f) break;
                // exe 0xAB24C0: C_INTRO_E_1, sonst C_INTRO_E
                if (cin.PlayMain(cin.Find("C_INTRO_E_1") != null ? "C_INTRO_E_1" : "C_INTRO_E")) _phase = Phase.Intro;
                else OpenMenu();
                break;
            case Phase.Menu:
                UpdateMenu();
                break;
        }
        foreach (var n in cin.TakeEndEvents()) OnCinematicEnd(n);
        ApplyCamera();
    }

    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        var st = InteriorUtils._t?.Streamer;
        if (st != null) _cin?.Render(st.RootMatrix);
    }

    // ================================================================== Kino-Ende (0xBFCE90)
    static void OnCinematicEnd(string n)
    {
        var cin = _cin; if (cin == null) return;
        switch (n)
        {
            case "C_INTRO_E_1":
            case "C_INTRO_E_2":
            case "C_INTRO_E":
                if (_phase == Phase.Intro) OpenMenu();
                break;
            case "C_WASHING":
                // Wäsche fertig: Schmutz weg, Blick aus dem Auto (C_INCAR, ohne Kino-Kamera), Babes waschen ringsum
                Clean();
                _phase = Phase.InCar;
                foreach (var b in Babes) cin.PlayBackground(b, true);
                if (!cin.PlayMain("C_INCAR")) StartOutro();
                break;
            case "C_INCAR":
                StartOutro();
                break;
            case "C_OUTRO":
            case "C_OUTRO_FERRARI":
                Finish(true);
                break;
        }
    }

    static void StartOutro()
    {
        var cin = _cin; if (cin == null) return;
        cin.StopBackground();   // Babes aus (0xBFBF80)
        _phase = Phase.Outro;
        if (!cin.PlayMain("C_OUTRO")) Finish(true);
    }

    // ================================================================== Menü (0xBFC280, shopscar.vmf "car_wash")
    static void OpenMenu()
    {
        var cin = _cin; if (cin == null) return;
        _phase = Phase.Menu;
        cin.PlayMain("C_LOOP", true);
        var ui = _ui;
        if (ui == null) { Wash(); return; }
        _menu = true;
        var texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language);
        FlashLocalization.Apply(ui, texts);
        foreach (var d in ui.Root.List.Values) d.Visible = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:WhatPlatform", 1);
        SetMenuValues();
        ui.GotoLabel("/", "car_wash", true);
        ui.MouseEnabled = true;
        InteriorUtils._t.Input.CursorLocked = false;
        Log.Info($"Waschanlage: Menü, Preis {_price} $, Geld {TDU2Session.Save?.money ?? -1}");
    }

    static void SetMenuValues()
    {
        var ui = _ui; if (ui == null) return;
        long money = TDU2Session.Save?.money ?? 0;
        ui.SetVariable("/:MyMoneyStr", GAME_MODE_SHOP.Money(money));
        ui.SetVariable("/:ClubMoney", " ");
        ui.SetVariable("/:wash_price", GAME_MODE_SHOP.Money(_price));
        ui.SetValue("/:CanWash", money >= _price);
    }

    static void UpdateMenu()
    {
        var ui = _ui;
        if (!_menu || ui == null || FlashDialog.Active) return;
        FlashNav.Directions(ui);
        if (FlashNav.Pressed(FlashKey.Confirm)) FlashNav.Send(ui, FlashKey.Confirm);
        if (FlashNav.Pressed(FlashKey.Back)) FlashNav.Send(ui, FlashKey.Back);
    }

    static void CloseMenu()
    {
        _menu = false;
        FlashDialog.Cancel();
        if (_ui != null) _ui.MouseEnabled = false;
        InteriorUtils._t.Input.CursorLocked = true;
    }

    static void OnCommand(string cmd, string args)
    {
        if (_phase == Phase.None || _ui == null) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        switch (c)
        {
            case "sbdd": case "wain": SetMenuValues(); return;          // Menü aufgebaut
            case "dbxo": FlashDialog.Open(_ui); return;                  // cant_wash_car (zu wenig Geld)
            case "cwok": Pay(); Wash(); return;                          // 0xBFC160
            case "gofr":                                                 // car_wash_out: ohne Wäsche hinaus
                if (_phase == Phase.Menu && !_paid) { CloseMenu(); StartOutro(); }
                else CloseMenu();
                return;
        }
    }

    static void Pay()
    {
        if (_paid) return;
        _paid = true;
        var sv = TDU2Session.Save;
        if (sv != null)
        {
            sv.money = Math.Max(0, sv.money - _price);
            _ui?.SetVariable("/:MyMoneyStr", GAME_MODE_SHOP.Money(sv.money));
            SessionAutosave.Schedule(2f);
        }
        Log.Info($"Waschanlage: bezahlt {_price} $");
    }

    /// <summary>cwok: Fahrzeug mit DBCarPhysicsData._islightcarwashable -> C_OUTRO_FERRARI, sonst C_WASHING.</summary>
    static void Wash()
    {
        var cin = _cin; if (cin == null) return;
        CloseMenu();
        bool light = _car?.Raw != null && _car.Raw.B("_islightcarwashable");
        if (light && cin.Find("C_OUTRO_FERRARI") != null)
        {
            Clean();
            _phase = Phase.Outro;
            cin.PlayMain("C_OUTRO_FERRARI");
            return;
        }
        _phase = Phase.Washing;
        if (!cin.PlayMain("C_WASHING")) { Clean(); StartOutro(); }
    }

    static void Clean()
    {
        if (_car == null) return;
        CarDirt.SetSaved(_car, 0f);
        CarDirt.ApplyTo(_cin?.Car, _car);
    }

    // ================================================================== Kamera
    /// <summary>Kino-Kamera; C_INCAR (NO_CAMERA): Innenkamera des Autos (camerasingame Modus 23, exe 0xBFCEED).</summary>
    static void ApplyCamera()
    {
        var t = InteriorUtils._t; var cam = t.Camera; var st = t.Streamer; var cin = _cin;
        if (cam == null || st == null || cin == null) return;
        Vector3 eye, fwd, up; float fov;
        if (!cin.Camera(out eye, out fwd, out up, out fov))
        {
            if (_phase != Phase.InCar || cin.CarPoseGame is not Matrix4x4 pose || !InCarEye(pose, out eye, out var look)) return;
            fwd = Vector3.Normalize(look - eye);
            up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, pose));
            fov = 60f;
        }
        t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (fwd, up);
        t.FieldOfView = fov;
        t.NearClip = 0.05f;
    }

    static bool InCarEye(Matrix4x4 pose, out Vector3 eye, out Vector3 look)
    {
        eye = look = Vector3.Zero;
        var vm = InteriorUtils.Vm; var car = _car;
        if (car == null) return false;
        int id = car.CameraId != 0 ? car.CameraId : car.Id;
        var def = vm?.Cameras?.Get(id, 23);
        if (def != null && def.IsFixed)
        {
            eye = Vector3.Transform(def.FixedPos, pose);
            look = Vector3.Transform(def.FixedTarget, pose);
            return true;
        }
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, pose);
        eye = pose.Translation + Vector3.TransformNormal(new Vector3(0.35f, 1.15f, -0.2f), pose);
        look = eye + fwd * 3f;
        return true;
    }

    // ================================================================== Ende
    static void Finish(bool spawn)
    {
        if (_phase == Phase.None) return;
        var s = _spot; var car = _car;
        _phase = Phase.None;
        CloseMenu();
        _cin?.Dispose(); _cin = null;
        var t = InteriorUtils._t;
        InteriorUtils._v.Suspended = false;
        t.Input.CursorLocked = false;
        t.FlyCameraEnabled = true;
        t.NearClip = 1f;
        _spot = null;
        if (!spawn || s == null || car == null) return;
        var vm = InteriorUtils.Vm;
        var gp = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
        var fwd = new Vector3(MathF.Sin(s.Heading2), 0f, MathF.Cos(s.Heading2));
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.UnitZ;
        vm?.SpawnCar(car.FileName, gp, Vector3.Normalize(fwd));
        Log.Info($"Waschanlage {s.Code} verlassen ({(_paid ? "gewaschen" : "ohne Wäsche")})");
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }
}
