using Paradise.Core.Math;
using Paradise.Core.Render;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Fahrzeuglichter: Lichtgläser (TEXLIGHT + Funktionsmaske lgt_g im Shader TDU2/Car: Licht, Bremse, Rückfahr, Blinker)
    /// und Verkehrsauto-Lampen. Teil "Lichtgläser"/"Verkehrsauto-Lampen".
    /// Unity-Lichter (Scheinwerfer-Spots an FA_L, Brems-/Rückfahr-Punktlichter an FA_B/FA_R) als Engine-Lampen
    /// (<see cref="CollectLamps"/>), Flares (TDU2/Flare) an den FA_-Ankern (<see cref="RenderFlares"/>).
    /// Materialien werden nur bei geänderten Werten neu hochgeladen
    /// </summary>
    public sealed class VehicleLights
    {
        public enum Kind { Head, Brake, Reverse, Tail, Indicator }

        public Vehicle? Vehicle;
        public int Headlights;
        public static bool OriginalControl;
        public bool IndicatorLeft;
        public bool IndicatorRight;
        public bool Hazard;

        public bool External;
        public bool ExtBrake;
        public bool ExtReverse;
        public bool Flash;

        public float BlinkHz = 1.5f;
        public bool UnityLights = true;
        public bool RearPointLights = false;   // aus: die Punktlichter malten rote/weiße Flecken auf die eigene Karosserie
        public float HeadlightIntensity = 2f;
        /// <summary>Reichweite der zusätzlichen hinteren Punktlichter.</summary>
        public float RearLightRange = 1.5f;
        /// <summary>Helligkeit der zusätzlichen hinteren Punktlichter.</summary>
        public float RearLightIntensity = 1f;

        readonly List<Vector3> _headPos = new List<Vector3>();
        readonly Vector3[] _brakePos = new Vector3[2], _reversePos = new Vector3[2];
        float _brakeOn; bool _reverseOn; int _hlOn;
        public float Brightness = 1.5f;   // Engine ohne Bloom: Flares kräftiger (Unity 0,3)
        public float LensBrightness = 1f;

        readonly List<(CarMaterial mat, int pos)> _lenses = new List<(CarMaterial, int)>();
        /// <summary>Verkehrsauto-Lampen (Knotenname FA_xx -> Funktion), eigene Materialinstanz je Lampe.</summary>
        readonly List<CarModel.TrafficLamp> _trafficLamps = new List<CarModel.TrafficLamp>();
        readonly List<CarMaterial> _dash = new List<CarMaterial>();
        readonly HashSet<CarMaterial> _changed = new HashSet<CarMaterial>();
        CarModel? _model;

        float _blinkT;
        public float FlareSize = 0.22f;
        sealed class Flare { public Kind Kind; public int Side; public Vector3 Local; public Vector4 Color; }
        readonly List<Flare> _flares = new List<Flare>();
        float _head, _tail, _brake; bool _reverse, _left, _right;
        IGpuMaterial? _flareMat; IGpuTexture? _flareTex;
        ObjectVertex[] _fv = System.Array.Empty<ObjectVertex>(); uint[] _ft = System.Array.Empty<uint>();

        public bool BlinkOn => OriginalControl ? (_blinkT % 1f) < 0.6f : (_blinkT * BlinkHz) % 1f < 0.5f;

        public void Setup(Vehicle vehicle, CarModel? model)
        {
            Vehicle = vehicle;
            _model = model;
            if (model == null) return;

            _lenses.Clear();
            _dash.Clear();
            _trafficLamps.Clear();
            _trafficLamps.AddRange(model.TrafficLamps);
            _dash.AddRange(model.DashMaterials);

            // Lichtgläser erkennen.
            foreach (var m in model.LightMaterials)
            {
                if (m == null) continue;
                if (OriginalControl && !IsOriginalLightName(m.Name))
                {
                    // exe FUN_00c5ee20: nur die 9 Namen der Tabelle 0x00f7e700 werden als Licht angesteuert. Alle anderen Materialien
                    // mit Leucht-Ebene (Polizei-Lichtbalken "Gyro", Logos, Zierteile) leuchten nicht mit dem Fahrlicht.
                    // Lichtbalken: nur bei eingeschaltetem Blaulicht (FUN_00c5a9a0, noch nicht portiert) -> aus.
                    m.Desc.LgtLights = 0f; m.Desc.LgtBrake = 0f; m.Desc.LgtReverse = 0f; m.Desc.LgtBlink = 0f; m.Desc.Emission = 0f;
                    _changed.Add(m);
                    continue;
                }
                // Original-Namen (Tabelle 0x00f7e700): LGT_FL, LGT_RL, LGT_RL_SK, LGT_FR, LGT_RR, LGT_RR_SK, LGT_R_S, LGT_L, LGT_R
                // (_SK = Rückleuchten am Verdeck-Knoten, gleiche Funktion wie ohne Zusatz)
                string n = m.Name.ToUpperInvariant();
                if (n.EndsWith("_SK")) n = n[..^3]; else if (n.EndsWith("_S")) n = n[..^2];
                int side = n.EndsWith("FL") || n.EndsWith("RL") || n.EndsWith("_L") ? 1 : n.EndsWith("FR") || n.EndsWith("RR") || n.EndsWith("_R") ? 2 : 0;
                int pos = n.EndsWith("FL") || n.EndsWith("FR") ? 0 : n.EndsWith("RL") || n.EndsWith("RR") ? 1 : 2;
                _lenses.Add((m, pos | (side << 4)));
                // Original: emis = TEXLIGHT · dot(Maske · GLOSSFCT, 1) – alle Gewichte 1 (ps96)
                if (m.Desc.UseMask > 0.5f && OriginalControl) { m.Desc.LightIntensity = Vector4.One; _changed.Add(m); }
                // Engine: ohne Bloom wirken die Gläser dunkel -> Funktionsintensitäten (Maskenweg) mit LensBrightness skalieren
                else if (m.Desc.UseMask > 0.5f && LensBrightness != 1f) { m.Desc.LightIntensity *= LensBrightness; _changed.Add(m); }
            }

            // FA_-Ankern (Scheinwerfer: FA_L ohne "1_C", höchstens zwei; hinten je Seite)
            _headPos.Clear();
            Vector3? Anchor(char kind, int side)
            {
                foreach (var kv in model.Nodes)
                {
                    string n = kv.Key.ToUpperInvariant();
                    if (!n.StartsWith("FA_") || n.Length < 4 || n[3] != kind || GetAnchorSide(n) != side) continue;
                    return U.Mirror(model.LocalToVisualParent(kv.Value).Translation);
                }
                return null;
            }
            foreach (var kv in model.Nodes)
            {
                string n = kv.Key.ToUpperInvariant();
                if (!n.StartsWith("FA_L") || n.EndsWith("1_C") || _headPos.Count >= 2) continue;
                _headPos.Add(U.Mirror(model.LocalToVisualParent(kv.Value).Translation));
            }
            if (_headPos.Count == 0) { _headPos.Add(new Vector3(-0.5f, 0.7f, 2.2f)); _headPos.Add(new Vector3(0.5f, 0.7f, 2.2f)); }
            // Flares an den FA_-Ankern.
            _flares.Clear();
            foreach (var kv in model.Nodes)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                string n = kv.Key.ToUpperInvariant();
                if (!n.StartsWith("FA_") || n.Length < 4) continue;
                Kind kind;
                switch (n[3])
                {
                    case 'L': kind = Kind.Head; break;
                    case 'B': kind = Kind.Brake; break;
                    case 'R': kind = Kind.Reverse; break;
                    case 'S': kind = Kind.Tail; break;
                    case 'W': kind = Kind.Indicator; break;
                    default: continue;
                }
                // Engine: hinten keine Lichtkugel (Brems-/Rück-/Rückfahrlicht leuchten über die Gläser) – außer bei Verkehrsautos
                // (External): deren Rückleuchten sind nur Lampenflächen ohne Glas, nachts sonst erst spät zu sehen
                if (kind is Kind.Brake or Kind.Tail or Kind.Reverse && !External) continue;
                _flares.Add(new Flare { Kind = kind, Side = GetAnchorSide(n), Local = U.Mirror(model.LocalToVisualParent(kv.Value).Translation), Color = FlareColor(kind) });
            }

            _brakePos[0] = (Anchor('B', 1) ?? new Vector3(-0.7f, 0.7f, -2.2f)) + new Vector3(0.05f, 0, 0);
            _brakePos[1] = (Anchor('B', 2) ?? new Vector3(0.7f, 0.7f, -2.2f)) + new Vector3(0.05f, 0, 0);
            _reversePos[0] = (Anchor('R', 1) ?? new Vector3(-0.5f, 0.6f, -2.2f)) + new Vector3(0.05f, 0, 0);
            _reversePos[1] = (Anchor('R', 2) ?? new Vector3(0.5f, 0.6f, -2.2f)) + new Vector3(0.05f, 0, 0);
            // Verkehrsautos ohne FA_S-Anker: Schlusslicht-Schein an den Bremslicht-Ankern (bzw. Standardlage hinten)
            if (External && !_flares.Exists(f => f.Kind == Kind.Tail))
                for (int s = 0; s < 2; s++) _flares.Add(new Flare { Kind = Kind.Tail, Side = s + 1, Local = _brakePos[s], Color = FlareColor(Kind.Tail) });
        }

        static int GetAnchorSide(string n)
        {
            if (n.Contains("_FL") || n.Contains("_RL") || n.Contains("_LEFT") || n.EndsWith("_L") || n.Contains("_L_")) return 1;
            if (n.Contains("_FR") || n.Contains("_RR") || n.Contains("_RIGHT") || n.EndsWith("_R") || n.Contains("_R_")) return 2;
            return 0;
        }

        /// <summary>
        /// Scheinwerfer (Spot 70/140 m, 75°/50°, Intensität 2/2,8),
        /// Brems-/Rückfahr-Punktlichter (1,5 m). unityToRender = Fahrzeug-Unity-Raum -> Render-Raum.
        /// </summary>
        public void CollectLamps(List<LampLight> output, VehiclePose tr, Matrix4x4 unityToRender)
        {
            if (!UnityLights || Vehicle == null) return;
            var headColor = new Vector3(1f, 0.95f, 0.85f);
            if (_hlOn > 0)
            {
                // LookRotation(AngleAxis(hl == 2 ? 1 : 5, right) * fwd, up)
                var beam = U.Rot(U.AngleAxis(_hlOn == 2 ? 1f : 5f, tr.right), tr.forward);
                var beamR = Vector3.Normalize(Vector3.TransformNormal(beam, unityToRender));
                float range = _hlOn == 2 ? 140f : 70f, angle = _hlOn == 2 ? 50f : 75f;
                float intensity = HeadlightIntensity * (_hlOn == 2 ? 1.4f : 1f);
                foreach (var p in _headPos)
                    output.Add(new LampLight
                    {
                        Position = Vector3.Transform(tr.TransformPoint(p), unityToRender),
                        Range = range,
                        Color = headColor * intensity,
                        CosOuter = MathF.Cos(angle * 0.5f * Mathf.Deg2Rad),
                        CosInner = MathF.Cos(angle * 0.5f * 0.7f * Mathf.Deg2Rad),
                        Direction = beamR,
                    });
            }
            if (RearPointLights && _brakeOn > 0f)
                foreach (var p in _brakePos) output.Add(Point(Vector3.Transform(tr.TransformPoint(p), unityToRender), new Vector3(1f, 0.04f, 0.02f)));
            if (RearPointLights && _reverseOn)
                foreach (var p in _reversePos) output.Add(Point(Vector3.Transform(tr.TransformPoint(p), unityToRender), Vector3.One));
        }

        LampLight Point(Vector3 pos, Vector3 color) => new LampLight
        {
            Position = pos,
            Range = RearLightRange,
            Color = color * RearLightIntensity,
            CosOuter = -3f,
            CosInner = -2f,
            Direction = Vec.Down,
        };

        public void Update(float dt)
        {
            _blinkT += dt;
            if (!IndicatorLeft && !IndicatorRight && !Hazard) _blinkT = 0f;
            if (Vehicle == null && !External) return;

            float brake = External ? (ExtBrake ? 1f : 0f) : (Mathf.Clamp01(Vehicle!.Brake) > 0.05f ? 1f : 0f);
            bool reverse = External ? ExtReverse : Vehicle!.Gear < 0;
            int hl = Flash ? 2 : Headlights;
            float tail = Headlights > 0 ? 1f : 0f;
            float head = hl == 0 ? 0f : hl == 1 ? 1f : 1.4f;
            bool blink = BlinkOn;
            bool left = (IndicatorLeft || Hazard) && blink;
            bool right = (IndicatorRight || Hazard) && blink;
            _brakeOn = brake; _reverseOn = reverse; _hlOn = hl;
            _head = head; _tail = tail; _brake = brake; _reverse = reverse; _left = left; _right = right;
            if (OriginalControl) UpdateOriginalState(dt);   // überschreibt Scheinwerfer/Positionslicht mit den Werten des Originals

            // Lichtgläser.
            foreach (var entry in _lenses)
            {
                var mat = entry.mat;
                int code = entry.pos;
                int pos = code & 0xF;
                int side = code >> 4;
                float ind = ((side == 1 && left) || (side == 2 && right) || (side == 0 && (left || right))) ? 1f : 0f;

                if (mat.Desc.UseMask > 0.5f && OriginalControl)
                {
                    // exe FUN_00c588d0: hinten GLOSSFCT = (Bremse, Rückfahr, Positionslicht, Blinker der Seite),
                    // vorn (0, 0, max(Positionslicht, Scheinwerfer), Blinker der Seite)
                    Set(mat, ref mat.Desc.LgtLights, pos == 0 ? Mathf.Max(_origPos, _origHead) : _origPos);
                    Set(mat, ref mat.Desc.LgtBrake, pos == 0 ? 0f : brake);
                    Set(mat, ref mat.Desc.LgtReverse, pos == 0 ? 0f : reverse ? 1f : 0f);
                    Set(mat, ref mat.Desc.LgtBlink, _origMode == 0 ? 0f : ind);
                    continue;
                }
                if (mat.Desc.UseMask > 0.5f)
                {
                    // GLOSSFCT wie das Original (car_lights_spec 2.2/4): hinten (Bremse, Rückfahr, Positionslicht, Blinker der Seite),
                    // vorn Bremse/Rückfahr 0 und Positionslicht = max(Positionslicht, Scheinwerfer); Kanäle sind 0/1-Schalter
                    float lights = pos == 0 ? Mathf.Max(tail, Mathf.Min(1f, head)) : tail;
                    Set(mat, ref mat.Desc.LgtLights, lights);
                    Set(mat, ref mat.Desc.LgtBrake, pos == 0 ? 0f : brake);
                    Set(mat, ref mat.Desc.LgtReverse, pos == 0 ? 0f : reverse ? 1f : 0f);
                    Set(mat, ref mat.Desc.LgtBlink, ind);
                    Set(mat, ref mat.Desc.Emission, 1.3f * LensBrightness);
                    continue;
                }

                float emission = pos == 0 ? head * 0.9f
                    : pos == 1 ? Mathf.Max(tail * 0.45f, Mathf.Max(brake * 1.3f, reverse ? 1f : 0f))
                    : Mathf.Max(head * 0.6f, brake * 1.3f);
                emission = Mathf.Max(emission, ind * 1.2f);
                Set(mat, ref mat.Desc.Emission, emission * LensBrightness);
            }

            // Verkehrsauto-Lampen: feste Leuchtfarbe je Funktion, Stärke je Zustand.
            foreach (var lamp in _trafficLamps)
            {
                var mat = lamp.Mat;
                if (mat == null) continue;
                float e;
                switch (lamp.Kind)
                {
                    case CarModel.LampKind.Head: e = head * 1.7f; break;
                    case CarModel.LampKind.Tail: e = Mathf.Max(tail * (External ? 1.5f : 0.9f), brake * 2.0f); break;
                    case CarModel.LampKind.Brake: e = brake * 2.0f; break;
                    case CarModel.LampKind.Reverse: e = reverse ? 1.9f : 0f; break;
                    case CarModel.LampKind.Indicator:
                        e = ((lamp.Side == 1 && left) || (lamp.Side == 2 && right) || (lamp.Side == 0 && (left || right))) ? 1.9f : 0f;
                        break;
                    default: e = (Flash || Hazard) && blink ? 2f : 0f; break;   // Rundumlicht
                }
                // Nicht leuchtende Funktionen ganz aus: sonst glimmt die Lampe leicht nach.
                Set(mat, ref mat.Desc.Emission, e > 0.01f ? e * LensBrightness : 0f);
            }

            // Instrumentenbeleuchtung.
            foreach (var m in _dash) if (m.IsCarShader) Set(m, ref m.Desc.SelfIllum, Headlights > 0 ? 0.4f : 0.12f);

            if (_changed.Count > 0 && _model != null)
            {
                foreach (var m in _changed) _model.ApplyMaterial(m);
                _changed.Clear();
            }
        }

        /// <summary>Namen der Lichtmaterialien des Originals (exe Tabelle 0x00f7e700, Vergleich über den Namens-Hash, also exakt).</summary>
        static readonly HashSet<string> OriginalLightNames = new(StringComparer.OrdinalIgnoreCase)
            { "LGT_FL", "LGT_RL", "LGT_RL_SK", "LGT_FR", "LGT_RR", "LGT_RR_SK", "LGT_R_S", "LGT_L", "LGT_R" };
        static bool IsOriginalLightName(string? name) => name != null && OriginalLightNames.Contains(name);

        // ------------------------------------------------------------------ Original-Lichtsteuerung (exe FUN_00c588d0)
        int _origMode = 1;
        float _origHead, _origPos, _forcedT;

        /// <summary>
        /// Werte des Originals je Bild (car_lights_spec §2.2):
        ///   flare = car_headlight_flare_intensity (0 wenn &lt; 0,05), poslight = car_poslight_intensity (0 wenn &lt; 0,05)
        ///   Scheinwerfer: Modus 2 -> flare + 1, Modus 1 -> flare, Modus 0 -> 0
        ///   Positionslicht: Modus 1 -> poslight, Modus 0 -> 0, Modus 2 -> flare sobald der Modus 0,2 s an ist
        /// Tunnel-Werte (tunnel_car_*) und Klappscheinwerfer sind noch nicht portiert.
        /// </summary>
        void UpdateOriginalState(float dt)
        {
            _origMode = System.Math.Clamp(Headlights, 0, 2);
            float flare = CarLighting.HeadlightFlare < 0.05f ? 0f : CarLighting.HeadlightFlare;
            float pl = CarLighting.PosLight < 0.05f ? 0f : CarLighting.PosLight;
            _forcedT = _origMode == 2 ? _forcedT + dt : 0f;
            _origHead = _origMode == 2 ? flare + 1f : _origMode == 1 ? flare : 0f;
            _origPos = _origMode == 1 ? pl : _origMode == 2 ? (_forcedT > 0.2f ? flare : 0f) : 0f;
            _hlOn = _origHead > 0.001f ? (_origMode == 2 ? 2 : 1) : 0;
            _head = System.MathF.Min(_origHead, 1.4f);
            _tail = _origPos;
        }

        static Vector4 FlareColor(Kind kind) => kind switch
        {
            Kind.Head => new Vector4(1f, 0.95f, 0.85f, 1f),
            Kind.Brake => new Vector4(1f, 0.06f, 0.03f, 1f),
            Kind.Tail => new Vector4(0.8f, 0.03f, 0.02f, 1f),
            Kind.Reverse => new Vector4(1f, 1f, 1f, 1f),
            _ => new Vector4(1f, 0.45f, 0.02f, 1f),
        };

        /// <summary> weicher Schein + Kern + waagrechter Streifen (64x64).</summary>
        static IGpuTexture FlareTexture(IRenderer r)
        {
            const int S = 64;
            var px = new byte[S * S * 4];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f, dy = (y + 0.5f) / S * 2f - 1f;
                    float rr = Mathf.Sqrt(dx * dx + dy * dy);
                    float glow = Mathf.Pow(Mathf.Clamp01(1f - rr), 2.2f);
                    float core = Mathf.Pow(Mathf.Clamp01(1f - rr * 3f), 1.5f);
                    float streak = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dy) * 14f), 2f) * Mathf.Clamp01(1f - Mathf.Abs(dx)) * 0.35f;
                    byte v = (byte)(Mathf.Clamp01(glow * 0.6f + core + streak) * 255f);
                    int o = (y * S + x) * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = v;
                }
            return r.CreateTexture(new TextureUpload { Width = S, Height = S, Format = GpuTextureFormat.Rgba8, Blocks = px, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "TDU2_Flare" });
        }

        /// <summary>
        /// Teil "Flares": Quad je FA_-Anker, 5 cm zur Kamera versetzt, LookRotation(quad - cam, cam.up),
        /// Größe FlareSize (x1,3 Scheinwerfer, x0,9 Rücklicht), Intensität k x Brightness.
        /// </summary>
        public void RenderFlares(IRenderer r, VehiclePose tr, Vector3 camPos, Vector3 camUp, Matrix4x4 unityToRender)
        {
            if (_flares.Count == 0) return;
            _flareTex ??= FlareTexture(r);
            _flareMat ??= r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Flare, Name = "Flare", Main = _flareTex });
            if (_fv.Length < _flares.Count * 4) { _fv = new ObjectVertex[_flares.Count * 4]; _ft = new uint[_flares.Count * 6]; }
            Vector3 fwd = tr.forward;
            int n = 0;
            foreach (var f in _flares)
            {
                float k = f.Kind switch
                {
                    Kind.Head => _head,
                    Kind.Brake => _brake,
                    Kind.Reverse => _reverse ? 1f : 0f,
                    Kind.Tail => _tail * 0.5f,
                    _ => ((f.Side == 1 && _left) || (f.Side == 2 && _right) || (f.Side == 0 && (_left || _right))) ? 1f : 0f,
                };
                Vector3 p = tr.TransformPoint(f.Local);
                if (k <= 0.001f) continue;
                Vector3 toCam = camPos - p;
                float dist = toCam.Length();
                toCam /= Mathf.Max(0.001f, dist);
                if (!External)
                {
                    float facing = f.Kind == Kind.Head ? Vector3.Dot(toCam, fwd) : f.Kind == Kind.Indicator ? 1f : -Vector3.Dot(toCam, fwd);
                    if (f.Kind != Kind.Indicator) k *= Mathf.Clamp01(facing * 1.5f + 0.15f);
                }
                if (dist < 0.5f) k = 0f;
                if (k <= 0.01f) continue;

                Vector3 q = p + toCam * 0.05f;
                Vector3 zf = Vector3.Normalize(q - camPos);
                Vector3 xr = Vector3.Cross(camUp, zf);
                if (xr.LengthSquared() < 1e-8f) continue;
                xr = Vector3.Normalize(xr);
                Vector3 yu = Vector3.Cross(zf, xr);
                float size = FlareSize * (External ? 1.8f : 1f) * (f.Kind == Kind.Head ? 1.3f : f.Kind == Kind.Tail ? 0.9f : 1f) * 0.5f;
                float inten = k * Brightness * (External ? 1.6f : 1f);
                uint c = ObjectVertex.PackColor((byte)(f.Color.X * 255), (byte)(f.Color.Y * 255), (byte)(f.Color.Z * 255), (byte)(Mathf.Clamp01(inten / 4f) * 255));
                Vector3 rx = xr * size, uy = yu * size;
                int b = n * 4;
                _fv[b] = new ObjectVertex { Position = q - rx - uy, Normal = Vec.Up, Color = c, Uv0 = new Vector2(0, 0) };
                _fv[b + 1] = new ObjectVertex { Position = q + rx - uy, Normal = Vec.Up, Color = c, Uv0 = new Vector2(1, 0) };
                _fv[b + 2] = new ObjectVertex { Position = q + rx + uy, Normal = Vec.Up, Color = c, Uv0 = new Vector2(1, 1) };
                _fv[b + 3] = new ObjectVertex { Position = q - rx + uy, Normal = Vec.Up, Color = c, Uv0 = new Vector2(0, 1) };
                uint u = (uint)b; int t = n * 6;
                _ft[t] = u; _ft[t + 1] = u + 2; _ft[t + 2] = u + 1; _ft[t + 3] = u; _ft[t + 4] = u + 3; _ft[t + 5] = u + 2;
                n++;
            }
            if (n > 0) r.DrawDynamic(_fv.AsSpan(0, n * 4), _ft.AsSpan(0, n * 6), _flareMat, unityToRender);
        }

        /// <summary>Flare-Material/Textur freigeben</summary>
        public void Dispose() { _flareMat?.Dispose(); _flareTex?.Dispose(); _flareMat = null; _flareTex = null; }

        void Set(CarMaterial m, ref float field, float value)
        {
            if (System.MathF.Abs(field - value) < 1e-4f) return;
            field = value;
            _changed.Add(m);
        }
    }
}
