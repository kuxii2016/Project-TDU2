using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.Gameplay;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Utils;
using AudioEngine = Paradise.Core.Audio.AudioEngine;

namespace Paradise.Game.Screens;

#pragma warning disable CS8981   // kleingeschrieben wie die geforderte Signatur PauseMenu.InsertWrecks(wreckinfo)
public sealed class wreckinfo
{
    /// <summary>Eine Zeile wie im Original (/:Wreck%d_zone … /:Wreck%d_nbPartsFound).</summary>
    public sealed class Row
    {
        public string Zone = "";
        public string CarName = "";
        public string State = "";
        public int PartsFound;
        public int PartsTotal;
    }

    public string Island = "";
    public bool Enabled;
    public int Found;
    public int Total;
    /// <summary>Entdeckungspunkte aus DBRewardXP (25 je Wrack, C++ gm_freeride.cpp).</summary>
    public int CruisingPoints;
    public int CruisingLevel;
    public int GlobalLevel;
    /// <summary>Aktuelles Cruising-Gebiet und dessen Funde (gapr:crui:*).</summary>
    public int Area = -1;
    public int AreaFound;
    public int AreaTotal;
    /// <summary>Retail-Wreckzeilen (/:Wreck%d_*) für shopscar.bnk – nicht die sichtbare Progressliste.</summary>
    public Row[] Rows = Array.Empty<Row>();

    /// <summary>DBCruisingLevel._crlpneeded, 16 Stufen (OpenTDU2 docs/STATE.md 5995).</summary>
    static readonly int[] CruisingThresholds =
        { 0, 125, 300, 525, 825, 1200, 1650, 2175, 2795, 3510, 4320, 5225, 6225, 7355, 8615, 10000 };

    /// <summary>Level zu einer Punktzahl (FindSkillLevelIndex im C++-Port).</summary>
    public static int LevelForPoints(int points)
    {
        int level = 0;
        for (int i = 0; i < CruisingThresholds.Length; i++)
            if (points >= CruisingThresholds[i]) level = i;
        return level;
    }
}
#pragma warning restore CS8981

public sealed class PauseMenu
{
    const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
              VK_A = 0x41, VK_D = 0x44, VK_S = 0x53, VK_W = 0x57, VK_X = 0x58, VK_Y = 0x59, VK_F10 = 0x79;

    // Menüpunkte, die hier funktionieren (alles andere ist im Film gesperrt dargestellt)
    static readonly string[] Unlocked = { "LockResume", "LockOptions", "LockExitGame", "LockQuit", "LockControls", "LockVideo", "LockDisplay",
        "LockAudio", "LockGameSettings", "LockDevice", "LockMouse", "LockWheel", "LockLanguage", "LockSave", "LockPlayers", "LockJoin", "LockGameProgress", "LockInvite" };
    // LockSnapshot ("Foto machen") setzt Open() nach der exe-Regel (PhotoMode.CanTake, 0x9eb142)
    static readonly string[] Locked = { "LockChangePosition", "LockInviteParty", "LockManageParty",
        "LockRestart", "LockEditor", "LockBookmarks", "LockProfiles", "LockNewGame", "LockContinueGame", "LockAchiev", "LockRankings",
        "LockOnline", "LockSocial", "LockStatus" };

    readonly IGameModeContext _ctx;
    VmfPlayer? _ui;

    /// <summary>Rennen läuft: Menü im Rennmodus (WhatGameMode 2) mit "Neu starten" und "Beenden" = Rennen abbrechen.</summary>
    bool _raceMode;
    Action? _raceQuit, _raceRestart;   // Anbieter, der beim Öffnen aktiv war

    /// <summary>Anbieter für das Renn-ESC-Menü (Rennschul-Prüfung, Meisterschaft, Missionen …).</summary>
    sealed class RaceProvider { public object Owner = null!; public Func<bool>? Active; public Action? Restart, Quit; }
    static readonly List<RaceProvider> _raceProviders = new();

    /// <summary>
    /// Renn-Modus des ESC-Menüs anmelden (WhatGameMode 2: "Neu starten" / "Beenden"). Mehrere Anbieter möglich; beim Öffnen
    /// gilt der erste aktive. Gleicher Besitzer ersetzt seine alte Anmeldung.
    /// </summary>
    public static void RegisterRace(object owner, Func<bool> active, Action restart, Action quit)
    {
        _raceProviders.RemoveAll(p => p.Owner == owner);
        _raceProviders.Add(new RaceProvider { Owner = owner, Active = active, Restart = restart, Quit = quit });
    }

    public static void UnregisterRace(object owner) => _raceProviders.RemoveAll(p => p.Owner == owner);

    static bool FindRace(out Action? restart, out Action? quit)
    {
        restart = null; quit = null;
        foreach (var p in _raceProviders)
        {
            bool on = false;
            try { on = p.Active != null && p.Active(); } catch (Exception) { }
            if (on) { restart = p.Restart; quit = p.Quit; return true; }
        }
        return false;
    }

    /// <summary>Rennen neu starten/abbrechen: Menü schließen, dann die Aktion des Anbieters.</summary>
    void RaceAction(Action? a) { Close(); _raceMode = false; a?.Invoke(); }
    Options? _options;
    Dictionary<string, string>? _texts;
    wreckinfo? _wrecks;   // letzter Stand des Wrecks-Moduls (auch vor dem Anhängen von main.bnk)

    /// <summary>
    /// Welche Daten die sichtbare gameprogress_fl zeigt. Der Film meldet das beim Umschalten der
    /// Cruising-Unterseiten über FSCommand: gapr_fill_crui:trea = Wracks, gapr_fill_crui:road = Straßen.
    /// "roads" ist im Original der erste Eintrag (setLine[0]) und daher der Standard.
    /// </summary>
    enum ProgressFill { Road, Treasure, Events, Photos, Competition, Collection, Social }
    ProgressFill _progressFill = ProgressFill.Road;
    readonly List<AreaRow> _roadRows = new();
    readonly List<AreaRow> _treasureRows = new();
    readonly List<AreaRow> _eventRows = new();
    readonly List<AreaRow> _photoRows = new();
    readonly List<AreaRow> _racingRows = new();
    readonly List<AreaRow> _collectRows = new();
    readonly List<AreaRow> _socialRows = new();
    Paradise.Game.Gameplay.Racing.ListKind _racingList = Paradise.Game.Gameplay.Racing.ListKind.School;
    Paradise.Game.Gameplay.Collectable.ListKind _collectList = Paradise.Game.Gameplay.Collectable.ListKind.Vehicles;
    Paradise.Game.Gameplay.Social.ListKind _socialList = Paradise.Game.Gameplay.Social.ListKind.Friends;
    /// <summary>Die aktive Instanz – Discovery pusht seine Daten auch, bevor main.bnk angehängt wurde.</summary>
    public static PauseMenu? MenuInstance { get; private set; }
    public Options? Settings => _options;
    bool _open, _pendingDialog;
    float _clock, _escAt = -1f;
    int _clockShown = -1, _clockDay = -1;
    public bool IsOpen => _open;
    public string? LastCommand { get; private set; }

    public PauseMenu(IGameModeContext ctx) { _ctx = ctx; MenuInstance = this; }

    /// <summary>Film übernehmen (einmal nach dem Laden von main.bnk).</summary>
    public void Attach(VmfPlayer? ui)
    {
        if (ui == null || ReferenceEquals(ui, _ui)) return;
        _ui = ui;
        ui.Visible = false;
        ui.FSCommand += OnCommand;
        ui.ListCreated += OnListCreated;
        ui.ListValidated += OnListValidated;   // A auf einer Spielerzeile -> Spielerkarte
        ui.ListLineDrawn += OnListLineDrawn;   // Zeile neu beschrieben (auch beim Blättern): Flagge, Online-Symbol, Foto
        EnsureOptions();
        _options!.Publish(ui);
    }

