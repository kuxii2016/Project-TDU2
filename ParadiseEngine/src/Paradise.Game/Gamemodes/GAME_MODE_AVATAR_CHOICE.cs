// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: AvatarChoiceMode).
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// GAME_MODE_AVATAR_CHOICE (OpenTDU2 GMAvatarChoice): Story-Schritt 0 "Avatar choice / First WalkMode". Der Ablauf läuft in
/// <see cref="AvatarChoiceStep"/> (jedes Bild über <see cref="Story"/>); der Modus selbst sperrt Pausemenü/Karte und HUD.
/// </summary>
public sealed class GAME_MODE_AVATAR_CHOICE : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_AVATAR_CHOICE;

    /// <summary>
    /// Schritt 0: Party im Haus EPH_L3DI_5067I (new_intro.bnk). C_INTRO ("The Dream", Credits), Auswahl "Select your character" (C_AV_xx
    /// über der Party C_AVATAR in Zeitlupe, Wechsel C_xTOy, Tasten links/rechts/Enter an intro.vmf), C_FIESTA_INTRO_xx, Laufmodus bis zu
    /// Tess (Volumen "Tess" -> C_TESS) und zum Garagentor (FSCommand "gain" -> Schritt 1).
    /// </summary>
    internal sealed class AvatarChoiceStep : StoryStep
    {
        enum St { Loading, Intro, Choice, Fiesta, Walk, Tess, Leaving }
        St _st;
        StoryScene? _scene;
        readonly StoryWalk _walk = new();
        readonly Queue<string> _cmds = new();
        int _index = -1, _selected = -1;
        bool _tessStarted;
        float _skipTimer;
        float _loadT;

        static readonly string[] Configs = { "AVT_Male_Intro", "AVT_Female_Intro", "AVT_Male_Afrik_Intro", "AVT_Female_Afrik_Intro", "AVT_Male_Asia_Intro", "AVT_Female_Asia_Intro" };

        public override void Enter()
        {
            GameMode.Push(Gamemode.GAME_MODE_AVATAR_CHOICE);
            Suspend();
            _scene = CreateScene(new StoryScene.Setup { Bank = "scenarics/cinematics/intro/new_intro.bnk", Spot = "EPH_L3DI_5067I", HideSpotInterior = true });
            _scene.Command += (c, a) => _cmds.Enqueue(c);
            _st = St.Loading; _loadT = 0f;
            // Streaming beginnt am Haus
            var s = Story.FindSpot("EPH_L3DI_5067I");
            if (s != null) Story.HoldCameraAt(s.Position + Vector3.UnitY * 2f, Vector3.UnitZ);
        }

        public override void Tick(float dt)
        {
            if (_scene == null) return;
            if (_scene.Failed) { Story.Abort(); return; }
            if (_st == St.Loading)
            {
                _loadT += dt;
                _scene.SetFade(1f, new Vector4(0f, 0f, 0f, 1f));
                UpdateScene(_scene, dt);
                if (!_scene.Loaded) return;
                // Welt um das Haus fertig (sonst sieht man das Nachladen), spätestens nach 20 s
                if (!ReadyAround(_scene.Frame.Translation, 300f) && _loadT < 20f) return;
                _scene.SetFade(0f, new Vector4(0f, 0f, 0f, 1f));
                _scene.PlayMain("C_INTRO");
                _st = St.Intro; _skipTimer = 0f;
                return;
            }
            var ui = _scene.Ui;
            if (_st == St.Choice && ui != null)
            {
                if (TDU2Nav.Left() || TDU2Controls.RawKeyPressed(0x41)) ui.SendKey(1);
                if (TDU2Nav.Right() || TDU2Controls.RawKeyPressed(0x44)) ui.SendKey(2);
                if (TDU2Controls.ConfirmPressed()) ui.SendKey(13);
            }
            // C_INTRO überspringen: ab 25 s (FUN_00da7e70)
            if (_scene.MainName == "C_INTRO" && _skipTimer >= 0f)
            {
                _skipTimer = _scene.MainTime;
                if (_skipTimer >= 25f && Story.AllowSkip && Story.SkipPressed()) { _scene.SkipMain(); _skipTimer = -1f; }
            }
            UpdateScene(_scene, dt);
            foreach (var n in _scene.TakeEndEvents()) OnEnd(n);
            while (_cmds.Count > 0) OnCommand(_cmds.Dequeue());
            if (_scene == null) return;
            if (_index >= 0 && ui != null && _scene.MainName.Length > 0)
                ui.SetValue("/:IsTransition", _scene.MainName.IndexOf("C_AV_", StringComparison.Ordinal) >= 0 ? 0 : 1);
            if (_st == St.Walk) UpdateWalk(dt);
        }

        public override void Render(Matrix4x4 gameToRender, Vector3 cameraRender) => _scene?.Render(gameToRender);

        void OnEnd(string name)
        {
            var ui = _scene!.Ui;
            if (name == "C_INTRO")
            {
                _scene.PlayMain("C_AV_01");
                _index = 0;
                _scene.PlayBackground("C_AVATAR");
                ui?.GotoLabel("/", "avatar", true);
                _st = St.Choice;
                return;
            }
            if (name.StartsWith("C_FIESTA_INTRO", StringComparison.Ordinal)) { StartWalk(); return; }
            if (name == "C_TESS")
            {
                ui?.SetVisible("/cursor", true);
                _scene.StopMain();
                _walk.Resume();
                _scene.ShowGoal(StoryScene.Loc(5108));   // "In die Garage gehen"
                _st = St.Walk;
                return;
            }
            if (_index >= 0 && name.Length > 5 && name.StartsWith("C_", StringComparison.Ordinal) && name.Contains("TO"))
            {
                // C_xTOy -> Schleife C_AV_0y
                var parts = name.Substring(2).Split(new[] { "TO" }, StringSplitOptions.None);
                if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to) && from == _index + 1)
                {
                    _index = to - 1;
                    _scene.PlayMain("C_AV_" + to.ToString("00"));
                }
            }
        }

        void OnCommand(string c)
        {
            switch (c)
            {
                case "nxav":
                    if (_index < 0 || _index > 4) return;
                    _scene!.PlayMain($"C_{_index + 1}TO{(_index + 1) % 6 + 1}");
                    break;
                case "prav":
                    if (_index < 1) return;
                    _scene!.PlayMain($"C_{_index + 1}TO{_index}");
                    break;
                case "crav":
                    _scene!.PlayEmitter("S_TimeStart");
                    _selected = _index;
                    Validate();
                    _index = -1;
                    break;
                case "skip": _skipTimer = -1f; break;
                case "gain":
                    // Garage gewählt -> Schritt 1 "First Garage"
                    _st = St.Leaving;
                    Story.Go(1);
                    break;
            }
        }

        void Validate()
        {
            var scene = _scene!;
            scene.StopBackground();
            scene.PlayBackground("C_WALKMODE");
            var ui = scene.Ui;
            string sex = ui?.GetVariable("/:WhatSex") ?? "male";
            string eth = ui?.GetVariable("/:WhatEthnicGrp") ?? "caucasian";
            bool woman = sex.StartsWith("f", StringComparison.OrdinalIgnoreCase);
            string suffix = eth.Length > 1 && eth[1] == 'a' ? "" : eth.Length > 1 && eth[1] == 's' ? "_Asia" : "_Afrik";
            string cfg = "AVT_" + (woman ? "Female" : "Male") + suffix + "_Intro";
            if (Array.IndexOf(Configs, cfg) < 0) cfg = Configs[System.Math.Clamp(_selected, 0, 5)];
            Log.Info($"Avatar {_selected + 1} gewählt ({sex}, {eth}) -> {cfg}");
            scene.SetPlayerAvatar(cfg);
            StoryProgress.SetIntroAvatar(Story.Nick, cfg);
            var pc = AvatarLibrary.PnjConfig(cfg);
            if (pc != null) AvatarConfigs.SetLocal(pc);
            ui?.SetVariable("/:PlayerName", TDU2Session.Nick ?? "");
            scene.PlayMain($"C_FIESTA_INTRO_{_selected + 1:00}");
            _st = St.Fiesta;
        }

        void StartWalk()
        {
            var scene = _scene!;
            scene.StopMain();
            _walk.Start(scene, $"cam_entree_{System.Math.Max(_selected, 0) + 1:00}", 60f);
            scene.Ui?.GotoLabel("/", "wm_home", true);
            scene.ShowGoal(StoryScene.Loc(5033));   // "Mit Tess reden"
            _st = St.Walk;
            var t = Story.Terrain;
            if (t != null) t.Input.CursorLocked = true;
        }

        void UpdateWalk(float dt)
        {
            var scene = _scene!;
            _walk.Update(dt);
            if (!_tessStarted)
            {
                var tess = scene.FindVolume("Tess");
                if (tess != null && tess.Contains(_walk.EyeLocal - Vector3.UnitY * 0.8f))
                {
                    scene.CloseGoal();
                    _walk.Clear();
                    _walk.Pause();
                    scene.PlayMain("C_TESS");
                    _tessStarted = true;
                    scene.Ui?.SetVisible("/cursor", false);
                    _st = St.Tess;
                    return;
                }
            }
            if (Story.ValidatePressed() && _walk.Selected >= 0)
            {
                var ui = scene.Ui;
                if (ui == null || !ui.Call("/cursor", "A")) ui?.Call("/", "ItemType", "valid");
            }
        }

        public override void Exit()
        {
            _walk.Stop();
            _scene?.Dispose(); _scene = null;
            Story.RestoreCamera();
        }
    }
}
