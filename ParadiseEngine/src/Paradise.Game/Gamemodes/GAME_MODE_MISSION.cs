// Story-Schritte portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: DrivingTessMode, GoHomeMode, CaravanMode).
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Racing;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Mission: wie Freifahrt (HUD, Pausemenü, Karte) – Story-Schritte mit Fahraufgabe (Driving Tess, Go Home, Wohnwagen).
/// Ein Interieur (Spot betreten) bekommt seinen eigenen Raum-Modus: der ausdrücklich gesetzte Missionsmodus wird dann freigegeben
/// (Unity: TDU2Interiors läuft unabhängig vom TDU2GameMode).
/// </summary>
public class GAME_MODE_MISSION : GAME_MODE_FREERIDE
{
    public override Gamemode Mode => Gamemode.GAME_MODE_MISSION;

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (InteriorUtils.Inside && !ctx.Paused)
        {
            GameMode.Pop();
            GameMode.Set(InteriorUtils.RoomMode);
            return;
        }
        base.Run(ctx, in time);
    }

    /// <summary>Missionsmodus der Geschichte (wieder) setzen, sobald man draußen in der Freifahrt ist (z.B. nach einem Interieur).</summary>
    static void EnsureMission()
    {
        if (!InteriorUtils.Inside && GameMode.Current == Gamemode.GAME_MODE_FREERIDE) GameMode.Push(Gamemode.GAME_MODE_MISSION);
    }

    /// <summary>
    /// Schritt 4 "Driving Tess" (Original: Spielmodus MISSION, DBEvent TESSEVEN, ig2/tesseven.ig2 "Der Weg zur Solar Crown",
    /// scenarics/packs/pack_events13.bnk): Tess sitzt auf dem Beifahrersitz, Tipp 4032 "Tess zum Drehort fahren.", Ziel = letzter
    /// Checkpoint der ig2 (Solar Crown, Club ECH_IA_1243I). Am Ziel stehen die LKW/der Teppich des Drehorts (C_TRUCK = M_SC_LIFE);
    /// Ankunft (Ziel-Tor durchfahren bzw. langsam im Zielradius) -> Schritt 5.
    /// </summary>
    internal sealed class DrivingTessStep : StoryStep
    {
        public override bool BlocksInput => false;
        public override bool BlocksMap => true;
        public override bool MutesRadio => false;
        // Zeitlimit der Mission (tesseven.ig2: "ChCD" = 1 Countdown, "timG" = 160000 ms), Anzeige über das Original-HUD
        // (hud.vmf /HUD/time_cd mit /:MinCd /:SecCd /:CentCd, letzte 5 s /HUD/time_out "on" + /:time_out)
        float _timeLimit, _timeLeft = -1f;
        int _lastTimeOut = -1;
        bool _hudShown;
        CarAvatar? _tess;
        Vehicle? _veh;
        Vector3 _targetGame;
        bool _hasTarget, _told;
        StoryScene? _decor;
        float _waitCar = 30f, _arriveT;
        // Ziel-Tor wie im Spiel: Checkpoint-Actor "Finish_line" aus pack_cp.bnk (dieselben Modelle wie bei Rennen) am letzten
        // Checkpoint der tesseven.ig2 – das Original zeigt das Ziel der Mission so in der Welt (dazu GPS-Route und Minikarte)
        Ige2Checkpoint? _finishCp;
        int _finishIndex;
        RaceProps? _gate;
        bool _gateBuilt;
        float _gateWait;

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_MISSION);
            if (Manager.Current == null && !Story.SpawnPending) Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            // Ziel: letzter Checkpoint der Mission (tesseven.ig2), sonst der Club-Spot der Solar Crown
            try
            {
                var path = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "ig2"), "tesseven.ig2");
                if (path != null)
                {
                    var ig = Ige2File.Parse(GameFiles.ReadAllBytes(path));
                    if (ig != null && ig.Checkpoints.Count > 0)
                    {
                        _finishIndex = ig.Checkpoints.Count - 1;
                        _finishCp = ig.Checkpoints[_finishIndex];
                        _targetGame = _finishCp.Position; _hasTarget = true;
                    }
                    if (ig != null && ig.Int("ChCD") == 1 && ig.Int("timG") > 0) _timeLimit = ig.Int("timG") / 1000f;
                }
            }
            catch (Exception ex) { Log.Warn("tesseven.ig2: " + ex.Message); }
            if (!_hasTarget) { var s = Story.FindSpot("ECH_IA_1243I"); if (s != null) { _targetGame = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position; _hasTarget = true; } }
            TrySetGps();
            // Drehort-Dekor (LKW, Teppich) am Club
            _decor = CreateScene(new StoryScene.Setup { Bank = "scenarics/packs/pack_events13.bnk", Spot = "ECH_IA_1243I", IntroUi = false });
        }

        public override void Tick(float dt)
        {
            EnsureMission();
            if (_decor != null)
            {
                _decor.Update(dt);
                if (_decor.Loaded && !_decor.IsBackgroundPlaying("C_TRUCK")) _decor.PlayBackground("C_TRUCK", true);
            }
            var veh = Manager.Current;
            if (veh == null)
            {
                _waitCar -= dt;
                if (_waitCar < 0f && !Story.SpawnPending && !InteriorUtils.Inside) { Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I"); _waitCar = 30f; }
                return;
            }
            if (veh != _veh)
            {
                _veh = veh;
                _tess?.Dispose(); _tess = null;
                var cfg = AvatarLibrary.PnjConfig("STO_Tess_Wintory");
                var v = veh;
                if (cfg != null && Manager.Model != null && Story.Vehicles != null) _tess = CarAvatar.Attach(Story.Vehicles.Renderer, Manager.Model, cfg, true, () => v.Steer);
            }
            if (!_told && _decor != null && _decor.Loaded && !Story.SpawnPending)
            {
                _told = true;
                _decor.ShowTutorial(StoryScene.Loc(4032));   // "Tess zum Drehort fahren."
                _decor.PlayFx("EVTE_to_player_01");          // Tess im Auto (pack_events13__<sprache>.bnk)
                if (_timeLimit > 0f) StartTimer();
            }
            if (_timeLeft >= 0f && UpdateTimer(dt)) return;
            if (!_hasTarget) return;
            TrySetGps();
            var pos = Manager.PositionGame;
            float dist = new Vector2(pos.X - _targetGame.X, pos.Z - _targetGame.Z).Length();
            TickGate(dt, dist);
            // Durch das Ziel-Tor gefahren (wie bei Rennen: Durchfahrt zählt, egal wie schnell) -> Zwischensequenz
            if (_finishCp != null && dist < MathF.Max(12f, _finishCp.Width * 0.75f))
            {
                Log.Debug($"STORY Driving Tess: Ziel-Tor durchfahren ({dist:0.0} m, {veh.SpeedKmh:0} km/h, Restzeit {_timeLeft:0.0} s)");
                Story.Go(5);
                return;
            }
            if (dist < 30f && veh.SpeedKmh < 15f)
            {
                _arriveT += dt;
                if (_arriveT > 1f) { Log.Info("Geschichte: Tess am Drehort"); Story.Go(5); }
            }
            else _arriveT = 0f;
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender)
        {
            _decor?.Render(gameToRender);
            if (Manager.Current != null) _tess?.Render(cameraRender, WorldWeather.Inside);
        }

        bool _gpsSet; float _gpsCheck, _clock;

        /// <summary>
        /// Ziel-Tor bauen, sobald das Auto näher als 600 m ist (dann ist der Boden am Ziel geladen und das Tor steht auf der Straße).
        /// Modelle aus worldactors/pack_cp (RaceProps.Preload lädt im Hintergrund). Nur das Original-Modell, kein Ersatz-Tor.
        /// </summary>
        void TickGate(float dt, float dist)
        {
            if (_gateBuilt || _finishCp == null || dist > 600f) return;
            var st = Story.Streamer;
            if (st == null) return;
            string? bank = RaceProps.WorldActorsBank(Manager.BnkDir, st.Index?.IslandName);
            _gateWait += dt;
            if (!RaceProps.Preload(bank, st) && _gateWait < 20f) return;
            _gateBuilt = true;
            try
            {
                var race = new RaceDefinition { Name = "tesseven" };
                var cp = RaceCheckpoint.From(_finishCp, System.Math.Max(1, _finishIndex));
                cp.IsFinish = true;
                race.Checkpoints.Add(cp);
                race.StartPosition = cp.Position + cp.Forward * -50f;   // kein Startplatz in der Nähe (ClearStart)
                _gate = new RaceProps();
                var gates = new List<RaceGate>();
                _gate.Build(st, race, gates, false);
                foreach (var g in gates) g.SetState(RaceGateState.Next);
                if (gates.Count == 0) { _gate.Dispose(); _gate = null; }
                Log.Debug($"STORY Ziel-Tor Driving Tess: {gates.Count} Tor(e) bei {cp.Position} Breite {cp.Width:0.0} m, Bank={bank ?? "-"}");
            }
            catch (Exception ex) { Log.Warn("Ziel-Tor Driving Tess: " + ex.Message); }
        }

        static VmfPlayer? Hud => Story.Vm?.Minimap?.Hud;

        void StartTimer()
        {
            _timeLeft = _timeLimit; _lastTimeOut = -1;
            Log.Debug($"STORY Driving Tess: Zeitlimit {_timeLimit:0} s gestartet, Ziel {_targetGame}");
            var h = Hud;
            if (h == null) return;
            SetTime(h, _timeLeft);
            h.SetVisible("/HUD/time_cd", true);
            h.GotoLabel("/HUD/time_cd", "open", true);
            _hudShown = true;
        }

        void HideTimer()
        {
            var h = Hud;
            // Countdown und die roten Schluss-Sekunden ganz weg (sonst stand z.B. "4" noch in der nächsten Mission)
            if (h != null)
            {
                h.GotoLabel("/HUD/time_cd", "default", false); h.GotoLabel("/HUD/time_out", "default", false);
                h.SetVariable("/:time_out", "");
                h.SetVisible("/HUD/time_cd", false); h.SetVisible("/HUD/time_out", false);
            }
            _hudShown = false; _lastTimeOut = -1;
        }

        static void SetTime(VmfPlayer h, float seconds)
        {
            int ms = System.Math.Max(0, (int)(seconds * 1000f + 0.5f));
            h.SetVariable("/:MinCd", (ms / 60000).ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:SecCd", (ms / 1000 % 60).ToString("00", CultureInfo.InvariantCulture));
            h.SetVariable("/:CentCd", (ms % 1000 / 10).ToString("00", CultureInfo.InvariantCulture));
        }

        /// <summary>Restzeit herunterzählen; abgelaufen -> Tess beschwert sich, zurück zum Hotel, neuer Versuch. true = diesen Tick beendet.</summary>
        bool UpdateTimer(float dt)
        {
            _timeLeft -= dt;
            var h = Hud;
            if (h != null)
            {
                if (!_hudShown) { h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true); _hudShown = true; }
                SetTime(h, _timeLeft);
                int sec = (int)MathF.Ceiling(_timeLeft);
                if (sec <= 5 && sec >= 1 && sec != _lastTimeOut)
                {
                    if (_lastTimeOut < 0) { h.SetVisible("/HUD/time_out", true); h.GotoLabel("/HUD/time_out", "on", true); }
                    _lastTimeOut = sec;
                    h.SetVariable("/:time_out", sec.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (_timeLeft > 0f) return false;
            Log.Info("Geschichte: Zeit abgelaufen – Tess zum Drehort, neuer Versuch");
            HideTimer();
            _decor?.PlayFx("EVTE_loose_01");
            _timeLeft = -1f; _told = false; _veh = null;
            Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            return true;
        }

        /// <summary>GPS-Ziel (Drehort) setzen, sobald das GPS da ist (entsteht erst mit der Minikarte); bleibt gesetzt, bis Tess ankommt.</summary>
        void TrySetGps()
        {
            if (!_hasTarget) return;
            var gps = Gps.Instance;
            if (gps == null) return;
            _clock = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            if (_gpsSet && _clock < _gpsCheck) return;
            _gpsCheck = _clock + 2f;
            if (!gps.HasTarget || !gps.IsTargetNear(_targetGame, 450f)) { gps.SetTarget(_targetGame, !_gpsSet); _gpsSet = true; }
        }

        public override void Exit()
        {
            _tess?.Dispose(); _tess = null;
            HideTimer();
            _gate?.Dispose(); _gate = null; _gateBuilt = false;
            _decor?.CloseTutorial();
            _decor?.Dispose(); _decor = null;
            if (Gps.Instance != null && _hasTarget) Gps.Instance.ClearTarget(true);
            GameMode.Pop();
        }
    }

    /// <summary>
    /// Schritt 9 "Go Home" (Spielmodus MISSION): zum ersten Haus fahren – der Wohnwagen EPH_L0C_1254I (DBHouse Stufe 0, "Wohnwagen",
    /// GamePC DefaultHouse des Originals). Ziel 4035 "Zum ersten Haus Fahren.", GPS zum Spot; angekommen (langsam am Spot oder drinnen)
    /// -> Schritt 10.
    /// </summary>
    internal sealed class GoHomeStep : StoryStep
    {
        public const string HouseSpot = "EPH_L0C_1254I";
        public override bool BlocksInput => false;
        StoryScene? _ui;
        Vector3 _targetGame;
        bool _hasTarget, _told, _gpsSet;
        float _t, _arriveT, _gpsCheck;

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_MISSION);
            _ui = CreateScene(new StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
            // Fortsetzen nach dem Anmelden: noch kein Auto -> eigenes Auto am Gebrauchtwagenhändler (Ende von Schritt 7)
            if (Story.NeedsCar) Story.SpawnAtSpot(Story.PlayerCar(), "ESTOL_B");
            var s = Story.FindSpot(HouseSpot);
            if (s != null) { _targetGame = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position; _hasTarget = true; }
            else Log.Warn("Geschichte: Spot " + HouseSpot + " nicht gefunden");
        }

        public override void Tick(float dt)
        {
            _t += dt;
            EnsureMission();
            _ui?.Update(dt);
            if (!_told && _ui != null && _ui.Loaded && _t > 1.5f) { _told = true; _ui.ShowGoal(StoryScene.Loc(4035)); }   // "Zum ersten Haus Fahren."
            if (InteriorUtils.Inside && IsCaravan(InteriorUtils.CurrentSpot)) { Story.Go(10); return; }
            var veh = Manager.Current;
            if (!_hasTarget || veh == null) return;
            var gps = Gps.Instance;
            if (gps != null && (!_gpsSet || _t >= _gpsCheck))
            {
                _gpsCheck = _t + 2f;
                if (!gps.HasTarget || !gps.IsTargetNear(_targetGame, 450f)) { gps.SetTarget(_targetGame, !_gpsSet); _gpsSet = true; }
            }
            var pos = Manager.PositionGame;
            float dist = new Vector2(pos.X - _targetGame.X, pos.Z - _targetGame.Z).Length();
            if (dist < 25f && veh.SpeedKmh < 15f) { _arriveT += dt; if (_arriveT > 1f) Story.Go(10); }
            else _arriveT = 0f;
        }

        public static bool IsCaravan(Spot? s) => s != null && GamePC.FindSpot(new List<Spot> { s }, HouseSpot) != null;

        public override void Exit()
        {
            _ui?.CloseGoal();
            _ui?.Dispose(); _ui = null;
            if (_gpsSet && Gps.Instance != null) Gps.Instance.ClearTarget(true);
        }
    }

    /// <summary>
    /// Schritt 10 "Home sweet home : Caravan": am Wohnwagen; Tipp "spva" (2225: Ort mit Bestätigen betreten), bis man drinnen ist.
    /// Danach ist die Geschichte auf Ibiza durch (Schritt 11 "Hawai First Freeride" ist noch nicht umgesetzt) -> freie Fahrt.
    /// </summary>
    internal sealed class CaravanStep : StoryStep
    {
        public override bool BlocksInput => false;
        StoryScene? _ui;
        bool _told, _inside;
        float _t, _insideT;

        public override void Enter()
        {
            if (!InteriorUtils.Inside) GameMode.Push(Gamemode.GAME_MODE_MISSION);
            // Nur ohne Auto UND draußen (Fortsetzen nach dem Anmelden). Wer per Enter schon im Wohnwagen ist, hat kein Auto –
            // ein Ersatz-Auto würde ihn mitten im Betreten auf die Straße setzen (Interieur halb geladen, Spieler hängt draußen).
            if (Story.NeedsCar) Story.SpawnAtSpot(Story.PlayerCar(), GoHomeStep.HouseSpot);
            Log.Debug($"STORY Wohnwagen: Auto={(Manager.Current != null ? Manager.Current.Car.FileName : "-")} drinnen={Story.InInterior} Ersatzauto={(Story.SpawnPending ? "ja" : "nein")}");
            _ui = CreateScene(new StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
        }

        public override void Tick(float dt)
        {
            _t += dt;
            _ui?.Update(dt);
            bool inside = InteriorUtils.Inside && GoHomeStep.IsCaravan(InteriorUtils.CurrentSpot);
            if (!_told && !inside && _ui != null && _ui.Loaded && _t > 1f) { _told = true; _ui.ShowTutorial(StoryScene.Loc(2225)); }
            if (inside && !_inside)
            {
                _inside = true;
                _ui?.CloseTutorial();
                if (GameMode.Current == Gamemode.GAME_MODE_MISSION) GameMode.Pop();   // drinnen: Wohnbereich (LIVING) wie sonst
                Log.Info("Geschichte: Home sweet home – Wohnwagen erreicht");
            }
            if (_inside) { _insideT += dt; if (_insideT > 2f) Story.Go(11); }
        }

        public override void Exit()
        {
            _ui?.CloseTutorial();
            _ui?.Dispose(); _ui = null;
            if (!_inside && GameMode.Current == Gamemode.GAME_MODE_MISSION) GameMode.Pop();
        }
    }
}
