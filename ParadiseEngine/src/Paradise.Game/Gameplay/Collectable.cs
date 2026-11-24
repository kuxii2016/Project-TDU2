using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using System.Globalization;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Datenquelle für die Collection-Listen des Pausemenüs (gapr:part:coll / gapr:flli:coll:*).
/// Aufbau und Füllen entsprechen Racing/Discovery: AreaRow-Zeilen für PauseMenu.FillProgressList().
/// Quellen ausschließlich aus vorhandenen Spieldaten:
///  - db_data.cpr / DBRewardXP: Zieltexte (goaltext_hc), echte Punkte (collectionpoints), Meilensteine.
///  - SaveData: autos/ownedCars, houses, ownedFurniture, ownedClothes, stickerBoards.
///  - Avatar: echte Kleidungs-/Frisuren-Hashes, falls ownedClothes leer ist (DATA liefert die Liste nicht).
/// Operationen: SaveData.surgery.nbSurgeries (Driver.NbSurgeries, Schönheitsklinik).
/// </summary>
internal sealed class Collectable : GameModule
{
    /// <summary>Aktives Modul – PauseMenu und UserProgress holen ihre Werte hierüber.</summary>
    public static Collectable? Current { get; private set; }

    /// <summary>Die sechs coll:*-Unterseiten des Flashs.</summary>
    public enum ListKind { Vehicles, Haircuts, Houses, Surgery, Clothes, Stickers }

    sealed class Reward
    {
        public string Event = "";
        public int GoalHc;
        public int Points;
        public int Required;       // Meilenstein-Zahl aus dem Event-Key (z.B. CARS_DIF_10 -> 10)
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<ListKind, List<AreaRow>> RowsByKind = new()
    {
        [ListKind.Vehicles] = new(),
        [ListKind.Haircuts] = new(),
        [ListKind.Houses] = new(),
        [ListKind.Surgery] = new(),
        [ListKind.Clothes] = new(),
        [ListKind.Stickers] = new(),
    };

    static Dictionary<string, Reward>? _rewards;
    /// <summary>DBClothe-Hash -> (Teil, echtes Teil?) – für Avatar-/Besitz-Auswertung.</summary>
    static Dictionary<int, (ClothePart Part, bool Real)>? _clothes;
    /// <summary>Zeilen mit echtem Stand/Ziel für die Detail-Prozentwerte je Kategorie.</summary>
    static readonly List<(ListKind Kind, AreaRow Row, int Current, int Required)> _milestones = new();
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

    /// <summary>Summe aller tatsächlich erreichten Collection-Punkte (für UserProgress).</summary>
    public int Points
    {
        get
        {
            Ensure();
            return RowsByKind.Values.Sum(list => list.Sum(r => r.Found));
        }
    }

    /// <summary>
    /// Echte Anzahl besessener Kleidungsstücke: ownedClothes plus die im Avatar getragenen
    /// echten Teile (die lokale DATA speichert ownedClothes nicht). Frisuren/Bärte zählen nicht.
    /// </summary>
    public static int ClothesCount => Wardrobe(out int clothes, out _) ? clothes : 0;

    /// <summary>Anzahl verschiedener gekaufter Frisuren (ownedClothes + Avatar, DBClothe parttype Hair).</summary>
    public static int HaircutCount => Wardrobe(out _, out int hair) ? hair : 0;

    static bool Wardrobe(out int clothes, out int hair)
    {
        clothes = hair = 0;
        EnsureMeta();
        var sv = TDU2Session.Save;
        if (sv == null || _clothes == null) return false;

        // Gekaufte Teile (auch Frisuren – der Friseur schreibt seine Hashes in ownedClothes)
        // und aktuell getragene Teile zusammenführen. Beide Quellen können denselben Hash
        // enthalten; ein HashSet verhindert Doppelzählung.
        var ownedClothes = new HashSet<int>();
        var ownedHair = new HashSet<int>();
        void Add(int h)
        {
            if (!_clothes.TryGetValue(h, out var m) || !m.Real) return;
            if (m.Part == ClothePart.Hair) ownedHair.Add(h);
            else if (m.Part != ClothePart.Beard) ownedClothes.Add(h);
        }
        foreach (int h in sv.ownedClothes ?? new List<int>()) Add(h);
        var avatar = AvatarConfig.Decode(sv.avatar);
        if (avatar != null)
            foreach (int h in avatar.Items) Add(h);
        clothes = ownedClothes.Count;
        hair = ownedHair.Count;
        return true;
    }

    void Ensure()
    {
        if (_built) return;
        _built = true;   // Build() liest TDU2Session – verhindert Reentrancy.
        try { Build(); }
        catch (Exception ex) { Log.Warn($"Collectable: Listen konnten nicht gebaut werden: {ex.Message}"); }
    }

    public void Refresh()
    {
        _built = false;
        Ensure();
    }

