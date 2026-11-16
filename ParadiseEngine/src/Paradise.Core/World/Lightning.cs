using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Gewitter wie im Original (WFX_Lightning, Ctor 0xa59ed0, Lader 0xa59f40, Update 0xa5d5a0, Wetter 0xa5d310). Selbst
/// disassembliert; Felder in Klammern = Versatz im Original-Objekt.
/// <list type="bullet">
/// <item><b>Zustände</b> (+0x310, Sprungtabelle 0xa5dd38): 0 Vorflackern (bis +0x3df4) → 1 Wachsen (0,2 s Pause, dann
///   +0x3de8 lang; sichtbare Punkte = ceil(p·Anzahl)) → 2 Stehen (+0x3df0; danach Donner) → 3 Ausblenden (+0x3dfc, Alpha
///   1 − t/Dauer, Blitz nicht mehr gezeichnet) → 4 Nachflackern (+0x3df8) → 5 Warten (+0x3e00) → neu würfeln, zurück zu 0.</item>
/// <item><b>Würfeln</b> (5 → 0): Wachsen rand(0,1; 0,2), Ausblenden 0,8, Abstand rand(thunder_time_spacing_min_max.xy),
///   Stehen rand(thunder_lightning_duration_min_max.xy), Vorflackern rand(thunder_flicker_duration_min_max.xy),
///   Nachflackern rand(.zw) + rand(0,1; 0,12); Wetterwerte festhalten (0xa5c070), Form (0xa5a380), Ort (0xa5a5e0).</item>
/// <item><b>Flackern</b> (0xa59d70): Frequenz rand(fA, fB) je Bild, Phase += f·2π·dt, an = sin(Phase) &gt; 0, bei jedem
///   Einschalten Stärke rand(iA, iB). Zustand 0: (freq.x, freq.y, int.x, int.x) – int.y wird im Original nicht gelesen;
///   sonst (freq.zw, int.zw). Glättung (0xa59e60): s = k·Flackern + (1 − k)·s, k = Ziel^(dt·30), Ziel 0,9 bzw. 0,3 ab Zustand 4.</item>
/// <item><b>Blitzlicht</b> (+0x3e08..10): beim Start des Wachsens (4, 4, 1), je Bild ×0,7^(dt·30).</item>
/// <item><b>Form</b> (0xa5a0f0): Mittelpunktverschiebung nur in x, Stufen-Amplitude ×0,48, Zufall rand·0,1 + 0,9 mit Vorzeichen.
///   Hauptast (r2, 1) → (r1, 0), r = rand(−0,2; 0,2), 6 Stufen = 65 Punkte, Amplitude 0,05. Ebenen 2..5: je rand%3 + 2 Äste vom
///   Ast [Ebene − 2], gleichmäßig verteilt, Richtung = (Mitte − Anfang)·rand(0,6; 0,9), x zusätzlich ±rand(0,9; 1,1),
///   5 Stufen (33 Punkte), Amplitude 0,05/(Ebene − 1); Start = Start des Elternasts + Index.</item>
/// <item><b>Ort</b> (0xa5a5e0): Entfernung S = rand·2000 + 1000 m in der waagrechten Blickrichtung der Kamera, Höhe des Bodens dort,
///   Größe D = (rand·0,3 + 0,8)·800 m. Der seitliche Versatz des Originals (±S·[Kamera+0x17c]·1,5) ist 0: das Feld ist das w
///   einer Culling-Maske der Kamera (0xf620e0) und dort immer 0.</item>
/// <item><b>Streifen</b> (0xa5a970): halbe Breite = S·0,0075/3000/Ebene, u = −1/+1, Spitze u = 0, v = 1/Ebene, Vertexalpha =
///   Alpha·255 (Hauptast: die ersten 5 Punkte i/5). Zeichnen TD_Lightning (Shader in GameShaders): Drehung um Y mit
///   atan2(Kamerablick.x, .z), Farbe = lerp((1 − u²)²·v, (1 − clamp(5u)²)·v, 0,1), additiv SrcAlpha/One.</item>
/// <item><b>Wetter</b> (0xa5d310, jedes Bild): sun_ambient += Blitzlicht.r, car_headlights_intensity ·= 1 − 0,25·r;
///   Zustand 0/4 → 0xa5baf0 (Flackern), 1–3 → 0xa5af80 (Blitzlicht auf die Szene, solange Alpha ≥ 0,7).</item>
/// </list>
/// </summary>
public sealed class Lightning : IDisposable
{
    /// <summary>Zählt die Einschläge (Übergang Stehen → Ausblenden, Original: Umgebung spielt dann den Donner, 0x657340).</summary>
    public static int Strikes { get; private set; }

