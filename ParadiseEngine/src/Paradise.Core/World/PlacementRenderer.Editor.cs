using System.Numerics;

namespace Paradise.Core.World;

/// <summary>Nur-lese-Sicht auf die Platzierungen eines Sektors für den Szenen-Editor (F1 &gt; Open Editor). Hauptthread.</summary>
public sealed partial class PlacementRenderer
{
    public int EditorBatchCount => _batches.Count;
    public LibraryObject EditorBatchObject(int i) => _batches[i].Obj;
    /// <summary>Instanz-Matrizen (Spielraum) eines Typs – nicht ändern (LOD-Eimer/Radien sind vorberechnet).</summary>
    public IReadOnlyList<Matrix4x4> EditorBatchMatrices(int i) => _batches[i].LocalMatrix;
}
