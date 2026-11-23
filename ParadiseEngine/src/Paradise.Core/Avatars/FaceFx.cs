using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using System.Numerics;
using System.Text;

namespace Paradise.Core.Avatars;

/// <summary>
/// FaceFX (OC3, SDK 1.7.2) der Avatare – 1:1-Port von Unity Runtime/Avatars/TDU2FaceFx.cs, erweitert um die Register der Eden-Schicht
/// (td2.exe FUN_00b76550: Gesichtsparameter der AvatarConfiguration als Register der 49 C_*-Knoten, Operation "ersetzen").
///   avatar/avatar_face_fx.bnk: female.fxa / male.fxa = FxActor (42 Gesichtsknochen mit Ruhepose und Posen-Deltas je Knoten,
///   Gesichtsgraph mit 445 Knoten), Kino-Bänke: *.fxe = FxAnimSet (Kurven je Knoten, Hermite).
/// Graph: Knotenwert = Kurve (+ Summe bzw. x Produkt der Eingänge über ihre Link-Funktionen), Register ersetzt den Wert,
/// Korrektur-Links skalieren (1 - Faktor x Eingang), dann auf [min, max] geklemmt. Knochen = Ruhepose + Summe(Wert x Delta)
/// (Position, Skalierung), Rotation = Ruhe x Produkt slerp(1, Delta, Wert).
/// Koordinaten: FaceFX in cm, X gespiegelt zur .xsb, Quaternionen (w x y z): .xsb-Position = (-x, y, z) / 100, .xsb-Rotation roh
/// (-x, y, z, w) -> Avatar-Raum der Engine (konjugiert wie XsbSkeleton.Parse) = (x, -y, -z, w) (geprüft an den 42 Ruheposen gegen
/// woman_body.xsb).
/// </summary>
public sealed class FaceFxActor
{
    public sealed class Node
    {
        public int Type, Op;
        public string Name = "";
        public float Min, Max;
        public int[] Src = Array.Empty<int>();
        public int[] Fn = Array.Empty<int>();
        public float[][] Prm = Array.Empty<float[]>();
    }

    public sealed class Delta { public int Node; public Vector3 Pos, Scl; public Quaternion Rot; }   // Rot: FaceFX-Raum (x y z w)

    public sealed class Bone
    {
        public string Name = "";
        public Vector3 Pos, Scl;
        public Quaternion Rot;
        /// <summary>Inverse Ruhedrehung aus der Datei (FxMasterBoneList, w zuerst), exe "invRot" in FUN_00b77b20.</summary>
        public Quaternion InvRot = Quaternion.Identity;
        public Delta[] Deltas = Array.Empty<Delta>();
    }

    public string Name = "";
    public Node[] Nodes = Array.Empty<Node>();
    public Bone[] Bones = Array.Empty<Bone>();
    public readonly Dictionary<string, int> NodeByName = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Lesen
    internal sealed class Reader
    {
        readonly byte[] _d; public int P;
        public Reader(byte[] d) { _d = d; }
        public bool Ok(int n) => P + n <= _d.Length;
        public uint U32() { uint v = BitConverter.ToUInt32(_d, P); P += 4; return v; }
        public int I32() { int v = BitConverter.ToInt32(_d, P); P += 4; return v; }
        public ushort U16() { ushort v = BitConverter.ToUInt16(_d, P); P += 2; return v; }
        public float F32() { float v = BitConverter.ToSingle(_d, P); P += 4; return v; }
        public string Str()
        {
            int n = (int)U32();
            if (n < 0 || P + n > _d.Length) throw new InvalidDataException("FaceFX: Zeichenkette");
            int len = n; while (len > 0 && _d[P + len - 1] == 0) len--;
            string s = Encoding.ASCII.GetString(_d, P, len); P += n; return s;
        }

        /// <summary>"FACE", Version, Kennungen, Klassenliste, Namenstabelle.</summary>
        public string[] Header()
        {
            if (_d.Length < 16 || _d[0] != 'F' || _d[1] != 'A' || _d[2] != 'C' || _d[3] != 'E') throw new InvalidDataException("FaceFX: kein FACE");
            P = 4; U32(); U32(); Str(); Str(); U32(); U16();
            int nc = (int)U32();
            for (int i = 0; i < nc; i++) { U32(); U32(); Str(); U16(); }
            int nn = (int)U32();
            var names = new string[nn];
            for (int i = 0; i < nn; i++) names[i] = Str();
            return names;
        }