    sealed class Branch
    {
        public Vector2[] P = Array.Empty<Vector2>();
        public int Start, Level;
    }

    readonly Random _rnd = new();
    readonly List<Branch> _branches = new();
    readonly Dictionary<string, Vector4> _ov = new(StringComparer.Ordinal);

    // Zustand (Werte des Laders 0xa59f40)
    int _state = 5;
    float _timer = 1f, _alpha;
    float _grow = 0.15f, _c3dec = 0.35f, _fade = 1f, _duration, _pre, _post, _spacing;
    float _target;                       // +0x3e14
    float _phase, _flickOn, _pulse, _smooth;   // +0x3e18, +0x3e1c, +0x3e24, +0x3e20
    Vector3 _color;                      // +0x3e08..10
    float _size;                         // +0x3de4 (Entfernung S)
    float _scale;                        // D
    Vector3 _pos;                        // Spielkoordinaten (Fußpunkt)
    bool _enabled;
    Vector3 _cam;                        // Kamera (Spielkoordinaten), 0xa5af80 liest [0x19c49ec]+0x3e0

    // festgehaltene Wetterwerte (0xa5c070)
    Vector4 _sHalo, _sCore, _sLens, _sDir, _sSpec, _sCloud, _sCloud2, _sFog;
    float _sOverlay, _sSoft, _sHgg, _sMoon;
    Vector4 _halo, _core, _lens;          // +0x3e30, +0x3e50, +0x3e40
    float _hgg, _moon;                    // +0x3e60, +0x3e64

    ObjectVertex[] _verts = new ObjectVertex[3120];
    uint[] _idx = new uint[3120 * 3];
    int _vCount, _iCount;
    IGpuMaterial? _mat;
    IRenderer? _matOwner;

    float Rand01() => _rnd.Next(0, 32768) / 32767f;          // MSVC rand()/32767
    float Rand(float a, float b) => a + (b - a) * Rand01();     // 0x538350
    int RandInt() => _rnd.Next(0, 32768);

    /// <summary>Wert eines Wetterparameters mit den Überschreibungen dieses Bildes (sonst raw).</summary>
    public Vector4 Value(string name, Vector4 raw) => _ov.TryGetValue(name, out var v) ? v : raw;

