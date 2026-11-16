using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Fx;

/// <summary>
/// FX-Parameter einer Instanz (Laufzeitblock der exe, gelesen in 0x1159b60/0x1158dd0): +0x2c Partikel je Emission, +0x30 Geschwindigkeit,
/// +0x34 Größe, +0x38 Lebensdauer, +0x44 Emissionen/s, +0x48 Alpha (auf 0..1 begrenzt). Faktoren auf die Werte der .prt.
/// </summary>
public struct FxParams
{
    public float Size, Speed, EmisPerSec, Alpha, LifeTime, PartPerEmis;
    public static FxParams One => new() { Size = 1f, Speed = 1f, EmisPerSec = 1f, Alpha = 1f, LifeTime = 1f, PartPerEmis = 1f };
}

/// <summary>
/// Ein Emitter einer .prt (Eden edParticles) – Erzeugen, Bewegen und Kurven wie die exe (re/prt_spec.md):
/// Ausstoß 0x1159b60, Startwerte 0x1158dd0, Emitterform 0x1157f50, Schritt je Bild 0x11573f0, Kurven 0x1154ac0/0x1154b40,
/// Atlasbild 0x11543d0/0x114fc20, Zufall 0x114d760/0x114d890/0x114d8d0. Gerechnet im Raum des Aufrufers (Unity-Raum).
/// </summary>
public sealed class ParticleEmitter
{
    struct P
    {
        public Vector3 Disp, Vel, Pos;   // +0x00 (Weg dieses Schritts / dt), +0x10, +0x20
        public float Size;               // +0x30
        public float Rot;                // +0x34
        public float Weight;             // +0x38
        public float VelFactor;          // +0x3c
        public byte R, G, B, A;          // +0x40..0x43
        public byte A2;                  // +0x4b (Alpha 2, Kanal 12)
        public byte SizeX, SizeY;        // +0x5c / +0x5d
        public Vector3 VelScale;         // +0x60..0x68
        public float Mass;               // +0x6c
        public float Age, Life;          // +0x70 / +0x74
        public uint Id;                  // +0x4c
        public int FrameStart, FrameLen;
    }

    public readonly PrtFile Prt;
    readonly P[] _p;
    int _count;
    uint _rng;
    float _timer;        // B+0x1ac
    float _acc;          // B+0x1a8 (Bruchteil offener Partikel)
    float _period;       // B+0x154
    bool _mirror;        // Aufruferraum an X gespiegelt (Unity-Raum): Beschleunigungen der .prt mitspiegeln
    Matrix4x4 _forceRot = Matrix4x4.Identity;   // Lage des Effekts (nur Drehung) für die Beschleunigungen von A
    Vector3 _prevPos, _curPos;                  // Emitterposition im letzten und in diesem Bild (B+0xc0 / B+0x30)
    bool _hasPrev;
    float _dt;

    public int Count => _count;

    public ParticleEmitter(PrtFile prt)
    {
        Prt = prt;
        _p = new P[System.Math.Max(1, prt.MaxParticles)];
        _rng = prt.Seed;
        _timer = prt.StartTimer;
        _period = prt.Rate == 0f ? float.MaxValue : 1f / prt.Rate;   // exe 0x1154670
    }

    // ------------------------------------------------------------------ Zufall (exe 0x114d760 …)
    uint Next() { _rng = _rng * 0x19660du + 0x3c6ef35fu; return _rng; }
    float U01() => BitConverter.UInt32BitsToSingle(0x3f800000u | (Next() & 0x7fffffu)) - 1f;
    /// <summary>a + (2u − 1)·b (exe 0x114d890): a = Mittelwert, b = Streuung.</summary>
    float Rand(float a, float b) => a + (2f * U01() - 1f) * b;
    /// <summary>Verteilung nach Typ (exe 0x114d8d0): 1 = Gauß (0x114d7e0), 2 = logarithmisch, 3 = gleichverteilt.</summary>
    float RandDist(int dist, float a, float b) => dist switch
    {
        1 => a + (2f * Gauss01() - 1f) * b,
        2 => a - b * MathF.Log(U01() * 0.9f + 0.1f),
        3 => Rand(a, b),
        _ => 0f,
    };

