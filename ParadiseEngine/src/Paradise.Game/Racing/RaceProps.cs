// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceProps.cs + RaceGateVisual.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Racing;
using Paradise.Core.Render;
using Paradise.Core.World;
using Paradise.Game.Config;
using System.Numerics;

namespace Paradise.Game.Racing;

/// <summary>Zustand eines Tors (Unity RaceGateVisual).</summary>
public enum RaceGateState { Pending, Next, Passed }

/// <summary>
/// Tor einer Prüfung: genau ein Checkpoint-Entity aus pack_cp.bnk, gewählt wie in der exe (Typ 0x979f90, Variante 0x980ea8,
/// Suche 0x8926d0). "_Off" in Start_line_Off / Finish_line_Off ist KEIN Aus-Zustand, sondern die Gelände-Variante
/// (OffRoadCheckpoint 1) – früher wurden beide gebaut und wartende Tore zeigten fälschlich das Gelände-Modell.
/// Gate_Main besteht aus zwei Kind-Actors (Actor1/Actor2) und hat daher mehrere Szenen. Passierte Tore werden ausgeblendet.
/// </summary>
public sealed class RaceGate
{
    public int Index;
    public RaceCheckpoint Checkpoint = null!;
    public string Model = "";
    internal readonly List<MapStreamer.SceneHandle> Scenes = new();
    public RaceGateState State { get; private set; }

    public void SetState(RaceGateState state)
    {
        State = state;
        foreach (var h in Scenes) h.Active = state != RaceGateState.Passed;
    }
}

/// <summary>
/// Props einer Prüfung: Kegel und Absperrungen (ig2 DEC2) sowie die Tor-Optik der Checkpoints.
///
/// Modelle wie im Spiel (EXE 0x9795a0): DEC2-Art 0/1/2 -> World-Actor "SF_Stud_act" (Kegel), 3/4 -> "RS_Separ_A_act",
/// 5 -> "RS_Separ_B_act" (Absperrung, Endstück). Die Actors stehen in
/// islands/&lt;insel&gt;/scenarics/packs/worldactors.bnk (entities.xmb: __Name, "mesh" = rsc-Hashes, aufgelöst über hashcodes.xmb).
/// Tore: pack_cp.bnk (Entity__Actor__CheckPoint: Check_point, CP_OffRoad, Start_line(_Off), Finish_line(_Off), BrakingZone_01, Trajectory).
///
/// Linien (1→2, 4→5) werden mit Objekten gefüllt: Kegel alle <see cref="ConeSpacing"/> m, Absperrungen Stoß an Stoß
/// (Länge aus den Modell-Bounds). Der Abstand selbst steht nicht in der .ig2 (vermutet).
/// Engine: Kegel/Absperrungen sind World Actors (<see cref="WorldActorBodies"/>: fest bis zum Anstoß, dann umfallend – wie die
/// worldactors_*.dhk des Spiels); ein Anstoß meldet <see cref="Hit"/> (Unity RacePropHit). Ersatzmodelle gibt es nicht (nur Originale).
/// </summary>
public sealed class RaceProps
{
    public const string ConeActor = "SF_Stud_act", SeparActorA = "RS_Separ_A_act", SeparActorB = "RS_Separ_B_act";
    public static float ConeSpacing = 2.5f;
    public static float FallbackSeparLength = 2f;
    public static float StartClearRadius = 3.5f;
    public static float TrajectorySpacing = 2.6f;
    /// <summary>Größe der Pfeile (Modell 1 x 1 m, Spitze zeigt zu -Z bzw. V=0 der Textur).</summary>
    public static float TrajectoryScale = 2.4f;
    /// <summary>Drehung des Trajectory-Modells um Y (Grad); NaN = automatisch aus den Modellmaßen.</summary>
    public static float TrajectoryYaw = 180f;   // Pfeilspitze zeigt im Modell nach -Z (Textur V = 0) -> umdrehen

    /// <summary>Wird bei einem Treffer eines Props durch das Spielerauto ausgelöst (Actor-Name, Aufprallgeschwindigkeit m/s).</summary>
    public event Action<string, float>? Hit;

    public int Count { get; private set; }
    public string Source { get; private set; } = "";
    /// <summary>Besitzer-Kennung der World Actors dieser Prüfung (WorldActorBodies-Sektor).</summary>
    public string Key { get; }

    static int _serial;
    MapStreamer? _st;
    readonly List<MapStreamer.SceneHandle> _scenes = new();
    readonly List<StaticCollider> _colliders = new();

    public RaceProps() { Key = "Race#" + System.Threading.Interlocked.Increment(ref _serial); WorldActorBodies.Broken += OnBroken; }

    void OnBroken(string sector, string actor, float speed) { if (sector == Key) Hit?.Invoke(actor, speed); }

