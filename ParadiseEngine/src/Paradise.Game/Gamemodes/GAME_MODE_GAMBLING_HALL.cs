using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Screens;
using System.Globalization;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Spielhalle / Community Racing Center (GAME_MODE_GAMBLING_HALL 11, Spots EGAMHA, Interieur igamha, Film gamblinhall.bnk).
/// Das Laufen im Interieur macht InteriorUtils (GAME_MODE_LIVING); die Auswahlpunkte der Halle tragen ihre Film-Art im
/// SelectableString (igamha entities.xmb): "gamblin_bar" (Barman: Challenges), "private_list", "score_board", "score_board_orc",
/// "periodical". Der Film (ItemType) ruft dafür OpenGamblin / OpenPeriodical bzw. springt zu den Tafeln.
///
/// Ablauf "gamblin_bar" (Film):
///   gamblin_hall -> OpenGamblin -> bar_open (SetMenu: "gamb:chal:open", Knöpfe Challenges / Meine Challenges / Hall of Fame)
///   -> bar_games -> bar_list "loading" (ASFlashList "bar_list", 9 Spalten; wartet auf _root.Clip.isListLoaded)
///   -> Zeile bestätigt -> details_load (wartet auf _root.IsCardLoaded) -> details_open (details.initCard liest die Karte)
///   -> A: IsChallengesValid -> Bild 182: ohne _root.NotEnoughMoney "gamb:chva" (Challenge annehmen) -> Rennen.
///   "wmon"/"gofr" = zurück ins Laufen.
/// Daten: eigener TDU2Server ("challlist", "challget", "challres"); die Challenge läuft über den Challenge-Ablauf der Testfahrt
/// (GAME_MODE_CHAMPIONSHIP.BeginTest). Gebühr wird beim Annehmen vom Geld (Spielstand) abgezogen.
/// [OFFEN] Tafeln (score_board, score_board_orc), private Liste, periodische Wettbewerbe (ORC), Bewertungen (Votes), Auszahlung
/// des Topfes bei Ablauf, Ergebnisbildschirm temp_result in der Halle.
/// </summary>
public sealed class GAME_MODE_GAMBLING_HALL : GameModeHandler
{
    public override Gamemode Mode => Gamemode.GAME_MODE_GAMBLING_HALL;
    static readonly ScreenDef[] _screens = { ScreenDefs.GamblingHall };
    public override IReadOnlyList<ScreenDef> Screens => _screens;

    static GAME_MODE_GAMBLING_HALL Instance => (GAME_MODE_GAMBLING_HALL)GameModes.Get(Gamemode.GAME_MODE_GAMBLING_HALL);

    static readonly HashSet<string> HallItems = new(StringComparer.OrdinalIgnoreCase)
        { "gamblin_bar", "private_list", "score_board", "score_board_orc", "periodical" };

    IGameModeContext? _ctx;
    VmfPlayer? _ui;
    bool _hooked;
    string? _pending;

    // Liste (bar_list) und Karte
    VmfPlayer.FlashList? _list;
    readonly List<ChallengeEntry> _items = new();
    Task<NetResponse>? _listTask, _cardTask, _resultTask;
    bool _listLoaded;
    ChallengeEntry? _sel;
    byte[]? _selData;

    // Rennen
    enum Phase { None, WaitCar, Racing }
    Phase _phase;
    Spot? _spot;
    RaceDefinition? _def;
    ChallengeEntry? _racing;
    float _waitT;

    /// <summary>SelectableString eines Punkts der Spielhalle (igamha: Barman, Tafeln, Privat-Liste, periodische Wettbewerbe).</summary>
    internal static bool IsHallItem(string? item) => !string.IsNullOrEmpty(item) && HallItems.Contains(item);

    /// <summary>Auswahlpunkt der Halle angesprochen (InteriorUtils-Hotspot mit SelectableString). false = kein Punkt der Halle.</summary>
    internal static bool OpenFromHotspot(string? item)
    {
        if (string.IsNullOrEmpty(item) || !HallItems.Contains(item)) return false;
        var h = Instance;
        if (h._phase != Phase.None) return true;
        h._pending = item;
        GameMode.Set(Gamemode.GAME_MODE_GAMBLING_HALL);
        return true;
    }

