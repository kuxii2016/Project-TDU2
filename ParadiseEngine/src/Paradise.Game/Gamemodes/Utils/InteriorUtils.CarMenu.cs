using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Math;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Fahrzeugmenü der Interieurs – nur die in Garage und Händler wortgleichen Teile von Unity TDU2CarDealer.cs: Zustand, Phasen aus
/// dem Film-Zustand, Einsteigen (enter_car: Cockpit-Kamera, Motor, Hupe). Das Menü selbst (öffnen, Befehle, Daten, Vergleichen,
/// Zurück, Losfahren) liegt im jeweiligen Modus: GAME_MODE_GARAGE bzw. GAME_MODE_SHOP; Testfahrt: GAME_MODE_TESTCAR.
/// </summary>
public abstract partial class InteriorUtils
{


    // ================================================================== Fahrzeugmenü (Unity TDU2CarDealer.cs)
    // Auto anvisieren + Bestätigen: Garage home.vmf _root.SetMenu() (Losfahren, Einsteigen, Daten, Vergleichen),
    // Händler shopscar.vmf _root.SetMiniMenu() (Kaufen, Einsteigen, Daten, Vergleichen, Testfahrt).
    // Noch nicht portiert: Kauffenster (Felgen/Lack/Innenraum mit Vorschaubild), Testfahrt, Zubehörmenü im HUD (Verdeck/Blinker).
    internal static readonly string[] DealerClips = { "cursor", "title_bar", "car_datas", "price", "help" };
    internal const float CarAimDistance = 1f;
    internal static float _aimCarDist, _aimInfoAt = -1f;
    internal static object? _infoHiddenFor;
    internal static bool _aimQuery;
    internal static VmfPlayer? _hooked;
    internal static CarPhysics? _compareCar;      // Vergleichsauto (Mini-Menü "Vergleichen"), bis rsco oder Verlassen
    internal static bool _dealerMenu, _garageMenu;
    internal static DisplayCar? _dealerCar;
    internal static float _driveOutAt = -1f; internal static CarPhysics? _driveOutCar;
    internal static string CarMenuStop => _garageMenu ? "garage_stop" : "cd_stop";

    /// <summary>Ablauf des Fahrzeugmenüs – die Phase wird jedes Bild aus dem Zustand des Films bestimmt (Unity DPhase).</summary>
    internal enum DPhase { Opening, Mini, Window, Datas, Car, DriveOut, Idle }
    internal static DPhase _dPhase; internal static float _dPhaseAt, _dIdleSince = -1f, _miniStuckAt = -1f, _miniDiagAt = -1f;

    /// <summary>Menü offen: ESC/Rücktaste gehören dem Film (GAME_MODE_LIVING.Run).</summary>
    internal static bool DealerMenuOpen => _dealerMenu;

    internal static void SetDealerPhase(DPhase p)
    {
        if (_dPhase == p) return;
        Log.Debug((_garageMenu ? "Garage" : "Autohändler") + ": " + _dPhase + " -> " + p);
        _dPhase = p; _dPhaseAt = _time;
    }

    internal static bool OpenLabel(string? l) => !string.IsNullOrEmpty(l) && l != "default";

    internal static bool DealerCarOpen => _carIn != CarIn.None || (_ui != null && OpenLabel(_ui.Label("/enter_car")) && _ui.Label("/enter_car") != "close");
    internal static bool DealerDatasOpen { get { string l = _ui?.Label("/car_datas") ?? ""; return l.StartsWith("fd_") || l == "data_back"; } }
    internal static bool DealerWindowOpen
    {
        get
        {
            string bl = _ui?.Label("/buy") ?? "";
            return bl == "car_action" || bl == "options_stop" || bl == "text" || bl == "no_text" || bl == "next"
                || bl == "confirm" || bl == "back_options" || bl == "valid_car_dealer" || bl == "valid_bike_dealer";
        }
    }
    internal static bool DealerMiniOpen => _ui != null && OpenLabel(_ui.Label("/cursor/mini_menu"));

