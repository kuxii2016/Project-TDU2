using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Core.Avatars;

/// <summary>
/// .xsb – Skeleton (Eden container “.XSB”, SKEL segment)
///   +32 u32 n, +36 i32 parent[n], u32 key4_sorted[n], u32 bone_of_sorted[n], pad 16,
///   local idle pose[n] (48 bytes each) (f32[4] quaternion, f32[4] position, f32[4] scale),
///   f32[16][n] inverse bind matrices (row vectors, translation in row 3), Key8 names[n] in sorted order.
/// Quaternions in the file rotate row vectors
/// rotates in the same way. Matrices here for row vectors (System.Numerics): InvBind = file unchanged.
/// All values in game space (right-handed, +Z forward, +X left).
/// </summary>
public sealed class XsbSkeleton
{
    public int Count;
    public int[] Parent = Array.Empty<int>();
    public uint[] Hash = Array.Empty<uint>();          // Key4 je Knochen (Spuren der .anm finden ihren Knochen darüber)
    public ulong[] Key = Array.Empty<ulong>();         // Key8 (gefalteter Name) je Knochen
    public Quaternion[] Rot = Array.Empty<Quaternion>();
    public Vector3[] Pos = Array.Empty<Vector3>(), Scale = Array.Empty<Vector3>();
    public Matrix4x4[] InvBind = Array.Empty<Matrix4x4>();   // Zeilenvektoren
    public readonly Dictionary<uint, int> ByHash = new();
    public readonly Dictionary<ulong, int> ByKey = new();

    public int Find(string name) => ByKey.TryGetValue(MaterialDef.KeyFromName(name), out int i) ? i : -1;

    public static XsbSkeleton? Parse(byte[]? d)
    {
        if (d == null || d.Length < 0x40) return null;
        int n = (int)Bin.U32(d, 0x20);
        if (n <= 0 || n > 512) return null;
        var s = new XsbSkeleton
        {
            Count = n,
            Parent = new int[n],
            Hash = new uint[n],
            Key = new ulong[n],
            Rot = new Quaternion[n],
            Pos = new Vector3[n],
            Scale = new Vector3[n],
            InvBind = new Matrix4x4[n],
        };
        int p = 0x24;
        for (int i = 0; i < n; i++) s.Parent[i] = Bin.I32(d, p + 4 * i);
        int a = p + 4 * n;
        int b = a + 4 * n;
        int c = (b + 4 * n + 15) & ~15;
        int m = c + 48 * n;
        int k = m + 64 * n;
        if (k + 8 * n > d.Length) return null;
        for (int j = 0; j < n; j++)
        {
            int bone = (int)Bin.U32(d, b + 4 * j);
            if (bone < 0 || bone >= n) continue;
            s.Hash[bone] = Bin.U32(d, a + 4 * j);
            s.Key[bone] = Bin.U64(d, k + 8 * j);
        }
        for (int i = 0; i < n; i++)
        {
            int q = c + 48 * i;
            s.Rot[i] = Norm(new Quaternion(-Bin.F32(d, q), -Bin.F32(d, q + 4), -Bin.F32(d, q + 8), Bin.F32(d, q + 12)));
            s.Pos[i] = new Vector3(Bin.F32(d, q + 16), Bin.F32(d, q + 20), Bin.F32(d, q + 24));
            var sc = new Vector3(Bin.F32(d, q + 32), Bin.F32(d, q + 36), Bin.F32(d, q + 40));
            s.Scale[i] = sc.LengthSquared() < 1e-8f ? Vector3.One : sc;
            int r = m + 64 * i;
            // Datei: Zeilen-Major für Zeilenvektoren = System.Numerics-Konvention (Mrc = f[r*4+c])
            s.InvBind[i] = new Matrix4x4(
                Bin.F32(d, r), Bin.F32(d, r + 4), Bin.F32(d, r + 8), Bin.F32(d, r + 12),
                Bin.F32(d, r + 16), Bin.F32(d, r + 20), Bin.F32(d, r + 24), Bin.F32(d, r + 28),
                Bin.F32(d, r + 32), Bin.F32(d, r + 36), Bin.F32(d, r + 40), Bin.F32(d, r + 44),
                Bin.F32(d, r + 48), Bin.F32(d, r + 52), Bin.F32(d, r + 56), Bin.F32(d, r + 60));
        }
        for (int i = 0; i < n; i++)
        {
            s.ByHash.TryAdd(s.Hash[i], i);
            s.ByKey.TryAdd(s.Key[i], i);
        }
        return s;
    }