        public string Name(string[] names) { uint i = U32(); return i < names.Length ? names[i] : ""; }
    }

    static void ReadBone(Reader r, string[] names, out string name, out Vector3 pos, out Quaternion rot, out Vector3 scl)
    {
        name = r.Name(names);
        pos = new Vector3(r.F32(), r.F32(), r.F32());
        float w = r.F32(), x = r.F32(), y = r.F32(), z = r.F32();
        rot = new Quaternion(x, y, z, w);
        scl = new Vector3(r.F32(), r.F32(), r.F32());
    }

    public static FaceFxActor Parse(byte[] d)
    {
        var r = new Reader(d);
        var names = r.Header();
        var a = new FaceFxActor { Name = r.Name(names) };
        r.U32();
        int nb = (int)r.U32();
        a.Bones = new Bone[nb];
        for (int i = 0; i < nb; i++)
        {
            ReadBone(r, names, out var bn, out var bp, out var bq, out var bs);
            var b = new Bone { Name = bn, Pos = bp, Rot = bq, Scl = bs };
            { float iw = r.F32(), ix = r.F32(), iy = r.F32(), iz = r.F32(); b.InvRot = new Quaternion(ix, iy, iz, iw); }   // inverse Ruhedrehung
            r.U32(); r.F32();                      // Index, Gewicht
            int np = (int)r.U32();
            b.Deltas = new Delta[np];
            for (int k = 0; k < np; k++)
            {
                int node = (int)r.U32();
                ReadBone(r, names, out _, out var dp, out var dq, out var ds);
                b.Deltas[k] = new Delta { Node = node, Pos = dp, Rot = dq, Scl = ds };
            }
            a.Bones[i] = b;
        }
        int nn = (int)r.U32();
        a.Nodes = new Node[nn];
        for (int i = 0; i < nn; i++)
        {
            var n = new Node { Type = (int)r.U32(), Name = r.Name(names) };
            n.Min = r.F32(); r.F32(); n.Max = r.F32(); r.F32();
            n.Op = (int)r.U32();
            int nl = (int)r.U32();
            n.Src = new int[nl]; n.Fn = new int[nl]; n.Prm = new float[nl][];
            for (int k = 0; k < nl; k++)
            {
                n.Src[k] = (int)r.U32(); n.Fn[k] = (int)r.U32();
                int pc = (int)r.U32();
                var p = new float[pc];
                for (int j = 0; j < pc; j++) p[j] = r.F32();
                n.Prm[k] = p;
            }
            int np = (int)r.U32();
            for (int k = 0; k < np; k++) { r.U32(); r.U32(); r.I32(); r.F32(); r.U32(); r.Str(); }
            a.Nodes[i] = n;
            if (!a.NodeByName.ContainsKey(n.Name)) a.NodeByName[n.Name] = i;
        }
        return a;
    }

    // ------------------------------------------------------------------ Auswerten
    static float Link(int fn, float[] p, float x)
    {
        switch (fn)
        {
            case 1: return p.Length >= 2 ? p[0] * x + p[1] : x;          // linear (m, b)
            case 2: return p.Length >= 1 ? p[0] * x * x : x * x;         // quadratisch
            case 3: return p.Length >= 1 ? p[0] * x * x * x : x * x * x; // kubisch
            case 4: return (p.Length >= 1 ? p[0] : 1f) * MathF.Sqrt(MathF.Max(0f, x));
            case 5: return -x;                                            // negieren
            case 6: return MathF.Abs(x) > 1e-9f ? 1f / x : 0f;            // invers
            case 7: return System.Math.Clamp(1f - x, 0f, 1f);             // "one clamp"
            case 8: return p.Length >= 1 ? p[0] : 0f;                     // konstant
            case 10:                                                      // geklemmt linear (x1, y1, x2, y2)
                if (p.Length < 4) return x;
                if (x <= p[0]) return p[1];
                if (x >= p[2]) return p[3];
                return p[1] + (p[3] - p[1]) * (x - p[0]) / (p[2] - p[0]);
            default: return x;
        }
    }