    /// <summary>
    /// Jedes Bild. raw = Wetterwerte ohne Blitz, rawDir = sundirection, on = thunder_off_on &gt; 0,5 (FUN_00aa6ce0 → 0xa59d00).
    /// camera/forward in Spielkoordinaten, ground(x, z) = Bodenhöhe oder null.
    /// </summary>
    public void Update(float dt, bool on, Func<string, Vector4, Vector4> raw, Vector4 rawDir,
                       Vector3 camera, Vector3 forward, Func<float, float, float?> ground)
    {
        _ov.Clear();
        _cam = camera;
        if (!on)
        {
            // 0xa59d00(false): Zustand 5, alles aus
            if (_enabled || _state != 5)
            {
                _state = 5; _timer = 0f; _phase = 0f; _flickOn = 0f; _smooth = 0f; _color = Vector3.Zero;
            }
            _enabled = false; _vCount = 0; _iCount = 0;
            return;
        }
        _enabled = true;
        _timer += dt;
        var freq = raw("thunder_flicker_frequency_min_max", Vector4.Zero);
        var inten = raw("thunder_flicker_intensity_min_max", Vector4.Zero);
        float decay = MathF.Pow(0.7f, dt * 30f);
        _color *= decay;
        float fA = freq.X, fB = freq.Y, iA = inten.X, iB = inten.X;   // Original: int.x zweimal
        if (_state > 0) { fA = freq.Z; fB = freq.W; iA = inten.Z; iB = inten.W; }

        float flick = 0f;
        bool build = false;
        int count = 0;
        switch (_state)
        {
            case 0:
                if (_timer > _pre) { _timer = 0f; _state = 1; _color = new Vector3(4f, 4f, 1f); }
                else flick = Flicker(dt, fA, fB, iA, iB);
                break;
            case 1:
                build = true;
                if (_timer > _grow + 0.2f) { _alpha = 1f; _state = 2; _timer = 0f; count = Main.P.Length; }
                else
                {
                    float p = MathF.Max(0f, (_timer - 0.2f) / _grow);
                    count = (int)MathF.Ceiling(p * Main.P.Length);
                    _alpha = 1f;
                }
                break;
            case 2:
                build = true;
                if (_timer > _duration)
                {
                    _alpha = 1f; _state = 3; _timer = 0f;
                    Strikes++;   // 0x657340: Donner der Umgebung (zufälliger Lightning-Eintrag der ambiences.xmb)
                }
                else count = Main.P.Length;
                break;
            case 3:
                build = true;
                if (_timer > _fade) { _timer = 0f; _alpha = 0f; _state = 4; _target = 0.3f; }
                else _alpha = System.Math.Clamp(1f - _timer / _fade, 0f, 1f);
                break;
            case 4:
                build = true;
                if (_timer > _post) { _state = 5; _timer = 0f; _alpha = 0f; }
                else flick = Flicker(dt, fA, fB, iA, iB);
                break;
            case 5:
                if (_timer > _spacing)
                {
                    _timer = 0f; _target = 0.9f; _state = 0; build = true;
                    Reroll(raw, rawDir, camera, forward, ground);
                }
                break;
        }
        // 0xa59e60: Glättung des Flackerns
        if (_target > 0f)
        {
            float k = MathF.Pow(_target, dt * 30f);
            _smooth = (1f - k) * _smooth + k * flick;
        }
        if (build) Build(count);   // 0x42bb84(count)
        Apply(raw);
    }

    Branch Main => _branches.Count > 0 ? _branches[0] : EmptyBranch;
    static readonly Branch EmptyBranch = new();

    /// <summary>0xa59d70</summary>
    float Flicker(float dt, float fA, float fB, float iA, float iB)
    {
        float f = fA + (fB - fA) * Rand01();
        _phase += f * MathF.Tau * dt;
        float old = _flickOn;
        float on = MathF.Sin(_phase) > 0f ? 1f : 0f;
        _flickOn = on;
        if (old == 0f && on > 0f) _pulse = Rand(iA, iB);
        _flickOn = _pulse * on;
        return _flickOn;
    }

    void Reroll(Func<string, Vector4, Vector4> raw, Vector4 rawDir, Vector3 camera, Vector3 forward, Func<float, float, float?> ground)
    {
        var ld = raw("thunder_lightning_duration_min_max", Vector4.Zero);
        var sp = raw("thunder_time_spacing_min_max", Vector4.Zero);
        var fd = raw("thunder_flicker_duration_min_max", Vector4.Zero);
        _grow = Rand(0.1f, 0.2f);
        _fade = Rand(0.8f, 0.8f);
        _spacing = Rand(sp.X, sp.Y);
        _duration = Rand(ld.X, ld.Y);
        _c3dec = Rand(0.1f, 0.12f);
        _pre = Rand(fd.X, fd.Y);
        _post = Rand(fd.Z, fd.W) + _c3dec;
        _smooth = 0f; _flickOn = 0f;
        Snapshot(raw, rawDir);
        float r1 = Rand(-0.2f, 0.2f), r2 = Rand(-0.2f, 0.2f);
        BuildPaths(new Vector2(r2, 1f), new Vector2(r1, 0f));
        Place(camera, forward, ground);
    }