    internal static Quaternion Norm(Quaternion q)
    {
        float l = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (l < 1e-6f) return Quaternion.Identity;
        return new Quaternion(q.X / l, q.Y / l, q.Z / l, q.W / l);
    }
}

/// <summary>A curve (rotation, position, or scale) of an .anm track.</summary>
public sealed class AnmCurve
{
    public float[] Times = Array.Empty<float>();
    public Vector4[] Values = Array.Empty<Vector4>();   // Rotation: Quaternion (bereits konjugiert), sonst xyz
    public bool IsRotation;

    public Quaternion SampleRot(float t)
    {
        int n = Values.Length;
        if (n == 1 || t <= Times[0]) return ToQ(Values[0]);
        for (int i = 1; i < n; i++)
        {
            if (t <= Times[i])
            {
                float f = (t - Times[i - 1]) / MathF.Max(1e-6f, Times[i] - Times[i - 1]);
                return Quaternion.Slerp(ToQ(Values[i - 1]), ToQ(Values[i]), f);
            }
        }
        return ToQ(Values[n - 1]);
    }

    public Vector3 SampleVec(float t)
    {
        int n = Values.Length;
        if (n == 1 || t <= Times[0]) return V3(Values[0]);
        for (int i = 1; i < n; i++)
        {
            if (t <= Times[i])
            {
                float f = (t - Times[i - 1]) / MathF.Max(1e-6f, Times[i] - Times[i - 1]);
                return Vector3.Lerp(V3(Values[i - 1]), V3(Values[i]), System.Math.Clamp(f, 0f, 1f));
            }
        }
        return V3(Values[n - 1]);
    }

    static Quaternion ToQ(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    static Vector3 V3(Vector4 v) => new(v.X, v.Y, v.Z);
}

public sealed class AnmTrack
{
    public uint Hash;
    public int Mask;          // Bit 0 Rotation, 1 Position, 2 Skalierung, 3 IK-Spur, 4 Wurzelbewegung
    public AnmCurve? Rot, Pos, Scl;
}

/// <summary>
/// .anm – Skeleton clip (“ANM!”, DATA): u32 tracks, f32 length, f32 length including fade-out, u32, f32 fade-out, u32 0,
/// Consecutive tracks: u16 mask, u16 0xffff, u32 size, u32 Key4 bones, one curve per set bit 0–2:
///   u16 n, u16 fmt, f32 length; times: fmt&amp;1 ? u16[n-1] (t = q/65535·length, first key at 0, padded to 4) : f32[n];
///   fmt&amp;4: f32 min, f32 span, values u16 (min + q/65535·span); fmt&amp;2: values i16 (rotation xyz/32767, w = +√(1−|xyz|²),
///   position/scaling q/16383); else f32[4] rotation / f32[3] position; fmt&amp;0x20: 3 extras per key
///   (float before or quantized (e0, e1, x, y, z, e2)); curve padded to 4 bytes.
/// </summary>
public sealed class AnmClip
{
    public string? Name;
    public float Length;
    public readonly List<AnmTrack> Tracks = new();