    /// <summary>
    /// Werte aller Knoten (in Dateireihenfolge: Eingänge stehen immer vor ihrem Knoten).
    /// <paramref name="registers"/> (je Knoten, NaN = keines): Register-Operation "ersetzen" der Eden-Schicht (exe FUN_006ed2d0(node, 2, …)),
    /// wirkt nach Kurve und Eingängen, vor Korrektur und Klemmung (FUN_006e8a70).
    /// </summary>
    public void Evaluate(FaceFxAnim? anim, float t, float[] vals, float[]? registers = null)
    {
        float[]? track = anim?.Sample(this, t);
        for (int i = 0; i < Nodes.Length; i++)
        {
            var n = Nodes[i];
            bool hasTrack = track != null && !float.IsNaN(track[i]);
            float tv = hasTrack ? track![i] : 0f;
            float v;
            bool any = false;
            switch (n.Op)
            {
                case 1:   // Produkt
                    v = hasTrack ? tv : 1f;
                    for (int k = 0; k < n.Src.Length; k++) if (n.Fn[k] != 9) { v *= Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]); any = true; }
                    if (!any && !hasTrack) v = 0f;
                    break;
                case 2:   // Maximum
                case 3:   // Minimum
                    v = hasTrack ? tv : (n.Op == 2 ? float.MinValue : float.MaxValue);
                    for (int k = 0; k < n.Src.Length; k++)
                        if (n.Fn[k] != 9) { float x = Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]); v = n.Op == 2 ? MathF.Max(v, x) : MathF.Min(v, x); any = true; }
                    if (!any && !hasTrack) v = 0f;
                    break;
                default:  // Summe
                    v = tv;
                    for (int k = 0; k < n.Src.Length; k++) if (n.Fn[k] != 9) v += Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]);
                    break;
            }
            if (registers != null && i < registers.Length && !float.IsNaN(registers[i])) v = registers[i];
            for (int k = 0; k < n.Src.Length; k++)
                if (n.Fn[k] == 9 && n.Prm[k].Length >= 1) v *= 1f - n.Prm[k][0] * vals[n.Src[k]];
            vals[i] = v < n.Min ? n.Min : (v > n.Max ? n.Max : v);
        }
    }

    float[]? _neutral;

    /// <summary>
    /// Knotenwerte ohne Animation (Ruhe des Graphen). Einige Knoten stehen dort nicht auf 0 (beim Mann "Focus" = -1 = Augäpfel 28°
    /// gedreht, "TeethHeigh" 0,1, "tBack" 0,05): ohne FaceFX zeigt das Spiel die Ruhepose des Skeletts, also werden nur die
    /// Abweichungen von diesen Ruhewerten angewendet – sonst schielen die Figuren, sobald eine Animation läuft.
    /// </summary>
    public float[] Neutral
    {
        get
        {
            if (_neutral == null) { var v = new float[Nodes.Length]; Evaluate(null, 0f, v); _neutral = v; }
            return _neutral;
        }
    }

    /// <summary>Lokale Knochenlage im Avatar-Raum der Engine (Position in m, .xsb-Konvention wie <see cref="XsbSkeleton"/>).</summary>
    public void Pose(int bone, float[] vals, out Vector3 pos, out Quaternion rot, out Vector3 scl)
    {
        // exe FUN_00b77b20 (Knochen-Teil): Positionen/Skalen der Posen sind Deltas, Drehungen ABSOLUT (Head_Facial-Posen tragen
        // die 90°-Ruhedrehung). Je Pose: r zur Pose hin drehen (Slerp unter 0,55, sonst Lerp), acc = acc x (invRot x S).
        const float Eps = 1.192e-7f;   // DAT_00f49b08 / DAT_00f49b60
        var b = Bones[bone];
        var p = b.Pos; var s = b.Scl;
        var r = b.Rot; var acc = b.Rot;
        var n0 = Neutral;
        foreach (var dl in b.Deltas)
        {
            if (dl.Node < 0 || dl.Node >= vals.Length) continue;
            float w = vals[dl.Node] - (dl.Node < n0.Length ? n0[dl.Node] : 0f);
            if (w < Eps && w > -Eps) continue;
            p += dl.Pos * w;
            var P = dl.Rot;
            if (LenSq(r + P) < LenSq(r - P)) r = Quaternion.Negate(r);   // bleibt für die folgenden Posen
            float d = Quaternion.Dot(r, P);
            Quaternion S;
            if (d <= 0.55f)   // float @0xf90738
            {
                float th = MathF.Acos(System.Math.Clamp(d, -1f, 1f)), inv = 1f / MathF.Sin(th);
                S = r * (MathF.Sin((1f - w) * th) * inv) + P * (MathF.Sin(w * th) * inv);
            }
            else S = P * w + r * (1f - w);   // ohne Normierung
            acc = acc * (b.InvRot * S);       // Hamilton-Produkt
            s += dl.Scl * w;
        }
        float l2 = LenSq(acc);
        if (MathF.Abs(l2 - 1f) >= Eps)
            acc = l2 < Eps ? Quaternion.Identity : acc * (1f / MathF.Sqrt(l2));
        pos = new Vector3(-p.X, p.Y, p.Z) * 0.01f;
        // Eden (-x, y, z, w); in der Engine (wie XsbSkeleton) konjugiert
        rot = new Quaternion(acc.X, -acc.Y, -acc.Z, acc.W);
        scl = s;
    }

    static float LenSq(Quaternion q) => q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
}