    internal static DPhase ProbeDealerPhase()
    {
        if (_driveOutAt > 0f) return DPhase.DriveOut;
        if (DealerCarOpen) return DPhase.Car;
        if (DealerWindowOpen) return DPhase.Window;
        if (DealerDatasOpen) return DPhase.Datas;
        if (DealerMiniOpen) return DPhase.Mini;
        return _dPhase == DPhase.Opening && _time - _dPhaseAt < 1.5f ? DPhase.Opening : DPhase.Idle;
    }

    internal static bool IsBike(CarPhysics? car) { int cat = car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0; return cat >= 18 && cat <= 24; }

    // ------------------------------------------------------------------ Weiterleitung an den Modus (Garage: GAME_MODE_GARAGE, Händler: GAME_MODE_SHOP)
    /// <summary>Fahrzeugmenü des Raums öffnen (Garage SetMenu, Händler SetMiniMenu).</summary>
    internal static bool OpenDealerMenu(DisplayCar dc) => _kind == Kind.Garage ? GAME_MODE_GARAGE.OpenGarageMenu(dc) : GAME_MODE_SHOP.OpenShopMenu(dc);
    /// <summary>Fahrzeugmenü schließen (zurück in den Laufmodus).</summary>
    internal static void EndDealerMenu(bool restoreCar = true) { if (_garageMenu) GAME_MODE_GARAGE.EndGarageMenu(); else GAME_MODE_SHOP.EndShopMenu(restoreCar); }
    /// <summary>ESC/Rücktaste im Fahrzeugmenü (Unity TDU2MenuFlow -> DealerBack).</summary>
    internal static void DealerBack() { if (_garageMenu) GAME_MODE_GARAGE.GarageBack(); else GAME_MODE_SHOP.ShopBack(); }
    /// <summary>Fahrzeugmenü ohne Mitwirkung des Films schließen.</summary>
    internal static void DealerForceClose() { if (_garageMenu) GAME_MODE_GARAGE.GarageForceClose(); else GAME_MODE_SHOP.ShopForceClose(); }
    /// <summary>Jedes Bild, solange das Fahrzeugmenü offen ist.</summary>
    internal static void UpdateDealerMenu(float dt) { if (_garageMenu) GAME_MODE_GARAGE.UpdateGarageMenu(dt); else GAME_MODE_SHOP.UpdateShopMenu(dt); }
    /// <summary>FSCommands des Fahrzeugmenüs.</summary>
    internal static bool OnDealerCommand(string c) => _garageMenu ? GAME_MODE_GARAGE.OnGarageCommand(c) : GAME_MODE_SHOP.OnShopCommand(c);

    /// <summary>
    /// Sicherung (wie Unity DiaryWatchdog), in Garage und Händler gleich: bleibt das Mini-Menü in seiner Öffnen-Animation angehalten
    /// stehen (Log 11:07:02: 40 s "open", Eingaben wurden ignoriert), auf "stop" weiterschalten, damit es bedienbar ist.
    /// </summary>
    internal static void UnstickMiniMenu()
    {
        if (_ui == null) return;
        if (_ui.Label("/cursor/mini_menu") != "open") { _miniStuckAt = -1f; return; }
        var mm = _ui.Find("/cursor/mini_menu")?.Sprite;
        if (mm != null && _time - _dPhaseAt > 2f && _miniDiagAt != _dPhaseAt)
        {
            _miniDiagAt = _dPhaseAt;
            Log.Info($"Fahrzeugmenü: Mini-Menü seit 2 s in 'open' – Bild {mm.Frame + 1}, läuft {mm.Playing}, Input {_ui.GetVariable("/:Input")}, Wurzel {_ui.Label("/")}, Cursor {_ui.Label("/cursor")}");
        }
        if (mm != null && !mm.Playing)
        {
            if (_miniStuckAt < 0f) _miniStuckAt = _time;
            else if (_time - _miniStuckAt > 0.5f && _ui.HasLabel("/cursor/mini_menu", "stop"))
            {
                Log.Warn("Fahrzeugmenü: Mini-Menü hing in 'open' (angehalten) – auf 'stop' weitergeschaltet");
                _ui.GotoLabel("/cursor/mini_menu", "stop", true);
                _ui.SetValue("/:Input", true);
                _miniStuckAt = -1f;
            }
        }
        else _miniStuckAt = -1f;
    }