    // ------------------------------------------------------------------ Bibliothek (Hintergrund)
    sealed class Library
    {
        public string Path = "";
        public ActorBank? World;
        public ObjectSet? Cone, SeparA, SeparB;
        public float SeparLength = FallbackSeparLength;
        public bool SeparMeasured, SeparAlongX = true;
        /// <summary>Actors aus pack_cp.bnk nach __Name (Check_point, Start_line(_Off), Finish_line(_Off), BrakingZone_01, CP_OffRoad, Trajectory ...).</summary>
        public readonly Dictionary<string, ObjectSet> Named = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Entity__Actor__CheckPoint-Einträge in Dateireihenfolge (für die Suche nach Typ/Variante wie exe 0x8926d0).</summary>
        public readonly List<CpRecord> CheckPoints = new();
        /// <summary>Bank, aus der ein Set stammt (worldactors.bnk oder pack_cp.bnk).</summary>
        public readonly Dictionary<ObjectSet, ActorBank> BankOf = new();
        public string Log = "";
    }

    /// <summary>Ein Entity__Actor__CheckPoint aus pack_cp.bnk: CheckPointType, OffRoadCheckpoint, Modell bzw. Kind-Actors.</summary>
    sealed class CpRecord
    {
        public string Name = "";
        public int Type, OffRoad;
        public ObjectSet? Set;
        /// <summary>Actor1 / Actor2 (Gate_Main: SF_Barel_D_act_left / _right, Verweis über __Hc) – Lage setzt <see cref="BuildGate"/> wie exe 0x8ad4da.</summary>
        public readonly List<(string name, ObjectSet set)> Children = new();
    }

    static Library? _lib;
    static Task<Library>? _libTask;
    static string? _libPath;
    // Unity _gateMatsByName: ein Overlay-Material pro (Materialname, Textur) für alle Tore des Rennens.
    // Vorher erzeugte jedes Tor eigene Materialien/Descriptor-Sets -> Transparent-Pipeline ohne Batching.
    static readonly Dictionary<string, IGpuMaterial> _gateMaterialCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<IGpuMaterial> _gateMaterialOwned = new();