    // ================================================================== Spielmodus
    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        _ui = ctx.Open(ScreenDefs.GamblingHall);
        if (_ui == null) { Log.Warn("Spielhalle: gamblinhall.bnk fehlt"); BackToRoom(); return; }
        if (!_hooked)
        {
            _ui.FSCommand += OnCommand;
            _ui.ListCreated += OnListCreated;
            _ui.ListLineDrawn += OnListLineDrawn;
            _ui.ListValidated += OnListValidated;
            _hooked = true;
        }
        if (from == Gamemode.GAME_MODE_PAUSE) return;
        if (_phase != Phase.None) { _ui.Visible = false; return; }
        _ui.Visible = true;
        _ui.MouseEnabled = true;
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        string item = _pending ?? "gamblin_bar";
        _pending = null;
        SetCommonVars(_ui);
        switch (item)
        {
            case "gamblin_bar":
                // exe/Film ItemType "gamblin_bar": OpenGamblin (FirstTime -> Hilfe, sonst bar_open)
                _ui.SetVariable("/:WhatGamblinMenu", "challenges");
                _ui.SetVariable("/:SelItemType", "gamblin_bar");
                _ui.GotoLabel("/", "gamblin_hall", true);
                _ui.CallClip("/", "OpenGamblin");
                Log.Info("Spielhalle: Bar geöffnet (Challenges): " + item);
                break;
            default:
                Log.NotImplemented("Spielhalle: " + item + " noch nicht portiert");
                BackToRoom();
                break;
        }
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;
        if (_ui != null) _ui.Visible = false;
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        float dt = time.DeltaTime;
        if (_listTask != null && _listTask.IsCompleted) ListDone();
        if (_cardTask != null && _cardTask.IsCompleted) CardDone();
        if (_resultTask != null && _resultTask.IsCompleted) ResultDone();
        if (_phase == Phase.WaitCar) { WaitForCar(dt); return; }
        if (_phase == Phase.Racing) return;
        if (!InteriorUtils.Inside) { GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        if (_ui != null) _ui.MouseEnabled = !FlashDialog.Active;
        if (FlashDialog.Active) FlashDialog.Update();
        // bar_list "loading": der Film wartet auf isListLoaded (uploader.initLoading setzt es zurück) – setzen, sobald die Liste da ist
        if (_listLoaded && _ui != null) _ui.SetValue("/bar_list:isListLoaded", true);
    }

    void SetCommonVars(VmfPlayer ui)
    {
        ui.SetValue("/:FirstTime", false);
        ui.SetValue("/:UGC", true);              // eigene Inhalte erlaubt (sonst Dialog UGCLocked)
        ui.SetValue("/:OnlyFriendsUGC", false);
        ui.SetValue("/:Input", true);
        if (TDU2Session.LoggedIn) ui.SetVariable("/:money", Money(TDU2Session.Save.money));
    }

    /// <summary>Zurück ins Laufen (Film "wmon"/"gofr", Fehler).</summary>
    void BackToRoom()
    {
        if (_ui != null) _ui.Visible = false;
        _list = null; _listLoaded = false;
        GameMode.Set(InteriorUtils.Inside ? InteriorUtils.RoomMode : Gamemode.GAME_MODE_FREERIDE);
    }

    void OnCommand(string cmd, string args)
    {
        if (GameMode.Current != Gamemode.GAME_MODE_GAMBLING_HALL || _ui == null) return;
        Log.Info("Spielhalle: FSCommand " + cmd + (string.IsNullOrEmpty(args) ? "" : " " + args));
        switch (cmd)
        {
            case "dbxo": FlashDialog.Open(_ui); return;
            case "wmon": case "gofr": case "back": BackToRoom(); return;            // BackToWM / Ausgang
            case "wmof": case "camn": case "camf": case "sbdd": case "stao": case "star": case "vtke": return;
            case "gamb:chal:open": return;                             // Menü der Bar (Knöpfe setzt der Film selbst)
            case "gamb:chva": AcceptChallenge(); return;               // Challenge annehmen (Bild 182)
        }
        if (cmd.StartsWith("sndp:")) return;
        Log.NotImplemented("Spielhalle Befehl nicht portiert: " + cmd + (string.IsNullOrEmpty(args) ? "" : " " + args));
    }

    // ================================================================== Liste der Challenges (bar_list)
    void OnListCreated(VmfPlayer.FlashList l)
    {
        if (GameMode.Current != Gamemode.GAME_MODE_GAMBLING_HALL || _ui == null || l.Name != "bar_list") return;
        _list = l;
        _listLoaded = false;
        // Spalten der Zeile (Textvariablen von /bar_list/lineN): Name, Gebühr, Belohnung, Restlaufzeit, Insel, Stimmen
        l.ColumnVars = new[] { "game_tag", "fee", "reward", "duration", "island", "votes" };
        l.Rows.Clear(); _items.Clear();
        var client = TDU2Session.Client;
        if (!TDU2Session.LoggedIn || client == null || TDU2Session.Offline)
        {
            Log.Warn("Spielhalle: nicht am Server angemeldet – leere Liste");
            _listLoaded = true; _ui.ListRefresh(l); _ui.ListLoaded(l);
            return;
        }
        _listTask = client.Send(new NetRequest { op = "challlist", session = TDU2Session.SessionId, kind = "", target = -1 });
    }

