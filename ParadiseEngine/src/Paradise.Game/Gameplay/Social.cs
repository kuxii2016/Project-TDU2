using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using System.Globalization;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Datenquelle für die Social-Listen des Pausemenüs (gapr:part:soci / gapr:flli:soci:*).
/// Aufbau und Füllen entsprechen Racing/Collectable: AreaRow-Zeilen für PauseMenu.FillProgressList().
/// Quelle ausschließlich db_data.cpr / DBRewardXP: Zieltexte (goaltext_hc), echte Punkte
/// (socialpoints), Meilensteine (_nbtimesallowed). Für Freunde/Club/Chase/Coop/Gambling gibt es
/// keine eigenen Savegame-Felder -> Stand 0 statt erfundener Werte.
/// </summary>
internal sealed class Social : GameModule
{
    /// <summary>Aktives Modul – PauseMenu und UserProgress holen ihre Werte hierüber.</summary>
    public static Social? Current { get; private set; }

    /// <summary>Die fünf soci:*-Unterseiten des Flashs.</summary>
    public enum ListKind { Friends, Chase, Club, Coop, Gambling }

    sealed class Reward
    {
        public string Event = "";
        public int GoalHc;
        public int Points;
        public int Times;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<ListKind, List<AreaRow>> RowsByKind = new()
    {
        [ListKind.Friends] = new(),
        [ListKind.Chase] = new(),
        [ListKind.Club] = new(),
        [ListKind.Coop] = new(),
        [ListKind.Gambling] = new(),
    };

    static Dictionary<string, Reward>? _rewards;
    static bool _built;

    public override void Load()
    {
        Current = this;
        TDU2Session.Changed += OnSessionChanged;
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

    /// <summary>Summe aller tatsächlich erreichten Social-Punkte (für UserProgress).</summary>
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
        _built = true;
        try { Build(); }
        catch (Exception ex) { Log.Warn($"Social: Listen konnten nicht gebaut werden: {ex.Message}"); }
    }

    public void Refresh()
    {
        _built = false;
        Ensure();
    }

    static void Build()
    {
        _rewards ??= LoadRewards(FlashHost.BnkDir);
        foreach (var list in RowsByKind.Values) list.Clear();

        // Reihenfolge und Unterseiten entsprechen den soci:*-Kategorien des Flashs.
        var order = new[]
        {
            (ListKind.Friends, new[] { "SOCIAL_FRIENDS", "SOCIAL_INVITE_FRIENDS" }),
            (ListKind.Chase, new[] { "SOCIAL_CHASEMODE_COP", "SOCIAL_CHASEMODE_OUTLAW", "SOCIAL_CHASEMODE_WIN" }),
            (ListKind.Club, new[] { "SOCIAL_CLUBMEMBER", "SOCIAL_CLUB_LVL2", "SOCIAL_CLUB_LVL3", "SOCIAL_INNER_CLUB", "SOCIAL_VS_CLUB" }),
            (ListKind.Coop, new[] { "SOCIAL_COOPFROG", "SOCIAL_COOP_COD", "SOCIAL_COOP_FTL", "SOCIAL_COOP_KYD" }),
            (ListKind.Gambling, new[] { "SOCIAL_GAMBLING_HALL_PLAY", "SOCIAL_GAMBLING_HALL_POST" }),
        };
        int index = 1;
        foreach (var (kind, events) in order)
        {
            foreach (string ev in events)
            {
                if (!_rewards!.TryGetValue(ev, out var rw)) continue;
                string goal = Goal(rw);
                RowsByKind[kind].Add(new AreaRow
                {
                    Index = index++,
                    Goal = goal,
                    Island = "",
                    Found = 0,          // keine Savegame-Quelle -> kein erfundener Fortschritt
                    Total = rw.Points,
                    Percent = 0,
                    Points = "0/" + rw.Points.ToString(Inv),
                });
            }
        }
        int totalPoints = RowsByKind.Values.Sum(list => list.Sum(r => r.Found));
    }

    static string Goal(Reward rw)
    {
        string t = Text(rw.GoalHc, rw.Event);
        var placeholders = new[]
        {
            "§NB_FRIENDS§", "§NB_FRIENDS_INVITED§", "§NB_CHASECOP_DONE§", "§NB_CHASEOUTLAW_DONE§",
            "§NB_CHASE_WON§", "§NB_CHAL_INNER§", "§NB_CHAL_VS§", "§NB_COOP_DONE§", "§NB_FTL_DONE§",
            "§NB_KYD_DONE§", "§NB_FROGCOOP_DONE§", "§CHALLPLAYEDGH§", "§CHALLUPLOADED§"
        };
        foreach (string p in placeholders) t = t.Replace(p, "0");
        return t;
    }

    static Dictionary<string, Reward> LoadRewards(string bnk)
    {
        var map = new Dictionary<string, Reward>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(
                Path.Combine(bnk, "database", "db_data.cpr"))));
            foreach (var e in root.Get("DBRewardXP")?.Items ?? new List<XmbNode>())
            {
                string ev = e.S("_event", "") ?? "";
                if (!ev.StartsWith("SOCIAL_", StringComparison.OrdinalIgnoreCase)) continue;
                int goal = e.I("_goaltext_hc");
                if (goal == 0 || map.ContainsKey(ev)) continue;   // DEBUG/leere Einträge und Duplikate überspringen
                map[ev] = new Reward
                {
                    Event = ev,
                    GoalHc = goal,
                    Points = e.I("_socialpoints"),
                    Times = Math.Max(0, e.I("_nbtimesallowed")),
                };
            }
        }
        catch (Exception ex) { Log.Warn($"Social: DBRewardXP nicht lesbar: {ex.Message}"); }
        return map;
    }

    static string Text(int hash, string fallback)
    {
        string? s = FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash);
        return string.IsNullOrEmpty(s) ? fallback : s!;
    }
}
