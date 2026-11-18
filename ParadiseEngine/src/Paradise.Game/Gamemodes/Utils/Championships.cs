// Portiert aus dem Unity-Projekt (Runtime/Racing/TDU2Championships.cs) – Daten, Freischaltung, Treffpunkte, Kartendaten.
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Online;
using Paradise.Core.Racing;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.Racing;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Einzelspieler-Meisterschaften (DBLicense mit _isachampionship) – gemeinsamer Teil für Freifahrt, Karte und Rennen:
///  - Freischaltung: Normal (Order 1) mit der Rennschul-Lizenz der Fahrzeugklasse (z.B. C4), Elite (Order 2) sobald die
///    Normal-Meisterschaft derselben Klasse abgeschlossen ist.
///  - Wie im Spiel EIN Spot je Meisterschaft (Treffpunkt = ECIL-Position der TNOC-Gruppe in scenarics/spotworld.bin):
///    Kartensymbol asphalt / dirt / classic / cup (Elite: *_h). Dort anhalten + Bestätigen öffnet die große Karte im
///    Wettbewerbsmodus (map.vmf "co_direct_enter", <see cref="GAME_MODE_MAP"/>): erst dort erscheinen die Rennen der
///    Meisterschaft, Auswahl -> Teleport an den Start und Rennen (<see cref="GAME_MODE_CHAMPIONSHIP"/>).
///  - Startpunkte der offenen Rennen (2EGI-Einträge der Gruppe): direkt am Startpunkt anhalten + Bestätigen startet auch.
///  - Einfahrt-Anzeige: Original /HUD/enter mit dem Meisterschafts-Symbol (falsche Fahrzeugklasse: gesperrt). In Unity war der
///    Hinweis vorbereitet (ExtraEnterCode), wurde aber nie angezeigt – hier über <see cref="Minimap.ExtraEnterCode"/>.
///  - Punkte 10-8-6-5-4-3-2-1 je Rennen, Gesamtwertung nach allen Rennen (<see cref="RaceProgress"/>).
/// Werte: Game/Config/ChampionshipConfig.cs.
/// </summary>
public static class Championships
{
    // ------------------------------------------------------------------ Daten
    public sealed class Cup
    {
        public LicenseInfo Lic = null!;
        public string Name = "";
        public readonly List<RaceDefinition> Races = new();
        public string ClassName => RaceDatabase.CategoryName(Lic.VehicleCategory1);
        public bool Elite => Lic.Order >= 2;
    }

    static RaceRepository? _repo;
    static string? _bnk;
    static int _island = -1;
    static Paradise.Core.World.MapStreamer? _streamer;
    static readonly List<Cup> _cups = new();
    static readonly Dictionary<string, (Cup cup, RaceDefinition race, Spot spot)> _spots = new();
    static readonly Dictionary<string, (Cup cup, Spot spot)> _hubs = new();
    static Cup? _nearHub;
    static (Cup? cup, RaceDefinition? race) _near;
    static float _clock, _nextRefresh;
    static int _lastSpotCount = -1;
    static (int cup, int race) _pendingStart = (-1, -1);
    static bool _introPending;

    public static RaceRepository? Repository => _repo;
    public static string? BnkDir => _bnk;
    public static int Island => _island;
    public static IReadOnlyList<Cup> Cups => _cups;
    public static int IndexOf(Cup? c) => c == null ? -1 : _cups.IndexOf(c);

    /// <summary>Ein Meisterschaftsrennen läuft (keine Treffpunkte, keine Einfahrt).</summary>
    public static bool RaceActive => GAME_MODE_CHAMPIONSHIP.Busy;

    static string CurrentIslandName(IGameModeContext ctx) => ctx.Terrain?.Streamer?.Index?.IslandName ?? "";