    /// <summary>exe 0x114d7e0: Box-Muller, g = √(−2·ln(1−u1))·cos(2π·u2)·0,15 + 0,5, auf 0..1 begrenzt.</summary>
    float Gauss01()
    {
        float u1 = U01(), u2 = U01();
        float g = MathF.Sqrt(-2f * MathF.Log(1f - u1)) * MathF.Cos(u2 * 6.2831855f) * 0.15f + 0.5f;
        return g < 0f ? 0f : g > 1f ? 1f : g;
    }

    /// <summary>
    /// Ein Bild: Ausstoß (falls aktiv) und Bewegung aller Partikel. emitter = Lage des Effekts (Raum des Aufrufers,
    /// Zeilenvektoren wie die exe), toGame = Spiegelung zwischen Spielraum der .prt und Aufruferraum (Unity: x gespiegelt).
    /// </summary>
    public void Update(float dt, bool emit, in Matrix4x4 emitter, in FxParams fx, bool mirrorX)
    {
        if (dt <= 0f) return;
        _mirror = mirrorX;
        _dt = dt;
        if (emit)
        {
            _forceRot = emitter; _forceRot.Translation = Vector3.Zero;
            _curPos = emitter.Translation;
            if (!_hasPrev) { _prevPos = _curPos; _hasPrev = true; }
        }
        Step(dt);
        if (emit) { Emit(dt, emitter, fx, mirrorX); _prevPos = _curPos; }
        else { _timer = Prt.StartTimer; _hasPrev = false; }
    }

    void Emit(float dt, in Matrix4x4 emitter, in FxParams fx, bool mirrorX)
    {
        float period = fx.EmisPerSec == 0f ? float.MaxValue : _period / fx.EmisPerSec;
        // exe 0x1159c3b: Restzeit = Zähler + dt − Periode; je Periode eine Emission, neue Partikel mit dem Zeitversatz
        float t = _timer + dt - period;
        if (t < 0f) { _timer += dt; return; }
        while (t >= 0f)
        {
            _acc += fx.PartPerEmis * Prt.PartsPerEmission * Prt.EmitFactor;
            if (_acc > 4294967296f) _acc = 4294967296f;
            float min = period * Prt.MinPerEmission;   // exe: max(offen, Periode × B+0x18c)
            if (_acc < min) _acc = min;
            while (_acc >= 1f)
            {
                Spawn(t, emitter, fx, mirrorX);
                _acc -= 1f;
            }
            t -= period;
        }
        _timer = t + period;
    }

