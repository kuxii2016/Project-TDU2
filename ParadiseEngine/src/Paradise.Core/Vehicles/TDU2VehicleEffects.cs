using Paradise.Core.Math;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Bodenschatten (wie im Spiel: Schattentextur "shadow.2db" aus common_car.bnk unter dem Auto, auf den Boden projiziert),
    /// Reifenspuren (Brems-/Driftspuren bei Längs- oder Querschlupf; Ringpuffer, verblassen nicht), Staub auf Erde/Gras und
    /// glühende Bremsscheiben (<see cref="TDU2BrakeGlow"/>). Portiert aus TDU2VehicleEffects (Unity). Alles im Unity-Raum
    /// (Fahrzeug-lokal zum Physik-Ursprung), gezeichnet mit VehicleManager.UnityToRender.
    /// </summary>
    public sealed class TDU2VehicleEffects : IDisposable
    {
        public TDU2Vehicle Vehicle = null!;
        public bool GroundShadow = true;
        public float ShadowStrength = 0.75f;
        // Reifenspuren
        public bool Skidmarks = true;
        public int MaxSkidQuads = 3000;
        public float SkidLongSlip = 3.5f;     // m/s Längsschlupf (Durchdrehen/Blockieren)
        public float SkidLatSlip = 2.2f;      // m/s Querschlupf (Driften)
        public float SkidOpacity = 0.55f;
        /// <summary>Fahrspuren auf Erde/Gras (Farbe der eingedrückten Erde)</summary>
        public (byte r, byte g, byte b) OffroadColor = (70, 52, 34);
        public float OffroadOpacity = 0.6f;
        // Staub (Erde/Feldwege)
        public bool Dust = true;
        public Vector4 DustColor = new Vector4(0.62f, 0.52f, 0.40f, 0.35f);
        DustCloud? _dust;
        bool[] _lastOff = Array.Empty<bool>();
        /// <summary>Glühende Bremsscheiben (null = Auto ohne Scheibenmaterial).</summary>
        public TDU2BrakeGlow? Glow { get; private set; }
        /// <summary>Darstellungsabsenkung des Geländes (VehicleManager.TerrainVisualLower): Spuren/Schatten auf dem Gelände genauso tief.</summary>
        public static float TerrainDrop;
        static float Drop(Physics.StaticCollider? c) => VehicleManager.IsTerrain(c) ? TerrainDrop : 0f;
        static SkidBuffer? _skid;   // gemeinsam für alle Fahrzeuge (bleibt beim Autowechsel liegen) – Asphalt (tarmac.2db)
        static SkidBuffer? _skidGround;   // Erde/Gras (ground.2db)
        static IRenderer? _skidOwner;
        Vector3[] _lastPos = Array.Empty<Vector3>();
        bool[] _lastValid = Array.Empty<bool>();
        int[] _lastIndex = Array.Empty<int>();
        Vector3 _camRight = Vec.Right, _camUp = Vec.Up;

        readonly IRenderer _r;
        IGpuMesh? _quad;
        MaterialDesc? _shadowDesc;
        IGpuMaterial? _shadowMat;
        IGpuTexture? _blob;
        Vector3 _shadowSize;
        bool _shadowActive;
        Matrix4x4 _shadowModel;   // Unity-Raum
        float _appliedStrength = -1f;

        public TDU2VehicleEffects(IRenderer r) { _r = r; }

        public void Setup(TDU2Vehicle v, CarModel? model, CarAssets? assets)
        {
            Vehicle = v;
            var size = model != null ? model.BodySize : new Vector3(1.9f, 1.2f, 4.5f);
            _shadowSize = new Vector3(size.X * 1.25f, 1f, size.Z * 1.12f);
            if (GroundShadow)
            {
                var tex = assets?.FindTexture("shadow");
                if (tex == null) tex = _blob = BlobTexture(_r);
                _shadowDesc = new MaterialDesc { Kind = MaterialKind.CarShadow, Name = "CarGroundShadow", Main = tex, Strength = ShadowStrength };
                _shadowMat = _r.CreateMaterial(_shadowDesc);
                _quad = Quad(_r);
            }
            int n = v != null ? v.Wheels.Length : 0;
            _lastPos = new Vector3[n]; _lastValid = new bool[n]; _lastIndex = new int[n]; _lastOff = new bool[n];
            if (Dust) _dust = new DustCloud(_r, 400, DustColor, _blob ?? (_blob = BlobTexture(_r)));
            Glow = TDU2BrakeGlow.Create(model, _r);
            if (Skidmarks && (_skid == null || _skidOwner != _r))
            {
                // Original-Masken aus Euro/Bnk/fx/skidmarks.bnk (TD_TireSkid: gTarmacSampler / gGroundSampler, Maske = Rot-Kanal)
                _skid = new SkidBuffer(_r, MaxSkidQuads, SkidMask(_r, "tarmac.2db"));
                _skidGround = new SkidBuffer(_r, MaxSkidQuads, SkidMask(_r, "ground.2db"));
                _skidOwner = _r;
            }
        }

        /// <summary>
        /// Unity AddStaticShadow (stehende Autos, Interieur): Schattenfläche mit der Bank-Textur "shadow" bzw. dem Blob,
        /// _Strength 0,6. Rückgabe: Quad (1 x 1 m), Material, ggf. eigene Blob-Textur (mit freigeben).
        /// </summary>
        public static (IGpuMesh mesh, IGpuMaterial mat, IGpuTexture? blob) CreateStaticShadow(IRenderer r, CarAssets? assets, float strength = 0.6f)
        {
            var tex = assets?.FindTexture("shadow");
            IGpuTexture? blob = null;
            if (tex == null) tex = blob = BlobTexture(r);
            var mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.CarShadow, Name = "CarGroundShadow (stehend)", Main = tex, Strength = strength });
            return (Quad(r), mat, blob);
        }

        /// <summary>Unity Mesh "ShadowQuad": 1 x 1 m in der XZ-Ebene.</summary>
        static IGpuMesh Quad(IRenderer r)
        {
            var p = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            var uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            var v = new ObjectVertex[4];
            for (int i = 0; i < 4; i++) v[i] = new ObjectVertex { Position = p[i], Normal = Vec.Up, Color = 0xFFFFFFFFu, Uv0 = uv[i], Uv1 = uv[i], Uv2 = uv[i] };
            return r.CreateMesh(v, new uint[] { 0, 1, 2, 0, 2, 3 });
        }

        static IGpuTexture BlobTexture(IRenderer r)
        {
            const int S = 64;
            var px = new byte[S * S * 4];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = Mathf.Abs((x + 0.5f) / S * 2f - 1f), dy = Mathf.Abs((y + 0.5f) / S * 2f - 1f);
                    float d = Mathf.Pow(Mathf.Pow(dx, 4f) + Mathf.Pow(dy, 4f), 0.25f);   // abgerundetes Rechteck
                    float t = Mathf.Clamp01((d - 0.55f) / 0.45f); t = t * t * (3f - 2f * t);
                    byte b = (byte)(Mathf.Clamp01(1f - t) * 255);
                    int o = (y * S + x) * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = b;
                }
            return r.CreateTexture(new TextureUpload { Width = S, Height = S, Format = GpuTextureFormat.Rgba8, Blocks = px, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "CarBlob" });
        }

        /// <summary>Floating Origin: Weltverschiebung (Unity-Raum) nachführen.</summary>
        public void Shift(Vector3 d)
        {
            _shadowModel.Translation -= d;
            for (int i = 0; i < _lastPos.Length; i++) _lastPos[i] -= d;
            _dust?.Shift(d);
        }

        /// <summary>Unity LateUpdate (Lage aus <see cref="TDU2Vehicle.VisualPose"/>); Kamera rechts/oben im Unity-Raum für den Staub.</summary>
        public void LateUpdate(float dt, Vector3 camRight, Vector3 camUp)
        {
            if (Vehicle == null) return;
            _camRight = camRight; _camUp = camUp;
            UpdateShadow();
            UpdateSkids();
            UpdateDust(dt);
            Glow?.Simulate(Vehicle, dt);
        }

        // ------------------------------------------------------------------ Staub
        /// <summary>
        /// Staubwolke hinter den Rädern auf Erde/Gras. Eigenes kleines Partikelsystem (Kamera-Billboards in einem Mesh).
        /// </summary>
        sealed class DustCloud : IDisposable
        {
            struct P { public Vector3 Pos, Vel; public float Age, Life, Size; }
            readonly P[] _p; int _count;
            readonly ObjectVertex[] _v; readonly uint[] _t;
            readonly Vector4 _col;
            readonly IGpuMaterial _mat;
            readonly Random _rnd = new Random();

            public DustCloud(IRenderer r, int max, Vector4 col, IGpuTexture tex)
            {
                _col = col;
                _p = new P[max];
                _v = new ObjectVertex[max * 4]; _t = new uint[max * 6];
                for (int i = 0; i < max; i++)
                {
                    uint b = (uint)(i * 4);
                    _t[i * 6] = b; _t[i * 6 + 1] = b + 1; _t[i * 6 + 2] = b + 2; _t[i * 6 + 3] = b; _t[i * 6 + 4] = b + 2; _t[i * 6 + 5] = b + 3;
                }
                _mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Particle, Name = "Dust", Main = tex });
            }

            float Range(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);
            Vector3 InsideUnitSphere()
            {
                while (true)
                {
                    var v = new Vector3(Range(-1, 1), Range(-1, 1), Range(-1, 1));
                    if (v.LengthSquared() <= 1f) return v;
                }
            }

            public void Emit(Vector3 pos, Vector3 vel, int n)
            {
                for (int k = 0; k < n; k++)
                {
                    if (_count >= _p.Length) return;
                    _p[_count++] = new P
                    {
                        Pos = pos + InsideUnitSphere() * 0.3f,
                        Vel = vel + InsideUnitSphere() * 0.8f,
                        Life = Range(1.2f, 2.2f),
                        Size = Range(0.5f, 1.0f),   // Engine: kleiner (Unity 1,2–2,4 m wirkte zu groß)
                    };
                }
            }

            public float Random01() => (float)_rnd.NextDouble();

            public void Shift(Vector3 d) { for (int i = 0; i < _count; i++) _p[i].Pos -= d; }

            public void Update(float dt)
            {
                int n = 0;
                for (int i = 0; i < _count; i++)
                {
                    var p = _p[i];
                    p.Age += dt;
                    if (p.Age >= p.Life) continue;
                    p.Vel *= 1f / (1f + 1.5f * dt);
                    p.Vel.Y += 0.15f * dt;
                    p.Pos += p.Vel * dt;
                    _p[n++] = p;
                }
                _count = n;
            }

            public void Render(IRenderer r, Vector3 right, Vector3 up, Matrix4x4 unityToRender)
            {
                if (_count == 0) return;
                for (int i = 0; i < _count; i++)
                {
                    var p = _p[i];
                    int b = i * 4;
                    float t = p.Age / p.Life;
                    float a = Mathf.Clamp01(t / 0.15f) * (1f - t);
                    float s = p.Size * Mathf.Lerp(0.6f, 1.8f, t) * 0.5f;
                    Vector3 rr = right * s, u = up * s;
                    uint c = ObjectVertex.PackColor((byte)(_col.X * 255), (byte)(_col.Y * 255), (byte)(_col.Z * 255), (byte)(Mathf.Clamp01(_col.W * a) * 255));
                    _v[b] = new ObjectVertex { Position = p.Pos - rr - u, Normal = Vec.Up, Color = c, Uv0 = new Vector2(0, 0) };
                    _v[b + 1] = new ObjectVertex { Position = p.Pos - rr + u, Normal = Vec.Up, Color = c, Uv0 = new Vector2(0, 1) };
                    _v[b + 2] = new ObjectVertex { Position = p.Pos + rr + u, Normal = Vec.Up, Color = c, Uv0 = new Vector2(1, 1) };
                    _v[b + 3] = new ObjectVertex { Position = p.Pos + rr - u, Normal = Vec.Up, Color = c, Uv0 = new Vector2(1, 0) };
                }
                r.DrawDynamic(_v.AsSpan(0, _count * 4), _t.AsSpan(0, _count * 6), _mat, unityToRender);
            }

            public void Dispose() => _mat.Dispose();
        }

        float _dustAcc;
        void UpdateDust(float frameDt)
        {
            if (_dust == null || Vehicle == null) return;
            float kmh = Vehicle.SpeedKmh;
            var ws = Vehicle.Wheels;
            _dustAcc += frameDt;
            float dt = _dustAcc;
            if (_dustAcc >= 0.04f) { _dustAcc = 0f; } else dt = 0f;   // Ausstoß ~25x je Sekunde, Bewegung jedes Bild
            // Engine: nur beim Fahren (im Stand erzeugte das Restrutschen der Räder Staub)
            if (Mathf.Abs(kmh) < 5f) dt = 0f;
            for (int i = 0; i < ws.Length && dt > 0f; i++)
            {
                var w = ws[i];
                if (!w.Grounded || w.Surface == 0 || w.Front && ws.Length > 2 && kmh > 1f && !w.Driven) continue;
                float slip = Mathf.Abs(w.LongSlipVel) + Mathf.Abs(w.LatSlipVel);
                float rate = Mathf.Clamp01((kmh - 10f) / 70f) * 14f + slip * 3f;   // Partikel je Sekunde
                if (w.Surface == 1) rate *= 0.35f;                                  // Gras staubt kaum
                int count = Mathf.FloorToInt(rate * dt + _dust.Random01());
                if (count <= 0) continue;
                _dust.Emit(w.ContactPoint + Vec.Up * (0.25f - Drop(w.GroundCollider)),
                    -Vehicle.VisualPose.forward * Mathf.Min(kmh / 3.6f * 0.15f, 3f) + Vec.Up * 0.4f, count);
            }
            _dust.Update(frameDt);
        }

        // ------------------------------------------------------------------ Reifenspuren
        void UpdateSkids()
        {
            if (_skid == null || _skidGround == null || Vehicle == null) return;
            var ws = Vehicle.Wheels;
            for (int i = 0; i < ws.Length && i < _lastPos.Length; i++)
            {
                var w = ws[i];
                float slip = Mathf.Max(Mathf.Abs(w.LongSlipVel) / SkidLongSlip, Mathf.Abs(w.LatSlipVel) / SkidLatSlip);
                bool off = w.Surface != 0;
                // Asphalt: schwarze Gummispuren nur beim Rutschen. Erde/Gras: helle Fahrspur schon beim Rollen (Reifen drücken
                // den Boden ein), kräftiger beim Rutschen – kein Gummiabrieb.
                bool mark = w.Grounded && Vehicle.SpeedKmh > 3f && (off ? Vehicle.SpeedKmh > 8f : slip > 1f);
                if (!mark) { _lastValid[i] = false; continue; }
                if (_lastValid[i] && _lastOff[i] != off) _lastValid[i] = false;   // Wechsel Asphalt/Erde: neue Spur
                _lastOff[i] = off;
                Vector3 p = w.ContactPoint + w.ContactNormal * 0.025f - Vec.Up * Drop(w.GroundCollider);
                // Der Radkontakt stammt aus dem letzten Physikschritt, das Rad wird interpoliert gezeichnet (bis zu einem Schritt zurück:
                // bei 280 km/h ~0,7 m, [DIAG Skid]). Spurpunkt um denselben Versatz mitnehmen, damit die Spur unter dem gezeichneten Rad liegt.
                var lag = Vehicle.VisualPose.TransformPoint(w.Anchor) - Vehicle.transform.TransformPoint(w.Anchor);
                lag.Y = 0f;
                p += lag;
                float a = off ? Mathf.Clamp01(0.18f + Mathf.Max(0f, slip - 0.5f) * 0.35f) * OffroadOpacity
                              : Mathf.Clamp01((slip - 1f) * 0.8f + 0.25f) * SkidOpacity;
                if (_lastValid[i] && (p - _lastPos[i]).LengthSquared() < 0.09f) continue;   // Segmente ab ~30 cm
                if (_lastValid[i] && (p - _lastPos[i]).LengthSquared() > 25f) _lastValid[i] = false;   // Sprung (Reset/Teleport/Verschiebung): neue Spur statt Streifen
                // Anfang einer Spur: Richtung aus der Fahrtrichtung (der alte Punkt kann weit weg liegen -> verdrehtes erstes Stück)
                var dir = _lastValid[i] ? p - _lastPos[i] : Vehicle.VisualPose.forward;
                var right = Vector3.Cross(w.ContactNormal, U.Normalized(dir));
                var col = off ? OffroadColor : ((byte)0, (byte)0, (byte)0);
                _lastIndex[i] = (off ? _skidGround : _skid).Add(p, w.ContactNormal, right, w.Width * (off ? 1.05f : 0.9f), a, _lastValid[i] ? _lastIndex[i] : -1, col);
                SkidDiag(i, w, p);
                _lastPos[i] = p; _lastValid[i] = true;
            }
        }

        // Diagnose ([DIAG Skid] im Log, alle 2 s je Rad): Lage des Spurpunkts zur gezeichneten Radposition (Unity-Raum, Fahrzeugachsen)
        float _skidDiagAt;
        void SkidDiag(int i, TDU2Vehicle.Wheel w, Vector3 p)
        {
            float now = Environment.TickCount64 / 1000f;
            if (i == 0) { if (now < _skidDiagAt) return; _skidDiagAt = now + 2f; }
            else if (now > _skidDiagAt - 1.9f) return;   // gleiche Runde wie Rad 0
            var vp = Vehicle!.VisualPose;
            var hub = vp.TransformPoint(w.Anchor);
            var d = p - hub;
            float side = Vector3.Dot(d, vp.right), along = Vector3.Dot(d, vp.forward), up = Vector3.Dot(d, Vec.Up);
            Diagnostics.Log.Info($"[DIAG Skid] Rad {i}: Spurpunkt-Rad seitlich {side:+0.00;-0.00} m, längs {along:+0.00;-0.00} m, Höhe {up:+0.00;-0.00} m, " +
                                 $"{Vehicle.SpeedKmh:0} km/h, Rad Unity ({hub.X:0.0},{hub.Y:0.0},{hub.Z:0.0}), Untergrund {(w.Surface != 0 ? "Erde" : "Asphalt")}, Gelände-Absenkung {Drop(w.GroundCollider):0.00}");
        }

        /// <summary>Spur-Maske aus Euro/Bnk/fx/skidmarks.bnk (tarmac.2db 128x32 / ground.2db 256x32, DXT1, Maske im Rot-Kanal).</summary>
        static IGpuTexture? SkidMask(IRenderer r, string name)
        {
            try
            {
                var dir = Config.GameData.BnkDir;
                if (dir == null) return null;
                var path = Path.Combine(dir, "fx", "skidmarks.bnk");
                if (!Files.GameFiles.Exists(path)) { Diagnostics.Log.Warn("Reifenspuren: " + path + " fehlt – Ersatztextur."); return null; }
                var bank = Formats.BnkArchive.Open(path);
                var e = bank.Find(name);
                var def = e != null ? Formats.TextureDef.Parse(bank.Read(e), name) : null;
                if (def == null) { Diagnostics.Log.Warn($"Reifenspuren: {name} nicht in skidmarks.bnk – Ersatztextur."); return null; }
                return World.TextureInfo.Create(r, def, TextureWrap.Repeat, TextureWrap.Clamp, linear: true);
            }
            catch (Exception ex) { Diagnostics.Log.Warn("Reifenspuren: " + ex.Message); return null; }
        }

        /// <summary>Ringpuffer aus Quads; jedes Quad verbindet den vorherigen mit dem aktuellen Spurpunkt.</summary>
        sealed class SkidBuffer
        {
            readonly int _max;
            readonly ObjectVertex[] _v; readonly uint[] _t;
            readonly Vector3[] _endL, _endR;
            readonly IGpuMaterial _mat;
            int _next, _used;

            readonly float _tileAspect;   // Länge einer Texturkachel entlang der Spur in Spurbreiten (Breite/Höhe der Maske)

            public SkidBuffer(IRenderer r, int max, IGpuTexture? mask)
            {
                mask ??= SkidTexture(r);
                _tileAspect = mask.Height > 0 ? (float)mask.Width / mask.Height : 2f;
                _max = max;
                _v = new ObjectVertex[max * 4]; _t = new uint[max * 6];
                _endL = new Vector3[max]; _endR = new Vector3[max];
                for (int q = 0; q < max; q++)
                {
                    uint b = (uint)(q * 4);
                    _t[q * 6] = b; _t[q * 6 + 1] = b + 1; _t[q * 6 + 2] = b + 2; _t[q * 6 + 3] = b; _t[q * 6 + 4] = b + 2; _t[q * 6 + 5] = b + 3;
                }
                _mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Skidmark, Name = "Skidmarks", Main = mask });
            }

            static IGpuTexture SkidTexture(IRenderer r)
            {
                // Ersatz, falls skidmarks.bnk fehlt: Maske im Rot-Kanal wie die Original-Texturen, U entlang, V quer zur Spur
                const int W = 64, H = 32;
                var px = new byte[W * H * 4];
                var rnd = new Random(7);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float v = (y + 0.5f) / H;
                        float edge = Mathf.Clamp01(Mathf.Min(v, 1f - v) * 6f);
                        float groove = 0.75f + 0.25f * Mathf.Sin(v * 40f);
                        float noise = 0.8f + 0.2f * (float)rnd.NextDouble();
                        int o = (y * W + x) * 4;
                        byte m = (byte)(edge * groove * noise * 255);
                        px[o] = m; px[o + 1] = m; px[o + 2] = m; px[o + 3] = 255;
                    }
                return r.CreateTexture(new TextureUpload { Width = W, Height = H, Format = GpuTextureFormat.Rgba8, Blocks = px, WrapU = TextureWrap.Repeat, WrapV = TextureWrap.Repeat, Name = "Skid" });
            }

            public void Shift(Vector3 d)
            {
                for (int i = 0; i < _used * 4; i++) _v[i].Position -= d;
                for (int i = 0; i < _used; i++) { _endL[i] -= d; _endR[i] -= d; }
            }

            /// <returns>Index des neuen Endpunkts (für das nächste Segment)</returns>
            public int Add(Vector3 p, Vector3 n, Vector3 right, float width, float alpha, int prev, (byte r, byte g, byte b) col)
            {
                int q = _next; _next = (_next + 1) % _max;
                _used = System.Math.Max(_used, q + 1);
                var rr = right * (width * 0.5f);
                Vector3 l1 = p - rr, r1 = p + rr;
                _endL[q] = l1; _endR[q] = r1;
                int b = q * 4;
                byte a = (byte)(Mathf.Clamp01(alpha) * 255);
                if (prev < 0)
                {
                    // Startpunkt: degeneriertes Quad (unsichtbar), dient nur als Anschluss
                    for (int k = 0; k < 4; k++) _v[b + k] = new ObjectVertex { Position = p, Normal = n, Color = 0u };
                }
                else
                {
                    byte pa = (byte)(_v[prev * 4 + 1].Color >> 24);
                    if (pa == 0) pa = a;
                    uint c0 = ObjectVertex.PackColor(col.r, col.g, col.b, pa), c1 = ObjectVertex.PackColor(col.r, col.g, col.b, a);
                    // TD_TireSkid: U entlang der Spur (Textur 128/256 px lang), V quer (32 px, Ränder schwarz)
                    float dist = Vector3.Distance(_endL[prev], l1);
                    float u0 = _v[prev * 4 + 1].Uv0.X;
                    float u1 = u0 + dist / (Mathf.Max(0.05f, width) * _tileAspect);
                    _v[b] = new ObjectVertex { Position = _endL[prev], Normal = n, Color = c0, Uv0 = new Vector2(u0, 0) };
                    _v[b + 1] = new ObjectVertex { Position = l1, Normal = n, Color = c1, Uv0 = new Vector2(u1, 0) };
                    _v[b + 2] = new ObjectVertex { Position = r1, Normal = n, Color = c1, Uv0 = new Vector2(u1, 1) };
                    _v[b + 3] = new ObjectVertex { Position = _endR[prev], Normal = n, Color = c0, Uv0 = new Vector2(u0, 1) };
                }
                return q;
            }

            public void Render(IRenderer r, Matrix4x4 unityToRender)
            {
                if (_used == 0) return;
                r.DrawDynamic(_v.AsSpan(0, _used * 4), _t.AsSpan(0, _used * 6), _mat, unityToRender);
            }
        }

        void UpdateShadow()
        {
            if (_shadowMat == null || _shadowDesc == null) return;
            var tr = Vehicle.VisualPose;
            Vector3 c = tr.TransformPoint(new Vector3(0f, 0.5f, 0f));
            float gy; Vector3 n = Vec.Up;
            if (Vehicle.Rigidbody.Raycast(c, Vec.Down, out var hit, 6f)) { gy = hit.point.Y - Drop(hit.collider); n = hit.normal; }
            else { _shadowActive = false; return; }
            float height = c.Y - 0.5f - gy;
            float k = Mathf.Clamp01(1f - height / 3f);
            _shadowActive = k > 0.02f;
            var fwd = U.ProjectOnPlane(tr.forward, n);
            if (fwd.LengthSquared() < 1e-4f) fwd = tr.forward;
            var rot = U.LookRotation(Vector3.Normalize(fwd), n);
            var scale = _shadowSize * (1f + height * 0.15f);
            _shadowModel = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(new Vector3(c.X, gy + 0.03f, c.Z));
            // _Strength nur bei merklicher Änderung neu hochladen (in Unity: SetFloat je Bild)
            float s = ShadowStrength * k;
            if (MathF.Abs(s - _appliedStrength) > 0.04f)
            {
                _shadowDesc.Strength = s; _appliedStrength = s;
                _r.UpdateMaterial(_shadowMat, _shadowDesc);
            }
        }

        /// <summary>Reifenspuren (gemeinsam, bleiben auch ohne Fahrzeug liegen wie in Unity) zeichnen.</summary>
        public static void RenderSkids(IRenderer r, Matrix4x4 unityToRender) { if (_skidOwner == r) { _skid?.Render(r, unityToRender); _skidGround?.Render(r, unityToRender); } }

        /// <summary>Floating Origin für die Reifenspuren (Unity-Raum), unabhängig vom Fahrzeug.</summary>
        public static void ShiftSkids(Vector3 d) { _skid?.Shift(d); _skidGround?.Shift(d); }

        /// <summary>Zeichnen; <paramref name="unityToRender"/> = Spiegelung (Unity -&gt; Spiel) x RootMatrix.</summary>
        public void Render(Matrix4x4 unityToRender)
        {
            _dust?.Render(_r, _camRight, _camUp, unityToRender);
            if (!_shadowActive || _quad == null || _shadowMat == null) return;
            _r.DrawMesh(_quad, _shadowMat, _shadowModel * unityToRender);
        }

        public void Dispose()
        {
            _quad?.Dispose(); _quad = null;
            _shadowMat?.Dispose(); _shadowMat = null;
            _dust?.Dispose(); _dust = null;
            _blob?.Dispose(); _blob = null;
        }
    }
}