    public static AnmClip? Parse(byte[]? d, string? name = null)
    {
        if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0, "ANM!")) return null;
        int p = 8 + (int)Bin.U32(d, 4);
        if (p + 32 > d.Length || !Bin.TagIs(d, p, "DATA")) return null;
        var clip = new AnmClip { Name = name };
        int n = (int)Bin.U32(d, p + 8);
        clip.Length = Bin.F32(d, p + 12);
        int q = p + 8 + 24;
        for (int i = 0; i < n && q + 12 <= d.Length; i++)
        {
            int mask = Bin.U16(d, q);
            int size = (int)Bin.U32(d, q + 4);
            if (size < 12 || q + size > d.Length) break;
            var t = new AnmTrack { Mask = mask, Hash = Bin.U32(d, q + 8) };
            if ((mask & 8) == 0)
            {
                int r = q + 12;
                try
                {
                    if ((mask & 1) != 0) t.Rot = ReadCurve(d, ref r, true);
                    if ((mask & 2) != 0) t.Pos = ReadCurve(d, ref r, false);
                    if ((mask & 4) != 0) t.Scl = ReadCurve(d, ref r, false);
                    clip.Tracks.Add(t);
                }
                catch (Exception) { /* unbekanntes Format: Spur auslassen */ }
            }
            q += size;
        }
        return clip;
    }

    static AnmCurve ReadCurve(byte[] d, ref int p, bool rot)
    {
        int n = Bin.U16(d, p), fmt = Bin.U16(d, p + 2);
        float len = Bin.F32(d, p + 4);
        p += 8;
        if (n <= 0 || n > 100000) throw new FormatException("anm");
        var c = new AnmCurve { Times = new float[n], Values = new Vector4[n], IsRotation = rot };
        if ((fmt & 1) != 0)
        {
            c.Times[0] = 0f;
            for (int i = 1; i < n; i++) c.Times[i] = Bin.U16(d, p + 2 * (i - 1)) / 65535f * len;
            int bytes = 2 * (n - 1);
            p += bytes + (bytes % 4 != 0 ? 2 : 0);
        }
        else
        {
            for (int i = 0; i < n; i++) c.Times[i] = Bin.F32(d, p + 4 * i);
            p += 4 * n;
        }
        float mn = 0f, ext = 0f;
        if ((fmt & 4) != 0) { mn = Bin.F32(d, p); ext = Bin.F32(d, p + 4); p += 8; }
        int start = p;
        bool extras = (fmt & 0x20) != 0;
        for (int k = 0; k < n; k++)
        {
            if ((fmt & 4) != 0)
            {
                int o = extras ? p + 4 : p;
                float x = mn + Bin.U16(d, o) / 65535f * ext, y = mn + Bin.U16(d, o + 2) / 65535f * ext, z = mn + Bin.U16(d, o + 4) / 65535f * ext;
                p += extras ? 12 : 6;
                c.Values[k] = rot ? RotFromXyz(x, y, z) : new Vector4(x, y, z, 0f);
            }
            else if ((fmt & 2) != 0)
            {
                int o = extras ? p + 4 : p;
                short qx = Bin.I16(d, o), qy = Bin.I16(d, o + 2), qz = Bin.I16(d, o + 4);
                p += extras ? 12 : 6;
                if (rot) c.Values[k] = RotFromXyz(qx / 32767f, qy / 32767f, qz / 32767f);
                else c.Values[k] = new Vector4(qx / 16383f, qy / 16383f, qz / 16383f, 0f);
            }
            else
            {
                if (extras) p += 12;
                if (rot)
                {
                    var qq = XsbSkeleton.Norm(new Quaternion(-Bin.F32(d, p), -Bin.F32(d, p + 4), -Bin.F32(d, p + 8), Bin.F32(d, p + 12)));
                    c.Values[k] = new Vector4(qq.X, qq.Y, qq.Z, qq.W);
                    p += 16;
                }
                else { c.Values[k] = new Vector4(Bin.F32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8), 0f); p += 12; }
            }
        }
        int used = p - start;
        if (used % 4 != 0) p += 4 - used % 4;
        return c;
    }

    static Vector4 RotFromXyz(float x, float y, float z)
    {
        float w = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y - z * z));
        return new Vector4(-x, -y, -z, w);   // konjugiert (Zeilen- -> Spaltenvektor-Konvention wie Unity)
    }
}

/// <summary>Bone weights of a vertex (Unity BoneWeight).</summary>
public struct BoneWeight4
{
    public int B0, B1, B2, B3;
    public float W0, W1, W2, W3;
}

/// <summary>A skinned primitive of an avatar mesh (body, head, clothing), with bones already defined as skeleton indices.</summary>
public sealed class SkinPrim
{
    public Vector3[] Pos = Array.Empty<Vector3>();
    public Vector3[] Nrm = Array.Empty<Vector3>();
    public Vector4[]? Tan;
    public Vector2[][] Uv = Array.Empty<Vector2[]>();
    public BoneWeight4[] Weights = Array.Empty<BoneWeight4>();
    public int[] Tris = Array.Empty<int>();
    public int MaterialIndex;

    Vector3[]? _shadeNrm;
    Vector4[]? _shadeTan;

    static (int, int, int) QP(Vector3 p) => ((int)MathF.Round(p.X * 2000f), (int)MathF.Round(p.Y * 2000f), (int)MathF.Round(p.Z * 2000f));