    void Spawn(float ageOffset, in Matrix4x4 emitter, in FxParams fx, bool mirrorX)
    {
        if (_count >= _p.Length)
        {
            if (Prt.Recycle != 1) return;
            // ältestes ersetzen (exe: Ringindex +0x70, Partikel auf Zustand 3)
            int oldest = 0;
            for (int i = 1; i < _count; i++) if (_p[i].Age > _p[oldest].Age) oldest = i;
            _p[oldest] = _p[--_count];
        }
        var p = new P();
        // Emitterform (0x1157f50): Position im Formraum, Richtung (Modus 1)
        ShapePoint(out var pos, out var dir, out bool faceDir);
        pos = Vector3.Transform(pos, Prt.EmitterMatrix);
        int dirMode = faceDir && Prt.DirMode == 1 ? 2 : Prt.DirMode;
        Vector3 v = dirMode switch
        {
            1 => dir,
            2 => Prt.Direction,
            _ => Vector3.UnitY,   // exe: globaler Vektor 0x16920f0 (Laufzeitwert, hier Weltachse oben)
        };
        float len = v.Length();
        v = len > 1e-6f ? v / len : Vector3.UnitY;
        float cone = RandDist(Prt.ConeDist, Prt.ConeMean, Prt.ConeSpread);
        if (cone != 0f) v = Deviate(v, cone);
        v *= RandDist(Prt.SpeedDist, Prt.SpeedMean, Prt.SpeedSpread);
        // Lage des Emitters im Effekt (B+0x40): Formgröße, Ausstoßrichtung, Geschwindigkeit mitskaliert
        pos = Vector3.Transform(pos, Prt.LocalMatrix);
        v = Vector3.TransformNormal(v, Prt.LocalMatrix);
        if (mirrorX) { pos.X = -pos.X; v.X = -v.X; }
        p.Pos = Vector3.Transform(pos, emitter);
        // B+0x1a4: Partikel, die vor ageOffset Sekunden fällig waren, an der damaligen Emitterposition (sonst Lücken bei schneller Fahrt)
        if (Prt.Interpolate && _dt > 0f) p.Pos += (_prevPos - _curPos) * System.Math.Clamp(ageOffset / _dt, 0f, 1f);
        p.Vel = Vector3.TransformNormal(v, emitter) * fx.Speed;

        // Startwerte (0x1158dd0)
        p.Life = RandDist(Prt.LifeDist, Prt.LifeMean, Prt.LifeSpread) * fx.LifeTime;
        p.Age = 0f;
        if (ageOffset >= p.Life) ageOffset = 0f;
        p.Id = Next();
        float alpha = System.Math.Clamp(fx.Alpha, 0f, 1f);
        p.R = ToByte(Rand(Prt.ColorMean[0], Prt.ColorSpread[0]));
        p.G = ToByte(Rand(Prt.ColorMean[1], Prt.ColorSpread[1]));
        p.B = ToByte(Rand(Prt.ColorMean[2], Prt.ColorSpread[2]));
        p.A = ToByte(Rand(Prt.ColorMean[3], Prt.ColorSpread[3]) * alpha);
        p.Rot = Rand(Prt.RotMean, Prt.RotSpread);
        p.Size = Rand(Prt.SizeMean, Prt.SizeSpread);
        p.A2 = 255; p.SizeX = 255; p.SizeY = 255;
        p.Weight = Rand(Prt.WeightMean, Prt.WeightSpread);
        if ((Prt.Flags174 & 1) != 0) p.Weight = p.Weight * p.Size * p.Size;
        if (p.Weight < 1.1754944e-38f) p.Weight = 1.1754944e-38f;
        p.Mass = Rand(Prt.MassMean, Prt.MassSpread);
        if (p.Mass < 1.1754944e-38f) p.Mass = 1.1754944e-38f;
        p.VelFactor = 1f;
        p.Size *= Prt.SizeFactor * fx.Size;
        p.VelScale = Vector3.One;
        InitFrames(ref p);
        Advance(ref p, ageOffset);   // erster Schritt mit dem Zeitversatz der Emission
        if (p.Age > p.Life || p.Age < 0f) return;
        _p[_count++] = p;
    }

