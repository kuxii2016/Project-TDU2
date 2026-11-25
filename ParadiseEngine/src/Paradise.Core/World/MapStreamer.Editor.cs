using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Szenen-Editor (F1 &gt; Open Editor, Projekt Paradise.Editor): Nur-lese-Sicht auf die gestreamten Zellen, Ausblenden einzelner
/// Areas/Sektoren und des Meeres sowie Strahltest für die Klick-Auswahl. Ausschließlich im Hauptthread (wie Render/Update).
/// </summary>
public sealed partial class MapStreamer
{
    /// <summary>Eine geladene Area/ein geladener Sektor (Unity: GameObject "Area_x_z" / "Sector_…" unter _root).</summary>
    public sealed class EditorCell
    {
        public string Kind = "", Key = "";
        public bool Built, Failed;
        public IReadOnlyList<Node> Nodes = Array.Empty<Node>();
        public PlacementRenderer? Placements;
        public string? Bank;
        public int ExtraModels, Colliders;
        public bool HasGrass, HasWires;
        internal object? State;
    }

    public List<EditorCell> EditorAreas()
    {
        var l = new List<EditorCell>(_areas.Count);
        foreach (var a in _areas.Values) l.Add(Cell(a));
        l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return l;
    }

    public List<EditorCell> EditorSectors()
    {
        var l = new List<EditorCell>(_sectors.Count);
        foreach (var s in _sectors.Values) l.Add(Cell(s));
        l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return l;
    }

    static EditorCell Cell(AreaState a) => new()
    {
        Kind = "Area",
        Key = a.Key.ToString(),
        Built = a.Built,
        Failed = a.Failed,
        Nodes = a.Nodes,
        Bank = a.Model?.Path,
        ExtraModels = a.ExtraModels.Count,
        Colliders = a.Colliders.Count,
        State = a,
    };

    static EditorCell Cell(SectorState s) => new()
    {
        Kind = "Sector",
        Key = s.Key.ToString(),
        Built = s.Built,
        Failed = s.Failed,
        Nodes = s.Nodes,
        Placements = s.Placements,
        Bank = s.Model?.Path,
        ExtraModels = s.ExtraModels.Count,
        Colliders = s.Colliders.Count,
        HasGrass = s.Grass != null,
        HasWires = s.Wires != null,
        State = s,
    };

    /// <summary>true, solange die Zelle noch geladen ist (sonst wurde sie entladen und ihre Objekte sind freigegeben).</summary>
    public bool EditorIsLoaded(EditorCell c) => c.State switch
    {
        AreaState a => _areas.TryGetValue(a.Key, out var cur) && ReferenceEquals(cur, a),
        SectorState s => _sectors.TryGetValue(s.Key, out var cur) && ReferenceEquals(cur, s),
        _ => false,
    };

    // ------------------------------------------------------------------ Ausblenden (Zeichnen überspringt die Zelle, Kollision bleibt)
    readonly HashSet<object> _editorHidden = new(ReferenceEqualityComparer.Instance);
    /// <summary>Große Meeresfläche nicht zeichnen.</summary>
    public bool EditorHideWater;

    bool EditorIsHidden(object state) => _editorHidden.Count > 0 && _editorHidden.Contains(state);
    public bool EditorGetHidden(EditorCell c) => c.State != null && _editorHidden.Contains(c.State);

    public void EditorSetHidden(EditorCell c, bool hidden)
    {
        if (c.State == null) return;
        if (hidden) _editorHidden.Add(c.State); else _editorHidden.Remove(c.State);
        // entladene Zellen vergessen
        _editorHidden.RemoveWhere(o => o switch
        {
            AreaState a => !(_areas.TryGetValue(a.Key, out var cur) && ReferenceEquals(cur, a)),
            SectorState s => !(_sectors.TryGetValue(s.Key, out var cur) && ReferenceEquals(cur, s)),
            _ => true,
        });
    }