/// <summary>Eine FaceFX-Animation (.fxe): Hermite-Kurven je Knoten.</summary>
public sealed class FaceFxAnim
{
    public string Name = "";
    public float BlendIn, BlendOut, Start, End;
    public string[] CurveNodes = Array.Empty<string>();
    public float[][] Keys = Array.Empty<float[]>();   // je Kurve: t, Wert, Steigung ein, Steigung aus (x Anzahl)
    int[]? _map; FaceFxActor? _mapFor;
    float[] _buf = Array.Empty<float>();

    public float Duration => MathF.Max(0f, End);

    static float Hermite(float[] k, float t)
    {
        int n = k.Length / 4;
        if (n == 0) return float.NaN;
        if (t <= k[0]) return k[1];
        if (t >= k[(n - 1) * 4]) return k[(n - 1) * 4 + 1];
        for (int i = 0; i < n - 1; i++)
        {
            int a = i * 4, b = a + 4;
            if (t > k[b]) continue;
            float dt = k[b] - k[a];
            float s = dt > 1e-6f ? (t - k[a]) / dt : 0f, s2 = s * s, s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * k[a + 1] + (s3 - 2 * s2 + s) * dt * k[a + 3] + (-2 * s3 + 3 * s2) * k[b + 1] + (s3 - s2) * dt * k[b + 2];
        }
        return k[(n - 1) * 4 + 1];
    }

    /// <summary>Kurvenwerte je Knoten des Darstellers (NaN = keine Kurve).</summary>
    public float[] Sample(FaceFxActor actor, float t)
    {
        if (_mapFor != actor || _map == null)
        {
            _mapFor = actor;
            _map = new int[CurveNodes.Length];
            for (int i = 0; i < CurveNodes.Length; i++) _map[i] = actor.NodeByName.TryGetValue(CurveNodes[i], out int n) ? n : -1;
            _buf = new float[actor.Nodes.Length];
        }
        for (int i = 0; i < _buf.Length; i++) _buf[i] = float.NaN;
        for (int i = 0; i < CurveNodes.Length; i++)
        {
            if (_map[i] < 0 || Keys[i].Length < 4) continue;
            _buf[_map[i]] = Hermite(Keys[i], t);
        }
        return _buf;
    }
}

/// <summary>FxAnimSet (.fxe) einer Kino-Bank.</summary>
public sealed class FaceFxAnimSet
{
    public string Actor = "";
    public readonly Dictionary<string, FaceFxAnim> Anims = new(StringComparer.OrdinalIgnoreCase);
    public bool IsFemale => Actor.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0;

