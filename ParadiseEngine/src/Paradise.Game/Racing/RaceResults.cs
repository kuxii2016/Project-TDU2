// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceResults.cs).
using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Gamemodes;
using System.Globalization;

namespace Paradise.Game.Racing;

/// <summary>
/// Ergebnisbildschirm einer Prüfung über den Original-Film frontend/&lt;res&gt;/results.bnk (results.vmf).
/// Variablen und Label wie GMCompDrivingSchool in der EXE (0xb5c840..0xb5cc40):
///   /:IsPenalty /:final_score /:basetime /:IsTimeOut /:ref_score /:IsRacingSchool=1 /:IsTestScore=1 /:TestName
///   /:LicenceName /:Licence_group1 /:Licence_group2 /:penalty ("+…") /:Message (Text 1625/1626/1627)
///   /:IsLicensed (0 = nein, 1 = schon vorher, 2 = gerade erworben) /:nb_tests_done /:nb_tests_total
///   dann GotoLabel("/", "success_test" | "failed_test").
/// Der Film meldet über FSCommands: "rest" (nochmal), "nets" (nächster Test), "quit" (zurück zur Akademie),
/// "dbxo" (Dialog -> hier sofort mit "a" = ja beantwortet), "gofr" (freie Fahrt).
/// </summary>
public sealed class RaceResults
{
    public VehicleManager? Manager;
    public event Action? Retry, Next, Quit;

    VmfPlayer? _film;
    bool _open;
    public bool IsOpen => _open;
    /// <summary>Irgendein Ergebnis-Film offen (ESC gehört dann ihm, Pausemenü gesperrt).</summary>
    public static bool AnyOpen => _openCount > 0;
    static int _openCount;
    float _time;

    bool Load()
    {
        if (_film != null) return true;
        var path = FlashHost.FindFrontendBank("results.bnk");
        if (path == null) { Log.Warn("results.bnk nicht gefunden"); return false; }
        // über HUD, Minimap und Tacho (kleinere Tiefe = weiter vorn)
        _film = FlashHost.Load(path, -100, true);
        if (_film == null) return false;
        _film.MouseEnabled = true;
        _film.Visible = false;
        _film.FSCommand += OnCommand;
        return true;
    }

    void Unload()
    {
        if (_film == null) return;
        _film.FSCommand -= OnCommand;
        FlashHost.Remove(_film);
        _film = null;
    }

    /// <summary>Ergebnis anzeigen. licensed: 0 nein, 1 schon vorher, 2 gerade erworben.</summary>
    public bool Show(RaceResult? r, LicenseInfo? lic, int testsDone, int testsTotal, int licensed, string? testName)
    {
        if (r == null) return false;
        // Film bei jedem Ergebnis frisch laden: im Spiel wird results.vmf pro Ergebnis neu geladen, und ein offen
        // gebliebenes menu_rs (InRootMenu, Input=false nach "valid") würde sonst die nächste Anzeige blockieren.
        Unload();
        if (!Load()) return false;
        var f = _film!;
        f.SetValue("/:WhatPlatform", 1);
        f.SetVariable("/:WhatChaType", "solo");
        f.SetValue("/:IsPenalty", r.PenaltyMs > 0);
        f.SetVariable("/:final_score", RaceHud.FormatRaceTimeMs(r.FinalTimeMs));
        f.SetVariable("/:basetime", RaceHud.FormatRaceTimeMs(r.RawTimeMs));
        f.SetValue("/:IsTimeOut", r.TimedOut);
        f.SetVariable("/:ref_score", r.TargetMs > 0 ? RaceHud.FormatRaceTimeMs(r.TargetMs) : "--'--\"--");
        f.SetValue("/:IsRacingSchool", true);
        f.SetValue("/:IsTestScore", 1);
        f.SetVariable("/:TestName", testName ?? "");
        f.SetVariable("/:LicenceName", lic != null ? lic.DisplayName : "");
        f.SetVariable("/:Licence_group1", lic != null ? lic.Group1 : "empty");
        f.SetVariable("/:Licence_group2", lic != null ? lic.Group2 : "empty");
        if (r.PenaltyMs > 0) f.SetVariable("/:penalty", "+" + RaceHud.FormatRaceTimeMs(r.PenaltyMs));
        ulong msg = !r.Passed ? RaceTexts.MsgTestFailed : licensed == 2 ? RaceTexts.MsgLicenceWon : RaceTexts.MsgTestPassed;
        f.SetVariable("/:Message", RaceTexts.Get(msg, r.Passed ? "Bestanden" : "Nicht bestanden"));
        _champ = false;
        f.SetValue("/:IsLicensed", licensed);
        f.SetVariable("/:nb_tests_done", testsDone.ToString(CultureInfo.InvariantCulture));
        f.SetVariable("/:nb_tests_total", testsTotal.ToString(CultureInfo.InvariantCulture));
        f.SetValue("/:IsReward", false);
        f.SetValue("/:LockReplay", true);    // Replays gibt es im Nachbau nicht
        f.SetValue("/:Input", true);
        f.SetValue("/:fake", 0);
        f.SetValue("/:InRootMenu", false);
        f.SetValue("/:InMiniMenu", false);
        f.SetVariable("/:BtValid", "");
        _lastBtValid = ""; _btValidAt = -1f;
        Open();
        f.GotoLabel("/", r.Passed ? "success_test" : "failed_test", true);
        return true;
    }

