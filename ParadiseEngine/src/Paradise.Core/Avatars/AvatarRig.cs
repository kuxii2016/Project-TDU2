using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Avatars;
/// <summary>
/// Avatar poses and CPU skinning (BeginPose / ApplyClip / SolveTwoBone / BlendLocal / CommitPose. 
/// Everything in avatar space (game space, right-handed, +Z forward),
/// Matrices for row vectors (System.Numerics): global = local · global(parent), skin = InvBind · global.
/// </summary>
public sealed class AvatarRig
{
    public readonly XsbSkeleton Skeleton;
    readonly Quaternion[] _lr;
    readonly Vector3[] _lp, _ls;
    readonly Matrix4x4[] _g, _skin;
    bool _dirty = true;

    public AvatarRig(XsbSkeleton skel)
    {
        Skeleton = skel;
        int n = skel.Count;
        _lr = new Quaternion[n]; _lp = new Vector3[n]; _ls = new Vector3[n];
        _g = new Matrix4x4[n]; _skin = new Matrix4x4[n];
        BeginPose();
    }

    public int Bone(string name) => Skeleton.Find(name);

    /// <summary>Arbeits-Pose auf die Ruhepose des Skeletts setzen.</summary>
    public void BeginPose()
    {
        for (int i = 0; i < Skeleton.Count; i++) { _lr[i] = Skeleton.Rot[i]; _lp[i] = Skeleton.Pos[i]; _ls[i] = Skeleton.Scale[i]; }
        _dirty = true;
    }

    /// <summary>Clip zum Zeitpunkt t überlagern (weight 1 = ersetzen).</summary>
    public void ApplyClip(AnmClip? clip, float t, float weight = 1f, bool positions = true)
    {
        if (clip == null || weight <= 0f) return;
        foreach (var tr in clip.Tracks)
        {
            if (!Skeleton.ByHash.TryGetValue(tr.Hash, out int i)) continue;
            if (tr.Rot != null)
            {
                var q = tr.Rot.SampleRot(t);
                _lr[i] = weight >= 1f ? q : Quaternion.Slerp(_lr[i], q, weight);
            }
            if (positions && tr.Pos != null)
            {
                var p = tr.Pos.SampleVec(t);
                _lp[i] = weight >= 1f ? p : Vector3.Lerp(_lp[i], p, weight);
            }
        }
        _dirty = true;
    }

    /// <summary>Set the local position of a bone or blend it using the weight <paramref name="w"/> (FaceFX).</summary>
    public void BlendLocal(int i, Vector3 pos, Quaternion rot, Vector3 scale, float w)
    {
        if (i < 0 || i >= Skeleton.Count || w <= 0f) return;
        if (w >= 1f) { _lp[i] = pos; _lr[i] = rot; _ls[i] = scale; }
        else
        {
            _lp[i] = Vector3.Lerp(_lp[i], pos, w);
            _lr[i] = Quaternion.Slerp(_lr[i], rot, w);
            _ls[i] = Vector3.Lerp(_ls[i], scale, w);
        }
        _dirty = true;
    }

    public Quaternion LocalRotation(int i) => _lr[i];
    public Vector3 LocalPosition(int i) => _lp[i];
    public Vector3 LocalScale(int i) => _ls[i];

    void ComputeGlobals()
    {
        if (!_dirty) return;
        int n = Skeleton.Count;
        for (int i = 0; i < n; i++)
        {
            var m = Matrix4x4.CreateScale(_ls[i]) * Matrix4x4.CreateFromQuaternion(_lr[i]) * Matrix4x4.CreateTranslation(_lp[i]);
            int p = Skeleton.Parent[i];
            _g[i] = p >= 0 && p < i ? m * _g[p] : p >= 0 && p < n ? m * Global(p) : m;
        }
        _dirty = false;
    }