    /// <summary>0xa5c070: Wetterwerte beim Würfeln festhalten (gemischte Werte ohne Blitz).</summary>
    void Snapshot(Func<string, Vector4, Vector4> raw, Vector4 rawDir)
    {
        _sHalo = raw("sunflare_halo_color", Vector4.Zero);
        _sCore = raw("sunflare_core_color", Vector4.Zero);
        _sOverlay = raw("photoshop_overlay_opacity", new Vector4(0.30f, 0, 0, 0)).X;
        _sSoft = raw("photoshop_softlight_opacity", new Vector4(0.55f, 0, 0, 0)).X;
        _sLens = raw("sunflare_lensflare_color", Vector4.Zero);
        _sHgg = raw("Skydome_HGg", Vector4.Zero).X;
        _sDir = rawDir;
        _sSpec = raw("sun_specular", new Vector4(1f, 1f, 1f, 0));
        _sCloud = raw("clouds_cloud3dcolor", new Vector4(1f, 1f, 1f, 0f));
        _sCloud2 = raw("clouds_cloud3dcolor2", Vector4.Zero);
        _sFog = raw("fog_color", new Vector4(0.7f, 0.62f, 0.55f, 0));
        _sMoon = raw("Skydome_moonPercent", Vector4.Zero).X;
        _halo = _sHalo; _lens = _sLens; _core = _sCore; _hgg = _sHgg; _moon = _sMoon;
    }

    // ------------------------------------------------------------------ Form
    /// <summary>0xa5a0f0: Mittelpunktverschiebung (nur x), levels Stufen, Amplitude ×0,48 je Stufe.</summary>
    Vector2[] Generate(Vector2 a, Vector2 b, int levels, float amp)
    {
        var buf = new List<Vector2>(140) { a, b };
        int s = 0, e = 1;
        Vector2 Mid(Vector2 p, Vector2 q)
        {
            var m = (p + q) * 0.5f;
            float sign = ((RandInt() % 2) - 0.5f) * 2f;
            float r = Rand01() * 0.1f + 0.9f;
            m.X += r * sign * amp;
            return m;
        }
        for (int lvl = 1; lvl < levels; lvl++)
        {
            int ns = buf.Count;
            buf.Add(buf[s]);
            for (int i = s; i < e; i++) { buf.Add(Mid(buf[i], buf[i + 1])); buf.Add(buf[i + 1]); }
            s = ns; e = buf.Count - 1;
            amp *= 0.48f;
        }
        var outp = new List<Vector2>(65) { buf[s] };
        for (int i = s; i < e; i++) { outp.Add(Mid(buf[i], buf[i + 1])); outp.Add(buf[i + 1]); }
        return outp.ToArray();
    }

    /// <summary>0xa5a380: Hauptast und Äste der Ebenen 2..5.</summary>
    void BuildPaths(Vector2 a, Vector2 b)
    {
        _branches.Clear();
        _branches.Add(new Branch { P = Generate(a, b, 6, 0.05f), Start = 0, Level = 1 });
        for (int lvl = 2; lvl < 6; lvl++)
        {
            if (lvl - 2 >= _branches.Count) break;
            var parent = _branches[lvl - 2];
            int k = RandInt() % 3 + 2;
            int n = parent.P.Length;
            int step = n / (k + 1);
            var dir = parent.P[n / 2] - parent.P[0];
            float amp = 1f / (lvl - 1) * 0.05f;
            int idx = step;
            for (int j = 0; j < k && _branches.Count < 24; j++)
            {
                var p = parent.P[idx];
                float s1 = Rand01() * 0.3f + 0.6f;
                var v = dir * s1;
                float s2 = Rand01() * 0.2f + 0.9f;
                float sign = ((RandInt() % 2) - 0.5f) * 2f;
                v.X = sign * s2 * v.X;
                _branches.Add(new Branch { P = Generate(p, p + v, 5, amp), Start = parent.Start + idx, Level = lvl });
                idx += step;
            }
        }
    }

    /// <summary>0xa5a5e0: Ort vor der Kamera (waagrechte Blickrichtung), Größe.</summary>
    void Place(Vector3 camera, Vector3 forward, Func<float, float, float?> ground)
    {
        _size = Rand01() * 2000f + 1000f;
        _scale = (Rand01() * 0.3f + 0.8f) * 800f;
        var h = new Vector3(forward.X, 0f, forward.Z);
        h = h.LengthSquared() > 1e-12f ? Vector3.Normalize(h) : Vector3.UnitZ;
        var p = camera + h * _size;
        p.Y = ground(p.X, p.Z) ?? 0f;
        _pos = p;
    }