    static byte ToByte(float v) => (byte)System.Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>Emitterformen 1..9 (exe 0x1157f50, Formraum ±0,5). dir nur für Richtungsmodus 1.</summary>
    void ShapePoint(out Vector3 pos, out Vector3 dir, out bool faceDir)
    {
        pos = Vector3.Zero; dir = Vector3.UnitY; faceDir = false;
        int sub = Prt.ShapeSub;
        switch (Prt.Shape)
        {
            case 7:   // Punkt; Richtung zufällig im Würfel
                dir = new Vector3(U01() - 0.5f, U01() - 0.5f, U01() - 0.5f);
                break;
            case 8:   // Strecke auf X
                if (sub >= 2) sub = 1;
                if (sub == 0) { pos.X = (Next() & 1) != 0 ? -0.5f : 0.5f; dir = new Vector3(pos.X > 0 ? 1f : -1f, 0f, 0f); }
                else { pos.X = U01() - 0.5f; dir = new Vector3(U01() - 0.5f, U01() - 0.5f, U01() - 0.5f); }
                break;
            case 9:   // Kreis in XZ: Ring (1) / Scheibe (2)
                {
                    if (sub == 0) sub = 1; else if (sub == 3) sub = 2;
                    float a = U01() * MathF.Tau;
                    float c = MathF.Cos(a), s = MathF.Sin(a);
                    dir = new Vector3(c, 0f, s);
                    if (sub == 1) pos = new Vector3(c * 0.5f, 0f, s * 0.5f);
                    else { float r = U01() * 0.5f; pos = new Vector3(c * r, 0f, s * r); }
                    break;
                }
            case 6:   // Quadrat in XZ: Ecke (0) / Kante (1) / Fläche (2; Richtungsmodus 1 wird dort zu 2)
                {
                    if (sub == 3) sub = 2;
                    if (sub == 2) { pos = new Vector3(U01() - 0.5f, 0f, U01() - 0.5f); faceDir = true; }
                    else if (sub == 1)
                    {
                        int e = (int)(Next() & 3);
                        float t = U01();
                        var a = Corner(e); var bb = Corner((e + 1) & 3);   // Kanten (0,1) (1,2) (2,3) (3,0), Tabelle 0x1584eb0
                        pos = bb * t + a * (1f - t);
                        dir = EdgeNormal(e);                                 // Tabelle 0x1584e70
                    }
                    else { pos = Corner((int)(Next() & 3)); dir = pos; }
                    break;
                }
            case 3:   // Zylinder (Mantel / Volumen), Achse Y
                {
                    if (sub <= 1) sub = 2;
                    float a = U01() * MathF.Tau;
                    float c = MathF.Cos(a), s = MathF.Sin(a);
                    dir = new Vector3(c, 0f, s);
                    float r = sub == 2 ? 0.5f : U01() * 0.5f;
                    pos = new Vector3(c * r, U01() - 0.5f, s * r);
                    break;
                }
            case 2:   // Kugel (Oberfläche 2 / Volumen 3)
                {
                    if (sub <= 1) sub = 2;
                    float th = U01() * MathF.Tau;
                    float ph = (U01() - 0.5f) * MathF.PI;
                    dir = new Vector3(MathF.Cos(ph) * MathF.Cos(th), MathF.Sin(ph), MathF.Cos(ph) * MathF.Sin(th));
                    pos = dir * (sub == 2 ? 0.5f : U01() * 0.5f);
                    break;
                }
            case 1:   // Würfel: Volumen
                pos = new Vector3(U01() - 0.5f, U01() - 0.5f, U01() - 0.5f);
                dir = pos;
                break;
        }
    }

    /// <summary>Ecken des Quadrats (exe 0x1584e90, x/z).</summary>
    static Vector3 Corner(int i) => i switch
    {
        0 => new Vector3(0.5f, 0f, 0.5f),
        1 => new Vector3(0.5f, 0f, -0.5f),
        2 => new Vector3(-0.5f, 0f, -0.5f),
        _ => new Vector3(-0.5f, 0f, 0.5f),
    };

    /// <summary>Kantennormalen (exe 0x1584e70, x/z).</summary>
    static Vector3 EdgeNormal(int e) => e switch
    {
        0 => new Vector3(1f, 0f, 0f),
        1 => new Vector3(0f, 0f, -1f),
        2 => new Vector3(-1f, 0f, 0f),
        _ => new Vector3(0f, 0f, 1f),
    };

    /// <summary>Richtung um den Winkel zufällig um die Achse kippen (exe 0x1157d80).</summary>
    Vector3 Deviate(Vector3 v, float angle)
    {
        var a = MathF.Abs(v.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var x = Vector3.Normalize(Vector3.Cross(v, a));
        var y = Vector3.Cross(v, x);
        float az = U01() * MathF.Tau;
        var axis = x * MathF.Cos(az) + y * MathF.Sin(az);
        return Vector3.Transform(v, Quaternion.CreateFromAxisAngle(axis, angle));
    }

    // ------------------------------------------------------------------ Schritt je Bild (exe 0x11573f0)
    void Step(float dt)
    {
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            var p = _p[i];
            Advance(ref p, dt);
            if (p.Age > p.Life || p.Age < 0f) continue;   // Zustand 3
            _p[n++] = p;
        }
        _count = n;
    }