    void ListDone()
    {
        var t = _listTask!; _listTask = null;
        NetResponse? r = null;
        try { r = t.Result; } catch (Exception ex) { Log.Warn("Spielhalle: Liste nicht geladen: " + ex.GetBaseException().Message); }
        var l = _list;
        _items.Clear();
        if (r != null && r.ok && r.challenges != null) _items.AddRange(r.challenges);
        else if (r != null) Log.Warn("Spielhalle: Liste abgelehnt: " + r.error);
        if (l != null && _ui != null)
        {
            l.Rows.Clear();
            foreach (var c in _items)
                l.Rows.Add(new[] { c.name, Money(c.fee), Money(c.reward), TimeLeft(c), IslandName(c.island), "0" });
            _ui.ListRefresh(l);
            _ui.ListLoaded(l);
        }
        _listLoaded = true;
        if(_items.Count == 0)
                BackToRoom();
        else
            _ui.PlayLabel("/bar_list", "open");
        Log.Info("Spielhalle: " + _items.Count + " Challenges vom Server");
    }

    /// <summary>Zeile gezeichnet: Symbol der Spielart (content/gm/ic: time/speed/trap) und Klasse (content/category: all).</summary>
    void OnListLineDrawn(VmfPlayer.FlashList l, int line, int index)
    {
        if (_ui == null || l != _list || index < 0 || index >= _items.Count) return;
        var c = _items[index];
        string lp = "/bar_list/line" + line + "/content";
        _ui.GotoLabel(lp + "/gm/ic", "modes", false);
        _ui.GotoLabel(lp + "/gm/ic/modes", ModeIcon(c.mode), false);
        _ui.GotoLabel(lp + "/category", "all", false);
        bool poor = TDU2Session.LoggedIn && TDU2Session.Save.money < c.fee;
        _ui.GotoLabel(lp + "/fee", poor ? "wrong" : "default", false);
    }

    /// <summary>Zeile bestätigt (der Film geht nach details_load und wartet auf _root.IsCardLoaded): Karte füllen, .ig2 holen.</summary>
    void OnListValidated(VmfPlayer.FlashList l)
    {
        if (_ui == null || l != _list) return;
        int i = l.Selected;
        if (i < 0 || i >= _items.Count) return;
        _sel = _items[i]; _selData = null;
        _ui.SetValue("/:IsCardLoaded", false);
        FillCard(_ui, _sel);
        var client = TDU2Session.Client;
        if (client == null) return;
        _cardTask = client.Send(new NetRequest { op = "challget", session = TDU2Session.SessionId, target = _sel.id });
    }

    void CardDone()
    {
        var t = _cardTask!; _cardTask = null;
        NetResponse? r = null;
        try { r = t.Result; } catch (Exception ex) { Log.Warn("Spielhalle: Challenge nicht geladen: " + ex.GetBaseException().Message); }
        if (r == null || !r.ok || string.IsNullOrEmpty(r.challData)) { Log.Warn("Spielhalle: Challenge nicht geladen: " + (r?.error ?? "keine Antwort")); return; }
        try { _selData = Convert.FromBase64String(r.challData); } catch (FormatException) { _selData = null; return; }
        if (r.challenges.Count > 0) _sel = r.challenges[0];
        if (_ui != null && _sel != null)
        {
            FillCard(_ui, _sel);
            _ui.SetValue("/:IsCardLoaded", true);
        }
    }

