// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: FirstGarageMode).
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// GAME_MODE_FIRST_WM (OpenTDU2 GMFirstWalkMode): Story-Schritt 1 "First Garage". Der Ablauf läuft in <see cref="FirstGarageStep"/>
/// (jedes Bild über <see cref="Story"/>); der Modus selbst sperrt Pausemenü/Karte und HUD.
/// </summary>
public sealed class GAME_MODE_FIRST_WM : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_FIRST_WM;

    /// <summary>
    /// Schritt 1: Garage (garage.bnk, geschlossener Raum) mit Tess (C_LIFE_TESS) und dem California (Auto 387) an D_CAR. C_INTRO,
    /// Laufmodus, Fahrertür mit der Zubehör-Taste öffnen (FSCommand opdo / Taste), Auto wählen -> C_ENTER_CAR, Cockpit, Gas geben ->
    /// C_OUT, C_OUTRO -> Schritt 2.
    /// </summary>
    internal sealed class FirstGarageStep : StoryStep
    {
        enum St { Loading, Intro, Walk, EnterCar, Seated, Out, Outro, Done }
        St _st;
        StoryScene? _scene;
        readonly StoryWalk _walk = new();
        readonly Queue<string> _cmds = new();
        CarAccessories? _acc;
        bool _carRequested;
        int _carSelectable = -1;
        float _doorT = -1f;      // seit dem Öffnen der Tür (s), -1 = zu
        const float DoorDuration = 2f;
        bool _entered;
        float _revTimer;
        float _tutoT; int _tuto = -1;
        float _loadT;
        // Kommentare von Tess, solange kein Kino läuft (FUN_009a9990 -> FUN_009a9920): 3 im Auto, 1/2 mit Ziel (2 = Tür offen), sonst 0
        static readonly string[] Voices = { "C_VOICE_03", "C_VOICE_04", "C_VOICE_05", "C_VOICE_06" };
        readonly bool[] _voiceDone = new bool[4];
        bool _voicePlaying;
        // Ausdehnung des Autos im Modellraum (Unity: Renderer-Bounds für AimingAtCar)
        Vector3 _carMin, _carMax; bool _carBounds;

        bool DoorOpen => _doorT >= DoorDuration;
        static readonly Vector4 Black = new(0f, 0f, 0f, 1f);

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_FIRST_WM);
            Suspend();
            _scene = CreateScene(new StoryScene.Setup { Bank = "scenarics/cinematics/intro/garage.bnk", Spot = "EPH_L3DI_5067I", Closed = true });
            _scene.Command += (c, a) => _cmds.Enqueue(c);
            _st = St.Loading;
        }

        public override void Tick(float dt)
        {
            if (_scene == null) return;
            if (_scene.Failed) { Story.Abort(); return; }
            if (_st == St.Loading)
            {
                _loadT += dt;
                _scene.SetFade(1f, Black);
                UpdateScene(_scene, dt);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig());
                    for (int i = 0; i < _scene.Selectables.Count; i++) if (_scene.Selectables[i].Str == "car") _carSelectable = i;
                    _walk.Filter = i => i != _carSelectable || DoorOpen;
                    _scene.SetCar(Story.StoryCar, "D_CAR", "CIN_CAR");
                }
                if (_scene.CarModel == null && _loadT < 15f) return;
                if (_scene.CarModel != null) { _acc = CarAccessories.For(_scene.CarModel); MeasureCar(_scene.CarModel); }
                _scene.SetFade(0f, Black);
                _scene.PlayBackground("C_LIFE_SOUND", true);
                _scene.PlayMain("C_INTRO");
                _st = St.Intro;
                return;
            }
            if (_st == St.Intro && Story.AllowSkip && Story.SkipPressed()) _scene.SkipMain();
            UpdateScene(_scene, dt);
            foreach (var n in _scene.TakeEndEvents()) OnEnd(n);
            while (_cmds.Count > 0) OnCommand(_cmds.Dequeue());
            if (_scene == null) return;
            if (_doorT >= 0f) _doorT += dt;
            _acc?.Update(dt);
            if (_st == St.Walk) UpdateWalk(dt);
            else if (_st == St.Seated) UpdateSeated(dt);
            UpdateTutorials(dt);
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        void OnEnd(string name)
        {
            var scene = _scene!;
            switch (name)
            {
                case "C_INTRO":
                    scene.StopMain();
                    scene.PlayBackground("C_LIFE_TESS", true);
                    _walk.Start(scene, "cam_entree", 50f);
                    scene.Ui?.GotoLabel("/", "wm_garage", true);
                    scene.ShowGoal(StoryScene.Loc(5032));   // "Steig in das Auto" (TUTO_ENTER_IN_THE _CAR)
                    _st = St.Walk;
                    if (Story.Terrain != null) Story.Terrain.Input.CursorLocked = true;
                    break;
                case "C_ENTER_CAR":
                    scene.StopMain();
                    _acc?.SetDoor(true, false);
                    _doorT = -1f;
                    _entered = true;
                    _st = St.Seated;
                    break;
                case "C_OUT":
                    scene.PlayMain("C_OUTRO");
                    _st = St.Outro;
                    break;
                case "C_OUTRO":
                    _st = St.Done;
                    Story.Go(2);
                    break;
                default:
                    for (int i = 0; i < Voices.Length; i++)
                        if (name == Voices[i]) { _voiceDone[i] = true; _voicePlaying = false; }
                    break;
            }
        }

        void UpdateVoices()
        {
            int index = 0;
            if (_entered) index = 3;
            else if (_walk.Selected >= 0 || AimingAtCar()) index = _doorT >= 0f ? 2 : 1;
            if (!_voiceDone[index] && !_voicePlaying && _scene!.PlayBackground(Voices[index], false)) _voicePlaying = true;
        }

        void OnCommand(string c)
        {
            if (c == "opdo" && !_entered) OpenDoor();
        }

        void OpenDoor()
        {
            if (_doorT >= 0f) return;
            _acc?.SetDoor(true, true);
            _doorT = 0f;
        }

        /// <summary>Ausdehnung aller Teile des Autos im Modellraum (für den Blick aufs Auto).</summary>
        void MeasureCar(CarModel m)
        {
            _carMin = new Vector3(float.MaxValue); _carMax = new Vector3(float.MinValue);
            foreach (var n in m.Nodes.Values)
            {
                if (!n.HasMesh) continue;
                var mm = ModelMatrix(n);
                for (int k = 0; k < 8; k++)
                {
                    var c = new Vector3((k & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (k & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (k & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    var p = Vector3.Transform(c, mm);
                    _carMin = Vector3.Min(_carMin, p); _carMax = Vector3.Max(_carMax, p);
                }
            }
            _carBounds = _carMin.X <= _carMax.X && _carMax.Y - _carMin.Y < 4f;
        }

        /// <summary>Lage eines Knotens im Modellraum (Kette der lokalen Matrizen bis zur Wurzel).</summary>
        static Matrix4x4 ModelMatrix(CarNode n)
        {
            var m = n.Local;
            for (var p = n.Parent; p != null; p = p.Parent) m *= p.Local;
            return m;
        }

        bool AimingAtCar()
        {
            if (!_carBounds || _scene?.CarPoseGame is not Matrix4x4 pose || !Matrix4x4.Invert(pose, out var inv)) return false;
            var o = Vector3.Transform(_walk.EyeWorld, inv);
            var d = Vector3.TransformNormal(_walk.ForwardWorld, inv);
            float t0 = 0f, t1 = 50f;
            for (int i = 0; i < 3; i++)
            {
                float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float mn = i == 0 ? _carMin.X : i == 1 ? _carMin.Y : _carMin.Z, mx = i == 0 ? _carMax.X : i == 1 ? _carMax.Y : _carMax.Z;
                if (MathF.Abs(di) < 1e-8f) { if (oi < mn || oi > mx) return false; continue; }
                float a = (mn - oi) / di, b = (mx - oi) / di;
                if (a > b) (a, b) = (b, a);
                t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
                if (t0 > t1) return false;
            }
            return true;
        }

        static bool AccessoryLeftPressed()
        {
            const int VK_4 = 0x34, VK_NUMPAD4 = 0x64;
            if (TDU2Controls.RawKeyPressed(VK_NUMPAD4) || TDU2Controls.RawKeyPressed(VK_4)) return true;
            return Story.ActionPressed("doors");
        }

        void UpdateWalk(float dt)
        {
            var scene = _scene!;
            _walk.Update(dt);
            UpdateVoices();
            bool aiming = AimingAtCar();
            if (aiming && _doorT < 0f && AccessoryLeftPressed()) OpenDoor();
            if (!_entered && _walk.SelectedType == "car" && DoorOpen && Story.ValidatePressed())
            {
                scene.Ui?.SetVisible("/cursor", false);
                _walk.Clear();
                scene.CloseGoal();
                _walk.Stop();
                if (Story.Terrain != null) Story.Terrain.Input.CursorLocked = false;
                scene.PlayMain("C_ENTER_CAR");
                _st = St.EnterCar;
            }
        }

        void UpdateSeated(float dt)
        {
            var scene = _scene!;
            // Cockpit-Kamera: über dem Fahrersitz, Blick nach vorne (camerasingame.xmb Modus 23 ~ (0.37, 1.01, -0.36) -> (0.37, 0.9, 1.35))
            var model = scene.CarModel;
            if (model != null && scene.CarPoseGame is Matrix4x4 pose)
            {
                var seat = model.Find("DVRSEAT_HR") ?? model.Find("DVRSEAT_LR");
                var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, pose));
                var fwdCar = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, pose));
                Vector3 eye = seat != null ? Vector3.Transform(ModelMatrix(seat).Translation, pose) + up * 0.75f
                                           : Vector3.Transform(new Vector3(0.37f, 1.01f, -0.36f), pose);
                var fwd = Vector3.Normalize(fwdCar * 1.71f - up * 0.11f);
                scene.CameraOverride = true;
                scene.SetCamera(eye, fwd, up, 55f);
            }
            UpdateVoices();
            // Gas > 0.7 -> 3 s, Entity__Fx C_OUTRO (Motor) wird gestartet
            if (_revTimer == 0f && Story.ActionHeld("accel")) { _revTimer = 3f; scene.PlayEmitter("C_OUTRO"); }
            if (_revTimer > 0f)
            {
                _revTimer -= dt;
                if (_revTimer <= 0f)
                {
                    _revTimer = 0f;
                    scene.CameraOverride = false;
                    scene.PlayMain("C_OUT");
                    _st = St.Out;
                }
            }
        }

        // DBTutoMess "opdo" (5036, nach 2 s, bis die Tür aufgeht) und "stca" (5035, im Auto bis Gas gegeben wird)
        void UpdateTutorials(float dt)
        {
            var scene = _scene!;
            int want = -1;
            if (_st == St.Walk && !_entered && _doorT < 0f && scene.MainName.Length == 0) want = 0;
            else if (_st == St.Seated && _revTimer == 0f) want = 1;
            if (_tuto >= 0 && want != _tuto) { scene.CloseTutorial(); _tuto = -1; _tutoT = 0f; }
            if (want >= 0 && _tuto < 0)
            {
                _tutoT += dt;
                if (_tutoT > 2f) { scene.ShowTutorial(StoryScene.Loc(want == 0 ? 5036 : 5035)); _tuto = want; }
            }
            else if (want < 0) _tutoT = 0f;
        }

        public override void Exit()
        {
            _walk.Stop();
            if (_scene?.CarModel != null) CarAccessories.Forget(_scene.CarModel);
            _acc = null;
            _scene?.Dispose(); _scene = null;
            Story.RestoreCamera();
        }
    }
}