    // ------------------------------------------------------------------ Streifen (0xa5a970)
    void Build(int n)
    {
        _vCount = 0; _iCount = 0;
        if (_branches.Count == 0) return;
        float hw0 = _size * 0.0075f / 3000f;
        byte full = (byte)(int)(_alpha * 255f);
        for (int b = 0; b < _branches.Count; b++)
        {
            var br = _branches[b];
            int m = n - br.Start;
            if (m < 2) continue;
            if (m > br.P.Length) m = br.P.Length;
            int last = m - 1;
            float hw = hw0 / br.Level, v = 1f / br.Level;
            int first = _vCount;
            for (int i = 0; i < last; i++)
            {
                byte a = full;
                if (b == 0 && i < 6) a = (byte)(int)(i / 5f * _alpha * 255f);   // Hauptast: Anfang einblenden
                Vert(new Vector3(br.P[i].X - hw, br.P[i].Y, 0f), -1f, v, a);
                Vert(new Vector3(br.P[i].X + hw, br.P[i].Y, 0f), 1f, v, a);
            }
            Vert(new Vector3(br.P[last].X, br.P[last].Y, 0f), 0f, v, full);   // Spitze
            // Dreiecksstreifen -> Liste
            for (int i = first; i + 2 < _vCount; i++)
            {
                if (_iCount + 3 > _idx.Length) break;
                bool odd = ((i - first) & 1) != 0;
                _idx[_iCount++] = (uint)(odd ? i + 1 : i);
                _idx[_iCount++] = (uint)(odd ? i : i + 1);
                _idx[_iCount++] = (uint)(i + 2);
            }
        }
    }

    void Vert(Vector3 p, float u, float v, byte a)
    {
        if (_vCount >= _verts.Length) return;
        _verts[_vCount++] = new ObjectVertex { Position = p, Uv0 = new Vector2(u, v), Color = ObjectVertex.PackColor(255, 255, 255, a) };
    }

