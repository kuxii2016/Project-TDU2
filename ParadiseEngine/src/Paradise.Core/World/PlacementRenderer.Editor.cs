using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Szenen-Editor (F1 &gt; Open Editor, Projekt Paradise.Editor): Nur-lese-Sicht auf die Platzierungen eines Sektors plus
/// Ausblenden (ganzer Sektor / ein Typ / einzelne Instanzen) und Strahltest für die Klick-Auswahl. Hauptthread.
/// </summary>
public sealed partial class PlacementRenderer
{
    /// <summary>Alle Platzierungen dieses Sektors ausblenden.</summary>
    public bool EditorHidden;
    readonly HashSet<Batch> _editorHiddenBatches = new();
    readonly Dictionary<Batch, HashSet<int>> _editorHiddenInstances = new();

    public int EditorBatchCount => _batches.Count;
    public LibraryObject EditorBatchObject(int i) => _batches[i].Obj;
    /// <summary>Instanz-Matrizen (Spielraum) eines Typs – nicht ändern (LOD-Eimer/Radien sind vorberechnet).</summary>
    public IReadOnlyList<Matrix4x4> EditorBatchMatrices(int i) => _batches[i].LocalMatrix;

    public bool EditorBatchHidden(int i) => _editorHiddenBatches.Contains(_batches[i]);
    public void EditorSetBatchHidden(int i, bool hidden) { if (hidden) _editorHiddenBatches.Add(_batches[i]); else _editorHiddenBatches.Remove(_batches[i]); }

    public bool EditorInstanceHidden(int batch, int instance) =>
        _editorHiddenInstances.TryGetValue(_batches[batch], out var h) && h.Contains(instance);

    public void EditorSetInstanceHidden(int batch, int instance, bool hidden)
    {
        var b = _batches[batch];
        if (!_editorHiddenInstances.TryGetValue(b, out var h)) { if (!hidden) return; _editorHiddenInstances[b] = h = new HashSet<int>(); }
        if (hidden) h.Add(instance); else { h.Remove(instance); if (h.Count == 0) _editorHiddenInstances.Remove(b); }
    }

    /// <summary>Zeichnen: Typ ausgelassen? hidden = ausgeblendete Instanzen dieses Typs (oder null).</summary>
    bool EditorSkip(Batch b, out HashSet<int>? hidden)
    {
        hidden = null;
        if (_editorHiddenBatches.Count > 0 && _editorHiddenBatches.Contains(b)) return true;
        if (_editorHiddenInstances.Count > 0 && _editorHiddenInstances.TryGetValue(b, out var h)) hidden = h;
        return false;
    }

    /// <summary>Strahltest (Spielraum) gegen die LOD0-Box jeder sichtbaren Instanz. Treffer: (Typ, Instanz, t, Volumen).</summary>
    public void EditorPick(Vector3 origin, Vector3 dir, List<(int Batch, int Instance, float T, float Volume)> hits)
    {
        if (EditorHidden) return;
        for (int bi = 0; bi < _batches.Count; bi++)
        {
            var b = _batches[bi];
            if (EditorSkip(b, out var hidden)) continue;
            var mn = b.Obj.BoundsMin; var mx = b.Obj.BoundsMax;
            if (!(mn.X <= mx.X)) continue;
            for (int i = 0; i < b.LocalMatrix.Length; i++)
            {
                if (hidden != null && hidden.Contains(i)) continue;
                // grob: Kugel um die Instanz
                var oc = b.LocalPos[i] - origin;
                float along = Vector3.Dot(oc, dir);
                float r = b.Radius[i];
                if (along < -r || (oc - dir * along).LengthSquared() > r * r) continue;
                var m = b.LocalMatrix[i];
                if (!Matrix4x4.Invert(m, out var inv)) continue;
                var lo = Vector3.Transform(origin, inv);
                var ld = Vector3.TransformNormal(dir, inv);
                if (!MapStreamer.EditorRayBox(lo, ld, mn, mx, out float t)) continue;
                var e = (mx - mn) * new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());
                hits.Add((bi, i, t, MathF.Max(e.X, 0.01f) * MathF.Max(e.Y, 0.01f) * MathF.Max(e.Z, 0.01f)));
            }
        }
    }
}
