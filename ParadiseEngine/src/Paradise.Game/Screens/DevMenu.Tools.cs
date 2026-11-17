using Paradise.Core.Avatars;
using System.Globalization;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1): Cutscene Tester und Animation Viewer – Seiten wie Unity TDU2DevMenu (CutsceneMenu, CutsceneBankMenu,
/// AnimViewerMenu). Die Werkzeuge selbst: DevMenuTools.cs (Port von Unity TDU2DebugTools: DebugStage, CutsceneTester, AnimViewer).
/// </summary>
public sealed partial class DevMenu
{
    CutsceneTester Tester => CutsceneTester.Get(_r, _terrain, _vehicles);
    AnimViewer Viewer => AnimViewer.Get(_r, _terrain, _vehicles);
    static readonly string[] StageNames3 = { "Sky stage (1500 m up)", "Here (in front of the camera)", "At the bank's spot" };

    /// <summary>Werkzeuge jedes Bild weiterlaufen lassen (auch bei geschlossenem Menü).</summary>
    void UpdateTools(float dt)
    {
        CutsceneTester.Instance?.Update(dt);
        AnimViewer.Instance?.Update(dt);
    }

    void RenderTools()
    {
        CutsceneTester.Instance?.Render();
        AnimViewer.Instance?.Render();
    }

    // ------------------------------------------------------------------ Cutscene Tester (Unity CutsceneMenu)
    List<Item> CutsceneMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("CUTSCENE TESTER") };
        if (m == null || Streamer == null) { l.Add(Info("no vehicle manager / island loaded")); return l; }
        var t = Tester;
        l.Add(Info("Status", () => t.Status));
        l.Add(Info("Bank", () => t.BankPath != null ? DebugStage.Rel(m.BnkDir, t.BankPath) : "-"));
        l.Add(Table("Stage", StageNames3, () => (int)t.Where, i => t.Where = (CutsceneTester.Stage)i, 0));
        l.Add(Bool("Cast player avatar", () => t.CastAvatar, v => { t.CastAvatar = v; if (t.Scene != null) t.Scene.CastPlayer = v; }, true));
        l.Add(Bool("Cast player car (PLAYER_CAR)", () => t.CastCar, v => t.CastCar = v, true));
        l.Add(Bool("Auto-play first cinematic after loading", () => t.AutoPlay, v => t.AutoPlay = v, true));
        l.Add(Bool("Show stage floor plane (Sky)", () => t.ShowFloor, v => t.ShowFloor = v, true));
        l.Add(Menu("Choose bank …", CutsceneBankMenu));
        if (t.Active)
        {
            l.Add(Head("CINEMATICS  > Enter = PLAY · Left/Right = play as background · Delete = stop"));
            var sc = t.Scene;
            if (sc == null || !t.Loaded) l.Add(Info("loading …"));
            else
            {
                var names = new List<string>(sc.Definitions.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var dn0 in names)
                {
                    var dn = dn0; var d = sc.Definitions[dn];
                    bool cam = d.Cin.Camera(d.Scenes!, 0f, out _, out _, out _);
                    string flags = cam ? "" : "no camera ";
                    l.Add(new Item
                    {
                        Label = "> " + dn,
                        Value = () => (t.Scene != null && t.Scene.MainName == dn ? "PLAYING  " : "[Enter] ") + flags + d.Cin.Duration.ToString("0.0", CultureInfo.InvariantCulture) + " s",
                        Activate = () => t.Play(dn, false),
                        Change = _ => t.Play(dn, true),
                        Reset = () => t.StopAll(),
                    });
                }
            }
            l.Add(Head("CONTROL"));
            l.Add(Float("Free view: orbit speed", () => t.ViewSpeed, v => t.ViewSpeed = v, 4f, -90f, 90f, 8f, "0", "°/s"));
            l.Add(Float("Free view: distance", () => t.ViewDistance, v => t.ViewDistance = v, 1f, 2f, 80f, 14f, "0", " m"));
            l.Add(Float("Free view: height", () => t.ViewHeight, v => t.ViewHeight = v, 0.5f, -5f, 40f, 5f, "0.0", " m"));
            l.Add(Action_("Skip main cinematic", () => t.Skip()));
            l.Add(Action_("Stop all", () => t.StopAll()));
            l.Add(Float("Time scale", () => t.TimeScale, v => t.TimeScale = v, 0.25f, 0f, 4f, 1f, "0.00", "x"));
            l.Add(Action_("Reload bank (with current stage)", () => { var b = t.BankPath; if (b != null) t.Load(b); }));
            l.Add(Action_("Close tester (back to the game)", () => { t.TimeScale = 1f; t.Unload(true); Toast("Cutscene tester closed"); }));
        }
        return l;
    }

    List<Item> CutsceneBankMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("BANKS WITH CINEMATICS  (" + DebugStage.ScanState + ")") };
        if (m == null) return l;
        var banks = DebugStage.CinematicBanks(m.BnkDir, Streamer);
        if (banks == null) { l.Add(Info("scanning banks …  (reopen in a moment)")); return l; }
        l.Add(Action_("Rescan", () => { DebugStage.Rescan(); Rebuild(); }));
        string? group = null;
        foreach (var b in banks)
        {
            string rel = DebugStage.Rel(m.BnkDir, b);
            string g = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
            if (g != group) { group = g; l.Add(Head(g.ToUpperInvariant())); }
            string path = b;
            l.Add(Action_(Path.GetFileNameWithoutExtension(b), () =>
            {
                var t = Tester;
                t.Load(path);
                Toast("Loading " + rel + " – " + CutsceneTester.StageName(t.Where));
                if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1);
            }));
        }
        return l;
    }

    // ------------------------------------------------------------------ Animation Viewer (Unity AnimViewerMenu)
    List<string>? _pnjIds;

    List<Item> AnimViewerMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("ANIMATION VIEWER") };
        if (m == null || Streamer == null) { l.Add(Info("no vehicle manager / island loaded")); return l; }
        if (!AvatarLibrary.Ready) { l.Add(Info("avatar data not loaded yet")); return l; }
        var v = Viewer;
        if (!v.Active) { l.Add(Action_("Open viewer (sky stage)", () => { v.Open(); Toast("Animation viewer open"); Rebuild(); })); return l; }
        if (_pnjIds == null) { _pnjIds = AvatarLibrary.PnjIds(); _pnjIds.Insert(0, ""); }
        var ids = _pnjIds;
        l.Add(new Item
        {
            Label = "Model  (Left/Right · Shift x10)",
            Value = () => string.IsNullOrEmpty(v.Model) ? "Player avatar" : v.Model + "  (" + (AvatarLibrary.PnjName(v.Model) ?? "") + ")",
            Change = d => { int i = ids.IndexOf(v.Model ?? ""); int n = ids.Count; i = ((i + d) % n + n) % n; v.SetModel(ids[i]); },
            Reset = () => v.SetModel(""),
        });
        l.Add(Menu("Choose model …", () =>
        {
            var ml = new List<Item> { Head("MODELS (DBPNJ)") };
            foreach (var id in ids)
            {
                string i2 = id;
                ml.Add(Action_(string.IsNullOrEmpty(i2) ? "Player avatar" : i2 + "  " + (AvatarLibrary.PnjName(i2) ?? ""), () => { v.SetModel(i2); if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); }));
            }
            return ml;
        }));
        l.Add(Info("Clip source", () => string.IsNullOrEmpty(v.ClipBank) ? "scenarics/avatars/avatarsdriver.bnk" : DebugStage.Rel(m.BnkDir, v.ClipBank)));
        l.Add(Menu("Choose clip source …", () =>
        {
            var bl = new List<Item> { Head("BANKS WITH ANIMATIONS  (" + DebugStage.ScanState + ")") };
            bl.Add(Action_("avatarsdriver.bnk (driver/walk clips)", () => { v.SetBank(""); if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); }));
            var banks = DebugStage.AnimationBanks(m.BnkDir, Streamer);
            if (banks == null) { bl.Add(Info("scanning banks …  (reopen in a moment)")); return bl; }
            foreach (var b in banks) { string p = b; bl.Add(Action_(DebugStage.Rel(m.BnkDir, b), () => { v.SetBank(p); if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); })); }
            return bl;
        }));
        l.Add(new Item
        {
            Label = "Clip  (Left/Right previous/next)",
            Value = () => string.IsNullOrEmpty(v.ClipName) ? "rest pose" : v.ClipName,
            Change = d => v.Step(Math.Sign(d)),
            Reset = () => v.SetClip(""),
        });
        l.Add(Menu("Choose clip …", () =>
        {
            var cl = new List<Item> { Head("CLIPS") };
            foreach (var c in v.Clips()) { string c2 = c; cl.Add(new Item { Label = c2, Value = () => v.ClipName == c2 ? ">" : "", Activate = () => v.SetClip(c2) }); }
            if (cl.Count == 1) cl.Add(Info("no clips in this bank"));
            return cl;
        }));
        l.Add(Info("Time", () => v.Clip != null ? v.Time_.ToString("0.00", CultureInfo.InvariantCulture) + " / " + v.Clip.Length.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "-"));
        l.Add(Bool("Paused", () => v.Paused, x => v.Paused = x, false));
        l.Add(Bool("Loop", () => v.Loop, x => v.Loop = x, true));
        l.Add(Bool("Show floor plane", () => v.ShowFloor, x => v.ShowFloor = x, true));
        l.Add(Float("Speed", () => v.Speed, x => v.Speed = x, 0.1f, -2f, 4f, 1f, "0.0", "x"));
        l.Add(Float("Scrub (time)", () => v.Time_, x => { v.Time_ = x; v.Paused = true; }, 0.05f, 0f, 600f, 0f, "0.00", " s"));
        l.Add(Head("CAMERA"));
        l.Add(Float("Orbit angle", () => v.Orbit, x => v.Orbit = x, 10f, -3600f, 3600f, 30f, "0", "°"));
        l.Add(Float("Orbit speed", () => v.OrbitSpeed, x => v.OrbitSpeed = x, 5f, -180f, 180f, 0f, "0", "°/s"));
        l.Add(Float("Distance", () => v.Distance, x => v.Distance = x, 0.2f, 0.5f, 20f, 3.2f, "0.0", " m"));
        l.Add(Float("Height", () => v.Height, x => v.Height = x, 0.1f, -1f, 3f, 1.1f, "0.0", " m"));
        l.Add(Action_("Close viewer (back to the game)", () => { v.Close(); Toast("Animation viewer closed"); Rebuild(); }));
        return l;
    }
}