    void Open()
    {
        HideHud(true);
        if (!_open) _openCount++;
        _open = true;
        _film!.Visible = true;
    }

    // ------------------------------------------------------------------ Meisterschaftsrennen (results.vmf, WhatChaType "solo", IsCup)
    /// <summary>Zeile einer Ergebnisliste (arrays/results bzw. arrays/contest: line1..8/content: pos, player_name, player_car_name, score).</summary>
    public sealed class Row { public int Place; public string Name = "", Car = "", Score = ""; public bool Player; }

    bool _champ;
    float _stuckSince = -1f; string? _stuckLabel; int _stuckFrame = -1;
    List<Row>? _race, _cup;

    /// <summary>
    /// Ergebnis eines Meisterschaftsrennens wie im Spiel: "results" -> Ergebniskarte ("score": Platz, Zeit, Medaille, Preisgeld)
    /// -> A -> Rennergebnis (challenge_rank, arrays/results, FSCommand rcha) -> A -> Meisterschaftswertung (contest_rank,
    /// arrays/contest, rcon) -> A -> Menü (end_menu: Weiter / Wiederholen / Beenden).
    /// </summary>
    public bool ShowChampionship(string? raceTitle, string? cupName, string? whatcha, string? cupIcon, int place, string? finalScore,
                                 long reward, List<Row>? race, List<Row>? cup)
    {
        Unload();
        if (!Load()) return false;
        var f = _film!;
        _champ = true;
        f.SetValue("/:WhatPlatform", 1);
        f.SetVariable("/:MenuType", "pc");
        f.SetValue("/:fake", 0);
        f.SetVariable("/:WhatChaType", "solo");
        f.SetValue("/:IsCup", true);
        f.SetValue("/:IsCupReward", reward > 0);
        f.SetValue("/:IsCupSCR", false);
        f.SetValue("/:IsORC", false);
        f.SetValue("/:ORCTraining", false);
        f.SetValue("/:InterClub", false);
        f.SetValue("/:IsTraining", false);
        f.SetValue("/:IsRacingSchool", false);
        f.SetValue("/:IsRaceOver", 1);
        f.SetVariable("/:whatcha", whatcha ?? "timeAttack");
        f.SetVariable("/:ChallengeName", raceTitle ?? "");
        f.SetVariable("/:ContestName", cupName ?? "");
        f.SetVariable("/:Championship_icon", cupIcon ?? "asphalt");
        f.SetValue("/:Championship_num", 1);
        f.SetValue("/:PlayerRanking", place);
        f.SetVariable("/:Exposant", "");
        f.SetValue("/:WhatCup", place >= 1 && place <= 3 ? place : 0);
        f.SetVariable("/:final_score", finalScore ?? "");
        f.SetVariable("/:BaseScore", finalScore ?? "");
        f.SetVariable("/:Reward", reward > 0 ? reward.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "0 $");
        f.SetValue("/:IsPenalty", false);
        f.SetValue("/:GME_RefTime", false);
        f.SetValue("/:LockReplay", true);    // keine Replays im Nachbau
        f.SetValue("/:IsRankings", 0);       // keine Online-Ranglisten
        f.SetValue("/:LockRankings", 1);
        f.SetValue("/:IsRetry", 0);          // 0 = Wiederholen nicht gesperrt
        f.SetValue("/:LooseCup", false);
        f.SetValue("/:CanReturnToLobby", false);
        f.SetValue("/:Input", true);
        f.SetValue("/:InRootMenu", false);
        f.SetValue("/:InMiniMenu", false);
        f.SetVariable("/:BtValid", "");
        FillLines("/arrays/results", race);
        FillLines("/arrays/contest", cup);
        _race = race; _cup = cup;
        _lastBtValid = ""; _btValidAt = -1f; _stuckSince = -1f;
        Open();
        f.GotoLabel("/", "results", true);
        Log.Info("Ergebnis Meisterschaft (results.vmf): Platz " + place + ", " + (race?.Count ?? 0) + " Fahrer");
        return true;
    }