    /// <summary>World-Actor-Bank der Insel suchen.</summary>
    public static string? WorldActorsBank(string? bnkDir, string? islandName)
    {
        if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(islandName)) return null;
        try
        {
            string dir = Path.Combine(bnkDir, "islands", islandName, "scenarics", "packs");
            if (!GameFiles.DirectoryExists(dir)) return null;
            // Der direkte Pfad wird über den Hash der .map aufgelöst (GameFiles.TryGetEntry).
            // GetFiles bleibt als Fallback für lose Dateien/ältere Namenslisten erhalten.
            string direct = Path.Combine(dir, "worldactors.bnk");
            if (GameFiles.Exists(direct)) return direct;
            foreach (var f in GameFiles.GetFiles(dir, "*.bnk"))
                if (string.Equals(Path.GetFileName(f), "worldactors.bnk", StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Lädt (im Hintergrund) die Kegel-/Absperrungs- und Tormodelle. true, sobald fertig (auch wenn nichts gefunden wurde).</summary>
    public static bool Preload(string? bankPath, MapStreamer st)
    {
        if (bankPath == null) return true;
        if (_lib != null && _libPath == bankPath) return true;
        if (_libTask == null || _libPath != bankPath)
        {
            _libPath = bankPath; _lib = null;
            var tex = st.Textures;
            _libTask = Task.Run(() => LoadLibrary(bankPath, tex));
        }
        if (!_libTask.IsCompleted) return false;
        if (_libTask.IsFaulted) { Log.Warn("worldactors.bnk: " + _libTask.Exception?.GetBaseException().Message); _lib = new Library { Path = bankPath }; }
        else _lib = _libTask.Result;
        if (!string.IsNullOrEmpty(_lib.Log)) Log.Info("Rennschul-Props: " + _lib.Log);
        return true;
    }

    /// <summary>Eine Actor-Bank (entities.xmb + hashcodes.xmb) zum Auflösen von Actor-Modellen.</summary>
    sealed class ActorBank
    {
        public string Path = "";
        public BankModel Model = null!;
        public XmbNode? Ent;
        public readonly Dictionary<ulong, BnkEntry> ByRsc = new();

        public static ActorBank? Open(string? path, List<string> log, GlobalTextureCache tex)
        {
            if (path == null || !GameFiles.Exists(path)) return null;
            var ab = new ActorBank { Path = path };
            ab.Model = BankModel.Load(path, BankKind.Generic, false, false, false);
            var bank = ab.Model.Bank;
            tex.IndexBank(bank);
            try { TextureAnimations.PrepareUva(bank); } catch (Exception) { }   // Tore: Lauflicht (.uva) wie die Interieurs
            var entEntry = bank.Find("entities.xmb");
            var hcEntry = bank.Find("hashcodes.xmb");
            if (entEntry == null || hcEntry == null) { log.Add("entities.xmb/hashcodes.xmb fehlt in " + System.IO.Path.GetFileName(path)); return ab; }
            ab.Ent = XmbFile.Parse(Unwrap(bank.Read(entEntry)));
            var hc = XmbFile.Parse(Unwrap(bank.Read(hcEntry)));
            var byIndex = new Dictionary<int, BnkEntry>();
            foreach (var e in bank.Entries) byIndex[e.Index] = e;
            var groups = hc.Get("___CmdSaveFileInfoArray");
            if (groups != null)
                foreach (var g in groups.Items)
                {
                    var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                    if (idx == null || refs == null) continue;
                    int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
                    for (int i = 0; i < n; i++)
                        if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) ab.ByRsc[refs.Items[i].AsULong()] = be;
                }
            return ab;
        }

        /// <summary>Alle Einträge (Array-Name, Objekt) aus entities.xmb.</summary>
        public IEnumerable<(string array, XmbNode item)> Items()
        {
            if (Ent == null) yield break;
            foreach (var section in Ent.Children)
            {
                if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
                foreach (var arr in section.Children)
                    if (arr.IsArray) foreach (var it in arr.Items) yield return (arr.Name, it);
            }
        }

        public ObjectSet? Load(XmbNode it, string actor, List<string> log)
        {
            var bank = Model.Bank;
            var mesh = it.Get("mesh") ?? it.Get("Model") ?? it.Get("Mesh");
            BnkEntry? dd = null, dg = null, dm = null;
            if (mesh != null)
                foreach (var r in mesh.Items)
                {
                    if (!ByRsc.TryGetValue(r.U("rsc"), out var be) || be == null) continue;
                    int sub = r.I("rscSubType", -1);
                    if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
                }
            // Rückfall: Modell über den Namen (Stamm = __Name ohne "_act")
            if (dd == null || dg == null)
            {
                string stem = actor.EndsWith("_act", StringComparison.OrdinalIgnoreCase) ? actor[..^4] : actor;
                foreach (var e in bank.Entries)
                {
                    if (!e.Name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Extension == ".3dd" && dd == null) dd = e;
                    else if (e.Extension == ".3dg" && dg == null) dg = e;
                    else if (e.Extension == ".2dm" && dm == null) dm = e;
                }
            }
            if (dd == null || dg == null) { log.Add(actor + ": Modell nicht aufgelöst"); return null; }
            try
            {
                var set = ObjectSet.Load(bank, dd, dg, dm, log);
                set.Placements = null;
                log.Add(actor + " -> " + dd.Name + " (" + System.IO.Path.GetFileName(Path) + ")");
                return set;
            }
            catch (Exception ex) { log.Add(actor + ": " + ex.Message); return null; }
        }

        public ObjectSet? Find(string actor, List<string> log)
        {
            foreach (var (_, it) in Items())
                if (string.Equals(it.S("__Name", ""), actor, StringComparison.OrdinalIgnoreCase)) return Load(it, actor, log);
            return null;
        }
    }

    static Library LoadLibrary(string path, GlobalTextureCache tex)
    {
        var lib = new Library { Path = path };
        var log = new List<string>();
        var wa = ActorBank.Open(path, log, tex);
        if (wa == null) { lib.Log = "worldactors.bnk fehlt"; return lib; }
        lib.World = wa;
        ObjectSet? Find(string actor)
        {
            var set = wa.Find(actor, log);
            if (set == null) log.Add(actor + " nicht in worldactors.bnk");
            else lib.BankOf[set] = wa;
            return set;
        }
        lib.Cone = Find(ConeActor);
        lib.SeparA = Find(SeparActorA);
        lib.SeparB = Find(SeparActorB);

        // Checkpoint-Tore: pack_cp.bnk (EXE: "Scenarics\Packs\pack_cp.bnk", liegt im Insel-Ordner neben worldactors.bnk),
        // zusätzlich worldactors.bnk. Klasse Entity__Actor__CheckPoint (Shader td_checkpoint).
        var banks = new List<ActorBank>();
        string? cpPath = null;
        string packsDir = Path.GetDirectoryName(path) ?? "";
        // pack_cp.bnk liegt in gepackten Installationen in den .big-Dateien. Der direkte Pfad findet
        // den Eintrag über seinen Hash in der .map (GameFiles.TryGetEntry); GetFiles bleibt als
        // Fallback für lose Dateien/ältere Namenslisten erhalten.
        string cpDirect = Path.Combine(packsDir, "pack_cp.bnk");
        if (GameFiles.Exists(cpDirect)) cpPath = cpDirect;
        if (cpPath == null)
        {
            try
            {
                foreach (var f in GameFiles.GetFiles(packsDir, "*.bnk"))
                    if (string.Equals(Path.GetFileName(f), "pack_cp.bnk", StringComparison.OrdinalIgnoreCase)) cpPath = f;
            }
            catch (Exception) { }
        }
        if (cpPath != null)
        {
            try { var cp = ActorBank.Open(cpPath, log, tex); if (cp != null) banks.Add(cp); }
            catch (Exception ex) { log.Add("pack_cp.bnk: " + ex.Message); }
        }
        else log.Add("pack_cp.bnk nicht gefunden");
        banks.Add(wa);

        foreach (var ab in banks)
        {
            var all = new List<string>();
            var arrays = new HashSet<string>();
            foreach (var (arr, it) in ab.Items())
            {
                arrays.Add(arr);
                var nm = it.S("__Name", "") ?? "";
                bool isCp = arr.IndexOf("CheckPoint", StringComparison.OrdinalIgnoreCase) >= 0
                            || (ab != wa && (it.Get("mesh") != null || it.Get("Model") != null));
                if (!isCp || nm.Length == 0) continue;
                all.Add(nm);
                bool hasMesh = (it.Get("mesh")?.Items.Count ?? 0) > 0 || it.Get("Model") != null || it.Get("Mesh") != null;   // Gate_Main: leeres mesh-Array
                var set = hasMesh || it.Get("CheckPointType") == null ? ab.Load(it, nm, log) : null;
                if (set != null)
                {
                    lib.BankOf[set] = ab;
                    if (!lib.Named.ContainsKey(nm)) lib.Named[nm] = set;
                }
                if (arr.IndexOf("CheckPoint", StringComparison.OrdinalIgnoreCase) >= 0 && it.Get("CheckPointType") != null)
                    lib.CheckPoints.Add(new CpRecord { Name = nm, Type = it.I("CheckPointType", 0), OffRoad = it.I("OffRoadCheckpoint", 0), Set = set });
            }
            // Kind-Actors (Actor1 / Actor2 = __Hc eines Entity__Actor derselben Bank; exe 0x8ac792 klont sie)
            var byHc = new Dictionary<ulong, (string name, XmbNode item)>();
            foreach (var (_, it) in ab.Items()) { ulong hc = it.U("__Hc", 0); if (hc != 0) byHc[hc] = (it.S("__Name", "") ?? "", it); }
            foreach (var (_, it) in ab.Items())
            {
                if (it.Get("CheckPointType") == null) continue;
                var rec = lib.CheckPoints.Find(r => string.Equals(r.Name, it.S("__Name", ""), StringComparison.OrdinalIgnoreCase));
                if (rec == null || rec.Children.Count > 0) continue;
                foreach (var key in new[] { "Actor1", "Actor2" })
                {
                    ulong hc = it.U(key, 0);
                    if (hc == 0) continue;
                    if (!byHc.TryGetValue(hc, out var child)) { log.Add(rec.Name + "." + key + ": Entity " + hc + " nicht gefunden"); continue; }
                    var cs = ab.Load(child.item, child.name, log);
                    if (cs == null) continue;
                    lib.BankOf[cs] = ab;
                    rec.Children.Add((child.name, cs));
                }
            }
            log.Add(Path.GetFileName(ab.Path) + ": Checkpoint-Actors [" + string.Join(", ", all) + "], Arrays [" + string.Join(", ", arrays) + "]");
        }
        lib.Log = string.Join("; ", log);
        return lib;
    }

    /// <summary>XMBF ggf. aus dem 12-Byte-Wrapper lösen.</summary>
    static byte[] Unwrap(byte[] b)
    {
        if (b == null || XmbFile.IsXmb(b)) return b!;
        for (int i = 1; i + 4 <= b.Length && i < 64; i++)
            if (b[i] == 'X' && b[i + 1] == 'M' && b[i + 2] == 'B' && b[i + 3] == 'F')
            {
                var r = new byte[b.Length - i];
                Array.Copy(b, i, r, 0, r.Length);
                return r;
            }
        return b;
    }

    // ------------------------------------------------------------------ Aufbau
    /// <summary>Props und Tore der Prüfung aufbauen (Spielkoordinaten).</summary>
    public void Build(MapStreamer st, RaceDefinition race, List<RaceGate>? gates, bool props)
    {
        Clear();
        _st = st;

        if (props)
        {
            // Platzierungen sammeln (Inselkoordinaten)
            var cones = new List<Matrix4x4>();
            var separA = new List<Matrix4x4>();
            var separB = new List<Matrix4x4>();
            float sepLen = SeparLength(st);
            foreach (var d in race.Decors)
            {
                if (!d.IsLine)
                {
                    var m = Trs(Ground(st, d.From), d.Forward);
                    (d.IsSeparator ? separA : cones).Add(m);
                    continue;
                }
                var a = d.From; var b = d.To;
                var dir = b - a; dir.Y = 0f;
                float len = dir.Length();
                if (len < 0.05f) { (d.IsSeparator ? separA : cones).Add(Trs(Ground(st, a), Vector3.UnitZ)); continue; }
                dir /= len;
                if (d.IsSeparator)
                {
                    // Absperrungen Stoß an Stoß quer zur Linie ausgerichtet entlang der Linie, Endstück B am Ende
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / sepLen));
                    var f = _lib != null && !_lib.SeparAlongX ? dir : new Vector3(-dir.Z, 0f, dir.X);
                    for (int i = 0; i <= n; i++)
                    {
                        var p = Vector3.Lerp(a, b, i / (float)n);
                        (i == n ? separB : separA).Add(Trs(Ground(st, p), f));
                    }
                }
                else
                {
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / ConeSpacing));
                    for (int i = 0; i <= n; i++) cones.Add(Trs(Ground(st, Vector3.Lerp(a, b, i / (float)n)), dir));
                }
            }