    static void Build()
    {
        EnsureMeta();
        foreach (var list in RowsByKind.Values) list.Clear();
        _milestones.Clear();

        int cars = 0, houses = 0, furniture = 0, stickers = 0;
        var sv = TDU2Session.Save;
        if (sv != null)
        {
            cars = sv.ownedCars?.Count ?? 0;
            houses = Paradise.Core.Savegame.TDU2Garage.OwnedNames(sv).Count;   // besessene Häuser (COLLECTION_HOUSES_%d)
            furniture = sv.ownedFurniture?.Count ?? 0;
            stickers = sv.stickerBoards?.Count ?? 0;
        }
        int clothes = ClothesCount;
        int haircuts = HaircutCount;

        // Event-Key -> Unterseite (sub entspricht der coll:*-Flash-Kategorie).
        var order = new[]
        {
            (ListKind.Vehicles, "COLLECTION_CARS_DIF_", cars),
            (ListKind.Haircuts, "COLLECTION_HAIRCUTS_", haircuts),
            (ListKind.Houses, "COLLECTION_HOUSES_", houses),
            (ListKind.Houses, "COLLECTION_FURNITURES_", furniture),
            (ListKind.Surgery, "COLLECTION_SURGERY", sv?.surgery?.nbSurgeries ?? 0),   // Driver.NbSurgeries (GAME_MODE_SURGERY)
            (ListKind.Clothes, "COLLECTION_CLOTHES_", clothes),
            (ListKind.Stickers, "COLLECTION_STICKERS_", stickers),
        };
        int index = 1;
        foreach (var (kind, prefix, current) in order)
        {
            foreach (var rw in _rewards!.Values
                         .Where(r => r.Event.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(r => r.Required))
            {
                if (rw.Event.EndsWith("_DEBUG", StringComparison.OrdinalIgnoreCase)) continue;
                int have = Math.Clamp(current, 0, rw.Required);
                bool done = have >= rw.Required;
                string goal = Goal(rw, have);
                var row = new AreaRow
                {
                    Index = index++,
                    Goal = goal,
                    Island = "",
                    Found = done ? rw.Points : 0,
                    Total = rw.Points,
                    Percent = 0,   // unten aus echtem Stand / höchstem Meilenstein der Kategorie
                    Points = (done ? rw.Points : 0).ToString(Inv) + "/" + rw.Points.ToString(Inv),
                };
                RowsByKind[kind].Add(row);
                _milestones.Add((kind, row, have, rw.Required));
            }
        }
        // Detail-Prozentzeile = Anteil am höchsten Meilenstein der Kategorie (z.B. 3/30 = 10%).
        foreach (var kind in RowsByKind.Keys)
        {
            int max = 0;
            foreach (var m in _milestones) if (m.Kind == kind) max = Math.Max(max, m.Required);
            if (max <= 0) continue;
            foreach (var m in _milestones)
                if (m.Kind == kind) m.Row.Percent = Math.Clamp(m.Current * 100 / max, 0, 100);
        }
    }

    static string Goal(Reward rw, int have)
    {
        string t = Text(rw.GoalHc, "§NB_VEHICLES§");
        return t.Replace("§NB_VEHICLES§", rw.Required.ToString(Inv))
                .Replace("§NB_HOUSES§", rw.Required.ToString(Inv))
                .Replace("§NB_FURN§", rw.Required.ToString(Inv))
                .Replace("§NB_CLOTHES§", rw.Required.ToString(Inv))
                .Replace("§NB_HAIR§", rw.Required.ToString(Inv))
                .Replace("§NB_STICKERS§", rw.Required.ToString(Inv))
                .Replace("§NB_VEHICLES_OWNED§", have.ToString(Inv))
                .Replace("§NB_HOUSES_OWNED§", have.ToString(Inv))
                .Replace("§NB_FURN_OWNED§", have.ToString(Inv))
                .Replace("§NB_CLOTHES_OWNED§", have.ToString(Inv))
                .Replace("§NB_HAIR_OWNED§", have.ToString(Inv))
                .Replace("§NB_STICKERS_OWNED§", have.ToString(Inv))
                .Replace("§NB_COSCLIN_DONE§", have.ToString(Inv));
    }

    /// <summary>Metadaten einmal laden; leere DB (BnkDir noch nicht gesetzt) später erneut versuchen.</summary>
    static void EnsureMeta()
    {
        if (_rewards != null && (_rewards.Count > 0 || string.IsNullOrEmpty(FlashHost.BnkDir))) return;
        _rewards = LoadRewards(FlashHost.BnkDir);
    }

    static Dictionary<string, Reward> LoadRewards(string bnk)
    {
        var map = new Dictionary<string, Reward>(StringComparer.OrdinalIgnoreCase);
        _clothes = new Dictionary<int, (ClothePart, bool)>();
        try
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(
                Path.Combine(bnk, "database", "db_data.cpr"))));
            foreach (var e in root.Get("DBClothe")?.Items ?? new List<XmbNode>())
            {
                int h = e.I("_hashcode");
                string file = e.S("_file", "") ?? "";
                bool nothing = file.IndexOf("_NO_", StringComparison.OrdinalIgnoreCase) >= 0;
                bool real = !nothing && e.I("_price") > 0;
                _clothes[h] = ((ClothePart)e.I("_parttype"), real);
            }
            foreach (var e in root.Get("DBRewardXP")?.Items ?? new List<XmbNode>())
            {
                string ev = e.S("_event", "") ?? "";
                if (!ev.StartsWith("COLLECTION_", StringComparison.OrdinalIgnoreCase)) continue;
                int goal = e.I("_goaltext_hc");
                if (goal == 0 || map.ContainsKey(ev)) continue;   // DEBUG/leere Einträge und Duplikate überspringen
                int pts = e.I("_collectionpoints");
                var m = System.Text.RegularExpressions.Regex.Match(ev, "_(\\d+)$");
                int required = m.Success ? int.Parse(m.Groups[1].Value, Inv) : Math.Max(1, e.I("_nbtimesallowed", 1));
                map[ev] = new Reward { Event = ev, GoalHc = goal, Points = pts, Required = required };
            }
        }
        catch (Exception ex) { Log.Warn($"Collectable: DBRewardXP nicht lesbar: {ex.Message}"); }
        return map;
    }

    static string Text(int hash, string fallback)
    {
        string? s = FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, hash);
        return string.IsNullOrEmpty(s) ? fallback : s!;
    }
}