    /// <summary>
    /// Gleiche Stellen: Vertices im Abstand ≤ 0,3 mm bekommen dieselbe Id (Suche über die Nachbarzellen eines 0,5-mm-Rasters) –
    /// reines Runden trennt fast gleiche Punkte an Zellgrenzen, dann reißen Nähte (dunkle Linien an Fingern).
    /// </summary>
    int[] CanonIds()
    {
        int n = Pos.Length;
        var ids = new int[n];
        var cells = new Dictionary<(int, int, int), List<int>>();
        var reps = new List<Vector3>();
        const float eps2 = 0.0003f * 0.0003f;
        for (int i = 0; i < n; i++)
        {
            var p = Pos[i]; var q = QP(p);
            int found = -1;
            for (int dx = -1; dx <= 1 && found < 0; dx++)
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dz = -1; dz <= 1 && found < 0; dz++)
                        if (cells.TryGetValue((q.Item1 + dx, q.Item2 + dy, q.Item3 + dz), out var l))
                            foreach (int r in l) if (Vector3.DistanceSquared(reps[r], p) <= eps2) { found = r; break; }
            if (found < 0)
            {
                found = reps.Count; reps.Add(p);
                if (!cells.TryGetValue(q, out var l)) cells[q] = l = new List<int>(2);
                l.Add(found);
            }
            ids[i] = found;
        }
        return ids;
    }

    /// <summary>
    /// Eine Stufe gekrümmte Unterteilung (PN-Dreiecke, Vlachos et al. 2001): jedes Dreieck wird in 4 geteilt, die neuen Kantenpunkte
    /// liegen auf der Kubik aus den Eckpunkten und ihren Normalen – Umriss und Schattierung werden rund statt eckig. Die Kantenpunkte
    /// hängen nur von den beiden Endpunkten und ihren (über die Position gemittelten) Normalen ab, daher keine Risse über UV-Nähte;
    /// offene Ränder (Hals, Ärmel, Saum) bleiben gerade, damit Kopf/Körper/Kleidung weiter aneinanderpassen. Normalen quadratisch
    /// (PN), UVs/Tangenten linear, Knochengewichte gemischt (die 4 stärksten).
    /// </summary>
    public SkinPrim Subdivided()
    {
        int n = Pos.Length;
        if (n == 0 || Tris.Length < 3 || Nrm.Length != n) return this;
        // Geometrie-Normalen: Datei-Normalen je Stelle gemittelt (alle Vertices an derselben Stelle gleich -> rissfrei)
        var cid = CanonIds();
        var gsum = new Dictionary<int, Vector3>();
        for (int i = 0; i < n; i++) { gsum.TryGetValue(cid[i], out var v); gsum[cid[i]] = v + Nrm[i]; }
        var gn = new Vector3[n];
        for (int i = 0; i < n; i++) { var v = gsum[cid[i]]; float l = v.Length(); gn[i] = l > 1e-6f ? v / l : Nrm[i]; }
        // offene Kanten (nach Stelle): nur ein Dreieck
        var edgeUse = new Dictionary<long, int>();
        long EK(int a, int b)
        {
            int qa = cid[a], qb = cid[b];
            return qa <= qb ? ((long)qa << 32) | (uint)qb : ((long)qb << 32) | (uint)qa;
        }
        for (int k = 0; k + 2 < Tris.Length; k += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = Tris[k + e], b = Tris[k + (e + 1) % 3];
                if ((uint)a >= (uint)n || (uint)b >= (uint)n) continue;
                var key = EK(a, b); edgeUse.TryGetValue(key, out int c); edgeUse[key] = c + 1;
            }
        var pos = new List<Vector3>(Pos); var nrm = new List<Vector3>(Nrm);
        var tan = Tan != null && Tan.Length == n ? new List<Vector4>(Tan) : null;
        var uvs = new List<Vector2>[Uv.Length];
        for (int u = 0; u < Uv.Length; u++) uvs[u] = new List<Vector2>(Uv[u].Length == n ? Uv[u] : new Vector2[n]);
        var wts = new List<BoneWeight4>(Weights.Length == n ? Weights : new BoneWeight4[n]);
        var mid = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (mid.TryGetValue(key, out int m)) return m;
            // Endpunkte in fester Reihenfolge (nach Stellen-Id), damit beide Seiten einer Naht denselben Punkt rechnen
            int ga = a, gb = b;
            if (cid[ga] > cid[gb]) { ga = b; gb = a; }
            Vector3 pa = Pos[ga], pb = Pos[gb];
            Vector3 p;
            if (edgeUse.TryGetValue(EK(a, b), out int use) && use >= 2)
            {
                Vector3 na = gn[ga], nb = gn[gb];
                var b210 = (2f * pa + pb - Vector3.Dot(pb - pa, na) * na) / 3f;
                var b120 = (2f * pb + pa - Vector3.Dot(pa - pb, nb) * nb) / 3f;
                p = (pa + pb) * 0.125f + (b210 + b120) * 0.375f;
            }
            else p = (pa + pb) * 0.5f;
            // Schattierungs-Normale quadratisch (PN): n110 = normalize(Na + Nb - v (Pb - Pa))
            Vector3 sa = Nrm[a], sb = Nrm[b], d = pb - pa;
            float dd = Vector3.Dot(d, d);
            var n110 = sa + sb - (dd > 1e-12f ? 2f * Vector3.Dot(d, sa + sb) / dd : 0f) * d;
            float l110 = n110.Length();
            n110 = l110 > 1e-6f ? n110 / l110 : Vector3.Normalize(sa + sb + new Vector3(0f, 1e-6f, 0f));
            var sn = 0.25f * sa + 0.25f * sb + 0.5f * n110;
            float ls = sn.Length();
            sn = ls > 1e-6f ? sn / ls : sa;
            int idx = pos.Count;
            pos.Add(p); nrm.Add(sn);
            if (tan != null)
            {
                var ta = Tan![a]; var tb = Tan[b];
                var t3 = new Vector3(ta.X + tb.X, ta.Y + tb.Y, ta.Z + tb.Z);
                float lt = t3.Length();
                tan.Add(new Vector4(lt > 1e-6f ? t3 / lt : new Vector3(ta.X, ta.Y, ta.Z), ta.W));
            }
            for (int u = 0; u < uvs.Length; u++) uvs[u].Add((uvs[u][a] + uvs[u][b]) * 0.5f);
            wts.Add(MixWeights(wts[a], wts[b]));
            mid[key] = idx;
            return idx;
        }
        var tris = new List<int>(Tris.Length * 4);
        for (int k = 0; k + 2 < Tris.Length; k += 3)
        {
            int a = Tris[k], b = Tris[k + 1], c = Tris[k + 2];
            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n || a == b || b == c || a == c) continue;
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            tris.Add(a); tris.Add(ab); tris.Add(ca);
            tris.Add(ab); tris.Add(b); tris.Add(bc);
            tris.Add(ca); tris.Add(bc); tris.Add(c);
            tris.Add(ab); tris.Add(bc); tris.Add(ca);
        }
        var res = new SkinPrim { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Tan = tan?.ToArray(), Weights = wts.ToArray(), Tris = tris.ToArray(), MaterialIndex = MaterialIndex };
        res.Uv = new Vector2[uvs.Length][];
        for (int u = 0; u < uvs.Length; u++) res.Uv[u] = uvs[u].ToArray();
        return res;
    }

    static BoneWeight4 MixWeights(BoneWeight4 a, BoneWeight4 b)
    {
        var acc = new Dictionary<int, float>(8);
        void Add(int bone, float w) { if (w <= 0f || bone < 0) return; acc.TryGetValue(bone, out float v); acc[bone] = v + 0.5f * w; }
        Add(a.B0, a.W0); Add(a.B1, a.W1); Add(a.B2, a.W2); Add(a.B3, a.W3);
        Add(b.B0, b.W0); Add(b.B1, b.W1); Add(b.B2, b.W2); Add(b.B3, b.W3);
        var l = new List<KeyValuePair<int, float>>(acc);
        l.Sort((x, y) => y.Value.CompareTo(x.Value));
        var r = new BoneWeight4 { B0 = a.B0, B1 = a.B1, B2 = a.B2, B3 = a.B3 };
        float s = 0f;
        for (int i = 0; i < l.Count && i < 4; i++) s += l[i].Value;
        if (l.Count == 0 || s <= 1e-6f) return a;
        if (l.Count > 0) { r.B0 = l[0].Key; r.W0 = l[0].Value / s; } else r.W0 = 0f;
        if (l.Count > 1) { r.B1 = l[1].Key; r.W1 = l[1].Value / s; } else r.W1 = 0f;
        if (l.Count > 2) { r.B2 = l[2].Key; r.W2 = l[2].Value / s; } else r.W2 = 0f;
        if (l.Count > 3) { r.B3 = l[3].Key; r.W3 = l[3].Value / s; } else r.W3 = 0f;
        return r;
    }

    /// <summary>Normalen-Glättung: Vertices gleicher Position, deren Normalen höchstens so weit auseinander liegen (cos), werden gemittelt.</summary>
    public static float SmoothCos = 0.5f;

    /// <summary>
    /// Normalen fürs Zeichnen: die Teile liegen teils mit getrennten Vertices je Dreieck vor (gleiche Position, eigene Normale) –
    /// dann ist jede Fläche einzeln schattiert (eckig). Hier werden die Normalen gleicher Positionen gemittelt, solange sie nicht
    /// weiter als <see cref="SmoothCos"/> auseinander liegen (echte Kanten bleiben).
    /// </summary>
    public Vector3[] ShadingNormals()
    {
        if (_shadeNrm != null) return _shadeNrm;
        int n = Pos.Length;
        // Mit Datei-Tangenten: Datei-Normalen, aber an Nähten (gleiche Position, getrennte Vertices – UV-/Materialnaht, harte Kante
        // der Datei) gemittelt, solange sie höchstens SmoothCos auseinander liegen: sonst springt das Licht dort (helle Kante an der
        // Schulter). Echte Kanten (> 60°) bleiben; die Tangenten der Datei richtet der Shader an der neuen Normale aus.
        if (Tan != null && Tan.Length == n && Nrm.Length == n)
        {
            var cid = CanonIds();
            var grp = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!grp.TryGetValue(cid[i], out var l)) grp[cid[i]] = l = new List<int>(4);
                l.Add(i);
            }
            var outN = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var g = grp[cid[i]];
                if (g.Count == 1) { outN[i] = Nrm[i]; continue; }
                Vector3 acc = Vector3.Zero;
                foreach (int j in g) if (Vector3.Dot(Nrm[i], Nrm[j]) >= SmoothCos) acc += Nrm[j];
                float l = acc.Length();
                outN[i] = l > 1e-6f && !float.IsNaN(l) ? acc / l : Nrm[i];
            }
            _shadeNrm = outN;
            return outN;
        }
        var src = new Vector3[n];
        for (int i = 0; i < n; i++) src[i] = Nrm.Length == n ? Nrm[i] : Vector3.UnitY;
        // Flächennormalen (nach Fläche gewichtet) je Vertex, damit auch Teile ohne glatte Datei-Normalen weich werden
        var face = new Vector3[n];
        for (int k = 0; k + 2 < Tris.Length; k += 3)
        {
            int i0 = Tris[k], i1 = Tris[k + 1], i2 = Tris[k + 2];
            if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;
            var fn = Vector3.Cross(Pos[i1] - Pos[i0], Pos[i2] - Pos[i0]);
            // Wicklung an der Datei-Normale ausrichten
            if (Vector3.Dot(fn, src[i0] + src[i1] + src[i2]) < 0f) fn = -fn;
            face[i0] += fn; face[i1] += fn; face[i2] += fn;
        }
        var groups = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var q = (Q(Pos[i].X), Q(Pos[i].Y), Q(Pos[i].Z));
            if (!groups.TryGetValue(q, out var l)) groups[q] = l = new List<int>(4);
            l.Add(i);
        }
        var res = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var g = groups[(Q(Pos[i].X), Q(Pos[i].Y), Q(Pos[i].Z))];
            var me = src[i];
            Vector3 acc = Vector3.Zero;
            foreach (int j in g)
                if (Vector3.Dot(me, src[j]) >= SmoothCos) acc += face[j].LengthSquared() > 0f ? face[j] : src[j];
            float l = acc.Length();
            res[i] = l > 1e-12f && !float.IsNaN(l) ? acc / l : me;
        }
        _shadeNrm = res;
        return res;
    }

    static int Q(float v) => (int)MathF.Round(v * 2000f);   // 0,5 mm

    /// <summary>
    /// Tangenten für die Normalmaps der Avatar-Shader (Ruhepose, UV0; w = Händigkeit, 0 = keine), über alle Dreiecke an derselben
    /// Stelle gemittelt (gleiche Position, ähnliche Normale, gleiche UV). Gleiche Richtung wie die Bildschirm-Ableitungen des Shaders
    /// (T = dP/du, B = dP/dv), aber stetig – die Ableitungen sind je Dreieck konstant und ließen die Schattierung springen.
    /// </summary>
    public Vector4[] ShadingTangents()
    {
        if (_shadeTan != null) return _shadeTan;
        int n = Pos.Length;
        if (Tan != null && Tan.Length == n) { _shadeTan = Tan; return Tan; }
        var nrm = ShadingNormals();
        var t = new Vector3[n]; var b = new Vector3[n];
        var uv = Uv.Length > 0 ? Uv[0] : Array.Empty<Vector2>();
        if (uv.Length == n)
            for (int k = 0; k + 2 < Tris.Length; k += 3)
            {
                int i0 = Tris[k], i1 = Tris[k + 1], i2 = Tris[k + 2];
                if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;
                Vector3 e1 = Pos[i1] - Pos[i0], e2 = Pos[i2] - Pos[i0];
                Vector2 d1 = uv[i1] - uv[i0], d2 = uv[i2] - uv[i0];
                float r = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(r) < 1e-12f) continue;
                float f = 1f / r;
                var tt = (e1 * d2.Y - e2 * d1.Y) * f;
                var bb = (e2 * d1.X - e1 * d2.X) * f;
                t[i0] += tt; t[i1] += tt; t[i2] += tt;
                b[i0] += bb; b[i1] += bb; b[i2] += bb;
            }
        // gleiche Stelle (Position + UV) zusammenfassen, Normale ähnlich
        var groups = new Dictionary<(int, int, int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var u = uv.Length == n ? uv[i] : Vector2.Zero;
            var q = (Q(Pos[i].X), Q(Pos[i].Y), Q(Pos[i].Z), (int)MathF.Round(u.X * 4096f), (int)MathF.Round(u.Y * 4096f));
            if (!groups.TryGetValue(q, out var l)) groups[q] = l = new List<int>(4);
            l.Add(i);
        }
        var res = new Vector4[n];
        foreach (var g in groups.Values)
            foreach (int i in g)
            {
                var nn = nrm[i];
                Vector3 ts = Vector3.Zero, bs = Vector3.Zero;
                foreach (int j in g) if (Vector3.Dot(nn, nrm[j]) >= SmoothCos) { ts += t[j]; bs += b[j]; }
                var tt = ts - nn * Vector3.Dot(nn, ts);
                float l = tt.Length();
                if (l < 1e-8f || float.IsNaN(l)) { res[i] = Vector4.Zero; continue; }
                tt /= l;
                float w = Vector3.Dot(Vector3.Cross(nn, tt), bs) < 0f ? -1f : 1f;
                res[i] = new Vector4(tt, w);
            }
        _shadeTan = res;
        return res;
    }
}

