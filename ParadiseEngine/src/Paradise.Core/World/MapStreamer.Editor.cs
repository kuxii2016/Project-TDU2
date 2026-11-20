using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Nur-lese-Sicht auf die gestreamten Zellen für den Szenen-Editor (F1 &gt; Open Editor, Projekt Paradise.Editor).
/// Wird ausschließlich im Hauptthread aufgerufen (wie Render/Update) – keine Änderung am Streaming.
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
        foreach (var a in _areas.Values)
            l.Add(new EditorCell
            {
                Kind = "Area", Key = a.Key.ToString(), Built = a.Built, Failed = a.Failed, Nodes = a.Nodes,
                Bank = a.Model?.Path, ExtraModels = a.ExtraModels.Count, Colliders = a.Colliders.Count, State = a,
            });
        l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return l;
    }

    public List<EditorCell> EditorSectors()
    {
        var l = new List<EditorCell>(_sectors.Count);
        foreach (var s in _sectors.Values)
            l.Add(new EditorCell
            {
                Kind = "Sector", Key = s.Key.ToString(), Built = s.Built, Failed = s.Failed, Nodes = s.Nodes,
                Placements = s.Placements, Bank = s.Model?.Path, ExtraModels = s.ExtraModels.Count, Colliders = s.Colliders.Count,
                HasGrass = s.Grass != null, HasWires = s.Wires != null, State = s,
            });
        l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return l;
    }

    /// <summary>true, solange die Zelle noch geladen ist (sonst wurde sie entladen und ihre Objekte sind freigegeben).</summary>
    public bool EditorIsLoaded(EditorCell c) => c.State switch
    {
        AreaState a => _areas.TryGetValue(a.Key, out var cur) && ReferenceEquals(cur, a),
        SectorState s => _sectors.TryGetValue(s.Key, out var cur) && ReferenceEquals(cur, s),
        _ => false,
    };

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
}
