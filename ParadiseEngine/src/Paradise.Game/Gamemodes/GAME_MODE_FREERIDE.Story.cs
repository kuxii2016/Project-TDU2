// Story-Schritt portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: TutoFrogMode).
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using System.Globalization;

namespace Paradise.Game.Gamemodes
{
    public partial class GAME_MODE_FREERIDE
    {
        /// <summary>
        /// Schritt 8 "Tuto FROG" (Freifahrt; pack_freerideFROG, DBTutoMess "frog", Ziel 4034 "Mit dem F.R.I.M. in das 1. Level Geld holen."):
        /// zuerst wie im Original die Wahl der Fahrhilfen (general.vmf /driving_assistance: Open(), Auswahlbox "tuto:assi" mit FSCommands
        /// tuto:assi:scne/scpr = nächste/vorige, tuto:assi:vali = bestätigt; 0 Sicher, 1 Sport, 2 Hypersport). Die Wahl gilt wie die
        /// Option "DrivingHelp" des Optionsmenüs (Spielstand). Das F.R.I.M.-Minispiel selbst ist noch nicht umgesetzt -> Schritt 9.
        /// </summary>
        internal sealed class TutoFrogStep : StoryStep
        {
            public override bool BlocksInput => true;
            public override bool MutesRadio => false;
            StoryScene? _ui;
            VmfPlayer? _g;
            bool _opened, _initSeen, _done, _wasSuspended, _cursorWas;
            int _idx = 1;   // Original: DrivingAssistance = txt1 (Sport)
            float _t, _openT;

            public override void Enter()
            {
                _wasSuspended = Story.Vehicles?.Suspended ?? false;
                _ui = CreateScene(new StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
            }

            public override void Tick(float dt)
            {
                _t += dt;
                _ui?.Update(dt);
                if (_done) return;
                if (_ui == null || _ui.Failed) { Finish("keine Oberfläche"); return; }
                if (!_ui.Loaded) { if (_t > 15f) Finish("Oberfläche lädt nicht"); return; }
                if (_g == null)
                {
                    _g = _ui.General;
                    if (_g == null || _g.Find("/driving_assistance") == null) { Finish("general.vmf ohne /driving_assistance"); return; }
                    _g.FSCommand += OnCommand;
                }
                if (!_opened)
                {
                    if (Manager.Current == null || _t < 1f) return;   // erst, wenn das gekaufte Auto da ist
                    _opened = true; _openT = _t;
                    if (Story.Vehicles != null) Story.Vehicles.Suspended = true;
                    _g.SetVisible("/driving_assistance", true);
                    _g.SetValue("/:Input", true);
                    if (!_g.Call("/driving_assistance", "Open")) { Finish("Open() fehlt"); return; }
                    var t = Story.Terrain;
                    if (t != null) { _cursorWas = t.Input.CursorLocked; t.Input.CursorLocked = false; }
                    _g.MouseEnabled = true;
                    Log.Debug("STORY Fahrhilfen-Auswahl geöffnet (general.vmf /driving_assistance)");
                    return;
                }
                // Rückfall: Bild 9 ruft initParameter() – kam nach 1 s noch kein tuto:assi:open, selbst aufrufen
                if (!_initSeen && _t - _openT > 1f) { _initSeen = true; _g.Call("/driving_assistance", "initParameter"); }
                const int VK_A = 0x41, VK_D = 0x44, VK_S = 0x53, VK_W = 0x57;
                if (TDU2Nav.Left() || TDU2Controls.RawKeyPressed(VK_A) || TDU2Nav.Up() || TDU2Controls.RawKeyPressed(VK_W)) Step(-1);
                if (TDU2Nav.Right() || TDU2Controls.RawKeyPressed(VK_D) || TDU2Nav.Down() || TDU2Controls.RawKeyPressed(VK_S)) Step(1);
                if (TDU2Controls.ConfirmPressed())
                {
                    if (!_g.Input("A")) Choose();
                }
            }

            /// <summary>Auswahl um eins weiter (wie die Pfeile der Auswahlbox, die das Spiel mit scne/scpr beantwortet).</summary>
            void Step(int dir)
            {
                _idx = (_idx + dir + 3) % 3;
                Apply();
            }

            void Apply()
            {
                if (_g == null) return;
                string? txt = _g.GetVariable("/driving_assistance:txt" + _idx);
                if (txt != null) _g.SetVariable("/:DrivingAssistance", txt);
                _g.Call("/driving_assistance", "updateText", _idx);
            }

            void OnCommand(string c, string a)
            {
                string cmd = string.IsNullOrEmpty(a) ? c : c + ":" + a;
                Log.Debug("STORY Fahrhilfen FSCommand " + cmd);
                if (cmd.StartsWith("tuto:assi:open", StringComparison.Ordinal)) { _initSeen = true; Apply(); }
                else if (cmd.EndsWith(":scne", StringComparison.Ordinal)) Step(1);
                else if (cmd.EndsWith(":scpr", StringComparison.Ordinal)) Step(-1);
                else if (cmd.StartsWith("tuto:assi:vali", StringComparison.Ordinal)) Choose();
            }

            void Choose()
            {
                if (_done) return;
                var aids = _idx == 0 ? DrivingAids.Secure : _idx == 2 ? DrivingAids.HyperSport : DrivingAids.Sport;
                Manager.Aids = aids;
                if (Manager.Current != null) Manager.Current.Aids = aids;
                SaveAids(aids);
                _g?.Call("/driving_assistance", "Close");
                Finish("gewählt: " + aids);
            }

            /// <summary>Wahl wie die Option "DrivingHelp" (Optionsmenü) in den Spielstand (Unity PlayerPrefs "TDU2.Aids").</summary>
            static void SaveAids(DrivingAids aids)
            {
                var o = Options.Instance;
                var e = o?.Get("DrivingHelp");
                if (o != null && e != null) { e.Value = (int)aids; Options.Saved = o.Export(); }
                else
                {
                    var parts = new List<string>((Options.Saved ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
                    parts.RemoveAll(p => p.StartsWith("DrivingHelp=", StringComparison.Ordinal));
                    parts.Add("DrivingHelp=" + ((int)aids).ToString(CultureInfo.InvariantCulture));
                    Options.Saved = string.Join(";", parts);
                }
                if (Paradise.Core.Online.TDU2Session.Save != null) Paradise.Core.Online.TDU2Session.Save.options = Options.Saved ?? "";
                SessionAutosave.Schedule(1f);
            }

            void Finish(string why)
            {
                if (_done) return;
                _done = true;
                Log.Debug("STORY Fahrhilfen-Auswahl: " + why + " – weiter mit Go Home (F.R.I.M. noch nicht umgesetzt)");
                Story.Go(9);
            }

            public override void Exit()
            {
                if (_g != null) _g.FSCommand -= OnCommand;
                _g = null;
                _ui?.Dispose(); _ui = null;
                if (_opened)
                {
                    if (Story.Vehicles != null) Story.Vehicles.Suspended = _wasSuspended;
                    var t = Story.Terrain;
                    if (t != null) t.Input.CursorLocked = _cursorWas;
                }
            }
        }
    }
}