    public static FaceFxAnimSet Parse(byte[] d)
    {
        var r = new FaceFxActor.Reader(d);
        var names = r.Header();
        var s = new FaceFxAnimSet();
        r.Name(names); r.U32(); s.Actor = r.Name(names);
        r.Name(names);
        int na = (int)r.U32();
        for (int i = 0; i < na; i++)
        {
            var a = new FaceFxAnim { Name = r.Name(names) };
            int nc = (int)r.U32();
            a.CurveNodes = new string[nc];
            for (int k = 0; k < nc; k++) { a.CurveNodes[k] = r.Name(names); r.U32(); }
            int nk = (int)r.U32();
            var all = new float[nk * 4];
            for (int k = 0; k < all.Length; k++) all[k] = r.F32();
            int ncnt = (int)r.U32();
            a.Keys = new float[nc][];
            int o = 0;
            float start = float.MaxValue, end = 0f;
            for (int k = 0; k < ncnt; k++)
            {
                int c = (int)r.U32();
                if (k >= nc) continue;
                c = System.Math.Clamp(c, 0, nk - o);
                a.Keys[k] = new float[c * 4];
                Array.Copy(all, o * 4, a.Keys[k], 0, c * 4);
                if (c > 0) { start = MathF.Min(start, all[o * 4]); end = MathF.Max(end, all[(o + c - 1) * 4]); }
                o += c;
            }
            for (int k = 0; k < nc; k++) a.Keys[k] ??= Array.Empty<float>();
            a.BlendIn = r.F32(); a.BlendOut = r.F32();
            r.U32(); r.U32(); r.U32(); r.I32();
            a.Start = start == float.MaxValue ? 0f : start; a.End = end;
            s.Anims[a.Name] = a;
        }
        return s;
    }
}

/// <summary>Darsteller (female.fxa / male.fxa aus avatar/avatar_face_fx.bnk), einmal geladen.</summary>
public static class FaceFxLibrary
{
    /// <summary>
    /// Die 49 Knoten der Gesichtsparameter in der Reihenfolge der FaceParamArray (.avt 9d93, AvatarSave.Config) –
    /// Tabelle 0x1675D40 der exe (FUN_00b770b0, GMSurgery 0xBEE162 / 0xBEF030).
    /// </summary>
    public static readonly string[] CustomNodes =
    {
        "CustPos Jaw thin", "CustPos Nose pushed", "C_Nose_Tall_or_Small", "C_Brow_push", "C_Cheek_size", "C_Chin_orient", "C_Chin_size",
        "C_Ear_orientation", "C_Ear_size", "C_Eyes_height", "C_Eyes_opening", "C_Eyes_right_and_left", "C_Eyes_type", "C_Eyes_up_and_down",
        "C_Eyes_width", "C_Fat_or_Thin", "C_Jaw_size", "C_Jaw_in_and_out", "C_Assymetry", "C_Lips_down_in_and_out", "C_Lips_size",
        "C_Lips_up_in_and_out", "C_LowerFace", "C_Mouth_position", "C_Mouth_size", "C_Nose_shape", "C_Nose_size", "C_Nose_thick_and_thin",
        "C_Nose_up_and_down", "C_Throat", "C_Chin_up_and_down", "C_Cheek_height", "C_Chin_Slim_or_Wide", "C_Eyebrow_inside",
        "C_Eyebrow_outside", "C_Eyebrow_pushed", "C_Head_depth", "C_Head_height", "C_Head_width", "C_EyeBrow_rotate", "C_Eyebrow_size",
        "CustPos Asian", "CustPos Afric", "CustPos Older", "C_Lower_lip_thickness", "C_Upper_lip_thickness", "C_UpperLipShrink",
        "C_LowerLipShrink", "C_LowerEyeLid_Up",
    };

    static FaceFxActor? _female, _male;
    static bool _tried;
    static readonly object _lock = new();

