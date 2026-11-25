// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: FirstFreerideMode, MeetingTessMode, SolarCrownExteriorMode,
// SolarCrownInteriorMode).
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using System.Numerics;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// GAME_MODE_CINE: Zwischensequenzen der Geschichte (Ausfahrt aus der Garage, Meeting Tess, Solar Crown außen/innen). Die Abläufe
/// laufen in den Schritten unten (jedes Bild über <see cref="Story"/>); der Modus selbst sperrt Pausemenü/Karte und HUD.
/// </summary>
public sealed class GAME_MODE_CINE : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_CINE;

    static readonly Vector4 Black = new(0f, 0f, 0f, 1f), White = new(1f, 1f, 1f, 1f);

    /// <summary>
    /// Schritt 2 "First Freeride": Ausfahrt-Kino C_FIRST_OUTRO am Haus EPH_L3DI_5067I (islands/ibiza/exterior/eph_l3di.bnk, der California
    /// PLAYER_CAR fährt aus der Garage), dann freie Fahrt; nach 90 s (Story-Timer) C_FADE_FREERIDE (Weißblende) -> Schritt 3 "Meeting Tess".
    /// </summary>
    internal sealed class FirstFreerideStep : StoryStep
    {
        enum St { Loading, Outro, Driving, Fade }
        St _st;
        StoryScene? _scene;
        float _timer = 90f, _fadeT, _loadT;
        bool _carRequested;
        Matrix4x4? _lastCarPose;

        public override bool BlocksInput => _st != St.Driving;

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_CINE);
            if (Manager.Current != null)
            {
                // Spielstand mit Auto (Fortsetzen): ohne Ausfahrt-Kino direkt fahren
                _st = St.Driving;
                GameMode.Push(Gamemode.GAME_MODE_MISSION);   // Story-Schritt, keine freie Fahrt (keine Mitspieler)
                return;
            }
            Suspend();
            string island = Story.Streamer?.Index?.IslandName ?? "ibiza";
            _scene = CreateScene(new StoryScene.Setup { Bank = "islands/" + island + "/exterior/eph_l3di.bnk", Spot = "EPH_L3DI_5067I", LoadSets = false, IntroUi = false });
            _st = St.Loading;
            var s = Story.FindSpot("EPH_L3DI_5067I");
            if (s != null) Story.HoldCameraAt(s.Position + new Vector3(0f, 6f, 0f), Vector3.UnitZ);
        }

        public override void Tick(float dt)
        {
            switch (_st)
            {
                case St.Loading:
                    _loadT += dt;
                    if (_scene == null || _scene.Failed) { SpawnFallback(); return; }
                    _scene.SetFade(1f, Black);
                    UpdateScene(_scene, dt);
                    if (!_scene.Loaded) return;
                    if (!_carRequested) { _carRequested = true; _scene.SetCar(Story.StoryCar, null, "PLAYER_CAR"); }
                    if ((_scene.CarModel == null || !ReadyAround(_scene.Frame.Translation, 300f)) && _loadT < 20f) return;
                    if (!_scene.PlayMain("C_FIRST_OUTRO")) { SpawnFallback(); return; }
                    _scene.SetFade(0f, Black);
                    _st = St.Outro;
                    break;
                case St.Outro:
                    if (_scene == null) { SpawnFallback(); return; }
                    if (Story.AllowSkip && Story.SkipPressed()) _scene.SkipMain();
                    UpdateScene(_scene, dt);
                    if (_scene.CarPoseGame is Matrix4x4 pose) _lastCarPose = pose;
                    foreach (var n in _scene.TakeEndEvents())
                        if (n == "C_FIRST_OUTRO") { SpawnFromCinematic(); return; }
                    break;
                case St.Driving:
                    // Story-Timer (DAT_00ede78c: 90 s), nur außerhalb von Interieurs/Menüs
                    if (Manager.Current != null && GameMode.Current != Gamemode.GAME_MODE_MAP) _timer -= dt;
                    if (_timer <= 0f) { _st = St.Fade; _fadeT = 0f; GameMode.Push(Gamemode.GAME_MODE_CINE); }
                    break;
                case St.Fade:
                    // C_FADE_FREERIDE: Weißblende (fade_blanc), dann Meeting Tess
                    _fadeT += dt;
                    StoryFade.Set(System.Math.Clamp(_fadeT / 2f, 0f, 1f), White);
                    if (_fadeT > 2.5f) Story.Go(3);
                    break;
            }
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        void SpawnFromCinematic()
        {
            var car = Story.StoryCar;
            if (_lastCarPose is not Matrix4x4 pose) { SpawnFallback(); return; }
            var p = pose.Translation;
            var f = Vector3.TransformNormal(Vector3.UnitZ, pose); f.Y = 0f;
            CleanupScene();
            Story.RequestSpawn(car, p + Vector3.UnitY * 0.3f, f.LengthSquared() > 1e-4f ? Vector3.Normalize(f) : Vector3.UnitZ);
            AfterSpawn();
        }

        void SpawnFallback()
        {
            CleanupScene();
            Story.SpawnAtSpot(Story.StoryCar, "EPH_L3DI_5067I");
            AfterSpawn();
        }

        void AfterSpawn()
        {
            _st = St.Driving; _timer = 90f;
            GameMode.Push(Gamemode.GAME_MODE_MISSION);   // Story-Schritt, keine freie Fahrt (keine Mitspieler)
        }

        void CleanupScene()
        {
            _scene?.Dispose(); _scene = null;
        }

        public override void Exit() { CleanupScene(); }
    }

    /// <summary>
    /// Schritt 3 "Meeting Tess" (Original: Spielmodus CINE, Kino-Bank scenarics/cinematics/intro/meeting_tess am Spot E_HOTELA_1266I):
    /// C_MEETING_TESS (Mann) bzw. C_MEETING_TESS_W (Frau) mit Tess, Avatar und dem California (PLAYER_CAR). Danach -> Schritt 4.
    /// </summary>
    internal sealed class MeetingTessStep : StoryStep
    {
        StoryScene? _scene;
        bool _carRequested, _started;
        float _loadT;
        string _cine = "";

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_CINE);
            Suspend();
            _cine = PlayerIsWoman ? "C_MEETING_TESS_W" : "C_MEETING_TESS";
            // Rahmen = Exterior des Hotels (Spot-Position/Richtung); den Ursprung der .sce gleicht StoryScene aus
            _scene = CreateScene(new StoryScene.Setup { Bank = "scenarics/cinematics/intro/meeting_tess.bnk", Spot = "E_HOTELA_1266I" });
            var s = Story.FindSpot("E_HOTELA_1266I");
            if (s != null) Story.HoldCameraAt(s.Position2 + Vector3.UnitY * 3f, Vector3.UnitZ);
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Next(); return; }
            if (!_started)
            {
                _loadT += dt;
                _scene.SetFade(1f, White);
                UpdateScene(_scene, dt);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig());
                    _scene.SetCar(Story.StoryCar, "D_CAR", "PLAYER_CAR");
                }
                if ((_scene.CarModel == null || !ReadyAround(_scene.Frame.Translation, 300f)) && _loadT < 20f) return;
                _scene.SetFade(0f, White);
                if (!_scene.PlayMain(_cine)) { Next(); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && Story.SkipPressed()) _scene.SkipMain();
            UpdateScene(_scene, dt);
            foreach (var n in _scene.TakeEndEvents()) if (n == _cine) { Next(); return; }
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        void Next()
        {
            // Auto an den Start der Mission (tesseven.ig2, erster Checkpoint: Straße vor dem Hotel, Fahrtrichtung), sonst an den
            // Auto-Punkt des Spots. (D_CAR ist ein Entity__PosCam – ein Kamerapunkt, keine Auto-Platzierung.)
            Cleanup();
            if (!Story.SpawnAtMissionStart("tesseven.ig2")) Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            StoryFade.Set(0f, default);
            GameMode.Pop();
            Story.Go(4);
        }

        void Cleanup() { _scene?.Dispose(); _scene = null; }

        public override void Exit() { Cleanup(); StoryFade.Set(0f, default); }
    }

    /// <summary>
    /// Schritt 5 "Solar Crown exterior" (Spielmodus CINE, scenarics/cinematics/intro/solar_crown_ext am Club ECH_IA_1243I):
    /// C_SOLAR_CROWN_E (Ankunft am Drehort, Tess, Sicherheitsleute, Kamerateam, PLAYER_CAR) -> Schritt 6.
    /// </summary>
    internal sealed class SolarCrownExteriorStep : StoryStep
    {
        StoryScene? _scene;
        CarPhysics? _car;
        bool _carRequested, _started;
        float _loadT;

        public override void Enter()
        {
            _car = Manager.Current?.Car ?? Story.StoryCar;
            GameMode.Push(Gamemode.GAME_MODE_CINE);
            Suspend();
            _scene = CreateScene(new StoryScene.Setup { Bank = "scenarics/cinematics/intro/solar_crown_ext.bnk", Spot = "ECH_IA_1243I" });
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Finish(); return; }
            if (!_started)
            {
                _loadT += dt;
                _scene.SetFade(1f, Black);
                UpdateScene(_scene, dt);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig(), AvatarConfigs.Local);
                    _scene.SetCar(_car, null, "PLAYER_CAR");
                }
                if ((_scene.CarModel == null || !ReadyAround(_scene.Frame.Translation, 300f)) && _loadT < 20f) return;
                _scene.SetFade(0f, Black);
                if (!_scene.PlayMain("C_SOLAR_CROWN_E")) { Finish(); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && Story.SkipPressed()) _scene.SkipMain();
            UpdateScene(_scene, dt);
            foreach (var n in _scene.TakeEndEvents()) if (n == "C_SOLAR_CROWN_E") { Finish(); return; }
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        void Finish()
        {
            _scene?.Dispose(); _scene = null;
            GameMode.Pop();
            // Tess nimmt den California zurück (Original: Auto 387 mit -1 entfernt) – nur, wenn die Geschichte ihn gegeben hat
            ReturnStoryCar();
            Story.Go(6);
        }

        static void ReturnStoryCar()
        {
            var save = TDU2Session.Save;
            var car = Story.StoryCar;
            if (save == null || car == null || !StoryProgress.GaveStoryCar(Story.Nick)) return;
            save.ownedCars.RemoveAll(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(save.currentCar, car.FileName, StringComparison.OrdinalIgnoreCase)) save.currentCar = save.ownedCars.Count > 0 ? save.ownedCars[0] : "";
            if (save.ownedCars.Count == 0) save.starterChosen = false;   // Startauto beim Gebrauchtwagenhändler (Schritt 7)
            StoryProgress.SetGaveStoryCar(Story.Nick, false);
            if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            SessionAutosave.Schedule(3f);
        }

        public override void Exit() { _scene?.Dispose(); _scene = null; }
    }

    /// <summary>
    /// Schritt 6 "Solar Crown interior" (Spielmodus CINE, scenarics/cinematics/intro/solar_crown_int + interior/ich_ib.bnk am Club
    /// ECH_IA_1243I): C_SOLAR_CROWN_I (Drehort im Club, Tess, Todd, Kamerateam) -> Schritt 7.
    /// </summary>
    internal sealed class SolarCrownInteriorStep : StoryStep
    {
        StoryScene? _scene;
        bool _started, _avatar;
        float _loadT;

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_CINE);
            Suspend();
            _scene = CreateScene(new StoryScene.Setup
            {
                Bank = "scenarics/cinematics/intro/solar_crown_int.bnk",
                Spot = "ECH_IA_1243I",
                Closed = true,
                ClosedAltitude = 0f,
                ExtraBanks = new[] { "interior/ich_ib.bnk" },
                IntroUi = false,
            });
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Story.Go(7); return; }
            if (!_started)
            {
                _loadT += dt;
                _scene.SetFade(1f, Black);
                UpdateScene(_scene, dt);
                if (!_scene.Loaded) return;
                if (!_avatar) { _avatar = true; _scene.SetPlayerAvatar(PlayerAvatarConfig(), AvatarConfigs.Local); }
                if (_loadT < 1.5f) return;   // Figuren laden
                _scene.SetFade(0f, Black);
                if (!_scene.PlayMain("C_SOLAR_CROWN_I")) { Story.Go(7); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && Story.SkipPressed()) _scene.SkipMain();
            UpdateScene(_scene, dt);
            foreach (var n in _scene.TakeEndEvents()) if (n == "C_SOLAR_CROWN_I") { Story.Go(7); return; }
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        public override void Exit()
        {
            _scene?.Dispose(); _scene = null;
            GameMode.Pop();
        }
    }
}