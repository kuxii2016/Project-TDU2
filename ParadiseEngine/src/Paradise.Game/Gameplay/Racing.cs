using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Racing;
using Paradise.Game.Racing;
using System.Globalization;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Datenquelle für die Wettbewerbs-Listen des Pausemenüs (gapr:part:raci / gapr:flli:raci:*).
/// Aufbau und Füllen entsprechen Discovery/Wrecks: AreaRow-Zeilen, die PauseMenu.FillProgressList()
/// direkt in gameprogress_fl schreibt. Quellen sind ausschließlich vorhandene Spieldaten:
///  - db_data.cpr / DBLicense : Rennschulen, Meisterschaften, Insel-Pokale
///  - db_data.cpr / DBRewardXP: Zieltexte (goaltext_hc) und echte Punktwerte (racingpoints)
///  - SaveData.racing über RaceProgress: bestandene Prüfungen/Lizenzen und Meisterschaftsergebnisse.
/// Duell-/Challenge-Siege haben kein Savegame-Feld – sie bleiben 0 statt erfundener Werte.
/// </summary>
internal sealed class Racing : GameModule
{
    /// <summary>Aktives Modul – PauseMenu und UserProgress holen ihre Werte hierüber (wie Discovery/Wrecks).</summary>
    public static Racing? Current { get; private set; }

    public enum ListKind { School, Championship, Cup, Duel, Multi, Instant }

    sealed class Reward
    {
        public string Event = "";
        public int GoalHc;
        public int Points;
        public int Times;
        public int Priority;
        public string Target = "";
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<ListKind, List<AreaRow>> RowsByKind = new()
    {
        [ListKind.School] = new(),
        [ListKind.Championship] = new(),
        [ListKind.Cup] = new(),
        [ListKind.Duel] = new(),
        [ListKind.Multi] = new(),
        [ListKind.Instant] = new(),
    };

    static RaceDatabase? _db;
    static Dictionary<string, Reward>? _rewards;
    static bool _built;

    public override void Load()
    {
        Current = this;
        TDU2Session.Changed += OnSessionChanged;   // Fortschritt nach Login/Import nachziehen
        Refresh();
    }

    public override void Unload()
    {
        TDU2Session.Changed -= OnSessionChanged;
        if (Current == this) Current = null;
        foreach (var list in RowsByKind.Values) list.Clear();
        _built = false;
    }

    void OnSessionChanged() => Refresh();

    public IReadOnlyList<AreaRow> Rows(ListKind kind)
    {
        Ensure();
        return RowsByKind[kind];
    }

    /// <summary>Summe aller tatsächlich erreichten Racing-Punkte (für UserProgress).</summary>
    public int Points
    {
        get
        {
            Ensure();
            return RowsByKind.Values.Sum(list => list.Sum(r => r.Found));
        }
    }

    void Ensure()
    {
        if (_built) return;
        // Vor dem Aufbau setzen: Build() liest RaceProgress/TDU2Session – ein erneuter Zugriff
        // auf Rows/Points darf nicht wieder Build() auslösen (sonst StackOverflow).
        _built = true;
        try { Build(); }
        catch (Exception ex) { Log.Warn($"Racing: Listen konnten nicht gebaut werden: {ex.Message}"); }
    }

    /// <summary>Neu aus Savegame + Datenbank füllen (nach Rennen/Fund/Import).</summary>
    public void Refresh()
    {
        _built = false;
        Ensure();
    }

    static void Build()
    {
        string bnk = FlashHost.BnkDir;
        _db ??= RaceDatabase.Load(bnk);
        _rewards ??= LoadRewards(bnk);
        foreach (var list in RowsByKind.Values) list.Clear();

        BuildSchools();
        BuildChampionships();
        BuildCups();
        BuildDuels();
        BuildChallenges(ListKind.Multi, "RACING_MULTICHALL", 4222, "§MULTICHALWON§", "50 Multi-Herausforderungen gewinnen (§MULTICHALWON§)");
        BuildChallenges(ListKind.Instant, "RACING_INSTCHALL", 4223, "§INSTCHALWON§", "50 Sofort-Herausforderungen gewinnen (§INSTCHALWON§)");
        int total = RowsByKind.Values.Sum(list => list.Sum(r => r.Found));
        Log.Info($"Racing: Listen gebaut – Schule={RowsByKind[ListKind.School].Count} Meisterschaft={RowsByKind[ListKind.Championship].Count} " +
                 $"Pokal={RowsByKind[ListKind.Cup].Count} Duell={RowsByKind[ListKind.Duel].Count} Punkte={total}");
    }