    public static FaceFxActor? Actor(bool woman)
    {
        lock (_lock)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    var p = Path.Combine(AvatarLibrary.BnkDir ?? "", "avatar", "avatar_face_fx.bnk");
                    if (Paradise.Core.Files.GameFiles.Exists(p))
                    {
                        var b = BnkArchive.Open(p);
                        var ef = b.Find("female.fxa"); if (ef != null) _female = FaceFxActor.Parse(b.Read(ef));
                        var em = b.Find("male.fxa"); if (em != null) _male = FaceFxActor.Parse(b.Read(em));
                        Log.Info($"FaceFX: {(_female != null ? _female.Nodes.Length : 0)} / {(_male != null ? _male.Nodes.Length : 0)} Knoten (Frau/Mann)");
                    }
                    else Log.Warn("FaceFX: avatar/avatar_face_fx.bnk fehlt");
                }
                catch (Exception ex) { Log.Error("FaceFX: " + ex.Message); }
            }
            return woman ? (_female ?? _male) : (_male ?? _female);
        }
    }

    /// <summary>Knotenindex des Gesichtsparameters <paramref name="param"/> (0..48) im Darsteller, -1 = fehlt.</summary>
    public static int NodeOf(FaceFxActor actor, int param)
        => param >= 0 && param < CustomNodes.Length && actor.NodeByName.TryGetValue(CustomNodes[param], out int n) ? n : -1;
}

/// <summary>
/// Gesichtsform eines Avatars aus den Gesichtsparametern (AvatarConfig.Face): Graph nur mit den Registern auswerten (ohne Animation,
/// exe: erzwungener Tick des Animators, Flag +0x1EC0 – gesetzt von 0x6726F0 nach jeder Änderung in der Schönheitsklinik) und die
/// 42 Gesichtsknochen mit Gewicht 1 in die Arbeits-Pose schreiben. Posen werden nur bei geänderten Parametern neu berechnet.
/// </summary>
public sealed class FaceFxFace
{
    readonly FaceFxActor _actor;
    readonly int[] _boneMap;
    readonly float[] _vals, _regs;
    readonly Vector3[] _pos, _scl;
    readonly Quaternion[] _rot;
    readonly sbyte[] _last = new sbyte[AvatarConfig.FaceParamCount];
    bool _valid;

    FaceFxFace(FaceFxActor actor, AvatarRig rig)
    {
        _actor = actor;
        _boneMap = new int[actor.Bones.Length];
        for (int i = 0; i < _boneMap.Length; i++) _boneMap[i] = rig.Bone(actor.Bones[i].Name);
        _vals = new float[actor.Nodes.Length];
        _regs = new float[actor.Nodes.Length];
        _pos = new Vector3[actor.Bones.Length]; _scl = new Vector3[actor.Bones.Length]; _rot = new Quaternion[actor.Bones.Length];
    }

    public static FaceFxFace? Create(AvatarRig rig, int sex)
    {
        var a = FaceFxLibrary.Actor(sex == 1);
        return a == null ? null : new FaceFxFace(a, rig);
    }

    /// <summary>Gesichtsform auf die Arbeits-Pose von <paramref name="rig"/> legen (nach dem Körper-Clip, vor dem Häuten).</summary>
    public void Apply(AvatarRig rig, sbyte[] face)
    {
        bool changed = !_valid;
        for (int i = 0; i < _last.Length && !changed; i++) if (i < face.Length && face[i] != _last[i]) changed = true;
        if (changed)
        {
            for (int i = 0; i < _regs.Length; i++) _regs[i] = float.NaN;
            for (int p = 0; p < AvatarConfig.FaceParamCount; p++)
            {
                int n = FaceFxLibrary.NodeOf(_actor, p);
                sbyte v = p < face.Length ? face[p] : (sbyte)0;
                _last[p] = v;
                if (n >= 0) _regs[n] = v / 100f;   // exe FUN_00b76550: (char)FaceParam / 100.0
            }
            _actor.Evaluate(null, 0f, _vals, _regs);
            for (int b = 0; b < _actor.Bones.Length; b++) _actor.Pose(b, _vals, out _pos[b], out _rot[b], out _scl[b]);
            _valid = true;
        }
        for (int b = 0; b < _boneMap.Length; b++)
            if (_boneMap[b] >= 0) rig.BlendLocal(_boneMap[b], _pos[b], _rot[b], _scl[b], 1f);
    }
}
