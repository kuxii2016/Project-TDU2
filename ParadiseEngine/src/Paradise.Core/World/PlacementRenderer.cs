using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>Ein gesammelter Zeichenaufruf (Sichtbarkeit/LOD auf Arbeits-Threads, DrawMesh danach im Hauptthread).</summary>
public readonly struct DrawItem
{
    public readonly IGpuMesh Mesh;
    public readonly IGpuMaterial? Material;
    public readonly Matrix4x4 Model;
    public DrawItem(IGpuMesh mesh, IGpuMaterial? material, Matrix4x4 model) { Mesh = mesh; Material = material; Model = model; }
}

/// <summary>
/// Zeichnet die .pmi-Platzierungen (Bäume, Büsche, Felsen, Leitplanken, Schilder) eines Sektors mit distanzbasierter
/// LOD-Auswahl. Portiert aus dem Unity-Projekt (VegetationRenderer). Matrizen in Spielkoordinaten.
/// Kollision (Kapseln/Mesh-Collider) folgt mit der Physik.
/// </summary>
public sealed class PlacementRenderer
{
    sealed class Batch
    {
        public LibraryObject Obj = null!;
        public Vector3[] LocalPos = Array.Empty<Vector3>();
        public Matrix4x4[] LocalMatrix = Array.Empty<Matrix4x4>();
        public float[] Radius = Array.Empty<float>();
        public List<int>[] PerLod = Array.Empty<List<int>>();
    }

    public float MaxDistance = 800f;
    /// <summary>Sichtweiten-Faktor für alle Vegetations-Renderer (TDU2RenderDistance).</summary>
    public static float DistanceMultiplier = 1f;
    public bool CullLastLod = false;
    public int RebucketEveryFrames = 8;

    private readonly List<Batch> _batches = new();
    private Vector3 _lastViewer = new(float.MaxValue, 0, 0);
    private float _appliedMul = 1f;
    private int _frame;

    public int InstanceCount { get; private set; }

    /// <summary>Instanzen, deren Library-Name mit einem Präfix beginnt, im Umkreis (Unity: VegetationRenderer.FindInstances).</summary>
    public void FindInstances(string[] prefixes, Vector3 pos, float radius, List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)> result)
    {
        float r2 = radius * radius;
        foreach (var b in _batches)
        {
            if (b.Obj.Lods.Length == 0 || string.IsNullOrEmpty(b.Obj.Name)) continue;
            bool ok = false;
            foreach (var p in prefixes) if (b.Obj.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            if (!ok) continue;
            for (int i = 0; i < b.LocalMatrix.Length; i++)
            {
                if ((b.LocalPos[i] - pos).LengthSquared() > r2) continue;
                result.Add((b.LocalMatrix[i], b.Obj.BoundsMin, b.Obj.BoundsMax, b.Obj.Name));
            }
        }
    }

    public void Add(LibraryObject? obj, List<Matrix4x4> localMatrices)
    {
        if (obj == null || localMatrices.Count == 0) return;
        var b = new Batch
        {
            Obj = obj,
            LocalMatrix = localMatrices.ToArray(),
            LocalPos = new Vector3[localMatrices.Count],
            Radius = new float[localMatrices.Count],
            PerLod = new List<int>[obj.Lods.Length],
        };
        for (int i = 0; i < b.LocalMatrix.Length; i++)
        {
            var m = b.LocalMatrix[i];
            b.LocalPos[i] = m.Translation;
            float s = MathF.Sqrt(System.Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(),
                       System.Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
            float br = MathF.Max(obj.Radius, MathF.Max(obj.BoundsMin.Length(), obj.BoundsMax.Length()));
            b.Radius[i] = br * s;
        }
        for (int l = 0; l < b.PerLod.Length; l++) b.PerLod[l] = new List<int>();
        _batches.Add(b);
        InstanceCount += b.LocalMatrix.Length;
        _lastViewer = new Vector3(float.MaxValue, 0, 0);
    }

    void Rebucket(Vector3 viewerLocal)
    {
        float mul = DistanceMultiplier;
        float maxSq = MaxDistance * mul * MaxDistance * mul;
        foreach (var b in _batches)
        {
            foreach (var l in b.PerLod) l.Clear();
            var lods = b.Obj.Lods;
            for (int i = 0; i < b.LocalPos.Length; i++)
            {
                float dSq = (b.LocalPos[i] - viewerLocal).LengthSquared();
                if (dSq > maxSq) continue;
                float d = MathF.Sqrt(dSq);
                int chosen = -1;
                for (int l = 0; l < lods.Length; l++)
                    if (d <= lods[l].MaxDistance * mul) { chosen = l; break; }
                if (chosen < 0 && !CullLastLod) chosen = lods.Length - 1;
                if (chosen >= 0) b.PerLod[chosen].Add(i);
            }
        }
    }

    /// <summary>Zeichnen (Unity: Update mit Graphics.DrawMeshInstanced). viewerGame in Spielkoordinaten.</summary>
    /// <summary>Wie Render, aber sammelt die Aufrufe (darf parallel zu anderen Sektoren laufen: nur eigener Zustand).</summary>
    public void Collect(List<DrawItem> sink, Matrix4x4 root, Vector3 viewerGame, Vector4[] frustum)
    {
        if (_batches.Count == 0) return;
        if (++_frame % RebucketEveryFrames == 0 || (viewerGame - _lastViewer).LengthSquared() > 25f || _appliedMul != DistanceMultiplier)
        {
            Rebucket(viewerGame);
            _lastViewer = viewerGame;
            _appliedMul = DistanceMultiplier;
        }
        var rootOff = root.Translation;
        foreach (var b in _batches)
            for (int l = 0; l < b.PerLod.Length; l++)
            {
                var idx = b.PerLod[l];
                if (idx.Count == 0) continue;
                var parts = b.Obj.Lods[l].Parts;
                foreach (int i in idx)
                {
                    if (!MapStreamer.SphereInFrustum(frustum, b.LocalPos[i] + rootOff, b.Radius[i])) continue;
                    var m = b.LocalMatrix[i] * root;
                    foreach (var p in parts) sink.Add(new DrawItem(p.Mesh, p.Material, m));
                }
            }
    }

    public void Render(IRenderer r, Matrix4x4 root, Vector3 viewerGame, Vector4[] frustum)
    {
        if (_batches.Count == 0) return;
        if (++_frame % RebucketEveryFrames == 0 || (viewerGame - _lastViewer).LengthSquared() > 25f || _appliedMul != DistanceMultiplier)
        {
            Rebucket(viewerGame);
            _lastViewer = viewerGame;
            _appliedMul = DistanceMultiplier;
        }
        var rootOff = root.Translation;
        foreach (var b in _batches)
            for (int l = 0; l < b.PerLod.Length; l++)
            {
                var idx = b.PerLod[l];
                if (idx.Count == 0) continue;
                var parts = b.Obj.Lods[l].Parts;
                foreach (int i in idx)
                {
                    if (!MapStreamer.SphereInFrustum(frustum, b.LocalPos[i] + rootOff, b.Radius[i])) continue;
                    var m = b.LocalMatrix[i] * root;
                    foreach (var p in parts) r.DrawMesh(p.Mesh, p.Material, m);
                }
            }
    }
}