    /// <summary>
    /// Zeichnen (nur Wachsen/Stehen haben Punkte). TD_Lightning vs dreht den flachen Streifen um Y zur Kamera:
    /// x' = x·cos θ, z' = −x·sin θ, θ = atan2(Blick.x, Blick.z) (CamDir_ObjSpace; Modell ohne Drehung → Weltblick).
    /// </summary>
    public void Draw(IRenderer r, Vector3 rootOffset, Vector3 viewForward)
    {
        if (!_enabled || _iCount == 0 || _vCount == 0) return;
        if (_mat == null || _matOwner != r)
        {
            _mat?.Dispose();
            _mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Lightning, Name = "TD_Lightning" });
            _matOwner = r;
        }
        float th = MathF.Atan2(viewForward.X, viewForward.Z);
        float c = MathF.Cos(th), s = MathF.Sin(th);
        var rot = new Matrix4x4(c, 0f, -s, 0f, 0f, 1f, 0f, 0f, s, 0f, c, 0f, 0f, 0f, 0f, 1f);
        var model = rot * Matrix4x4.CreateScale(_scale) * Matrix4x4.CreateTranslation(_pos + rootOffset);
        r.DrawDynamic(_verts.AsSpan(0, _vCount), _idx.AsSpan(0, _iCount), _mat, model);
    }

    // ------------------------------------------------------------------ Wetter (0xa5d310)
    void Apply(Func<string, Vector4, Vector4> raw)
    {
        float cr = _color.X, k = cr * 0.25f;
        var amb = raw("sun_ambient", new Vector4(0.58f, 0.67f, 0.82f, 0));
        _ov["sun_ambient"] = amb + new Vector4(cr);
        float hl = raw("car_headlights_intensity", Vector4.Zero).X;
        _ov["car_headlights_intensity"] = new Vector4((1f - k) * hl + k * 0f, 0f, 0f, 0f);
        switch (_state)
        {
            case 0: ApplyFlicker(1f - _smooth); break;
            case 1: case 2: case 3: ApplyBolt(); break;
            case 4: ApplyFlicker(1f - System.Math.Clamp(_alpha + (1f - _alpha) * _smooth, 0f, 1f)); break;
        }
    }

    static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a * (1f - t) + b * t;   // 0x6a6a30

    /// <summary>0xa5af80: Blitzlicht auf die Szene, solange Alpha ≥ 0,7; danach immer Sonnen-Halos/Mond aus, HGg 1000.</summary>
    void ApplyBolt()
    {
        if (_alpha >= 0.7f)
        {
            // Lichtrichtung: vom Blitz-Kopf (Fußpunkt + D nach oben) zur Kamera, Höhe aus der festgehaltenen Sonne
            var top = _pos + new Vector3(0f, _scale, 0f);
            var d = _cam - top;
            float len = d.Length();
            var dir = new Vector3(d.X, len * _sDir.Y, d.Z);
            if (dir.LengthSquared() > 1e-12f) dir = Vector3.Normalize(dir);
            _ov["sundirection"] = new Vector4(dir, 0f);
            float t = (_alpha - 0.7f) / 0.3f;
            _ov["sun_shadow_opacity"] = new Vector4(t, 0, 0, 0);
            _ov["photoshop_softlight_opacity"] = new Vector4((1f - t) * _sSoft + t * 0.5f, 0, 0, 0);
            _ov["photoshop_overlay_opacity"] = new Vector4(_sOverlay * (1f - t) + t * 0.9f, 0, 0, 0);
            _ov["sun_diffuse"] = Lerp(new Vector4(0.5f, 0.5f, 0.5f, 1f), new Vector4(2f, 2f, 2f, 1f), t) + new Vector4(_color.Y);
            _ov["sun_specular"] = Lerp(_sSpec, new Vector4(1.6f, 1.6f, 1.6f, 1f), t);
            var c1 = Lerp(_sCloud, new Vector4(0f, 0f, 0f, 1f), t) + new Vector4(_color.Z);
            c1.W = MathF.Min(1f, c1.W);
            _ov["clouds_cloud3dcolor"] = c1;
            var c2 = Lerp(_sCloud2, new Vector4(0.7f, 0.7f, 0.7f, 1f), t) + new Vector4(_color.Z);
            c2.W = MathF.Min(1f, c2.W);
            _ov["clouds_cloud3dcolor2"] = c2;
            var fogB = new Vector4(_sFog.X * 0.85f, _sFog.Y * 0.85f, _sFog.Z * 0.85f, 0f);
            var fog = Lerp(_sFog, fogB, t); fog.W = 0f;   // 0x72acd0 (vec3, w = 0)
            _ov["fog_color"] = fog;
        }
        _halo = _lens = _core = Vector4.Zero;
        _hgg = 1000f; _moon = 0f;
        _ov["Skydome_HGg"] = new Vector4(_hgg, 0, 0, 0);
        _ov["sunflare_lensflare_color"] = _lens;
        _ov["sunflare_halo_color"] = _halo;
        _ov["sunflare_core_color"] = _core;
        _ov["Skydome_moonPercent"] = new Vector4(_moon, 0, 0, 0);
    }

    /// <summary>0xa5baf0: Flackern (f = 1 − geglättete Stärke). HGg/Mond/Halos kehren je Bild zu 25 % zu den festgehaltenen Werten zurück.</summary>
    void ApplyFlicker(float f)
    {
        _ov["photoshop_overlay_opacity"] = new Vector4(_sOverlay * f + (1f - f) * 0.9f, 0, 0, 0);
        _ov["clouds_cloud3dcolor"] = Lerp(new Vector4(0f, 0f, 0f, 1f), _sCloud, f);
        _ov["clouds_cloud3dcolor2"] = Lerp(new Vector4(0.9f, 0.9f, 0.9f, 1f), _sCloud2, f);
        _hgg = _sHgg * 0.25f + _hgg * 0.75f;
        _ov["Skydome_HGg"] = new Vector4(_hgg, 0, 0, 0);
        _moon = _sMoon * 0.25f + _moon * 0.75f;
        _ov["Skydome_moonPercent"] = new Vector4(_moon, 0, 0, 0);
        _lens = Lerp(_lens, _sLens, 0.25f);
        _ov["sunflare_lensflare_color"] = _lens;
        _core = Lerp(_core, _sCore, 0.25f);
        _ov["sunflare_core_color"] = _core;
        _halo = Lerp(_halo, _sHalo, 0.25f);
        _ov["sunflare_halo_color"] = _halo;
    }

    public void Dispose()
    {
        _mat?.Dispose(); _mat = null; _matOwner = null;
    }
}
