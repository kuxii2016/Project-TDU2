using Paradise.Core.Racing;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Racing;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1) > Race Debug / Start Race (.ig2) – Port von Unity TDU2DevMenu.RaceMenu/RaceList/AllRaceList mit
/// <see cref="RaceDebugView"/> (Katalog aller .ig2, Details, Start über die Rennschule, Teleport, Kamera, Straßenroute, 3D-Ansicht).
/// </summary>
public sealed partial class DevMenu
{
    static RaceDebugView RaceView => RaceDebugView.Instance;
    double _raceCatalogRefreshAt;

    /// <summary>Katalog bereitstellen (billig: Namen + Lizenz; die .ig2 selbst wird erst bei der Auswahl gelesen).</summary>
    RaceDebugView ReadyRaceView()
    {
        var v = RaceView;
        if (!v.Loaded && Paradise.Core.Config.GameData.BnkDir != null && _time >= _raceCatalogRefreshAt)
        {
            _raceCatalogRefreshAt = _time + 2.0;
            v.Refresh();
        }
        return v;
    }

    /// <summary>Jedes Bild: 3D-Ansicht der gewählten Strecke (auch bei geschlossenem Menü).</summary>
    void CollectRaceDebug() => RaceView.Collect(_terrain);

    List<Item> RaceMenu()
    {
        var v = ReadyRaceView();
        var school = GAME_MODE_SCHOOL.Instance;
        var l = new List<Item>
        {
            Head("RACE CATALOG"),
            Info("Status", () => v.Status),
            Info("Selected", () => v.Selected != null ? v.Selected.Title : "none"),
            Info("Group", () => v.Selected != null ? v.SelectedGroupName : "-"),
            Info("Details", () =>
            {
                var d = v.Selected;
                if (d == null) return "-";
                return d.Checkpoints.Count + " CP · " + d.Decors.Count + " decor · " + d.Opponents.Count + " cars · " +
                       d.Length.ToString("0") + " m · " + (d.IsCircuit ? "circuit" : "point-to-point") + " · " + d.Laps + " lap(s)";
            }),
            Info("Times", () =>
            {
                var d = v.Selected;
                if (d == null) return "-";
                return "target " + (d.TargetTimeMs > 0 ? RaceHud.FormatRaceTimeMs(d.TargetTimeMs) : "-") +
                       (d.HasTimeLimit ? " · limit " + RaceHud.FormatRaceTimeMs(d.TimeLimitMs) : "") +
                       " · car #" + d.CarId;
            }),
            Info("Start (world)", () => v.StartWorldText()),
            Info("Road route", () => v.Selected?.RoadPath != null ? v.Selected.RoadPath.Count + " points" : "checkpoint line"),
            Info("Running", () => school.Race.State + (school.Race.Race != null ? " · " + school.Race.Race.Title + " · " +
                 RaceHud.FormatRaceTime(school.Race.ElapsedSeconds) + " · CP " + school.Race.PassedCount + "/" + school.Race.TargetCount +
                 (school.Race.PenaltyMs > 0 ? " · +" + RaceHud.FormatRaceTimeMs(school.Race.PenaltyMs) : "") : "")),

            Head("ACTIONS"),
            Action_("Start selected race", () =>
            {
                string msg = v.StartRace();
                Toast(msg);
                if (msg.StartsWith("starting", StringComparison.OrdinalIgnoreCase)) Close();
            }),
            Action_("Teleport player car to race start", () => Toast(v.TeleportCarToStart(_terrain, _vehicles))),
            Action_("Move camera to race start (free camera)", () => Toast(v.MoveCameraToStart(_terrain, true))),
            Action_("Compute route along the roads", () => Toast(v.ComputeRoute(_vehicles))),
            Action_("Abort running race", () => { if (school.InTest) { school.Race.Abort(); Toast("Race aborted"); } else Toast("No race running"); }),
            Action_("Select next race in this group", () => { if (!v.CycleGroup(1)) Toast("No race selected / group empty"); }),
            Action_("Select previous race in this group", () => { if (!v.CycleGroup(-1)) Toast("No race selected / group empty"); }),
            Action_("Refresh race catalog", () => { v.Refresh(); Toast("Race catalog refreshed"); }),

            Head("DISPLAY"),
            Bool("Show race debug in the 3D world", () => v.ShowInWorld, x => v.ShowInWorld = x, true),
            Bool("Runtime overlay + labels", () => v.ShowOverlay, x => v.ShowOverlay = x, true),
            Bool("Route", () => v.ShowRoute, x => v.ShowRoute = x, true),
            Bool("Follow roads (RaceRoadPath)", () => v.ShowRoadRoute, x => v.ShowRoadRoute = x, true),
            Bool("Gates / checkpoints", () => v.ShowGates, x => v.ShowGates = x, true),
            Bool("Cones / barriers", () => v.ShowDecor, x => v.ShowDecor = x, true),
            Bool("Opponent cars", () => v.ShowOpponents, x => v.ShowOpponents = x, true),
            Bool("World labels", () => v.ShowLabels, x => v.ShowLabels = x, true),
            Float("View distance", () => v.ViewDistance, x => v.ViewDistance = x, 25f, 100f, 5000f, 900f, "0", " m"),
            Float("Label distance", () => v.LabelDistance, x => v.LabelDistance = x, 10f, 30f, 1500f, 250f, "0", " m"),

            Head("RACE / SCHOOL SETTINGS (Game/Config/RaceConfig.cs)"),
            Bool("Gates (pack_cp.bnk)", () => RaceConfig.ShowGates, x => RaceConfig.ShowGates = x, true),
            Bool("Cones / barriers (worldactors.bnk)", () => RaceConfig.SpawnProps, x => RaceConfig.SpawnProps = x, true),
            Float("Countdown", () => RaceConfig.CountdownSeconds, x => RaceConfig.CountdownSeconds = x, 0.1f, 0.5f, 10f, 3.7f, "0.0", " s"),
            Float("Penalty per hit", () => RaceConfig.PenaltySeconds, x => RaceConfig.PenaltySeconds = x, 1f, 0f, 60f, 10f, "0", " s"),
            Bool("Penalties also without PeLv", () => RaceConfig.PenaltiesAlways, x => RaceConfig.PenaltiesAlways = x, false),
            Float("Penalty min. impact", () => RaceConfig.PenaltyMinImpact, x => RaceConfig.PenaltyMinImpact = x, 0.5f, 0f, 20f, 2.5f, "0.0", " m/s"),
            Bool("Unlock all licences", () => RaceConfig.UnlockAllLicences, x => RaceConfig.UnlockAllLicences = x, false),
            Bool("Use the test car (ig2 VMod)", () => RaceConfig.UseTestCar, x => RaceConfig.UseTestCar = x, true),
            Bool("Back to the academy interior", () => RaceConfig.ReturnIntoInterior, x => RaceConfig.ReturnIntoInterior = x, true),
            Bool("Hide players during tests", () => RaceConfig.HidePlayersInRacingSchool, x => RaceConfig.HidePlayersInRacingSchool = x, true),
            Float("Autopilot cruise speed", () => RaceConfig.AutopilotCruiseKmh, x => RaceConfig.AutopilotCruiseKmh = x, 5f, 0f, 120f, 35f, "0", " km/h"),
            Action_("Reset racing school progress", () =>
            {
                var repo = school.EnsureRepository();
                RaceProgress.ResetAll(repo?.Database.Licenses);
                Toast("Racing school progress reset");
            }),
        };

        if (!v.Loaded)
        {
            l.Add(Head(""));
            l.Add(Info("No catalog yet – waiting for the bnk folder."));
            return l;
        }

        l.Add(Head("RACES BY GROUP"));
        foreach (RaceGroup g in Enum.GetValues(typeof(RaceGroup)))
        {
            var group = g;
            int count = v.FilesInGroup(group).Count;
            if (count == 0) continue;
            l.Add(Menu(RaceDebugView.GroupName(group) + " (" + count + ")", () => RaceList(group)));
        }
        l.Add(Menu("ALL .ig2 FILES (A-Z)", AllRaceList));
        return l;
    }