    /// <summary>
    /// Optionsseiten (Unity TDU2PauseMenu.EnsureLoaded: Texte in der gespeicherten Sprache laden, TDU2Options anlegen und
    /// ApplyAll) – Unity macht das beim Spielstart, nicht erst beim ersten Öffnen des Pausemenüs. Hier nach dem Anmelden
    /// (gespeicherte Optionen liegen im Spielstand), siehe ScreenModule.ImportProfile.
    /// </summary>
    public void EnsureOptions()
    {
        if (_options != null) return;
        Options.SetLanguage(Options.SavedLanguage());
        try { _texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language); }
        catch (Exception ex) { Log.Warn("Pausemenü-Texte: " + ex.Message); _texts = new(); }
        _options = new Options(_ctx, k => _texts != null && _texts.TryGetValue(k, out var v) ? v : null);
        _options.LanguageChanged = t => { _texts = t; if (_ui != null) _options?.Publish(_ui); };
    }

    // ------------------------------------------------------------------ Wracks (Discovery)
    /// <summary>
    /// Integrationsstelle des Wrecks-Moduls: Daten merken und – falls main.bnk schon angehängt
    /// ist – sofort in die nativen Variablen schreiben. Auch vor dem Öffnen aufrufbar.
    /// </summary>
    public void InsertWrecks(wreckinfo wrecks)
    {
        if (wrecks == null) return;
        _wrecks = wrecks;
        PushWrecks();
    }

    /// <summary>Nativer Sprachstring aus db_&lt;lang&gt;.cpr (Decodet German lang.json: 2007, 3322, 4178).</summary>
    static string Loc(int id, string fallback)
    {
        try { return FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, id) ?? fallback; }
        catch (Exception) { return fallback; }
    }

    /// <summary>
    /// main.bnk hat keine eigenen Wreck-Variablen: die Anzeige läuft über PercentTreasure und
    /// die Fortschrittsseite (Cruising*/Global*, GPTitle). Zusätzlich werden die Retail-Zeilen
    /// /:Wreck%d_* gesetzt, damit shopscar.bnk dieselben Funde zeigt.
    /// </summary>
    void PushWrecks()
    {
        var ui = _ui;
        var w = _wrecks;
        if (ui == null || w == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int total = Math.Max(0, w.Total);
        int found = Math.Clamp(w.Found, 0, total);
        int percent = total > 0 ? found * 100 / total : 0;

        ui.SetValue("/:NbWrecksFound", found);
        ui.SetValue("/:NbWrecksTotal", total);
        ui.SetValue("/:PercentTreasure", percent);        // Treasure-Zeile der Fortschrittsseite (unten)
        ui.SetVariable("/:m_TreasureHunt", Loc(3245, "Treasure Hunt"));  // Name oben – nicht überschreiben!
        ui.SetValue("/:m_WrecksFound", found);            // wie map.bnk (GAME_MODE_MAP)
        ui.SetValue("/:AreaWrecksTotal", w.AreaTotal);
        ui.SetValue("/:AreaWrecksFound", w.AreaFound);

        var rows = w.Rows ?? Array.Empty<wreckinfo.Row>();
        ui.SetValue("/:nbWrecks", rows.Length > 0 ? rows.Length : total);   // Retail-Zähler (exe 0x9916b7)
        for (int i = 0; i < 10; i++)   // Original: maximal 10 Zeilen (exe 0x9915D0)
        {
            var r = i < rows.Length ? rows[i] : null;
            ui.SetVariable("/:Wreck" + i + "_zone", r?.Zone ?? "");
            ui.SetVariable("/:Wreck" + i + "_carName", r?.CarName ?? "");
            ui.SetVariable("/:Wreck" + i + "_state", r?.State ?? "");   // "abgehakt" = gefunden
            ui.SetVariable("/:Wreck" + i + "_carGp", "");
            ui.SetVariable("/:Wreck" + i + "_nbPartsFound", r != null && r.PartsFound > 0 ? r.PartsFound.ToString(inv) : "--");
            ui.SetVariable("/:Wreck" + i + "_nbPartsTotal", (r?.PartsTotal ?? 0).ToString(inv));
        }

        const int maxCruising = 14500, maxCruisingLevel = 18, maxGlobal = 73;
        int xp = Math.Max(0, w.CruisingPoints);
        int level = Math.Clamp(w.CruisingLevel, 0, maxCruisingLevel);
        int global = Math.Clamp(w.GlobalLevel, 0, maxGlobal);
        int cruisePercent = Math.Min(100, xp * 100 / maxCruising);
        ui.SetValue("/:CruisingPoints", cruisePercent);   // Ring: Prozent
        ui.SetValue("/:CruisingLvlPoints", maxCruising);
        ui.SetVariable("/:CruisingLevelCount", level.ToString(inv) + "/" + maxCruisingLevel.ToString(inv));
        string discovery = Loc(3322, "Entdeckungslevel");
        ui.SetVariable("/:CruisingTitle", discovery + " " + level.ToString(inv));
        ui.SetVariable("/:GPCruisingTitle", discovery + " " + level.ToString(inv));
        ui.SetValue("/:GlobalLevel", global);
        ui.SetVariable("/:GlobalLevelCount", global.ToString(inv) + "/" + maxGlobal.ToString(inv));
        ui.SetValue("/:GlobalPoints", Math.Min(100, global * 100 / maxGlobal));
        ui.SetValue("/:GlobalLvlPoints", maxGlobal);

        string foundText = Loc(2007, "§VAR_NB_FOUND§/§VAR_NB_TOTAL§ Wracks gefunden")
            .Replace("§VAR_NB_FOUND§", found.ToString(inv)).Replace("§VAR_NB_TOTAL§", total.ToString(inv));
        string xpText = Loc(4178, "Entdeckungspunkte: §NB-PT§ / §MX-PT§ (§RATIO§%)")
            .Replace("§NB-PT§", xp.ToString(inv)).Replace("§MX-PT§", maxCruising.ToString(inv))
            .Replace("§RATIO§", cruisePercent.ToString(inv));
        ui.SetVariable("/:GPTitle", foundText + "  |  " + xpText);
    }

    /// <summary>
    /// Straßenzeilen kommen ausschließlich aus Discovery, Wrackzeilen ausschließlich aus wreckinfo.
    /// Keine Vermischung – die FSCommands gapr_fill_crui:road / :trea wählen die Quelle.
    /// </summary>
    void RefreshProgressRows()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Straßen: Hawaii 4 + Ibiza 2 – strikt getrennt, keine Vermischung.
        // Goal = lokalisierter Straßen-Text (db_<lang>.cpr Hash 4233 Hawaii / 4232 Ibiza),
        // Platzhalter §PERC§ = Ziel (100), §NUM_AREA§ = Area, §PERC_DONE§ = aktueller Stand.
        _roadRows.Clear();
        var d = Discovery.Current;
        var areas = d?.BuildAreaRows();
        if (areas != null)
        {
            foreach (var (rows, hash) in new[] { (areas.Hawaii, 4233), (areas.Ibiza, 4232) })
            {
                string goalText = FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash) ?? "";
                foreach (var a in rows)
                {
                    string goal = goalText.Length > 0
                        ? goalText.Replace("§PERC§", "100%")
                                  .Replace("§NUM_AREA§", a.Index.ToString(inv))
                                  .Replace("§PERC_DONE§", a.Percent.ToString(inv) + "%")
                        : a.Goal;
                    _roadRows.Add(new AreaRow
                    {
                        Index = a.Index,
                        Goal = goal,
                        Island = a.Island,
                        Points = a.Points,
                        Found = a.Found,
                        Total = a.Total,
                        Percent = a.Percent,
                    });
                }
            }
        }

        // Wracks: nach Area gruppiert – eine Zeile pro Bereich, genau wie die Straßen.
        // Beide Inseln werden angezeigt (Hawaii 4 + Ibiza 2), auch wenn nur eine aktiv ist.
        // Goal = lokalisierter Wrack-Text (db_<lang>.cpr Hash 4237 Hawaii / 4236 Ibiza),
        // Island = echte Insel, Points = gefundene/alle Wracks der Area.
        _treasureRows.Clear();
        if (areas != null)
        {
            foreach (var (island, rows, hash) in new[]
            {
                ("hawai", areas.Hawaii, 4237),
                ("ibiza", areas.Ibiza, 4236),
            })
            {
                var counts = Wrecks.AreaCounts(island);
                string goalText = FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash)
                    ?? "§NUM_AREA§";
                foreach (var a in rows)
                {
                    counts.TryGetValue(a.Index - 1, out var c);
                    string goal = goalText
                        .Replace("§NUM_AREA§", a.Index.ToString(inv))
                        .Replace("§NB_WRECKS_FOUND§", c.found.ToString(inv));
                    _treasureRows.Add(new AreaRow
                    {
                        Index = a.Index,
                        Goal = goal,
                        Island = a.Island,
                        Points = c.found.ToString(inv) + "/" + c.total.ToString(inv),
                        Found = c.found,
                        Total = c.total,
                    });
                }
            }
        }

        // Cruising: Events und Aussichtspunkte – dieselben AreaRow-Zeilen aus Discovery/DBRewardXP.
        _eventRows.Clear();
        _photoRows.Clear();
        if (d != null)
        {
            _eventRows.AddRange(d.BuildEventRows());
            _photoRows.AddRange(d.BuildPhotoRows());
        }

        // Wettbewerb: eine der sechs Racing-Listen (Schule/Meisterschaft/Pokal/Duell/Multi/Instant).
        // Racing.cs liefert AreaRow-Zeilen aus Savegame + DBRewardXP – identisch zum Listenmechanismus oben.
        _racingRows.Clear();
        _racingRows.AddRange(Paradise.Game.Gameplay.Racing.Current?.Rows(_racingList) ?? System.Array.Empty<AreaRow>());

        // Collection/Social: dieselben AreaRow-Zeilen aus den neuen Modulen (Savegame + DBRewardXP).
        _collectRows.Clear();
        _collectRows.AddRange(Paradise.Game.Gameplay.Collectable.Current?.Rows(_collectList) ?? System.Array.Empty<AreaRow>());
        _socialRows.Clear();
        _socialRows.AddRange(Paradise.Game.Gameplay.Social.Current?.Rows(_socialList) ?? System.Array.Empty<AreaRow>());
    }

    /// <summary>Vom FSCommand gewählte Quelle setzen und eine bereits offene Liste sofort nachziehen.</summary>
    void SetProgressFill(ProgressFill mode)
    {
        _progressFill = mode;
        // Wreck-Command: frische Daten vom Wrecks-Modul holen (insertWrecks merkt sie für die Liste).
        if (mode == ProgressFill.Treasure) Wrecks.Current?.PushPauseMenu();
        RefreshProgressRows();
        PushRoadProgress();      // Roads-% bleibt auf der Hauptseite aktuell
        PushCruisingProgress();  // Event-/Foto-% und der richtige Discovery-Text
        UpdateProgressLists();
    }

    /// <summary>Wettbewerbs-Seite: gewählte Racing-Liste setzen und die offene gameprogress_fl neu füllen.</summary>
    void SetRacingFill(string key)
    {
        _racingList = key switch
        {
            "cham" => Paradise.Game.Gameplay.Racing.ListKind.Championship,
            "cups" => Paradise.Game.Gameplay.Racing.ListKind.Cup,
            "duel" => Paradise.Game.Gameplay.Racing.ListKind.Duel,
            "mult" => Paradise.Game.Gameplay.Racing.ListKind.Multi,
            "inst" => Paradise.Game.Gameplay.Racing.ListKind.Instant,
            _ => Paradise.Game.Gameplay.Racing.ListKind.School,
        };
        _progressFill = ProgressFill.Competition;
        Paradise.Game.Gameplay.Racing.Current?.Refresh();   // Savegame kann sich seit dem letzten Öffnen geändert haben
        PushRacingProgress();
        PushRacingHeadline();
        RefreshProgressRows();
        UpdateProgressLists();
    }

    /// <summary>Collection-Seite: gewählte coll:*-Liste setzen und die offene Liste neu füllen.</summary>
    void SetCollectFill(string key)
    {
        _collectList = key switch
        {
            "hair" => Paradise.Game.Gameplay.Collectable.ListKind.Haircuts,
            "hous" => Paradise.Game.Gameplay.Collectable.ListKind.Houses,
            "surg" => Paradise.Game.Gameplay.Collectable.ListKind.Surgery,
            "clot" => Paradise.Game.Gameplay.Collectable.ListKind.Clothes,
            "stic" => Paradise.Game.Gameplay.Collectable.ListKind.Stickers,
            _ => Paradise.Game.Gameplay.Collectable.ListKind.Vehicles,
        };
        _progressFill = ProgressFill.Collection;
        Paradise.Game.Gameplay.Collectable.Current?.Refresh();
        PushCollectionProgress();
        PushCollectionHeadline();
        RefreshProgressRows();
        UpdateProgressLists();
    }

    /// <summary>Social-Seite: gewählte soci:*-Liste setzen und die offene Liste neu füllen.</summary>
    void SetSocialFill(string key)
    {
        _socialList = key switch
        {
            "chas" => Paradise.Game.Gameplay.Social.ListKind.Chase,
            "club" => Paradise.Game.Gameplay.Social.ListKind.Club,
            "coop" => Paradise.Game.Gameplay.Social.ListKind.Coop,
            "gamb" => Paradise.Game.Gameplay.Social.ListKind.Gambling,
            _ => Paradise.Game.Gameplay.Social.ListKind.Friends,
        };
        _progressFill = ProgressFill.Social;
        Paradise.Game.Gameplay.Social.Current?.Refresh();
        PushSocialProgress();
        PushSocialHeadline();
        RefreshProgressRows();
        UpdateProgressLists();
    }

    /// <summary>
    /// /:PercentRoad und die Gebietswerte aus Discovery – pause_menu.cpp pushProgressData() schreibt
    /// /:PercentRoad aus profile.ExploredPercent (hier: aufgedeckte Straßen / alle Straßen).
    /// </summary>
    void PushRoadProgress()
    {
        var ui = _ui;
        var d = Discovery.Current;
        if (ui == null || d == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int total = Math.Max(1, d.RoadCount);
        int unlocked = Math.Clamp(d.UnlockedRoadCount, 0, total);
        ui.SetValue("/:PercentRoad", unlocked * 100 / total);
        ui.SetValue("/:AreaUnlockedRoads", unlocked);
        ui.SetVariable("/:AreaCruisingPtsCount", d.RoadPoints.ToString(inv));
        ui.SetVariable("/:AreaCruisingLevel", d.Level.ToString(inv));
        ui.SetVariable("/:AreaCruisingTitle", d.AreaName);
        ui.SetVariable("/:AreaName", d.AreaName);
    }

    /// <summary>
    /// Cruising-Hauptseite: Prozentzeilen für Events/Fotos und die geteilte Kopfzeile
    /// (pause_menu.cpp:1119) – dort steht der Discovery-Text, nicht der Wettbewerbs-Text.
    /// </summary>
    void PushCruisingProgress()
    {
        var ui = _ui;
        var d = Paradise.Game.Gameplay.Discovery.Current;
        if (ui == null || d == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        SetCruisePercent("/:PercentEvent", "/:m_Events_", 4169, "Events", _eventRows);
        SetCruisePercent("/:PercentPhotographer", "/:m_photographer_", 4170, "Photographer", _photoRows);

        var p = new UserProgress(TDU2Session.Save);
        const int maxCruising = 14500, maxCruisingLevel = 18;
        int points = Math.Clamp((int)p.CruisingPoints, 0, maxCruising);
        int level = Math.Clamp(d.Level, 0, maxCruisingLevel);
        int percent = Math.Min(100, points * 100 / maxCruising);
        string text = Loc(4178, "Entdeckungspunkte: §NB-PT§ / §MX-PT§ (§RATIO§%)")
            .Replace("§NB-PT§", points.ToString(inv)).Replace("§MX-PT§", maxCruising.ToString(inv))
            .Replace("§RATIO§", percent.ToString(inv));
        string grade = GradeTitle("DBCruisingLevel", level, Loc(3322, "Entdeckungslevel"));
        ui.SetVariable("/:GPTitle", text);
        ui.SetVariable("/:GPCruisingTitle", grade);
        ui.SetVariable("/:CruisingTitle", grade);
        ui.SetVariable("/:CruisingLevelCount", level.ToString(inv) + "/" + maxCruisingLevel.ToString(inv));
    }

    /// <summary>
    /// Prozentwert unten (numerisch → Flash zeigt "x%"), Originalname oben bleibt erhalten.
    /// </summary>
    void SetCruisePercent(string varName, string nameVar, int nameHash, string nameFallback, List<AreaRow> rows)
    {
        var ui = _ui!;
        int found = rows.Sum(r => r.Found), total = rows.Sum(r => r.Total);
        int percent = total > 0 ? Math.Clamp(found * 100 / total, 0, 100) : 0;
        ui.SetValue(varName, percent);
        ui.SetVariable(nameVar, Loc(nameHash, nameFallback));
    }

    /// <summary>
    /// Discovery-Prozente aus dem aktuellen Stand: Straßen (Discovery), Wracks (frisch vom Wrecks-Modul), Event-/Fotozeilen
    /// (für PushCruisingProgress). Dieselben Quellen wie beim Öffnen der Listen.
    /// </summary>
    void PushDiscoveryPercents()
    {
        Wrecks.Current?.PushPauseMenu();   // ruft InsertWrecks -> PushWrecks
        PushWrecks();
        PushRoadProgress();
        RefreshProgressRows();
    }

    /// <summary>Prozentwerte mit 0 initialisieren, damit unten "0%" statt "%" steht.</summary>
    void InitProgressPercents()
    {
        var ui = _ui;
        if (ui == null) return;
        foreach (var n in new[] { "PercentRoad", "PercentTreasure", "PercentEvent", "PercentPhotographer",
                                  "PercentSchool", "PercentDuel", "PercentChampionship", "PercentMulti",
                                  "PercentCup", "PercentInstant",
                                  "PercentVehicule", "PercentHair", "PercentHouse", "PercentSurgery", "PercentClothe", "PercentSticker",
                                  "PercentFriend", "PercentChase", "PercentClub", "PercentCoop", "PercentGamblin" })
            ui.SetValue("/:" + n, 0);
        ui.SetVariable("/:m_TreasureHunt", Loc(3245, "Treasure Hunt"));
        ui.SetVariable("/:m_Events_", Loc(4169, "Events"));
        ui.SetVariable("/:m_photographer_", Loc(4170, "Photographer"));
    }

    /// <summary>
    /// Wettbewerbs-Hauptseite (gapr:part:raci): die sechs Prozentwerte aus den echten
    /// Racing-Listen sowie Titel/Punkte/Level aus UserProgress. Analog zu PushWrecks().
    /// </summary>
    void PushRacingProgress()
    {
        var ui = _ui;
        var r = Paradise.Game.Gameplay.Racing.Current;
        if (ui == null || r == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var kinds = new (string Key, Paradise.Game.Gameplay.Racing.ListKind Kind)[]
        {
            ("School", Paradise.Game.Gameplay.Racing.ListKind.School),
            ("Duel", Paradise.Game.Gameplay.Racing.ListKind.Duel),
            ("Championship", Paradise.Game.Gameplay.Racing.ListKind.Championship),
            ("Multi", Paradise.Game.Gameplay.Racing.ListKind.Multi),
            ("Cup", Paradise.Game.Gameplay.Racing.ListKind.Cup),
            ("Instant", Paradise.Game.Gameplay.Racing.ListKind.Instant),
        };
        foreach (var (key, kind) in kinds)
        {
            int found = 0, total = 0;
            foreach (var row in r.Rows(kind)) { found += row.Found; total += row.Total; }
            ui.SetValue("/:Percent" + key, total > 0 ? Math.Clamp(found * 100 / total, 0, 100) : 0);
        }

        var p = new UserProgress(TDU2Session.Save);
        const int maxRacing = 10000, maxRacingLevel = 15, maxGlobal = 73;
        int points = Math.Clamp((int)p.RacingPoints, 0, maxRacing);
        int level = Math.Clamp((int)p.RacingLevel, 0, maxRacingLevel);
        int global = Math.Clamp((int)p.GlobalLevel, 0, maxGlobal);
        int percent = Math.Min(100, points * 100 / maxRacing);
        ui.SetValue("/:RacingPoints", percent);
        ui.SetValue("/:RacingLvlPoints", maxRacing);
        ui.SetVariable("/:RacingLevelCount", level.ToString(inv) + "/" + maxRacingLevel.ToString(inv));
        ui.SetValue("/:IsRacingGaugeFull", percent >= 100);
        ui.SetValue("/:GlobalLevel", global);
        ui.SetValue("/:GlobalPoints", Math.Min(100, global * 100 / maxGlobal));
        ui.SetValue("/:GlobalLvlPoints", maxGlobal);

        string grade = RacingGradeTitle(level);
        ui.SetVariable("/:RacingTitle", grade);
        ui.SetVariable("/:GPRacingTitle", grade);
        ui.SetVariable("/:GPTitleRacing", grade);
        ui.SetVariable("/:GPRacingText", grade);
    }

    /// <summary>
    /// Geteilte Kopfzeile der Wettbewerbs-Seite (pause_menu.cpp:1119): dort steht die
    /// Punktzeile, nicht der Wrack-Text aus PushWrecks().
    /// </summary>
    void PushRacingHeadline()
    {
        var ui = _ui;
        if (ui == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var p = new UserProgress(TDU2Session.Save);
        const int maxRacing = 10000;
        int points = Math.Clamp((int)p.RacingPoints, 0, maxRacing);
        int percent = Math.Min(100, points * 100 / maxRacing);
        string text = Loc(4174, "Wettbewerbspunkte: §NB-PT§ / §MX-PT§ (§RATIO§%)")
            .Replace("§NB-PT§", points.ToString(inv)).Replace("§MX-PT§", maxRacing.ToString(inv))
            .Replace("§RATIO§", percent.ToString(inv));
        ui.SetVariable("/:GPTitle", text);
    }

    /// <summary>Grade-Titel zum Level aus DBRacingLevel (_grade_hc), wie pause_menu.cpp:1027.</summary>
    static string RacingGradeTitle(int level) => GradeTitle("DBRacingLevel", level, Loc(3325, "Wettbewerb"));

    void PushCollectionProgress()
    {
        var ui = _ui;
        var c = Paradise.Game.Gameplay.Collectable.Current;
        if (ui == null || c == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Prozentwerte je Unterseite (falls Flash-Variablen existieren)
        ui.SetValue("/:PercentVehicule", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Vehicles));
        ui.SetValue("/:PercentHair", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Haircuts));
        ui.SetValue("/:PercentHouse", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Houses));
        ui.SetValue("/:PercentSurgery", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Surgery));
        ui.SetValue("/:PercentClothe", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Clothes));
        ui.SetValue("/:PercentSticker", PercentOf(c, Paradise.Game.Gameplay.Collectable.ListKind.Stickers));
        var p = new UserProgress(TDU2Session.Save);
        const int max = 10000, maxLvl = 15;
        int pts = Math.Clamp((int)p.CollectionPoints, 0, max);
        int level = Math.Clamp((int)p.CollectionLevel, 0, maxLvl);
        int pct = Math.Min(100, pts * 100 / max);
        ui.SetValue("/:CollectionPoints", pct);
        ui.SetValue("/:CollectionLvlPoints", max);
        ui.SetVariable("/:CollectionLevelCount", level.ToString(inv) + "/" + maxLvl.ToString(inv));
        ui.SetValue("/:IsCollectionGaugeFull", pct >= 100);
        string grade = GradeTitle("DBCollectionLevel", level, Loc(3322, "Entdeckungslevel"));
        ui.SetVariable("/:CollectionTitle", grade);
        ui.SetVariable("/:GPCollectionTitle", grade);
        ui.SetVariable("/:GPTitleCollection", grade);
    }

    void PushCollectionHeadline()
    {
        var ui = _ui;
        if (ui == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var p = new UserProgress(TDU2Session.Save);
        const int max = 10000;
        int pts = Math.Clamp((int)p.CollectionPoints, 0, max);
        int pct = Math.Min(100, pts * 100 / max);
        string text = Loc(4176, "Collection points: §NB-PT§ / §MX-PT§ (§RATIO§%)")
            .Replace("§NB-PT§", pts.ToString(inv)).Replace("§MX-PT§", max.ToString(inv))
            .Replace("§RATIO§", pct.ToString(inv));
        // GPTitleCollection ist der Titel (wie GPTitleRacing) – nicht mit der Punktzeile überschreiben.
        ui.SetVariable("/:GPTitle", text);
    }

    void PushSocialProgress()
    {
        var ui = _ui;
        var s = Paradise.Game.Gameplay.Social.Current;
        if (ui == null || s == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Best-effort Prozentwerte je Unterseite
        ui.SetValue("/:PercentFriend", PercentOf(s, Paradise.Game.Gameplay.Social.ListKind.Friends));
        ui.SetValue("/:PercentChase", PercentOf(s, Paradise.Game.Gameplay.Social.ListKind.Chase));
        ui.SetValue("/:PercentClub", PercentOf(s, Paradise.Game.Gameplay.Social.ListKind.Club));
        ui.SetValue("/:PercentCoop", PercentOf(s, Paradise.Game.Gameplay.Social.ListKind.Coop));
        ui.SetValue("/:PercentGamblin", PercentOf(s, Paradise.Game.Gameplay.Social.ListKind.Gambling));
        var p = new UserProgress(TDU2Session.Save);
        const int max = 10000, maxLvl = 15;
        int pts = Math.Clamp((int)p.SocialPoints, 0, max);
        int level = Math.Clamp((int)p.SocialLevel, 0, maxLvl);
        int pct = Math.Min(100, pts * 100 / max);
        ui.SetValue("/:SocialPoints", pct);
        ui.SetValue("/:SocialLvlPoints", max);
        ui.SetVariable("/:SocialLevelCount", level.ToString(inv) + "/" + maxLvl.ToString(inv));
        ui.SetValue("/:IsSocialGaugeFull", pct >= 100);
        string grade = GradeTitle("DBSocialLevel", level, Loc(3322, "Entdeckungslevel"));
        ui.SetVariable("/:SocialTitle", grade);
        ui.SetVariable("/:GPSocialTitle", grade);
        ui.SetVariable("/:GPTitleSocial", grade);
    }

    void PushSocialHeadline()
    {
        var ui = _ui;
        if (ui == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var p = new UserProgress(TDU2Session.Save);
        const int max = 10000;
        int pts = Math.Clamp((int)p.SocialPoints, 0, max);
        int pct = Math.Min(100, pts * 100 / max);
        string text = Loc(4180, "Social points: §NB-PT§ / §MX-PT§ (§RATIO§%)")
            .Replace("§NB-PT§", pts.ToString(inv)).Replace("§MX-PT§", max.ToString(inv))
            .Replace("§RATIO§", pct.ToString(inv));
        // GPTitleSocial ist der Titel (wie GPTitleRacing) – nicht mit der Punktzeile überschreiben.
        ui.SetVariable("/:GPTitle", text);
    }

    static int PercentOf(object mod, object kind)
    {
        try
        {
            var t = mod.GetType();
            var rows = (System.Collections.IEnumerable)t.GetMethod("Rows")!.Invoke(mod, new[] { kind })!;
            int percent = 0;
            foreach (var row in rows)
            {
                var rt = row.GetType();
                // Detailzeile = Fortschritt zum nächsten Meilenstein (von Collectable.Row.Percent),
                // nicht Punkte-Summe: sonst stünde bei 3 von 5 Teilen weiterhin 0%.
                percent = Math.Max(percent, (int)rt.GetField("Percent").GetValue(row)!);
            }
            return Math.Clamp(percent, 0, 100);
        }
        catch { return 0; }
    }

    /// <summary>Grade-Titel aus der jeweiligen Skill-Tabelle (Racing/Cruising).</summary>
    static string GradeTitle(string table, int level, string fallback)
    {
        try
        {
            var db = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(
                Path.Combine(FlashHost.BnkDir, "database", "db_data.cpr"))));
            int hash = 0, best = -1;
            foreach (var e in db.Get(table)?.Items ?? new List<XmbNode>())
            {
                int lv = e.I("_level", -1);
                if (lv <= level && lv > best) { best = lv; hash = e.I("_grade_hc", 0); }
            }
            if (hash != 0) return Loc(hash, fallback);
        }
        catch (Exception) { }
        return fallback;
    }

    // ------------------------------------------------------------------ Öffnen / Schließen
    public void Open()
    {


        var ui = _ui;
        if (ui == null || _open) return;
        _open = true;
        _clockShown = -1; _clockDay = -1;
        _raceMode = FindRace(out _raceRestart, out _raceQuit);
        ui.SetValue("/:WhatGameMode", _raceMode ? 2 : 1);   // 1 Freie Fahrt, 2 Rennen (Fortsetzen/Neu starten/…/Beenden)
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetVariable("/:MenuType", "pc");
        ui.SetValue("/:IsPC_X360", true);
        ui.SetValue("/:IsCasino", 0);
        ui.SetValue("/:isCasinoStandalone", 0);
        ui.SetValue("/:IsBootCasino", false);
        ui.SetValue("/:IsInOptions", false);
        ui.SetValue("/:ListPlayerCardOpened", false);
        ui.SetValue("/:Input", true);
        ui.ForceVisible("/pause_menu/data_extd/objective", false);
        foreach (var n in Unlocked) ui.SetValue("/:" + n, false);
        foreach (var n in Locked) ui.SetValue("/:" + n, true);
        ui.SetValue("/:LockSnapshot", !PhotoMode.CanTake(_ctx, _raceMode));
        if (_raceMode) { ui.SetValue("/:LockRestart", false); ui.SetValue("/:LockQuit", false); }

        ui.SetValue("/:LockEditor", StoryProgress.Get(TDU2Session.Nick) < 11);
        ui.SetVariable("/:NbNewsUnread", "0");
        ui.SetVariable("/:Money", TDU2Session.LoggedIn
            ? TDU2Session.Save.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $"
            : "999 999 999 $");
        if (TDU2Session.LoggedIn)
        {
            ui.SetVariable("/:CurrentProfile", TDU2Session.Nick);
            ui.SetVariable("/:NbCars", TDU2Session.OwnedCars.Count.ToString());
        }
        else ui.SetVariable("/:NbCars", "1");
        ui.SetVariable("/:NbHouses", "0");
        if (TDU2GameData.CarName.Length > 0) ui.SetVariable("/:VehicleName", TDU2GameData.CarName);
        // Insel/Gebiet für die Karte (initMap: erster Buchstabe h = Hawaii, i = Ibiza; Gebiete h1..h4, i1..i2)
        string area = CurrentArea() ?? (IsHawaii() ? "h1" : "i1");
        ui.SetVariable("/:CurrentArea", area);
        ui.SetVariable("/:AvatarName", TDU2GameData.PlayerName);
        ui.SetVariable("/:IsOnline", TDU2GameData.Online ? "1" : "0");
        ui.SetVariable("/:IsLoggedIn", TDU2Session.LoggedIn ? "1" : "0");
        // Ausweis: Flagge = Bild Ländernummer (Zahl), Online-Symbol über state_online, Titel NumTitle
        ui.SetNumber("/:Nationality", TDU2GameData.NationalityId);
        ui.SetNumber("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/flag", TDU2GameData.NationalityId);
        ui.SetNumber("/:state_online", TDU2GameData.Online ? 1 : 0);
        ui.SetNumber("/:NumTitle", 1);
        InitProgressPercents();   // 0 statt leerer Prozentzeilen
        PushWrecks();             // Wreck-Anzeige und Level/XP beim Öffnen aus dem gemerkten Stand
        PushRoadProgress();       // Roads-% auf der Hauptseite (/:PercentRoad)
        RefreshProgressRows();    // Event-/Foto-Zeilen für die Prozentwerte
        PushCruisingProgress();   // Event-/Foto-% und Discovery-Text
        PushRacingProgress();     // Wettbewerbs-% und Titel auf der Hauptseite

        ui.Visible = true;
        bool ok = ui.GotoLabel("/", "pause_in", true);
        Log.Info($"Pausemenü offen (Gebiet {area}, pause_in={ok})");
        if (_ctx.Vehicles != null) _ctx.Vehicles.Frozen = true;   // Unity: Time.timeScale = 0
        AudioEngine.Paused = true;                                  // Unity: AudioListener.pause = true
        ui.ForceVisible("/options/controls_menu/content_menu/pc/binder_key/#2", false);
    }

    public void Close()
    {
        _escAt = -1f;
        if (!_open) return;
        _open = false;
        PhotoMode.End();   // Notausgang/Schließen während des Fotomodus: Kamera zurück
        if (_ui != null) { _ui.Visible = false; _ui.GotoLabel("/", "default", false); }
        if (_ctx.Vehicles != null) _ctx.Vehicles.Frozen = false;
        AudioEngine.Paused = false;
        Log.Info("Pausemenü geschlossen");
    }

    // ------------------------------------------------------------------ Jedes Bild
    public void Update(float dt)
    {
        _clock += dt;
        var ui = _ui;
        if (ui != null) UpdateOwnPhoto();   // Unity: jedes Bild, sobald der Film geladen ist
        if (!_open || ui == null) return;
        UpdateClock();
        UpdateMapMarker();
        UpdatePlayerLists();
        if (UpdateBinder()) return;   // "Taste zuweisen" wartet auf eine Taste
        if (_pendingDialog) AnswerDialog();
        if (Paradise.Game.Gamemodes.FlashDialog.Active) return;   // Bestätigungsbox offen: Tasten gehören ihr
        // Fotomodus: Kamera/Aufnahme; in der Konsole gehören die Richtungen der Kamera, A/B/X gehen weiter an den Film
        bool photoCam = PhotoMode.Update(dt);

        // Texteingabe (setInputFocus, z.B. "Spieler suchen"): Buchstaben gehören dem Feld, nur Enter bestätigt, ESC bricht ab
        bool typing = ui.InputFocus != null;
        if (!typing && !photoCam)
        {
            // Menü-Belegung (Game/Config/KeyConfig.cs, Unity TDU2Controls "menu_*")
            // Tasten-Buttons des Films zuerst, sonst Move (Gamemodes/Utils/FlashNav, wie der Flash Player des Spiels)
            FlashNav.Directions(ui);
        }
        // Texteingabe: wie Unity Return/KeypadEnter/JoystickButton0 (Buchstaben gehören dem Textfeld),
        // sonst die belegbare Bestätigungstaste inkl. Pad A (Unity TDU2PauseMenu.cs).
        if (typing ? TDU2Controls.RawConfirmPressed() : TDU2Controls.ConfirmPressed()) FlashNav.Send(ui, FlashKey.Confirm);
        bool esc = _ctx.Pressed(VK_ESCAPE);
        bool back = !typing && TDU2Controls.BackPressed();
        if (esc || back)
        {
            // Zurück; reagiert der Film nicht (keine B-Funktion), Menü direkt schließen – nie im Pausenzustand hängen bleiben
            if (!FlashNav.Send(ui, FlashKey.Back)) { Log.Info("Pausemenü: B nicht verarbeitet -> schließen"); Close(); return; }
            if (esc && !PhotoMode.Active) _escAt = _clock;   // im Fotomodus führt B zurück ins Pausemenü, kein paou
        }
        // ESC im Hauptmenü muss nach der Schließ-Animation "paou" liefern; blockiert der Film, trotzdem schließen
        // (im Optionsmenü führt ESC nur zurück und bleibt offen)
        if (_escAt > 0f && _clock - _escAt > 2f)
        {
            _escAt = -1f;
            var inOpt = ui.GetVariable("/:IsInOptions");
            if (!string.Equals(inOpt, "true", StringComparison.OrdinalIgnoreCase) && inOpt != "1") { Log.Info("Pausemenü: ESC ohne paou -> schließen"); Close(); }
        }
        if (_ctx.Pressed(VK_F10)) Close();   // Notausgang
        if (!typing && FlashNav.Pressed(FlashKey.X)) FlashNav.Send(ui, FlashKey.X);
        if (!typing && FlashNav.Pressed(FlashKey.Y)) FlashNav.Send(ui, FlashKey.Y);
    }

    // ------------------------------------------------------------------ FSCommands
    /// <summary>
    /// Die FSCommands, die die sichtbare Cruising-Liste umschalten:
    ///   :road -> Straßen, :trea -> Wracks, :even -> Events, :phot -> Aussichtspunkte
    /// Kein Raten, kein Substring – nur diese Strings (plus der Flash-Form gapr:flli: mit crui:* als Argument).
    /// </summary>
    static string? CruiseFillMode(string cmd, string args)
    {
        string c = (cmd ?? "").Trim();
        string a = (args ?? "").Trim();
        string both = a.Length == 0 ? c : c + ":" + a;
        var o = StringComparison.OrdinalIgnoreCase;

        if (string.Equals(c, "gapr_fill_crui:trea", o) || string.Equals(both, "gapr_fill_crui:trea", o)) return "trea";
        if (string.Equals(c, "gapr_fill_crui:road", o) || string.Equals(both, "gapr_fill_crui:road", o)) return "road";
        if (string.Equals(c, "gapr_fill_crui:even", o) || string.Equals(both, "gapr_fill_crui:even", o)) return "even";
        if (string.Equals(c, "gapr_fill_crui:phot", o) || string.Equals(both, "gapr_fill_crui:phot", o)) return "phot";

        // Direktform (cmd enthält schon die Auswahl).
        if (string.Equals(c, "gapr:flli:crui:trea", o) || string.Equals(both, "gapr:flli:crui:trea", o)) return "trea";
        if (string.Equals(c, "gapr:flli:crui:road", o) || string.Equals(both, "gapr:flli:crui:road", o)) return "road";
        if (string.Equals(c, "gapr:flli:crui:even", o) || string.Equals(both, "gapr:flli:crui:even", o)) return "even";
        if (string.Equals(c, "gapr:flli:crui:phot", o) || string.Equals(both, "gapr:flli:crui:phot", o)) return "phot";

        // main.bnk sendet auch gapr:flli: mit crui:trea / crui:road als Argument.
        if (string.Equals(c, "gapr:flli:", o) || string.Equals(c, "gapr:flli", o))
        {
            if (string.Equals(a, "crui:trea", o)) return "trea";
            if (string.Equals(a, "crui:road", o)) return "road";
            if (string.Equals(a, "crui:even", o)) return "even";
            if (string.Equals(a, "crui:phot", o)) return "phot";
        }
        return null;
    }

    /// <summary>
    /// FSCommands der Wettbewerbs-Seite: gapr:flli:raci:scho|cham|cups|duel|mult|inst
    /// (und die Flash-Form gapr:flli: mit raci:* als Argument). Liefert den Listenschlüssel.
    /// </summary>
    static string? RacingFillMode(string cmd, string args)
    {
        string c = (cmd ?? "").Trim();
        string a = (args ?? "").Trim();
        string both = a.Length == 0 ? c : c + ":" + a;
        var o = StringComparison.OrdinalIgnoreCase;
        string[] keys = { "scho", "cham", "cups", "duel", "mult", "inst" };
        foreach (string key in keys)
        {
            string direct = "gapr:flli:raci:" + key;
            string legacy = "gapr_fill_raci:" + key;
            if (string.Equals(c, direct, o) || string.Equals(both, direct, o) ||
                string.Equals(c, legacy, o) || string.Equals(both, legacy, o) ||
                string.Equals(a, "raci:" + key, o)) return key;
        }
        return null;
    }

    /// <summary>FSCommands der Collection-Seite: gapr:flli:coll:vehi|hair|hous|surg|clot|stic.</summary>
    static string? CollectFillMode(string cmd, string args)
    {
        string c = (cmd ?? "").Trim(), a = (args ?? "").Trim();
        string both = a.Length == 0 ? c : c + ":" + a;
        var o = StringComparison.OrdinalIgnoreCase;
        foreach (string key in new[] { "vehi", "hair", "hous", "surg", "clot", "stic" })
        {
            string direct = "gapr:flli:coll:" + key, legacy = "gapr_fill_coll:" + key;
            if (string.Equals(c, direct, o) || string.Equals(both, direct, o) ||
                string.Equals(c, legacy, o) || string.Equals(both, legacy, o) ||
                string.Equals(a, "coll:" + key, o)) return key;
        }
        return null;
    }

    /// <summary>FSCommands der Social-Seite: gapr:flli:soci:frie|chas|club|coop|gamb.</summary>
    static string? SocialFillMode(string cmd, string args)
    {
        string c = (cmd ?? "").Trim(), a = (args ?? "").Trim();
        string both = a.Length == 0 ? c : c + ":" + a;
        var o = StringComparison.OrdinalIgnoreCase;
        foreach (string key in new[] { "frie", "chas", "club", "coop", "gamb" })
        {
            string direct = "gapr:flli:soci:" + key, legacy = "gapr_fill_soci:" + key;
            if (string.Equals(c, direct, o) || string.Equals(both, direct, o) ||
                string.Equals(c, legacy, o) || string.Equals(both, legacy, o) ||
                string.Equals(a, "soci:" + key, o)) return key;
        }
        return null;
    }

    void OnCommand(string cmd, string args)
    {
        var ui = _ui;
        if (GameMode.Current == Gamemode.GAME_MODE_LOGIN) return;   // Befehle des Profilmenüs behandelt der Login
        LastCommand = cmd + (string.IsNullOrEmpty(args) ? "" : " " + args);
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("sndp:")) return;                 // Menü-Sounds (FrontendSounds)
        if (ui != null && PhotoMode.Command(c, args, _ctx, ui, Loc, CurrentArea)) return;   // Fotomodus (phmo/phsa/phop, exe MngPhotoMode)
        if (c == "dbxo") { _pendingDialog = true; return; }
        if (c.StartsWith("mylf:scrl:")) return;            // Scrollbalken der Liste: behandelt VmfPlayer
        if (c == "pcrd:open") { OpenPlayerCard(); return; }  // Spielerkarte: Daten liefern
        if (c == "plli") { TDU2GameData.RequestRecent(); ReloadPlayerLists(); return; }  // Spielerliste (Reiter) geöffnet
        if (c == "psch:vali") { SearchPlayer(); return; }  // "Spieler suchen": Name bestätigt
        if (c == "psch:canc") return;
        // Wettbewerb: Seiten-Command wählt die RennAkademie als Standardliste, flli:* die Unterliste.
        if (c == "gapr:part:raci" || c == "gapr:part.raci") { PushRacingProgress(); SetRacingFill("scho"); return; }
        string? raceFill = RacingFillMode(c, args);
        if (raceFill != null) { SetRacingFill(raceFill); return; }
        // Collection/Social: Seiten-Command wählt die erste Unterliste, flli:* die Unterliste.
        if (c == "gapr:part:coll" || c == "gapr:part.coll") { SetCollectFill("vehi"); return; }
        string? collectFill = CollectFillMode(c, args);
        if (collectFill != null) { SetCollectFill(collectFill); return; }
        if (c == "gapr:part:soci" || c == "gapr:part.soci") { SetSocialFill("frie"); return; }
        string? socialFill = SocialFillMode(c, args);
        if (socialFill != null) { SetSocialFill(socialFill); return; }
        string? fill = CruiseFillMode(c, args);
        if (fill != null)
        {
            // FSCommand ausgewertet – Quelle umschalten und offene Liste sofort neu füllen.
            var mode = fill switch
            {
                "road" => ProgressFill.Road,
                "trea" => ProgressFill.Treasure,
                "even" => ProgressFill.Events,
                "phot" => ProgressFill.Photos,
                _ => ProgressFill.Road,
            };
            SetProgressFill(mode);
            return;
        }

        if (HandleControlsCommand(c)) return;                // Steuerung (Tastenbelegung)
        if (HandleFriendCommand(c)) return;                  // Freund hinzufügen / entfernen (Spielerkarte)
        if (HandlePartyCommand(c)) return;                   // Beitreten / Einladen (Spielerkarte, Party)
        if (_options != null && _ui != null && _options.Handle(_ui, c)) return;   // Optionsseiten
        switch (c)
        {
            case "gapr:open":
                var p = new UserProgress(TDU2Session.Save);
                InitProgressPercents();
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/flag", TDU2GameData.NationalityId);
                // maskLevel.gotoAndStop bekommt einen Prozentwert (0..100), keine Rohpunkte.
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/social_gge/maskLevel", (int)Math.Clamp(p.SocialPoints * 100 / 10000, 0, 100));
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/collection_gge/maskLevel", (int)Math.Clamp(p.CollectionPoints * 100 / 10000, 0, 100));
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/cruising_gge/maskLevel", (int)Math.Clamp(p.CruisingPoints * 100 / 14500, 0, 100));
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/racing_gge/maskLevel", (int)Math.Clamp(p.RacingPoints * 100 / 10000, 0, 100));
                ui.SetFrame("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/global_gge/maskLevel", (int)Math.Clamp(p.GlobalLevel * 100 / 73, 0, 100));
                ui.SetText("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/progression/global_gge:gradeLevel", p.GlobalLevel.ToString());
                // InitProgressPercents hat alle Prozente auf 0 gesetzt: Discovery-Seite (Straßen, Wracks, Events, Fotos)
                // sofort wieder füllen – sonst stand dort 0 %, bis eine der Listen einmal geöffnet war.
                PushDiscoveryPercents();
                PushCruisingProgress();
                PushRacingProgress();
                // Alle vier Gauges vor dem ersten Tab-Klick füllen – sonst zeigt Collection/Social
                // erst nach dem Listenzugriff den richtigen Wert (initGauges liesst die Variablen).
                PushCollectionProgress();
                PushSocialProgress();
                Log.Info($"SetLevel(racing:{p.RacingPoints} social:{p.SocialPoints} collection:{p.CollectionPoints} cruising:{p.CruisingPoints} total:{p.TotalPoints} GlobalLevel:{p.GlobalLevel})");
                break;

            case "gapr:part.raci": //Racing
                break;

            case "gapr:part:coll":  //Collection (colon-Form)
            case "gapr:part.coll": //Collection (Punkt-Form aus main.bnk)
                SetCollectFill("vehi");
                break;

            case "gapr:part:crui":  //Discovery (colon-Form wie gapr:part:raci)
            case "gapr:part.crui":  //Discovery (Punkt-Form aus main.bnk)
                PushDiscoveryPercents();
                PushCruisingProgress();
                break;

            case "gapr:flli:crui:road": // freigeschaltete Straßen – über gemeinsamen Listenmechanismus
                SetProgressFill(ProgressFill.Road);
                break;

            case "gapr:flli:crui:even": // Veranstaltungen aus DBRewardXP
                SetProgressFill(ProgressFill.Events);
                break;

            case "gapr:flli:crui:phot": // Aussichtspunkte aus DBRewardXP
                SetProgressFill(ProgressFill.Photos);
                break;

            case "gapr:part:soci": //Social (colon-Form)
            case "gapr:part.soci": //Social (Punkt-Form aus main.bnk)
                SetSocialFill("frie");
                break;

            case "paou":           // Pause beendet (nach der Schließen-Animation)
                Close();
                break;
            case "goed":           // Menüpunkt "Editor" (valid_menu, exe 0x9ec1bb): nach der Pause in den Challenge-Editor
                // Die exe schließt das Pausemenü dabei selbst (kein paou: der Film sendet nach goed keins, das Menü blieb offen,
                // bis ESC kam – das war das lange Warten).
                GAME_MODE_PAUSE.Next = Gamemode.GAME_MODE_EDITOR;
                Close();
                break;
            case "rest" when _raceMode:   // Rennen neu starten
                RaceAction(_raceRestart);
                break;
            case "quit" when _raceMode:   // im Rennen: "Beenden" = Rennen abbrechen (nicht das Spiel)
            case "fain" when _raceMode:
                RaceAction(_raceQuit);
                break;
            case "exit:vali":      // "Spiel beenden" bestätigt
            case "quit":
            case "egam":
                Close();
                Log.Info("Pausemenü: Spiel beenden");
                _ctx.Quit();       // ScreenModule.Unload speichert und meldet ab (SessionAutosave.SaveAndLogout)
                break;
            default:
                Log.Debug("Pausemenü FSCommand: " + LastCommand);
                break;
        }
    }

    void AnswerDialog()
    {
        _pendingDialog = false;
        var ui = _ui!;
        Paradise.Game.Gamemodes.FlashDialog.Open(ui, ans => DeliverAnswer(ui, ans));
    }

    void DeliverAnswer(VmfPlayer ui, string ans)
    {
        string kase = ui.GetVariable("/:DgbxCase") ?? "";
        // Im Rennen: "Beenden"/"Neu starten" bestätigt -> direkt ausführen (die Antwortkette des Films kommt nicht immer an);
        // Anbieter, der beim Öffnen aktiv war (Rennschule, Meisterschaft …)
        if (_raceMode && ans == "a" && string.Equals(kase, "confirm_quit_race", StringComparison.OrdinalIgnoreCase)) { RaceAction(_raceQuit); return; }
        if (_raceMode && ans == "a" && string.Equals(kase, "confirm_restart", StringComparison.OrdinalIgnoreCase)) { RaceAction(_raceRestart); return; }
        var owners = ui.FunctionOwners("DialogBox");
        string? target = null;
        foreach (var o in owners) if (o.IndexOf("options", StringComparison.OrdinalIgnoreCase) >= 0) { target = o; break; }
        if (target == null && owners.Count > 0) target = owners[owners.Count - 1];
        Log.Info($"Pausemenü: Dialog '{kase}' -> {ans} ({target ?? "kein DialogBox"})");
        ui.SetVariable("/:PlayerAnswer", ans);
        if (target == null || !ui.CallClip(target, "DialogBox"))
        {
            ui.SetValue("/:Input", true);
            ui.SetValue("/:_buttons", true);
        }
        //This is why the Windows was still Open.
        if (kase == "confirm_exit_game" && ans == "a")
        {
            _ctx.Quit();
            ProcessEnd.Now();
        }
    }

    void UpdateClock()
    {
        var ui = _ui!;
        float h = WorldWeather.Hour;
        int hh = (int)MathF.Floor(h) % 24, mm = (int)MathF.Floor((h - MathF.Floor(h)) * 60f) % 60;
        int code = hh * 100 + mm;
        if (code != _clockShown)
        {
            _clockShown = code;
            ui.SetValue("/:HDecade", hh / 10); ui.SetValue("/:HUnit", hh % 10);
            ui.SetValue("/:MDecade", mm / 10); ui.SetValue("/:MUnit", mm % 10);
            ui.SetValue("/:SDecade", 0); ui.SetValue("/:SUnit", 0);
            ui.CallClip("/pause_menu/clock/time/disp", "setHour");
        }
        int day = WorldWeather.Night < 0.5f ? 1 : 0;
        if (day != _clockDay)
        {
            bool first = _clockDay < 0;
            _clockDay = day;
            ui.GotoLabel("/pause_menu/clock/time/day", day == 1 ? "on" : "off", !first);
            ui.GotoLabel("/pause_menu/clock/time/night", day == 1 ? "off" : "on", !first);
        }
    }

    List<(string label, (float x, float z)[] poly)>? _zones;
    string? _zonesFor;

    bool IsHawaii() => (_ctx.Terrain?.Streamer?.Index?.IslandName ?? "").StartsWith("h", StringComparison.OrdinalIgnoreCase);

    System.Numerics.Vector3? PlayerGamePos()
    {
        var vm = _ctx.Vehicles?.Manager;
        if (vm?.Current != null) return vm.PositionGame;
        var s = _ctx.Terrain?.Streamer; var cam = _ctx.Terrain?.Camera;
        return s != null && cam != null ? s.RenderToGame(cam.Position) : null;
    }

    void EnsureZones()
    {
        var idx = _ctx.Terrain?.Streamer?.Index;
        if (idx == null || _zonesFor == idx.LevelDir) return;
        _zonesFor = idx.LevelDir;
        _zones = new();
        try
        {
            string? f = string.IsNullOrEmpty(idx.LevelDir) ? null : Path.Combine(idx.LevelDir, "cruisingzones.bin");
            if (f == null || !GameFiles.Exists(f)) return;
            var b = GameFiles.ReadAllBytes(f);
            var polys = new List<List<(float, float)>>();
            var cur = new List<(float, float)>();
            var vals = new List<float>();
            void Flush()
            {
                for (int i = 0; i + 1 < vals.Count; i += 2) cur.Add((vals[i], vals[i + 1]));
                vals.Clear();
                if (cur.Count >= 3) polys.Add(cur);
                cur = new();
            }
            // ab Offset 36: Floats, Gruppen getrennt durch 0xEFBEADDE; Polygone = Gruppen mit >= 3 Punkten
            for (int o = 36; o + 4 <= b.Length; o += 4)
            {
                uint u = BitConverter.ToUInt32(b, o);
                if (u == 0xEFBEADDEu) { if (vals.Count > 0) Flush(); continue; }
                vals.Add(BitConverter.ToSingle(b, o));
            }
            if (vals.Count > 0) Flush();
            // Reihenfolge der Polygone -> Gebiets-Label (Lage der Plaketten h1..h4 / i1..i2 im Film)
            string[] labels = IsHawaii() ? new[] { "h4", "h1", "h3", "h2" } : new[] { "i1", "i2" };
            for (int i = 0; i < polys.Count && i < labels.Length; i++) _zones.Add((labels[i], polys[i].ToArray()));
        }
        catch (Exception ex) { Log.Warn("cruisingzones.bin: " + ex.Message); }
    }

    static bool InPolygon((float x, float z)[] poly, float px, float pz)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var c = poly[j];
            if ((a.z > pz) != (c.z > pz) && px < (c.x - a.x) * (pz - a.z) / (c.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>Gebiet (Flash-Label h1..h4 / i1..i2) an der Spielerposition; null außerhalb aller Gebiete.</summary>
    public string? CurrentArea()
    {
        var gp = PlayerGamePos();
        return gp == null ? null : AreaAt(gp.Value.X, gp.Value.Z);
    }

    /// <summary>Gebiet (h1..h4 / i1..i2) an einer Position der aktuellen Insel (Spielkoordinaten); null ohne Gebiete.
    /// Auch für die Garage (Fahrzeugliste/Platz wechseln: Gebiet des Hauses, Original /:CurHouseArea).</summary>
    public string? AreaAt(float px, float pz)
    {
        EnsureZones();
        if (_zones == null || _zones.Count == 0) return null;
        foreach (var z in _zones) if (InPolygon(z.poly, px, pz)) return z.label;
        // knapp außerhalb (Küste/Meer): nächstes Gebiet
        string? best = null; float bd = float.MaxValue;
        foreach (var z in _zones)
        {
            float cx = 0, cz = 0; foreach (var q in z.poly) { cx += q.x; cz += q.z; }
            cx /= z.poly.Length; cz /= z.poly.Length;
            float d = (cx - px) * (cx - px) + (cz - pz) * (cz - pz);
            if (d < bd) { bd = d; best = z.label; }
        }
        return best;
    }

    void UpdateMapMarker()
    {
        var ui = _ui!;
        var gp = PlayerGamePos();
        if (gp == null) return;
        const string loc = "/pause_menu/data_extd/map/island/location";
        var lure = ui.Find(loc + "/lure");
        if (lure == null) return;
        var r = ui.LocalBounds(lure);   // im Koordinatensystem von location (wie GetBounds im Spiel)
        if (!r.HasValue) return;
        bool hawaii = IsHawaii();
        float ax = (hawaii ? 11 : 7) * 6144f, az = (hawaii ? 9 : 6) * 6144f;
        ui.SetPosition(loc + "/circle", new Vector2(r.Value.width * gp.Value.X / ax, r.Value.height * gp.Value.Z / az));
    }

    static readonly HashSet<string> PlayerLists = new(StringComparer.OrdinalIgnoreCase) { "friends", "club_members", "current_players", "recent_players", "black_list" };
    readonly List<VmfPlayer.FlashList> _playerLists = new();
    readonly Dictionary<VmfPlayer.FlashList, List<PlayerInfo>> _listPlayers = new();
    float _nextListRefresh;
    static readonly string[] ProgressColumnVars = { "Status", "Position", "Goal", "Island", "Points" };

    static string[] ProgressColumns(int columns)
    {
        if (columns >= 5) return ProgressColumnVars;
        if (columns == 4) return new[] { ProgressColumnVars[0], ProgressColumnVars[2], ProgressColumnVars[3], ProgressColumnVars[4] };
        return new[] { ProgressColumnVars[0], ProgressColumnVars[2], ProgressColumnVars[4] };
    }
    readonly List<VmfPlayer.FlashList> _progressLists = new();
    List<AreaRow> ProgressSource => _progressFill switch
    {
        ProgressFill.Treasure => _treasureRows,
        ProgressFill.Events => _eventRows,
        ProgressFill.Photos => _photoRows,
        ProgressFill.Competition => _racingRows,
        ProgressFill.Collection => _collectRows,
        ProgressFill.Social => _socialRows,
        _ => _roadRows,
    };

    void OnListCreated(VmfPlayer.FlashList l)
    {
        if (string.Equals(l.Name, "controls_list", StringComparison.OrdinalIgnoreCase))
        {
            _controlsList = l;
            TDU2Controls.Load();
            FillControls(l);
            // wie Unity OnListCreated: Liste zeichnen und als geladen melden (sonst bleibt die Seite in der Ladeanimation)
            _ui?.ListRefresh(l);
            _ui?.ListLoaded(l);
            return;
        }
        if (string.Equals(l.Name, "gameprogress_fl", StringComparison.OrdinalIgnoreCase))
        {
            var gx = _ui!;
            RefreshProgressRows();
            FillProgressList(l);
            _progressLists.RemoveAll(x => x.Owner == l.Owner || x.Owner == null);
            _progressLists.Add(l);
            gx.ListRefresh(l);
            gx.ListLoaded(l);
            ApplyProgressLineStates(l);
            ShowProgressLines(l);
            return;
        }
        if (!PlayerLists.Contains(l.Name ?? "")) return;
        var ui = _ui!;
        TDU2GameData.RequestRecent();   // "Zuletzt gesehen" vom Server nachladen
        FillPlayers(l);
        foreach (var old in _playerLists.FindAll(x => x.Owner == l.Owner)) _listPlayers.Remove(old);
        _playerLists.RemoveAll(x => x.Owner == l.Owner || x.Owner == null);
        _playerLists.Add(l);
        ui.ListRefresh(l);
        ui.ListLoaded(l);
        ApplyPlayerLineStates(l);
    }

    void FillPlayers(VmfPlayer.FlashList l)
    {
        var players = TDU2GameData.ListFor(l.Name);   // Unity TDU2GameData.ListFor: Freunde, Club, Lobby, Zuletzt gesehen, Sperrliste
        _listPlayers[l] = players;
        l.ColumnVars = new[] { "PlayerName", "PlayerTitle", "GlobalLevel" };
        l.Rows.Clear();
        foreach (var p in players)
        {
            string title = p.CarName;
            if (l.Name == "recent_players" && !p.Online && p.LastSeen != default) title = (title.Length > 0 ? title + "  –  " : "") + p.LastSeen.ToString("dd.MM. HH:mm");
            l.Rows.Add(new[] { p.Nick, title, System.Math.Max(1, p.NbCars).ToString() });
        }
        if (l.Selected >= l.Rows.Count) { l.Selected = System.Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
    }

    void ApplyPlayerLineStates(VmfPlayer.FlashList l)
    {
        if (!_listPlayers.ContainsKey(l)) return;
        for (int i = 1; i <= l.ViewSize; i++) ApplyPlayerLine(l, i, l.Top + i - 1);
    }

    /// <summary>Rückruf der nativen Liste je Zeile (exe 0x9ef1e0) – nur Spielerlisten.</summary>
    void OnListLineDrawn(VmfPlayer.FlashList l, int line, int row)
    {
        if (_ui == null) return;
        if (_progressLists.Contains(l)) ApplyProgressLine(l, line, row);
        if (_listPlayers.ContainsKey(l)) ApplyPlayerLine(l, line, row);
    }

    /// <summary>
    /// Die Liste wird nicht vom Film gefüllt (der erzeugt nur fakeList), sondern vom Spiel – und zwar
    /// je nach FSCommand aus genau einer Quelle: gapr_fill_crui:road -> Straßen aus Discovery,
    /// gapr_fill_crui:trea -> Wracks aus wreckinfo. Reihenfolge der setColumns: Status, Position,
    /// Goals, Island, Points. Die EditText-Felder lesen ../:Goal / ../:Island / ../:Points.
    /// </summary>
    void FillProgressList(VmfPlayer.FlashList l)
    {
        l.ColumnVars = ProgressColumns(l.Columns);
        l.Rows.Clear();
        foreach (var a in ProgressSource)
        {
            // Alle Areas sind sichtbar – kein lock/ausgrauen (auch ohne Funde).
            string state = "normal";
            switch (l.ColumnVars.Length)
            {
                case 5: l.Rows.Add(new[] { state, "", a.Goal ?? "", a.Island ?? "", a.Points ?? "" }); break;
                case 3: l.Rows.Add(new[] { state, a.Goal ?? "", a.Points ?? "" }); break;
                default: l.Rows.Add(new[] { state, a.Goal ?? "", a.Island ?? "", a.Points ?? "" }); break;
            }
        }
        if (l.Selected >= l.Rows.Count) { l.Selected = System.Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
    }

    /// <summary>Offene Progress-Liste nach Fund/Rücknahme/Import/FSCommand ohne erneutes Öffnen aktualisieren.</summary>
    void UpdateWreckLists() => UpdateProgressLists();

    void UpdateProgressLists()
    {
        if (_progressLists.Count == 0 || _ui == null) return;
        var ui = _ui;
        foreach (var l in _progressLists.ToArray())
        {
            if (l.Owner == null) { _progressLists.Remove(l); continue; }
            if (!ui.IsShown(l.Owner)) continue;
            FillProgressList(l);
            ui.ListRefresh(l);
            ui.ListLoaded(l);
            ApplyProgressLineStates(l);
            ShowProgressLines(l);
        }
    }

    void ApplyProgressLineStates(VmfPlayer.FlashList l)
    {
        if (_ui == null) return;
        for (int i = 1; i <= l.ViewSize; i++) ApplyProgressLine(l, i, l.Top + i - 1);
    }

    /// <summary>
    /// Wie gm_shop.cpp::fillSellLines(): für jede sichtbare Zeile SetLineState(n, normal|lock|empty)
    /// und danach displayLines("show") auf dem Listen-Root. Ohne displayLines zeichnet der Film nur
    /// die erste Zeile – genau der Fehler, den wir hatten.
    /// </summary>
    void ShowProgressLines(VmfPlayer.FlashList l)
    {
        var ui = _ui;
        if (ui == null || l.Owner == null) return;
        string owner = VmfPlayer.PathOf(l.Owner);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rows = ProgressSource;
        for (int i = 1; i <= l.ViewSize; i++)
        {
            int r = l.Top + i - 1;
            bool has = r >= 0 && r < rows.Count && !string.IsNullOrEmpty(rows[r].Goal);
            string state = !has ? "empty" : "normal";
            CallProgressClip(owner, "SetLineState", i.ToString(inv), state);
        }
        CallProgressClip(owner, "displayLines", "show");
    }

    /// <summary>Funktion im Film aufrufen – erst am Listen-Root, sonst an einem Clip, der sie definiert.</summary>
    void CallProgressClip(string owner, string function, params object[] args)
    {
        var ui = _ui;
        if (ui == null) return;
        if (ui.CallClip(owner, function, args)) return;
        foreach (var p in ui.FunctionOwners(function))
            if (ui.CallClip(p, function, args)) return;
    }

    /// <summary>
    /// Zeilenzustand wie im Retail: /:position, m_St_/m_Goals_/m_Island_/m_Points_ und content auf
    /// stop / stop_on (ohne lock – alle Zeilen sichtbar).
    /// </summary>
    void ApplyProgressLine(VmfPlayer.FlashList l, int line, int row)
    {
        var ui = _ui;
        if (ui == null || l.Owner == null) return;
        string op = VmfPlayer.PathOf(l.Owner);
        string lp = (op == "/" ? "" : op) + "/line" + line;
        var areas = ProgressSource;
        var a = row >= 0 && row < areas.Count ? areas[row] : null;
        if (a == null) return;
        bool complete = a.Complete;
        bool selected = l.SelectedLine == line;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // position steuert im Retail den Haken: erst komplett gefunden, nicht schon bei Telefunden.
        ui.SetValue(lp + ":position", complete ? 1 : 0);
        string state = "normal";   // keine Area ausgegraut – Texte immer sichtbar
        ui.SetVariable(lp + ":m_St_", state);
        ui.SetVariable(lp + ":m_Goals_", a.Goal);
        ui.SetVariable(lp + ":m_Island_", a.Island);
        ui.SetVariable(lp + ":m_Points_", a.Points ?? a.Found.ToString(inv) + "/" + a.Total.ToString(inv));
        // Die EditText-Felder in content lesen ../:Goal / ../:Island / ../:Points direkt von der Zeile.
        ui.SetVariable(lp + ":Goal", a.Goal);
        ui.SetVariable(lp + ":Island", a.Island);
        ui.SetVariable(lp + ":Points", a.Points ?? a.Found.ToString(inv) + "/" + a.Total.ToString(inv));

        // Keine gelockten Frames mehr: alle Zeilen nutzen stop/stop_on, damit die Texte sichtbar bleiben.
        // Die Checkbox shows nur bei komplett gefundenem Bereich den Haken.
        string contentFrame = selected ? "stop_on" : "stop";
        string checkFrame;
        bool checkOk;
        if (complete)
        {
            checkFrame = "check_on";
            checkOk = ui.PinLabel(lp + "/content/checkbox", checkFrame, true);
        }
        else
        {
            checkFrame = "check_off";
            ui.Unpin(lp + "/content/checkbox");
            checkOk = ui.GotoLabel(lp + "/content/checkbox", checkFrame, false);
        }
        bool contentOk = ui.PinLabel(lp + "/content", contentFrame, true);
    }

    /// <summary>
    /// Eine Spielerzeile wie das Spiel (exe 0x9ef1e0): content/forbidden aus, content/nationality = Bild Ländernummer (1..113,
    /// sonst 135), content/state online/offline, Foto in content/photo/lure. Bild/Label festgehalten (PinFrame/PinLabel), weil der
    /// Film die Zeilen-Clips beim Aufbau/Blättern neu anlegt – sonst fehlte die Flagge.
    /// </summary>
    void ApplyPlayerLine(VmfPlayer.FlashList l, int i, int r)
    {
        var ui = _ui!;
        if (!_listPlayers.TryGetValue(l, out var players) || r < 0 || r >= players.Count) return;
        string op = VmfPlayer.PathOf(l.Owner);
        string line = (op == "/" ? "" : op) + "/line" + i;
        var pl = players[r];
        ui.ForceVisible(line + "/content/forbidden", false);
        ui.PinLabel(line + "/content/state", pl.Online ? "online" : "offline");
        ui.PinFrame(line + "/content/nationality", pl.Nationality >= 1 && pl.Nationality <= 113 ? pl.Nationality : 135);   // Flagge = Bild Ländernummer
        // MyTDULife-Foto des Spielers (kommt per op "profile" nach, Version aus der Spielerliste)
        ui.SetClipTexture(line + "/content/photo/lure", pl.Photo);
        SetPhotoState(line + "/content/photo", pl.Photo != null, IsFemale(pl));
        // Profil noch nie geholt (Freunde/Club/Zuletzt gesehen kennen die Foto-Version nicht) -> wie beim Öffnen der Karte holen
        if (pl.Life == null || (pl.PhotoVer > 0 && (pl.Photo == null || pl.PhotoTexVer != pl.PhotoVer)))
            TDU2GameData.FetchLife(pl, _ => { if (_ui != null && _listPlayers.ContainsKey(l)) ApplyPlayerLineStates(l); });
    }

    /// <summary>Offene Spielerliste regelmäßig aktualisieren (Spieler kommen/gehen) – nur bei geändertem Inhalt neu zeichnen.</summary>
    void UpdatePlayerLists()
    {
        if (_playerLists.Count == 0 || _clock < _nextListRefresh) return;
        _nextListRefresh = _clock + 1f;
        var ui = _ui!;
        foreach (var l in _playerLists)
        {
            if (l.Owner == null || !ui.IsShown(l.Owner)) continue;
            string before = RowsKey(l);
            FillPlayers(l);
            if (RowsKey(l) == before) continue;
            ui.ListRefresh(l);
            ui.ListLoaded(l);
            ApplyPlayerLineStates(l);
        }
    }

    static string RowsKey(VmfPlayer.FlashList l)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in l.Rows) { foreach (var c in r) sb.Append(c).Append('|'); sb.Append('\n'); }
        return sb.ToString();
    }

    void ReloadPlayerLists()
    {
        var ui = _ui!;
        foreach (var l in _playerLists)
        {
            if (l.Owner == null) continue;
            FillPlayers(l);
            ui.ListRefresh(l);
            ui.ListLoaded(l);
            ApplyPlayerLineStates(l);
        }
    }

    /// <summary>Spielersuche (/search_player, _root.PlayerSearched): gefunden -> endLoading(), sonst timeOutLoading().</summary>
    void SearchPlayer()
    {
        var ui = _ui!;
        string name = (ui.GetVariable("/:PlayerSearched") ?? "").Trim();
        PlayerInfo? hit = null;
        // unter allen bekannten Spielern suchen (online, Freunde, Club, zuletzt gesehen)
        if (name.Length > 0)
            foreach (var ln in PlayerLists)
            {
                foreach (var p in TDU2GameData.ListFor(ln))
                    if (p != null && string.Equals(p.Nick, name, StringComparison.OrdinalIgnoreCase)) { hit = p; break; }
                if (hit != null) break;
            }
        if (hit != null) _cardPlayer = hit;
        // Wie im Original: "Nach Spieler suchen" schickt dem Spieler eine Freundschaftsanfrage (Erfolg = Dialog search_player_ok
        // "Freundschaftsanfrage gesendet"). Der Server kennt alle Konten – auch Spieler, die nie in einer Liste standen (Unity).
        if (TDU2Session.LoggedIn && name.Length > 0)
        {
            Friends.Request(name, r =>
            {
                bool ok = r != null && r.ok;
                Log.Info($"Pausemenü: Freundschaftsanfrage '{name}' -> {(ok ? r!.friendResult : (r?.error ?? "keine Antwort"))}");
                if (_ui == null) return;
                _ui.SetValue("/:PlayerFound", ok);
                _ui.CallClip("/search_player", ok ? "endLoading" : "timeOutLoading");
            });
            return;
        }
        ui.SetValue("/:PlayerFound", hit != null);
        ui.CallClip("/search_player", hit != null ? "endLoading" : "timeOutLoading");
        Log.Info($"Pausemenü: Spieler '{name}' {(hit != null ? "gefunden" : "nicht gefunden")}");
    }

    // ------------------------------------------------------------------ Spielerkarte (Unity TDU2PauseMenu.OpenPlayerCard)
    // A auf einer Zeile -> FSCommand "pcrd:open", Daten in _root.listAvatarName/listMoney/…, dann _root.Clip.IsCardLoaded = true.
    PlayerInfo? _cardPlayer;

    void OnListValidated(VmfPlayer.FlashList l)
    {
        _cardPlayer = null;
        if (l != null && _listPlayers.TryGetValue(l, out var players) && l.Selected >= 0 && l.Selected < players.Count) _cardPlayer = players[l.Selected];
    }

    string CardPath
    {
        get
        {
            string? card = _ui?.GetVariable("/:PlayerCardPath");
            return string.IsNullOrEmpty(card) ? "/players/player_card" : card;
        }
    }

    void OpenPlayerCard()
    {
        var ui = _ui!;
        var p = _cardPlayer;
        // Menü der Karte baut der Film nach _root.PlayerType (setMenu, main.bnk): "current" (Aktuelle Spieler) hat im Original nur
        // Link/Freund/Chat … – gewünscht sind dort Beitreten + Einladen auf Platz 1 und 2 wie bei Freunde/Zuletzt gesehen ("recent").
        if (string.Equals(ui.GetVariable("/:PlayerType"), "current", StringComparison.OrdinalIgnoreCase)) ui.SetVariable("/:PlayerType", "recent");
        ui.SetVariable("/:listAvatarName", p?.Nick ?? "");
        ui.SetNumber("/:listNationality", p != null && p.Nationality >= 1 && p.Nationality <= 113 ? p.Nationality : 135);   // unbekannt = 135 (exe 0xc09030)
        ui.SetNumber("/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/flag", p != null && p.Nationality >= 1 && p.Nationality <= 113 ? p.Nationality : 135);

        ui.SetVariable("/:listMoney", p != null ? p.Money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "");
        ui.SetVariable("/:listNbCars", (p?.NbCars ?? 0).ToString());
        ui.SetVariable("/:listNbHouses", "0");
        // MyTDULife: Smiley, Stimmungstext und Avatar-Foto (Foto/Profil kommen per op "profile" nach)
        ui.SetVariable("/:listMood", p != null && !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
        ui.SetVariable("/:listMoodTxt", p?.MoodTxt ?? "");
        ApplyCardPhoto(p);
        if (p != null) TDU2GameData.FetchLife(p, OnCardLife);
        ui.SetValue("/:listIsConnected", p != null && p.Online);
        ApplyCardFriendState(p);
        // Kreise (Wettbewerb/Sozial/Entdeckung/Sammlung) und Globales Level: Standard wie Unity PlayerCardStats.Default
        ui.SetValue("/:listGlobalLevel", 1);
        ui.SetValue("/:listGlobalPoints", 0);
        ui.SetValue("/:listGlobalLvlPoints", 0);
        ui.SetVariable("/:GlobalLevelCount", "1");
        // Wettbewerb: Ring und Level aus dem eigenen Spielstand (Racing.Points via UserProgress).
        var own = new UserProgress(TDU2Session.Save);
        ui.SetValue("/:listRacingPoints", own.LevelPercentFor(UserProgress.ProgressCategory.Racing));
        ui.SetValue("/:listSocialPoints", 0);
        ui.SetValue("/:listCruisingPoints", 0);
        ui.SetValue("/:listCollectionPoints", 0);
        ui.SetVariable("/:listRacingLevelCount", own.RacingLevel.ToString());
        ui.SetVariable("/:listCollectionLevelCount", "0");
        ui.SetValue(CardPath + ":IsCardLoaded", true);
        // Der Lader der Karte fragt "_root.Clip.IsCardLoaded" ab (Clip = Verweis auf das offene Menü)
        ui.SetValue("_root.Clip.IsCardLoaded", true);
    }

    // ------------------------------------------------------------------ Eigenes MyTDULife-Foto (Ausweis im Pausemenü / Spielfortschritt)
    static readonly string[] OwnPhotoClips = { "/pause_menu/data_extd/idcard/avatar_photo/lure", "/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/avatar_photo/lure" };
    Texture2D? _ownPhoto; int _ownPhotoVer = -1;

    void UpdateOwnPhoto()
    {
        var life = TDU2Session.Save?.myLife;
        int ver = life != null && !string.IsNullOrEmpty(life.photo) ? life.photoVer : 0;
        if (ver == _ownPhotoVer) return;
        _ownPhotoVer = ver;
        // altes Foto nicht sofort freigeben (Unity Destroy): der Film zeichnet es evtl. noch in diesem Bild
        _ownPhoto = ver > 0 ? TDU2GameData.DecodePhoto(life!.photo) : null;
        foreach (var c in OwnPhotoClips) _ui!.SetClipTexture(c, _ownPhoto);
    }

    /// <summary>Avatar-Foto des Spielers in die Karte (content/photo/lure); ohne Foto leer.</summary>
    void ApplyCardPhoto(PlayerInfo? p)
    {
        if (_ui == null) return;
        _ui.SetClipTexture(CardPath + "/content/photo/lure", p?.Photo);
        SetPhotoState(CardPath + "/content/photo", p != null && p.Photo != null, IsFemale(p));
    }

    /// <summary>MyTDULife ist angekommen: offene Karte desselben Spielers aktualisieren.</summary>
    void OnCardLife(PlayerInfo p)
    {
        if (_ui == null || p == null || p != _cardPlayer) return;
        _ui.SetVariable("/:listMood", !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
        _ui.SetVariable("/:listMoodTxt", p.MoodTxt ?? "");
        ApplyCardPhoto(p);
        var ci = _ui.Find(CardPath)?.Sprite;
        if (ci != null && _ui.IsShown(ci)) _ui.CallClip(CardPath, "setPlayerCard");
    }

    /// <summary>Foto-Clip wie im Spiel: "loaded", ohne Foto der Platzhalter nach Geschlecht ("empty_male"/"empty_female").</summary>
    void SetPhotoState(string photoClip, bool loaded, bool female)
    {
        var ui = _ui!;
        string want = loaded ? "loaded" : female ? "empty_female" : "empty_male";
        if (ui.Find(photoClip) != null && ui.Label(photoClip) == want) return;
        ui.PinLabel(photoClip, want);   // festhalten: Zeilen-/Karten-Clips werden vom Film neu angelegt
    }

    static bool IsFemale(PlayerInfo? p)
    {
        if (p == null || string.IsNullOrEmpty(p.Avatar)) return false;
        var c = Paradise.Core.Avatars.AvatarConfig.Decode(p.Avatar);
        return c != null && c.Sex == 1;
    }

    /// <summary>Spieler der Karte als aktueller Online-Eintrag (über den Namen); null = nicht online.</summary>
    PlayerInfo? LiveCardPlayer()
    {
        string? nick = _cardPlayer?.Nick;
        if (string.IsNullOrEmpty(nick)) nick = _ui?.GetVariable("/:listAvatarName");
        if (string.IsNullOrEmpty(nick)) return null;
        foreach (var o in TDU2GameData.Players.Values)
            if (string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) return o;
        return null;
    }

    /// <summary>Menü der Karte: IsFriend -> "Aus Freundesliste entfernen" statt "Zu Freunden hinzufügen"; gesperrt für sich selbst/ohne Server.</summary>
    void ApplyCardFriendState(PlayerInfo? p)
    {
        var ui = _ui;
        if (ui == null) return;
        string nick = p?.Nick ?? ui.GetVariable("/:listAvatarName") ?? "";
        bool self = string.Equals(nick, TDU2Session.Nick, StringComparison.OrdinalIgnoreCase);
        bool can = TDU2Session.LoggedIn && nick.Length > 0 && !self;
        bool friend = can && Friends.IsFriend(nick);
        ui.SetValue("/:IsFriend", friend);
        ui.SetValue("/:LockAddFriend", !can || friend || Friends.RequestSentTo(nick));
        ui.SetValue("/:LockAddToFriends", !can || friend || Friends.RequestSentTo(nick));
        ui.SetValue("/:LockDelFriend", !can || !friend);
        ui.SetValue("/:LockFriendRequest", !can);
    }

    bool HandleFriendCommand(string c)
    {
        bool add = c == "frre" || c == "pcrd:frre", del = c == "delf" || c == "pcrd:delf";
        if (!add && !del) return false;
        string? nick = _cardPlayer?.Nick;
        if (string.IsNullOrEmpty(nick)) nick = _ui?.GetVariable("/:listAvatarName");
        Log.Info($"Pausemenü: Freunde {(add ? "Anfrage an" : "entfernen")} '{nick}'");
        if (string.IsNullOrEmpty(nick)) return true;
        void Done(NetResponse? r)
        {
            if (_ui == null) return;
            ApplyCardFriendState(_cardPlayer);
            var ci = _ui.Find(CardPath)?.Sprite;
            if (ci != null && _ui.IsShown(ci)) _ui.CallClip(CardPath, "setMenu");   // Menü der Karte neu aufbauen
        }
        if (add) Friends.Request(nick!, Done); else Friends.Remove(nick!, Done);
        return true;
    }

    // Karte: "pcrd:pjoi" (Beitreten) bzw. join_player.joinPlayer() -> "join:wait" (Ladeanzeige wartet auf _root.CanJoinPlayer)
    // -> "pjoi"; Fehler: _root.JoinError + timeOutLoading() (Dialog player_join_fail). "pcrd:pinv" = Einladen.
    bool HandlePartyCommand(string c)
    {
        var ui = _ui!;
        var p = c == "join:wait" || c == "pjoi" || c == "pcrd:pjoi" || c == "pcrd:pinv" ? LiveCardPlayer() : _cardPlayer;
        switch (c)
        {
            case "join:wait":
                {
                    string err = "Spieler nicht gefunden.";
                    bool ok = p != null && p.Id != 0 && Party.CanJoin(p.Id, out err);
                    Log.Info($"Pausemenü: Beitreten {p?.Nick ?? "-"}: " + (ok ? "ok" : err));
                    if (ok) ui.SetValue("/:CanJoinPlayer", true);
                    else
                    {
                        ui.SetVariable("/:JoinError", err);
                        // Ladeanzeige der Beitreten-Funktion (/join_player); der letzte Besitzer von timeOutLoading war oft die
                        // Spielersuche -> deren Fehlerfenster ("none") statt player_join_fail mit JoinError
                        var owners = ui.FunctionOwners("timeOutLoading");
                        if (!ui.CallClip("/join_player", "timeOutLoading") && (owners.Count == 0 || !ui.CallClip(owners[owners.Count - 1], "timeOutLoading"))) ui.SetValue("/:Input", true);
                    }
                    return true;
                }
            case "join:canc":
                return true;
            case "pjoi":
            case "pcrd:pjoi":
                if (p != null && p.Id != 0)
                {
                    int id = p.Id;
                    Close();
                    Party.Join(id);
                }
                return true;
            case "pcrd:pinv":
                if (p == null || p.Id == 0) Log.Info("Pausemenü: " + (_cardPlayer?.Nick ?? "Spieler") + " ist nicht online – Einladung nicht möglich.");
                else if (p.Id == TDU2Session.PlayerId) Log.Info("Pausemenü: Du kannst dich nicht selbst einladen.");
                else Party.Invite(p.Id);
                return true;
        }
        return false;
    }

    // Liste je Reiter wie im Original (_root.WhatListe: "driving" Fahren, "walkmode" Laufen/Avatar, "menu" Menü).
    VmfPlayer.FlashList? _controlsList;
    List<TDU2Controls.Action> _shownControls = new();
    float _binderSince = -1f;

    void FillControls(VmfPlayer.FlashList l)
    {
        bool de = FlashHost.Language == "ge";
        var cat = TDU2Controls.CategoryOf(_ui?.GetVariable("/:WhatListe"));
        _shownControls = TDU2Controls.InCategory(cat);
        l.Rows.Clear();
        // dritte Spalte: Belegung am Gamepad/Lenkrad (folgt mit TDU2InputDevices)
        foreach (var a in _shownControls) l.Rows.Add(new[] { de ? a.Name : a.NameEn, TDU2Controls.BindText(a), TDU2InputDevices.BindingText(a.Id) });
        if (l.Selected >= l.Rows.Count) { l.Selected = 0; l.Top = 0; }
    }

    TDU2Controls.Action? SelectedControl()
    {
        var l = _controlsList;
        if (l == null || l.Selected < 0 || l.Selected >= _shownControls.Count) return null;
        return _shownControls[l.Selected];
    }

    /// <summary>Steht der Film zwischen den Bildmarken binder_in und binder_out ("Taste drücken …")?</summary>
    bool InBinder()
    {
        var o = _controlsList?.Owner;
        if (o == null || _ui == null || !_ui.IsShown(o)) return false;
        var lb = o.Timeline.Labels;
        return lb.TryGetValue("binder_in", out int a) && lb.TryGetValue("binder_out", out int b) && o.Frame >= a && o.Frame < b;
    }

    /// <summary>Wartet das Menü auf eine Taste: nächste Taste zuweisen (ESC = abbrechen). true = Eingabe verbraucht.</summary>
    bool UpdateBinder()
    {
        if (!InBinder()) { _binderSince = -1f; return false; }
        // erst nach dem Öffnen der Eingabe-Box zuweisen (die Taste, die sie geöffnet hat, zählt nicht)
        if (_binderSince < 0f) { _binderSince = _clock; TDU2InputDevices.BeginAxisCapture(); return true; }
        if (_clock - _binderSince < 0.15f) return true;
        var act = SelectedControl();
        // Gas/Bremse/Kupplung/Lenken: Pedal treten bzw. Lenkrad drehen belegt die Achse (Gerät + Achse + Richtung)
        if (act != null && _controlsList != null && TDU2InputDevices.IsAnalogAction(act.Id) && TDU2InputDevices.CaptureAxis(act.Id, out var axisText))
        {
            _binderSince = -1f;
            FillControls(_controlsList);
            _ui!.ListRefresh(_controlsList);
            Log.Info($"Steuerung: Achse {axisText} -> {act.Id}");
            _ui.GotoLabel(VmfPlayer.PathOf(_controlsList.Owner), "binder_out", true);
            return true;
        }
        var key = TDU2Controls.AnyKeyDown();
        if (key == 0) return true;
        _binderSince = -1f;
        if (key != VK_ESCAPE && act != null && _controlsList != null)
        {
            TDU2Controls.Bind(act, key);
            FillControls(_controlsList);
            _ui!.ListRefresh(_controlsList);
            Log.Info($"Steuerung: Taste {TDU2Controls.KeyName(key)} -> {act.Id}");
        }
        if (_controlsList != null) _ui!.GotoLabel(VmfPlayer.PathOf(_controlsList.Owner), "binder_out", true);
        return true;
    }

    bool HandleControlsCommand(string c)
    {
        switch (c)
        {
            case "opts:cont:vadt":            // Y: übernehmen (Unity: speichern – hier in den Spielstand)
                TDU2Controls.Save();
                SessionAutosave.Schedule(0.5f);
                return true;
            case "opts:cont:canc":            // verwerfen
                TDU2Controls.Load();
                return true;
            case "opts:cont:bind:open":       // Liste (Fahren/Laufen/Menü) geöffnet: frisch füllen, oben beginnen
                if (_controlsList != null && _ui != null)
                {
                    TDU2Controls.Load();
                    FillControls(_controlsList);
                    _controlsList.Selected = 0; _controlsList.Top = 0;
                    _ui.ListRefresh(_controlsList);
                    _ui.ListLoaded(_controlsList);
                    _ui.ForceVisible("/options/controls_menu/content_menu/pc/binder_key/#2", false);
                }
                return true;
            case "widg:kmap:dflt":            // Standardbelegung (Hilfeleiste)
            case "opts:cont:dflt":
                TDU2Controls.ResetDefaults();
                TDU2InputDevices.ClearOverrides();   // eigene Achsen-Belegungen des Geräts auch zurück auf das Profil
                if (_controlsList != null && _ui != null) { FillControls(_controlsList); _ui.ListRefresh(_controlsList); }
                return true;
            case "opts:cont:bind:delt":       // Belegung löschen
                {
                    var act = SelectedControl();
                    if (act != null && _controlsList != null && _ui != null) { TDU2Controls.Unbind(act); FillControls(_controlsList); _ui.ListRefresh(_controlsList); }
                    return true;
                }
        }
        return false;
    }
}