    /// <summary>Beim Verlassen/Neuaufbau der Szene: Menüzustand ohne Film zurücksetzen.</summary>
    internal static void EndDealerModes()
    {
        EndDealerCar();
        GAME_MODE_SHOP.EndLure();
        _dealerMenu = false; _dealerCar = null; _dTints.Clear();
        _driveOutAt = -1f; _driveOutCar = null;
        _compareCar = null; _dChanged = false; _garageMenu = false;
        GAME_MODE_SHOP._previewDirty = false; GAME_MODE_SHOP._previewFor = null;
        if (GAME_MODE_SHOP._previewTask != null) { var t = GAME_MODE_SHOP._previewTask; GAME_MODE_SHOP._previewTask = null; t.ContinueWith(x => { if (!x.IsFaulted) x.Result?.DestroyTextures(); }); }
    }

    internal static void OnUiCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;   // Menü-Sounds (FrontendSounds)
        Log.Info("Interieur-Film -> " + c + (_dealerMenu ? " (Menü " + _dPhase + ", Mini " + (_ui?.Label("/cursor/mini_menu") ?? "-") + ", BtValid " + (_ui?.GetVariable("/:BtValid") ?? "") + ")" : ""));
        if (_dealerMenu)
        {
            bool handled;
            try { handled = OnDealerCommand(c); }
            catch (Exception ex) { Log.Error("Fahrzeugmenü " + c + ": " + ex.Message); handled = true; }
            if (handled) return;
        }
        // Garage ohne Fahrzeugmenü: Fahrzeugliste (dbxo confirm_change_vehicule, seca, gofr)
        if (_kind == Kind.Garage)
        {
            try { if (GAME_MODE_GARAGE.OnGarageListCommand(c)) return; }
            catch (Exception ex) { Log.Error("Fahrzeugliste " + c + ": " + ex.Message); }
        }
        try { GAME_MODE_LIVING.OnHomeCommand(c); }   // Tagebuch, MyTDULife, My House (Unity OnUiCommand)
        catch (Exception ex) { Log.Error("Wohnbereich " + c + ": " + ex.Message); }
    }

    // ================================================================== Einsteigen (enter_car)
    internal enum CarIn { None, Loading, Inside }
    internal static CarIn _carIn;
    internal static DisplayCar? _inCar;
    internal static Task? _inSoundTask;
    internal static VehicleAudio? _inAudio;
    internal static CarAccessories? _inAcc;
    internal static float _inYaw, _inPitch, _inRpm, _inStartT;
    internal static bool _inEngine, _inHornClick, _inBackClick;

    /// <summary>rqca: Fahrersitz vorbereiten (Sound im Hintergrund), dann darf der Lader weiter (CanEnterCar).</summary>
    internal static void BeginDealerCar()
    {
        var dc = _dealerCar;
        if (dc == null || _ui == null) { _ui?.SetValue("/:CanEnterCar", true); return; }
        _carIn = CarIn.Loading; _inCar = dc;
        _inYaw = 0f; _inPitch = 0f; _inEngine = false; _inRpm = 0f; _inStartT = -1f;
        _inHornClick = _inBackClick = false;
        GAME_MODE_SHOP.EndLure();
        if (dc.Assets != null && dc.Assets.Sound == null && Vm != null)
        {
            var a = dc.Assets; string bnk = Vm.BnkDir; var car = dc.Car;
            _inSoundTask = Task.Run(() => CarAssets.LoadSound(a, bnk, car));
        }
        _inAcc = CarAccessories.For(dc.Model);
        PlaceDealerCam();
        _carIn = CarIn.Inside;
        _ui.SetVisible("/enter_car", true);   // Ladeanzeige / Innenansicht des Films (im Laufmodus ausgeblendet)
        _ui.SetValue("/:CanEnterCar", true);   // Lader (enter_car/cmon_loader) -> endLoading: Hilfeleiste "in_car", cain, shin
        _t.Input.CursorLocked = true;
    }

    /// <summary>Augenpunkt und Blickziel des Fahrers im Spielraum (camerasingame Modus 23 bzw. Rückblick).</summary>
    internal static bool CockpitEye(DisplayCar dc, bool back, out Vector3 eye, out Vector3 look)
    {
        var cams = Vm?.Cameras;
        int id = dc.Car.CameraId != 0 ? dc.Car.CameraId : dc.Car.Id;
        var def = cams?.Get(id, 23);
        if (def != null && back) { var b = cams!.Get(id, def.BackMode); if (b != null) def = b; }
        // Fahrzeugraum des Spiels (+X = links) = Elternraum des Visual-Knotens = dc.Pose (Unity: TransformPoint(-x, y, z))
        if (def != null && def.IsFixed)
        {
            eye = Vector3.Transform(def.FixedPos, dc.Pose);
            look = Vector3.Transform(def.FixedTarget, dc.Pose);
            return true;
        }
        // Rückfall: Fahrersitz über den Sitzknoten bzw. vorne links in der Karosserie
        var seat = dc.Model.Find("DVRSEAT_HR") ?? dc.Model.Find("SEAT_HR_L");
        var up = Vector3.TransformNormal(Vector3.UnitY, dc.Pose);
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, dc.Pose);
        var bc = dc.Model.BodyCenter; var be = dc.Model.BodySize * 0.5f;
        eye = seat != null ? Vector3.Transform(dc.Model.LocalToVisualParent(seat).Translation, dc.Pose) + up * 0.7f
                           : Vector3.Transform(bc + new Vector3(be.X * 0.4f, be.Y * 0.45f, 0f), dc.Pose);
        look = eye + (back ? -fwd : fwd) * 3f;
        return seat != null;
    }

    /// <summary>Unity PlaceDealerCam: Fahrersitz + Umsehen (Euler(pitch, yaw) im Unity-Raum, gespiegelt in den Spielraum).</summary>
    internal static void PlaceDealerCam()
    {
        var dc = _inCar; var cam = _t.Camera; var st = _t.Streamer;
        if (dc == null || cam == null || st == null) return;
        bool back = TDU2Controls.Held("lookback") || _inBackClick;
        CockpitEye(dc, back, out var eye, out var look);
        var upG = Vector3.TransformNormal(Vector3.UnitY, dc.Pose);
        var q = U.LookRotation(U.Mirror(look - eye), U.Mirror(upG)) * U.Euler(_inPitch, _inYaw, 0f);
        _t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (U.Mirror(U.Rot(q, Paradise.Core.Math.Vec.Forward)), U.Mirror(U.Rot(q, Paradise.Core.Math.Vec.Up)));
        _t.FieldOfView = 62f;
        _t.NearClip = 0.03f;
    }

    internal static void UpdateDealerCar(float dt)
    {
        var dc = _inCar;
        if (dc == null) { EndDealerCar(); return; }
        _t.Input.CursorLocked = true;
        // Sound fertig geladen: Motor-/Hupen-Klang am Auto (Werte von außen)
        if (_inSoundTask != null && _inSoundTask.IsCompleted) _inSoundTask = null;
        if (_inAudio == null && _inSoundTask == null && dc.Assets?.Sound != null)
        {
            _inAudio = new VehicleAudio { External = true, InteriorView = true, EngineOn = false };
            _inAudio.Setup(dc.Assets);
        }
        if (_inAcc != null) _inAcc.Audio = _inAudio;
        // Umsehen (Unity: Mouse X/Y x 2,2 ≈ Pixel x 0,22)
        _inYaw = Math.Clamp(_inYaw + _t.Input.MouseDeltaX * 0.22f, -150f, 150f);
        _inPitch = Math.Clamp(_inPitch + _t.Input.MouseDeltaY * 0.22f, -55f, 65f);
        if (TDU2Controls.Pressed("doors") && _inAcc != null) _inAcc.ToggleDoors();
        if (TDU2Controls.Pressed("windows") && _inAcc != null) _inAcc.ToggleWindows();
        CarPad(_inAcc);
        if (TDU2Controls.Pressed("starter") || TDU2Controls.ConfirmPressed()) ToggleDealerEngine();
        _inAudio?.SetHorn(TDU2Controls.Held("horn") || _inHornClick);
        bool gas = _inEngine && _inStartT < 0f && TDU2Controls.Held("accel");
        UpdateDealerEngine(dt, gas);
        _inAudio?.Update(dt);
    }

    internal static void ToggleDealerEngine()
    {
        if (_inEngine) { _inEngine = false; _inStartT = -1f; if (_inAudio != null) _inAudio.EngineOn = false; return; }
        _inEngine = true; _inStartT = 0f;
        if (_inAudio != null) { _inAudio.EngineOn = true; _inAudio.Play("EngineStart"); }
    }

    /// <summary>Drehzahl im Stand: Anlassen (Zündzeit), Leerlauf, Gas hoch bis kurz vor den Begrenzer, langsam zurück.</summary>
    internal static void UpdateDealerEngine(float dt, bool gas)
    {
        var c = _inCar?.Car;
        if (c == null) return;
        float idle = MathF.Max(600f, c.RpmMin > 0f ? c.RpmMin : 850f);
        float red = MathF.Max(idle + 1500f, c.RedLine > 0f ? c.RedLine : 7000f);
        float torque = 0f;
        if (!_inEngine) _inRpm = Mathf.MoveTowards(_inRpm, 0f, idle * 2f * dt);
        else if (_inStartT >= 0f)
        {
            _inStartT += dt;
            float ign = MathF.Max(0.3f, c.TimeIgnition);
            _inRpm = Mathf.Lerp(idle * 0.3f, idle * 1.25f, Mathf.Clamp01(_inStartT / ign));
            if (_inStartT >= ign) _inStartT = -1f;
        }
        else if (gas)
        {
            _inRpm = Mathf.MoveTowards(_inRpm, red * 0.97f, (red - idle) * 0.9f * dt);
            if (_inRpm >= red * 0.965f) _inRpm -= (red - idle) * 0.08f;   // Begrenzer
            torque = c.TorqueNm;
        }
        else
        {
            _inRpm = Mathf.MoveTowards(_inRpm, idle, (red - idle) * 0.55f * dt);
            torque = -c.TorqueNm * 0.1f;
        }
        if (_inAudio != null) { _inAudio.ExtRpm = _inRpm; _inAudio.ExtKmh = 0f; _inAudio.ExtTorque = torque; }
    }

    /// <summary>Aussteigen (caou/caca bzw. Menü geschlossen): Kamera zurück an den Fußgänger, Klang/Zubehör aus.</summary>
    internal static void EndDealerCar()
    {
        if (_carIn == CarIn.None) return;
        _carIn = CarIn.None;
        if (_inAcc != null) { _inAcc.CloseAll(); if (_inAcc.RoofOpen) _inAcc.ToggleRoof(); _inAcc.Audio = null; }
        _inHornClick = _inBackClick = false;
        if (_inAudio != null) { _inAudio.SetHorn(false); _inAudio.Dispose(); }
        _inAudio = null; _inAcc = null; _inSoundTask = null;
        _t.FieldOfView = 60f; _t.NearClip = 0.05f;   // wieder Fußgänger (ApplyCamera)
        if (_fp != null) _fp.LookEnabled = false;
        _inCar = null;
    }
    internal static readonly List<RimInfo> _dRims = new();
    internal static readonly List<CarColor> _dColors = new();
    internal static readonly List<InteriorInfo> _dInts = new();
    internal static int _dRim, _dColor, _dInt;
    internal static CarChoice _dOrig;
    internal static bool _dBought, _dChanged;
    internal static readonly Dictionary<string, Color32> _dTints = new();

    internal static int IntVar(string name)
    {
        var s = _ui!.GetVariable(name);
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : 0;
    }
}