    void FillLines(string array, List<Row>? rows)
    {
        if (_film == null) return;
        for (int i = 1; i <= 8; i++)
        {
            string c = array + "/line" + i + "/content";
            var r = rows != null && i - 1 < rows.Count ? rows[i - 1] : null;
            _film.SetVariable(c + ":pos", r != null ? r.Place.ToString(CultureInfo.InvariantCulture) : "");
            _film.SetVariable(c + ":player_name", r?.Name ?? "");
            _film.SetVariable(c + ":player_car_name", r?.Car ?? "");
            _film.SetVariable(c + ":score", r?.Score ?? "");
            _film.SetValue(c + ":isplayer", r != null && r.Player ? 1 : 0);
        }
    }

    /// <summary>
    /// Rückfall für die Animationsketten (Ende von score/arrays ruft _root.gotoAndPlay(...)): bleibt der Film nach einer
    /// Eingabe an derselben Stelle stehen (Input aus), selbst zum nächsten Schritt.
    /// </summary>
    void WatchChampionship()
    {
        var f = _film!;
        var root = f.Root;
        if (root == null) return;
        string lab = f.Label("/") ?? "";
        bool input = Truthy(f.GetVariable("/:Input"));
        if (lab != _stuckLabel || root.Frame != _stuckFrame || input && lab != "results" && lab != "loop")
        {
            _stuckLabel = lab; _stuckFrame = root.Frame; _stuckSince = _time;
            return;
        }
        if (_time - _stuckSince < (lab == "results" || lab == "loop" ? 1.5f : 3f)) return;
        _stuckSince = _time;
        string? next = lab switch
        {
            "results" or "loop" => "score",
            "score" => "challenge_rank",
            "challenge_rank" => "contest_rank",
            "contest_rank" or "champ_reward" => "end_menu",
            _ => null,
        };
        if (next == null) return;
        Log.Info("Ergebnis: Film hängt in '" + lab + "' – weiter mit '" + next + "'");
        f.SetValue("/:Input", true);
        f.GotoLabel("/", next, true);
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        _openCount = System.Math.Max(0, _openCount - 1);
        if (_film != null) _film.Visible = false;
        HideHud(false);
    }

    // Fahr-HUD (Minimap/Tacho, Flash) während des Ergebnisses ausblenden (Unity: HUD-Film unsichtbar, Minimap-Komponente aus)
    bool _hudHidden, _hudWas;

    void HideHud(bool hide)
    {
        var m = Manager;
        if (m == null) return;
        if (hide && !_hudHidden) { _hudWas = m.ShowHud; m.ShowHud = false; _hudHidden = true; m.ApplyHudVisibility(); }
        else if (!hide && _hudHidden) { m.ShowHud = _hudWas; _hudHidden = false; m.ApplyHudVisibility(); }
        Log.Debug("Ergebnis-HUD " + (hide ? "aus" : "an"));
    }

    // Rückfall: Knopf gewählt (_root.BtValid), aber der Film meldet kein FSCommand (Animationskette menu_rs -> close_loop -> ValidMenu)
    string _lastBtValid = ""; float _btValidAt = -1f;
    const float ValidTimeout = 1.5f;