    void Advance(ref P p, float dt)
    {
        var acc = Vector3.Zero;
        p.Age += dt;
        if ((Prt.GroupFlags & 0x40) != 0) ApplyCurves(ref p);
        if (Prt.HasForces || (Prt.GroupFlags & 1) != 0)
        {
            acc += Prt.Gravity;
            if ((Prt.GroupFlags & 1) != 0) acc += Prt.GroupAccel;
            // Zufallsbeschleunigung aus der Partikelkennung (exe: Bits 0..15 / 0..17 / 0..16)
            float fx = (p.Id & 0xffff) * 1.5259022e-5f, fy = (p.Id & 0x3ffff) * 3.8147118e-6f, fz = (p.Id & 0x1ffff) * 7.6294527e-6f;
            acc += new Vector3(
                Prt.RandomAccelMin.X + fx * (Prt.RandomAccelMax.X - Prt.RandomAccelMin.X),
                Prt.RandomAccelMin.Y + fy * (Prt.RandomAccelMax.Y - Prt.RandomAccelMin.Y),
                Prt.RandomAccelMin.Z + fz * (Prt.RandomAccelMax.Z - Prt.RandomAccelMin.Z));
        }
        // A+0x00..0x28 im Emitterraum (B+0x40), dann Lage des Effekts: Leerlauf-Auspuff +90 in z → nach der Drehung um −90° (x) nach oben
        acc = Vector3.TransformNormal(acc, Prt.LocalMatrix);
        if (_mirror) acc.X = -acc.X;
        acc = Vector3.TransformNormal(acc, _forceRot);
        p.Vel += acc * dt;
        p.Disp = p.Vel * p.VelScale * p.VelFactor;
        p.Pos += p.Disp * dt;
    }

    /// <summary>Kurven über die Lebenszeit (exe 0x1154ac0/0x1154b40): 28 Stützwerte, Kanal → Feld (0x115c690).</summary>
    void ApplyCurves(ref P p)
    {
        float t = p.Life > 0f ? p.Age / p.Life : 1f;
        t = System.Math.Clamp(t, 0f, 1f) * 27f;
        int i = (int)t;
        foreach (var c in Prt.Curves)
        {
            var s = c.Samples;
            float v = i >= 27 ? s[27] : s[i] + (s[i + 1] - s[i]) * (t - i);
            switch (c.Channel)
            {
                case 1: p.R = Trunc(v); break;
                case 2: p.G = Trunc(v); break;
                case 3: p.B = Trunc(v); break;
                case 4: p.A = Trunc(v); break;
                case 9: p.SizeX = Trunc(v); break;
                case 10: p.SizeY = Trunc(v); break;
                case 11: p.Rot = v; break;
                case 12: p.A2 = Trunc(v); break;
                case 13: p.VelFactor = v; break;
                case 17: p.VelScale.X = v; break;
                case 18: p.VelScale.Y = v; break;
                case 19: p.VelScale.Z = v; break;
            }
        }
    }

    static byte Trunc(float v) => (byte)System.Math.Clamp((int)v, 0, 255);   // cvttss2si → Byte

    // ------------------------------------------------------------------ Atlas (exe 0x11543d0 / 0x114fc20)
    void InitFrames(ref P p)
    {
        if (!Prt.Atlas) { p.FrameStart = 0; p.FrameLen = 1; return; }   // C+0x210 Bit 0 aus: ganze Textur (exe 0x114ffe0)
        int total = Prt.AtlasCols * Prt.AtlasRows;
        int start = Prt.FrameStart + (Prt.FrameStartRand > 1 ? (int)(p.Id % (uint)Prt.FrameStartRand) : 0);
        int cnt = Prt.FrameCount + (Prt.FrameCountRand > 1 ? (int)((p.Id >> 1) % (uint)Prt.FrameCountRand) : 0);
        if (start >= total) start = total - 1;
        int last = start + cnt - 1;
        if (last >= total) last = total - 1;
        p.FrameStart = start;
        p.FrameLen = System.Math.Max(1, last - start + 1);
    }

