// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceProps.cs + RaceGateVisual.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Racing;
using Paradise.Core.Render;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Game.Racing;

/// <summary>Zustand eines Tors (Unity RaceGateVisual).</summary>
public enum RaceGateState { Pending, Next, Passed }

/// <summary>
/// Tor einer Prüfung (Unity RaceGateVisual mit Original-Modell): An-/Aus-Variante (Start_line/_Off, Finish_line/_Off).
/// Nächstes Tor = "an", spätere Tore = "aus" (bzw. "an", wenn es keine Aus-Variante gibt), passierte Tore ausgeblendet.
/// </summary>
public sealed class RaceGate
{
    public int Index;
    public RaceCheckpoint Checkpoint = null!;
    internal MapStreamer.SceneHandle? On, Off;
    public RaceGateState State { get; private set; }

    public void SetState(RaceGateState state)
    {
        State = state;
        bool showOn = state == RaceGateState.Next || Off == null;
        if (On != null) On.Active = state != RaceGateState.Passed && showOn;
        if (Off != null) Off.Active = state != RaceGateState.Passed && !showOn;
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
        /// <summary>Bank, aus der ein Set stammt (worldactors.bnk oder pack_cp.bnk).</summary>
        public readonly Dictionary<ObjectSet, ActorBank> BankOf = new();
        public readonly Dictionary<ObjectSet, float> GateWidth = new();
        public string Log = "";
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
        try
        {
            foreach (var f in GameFiles.GetFiles(Path.GetDirectoryName(path) ?? "", "*.bnk"))
                if (string.Equals(Path.GetFileName(f), "pack_cp.bnk", StringComparison.OrdinalIgnoreCase)) cpPath = f;
        }
        catch (Exception) { }
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
                var set = ab.Load(it, nm, log);
                if (set == null) continue;
                lib.BankOf[set] = ab;
                if (!lib.Named.ContainsKey(nm)) lib.Named[nm] = set;
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

            // Kegel an den Seiten der Kegel-Tore (CHK2-Art 1) und an den Ecken der Bremszone
            foreach (var cp in race.Checkpoints)
            {
                if (cp.GateKind == Ige2GateKind.Cones)
                {
                    cones.Add(Trs(Ground(st, cp.Position + cp.Right * (cp.Width * 0.5f)), cp.Forward));
                    cones.Add(Trs(Ground(st, cp.Position - cp.Right * (cp.Width * 0.5f)), cp.Forward));
                }
                else if (cp.IsBrakingZone)
                {
                    float hw = cp.Width * 0.5f, hd = cp.ZoneDepth * 0.5f;
                    foreach (var sx in new[] { -1f, 1f })
                        foreach (var sz in new[] { -1f, 1f })
                            cones.Add(Trs(Ground(st, cp.Position + cp.Right * (hw * sx) + cp.Forward * (hd * sz)), cp.Forward));
                }
            }

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
                else Log.Warn($"Tor {cp.Index} ({cp.GateKind}) von {race.Name}: kein Modell in pack_cp.bnk");
            }
            BuildTrajectory(st, race);
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
    /// Unity RaceProps.ConvertMaterials (TDU2/Checkpoint): die Tore werden nicht mit den normalen 2dm-Materialien
    /// gezeichnet, sondern als additives, unlit, doppelseitiges Overlay (SrcAlpha/One, ZWrite aus, RenderQueue 3000).
    /// Ohne diesen Schritt sind die aus pack_cp.bnk gebauten Portal-Rahmen zwar da, aber im Spiel praktisch unsichtbar.
    /// Die .uva-Lauflicht-Animation bleibt über TextureAnimations.Apply (BuildScene) erhalten.
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
                    // Unity: _gateMatsByName[(mainTexture, name)] – gleiche Tore teilen sich ein Material.
                    string key = (srcDesc?.Name ?? src.GetType().Name) + "|" + (info?.MainTexture ?? "");
                    if (!_gateMaterialCache.TryGetValue(key, out var gm))
                    {
                        // Unity ConvertMaterials: Tönung des Originalmaterials als _Tint, weiße Default-Tönung
                        // (z.B. occlu_white) auf die Checkpoint-Farbe setzen, sonst ist das Tor weiß statt orange.
                        var tint = srcDesc?.Color ?? new Vector4(1f, 1f, 1f, 1f);
                        bool white = tint.X > 0.95f && tint.Y > 0.95f && tint.Z > 0.95f;
                        if (white) tint = new Vector4(1f, 0.45f, 0.08f, 1f);   // Original-Checkpoint-Orange
                        var desc = new MaterialDesc
                        {
                            Kind = MaterialKind.Transparent,
                            Name = "Checkpoint_" + (srcDesc?.Name ?? src.GetType().Name),
                            Main = srcDesc?.Main,
                            Color = tint,
                            TwoSided = true,
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
    /// Original-Tor aus pack_cp.bnk (Entity__Actor__CheckPoint) an einem Checkpoint, ausgerichtet nach der CHK2-Matrix
    /// (Right/Up/Forward wie im Spiel, daher auch Schrift lesbar) und auf die Torbreite skaliert.
    /// Start: Start_line / Start_line_Off, Ziel: Finish_line / Finish_line_Off, Bremszone: BrakingZone_01,
    /// sonst Check_point (Dirt-Schule: CP_OffRoad). "_Off" = Variante, solange das Tor nicht das nächste ist.
    /// </summary>
    RaceGate? BuildGate(MapStreamer st, RaceCheckpoint cp, RaceDefinition race)
    {
        if (_lib == null || _lib.Named.Count == 0) return null;
        string on; string? off = null;
        if (cp.IsBrakingZone) on = "BrakingZone_01";
        else if (cp.IsStart && !race.IsCircuit) { on = "Start_line"; off = "Start_line_Off"; }
        else if (cp.IsFinish || (cp.IsStart && race.IsCircuit)) { on = "Finish_line"; off = "Finish_line_Off"; }
        else on = race.School == 'B' && _lib.Named.ContainsKey("CP_OffRoad") ? "CP_OffRoad" : "Check_point";
        if (!_lib.Named.TryGetValue(on, out var setOn)) return null;
        _lib.Named.TryGetValue(off ?? "", out var setOff);

        // Breite (lokal X) messen und auf die Checkpoint-Breite skalieren
        if (!_lib.GateWidth.TryGetValue(setOn, out float w))
        {
            var probe = BuildFromLibrary(st, setOn, new List<Matrix4x4> { Matrix4x4.Identity }, false);
            float ex = 0f;
            if (probe != null) { foreach (var o in probe.Objects) ex = MathF.Max(ex, o.BoundsMax.X - o.BoundsMin.X); probe.Res.Destroy(); }
            w = ex > 0.2f ? ex : 0f;
            _lib.GateWidth[setOn] = w;
        }
        float k = w > 0f && cp.Width > 0.5f ? cp.Width / w : 1f;

        var gy = Ground(st, cp.Position).Y;
        var r = cp.Right.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Right) : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, cp.Forward));
        var f = cp.Forward.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Forward) : Vector3.UnitZ;
        var u = cp.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Up) : Vector3.UnitY;
        // Unity: holder.transform = LookRotation(f, u) und danach localScale = (k, 1, 1).
        // Das Original-Tormodell trägt die Schrift auf der Rückseite (-Z laut CHK2-Matrix): ohne Drehung liest der
        // herannahende Fahrer "TNIOPKCEHC". Deshalb um Y um 180° drehen (-r, -f), damit die Schrift zu ihm zeigt.
        // In der row-vector-Konvention von System.Numerics entspricht das genau einer Multiplikation mit -1 auf
        // r/f, während u (Hochachse) unverändert bleibt.
        r = -r; f = -f;
        var m = new Matrix4x4(r.X * k, r.Y * k, r.Z * k, 0f, u.X, u.Y, u.Z, 0f, f.X, f.Y, f.Z, 0f, cp.Position.X, gy, cp.Position.Z, 1f);
        Log.Debug($"Tor {cp.Index} ({on}): cp.Width {cp.Width:0.00} m, Modell {w:0.00} m, Faktor {k:0.000}, " +
                  $"Richtung f=({f.X:0.00},{f.Z:0.00}) r=({r.X:0.00},{r.Z:0.00})");

        MapStreamer.SceneHandle? hOn = null, hOff = null;
        try { hOn = BuildFromLibrary(st, setOn, new List<Matrix4x4> { m }); }
        catch (Exception ex) { Log.Warn("Tormodell " + on + ": " + ex.Message); }
        if (hOn == null) return null;
        ConvertGateMaterials(hOn);
        // Start_line/_Off und Finish_line/_Off verweisen auf dieselbe Szene (verschiedene Knoten) -> nur einmal bauen
        if (setOff != null && setOff != setOn)
        {
            try { hOff = BuildFromLibrary(st, setOff, new List<Matrix4x4> { m }); }
            catch (Exception ex) { Log.Warn("Tormodell " + off + ": " + ex.Message); }
            if (hOff != null) ConvertGateMaterials(hOff);
        }
        var g = new RaceGate { Index = cp.Index, Checkpoint = cp, On = hOn, Off = hOff };
        g.SetState(cp.IsStart || cp.IsFinish ? RaceGateState.Next : RaceGateState.Pending);
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
