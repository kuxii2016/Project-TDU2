using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Fx;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Partikeleffekte am Auto aus fx/vfx_car.bnk (DisplayVehicleParticles 0x73a800 im Original): Reifen je Untergrund
    /// (VFX_CarWheel_*, Namenstabelle 0x1661750), Auspuff (VFX_Exhaust_*). Werte je Untergrund und Geschwindigkeit aus
    /// fx/carfx.xmb (INIT/MIN/MED/MAX, _0 = km/h; exe 0x739a40: _1 Geschwindigkeit, _2 Emissionen/s, _3 Größe, _4 Lebensdauer,
    /// _5 FX+0x40, _6 Alpha). Gerechnet im Unity-Raum wie die übrigen Fahrzeugeffekte.
    /// Noch offen (CHECKLIST): Wahl des Grastyps, Wasser-Gischt, FX+0x40.
    /// </summary>
    public sealed class CarParticles : IDisposable
    {
        static FxLibrary? _lib;
        static IRenderer? _libOwner;
        static Dictionary<string, float[][]>? _carFx;

        readonly List<FxInstance> _live = new();
        FxInstance?[] _wheelFx = Array.Empty<FxInstance?>();
        string?[] _wheelName = Array.Empty<string?>();
        readonly List<(Matrix4x4 local, FxInstance? fx, string? name)> _exhaust = new();
        Vehicle? _v;
        readonly float[] _rpmHist = new float[4];   // exe Auspuff +0xb0..+0xbc (Ring), +0xc0 Index
        int _rpmIdx, _exState = 3;                   // +0x18 Zustand (Konstruktor 0x7391b0: 3 = aus)

        // Rauch-Schwellen der exe (Tweakables 0x1661978.., DisplayVehicleParticles 0x73ab1a): |Omega| > Min Omega und
        // (|Längsschlupf am Kontakt| > Min Contact Speed Longitudinal oder |Schräglauf| > Min Slip Angle); nur auf trockenem Boden.
        public float SmokeMinOmega = 0.1f, SmokeMinContactSpeedLong = 5f, SmokeMinSlipAngle = 0.5f;
        /// <summary>exe 0x73ab08: unter 30 km/h VFX_CarWheel_Burn (dichter, langer Rauch), darüber VFX_CarWheel_Asphalt.</summary>
        public float BurnMaxKmh = 30f;
        /// <summary>exe 0x73aaef: Nässe unter dem Rad &lt; 0,33 = trocken (darüber Wasser-Gischt statt Rauch).</summary>
        public float WetThreshold = 0.33f;
        /// <summary>exe 0x73ac57: Staub auf Erde/Gras ab 10,8 km/h oder Radumfangsgeschwindigkeit |Omega|·Radius &gt; 3 m/s.</summary>
        public float DustMinKmh = 10.8f, DustMinWheelSpeed = 3f;
        // Auspuff-Werte der exe (Konstruktor 0x740bc0): +0x00 15 km/h, +0x04 2,1 × Leerlauf, +0x0c Gas 0,08,
        // +0x10 Drehzahl-Beschleunigung −500000 U/min/s², +0x14 Flammenchance 0,6 (je Bild).
        public float ExhaustMaxKmh = 15f, ExhaustIdleFactor = 2.1f, ExhaustThrottle = 0.08f, FlameRpmAccel = 500000f, FlameChance = 0.6f;

        public static FxLibrary? Library(IRenderer r)
        {
            if (_lib == null || _libOwner != r)
            {
                _lib?.Dispose();
                _lib = FxLibrary.Load(r, Path.Combine("fx", "vfx_car.bnk"));
                _libOwner = r;
            }
            _carFx ??= LoadCarFx();
            return _lib;
        }

        static Dictionary<string, float[][]> LoadCarFx()
        {
            var d = new Dictionary<string, float[][]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Config.GameData.BnkDir;
                var path = dir != null ? Path.Combine(dir, "fx", "carfx.xmb") : null;
                if (path == null || !Files.GameFiles.Exists(path)) { Log.Warn("FX: carfx.xmb fehlt."); return d; }
                var root = XmbFile.Parse(Files.GameFiles.ReadAllBytes(path));
                foreach (var s in root.Children)
                {
                    var rows = new float[4][];
                    string[] keys = { "INIT", "MIN", "MED", "MAX" };
                    for (int k = 0; k < 4; k++)
                    {
                        var n = s.Get(keys[k]);
                        rows[k] = n != null ? n.Floats() : new float[7];
                        if (rows[k].Length < 7) System.Array.Resize(ref rows[k], 7);
                    }
                    d[s.Name] = rows;
                }
            }
            catch (Exception ex) { Log.Error("FX carfx.xmb: " + ex.Message); }
            return d;
        }

        public void Setup(Vehicle v, CarModel? model, IRenderer r)
        {
            _v = v;
            Library(r);
            int n = v.Wheels.Length;
            _wheelFx = new FxInstance?[n];
            _wheelName = new string?[n];
            _exhaust.Clear();
            if (model != null)
                for (int k = 0; k < 4; k++)
                {
                    // exe 0x73d747: EXH_FX0..3, sonst EXO_FX0..3
                    string name = "EXH_FX" + k;
                    var node = model.Find(name);
                    if (node == null) { name = "EXO_FX" + k; node = model.Find(name); }
                    if (node == null) continue;
                    var m = Matrix4x4.Identity;
                    for (var c = node; c != null; c = c.Parent) { m *= c.Local; if (c == model.Visual) break; }
                    _exhaust.Add((m, null, null));
                }
        }

        /// <summary>Werte aus carfx.xmb bei km/h (zwischen INIT/MIN/MED/MAX linear); null = unter der INIT-Schwelle.</summary>
        static FxParams? Params(string key, float kmh)
        {
            if (_carFx == null || !_carFx.TryGetValue(key, out var rows)) return FxParams.One;
            if (kmh < rows[0][0]) return null;
            float[] a = rows[3], b = rows[3];
            float t = 0f;
            for (int k = 0; k < 3; k++)
                if (kmh <= rows[k + 1][0]) { a = rows[k]; b = rows[k + 1]; float span = b[0] - a[0]; t = span > 0f ? (kmh - a[0]) / span : 1f; break; }
            float L(int i) => a[i] + (b[i] - a[i]) * t;
            // exe 0x739c7d: _1 → FX+0x30, _4 → +0x38, _3 → +0x34, _2 → +0x44, _5 → +0x40, _6 → +0x48 (Partikel je Emission bleibt 1)
            return new FxParams { Speed = L(1), EmisPerSec = L(2), Size = L(3), LifeTime = L(4), Alpha = L(6), PartPerEmis = 1f };
        }

        FxInstance? Start(string name)
        {
            var e = _lib?.Get(name);
            if (e == null) return null;
            var fx = new FxInstance(e);
            _live.Add(fx);
            return fx;
        }

        public void Update(float dt)
        {
            if (_v == null || _lib == null || dt <= 0f) return;
            var pose = _v.VisualPose;
            var rot = Matrix4x4.CreateFromQuaternion(pose.rotation);
            float kmh = _v.SpeedKmh;
            var ws = _v.Wheels;
            var active = new HashSet<FxInstance>();
            for (int i = 0; i < ws.Length && i < _wheelFx.Length; i++)
            {
                var w = ws[i];
                string? name = null, key = null;
                bool burn = false;
                if (w.Grounded)
                {
                    bool wet = World.WorldWeather.GroundWet > WetThreshold;
                    bool smoke = !wet && System.Math.Abs(w.Omega) > SmokeMinOmega
                        && (System.Math.Abs(w.LongSlipVel) > SmokeMinContactSpeedLong || System.Math.Abs(w.SlipAngle) > SmokeMinSlipAngle);
                    bool dust = kmh > DustMinKmh || System.Math.Abs(w.Omega) * w.Radius > DustMinWheelSpeed;
                    switch (w.Surface)
                    {
                        case 0:
                            if (smoke && kmh < BurnMaxKmh) { name = "VFX_CarWheel_Burn"; burn = true; }   // Index 0x1c, ohne carfx-Zeile
                            else if (smoke) { name = "VFX_CarWheel_Asphalt"; key = "ASPHALT"; }
                            break;
                        case 1: if (dust) { name = "VFX_CarWheel_Grass_01"; key = "GRASS_1"; } break;
                        case 2: if (dust) { name = "VFX_CarWheel_Dirt"; key = "DIRT"; } break;
                    }
                }
                FxParams? p = key != null ? Params(key, kmh) : burn ? FxParams.One : null;
                if (p == null) name = null;
                if (name != _wheelName[i]) { _wheelFx[i] = name != null ? Start(name) : null; _wheelName[i] = name; }
                var fx = _wheelFx[i];
                if (fx == null) continue;
                active.Add(fx);
                var m = rot * Matrix4x4.CreateTranslation(w.ContactPoint);
                fx.Update(dt, true, m, p!.Value, true);
            }

            // Auspuff-Zustand wie exe 0x73a2d0 (0 = Leerlauf 0x2e, 1 = Rauch 0x2c, 2 = Flamme 0x30, 3 = aus)
            float rpm = _v.Rpm;
            _rpmHist[_rpmIdx] = rpm;
            _rpmIdx = (_rpmIdx + 1) & 3;
            if (_exState == 2)
            {
                // bleibt, bis alle Flammen-Instanzen erloschen sind (0x73a56a), dann aus
                bool alive = false;
                foreach (var e in _exhaust) if (e.fx != null && !e.fx.Finished) alive = true;
                if (!alive) _exState = 3;
            }
            else if (kmh < ExhaustMaxKmh)
            {
                // 0x73a3c5: Drehzahl unter 2,1 × Leerlauf → Leerlaufrauch, sonst bei Gas > 0,08 Rauch, sonst unverändert
                float idle = System.Math.Max(500f, _v.Car.RpmMin);
                if (idle * ExhaustIdleFactor > rpm) _exState = 0;
                else if (_v.Throttle > ExhaustThrottle) _exState = 1;
            }
            else
            {
                // 0x73a42a: mittlere 2. Ableitung der Drehzahl aus den letzten 4 Bildern; Flamme bei < −500000 und Zufall > 1 − 0,6
                float inv = 1f / dt;
                float s0 = _rpmHist[_rpmIdx], s1 = _rpmHist[(_rpmIdx + 1) & 3], s2 = _rpmHist[(_rpmIdx + 2) & 3], s3 = _rpmHist[(_rpmIdx + 3) & 3];
                float d1 = (s1 - s0) * inv, d2 = (s2 - s1) * inv, d3 = (s3 - s2) * inv;
                float acc2 = ((d2 - d1) * inv + (d3 - d2) * inv) * 0.5f;
                _exState = acc2 < -FlameRpmAccel && Random.Shared.NextSingle() > 1f - FlameChance ? 2 : 3;
            }
            int state = _exState;
            string? exName = state switch { 0 => "VFX_Exhaust_PipeSmoke_Idle", 1 => "VFX_Exhaust_PipeSmoke", 2 => "VFX_Exhaust_PipeFlame", _ => null };
            for (int i = 0; i < _exhaust.Count; i++)
            {
                var (local, fx, cur) = _exhaust[i];
                if (exName != cur) { fx = exName != null ? Start(exName) : null; cur = exName; _exhaust[i] = (local, fx, cur); }
                if (fx == null) continue;
                active.Add(fx);
                // Modellraum → Unity-Raum: Lage gespiegelt (U.Mirror), wie VehicleManager.Render
                Matrix4x4.Decompose(local, out _, out var q, out var t);
                var m = Matrix4x4.CreateFromQuaternion(U.Mirror(q)) * rot * Matrix4x4.CreateTranslation(pose.TransformPoint(U.Mirror(t)));
                fx.Update(dt, true, m, FxParams.One, true);
            }

            // abgelöste Instanzen ausklingen lassen
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var fx = _live[i];
                if (active.Contains(fx)) continue;
                fx.Update(dt, false, Matrix4x4.Identity, FxParams.One, true);
                if (!fx.Alive) _live.RemoveAt(i);
            }
        }

        public void Shift(Vector3 d) { foreach (var fx in _live) fx.Shift(d); }

        readonly List<(float d, FxInstance fx)> _sorted = new();
        float _diagAt;

        public void Render(IRenderer r, Vector3 camRight, Vector3 camUp, Vector3 camPos, Matrix4x4 unityToRender)
        {
            var fwd = Vector3.Cross(camRight, camUp);   // Unity: rechts × oben = vorne
            _sorted.Clear();
            foreach (var fx in _live) _sorted.Add((fx.Depth(camPos, fwd), fx));
            _sorted.Sort((a, b) => b.d.CompareTo(a.d));   // fernste zuerst
            foreach (var (_, fx) in _sorted) fx.Render(r, camRight, camUp, camPos, fwd, unityToRender);
            // Diagnose alle 5 s: laufende Effekte und Partikelzahl
            float now = Environment.TickCount64 / 1000f;
            if (now >= _diagAt && _live.Count > 0)
            {
                _diagAt = now + 5f;
            }
        }

        public void Dispose() { _live.Clear(); _exhaust.Clear(); }
    }
}