    int Frame(in P p)
    {
        int f;
        if (Prt.AnimMode == 0)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 0f;
            f = (int)(p.FrameLen * t);
        }
        else f = (int)(Prt.AnimFps * p.Age);
        if (f >= p.FrameLen) f = p.FrameLen - 1;
        if (f < 0) f = 0;
        return p.FrameStart + f;
    }

    // ------------------------------------------------------------------ Zeichnen (Kamera-Billboards)
    /// <summary>Vertices anhängen; Größe = Größe × Byte(0x5c/0x5d)/255 × C+0x214 (exe 0x1156b10: Halbe = …/510).</summary>
    static readonly byte[] SrgbToLinear = BuildSrgbTable();
    static byte[] BuildSrgbTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            float l = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            t[i] = (byte)System.Math.Clamp((int)MathF.Round(l * 255f), 0, 255);
        }
        return t;
    }

    int[] _order = Array.Empty<int>();
    float[] _depth = Array.Empty<float>();

    /// <summary>Mittlere Tiefe entlang der Blickrichtung (zum Sortieren der Effekte untereinander).</summary>
    public float Depth(Vector3 camPos, Vector3 camFwd)
    {
        if (_count == 0) return 0f;
        float s = 0f;
        for (int i = 0; i < _count; i++) s += Vector3.Dot(_p[i].Pos - camPos, camFwd);
        return s / _count;
    }

    public void Build(List<ObjectVertex> v, List<uint> idx, Vector3 right, Vector3 up, Vector3 camPos, Vector3 camFwd)
    {
        int cols = Prt.Atlas ? Prt.AtlasCols : 1;
        float du = Prt.Atlas ? 1f / Prt.AtlasCols : 1f, dv = Prt.Atlas ? 1f / Prt.AtlasRows : 1f;
        // Alpha-Mischung hinten → vorne (sonst decken die fernen Partikel die nahen eckig ab)
        if (_order.Length < _count) { _order = new int[_p.Length]; _depth = new float[_p.Length]; }
        for (int i = 0; i < _count; i++) { _order[i] = i; _depth[i] = -Vector3.Dot(_p[i].Pos - camPos, camFwd); }
        System.Array.Sort(_depth, _order, 0, _count);
        for (int k = 0; k < _count; k++)
        {
            ref var p = ref _p[_order[k]];
            float hx = p.Size * p.SizeX * Prt.SizeScale / 510f;
            float hy = p.Size * p.SizeY * Prt.SizeScale / 510f;
            if (hx <= 0f && hy <= 0f) continue;
            float c = MathF.Cos(p.Rot), s = MathF.Sin(p.Rot);
            var rx = (right * c + up * s) * hx;
            var ry = (up * c - right * s) * hy;
            int f = Frame(p);
            float u0 = (f % cols) * du, v0 = (f / cols) * dv;
            float ua = u0, ub = u0 + du, va = v0, vb = v0 + dv;
            if (Prt.FlipU && ((p.Id >> 6) & 1) != 0) (ua, ub) = (ub, ua);
            if (Prt.FlipV && ((p.Id >> 7) & 1) != 0) (va, vb) = (vb, va);
            // Vertexfarbe des Originals ist Gamma-Raum (D3D9, td_particle: Textur × Farbe); unser Bild rechnet linear → umrechnen
            uint col = ObjectVertex.PackColor(SrgbToLinear[p.R], SrgbToLinear[p.G], SrgbToLinear[p.B], (byte)(p.A * p.A2 / 255));
            uint b = (uint)v.Count;
            v.Add(new ObjectVertex { Position = p.Pos - rx - ry, Normal = up, Color = col, Uv0 = new Vector2(ua, vb) });
            v.Add(new ObjectVertex { Position = p.Pos - rx + ry, Normal = up, Color = col, Uv0 = new Vector2(ua, va) });
            v.Add(new ObjectVertex { Position = p.Pos + rx + ry, Normal = up, Color = col, Uv0 = new Vector2(ub, va) });
            v.Add(new ObjectVertex { Position = p.Pos + rx - ry, Normal = up, Color = col, Uv0 = new Vector2(ub, vb) });
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2); idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
        }
    }

    /// <summary>Floating Origin: Verschiebung nachführen.</summary>
    public void Shift(Vector3 d) { for (int i = 0; i < _count; i++) _p[i].Pos -= d; _prevPos -= d; _curPos -= d; }

    public void Clear() { _count = 0; _timer = Prt.StartTimer; _acc = 0f; _hasPrev = false; }
}