    /// <summary>Insel-LOD-Stücke (LOD2) je Area.</summary>
    public List<(string Key, Node Node)> EditorIslandPieces()
    {
        var l = new List<(string, Node)>(_islandPieces.Count);
        foreach (var kv in _islandPieces) l.Add((kv.Key.ToString(), kv.Value));
        l.Sort((x, y) => string.CompareOrdinal(x.Item1, y.Item1));
        return l;
    }

    /// <summary>Große Meeresfläche (Mesh, Material wie in Render, Position im Spielraum).</summary>
    public (IGpuMesh? Mesh, IGpuMaterial? Material, Vector3 PositionGame) EditorWater => (_waterMesh, _oceanIsland ?? MaterialFactory.Water, _waterPos);

    // ------------------------------------------------------------------ Klick-Auswahl
    public sealed class EditorPickHit
    {
        public float T, Volume;
        public EditorCell? Cell;
        public Node? Node;
        public RenderObject? Object;
        public SceneHandle? Scene;
        public int Batch = -1, Instance = -1;
    }

    /// <summary>
    /// Strahl (Spielraum) gegen alle gezeichneten Objekte: Bounds der RenderObjects in Areas/Sektoren/Szenen und die Boxen der
    /// Platzierungen. Sortierung wie <see cref="Pick"/>: zuerst Boxen vor der Kamera nach Abstand, dann umschließende nach Größe.
    /// </summary>
    public List<EditorPickHit> EditorPick(Vector3 origin, Vector3 dir)
    {
        dir = Vector3.Normalize(dir);
        var hits = new List<EditorPickHit>();
        void TestNodes(EditorCell? cell, IEnumerable<Node> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.Active) continue;
                foreach (var ro in n.Objects)
                {
                    if (!ro.Active || ro.BoundsMin.X > ro.BoundsMax.X) continue;
                    if (RayBox(origin, dir, ro.BoundsMin, ro.BoundsMax, out float t))
                        hits.Add(new EditorPickHit { T = t, Volume = Vol(ro.BoundsMin, ro.BoundsMax), Cell = cell, Node = n, Object = ro });
                }
            }
        }
        if (!HideWorld)
        {
            foreach (var a in _areas.Values) if (a.Built && !EditorIsHidden(a)) TestNodes(Cell(a), a.Nodes);
            var tmp = new List<(int Batch, int Instance, float T, float Volume)>();
            foreach (var s in _sectors.Values)
            {
                if (!s.Built || EditorIsHidden(s)) continue;
                var c = Cell(s);
                TestNodes(c, s.Nodes);
                if (s.Placements == null) continue;
                tmp.Clear();
                s.Placements.EditorPick(origin, dir, tmp);
                foreach (var h in tmp) hits.Add(new EditorPickHit { T = h.T, Volume = h.Volume, Cell = c, Batch = h.Batch, Instance = h.Instance });
            }
        }
        foreach (var sc in Scenes)
        {
            if (!sc.Active) continue;
            foreach (var ro in sc.Objects)
            {
                if (!ro.Active || ro.BoundsMin.X > ro.BoundsMax.X) continue;
                if (RayBox(origin, dir, ro.BoundsMin, ro.BoundsMax, out float t))
                    hits.Add(new EditorPickHit { T = t, Volume = Vol(ro.BoundsMin, ro.BoundsMax), Scene = sc, Object = ro });
            }
        }
        hits.Sort((x, y) =>
        {
            bool xi = x.T <= 0f, yi = y.T <= 0f;
            if (xi != yi) return xi ? 1 : -1;
            return xi ? x.Volume.CompareTo(y.Volume) : x.T.CompareTo(y.T);
        });
        return hits;
    }

    static float Vol(Vector3 mn, Vector3 mx)
    {
        var e = mx - mn;
        return MathF.Max(e.X, 0.01f) * MathF.Max(e.Y, 0.01f) * MathF.Max(e.Z, 0.01f);
    }

    /// <summary>Strahl gegen achsparallele Box (t = 0, wenn der Ursprung in der Box liegt). Auch für den Editor (lokale Boxen).</summary>
    public static bool EditorRayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t) => RayBox(o, d, mn, mx, out t);
}