    List<Item> RaceList(RaceGroup group)
    {
        var v = ReadyRaceView();
        var l = new List<Item> { Head(RaceDebugView.GroupName(group)) };
        var names = v.FilesInGroup(group);
        if (names.Count == 0) { l.Add(Info("no races in this group")); return l; }
        foreach (var name in names) l.Add(RaceEntry(v, name));
        return l;
    }

    List<Item> AllRaceList()
    {
        var v = ReadyRaceView();
        var l = new List<Item> { Head("ALL .ig2 FILES") };
        if (v.Files.Count == 0) { l.Add(Info("no files")); return l; }
        foreach (var name in v.Files) l.Add(RaceEntry(v, name));
        return l;
    }

    Item RaceEntry(RaceDebugView v, string n)
    {
        string hint = v.HintFor(n);
        bool sel = string.Equals(n, v.SelectedName, StringComparison.OrdinalIgnoreCase);
        return Action_((sel ? "● " : "  ") + n.ToUpperInvariant() + (hint.Length > 0 ? "   [" + hint + "]" : ""),
            () => { if (v.Select(n)) Toast("Selected " + n); else Toast(v.Status); });
    }

    /// <summary>Race-Teil der Debug-Ansicht (Unity DebugViewMenu "RACE DEBUG VIEW").</summary>
    void AddRaceDebugToggles(List<Item> l)
    {
        var v = RaceView;
        l.Add(Bool("Show in the 3D world", () => v.ShowInWorld, x => v.ShowInWorld = x, true));
        l.Add(Bool("Runtime overlay + labels", () => v.ShowOverlay, x => v.ShowOverlay = x, true));
        l.Add(Bool("Route", () => v.ShowRoute, x => v.ShowRoute = x, true));
        l.Add(Bool("Follow roads", () => v.ShowRoadRoute, x => v.ShowRoadRoute = x, true));
        l.Add(Bool("Gates / checkpoints", () => v.ShowGates, x => v.ShowGates = x, true));
        l.Add(Bool("Cones / barriers", () => v.ShowDecor, x => v.ShowDecor = x, true));
        l.Add(Bool("Opponent cars", () => v.ShowOpponents, x => v.ShowOpponents = x, true));
        l.Add(Bool("World labels", () => v.ShowLabels, x => v.ShowLabels = x, true));
        l.Add(Float("View distance", () => v.ViewDistance, x => v.ViewDistance = x, 25f, 100f, 5000f, 900f, "0", " m"));
        l.Add(Action_("Move free camera to selected race", () => Toast(v.MoveCameraToStart(_terrain, true))));
    }
}