    void WatchValidation()
    {
        string v = (_film!.GetVariable("/:BtValid") ?? "").Trim().ToLowerInvariant();
        if (v != _lastBtValid) { _lastBtValid = v; _btValidAt = v.Length > 0 ? _time : -1f; return; }
        if (_btValidAt < 0f || _time - _btValidAt < ValidTimeout) return;
        _btValidAt = -1f;
        Log.Info("Ergebnis: Knopf " + v + " ohne FSCommand – führe selbst aus");
        switch (v)
        {
            case "retrytest": case "retry": Fire(Retry); break;
            case "nexttest": case "next": Fire(Next); break;
            case "rankings": case "replay": if (_champ) Fire(Next); break;
            case "quittest": case "quit": Fire(Quit); break;
        }
    }

    /// <summary>Jedes Bild: Eingaben an den Film (Unity Update). f10 = Notausgang.</summary>
    public void Update(IGameModeContext ctx, float dt)
    {
        _time += dt;
        if (!_open || _film == null) return;
        WatchValidation();
        if (!_open || _film == null) return;
        if (_champ) WatchChampionship();
        if (FlashNav.Pressed(FlashKey.Up)) Send("Move", "up");
        if (FlashNav.Pressed(FlashKey.Down)) Send("Move", "down");
        if (FlashNav.Pressed(FlashKey.Left)) Send("Move", "left");
        if (FlashNav.Pressed(FlashKey.Right)) Send("Move", "right");
        if (FlashNav.Pressed(FlashKey.Confirm)) Send("A");
        if (FlashNav.Pressed(FlashKey.Back) || ctx.Pressed(0x1B)) { if (!Send("B")) Fire(Quit); }
        if (ctx.Pressed(0x79)) Fire(Quit);   // F10: Notausgang
    }

    /// <summary>
    /// Eingabe so verteilen wie die EXE (vgl. Hilfeleisten-Button 7343 in results.vmf):
    /// _root.InRootMenu → _root.RootMenuPath (Clip "menu_rs"), _root.InMiniMenu → _root.MiniMenuPath, sonst _root.Clip.
    /// Ohne diese Weiche landete "A" immer wieder in _root.A() (Ergebnis → menu_rs neu öffnen) und die Knöpfe
    /// Wiederholen/Nächster Test/Beenden reagierten nie.
    /// </summary>
    bool Send(string fn, params object[] args)
    {
        var f = _film!;
        if (Truthy(f.GetVariable("/:InRootMenu")))
            foreach (var path in RootMenuPaths)
                if (f.Exists(path) && f.CallClip(path, fn, args)) return true;
        if (Truthy(f.GetVariable("/:InMiniMenu")) && f.Exists("/mini_menu") && f.CallClip("/mini_menu", fn, args)) return true;
        return f.Input(fn, args);
    }

    static readonly string[] RootMenuPaths = { "/menu_rs", "/menu" };
    static bool Truthy(string? v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    void OnCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) { FrontendSounds.Command(c); return; }
        Log.Info("Ergebnis FSCommand: " + cmd);
        switch (c)
        {
            case "rest": Fire(Retry); break;
            case "nets": Fire(Next); break;
            case "quit":
            case "gofr":
                {
                    // Meisterschaft: "Weiter" (BtValid next) = zurück in den Wettbewerb, "Wiederholen", sonst freie Fahrt
                    string bt = (_film?.GetVariable("/:BtValid") ?? "").Trim().ToLowerInvariant();
                    if (_champ && bt == "next") Fire(Next);
                    else if (_champ && bt.StartsWith("retry")) Fire(Retry);
                    else Fire(Quit);
                    break;
                }
            case "rcha": if (_champ) FillLines("/arrays/results", _race); break;     // Spiel liefert die Rennliste
            case "rcon": if (_champ) FillLines("/arrays/contest", _cup); break;      // ... und die Meisterschaftswertung
            case "dbxo":
                // Bestätigungsdialog (z.B. "Beenden?"): wie das Spiel mit "ja" beantworten
                _film?.SetVariable("/:PlayerAnswer", "a");
                _film?.CallClip("/", "DialogBox");
                break;
        }
    }

    void Fire(Action? a) { Close(); a?.Invoke(); }

    /// <summary>Unity OnDestroy.</summary>
    public void Dispose() { Close(); Unload(); }
}