    static int CurrentIsland(IGameModeContext ctx)
    {
        string n = CurrentIslandName(ctx);
        if (string.IsNullOrEmpty(n)) return -1;
        return n.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    /// <summary>Meisterschaften der aktuellen Insel laden (einmal je Bank/Insel). false = keine Welt/Daten.</summary>
    public static bool EnsureData(IGameModeContext ctx)
    {
        var vm = ctx.Vehicles?.Manager; var st = ctx.Terrain?.Streamer;
        if (vm == null || string.IsNullOrEmpty(vm.BnkDir) || st == null) return false;
        int isl = CurrentIsland(ctx);
        if (isl < 0) return false;
        if (!ReferenceEquals(st, _streamer)) { ClearSpots(); _streamer = st; }
        if (_repo != null && _bnk == vm.BnkDir && _island == isl) return true;
        try
        {
            ClearSpots();
            _bnk = vm.BnkDir;
            _island = isl;
            _repo = RaceRepository.Load(_bnk);
            _cups.Clear();
            foreach (var l in _repo.Database.Championships(isl))
            {
                var c = new Cup { Lic = l };
                c.Name = RaceTexts.Get(l.NameHc, (l.Order >= 2 ? "Elite-Meisterschaft " : "Meisterschaft ") + RaceDatabase.CategoryName(l.VehicleCategory1));
                foreach (var f in l.ChallengeFiles)
                {
                    var d = _repo.Build(f, l);
                    if (d != null && d.Checkpoints.Count > 0) c.Races.Add(d);
                }
                if (c.Races.Count > 0) _cups.Add(c);
            }
            Log.Info("Meisterschaften: " + _cups.Count + " auf Insel " + isl + " (" +
                     string.Join(", ", _cups.ConvertAll(c => (c.Lic.ChallengeFiles.Count > 0 && c.Lic.ChallengeFiles[0].Length >= 5 ? c.Lic.ChallengeFiles[0].Substring(0, 5) : c.Name) + (IsAvailable(c) ? "*" : ""))) + ")");
            _nextRefresh = 0f;
            return true;
        }
        catch (Exception ex) { Log.Warn("Meisterschaften: " + ex.Message); _repo = null; return false; }
    }

    // ------------------------------------------------------------------ Freischaltung
    static bool SchoolLicenceWon(int cat)
    {
        var school = _repo?.Database.SchoolLicenseWithCategory(cat);
        if (school == null) return false;
        if (RaceProgress.IsLicenceWon(school.Hash)) return true;
        foreach (var t in school.ChallengeFiles) if (!RaceProgress.IsTestDone(t)) return false;
        return school.ChallengeFiles.Count > 0;
    }

    public static bool IsAvailable(Cup c)
    {
        if (ChampionshipConfig.UnlockAll) return true;
        int cat = c.Lic.VehicleCategory1;
        if (cat <= 0) return false;   // Insel-Meisterschaften (alle Klassen) – noch nicht freigeschaltet
        if (!SchoolLicenceWon(cat)) return false;
        if (c.Lic.Order <= 1) return true;
        foreach (var o in _cups)
            if (o.Lic.VehicleCategory1 == cat && o.Lic.Order == c.Lic.Order - 1) return RaceProgress.IsCupDone(o.Lic.Hash);
        return true;
    }

    public static string ModeLabel(RaceDefinition d) => d.Kind switch
    {
        RaceMode.Race => "race",
        RaceMode.SpeedTrap => "speedtrap",
        RaceMode.Speed => "speed",
        _ => "timeAttack",
    };

    public static string ModeName(RaceDefinition d) => d.Kind switch
    {
        RaceMode.Race => "Rennen",
        RaceMode.SpeedTrap => "Radar",
        RaceMode.Speed => "Tempo",
        _ => "Zeitfahren",
    };

    public static string RaceName(RaceDefinition d) => RaceTexts.Get(d.NameHc, d.Title);

    // ------------------------------------------------------------------ Spots (Treffpunkte auf Karte/Minimap)
    static void RefreshSpots()
    {
        var spots = _streamer?.Spots;
        if (spots == null) return;
        var want = new HashSet<string>();
        bool idle = !RaceActive;
        if (idle)
            foreach (var c in _cups)
            {
                if (!IsAvailable(c) || RaceProgress.IsCupDone(c.Lic.Hash)) continue;
                foreach (var r in c.Races)
                {
                    if (RaceProgress.IsCupRaceDone(c.Lic.Hash, r.Name)) continue;
                    string code = "#CUP_" + r.Name;
                    want.Add(code);
                    if (!_spots.ContainsKey(code))
                    {
                        // Rennstart: nicht als Kartensymbol (die Rennen zeigt erst der Wettbewerbsmodus der Karte)
                        StartFrame(r, out var sp, out var f);
                        _spots[code] = (c, r, new Spot { Code = code, Position = sp, Position2 = sp - f * 14f, Index = -1 });
                    }
                }
            }
        // Meisterschafts-Spots (Treffpunkt) – alle freigeschalteten, auch abgeschlossene (Wiederholen/Ergebnis)
        var wantHub = new HashSet<string>();
        if (idle)
            foreach (var c in _cups)
            {
                if (!IsAvailable(c)) continue;
                string code = HubCode(c);
                wantHub.Add(code);
                if (!_hubs.TryGetValue(code, out var h))
                {
                    var pos = HubPosition(c, out var f);
                    h = (c, new Spot { Code = code, Position = pos, Position2 = pos - f * 14f, Index = -1 });
                    _hubs[code] = h;
                    Minimap.ExtraSpotLabels[code] = HubLabel(c);
                    Minimap.ExtraSpotNames[code] = c.Name;
                    Minimap.ExtraSpotNumbers[code] = ClassNumber(c);
                    Minimap.ExtraSpotPictures[code] = ContestPicture(c);
                }
                if (!spots.Spots.Contains(h.spot)) spots.Spots.Add(h.spot);
            }
        foreach (var code in new List<string>(_hubs.Keys))
        {
            if (wantHub.Contains(code)) continue;
            spots.Spots.Remove(_hubs[code].spot);
            _hubs.Remove(code);
            ForgetExtra(code);
        }
        if (want.Count != _lastSpotCount)
        {
            _lastSpotCount = want.Count;
            string? first = null;
            foreach (var kv in _spots) { first = kv.Value.race.Name + " @ " + kv.Value.spot.Position.ToString("0", CultureInfo.InvariantCulture); break; }
            Log.Info("Meisterschaften: " + _hubs.Count + " Treffpunkte auf der Karte, " + want.Count + " offene Rennen (" + (first ?? "-") + "), Spots gesamt " + spots.Spots.Count);
        }
        foreach (var code in new List<string>(_spots.Keys)) if (!want.Contains(code)) _spots.Remove(code);
    }

    static void ForgetExtra(string code)
    {
        Minimap.ExtraSpotLabels.Remove(code);
        Minimap.ExtraSpotNames.Remove(code);
        Minimap.ExtraSpotPictures.Remove(code);
        Minimap.ExtraSpotNumbers.Remove(code);
    }

    /// <summary>Kartenbild der Meisterschaft: frontend/AllRes/MapScreens/contest/&lt;as|of|cl&gt;-&lt;Klasse&gt;-&lt;1 Normal|2 Elite&gt;.bnk,
    /// Insel-Meisterschaften cu-&lt;i|h&gt;-&lt;n&gt;.</summary>
    public static string ContestPicture(Cup c)
    {
        int cat = c.Lic.VehicleCategory1, ord = System.Math.Clamp(c.Lic.Order, 1, 2);
        string kind; int num;
        if (cat >= 1 && cat <= 7) { kind = "as"; num = cat; }
        else if (cat >= 9 && cat <= 12) { kind = "of"; num = cat - 8; }
        else if (cat >= 14 && cat <= 17) { kind = "cl"; num = cat - 13; }
        else return "contest/cu-" + (_island == 0 ? "i" : "h") + "-" + ord;
        return "contest/" + kind + "-" + num + "-" + ord;
    }

    static void ClearSpots()
    {
        var spots = _streamer?.Spots;
        foreach (var kv in _hubs)
        {
            spots?.Spots.Remove(kv.Value.spot);
            ForgetExtra(kv.Key);
        }
        _hubs.Clear();
        _spots.Clear();
        Minimap.ExtraEnterCode = null;
    }

    public static string HubCode(Cup c) => "#CUPHUB_" + c.Lic.Hash.ToString("X");

    /// <summary>Kartensymbol der Meisterschaft (map.vmf point/spot): asphalt (A), dirt (B), classic (C), cup (Insel); Elite "_h".</summary>
    public static string HubKind(Cup c)
    {
        int cat = c.Lic.VehicleCategory1;
        if (cat >= 1 && cat <= 7) return "asphalt";
        if (cat >= 9 && cat <= 12) return "dirt";
        if (cat >= 14 && cat <= 17) return "classic";
        return "cup";
    }
    public static string HubLabel(Cup c) => HubKind(c) + (c.Elite ? "_h" : "");

    /// <summary>Treffpunkt: ECIL-Position der TNOC-Gruppe der Lizenz, sonst Start des ersten Rennens.</summary>
    public static Vector3 HubPosition(Cup c, out Vector3 fwd)
    {
        var sw = _repo?.SpotWorld;
        if (sw != null)
            foreach (var g in sw.Groups)
                if (g.License == c.Lic.Hash && g.Position.LengthSquared() > 1f)
                {
                    fwd = new Vector3(MathF.Sin(g.Heading), 0f, MathF.Cos(g.Heading));
                    return g.Position;
                }
        StartFrame(c.Races[0], out var p, out fwd);
        return p;
    }

    static string ClassNumber(Cup c)
    {
        string n = c.ClassName ?? "";
        int i = n.Length; while (i > 0 && char.IsDigit(n[i - 1])) i--;
        return i < n.Length ? n.Substring(i) : "";
    }

    /// <summary>Startpunkt und -richtung (Inselkoordinaten) wie in der Rennschule: Startlinie, ggf. zum ersten Ziel gedreht.</summary>
    public static void StartFrame(RaceDefinition def, out Vector3 start, out Vector3 fwd)
    {
        start = def.StartPosition;
        Vector3 f = def.Start != null ? def.Start.Forward : new Vector3(MathF.Sin(def.StartHeading), 0f, MathF.Cos(def.StartHeading));
        Vector3 aim = Vector3.Zero;
        foreach (int k in def.TargetSequence()) { var d = def.Checkpoints[k].Position - start; d.Y = 0f; if (d.LengthSquared() > 225f) { aim = d; break; } }
        var fh = new Vector3(f.X, 0f, f.Z);
        if (aim != Vector3.Zero && fh.LengthSquared() > 1e-6f
            && MathF.Acos(System.Math.Clamp(Vector3.Dot(Vector3.Normalize(fh), Vector3.Normalize(aim)), -1f, 1f)) * Mathf.Rad2Deg > 100f) f = -f;
        f.Y = 0f;
        fwd = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitZ;
    }

    public static int CarCategory(CarPhysics? c) => c != null && c.Raw != null ? c.Raw.I("_vehiclecategory") : 0;

    /// <summary>Auto passt zur Meisterschaft (Klasse 1 oder 2)?</summary>
    public static bool CarAllowed(Cup c, CarPhysics? car)
    {
        if (!ChampionshipConfig.RequireCarCategory || c.Lic.VehicleCategory1 <= 0) return true;
        int cat = CarCategory(car);
        return cat == c.Lic.VehicleCategory1 || (c.Lic.VehicleCategory2 > 0 && cat == c.Lic.VehicleCategory2);
    }

    // ------------------------------------------------------------------ Freifahrt: Treffpunkt/Startpunkt (Unity UpdateNear)
    /// <summary>Jedes Bild in der Freifahrt (GAME_MODE_FREERIDE.Run): Spots pflegen, Einfahrt anzeigen, Bestätigen.</summary>
    internal static void UpdateFreeride(IGameModeContext ctx, float dt)
    {
        _clock += dt;
        if (!EnsureData(ctx)) return;
        if (_clock >= _nextRefresh) { _nextRefresh = _clock + 2f; RefreshSpots(); }
        var vm = ctx.Vehicles?.Manager; var veh = vm?.Current;
        // Rennen aus der Karte gewählt (nach dem Schließen der Karte, Spiel läuft wieder)
        if (_pendingStart.cup >= 0)
        {
            var ps = _pendingStart; _pendingStart = (-1, -1);
            BeginRace(ctx, ps.cup, ps.race);
            return;
        }
        _near = default;
        _nearHub = null;
        if (veh == null || ctx.Paused || RaceActive || InteriorUtils.Inside || ctx.Vehicles!.Suspended || ctx.Vehicles.Frozen || _introPending) { Prompt(vm, null); return; }
        var car = vm!.PositionGame;
        float best = ChampionshipConfig.TriggerRadius * ChampionshipConfig.TriggerRadius;
        foreach (var kv in _hubs)
        {
            var d = kv.Value.spot.Position - car; d.Y *= 0.3f;
            if (d.LengthSquared() < best * 1.6f) { best = d.LengthSquared(); _nearHub = kv.Value.cup; }
        }
        // Startpunkte der offenen Rennen: direkt am Start anhalten + Bestätigen startet das Rennen (wie im Spiel)
        if (_nearHub == null)
        {
            float bestR = ChampionshipConfig.TriggerRadius * ChampionshipConfig.TriggerRadius;
            foreach (var kv in _spots)
            {
                var d = kv.Value.spot.Position - car; d.Y *= 0.3f;
                if (d.LengthSquared() < bestR) { bestR = d.LengthSquared(); _near = (kv.Value.cup, kv.Value.race); }
            }
        }
        var c = _nearHub ?? _near.cup;
        bool wrongCar = c != null && !CarAllowed(c, veh.Car);
        Prompt(vm, c, wrongCar);
        if (c == null || MathF.Abs(veh.SpeedKmh) > ChampionshipConfig.TriggerMaxKmh || !TDU2Controls.ConfirmPressed()) return;
        if (_nearHub == null && _near.cup != null && _near.race != null)
        {
            Log.Info("Meisterschaft: Bestätigen am Start " + _near.race.Name + " (" + _near.cup.Name + ")");
            var n = _near; _near = default;
            BeginRace(ctx, n.cup!, n.race!);
            return;
        }
        Log.Info("Meisterschaft: Bestätigen am Treffpunkt " + _nearHub!.Name + (wrongCar ? " – falsche Fahrzeugklasse (" + RaceDatabase.CategoryName(CarCategory(veh.Car)) + ")" : " – öffne Wettbewerb"));
        if (wrongCar) return;   // Einfahrt zeigt "Gesperrt"
        var cup = _nearHub;
        _nearHub = null; Prompt(vm, null);
        // wie im Spiel: beim ersten Mal das Vorstellungs-Kino, danach Treffpunkt -> große Karte im Wettbewerbsmodus
        PlayHubIntro(cup, () => OpenHub(cup));
    }

    /// <summary>Einfahrt /HUD/enter mit dem Meisterschafts-Symbol (Treffpunkt oder Startpunkt eines offenen Rennens).</summary>
    static void Prompt(VehicleManager? vm, Cup? c, bool wrongCar = false)
    {
        if (c == null)
        {
            if (Minimap.ExtraEnterCode != null) { Minimap.ExtraEnterCode = null; vm?.Minimap?.ShowEnter(null); }
            return;
        }
        Minimap.ExtraEnterCode = HubCode(c);
        Minimap.ExtraEnterLockedText = wrongCar ? LockedText(c) : null;
        vm?.Minimap?.ShowEnter(null);   // nimmt ExtraEnterCode
    }

    /// <summary>Text unter dem Schloss bei falscher Fahrzeugklasse: Klasse der Meisterschaft (Original: Gruppe im Tooltip).</summary>
    static string LockedText(Cup c) => c.ClassName + (c.Lic.VehicleCategory2 > 0 ? " / " + RaceDatabase.CategoryName(c.Lic.VehicleCategory2) : "");

    static void OpenHub(Cup cup)
    {
        if (RaceActive) return;
        int idx = _cups.IndexOf(cup);
        if (idx >= 0 && GAME_MODE_MAP.OpenContest(idx, true)) return;
        var first = cup.Races.Find(r => !RaceProgress.IsCupRaceDone(cup.Lic.Hash, r.Name)) ?? cup.Races[0];
        if (GAME_MODE_CHAMPIONSHIP.Context != null) BeginRace(GAME_MODE_CHAMPIONSHIP.Context, cup, first);
    }

    // ------------------------------------------------------------------ Kinos am Treffpunkt (je Spieler einmal)
    public static string CupIntroKey(Cup cup) => "cup." + cup.Lic.Hash.ToString("X");
    public static string SchoolIntroKey(Cup cup)
    {
        string n = RaceDatabase.CategoryName(cup.Lic.VehicleCategory1) ?? "";
        return "first." + (n.Length > 0 ? n.Substring(0, 1) : "?");
    }

    /// <summary>Kinos am Treffpunkt: Vorstellung der Meisterschaft (das Einführungskino der Schulart gehört nicht hierher).</summary>
    static void PlayHubIntro(Cup cup, Action then)
    {
        if (!RaceConfig.Cinematics) { then(); return; }
        string cupKey = CupIntroKey(cup);
        var intro = RaceCinematics.Seen(cupKey) ? null : RaceCinematics.ChampionshipIntro(_bnk, cup.Lic, _island);
        // *_firstintro (sta_rsa_first_intro u.a.) gehört nicht vor die Meisterschaften – Schlüssel setzen, sonst fragt BeginRace erneut
        RaceCinematics.MarkSeen(SchoolIntroKey(cup));
        RaceCinematics.MarkSeen(cupKey);
        if (intro == null) { then(); return; }
        RaceCinematics.Play(intro, then);
    }

    // ------------------------------------------------------------------ Start
    /// <summary>Rennen aus der Karte (nach dem Schließen; Unity StartFromMap).</summary>
    internal static void StartFromMap(int cupIndex, int raceIndex) => _pendingStart = (cupIndex, raceIndex);

    static void BeginRace(IGameModeContext ctx, int cupIndex, int raceIndex)
    {
        if (cupIndex < 0 || cupIndex >= _cups.Count) return;
        var c = _cups[cupIndex];
        if (raceIndex < 0 || raceIndex >= c.Races.Count) return;
        BeginRace(ctx, c, c.Races[raceIndex]);
    }

    /// <summary>
    /// Rennen beginnen: Fahrzeugklasse prüfen, Vorstellungs-Kino vor dem ersten Rennen (egal ob über Treffpunkt, Startpunkt oder
    /// Wettbewerbskarte gestartet), dann <see cref="GAME_MODE_CHAMPIONSHIP"/>.
    /// </summary>
    internal static void BeginRace(IGameModeContext ctx, Cup cup, RaceDefinition def)
    {
        if (_introPending || RaceActive) return;
        Prompt(ctx.Vehicles?.Manager, null);
        var veh = ctx.Vehicles?.Manager?.Current;
        if (veh == null) return;
        if (!CarAllowed(cup, veh.Car))
        {
            Log.Info(cup.Name + ": nur mit einem Auto der Klasse " + cup.ClassName + " (dein Auto: " + RaceDatabase.CategoryName(CarCategory(veh.Car)) + ")");
            Paradise.Core.Audio.FrontendSounds.Play("wrong");
            return;
        }
        if (RaceConfig.Cinematics && (!RaceCinematics.Seen(CupIntroKey(cup)) || !RaceCinematics.Seen(SchoolIntroKey(cup))))
        {
            _introPending = true;
            PlayHubIntro(cup, () => { _introPending = false; GAME_MODE_CHAMPIONSHIP.Begin(ctx, cup, def); });
            return;
        }
        GAME_MODE_CHAMPIONSHIP.Begin(ctx, cup, def);
    }

    // ------------------------------------------------------------------ Daten für die große Karte (Wettbewerbsmodus)
    /// <summary>Meisterschaft für die Karte (map.vmf: Spot-Symbol, Tooltip setContests, Titel).</summary>
    public sealed class MapCup
    {
        public int Index;
        public string Code = "", Name = "", Label = "", ContestType = "", Picture = "", ClassName = "", Group = "";
        public bool Elite, Done;
        public Vector3 Hub;                 // Inselkoordinaten
        public int RaceCount, RacesDone, Points, Place;
        public long RaceReward, ChampReward;
    }

    /// <summary>Rennen einer Meisterschaft: State 0 gesperrt, 1 offen, 2 gefahren (map.vmf chall&lt;i&gt;_state).</summary>
    public sealed class MapRace
    {
        public int Index;
        public string Name = "", Title = "", Label = "", ModeName = "";
        public int State, Laps, Points, Place;
        public float LengthM;
        public Vector3 Start, Forward;
    }

    /// <summary>Freigeschaltete Meisterschaften der aktuellen Insel.</summary>
    public static List<MapCup> MapCups()
    {
        var list = new List<MapCup>();
        for (int i = 0; i < _cups.Count; i++) if (IsAvailable(_cups[i])) list.Add(MapCupAt(i)!);
        return list;
    }

    public static MapCup? MapCupAt(int index)
    {
        if (index < 0 || index >= _cups.Count) return null;
        var c = _cups[index];
        var m = new MapCup
        {
            Index = index,
            Code = HubCode(c),
            Name = c.Name,
            Label = HubLabel(c),
            ContestType = HubKind(c),
            Picture = ContestPicture(c),
            ClassName = c.ClassName,
            Group = c.ClassName,
            Elite = c.Elite,
            Done = RaceProgress.IsCupDone(c.Lic.Hash),
            Hub = HubPosition(c, out _),
            RaceCount = c.Races.Count,
            RaceReward = c.Lic.RaceMoneyReward,
            ChampReward = c.Lic.ChampMoneyReward,
        };
        var total = CupTotals(c, out int raced);
        m.RacesDone = raced; m.Points = total[0];
        m.Place = 1; for (int k = 1; k < total.Length; k++) if (total[k] > total[0]) m.Place++;
        return m;
    }

    public static List<MapRace> MapRaces(int index)
    {
        var list = new List<MapRace>();
        if (index < 0 || index >= _cups.Count) return list;
        var c = _cups[index];
        for (int i = 0; i < c.Races.Count; i++)
        {
            var r = c.Races[i];
            StartFrame(r, out var sp, out var f);
            var pts = RaceProgress.CupRacePoints(c.Lic.Hash, r.Name);
            list.Add(new MapRace
            {
                Index = i,
                Name = r.Name,
                Title = RaceName(r),
                Label = ModeLabel(r),
                ModeName = ModeName(r),
                State = RaceProgress.IsCupRaceDone(c.Lic.Hash, r.Name) ? 2 : 1,
                Laps = System.Math.Max(1, r.Laps),
                Points = pts != null && pts.Length > 0 ? pts[0] : 0,
                Place = PlaceOf(pts),
                LengthM = r.Length > 1f ? r.Length : 0f,
                Start = sp,
                Forward = f,
            });
        }
        return list;
    }

    /// <summary>Gesamtwertung (Name, Punkte, Spieler?) absteigend.</summary>
    public static List<(string name, int points, bool player)> MapStandings(int index)
    {
        var list = new List<(string name, int points, bool player)>();
        if (index < 0 || index >= _cups.Count || _repo == null) return list;
        var c = _cups[index];
        var total = CupTotals(c, out _);
        list.Add((PlayerName, total[0], true));
        for (int i = 0; i < c.Lic.Bots.Count && i + 1 < total.Length; i++) list.Add((BotName(c.Lic.Bots[i].pnj, i), total[i + 1], false));
        list.Sort((a, b) => b.points != a.points ? b.points.CompareTo(a.points) : (a.player ? -1 : b.player ? 1 : 0));
        return list;
    }

    public static string PlayerName => TDU2Session.Nick ?? "Du";

    public static string BotName(int pnj, int i) =>
        _repo != null && _repo.Database.PnjNames.TryGetValue(pnj, out var n) && !string.IsNullOrEmpty(n) ? n : "Fahrer " + (i + 1);

    public static int PlaceOf(int[]? pts)
    {
        if (pts == null || pts.Length == 0) return 0;
        int p = 1; for (int i = 1; i < pts.Length; i++) if (pts[i] > pts[0]) p++;
        return p;
    }

    public static int[] CupTotals(Cup c, out int raced)
    {
        var total = new int[c.Lic.Bots.Count + 1];
        raced = 0;
        foreach (var r in c.Races)
        {
            var p = RaceProgress.CupRacePoints(c.Lic.Hash, r.Name);
            if (p == null) continue;
            raced++;
            for (int i = 0; i < p.Length && i < total.Length; i++) total[i] += p[i];
        }
        return total;
    }

    /// <summary>Name des Rivalen (DBLicense._rival).</summary>
    public static string? RivalName(int index)
    {
        if (index < 0 || index >= _cups.Count || _repo == null) return null;
        int r = _cups[index].Lic.Rival;
        return r != 0 && _repo.Database.PnjNames.TryGetValue(r, out var n) ? n : null;
    }

    /// <summary>Meisterschaft aufgeben: Punkte/Ergebnisse zurücksetzen (map.vmf "giup").</summary>
    public static void GiveUp(int cupIndex)
    {
        if (cupIndex < 0 || cupIndex >= _cups.Count) return;
        RaceProgress.ResetCup(_cups[cupIndex].Lic);
        _nextRefresh = 0f;
        Log.Info(_cups[cupIndex].Name + ": aufgegeben – Wertung zurückgesetzt");
    }

    /// <summary>Neu einlesen der Treffpunkte beim nächsten Bild (nach einem Rennen).</summary>
    internal static void Refresh() => _nextRefresh = 0f;

    /// <summary>Auto eines Gegners (DBPNJCar) in der Klasse der Lizenz, sonst irgendeines der Klasse.</summary>
    public static CarPhysics? BotCar(int pnj, LicenseInfo lic, CarDatabase? db, string bnkDir)
    {
        if (db == null || _repo == null) return null;
        if (_repo.Database.PnjCars.TryGetValue(pnj, out var cars))
        {
            CarPhysics? any = null;
            foreach (var id in cars)
            {
                if (!db.ById.TryGetValue(id, out var c) || CarDatabase.CarBank(bnkDir, c) == null) continue;
                int cat = CarCategory(c);
                if (cat == lic.VehicleCategory1 || (lic.VehicleCategory2 > 0 && cat == lic.VehicleCategory2)) return c;
                any ??= c;
            }
            if (lic.VehicleCategory1 <= 0 && any != null) return any;
        }
        foreach (var c in db.Cars) if (CarCategory(c) == lic.VehicleCategory1 && CarDatabase.CarBank(bnkDir, c) != null) return c;
        return null;
    }
}