/// <summary>
/// Skinned vertex buffers for the avatars (layout word 0x0240: float stream for position, bone indices u8[4], weights (N-1)·f32;
/// 0x2040: float stream for position, UVs, bones; main stream for normals/color/(UVs)/tangent/binormal). The indices of a
/// vertex point to the BONE palette of its PRIM (child chunk), whose entries are bones of the skeleton.
/// </summary>
public static class SkinnedGeometry
{
    public static List<SkinPrim> Read(Geometry3DG geo, int geomOffset)
    {
        var list = new List<SkinPrim>();
        var d = geo.Data;
        if (geomOffset <= 0 || geomOffset + 16 > d.Length || !Bin.TagIs(d, geomOffset, "GEOM")) return list;
        int gsize = (int)Bin.U32(d, geomOffset + 8), ghs = (int)Bin.U32(d, geomOffset + 12);
        foreach (var c in EdenChunks.Read(d, geomOffset + ghs, geomOffset + gsize))
        {
            if (c.Tag != "PRIM") continue;
            int p = c.DataOffset;
            int ib = (int)Bin.U32(d, p), vb = (int)Bin.U32(d, p + 4);
            int mat = Bin.U16(d, p + 16);
            int primType = d[p + 18];
            int nb = d[p + 19];
            int[]? palette = null;
            foreach (var ch in EdenChunks.Children(d, c))
            {
                if (ch.Tag != "BONE") continue;
                palette = new int[nb];
                for (int i = 0; i < nb && ch.DataOffset + 4 * i + 4 <= d.Length; i++) palette[i] = (int)Bin.U32(d, ch.DataOffset + 4 * i);
            }
            var prim = ReadVb(d, vb, palette);
            if (prim == null) continue;
            prim.MaterialIndex = mat;
            var strip = geo.ReadIndexBuffer(ib);
            var tris = new List<int>(strip.Length * 3);
            if (primType == 4 || primType == 5) Geometry3DG.StripToList(strip, 0, tris);
            else Geometry3DG.ListToList(strip, 0, tris);
            int vc = prim.Pos.Length;
            for (int t = tris.Count - 3; t >= 0; t -= 3)
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) tris.RemoveRange(t, 3);
            prim.Tris = tris.ToArray();
            if (prim.Tris.Length > 0) list.Add(prim);
        }
        return list;
    }

    static SkinPrim? ReadVb(byte[] d, int vb, int[]? palette)
    {
        if (vb <= 0 || vb + 48 > d.Length || !Bin.TagIs(d, vb, "DXVB")) return null;
        int chunkSize = (int)Bin.U32(d, vb + 8);
        int n = (int)Bin.U32(d, vb + 16);
        int dec = vb + 20;
        uint flags = Bin.U32(d, vb + 44);
        if ((flags & 0x2200) == 0 || n <= 0) return null;
        bool uvS0 = (flags & 0x2000) != 0;
        bool hasNormal = d[dec + 2] != 0, hasColor = d[dec + 4] != 0, hasColor2 = d[dec + 6] != 0;
        int nuv = d[dec + 8];
        bool hasTan = d[dec + 10] != 0, hasBin = d[dec + 12] != 0, hasIdx = d[dec + 14] != 0;
        int nw = d[dec + 16] != 0 ? System.Math.Clamp((d[dec + 17] & 0x0F) - 1, 0, 3) : 0;
        int s0 = 12 + (uvS0 ? 8 * nuv : 0) + (hasIdx ? 4 : 0) + 4 * nw;
        int oUv0 = 12;
        int oIdx = 12 + (uvS0 ? 8 * nuv : 0);
        int s1 = 0, oN = -1, oUv1 = -1, oT = -1, oB = -1;
        if (hasNormal) { oN = s1; s1 += 8; }
        if (hasColor) s1 += 4;
        if (hasColor2) s1 += 4;
        if (!uvS0) { oUv1 = s1; s1 += 8 * nuv; }
        if (hasTan) { oT = s1; s1 += 8; }
        if (hasBin) { oB = s1; s1 += 8; }
        int b0 = vb + 48, b1 = b0 + s0 * n;
        if (b1 + s1 * n > vb + chunkSize) return null;
        var pr = new SkinPrim { Pos = new Vector3[n], Nrm = new Vector3[n], Weights = new BoneWeight4[n], Uv = new Vector2[System.Math.Max(1, nuv)][] };
        for (int u = 0; u < pr.Uv.Length; u++) pr.Uv[u] = new Vector2[n];
        if (hasTan) pr.Tan = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            int q = b0 + i * s0;
            pr.Pos[i] = new Vector3(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8));
            for (int u = 0; u < nuv; u++)
            {
                int o = uvS0 ? q + oUv0 + 8 * u : b1 + i * s1 + oUv1 + 8 * u;
                pr.Uv[u][i] = new Vector2(Bin.F32(d, o), Bin.F32(d, o + 4));
            }
            int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
            if (hasIdx) { i0 = d[q + oIdx]; i1 = d[q + oIdx + 1]; i2 = d[q + oIdx + 2]; i3 = d[q + oIdx + 3]; }
            float w0 = nw > 0 ? Bin.F32(d, q + oIdx + 4) : 1f;
            float w1 = nw > 1 ? Bin.F32(d, q + oIdx + 8) : 0f;
            float w2 = nw > 2 ? Bin.F32(d, q + oIdx + 12) : 0f;
            float rest = MathF.Max(0f, 1f - w0 - w1 - w2);
            float w3 = 0f;
            if (nw == 1) w1 = rest; else if (nw == 2) w2 = rest; else if (nw >= 3) w3 = rest;
            pr.Weights[i] = Normalize(new BoneWeight4
            {
                B0 = Map(palette, i0),
                W0 = w0,
                B1 = Map(palette, i1),
                W1 = w1,
                B2 = Map(palette, i2),
                W2 = w2,
                B3 = Map(palette, i3),
                W3 = w3,
            });
            if (hasNormal)
            {
                int o = b1 + i * s1 + oN;
                var nv = new Vector3(Bin.HalfAt(d, o), Bin.HalfAt(d, o + 2), Bin.HalfAt(d, o + 4));
                float l = nv.Length();
                pr.Nrm[i] = l > 1e-6f && !float.IsNaN(l) ? nv / l : Vector3.UnitY;
            }
            else pr.Nrm[i] = Vector3.UnitY;
            if (hasTan)
            {
                int o = b1 + i * s1 + oT;
                var tv = new Vector3(Bin.HalfAt(d, o), Bin.HalfAt(d, o + 2), Bin.HalfAt(d, o + 4));
                float l = tv.Length();
                tv = l > 1e-6f && !float.IsNaN(l) ? tv / l : Vector3.UnitX;
                // Händigkeit aus der Datei-Binormale (die Normalmaps sind gegen genau diese Basis gebacken)
                float hand = 1f;
                if (hasBin)
                {
                    int ob = b1 + i * s1 + oB;
                    var bv = new Vector3(Bin.HalfAt(d, ob), Bin.HalfAt(d, ob + 2), Bin.HalfAt(d, ob + 4));
                    if (Vector3.Dot(Vector3.Cross(pr.Nrm[i], tv), bv) < 0f) hand = -1f;
                }
                pr.Tan![i] = new Vector4(tv, hand);
            }
        }
        return pr;
    }

    static int Map(int[]? palette, int i) => palette != null && i < palette.Length ? palette[i] : i;

    static BoneWeight4 Normalize(BoneWeight4 b)
    {
        float s = b.W0 + b.W1 + b.W2 + b.W3;
        if (s <= 1e-6f) { b.W0 = 1f; b.W1 = b.W2 = b.W3 = 0f; return b; }
        b.W0 /= s; b.W1 /= s; b.W2 /= s; b.W3 /= s;
        return b;
    }
}