    // Eltern stehen in der .xsb normalerweise vor den Kindern; sonst rekursiv
    Matrix4x4 Global(int i)
    {
        var m = Matrix4x4.CreateScale(_ls[i]) * Matrix4x4.CreateFromQuaternion(_lr[i]) * Matrix4x4.CreateTranslation(_lp[i]);
        int p = Skeleton.Parent[i];
        return p >= 0 && p < Skeleton.Count && p != i ? m * Global(p) : m;
    }

    Vector3 GPos(int i) => _g[i].Translation;

    Quaternion GRot(int i)
    {
        Matrix4x4.Decompose(_g[i], out _, out var r, out _);
        return Quaternion.Normalize(r);
    }

    /// <summary>Weltrotation (Avatar-Raum) eines Knochens setzen; Kinder folgen.</summary>
    void SetGlobalRotation(int i, Quaternion g)
    {
        int p = Skeleton.Parent[i];
        var pr = p >= 0 ? GRot(p) : Quaternion.Identity;
        _lr[i] = Quaternion.Normalize(Quaternion.Inverse(pr) * g);
        _dirty = true;
        ComputeGlobals();
    }

    /// <summary>
    /// Two-bone IK in avatar space: chain <paramref name="upper"/> (shoulder/hip) – <paramref name="mid"/> (elbow/knee) –
    /// <paramref name="effector"/> (IK bone on hand/foot), target in avatar coordinates. The bending plane remains that of the animation.
    /// </summary>
    public void SolveTwoBone(int upper, int mid, int effector, Vector3 target, float weight = 1f)
    {
        if (upper < 0 || mid < 0 || effector < 0 || weight <= 0f) return;
        ComputeGlobals();
        Vector3 a = GPos(upper), b = GPos(mid), c = GPos(effector);
        float la = (b - a).Length(), lb = (c - b).Length();
        if (la < 1e-4f || lb < 1e-4f) return;
        target = Vector3.Lerp(c, target, System.Math.Clamp(weight, 0f, 1f));
        Vector3 at = target - a;
        float d = System.Math.Clamp(at.Length(), MathF.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
        // 1) Gelenk beugen: Winkel bei mid so, dass |c - a| = d
        Vector3 ba = a - b, bc = c - b;
        float cur = Angle(ba, bc);
        float want = MathF.Acos(System.Math.Clamp((la * la + lb * lb - d * d) / (2f * la * lb), -1f, 1f));
        Vector3 axis = Vector3.Cross(ba, bc);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(ba, Vector3.UnitY);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.UnitX;
        axis = Vector3.Normalize(axis);
        SetGlobalRotation(mid, Quaternion.CreateFromAxisAngle(axis, want - cur) * GRot(mid));
        // 2) Kette ausrichten
        c = GPos(effector);
        SetGlobalRotation(upper, FromTo(c - a, target - a) * GRot(upper));
    }

    /// <summary>Position eines Knochens im Avatar-Raum (aktuelle Arbeits-Pose).</summary>
    public Vector3 BonePosition(int i) { ComputeGlobals(); return GPos(i); }

    /// <summary>Lage eines Knochens im Avatar-Raum (aktuelle Arbeits-Pose).</summary>
    public Matrix4x4 BoneMatrix(int i) { ComputeGlobals(); return _g[i]; }

    static float Angle(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-8f || lb < 1e-8f) return 0f;
        return MathF.Acos(System.Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f));
    }

    /// <summary>Unity Quaternion.FromToRotation.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float lf = from.Length(), lt = to.Length();
        if (lf < 1e-8f || lt < 1e-8f) return Quaternion.Identity;
        from /= lf; to /= lt;
        float d = Vector3.Dot(from, to);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var ax = Vector3.Cross(Vector3.UnitX, from);
            if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    /// <summary>Skin matrices for the current pose (once per frame before <see cref="Skin"/>).</summary>
    public void UpdateSkin()
    {
        ComputeGlobals();
        for (int i = 0; i < Skeleton.Count; i++) _skin[i] = Skeleton.InvBind[i] * _g[i];
    }

    /// <summary>
    /// Write the vertices of a part in its current pose (avatar space) to <paramref name="dst"/> (starting at <paramref name="offset"/>).
    /// </summary>
    public void Skin(SkinPrim pr, ObjectVertex[] dst, int offset)
    {
        var w = pr.Weights; var pos = pr.Pos; var nrm = pr.ShadingNormals();
        var tan = pr.ShadingTangents();
        var uv0 = pr.Uv[0];
        var uv1 = pr.Uv.Length > 1 ? pr.Uv[1] : uv0;
        var uv2 = pr.Uv.Length > 2 ? pr.Uv[2] : uv0;
        int n = Skeleton.Count;
        for (int v = 0; v < pos.Length; v++)
        {
            var bw = w[v];
            Vector3 p = Vector3.Zero, nn = Vector3.Zero, tt = Vector3.Zero;
            var t4 = tan[v];
            var t3 = new Vector3(t4.X, t4.Y, t4.Z);
            // Gewichtete Knochenmatrizen zuerst mischen, dann je Vertex nur 3 Transformationen statt 12 (je Einfluss Position,
            // Normale, Tangente). Mathematisch gleich: Σ w·(v·M) = v·(Σ w·M), auch für die Verschiebung (w = 1 bei Positionen).
            Matrix4x4 m = default;
            bool any = false;
            Blend(bw.B0, bw.W0, n, ref m, ref any);
            Blend(bw.B1, bw.W1, n, ref m, ref any);
            Blend(bw.B2, bw.W2, n, ref m, ref any);
            Blend(bw.B3, bw.W3, n, ref m, ref any);
            if (any)
            {
                p = Vector3.Transform(pos[v], m);
                nn = Vector3.TransformNormal(nrm[v], m);
                tt = Vector3.TransformNormal(t3, m);
            }
            float l = nn.Length();
            ref var o = ref dst[offset + v];
            o.Position = p;
            o.Normal = l > 1e-6f ? nn / l : Vector3.UnitY;
            o.Uv0 = uv0[v]; o.Uv1 = uv1[v]; o.Uv2 = uv2[v];
            // Tangente für den Avatar-Shader: Richtung oktaeder-kodiert in Uv3, Farbe B = 0 als Kennung, G = Händigkeit
            float lt = tt.Length();
            if (t4.W != 0f && lt > 1e-6f)
            {
                o.Uv3 = OctEncode(tt / lt);
                o.Color = ObjectVertex.PackColor(255, (byte)(t4.W < 0f ? 0 : 255), 0, 255);
            }
            else { o.Uv3 = Vector2.Zero; o.Color = 0xFFFFFFFFu; }
        }
    }

    void Blend(int b, float wt, int n, ref Matrix4x4 m, ref bool any)
    {
        if (wt <= 0f || b < 0 || b >= n) return;
        m += _skin[b] * wt;
        any = true;
    }

    void Acc(int b, float wt, Vector3 pos, Vector3 nrm, Vector3 tan, int n, ref Vector3 p, ref Vector3 nn, ref Vector3 tt)
    {
        if (wt <= 0f || b < 0 || b >= n) return;
        ref var m = ref _skin[b];
        p += Vector3.Transform(pos, m) * wt;
        nn += Vector3.TransformNormal(nrm, m) * wt;
        tt += Vector3.TransformNormal(tan, m) * wt;
    }

    /// <summary>Einheitsvektor -> 2 Werte (Oktaeder), Gegenstück OctDecode im Avatar-Vertexshader.</summary>
    static Vector2 OctEncode(Vector3 v)
    {
        float s = MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z);
        var e = new Vector2(v.X, v.Y) / s;
        if (v.Z < 0f) e = new Vector2((1f - MathF.Abs(e.Y)) * (e.X >= 0f ? 1f : -1f), (1f - MathF.Abs(e.X)) * (e.Y >= 0f ? 1f : -1f));
        return e;
    }
}