    static Dictionary<string, Reward> LoadRewards(string bnk)
    {
        var map = new Dictionary<string, Reward>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(bnk, "database", "db_data.cpr");
        if (!GameFiles.Exists(path)) return map;
        var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(path)));
        var rw = root.Get("DBRewardXP");
        if (rw == null) return map;
        foreach (var e in rw.Items)
        {
            string ev = e.S("_event", "") ?? "";
            if (ev.Length == 0) continue;
            map[ev] = new Reward
            {
                Event = ev,
                GoalHc = e.I("_goaltext_hc"),
                Points = e.I("_racingpoints"),
                Times = e.I("_nbtimesallowed"),
                Priority = e.I("_priority"),
                Target = e.S("_targetspotworld", "") ?? "",
            };
        }
        return map;
    }

    static Reward? R(string ev) => _rewards != null && _rewards.TryGetValue(ev, out var r) ? r : null;

    static string Text(int hash, string fallback)
    {
        string? s = FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash);
        return string.IsNullOrEmpty(s) ? fallback : s!;
    }

    static string IslandName(int island) => island == 0 ? "Ibiza" : "Hawaii";

    static void Add(ListKind kind, string goal, int found, int total, string island, int index)
    {
        RowsByKind[kind].Add(new AreaRow
        {
            Index = index,
            Goal = goal,
            Island = island,
            Found = found,
            Total = total,
            Percent = total > 0 ? found * 100 / total : 0,
            Points = found.ToString(Inv) + "/" + total.ToString(Inv),
        });
    }

    // School-Code aus dem Anzeigenamen: "A7/A6" -> "A7A6" (_event RACING_LICENCE_A7A6).
    static string SchoolCode(LicenseInfo l) => l.DisplayName.Replace("/", "");

    static void BuildSchools()
    {
        var schools = _db!.Licenses
            .Where(l => l.IsRacingSchool)
            .OrderBy(l => R("RACING_LICENCE_" + SchoolCode(l))?.Priority ?? 999)
            .ThenBy(l => l.Hash)
            .ToArray();
        int i = 1;
        foreach (var l in schools)
        {
            var rw = R("RACING_LICENCE_" + SchoolCode(l));
            int pts = rw?.Points ?? 0;
            // Wie Championships.SchoolLicenceWon: Lizenz erworben ODER alle Prüfungen bestanden.
            bool won = RaceProgress.IsLicenceWon(l.Hash)
                       || (l.ChallengeFiles.Count > 0 && l.ChallengeFiles.All(RaceProgress.IsTestDone));
            string goal = Text(rw?.GoalHc ?? 4213, "Lizenz §LICENSE§ erwerben")
                .Replace("§LICENSE§", l.DisplayName);
            Add(ListKind.School, goal, won ? pts : 0, pts, IslandName(l.Island), i++);
        }
    }

    // Meisterschaft: "C4", Elite "C4H" – Bronze/Silber/Gold sind getrennte DBRewardXP-Einträge.
    static string ChampCode(LicenseInfo l) =>
        RaceDatabase.CategoryName(l.VehicleCategory1) + (l.Order >= 2 ? "H" : "");

    static int MedalPoints(string medal, LicenseInfo l) => R("RACING_CHAMP_" + medal + "_" + ChampCode(l))?.Points ?? 0;

    static void BuildChampionships()
    {
        var champs = _db!.Licenses
            .Where(l => l.IsChampionship && l.VehicleCategory1 > 0)
            .OrderBy(l => R("RACING_CHAMP_GOLD_" + ChampCode(l))?.Priority ?? 999)
            .ThenBy(l => l.Hash)
            .ToArray();
        int i = 1;
        foreach (var l in champs)
        {
            int bronze = MedalPoints("BRONZE", l), silver = MedalPoints("SILVER", l), gold = MedalPoints("GOLD", l);
            int total = bronze + silver + gold;
            int place = RaceProgress.IsCupDone(l.Hash) ? RaceProgress.CupFinalPlace(l.Hash) : 0;
            // Platz 1 erhält alle drei Medaillen, Platz 2 Bronze+Silber, Platz 3 nur Bronze.
            int found = place <= 1 && place > 0 ? total : place == 2 ? bronze + silver : place == 3 ? bronze : 0;
            string name = RaceDatabase.CategoryName(l.VehicleCategory1) + (l.Order >= 2 ? " Elite" : "");
            string goal = Text(4214, "Meisterschaft §CHAMP§ gewinnen").Replace("§CHAMP§", name);
            Add(ListKind.Championship, goal, found, total, IslandName(l.Island), i++);
        }
    }

    static void BuildCups()
    {
        var cups = _db!.Licenses
            .Where(l => l.IsChampionship && l.VehicleCategory1 == 0)
            .OrderBy(l => l.Island).ThenBy(l => l.Order)
            .ToArray();
        int i = 1;
        foreach (var l in cups)
        {
            string ev = "RACING_CUP_" + (l.Island == 0 ? "IBIZA" : "HAWAI") + l.Order.ToString(Inv);
            var rw = R(ev);
            int pts = rw?.Points ?? 0;
            bool won = RaceProgress.IsCupDone(l.Hash) && RaceProgress.CupFinalPlace(l.Hash) <= 1;
            string fallback = (l.Island == 0 ? "Ibiza" : "Hawaii") + "-Pokal §AREA§ gewinnen";
            string goal = Text(rw?.GoalHc ?? (l.Island == 0 ? 4215 : 4216), fallback)
                .Replace("§AREA§", l.Order.ToString(Inv));
            Add(ListKind.Cup, goal, won ? pts : 0, pts, IslandName(l.Island), i++);
        }
    }

    static void BuildDuels()
    {
        // Reihenfolge und Langkeys wie Menue.txt / DBRewardXP (C4, B4, A5, B3, A2).
        var events = new[] { "RACING_DUEL_C4", "RACING_DUEL_B4", "RACING_DUEL_A5", "RACING_DUEL_B3", "RACING_DUEL_A2" };
        int i = 1;
        foreach (string ev in events)
        {
            var rw = R(ev);
            if (rw == null) continue;
            // Duelle werden im Savegame nicht einzeln erfasst -> kein erfundener Fortschritt.
            string goal = Text(rw.GoalHc, "Duell gewinnen");
            Add(ListKind.Duel, goal, 0, rw.Points, "Ibiza", i++);
        }
    }

    static void BuildChallenges(ListKind kind, string ev, int fallbackHash, string placeholder, string fallback)
    {
        var rw = R(ev);
        if (rw == null) return;
        // Auch Multi-/Sofort-Siege haben kein Savegame-Feld -> 0.
        int won = 0;
        string goal = Text(rw.GoalHc, fallback).Replace(placeholder, won.ToString(Inv) + "/" + rw.Times.ToString(Inv));
        Add(kind, goal, won * rw.Points, rw.Times * rw.Points, "", 1);
    }
}