    /// <summary>Karte (details.initCard): Werte der Challenge, Bestwertung, Gebühr/Belohnung, Fahrzeug-Prüfung.</summary>
    void FillCard(VmfPlayer ui, ChallengeEntry c)
    {
        Ige2File? f = null;
        try { if (_selData != null) f = Ige2File.Parse(_selData); } catch (Exception) { f = null; }
        ui.SetVariable("/:ChallengeName", c.name);
        ui.SetVariable("/:ChallengeDescription", c.description);
        ui.SetVariable("/:ChallengeCreator", c.author);
        ui.SetVariable("/:WhatGM", ModeName(c.mode));
        ui.SetVariable("/:Distance", (c.length / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km");
        ui.SetVariable("/:NbCheckpoints", c.checkpoints.ToString(CultureInfo.InvariantCulture));
        ui.SetVariable("/:NbLaps", f != null && f.IsCircuit ? System.Math.Max(1, f.Laps).ToString(CultureInfo.InvariantCulture) : "--");
        ui.SetValue("/:TrafficValue", f != null && f.Int("ChTR") != 0 ? f.Int("trat") : 0);
        ui.SetValue("/:PenaltyValue", f != null && f.Int("offR") != 0 ? f.Int("PeCr") : 0);
        ui.SetValue("/:IsForcedPath", f != null && f.Int("forc") != 0);
        ui.SetVariable("/:FeesValue", Money(c.fee));
        ui.SetVariable("/:RewardValue", Money(c.reward + c.fee * c.tries));
        ui.SetVariable("/:ChallengeDuration", TimeLeft(c));
        ui.SetVariable("/:RefScore", string.IsNullOrEmpty(c.bestNick) ? "--" : ScoreText(c.mode, c.bestScore));
        ui.SetVariable("/:CurrentBestScore", string.IsNullOrEmpty(c.bestNick) ? "--" : ScoreText(c.mode, c.bestScore));
        ui.SetVariable("/:CurBestName", c.bestNick);
        ui.SetVariable("/:CurBestVehicleName", c.bestCar);
        ui.SetValue("/:IsScoreBeaten", !string.IsNullOrEmpty(c.bestNick));
        ui.SetVariable("/:NbParticipations", c.tries.ToString(CultureInfo.InvariantCulture));
        ui.SetVariable("/:NbVotes", "0");
        ui.SetVariable("/:VotesCount", "0");
        ui.SetVariable("/:RestrictionType", "empty");
        ui.SetVariable("/:GroupRestriction", "all");
        ui.SetValue("/:IsCarOk", CarOk(f) ? 1 : 0);
        ui.SetValue("/:IslandLocked", false);
        ui.SetValue("/:NoMoreTries", false);
        ui.SetValue("/:NotEnoughMoney", TDU2Session.LoggedIn && TDU2Session.Save.money < c.fee);
        if (f != null && (c.mode == 10 || c.mode == 1))
        {
            string kmh = (f.Int("tras") * 3.6f / 1000f).ToString("0", CultureInfo.InvariantCulture) + " km/h";
            ui.SetVariable(c.mode == 10 ? "/:SpeedToReach" : "/:SpeedTrapMin", kmh);
        }
    }

    /// <summary>Einschränkung der Challenge (VSpe, wie der Editor schreibt) gegen das aktuelle Auto.</summary>
    bool CarOk(Ige2File? f)
    {
        if (f == null) return true;
        var car = _ctx?.Vehicles?.Manager?.Current?.Car ?? InteriorUtils.Vm?.Current?.Car;
        int cat = car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0;
        switch (f.Int("VSpe"))
        {
            case 2: return car != null && car.Id == f.Int("VMod");
            case 4: return cat == f.Int("Vli2");
            case 3: return cat >= 0 && cat < EditorChallenge.CatClass.Length && EditorChallenge.CatClass[cat] == f.Int("Vlt2");
            default: return true;
        }
    }

    // ================================================================== Rennen
    /// <summary>"gamb:chva": Gebühr zahlen, Interieur verlassen, am Start der Challenge losfahren (Challenge-Ablauf der Testfahrt).</summary>
    void AcceptChallenge()
    {
        var c = _sel;
        if (c == null || _selData == null) { Log.Warn("Spielhalle: keine Challenge geladen"); return; }
        RaceDefinition def;
        try { def = RaceRepository.FromIge2(Ige2File.Parse(_selData), "CRC_" + c.id, null); }
        catch (Exception ex) { Log.Warn("Spielhalle: Challenge unlesbar: " + ex.Message); return; }
        if (def.Checkpoints.Count == 0) { Log.Warn("Spielhalle: Challenge ohne Tore"); return; }
        // Werte der Original-Vorlage, die nicht zur Challenge gehören (wie die Testfahrt des Editors)
        def.RefSplitsMs.Clear(); def.SilverTimeMs = 0; def.BronzeTimeMs = 0; def.BotCount = 0; def.Opponents.Clear(); def.CarId = 0; def.RewardMoney = 0;
        def.TargetTimeMs = !string.IsNullOrEmpty(c.bestNick) && !CrcText.HigherIsBetter(c.mode) ? c.bestScore : 0;
        if (TDU2Session.LoggedIn && c.fee > 0)
        {
            TDU2Session.Save.money = System.Math.Max(0, TDU2Session.Save.money - c.fee);
            _ = TDU2Session.SaveAsync();
        }
        _def = def; _racing = c; _spot = InteriorUtils.CurrentSpot;
        if (_ui != null) _ui.Visible = false;
        _list = null; _listLoaded = false;
        Log.Info($"Spielhalle: Challenge #{c.id} \"{c.name}\" angenommen (Gebühr {c.fee} $)");
        InteriorUtils.ExitInterior();
        _phase = Phase.WaitCar; _waitT = 0f;
    }

    /// <summary>Nach dem Verlassen: warten, bis das Auto draußen steht, dann die Challenge starten.</summary>
    void WaitForCar(float dt)
    {
        _waitT += dt;
        var vm = _ctx?.Vehicles?.Manager;
        if (_ctx == null || _def == null) { _phase = Phase.None; return; }
        if (vm?.Current == null && _waitT < 30f) return;
        if (vm?.Current == null) { Log.Warn("Spielhalle: kein Auto – Challenge abgebrochen"); _phase = Phase.None; GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        _phase = Phase.Racing;
        var forced = _selData != null && Ige2File.Parse(_selData).Int("forc") != 0;
        GAME_MODE_CHAMPIONSHIP.BeginTest(_ctx, _def, forced, OnRaceEnd);
    }

    /// <summary>Ende der Fahrt: Wertung an den Server (nur im Ziel), danach zurück in die Spielhalle.</summary>
    void OnRaceEnd(bool completed)
    {
        _phase = Phase.None;
        var c = _racing;
        var res = GAME_MODE_CHAMPIONSHIP.LastTestResult;
        if (completed && c != null && res != null && TDU2Session.LoggedIn && TDU2Session.Client != null)
        {
            int score = CrcText.HigherIsBetter(c.mode) ? (int)(GAME_MODE_CHAMPIONSHIP.LastTestTopKmh * 100f) : res.FinalTimeMs;
            string car = _ctx?.Vehicles?.Manager?.Current?.Car?.FileName ?? "";
            _resultTask = TDU2Session.Client.Send(new NetRequest { op = "challres", session = TDU2Session.SessionId, target = c.id, score = score, kind = car });
            Log.Info($"Spielhalle: Challenge #{c.id} gefahren – Wertung {ScoreText(c.mode, score)}");
            //Update to Server Zeit, Current Car, Current Tuning, Level
        }
        else Log.Info("Spielhalle: Challenge nicht beendet – keine Wertung");
        // zurück in die Halle (Laufen); ohne Spot in die Freifahrt
        if (_spot != null && InteriorUtils.EnterSpot(_spot)) return;
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    void ResultDone()
    {
        var t = _resultTask!; _resultTask = null;
        try
        {
            var r = t.Result;
            if (r.ok) Log.Info("Spielhalle: Wertung gespeichert" + (r.friendResult == "best" ? " – neue Bestwertung!" : ""));
            else Log.Warn("Spielhalle: Wertung abgelehnt: " + r.error);
        }
        catch (Exception ex) { Log.Warn("Spielhalle: Wertung nicht gesendet: " + ex.GetBaseException().Message); }
    }

    // ================================================================== Hilfen
    /// <summary>Spielart (gmth) -> Symbol der Zeile (content/gm/ic/modes: time, speed, trap).</summary>
    static string ModeIcon(int mode) => mode switch { 10 => "speed", 1 => "trap", _ => "time" };

    static string ModeName(int mode) => mode switch { 10 => "speed", 1 => "speedtrap", 3 => "race", _ => "timeAttack" };

    static string IslandName(int island) => island == 0 ? "Ibiza" : island == 2 ? "Casino" : "Oahu";

    static string ScoreText(int mode, int score)
    {
        if (CrcText.HigherIsBetter(mode)) return (score / 100f).ToString("0.0", CultureInfo.InvariantCulture) + " km/h";
        int ms = System.Math.Max(0, score);
        return (ms / 60000) + "'" + (ms / 1000 % 60).ToString("00") + "''" + (ms % 1000 / 10).ToString("00");
    }

    /// <summary>Restlaufzeit "T Tag(e) hh:mm" (CRC ohne Ende: "--").</summary>
    static string TimeLeft(ChallengeEntry c)
    {
        if (string.IsNullOrEmpty(c.expires) || !DateTime.TryParse(c.expires, null, DateTimeStyles.RoundtripKind, out var e)) return "--";
        var left = e.ToUniversalTime() - DateTime.UtcNow;
        if (left.TotalSeconds <= 0) return "0";
        return left.Days > 0 ? left.Days + " " + RaceTexts.Get(0x528, "Tag(e)") : left.Hours.ToString("00") + ":" + left.Minutes.ToString("00");
    }

    static string Money(long v) => CrcText.Money(v);
}