            // Keine Zusatz-Kegel an Toren: CHK2-Art 1/2 sind selbst Fass-Entities aus pack_cp.bnk (exe 0x979f90 -> Typ 5/6),
            // die Bremszone ist das Entity BrakingZone_01 (Typ 7). Die früher an Tor-Seiten/Bremszonen-Ecken gesetzten Kegel gibt es nicht.

            // Startplatz freihalten: keine Kegel/Absperrungen auf dem Auto (Startpunkt aus spotworld.bin, Auto ~4.5 x 2 m)
            int removed = ClearStart(race, cones) + ClearStart(race, separA) + ClearStart(race, separB);
            if (removed > 0) Log.Info("Rennschule: " + removed + " Props am Startplatz entfernt");

            BuildSet(st, _lib?.Cone, cones, ConeActor, "Kegel");
            BuildSet(st, _lib?.SeparA, separA, SeparActorA, "Absperrung");
            BuildSet(st, _lib?.SeparB ?? _lib?.SeparA, separB, _lib?.SeparB != null ? SeparActorB : SeparActorA, "Absperrung");
            Source = _lib != null && (_lib.Cone != null || _lib.SeparA != null) ? "worldactors.bnk" : "keine Modelle (worldactors.bnk fehlt)";
        }

        // Tore in Inselkoordinaten
        if (gates != null)
        {
            foreach (var cp in race.Checkpoints)
            {
                var g = BuildGate(st, cp, race);
                if (g != null) gates.Add(g);
                else if (GateType(race, cp) != 0) Log.Warn($"Tor {cp.Index} ({cp.GateKind}) von {race.Name}: kein Modell in pack_cp.bnk");
            }
            // Nur Prüfungen mit freigestellten Toren zeigen das Band auf der Fahrbahn. Bei Straßentoren
            // (und damit bei allen Rennen) übernimmt stattdessen die Minimap die Routenanzeige.
            if (RaceConfig.DrivingLine(race)) BuildTrajectory(st, race);
            else Log.Debug($"{race.Name}: kein Straßenband (Tore an Straßen, Flag ShowDrivingLine = false)");
        }
    }

    static Matrix4x4 Trs(Vector3 pos, Vector3 forward)
    {
        var f = forward; f.Y = 0f;
        f = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitZ;
        var r = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f));   // Unity LookRotation(f, up): rechts = up x f
        return new Matrix4x4(r.X, r.Y, r.Z, 0f, 0f, 1f, 0f, 0f, f.X, f.Y, f.Z, 0f, pos.X, pos.Y, pos.Z, 1f);
    }

    static int ClearStart(RaceDefinition race, List<Matrix4x4> list)
    {
        var sp = race.StartPosition;
        return list.RemoveAll(m =>
        {
            float dx = m.M41 - sp.X, dz = m.M43 - sp.Z;
            return dx * dx + dz * dz < StartClearRadius * StartClearRadius;
        });
    }

    /// <summary>Ein Set an den angegebenen Stellen als Szene bauen (Materialien/Texturen wie die Map) und zeichnen lassen.</summary>
    MapStreamer.SceneHandle? BuildFromLibrary(MapStreamer st, ObjectSet set, List<Matrix4x4> placements, bool show = true)
    {
        if (_lib == null || !_lib.BankOf.TryGetValue(set, out var ab)) return null;
        var saved = set.Placements;
        var wrap = new BankModel { Path = ab.Model.Path, Kind = BankKind.Exterior, Bank = ab.Model.Bank, Objects = set };
        set.Placements = placements;
        MapStreamer.SceneHandle h;
        try { h = st.BuildScene(wrap, Matrix4x4.Identity, false); }
        finally { set.Placements = saved; }
        h.Model = null;   // gemeinsame Bank der Bibliothek – beim Abbau nicht freigeben
        if (show) { st.Scenes.Add(h); _scenes.Add(h); }
        return h;
    }

    /// <summary>
    /// Tor-Materialien wie im Original (pack_cp.bnk, .2DM MAT.+0x40): Die Schrift-Tafeln (checkpoint_A, start_A_02, finish_A) sind
    /// opak mit Alpha-Test und einseitig (Flag[3] = 2). Dass beidseitig gezeichnet normale und gespiegelte Schrift übereinanderlagen,
    /// zeigt: Vorder- und Rückseite sind eigene Flächen, jede nur von vorn sichtbar (Geometrie selbst nicht ausgelesen).
    /// Die Rahmen/Lauflichter (Line_Pulse_*, Outside_*, Radar_*) sind additiv (SrcAlpha/One), teils ein-, teils zweiseitig.
    /// Diese Materialien baut <see cref="MaterialFactory"/> schon richtig; nur die additiven werden hier unbeleuchtet, leuchtend und
    /// unabhängig von Tag/Nacht (die Fabrik blendet Leuchtmaterialien sonst nachts-only aus) neu angelegt – mit Culling und Farbe
    /// des Originals. Vorher wurde alles zweiseitig-additiv gezeichnet: Vorder- und Rückseitenschrift lagen übereinander (gespiegelt,
    /// unlesbar) und eine erfundene Orange-Tönung färbte alle Tore gleich. Die .uva-Lauflicht-Animation bleibt erhalten.
    /// </summary>
    static void ConvertGateMaterials(MapStreamer.SceneHandle h)
    {
        var r = MaterialFactory.Renderer;
        if (r == null) return;
        foreach (var o in h.Objects)
            foreach (var l in o.Lods)
                foreach (var p in l.Parts)
                {
                    var src = p.Material;
                    if (src == null) continue;
                    var info = MaterialFactory.TryGetInfo(src, out var i) ? i : null;
                    var srcDesc = info?.Desc;
                    if (srcDesc == null || !srcDesc.Additive) continue;   // opak/Alpha-Test: Original-Material bleibt
                    // gleiche Tore teilen sich ein Material (Name, Textur, Culling)
                    string key = (srcDesc.Name ?? src.GetType().Name) + "|" + (info?.MainTexture ?? "") + "|" + srcDesc.TwoSided;
                    if (!_gateMaterialCache.TryGetValue(key, out var gm))
                    {
                        var desc = new MaterialDesc
                        {
                            Kind = MaterialKind.Transparent,
                            Name = "Checkpoint_" + (srcDesc.Name ?? src.GetType().Name),
                            Main = srcDesc.Main,
                            Color = srcDesc.Color,
                            TwoSided = srcDesc.TwoSided,   // Original-Culling (Flag[3]: 2 einseitig, 3 beidseitig)
                            Additive = true,
                            Lit = 0f,
                            Unlit = 1f,
                            Intensity = 1f,
                            Glow = true,
                            Cutoff = 0f,
                        };
                        gm = r.CreateMaterial(desc);
                        _gateMaterialOwned.Add(gm);   // dauerhaft, nicht im Besitz einer einzelnen Tor-Szene
                        _gateMaterialCache[key] = gm;
                    }
                    TextureAnimations.Transfer(src, gm);   // Lauflicht (.uva) auf das neue Material mitnehmen
                    p.Material = gm;
                }
    }

    void BuildSet(MapStreamer st, ObjectSet? set, List<Matrix4x4> placements, string actor, string label)
    {
        if (placements.Count == 0) return;
        if (set == null) { Log.Warn($"Prüfung: {placements.Count} {label} ohne Modell ({actor} fehlt)"); return; }
        var bodies = WorldActorBodies.Current;
        int n = 0;
        foreach (var m in placements)
        {
            try
            {
                var h = BuildFromLibrary(st, set, new List<Matrix4x4> { m });
                if (h == null) continue;
                n++;
                // Unity Physicalize: Box aus den Bounds, Anstoß = Treffer (hier: World Actor, fällt um wie im Spiel)
                var stc = bodies?.Register(Key, actor, h.Objects, m);
                if (stc != null) _colliders.Add(stc);
            }
            catch (Exception ex) { Log.Warn("Prop-Aufbau " + label + ": " + ex.Message); }
        }
        if (bodies == null && n > 0) Log.Warn($"Prüfung: {label} ohne Kollision (World Actors der Insel noch nicht geladen)");
        Count += n;
    }

    float SeparLength(MapStreamer st)
    {
        var set = _lib?.SeparA;
        if (set == null || _lib == null) return FallbackSeparLength;
        if (_lib.SeparMeasured) return _lib.SeparLength;
        _lib.SeparMeasured = true;
        // Länge der Absperrung messen: einmal an der Identität bauen, Bounds im lokalen Raum
        var h = BuildFromLibrary(st, set, new List<Matrix4x4> { Matrix4x4.Identity }, false);
        float ex = 0f, ez = 0f;
        if (h != null)
        {
            foreach (var o in h.Objects) { ex = MathF.Max(ex, o.BoundsMax.X - o.BoundsMin.X); ez = MathF.Max(ez, o.BoundsMax.Z - o.BoundsMin.Z); }
            h.Res.Destroy();
        }
        float best = MathF.Max(ex, ez);
        _lib.SeparAlongX = ex >= ez;
        _lib.SeparLength = best > 0.3f && best < 30f ? best : FallbackSeparLength;
        return _lib.SeparLength;
    }

    /// <summary>
    /// CheckPointType eines Tors wie exe 0x979f90: Tor 0 = 1 (Start). Danach nach Spielart (Tabelle 0x97a078 über gmth 0..12):
    /// Zeitfahren/Rennen/Ereignis/HotSpots -> nach CHK2-Art (1 -> 5 Fass, 2 -> 6 Gate_Main, 3 -> 7 Bremszone, 5 -> 8, 6 -> 9, sonst
    /// letztes Tor 2 Ziel, andere 3 Checkpoint); Radarfalle -> 4; Abstand halten / Folge dem Führenden -> letztes 2, sonst 3;
    /// alle übrigen (Fahrprüfung 2, Tempo 10, 4-6, 8) -> 0 = kein Tor.
    /// </summary>
    public static int GateType(RaceDefinition race, RaceCheckpoint cp)
    {
        if (cp.Index == 0) return 1;
        bool last = cp.Index == race.Checkpoints.Count - 1;
        uint mode = (uint)race.Kind;
        int sel = mode <= 12 ? new[] { 0, 1, 4, 0, 4, 4, 4, 0, 4, 0, 2, 3, 3 }[mode] : 4;
        switch (sel)
        {
            case 0:
                return cp.Kind switch { 1 => 5, 3 => 7, 2 => 6, 6 => 9, 5 => 8, _ => last ? 2 : 3 };
            case 1: return 4;
            case 3: return last ? 2 : 3;
            default: return 0;
        }
    }

    /// <summary>OffRoadCheckpoint wie exe 0x980ea8: Ereignis-Ziel 2 (Finishline_Event), Tor an einer Straße 0, frei gesetzt 1.</summary>
    public static int GateVariant(RaceDefinition race, RaceCheckpoint cp, int type)
    {
        if (race.Kind == RaceMode.Event && type == 2) return 2;
        // exe: Straßenverweis vorhanden, Flag +0x60 = 0 und Prüfung 0x4327b3 (Straße selbst) negativ -> 0. Die Prüfung der Straße ist
        // nicht nachgebaut [vermutet: Geländestraße]; hier entscheidet allein der Straßenverweis (Placement 0).
        return cp.OnRoad ? 0 : 1;
    }

    /// <summary>
    /// Halber Abstand der beiden Fässer eines Gate_Main wie exe 0x8ad3a5: ohne Tor-Datensatz 3,5 m; frei gesetztes Tor (0x9796d0):
    /// CHK2-Art &lt;= 4 -> Torbreite * 0,5, sonst 20 * 0,5; Straßentor: (Profil +0x24 + +0x28) * 0,5 / 1,4 – im IA-Profil sind das
    /// Width (+36) und TotalWidth (+40) [vermutet: Zuordnung der Speicher- auf die Dateifelder]. Ohne Straßendaten: Torbreite * 0,5.
    /// </summary>
    static float BarrelHalfSpan(RaceCheckpoint cp)
    {
        if (!cp.OnRoad) return (cp.Kind <= 4 ? cp.Width : 20f) * 0.5f;
        var net = RaceRoadPath.Lanes?.Net;
        if (net != null && net.Roads.TryGetValue(cp.Road, out var road) && cp.Piece >= 0 && cp.Piece < road.Pieces.Count
            && net.Profiles.TryGetValue(road.Pieces[cp.Piece].ProfileId, out var prof))
            return (prof.Width + prof.TotalWidth) * 0.5f / 1.4f;
        Log.Debug($"Tor {cp.Index}: Straßenprofil {cp.Road}/{cp.Piece} nicht gefunden – Fässer bei Torbreite");
        return cp.Width * 0.5f;
    }

    /// <summary>Entity-Suche wie exe 0x8926d0: exakt (Typ, Variante), sonst der letzte Eintrag gleichen Typs.</summary>
    static CpRecord? FindCheckPoint(int type, int variant)
    {
        if (_lib == null) return null;
        CpRecord? any = null;
        foreach (var r in _lib.CheckPoints)
        {
            if (r.Type != type) continue;
            if (r.OffRoad == variant) return r;
            any = r;
        }
        return any;
    }

    /// <summary>
    /// Original-Tor aus pack_cp.bnk (Entity__Actor__CheckPoint) an einem Checkpoint: Typ und Variante wie die exe (siehe
    /// <see cref="GateType"/>, <see cref="GateVariant"/>), gesetzt mit der CHK2-Matrix der Datei (Achsen samt ihrer Länge, Position;
    /// exe 0x980ee0/0x97e330) – nicht mehr auf die Torbreite gestaucht (Straßentore sind 7 m breit, die Bögen 24,5 m: im Original
    /// stehen sie in voller Größe). Gate_Main: die beiden Kind-Actors (Fässer) in ihrer Lage im Tor.
    /// </summary>
    RaceGate? BuildGate(MapStreamer st, RaceCheckpoint cp, RaceDefinition race)
    {
        if (_lib == null) return null;
        int type = GateType(race, cp);
        if (type == 0) return null;
        int variant = GateVariant(race, cp, type);
        var rec = FindCheckPoint(type, variant);
        if (rec == null || (rec.Set == null && rec.Children.Count == 0))
        {
            Log.Warn($"Tor {cp.Index}: CheckPointType {type}/{variant} {(rec == null ? "nicht in pack_cp.bnk" : rec.Name + " ohne Modell")}");
            return null;
        }

        var gy = Ground(st, cp.Position).Y;
        var r = cp.Right.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Right) : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, cp.Forward));
        var f = cp.Forward.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Forward) : Vector3.UnitZ;
        var u = cp.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Up) : Vector3.UnitY;
        var s = cp.AxisScale;
        var m = new Matrix4x4(r.X * s.X, r.Y * s.X, r.Z * s.X, 0f, u.X * s.Y, u.Y * s.Y, u.Z * s.Y, 0f, f.X * s.Z, f.Y * s.Z, f.Z * s.Z, 0f,
                              cp.Position.X, gy, cp.Position.Z, 1f);
        Log.Debug($"Tor {cp.Index}: Typ {type}/{variant} -> {rec.Name}, Achsen {s.X:0.00}/{s.Y:0.00}/{s.Z:0.00}, f=({f.X:0.00},{f.Z:0.00})");

        var g = new RaceGate { Index = cp.Index, Checkpoint = cp, Model = rec.Name };
        void Add(ObjectSet set, Matrix4x4 at, string label)
        {
            try
            {
                var h = BuildFromLibrary(st, set, new List<Matrix4x4> { at });
                if (h == null) return;
                ConvertGateMaterials(h);
                g.Scenes.Add(h);
            }
            catch (Exception ex) { Log.Warn("Tormodell " + label + ": " + ex.Message); }
        }
        if (rec.Set != null) Add(rec.Set, m, rec.Name);
        if (rec.Children.Count > 0)
        {
            // exe 0x8ad4da..0x8ad6a2: Actor1 bei Position - X-Achse * h, Actor2 bei Position + X-Achse * h (Achse samt Matrix-Skalierung);
            // bei CHK2-Art 2 wird Actor1 um 180° gedreht (0x8ad6c8, Drehung pi), damit beide Fässer zur Mitte schauen.
            float h = BarrelHalfSpan(cp);
            var x = new Vector3(m.M11, m.M12, m.M13);
            var pos = new Vector3(m.M41, m.M42, m.M43);
            for (int i = 0; i < rec.Children.Count && i < 2; i++)
            {
                var cm = m;
                if (i == 0 && cp.Kind == 2 && rec.Children.Count > 1) cm = Matrix4x4.CreateRotationY(MathF.PI) * cm;
                var cpPos = i == 0 ? pos - x * h : pos + x * h;
                var cg = Ground(st, cpPos);
                cm.M41 = cpPos.X; cm.M42 = cg.Y; cm.M43 = cpPos.Z;
                Add(rec.Children[i].set, cm, rec.Children[i].name);
            }
            Log.Debug($"Tor {cp.Index} {rec.Name}: Fässer ±{h:0.00} m");
        }
        if (g.Scenes.Count == 0) return null;
        g.SetState(RaceGateState.Pending);
        return g;
    }

    /// <summary>
    /// Wegmarkierung auf der Fahrbahn: Actor "Trajectory" aus pack_cp.bnk entlang der (geglätteten) Route zwischen den Toren,
    /// alle <see cref="TrajectorySpacing"/> m, in Fahrtrichtung. [vermutet: Abstand]
    /// </summary>
    void BuildTrajectory(MapStreamer st, RaceDefinition race)
    {
        if (_lib == null || !_lib.Named.TryGetValue("Trajectory", out var set) || race.Checkpoints.Count < 2) return;
        // dichte Linie: Straßenroute (RaceRoadPath), sonst Catmull-Rom durch die Tore
        var dense = new List<Vector3>();
        if (race.RoadPath != null && race.RoadPath.Count > 1) dense.AddRange(race.RoadPath);
        else
        {
            var seq = race.TargetSequence();
            var pts = new List<Vector3> { race.StartPosition };
            foreach (int i in seq) pts.Add(race.Checkpoints[i].Position);
            dense.Add(pts[0]);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector3 P(int q) => pts[Mathf.Clamp(q, 0, pts.Count - 1)];
                Vector3 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                int sub = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p1, p2) / 1f), 2, 400);
                for (int k = 1; k <= sub; k++)
                {
                    float uu = k / (float)sub, u2 = uu * uu, u3 = u2 * uu;
                    dense.Add(0.5f * (2f * p1 + (-p0 + p2) * uu + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3));
                }
            }
        }
        // Ausrichtung des Modells: Längsachse X -> um 90° drehen, damit der Streifen in Fahrtrichtung liegt
        float yaw = TrajectoryYaw;
        if (float.IsNaN(yaw))
        {
            // Modellmaße aus einem Probeaufbau an der Identität (Unity: Mesh-Positionen)
            float ex = 0f, ez = 0f;
            var probe = BuildFromLibrary(st, set, new List<Matrix4x4> { Matrix4x4.Identity }, false);
            if (probe != null) { foreach (var o in probe.Objects) { ex = MathF.Max(ex, o.BoundsMax.X - o.BoundsMin.X); ez = MathF.Max(ez, o.BoundsMax.Z - o.BoundsMin.Z); } probe.Res.Destroy(); }
            yaw = ex > ez * 1.2f ? 90f : 0f;
            Log.Info($"Trajectory-Modell: Ausdehnung X {ex:0.00} m, Z {ez:0.00} m -> Drehung {yaw}°");
        }
        var rotY = Matrix4x4.CreateRotationY(yaw * Mathf.Deg2Rad);
        var marks = new List<Matrix4x4>();
        float carry = TrajectorySpacing * 0.5f;
        for (int i = 1; i < dense.Count; i++)
        {
            var prev = dense[i - 1]; var q = dense[i];
            var d = q - prev; d.Y = 0f;
            float len = d.Length();
            if (len < 1e-4f) continue;
            float walked = 0f;
            while (carry <= len - walked)
            {
                walked += carry;
                carry = TrajectorySpacing;
                var at = prev + (q - prev) * (walked / len);
                bool nearGate = false;
                foreach (var cp in race.Checkpoints) { var g = cp.Position - at; g.Y = 0f; if (g.LengthSquared() < 9f) { nearGate = true; break; } }
                if (!nearGate) marks.Add(Matrix4x4.CreateScale(TrajectoryScale) * rotY * Trs(Ground(st, at) + new Vector3(0f, 0.02f, 0f), d / len));
            }
            carry -= len - walked;
        }
        if (marks.Count == 0) return;
        try { BuildFromLibrary(st, set, marks); }
        catch (Exception ex) { Log.Warn("Trajectory: " + ex.Message); }
    }

    static Vector3 Ground(MapStreamer st, Vector3 island)
    {
        float y = island.Y;
        if (st.RaycastDown(island, 30f, 80f, out var hy)) y = hy;
        else if (st.TryGetGroundHeight(island, out var gy)) y = gy;
        return new Vector3(island.X, y, island.Z);
    }

    public void Clear()
    {
        WorldActorBodies.Current?.RemoveSector(Key);
        var phys = _st?.Physics;
        foreach (var c in _colliders) phys?.Remove(c);
        _colliders.Clear();
        if (_st != null) foreach (var h in _scenes) { _st.Scenes.Remove(h); h.Res.Destroy(); }
        _scenes.Clear();
        Count = 0;
    }

    /// <summary>Endgültig freigeben (Ereignis abmelden).</summary>
    public void Dispose() { Clear(); WorldActorBodies.Broken -= OnBroken; }
}
