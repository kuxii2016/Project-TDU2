using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Physics;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Fahrzeugdatenbank laden, Autos im Hintergrund laden, aufbauen (Modell + Fahrphysik + Kamera), steuern und zeichnen.
    /// Portiert aus TDU2VehicleManager (Unity-Projekt): EnsureDatabase, Load, PollLoad, BuildVehicle, Despawn, SwitchCar, DriveInput.
    /// Die Fahrzeuglogik rechnet wie in Unity im Unity-Raum (+X = rechts); Spielkoordinaten (+X = links) nur an den Rändern
    /// (Spawn-Position, Zeichnen, Kamera). Tasten/Spielmodus bleiben im Spiel (Paradise.Game).
    /// </summary>
    public sealed class VehicleManager : IDisposable
    {
        /// <summary>Startauto (Dateiname aus der Datenbank, z.B. delta_integrale_evo_2).</summary>
        public string StartCar = "delta_integrale_evo_2";
        public bool LoadSound = true;
        /// <summary>Motor beim Hinsetzen/Spawnen aus (nicht beim Wechsel in voller Fahrt); er springt an, sobald der Spieler Gas,
        /// Bremse oder Lenkung betätigt (Game/Config/VehicleConfig.EngineOffAtSpawn).</summary>
        public bool EngineOffAtSpawn = true;
        /// <summary>Motor läuft (sonst kein Motorgeräusch, kein Gas).</summary>
        public bool EngineRunning { get; private set; } = true;

        /// <summary>Motor anlassen (EngineStart-Geräusch).</summary>
        public void StartEngine()
        {
            if (EngineRunning) return;
            EngineRunning = true;
            if (_audio != null) { _audio.EngineOn = true; _audio.Play("EngineStart"); }
        }
        /// <summary>Motor aus: Dauergeräusche (Motor/Abgas/Reifen) stoppen, bis wieder eine Fahreingabe erfolgt.</summary>
        public void StopEngine()
        {
            EngineRunning = false;
            if (_audio != null) { _audio.EngineOn = false; _audio.StopLoops(); }
        }
        public float GripScale = 0.62f;
        public DrivingAids Aids = DrivingAids.Sport;
        public bool AutomaticGearbox = true;
        /// <summary>Physik-Zeitschritt während der Fahrt (s). TDU2 rechnet die Räder zusätzlich in Unterschritten.</summary>
        public float FixedTimeStep = 0.01f;

        public CarDatabase? Database { get; private set; }
        public CameraDatabase? Cameras { get; private set; }
        public PhysicsConfig? Physics { get; private set; }
        public readonly List<CarPhysics> Available = new List<CarPhysics>();
        public Vehicle? Current { get; private set; }
        public VehicleCamera Camera { get; } = new VehicleCamera(1f);

        readonly IRenderer _r;
        readonly PhysicsWorld _world;
        string? _bnkDir;
        Task? _dbTask;
        Task<CarAssets>? _loadTask;
        int _carIndex = -1, _colorIndex;
        Vector3? _pendingPos; Quaternion _pendingRot; Vector3 _pendingVel;
        CarAssets? _assets;
        CarModel? _model;
        VehicleRigidbody? _rb;
        VehicleEffects? _fx;
        VehicleAudio? _audio;
        Avatars.CarAvatar? _driver;
        /// <summary>Fahrer-Avatar im Auto (Unity TDU2VehicleManager.ShowDriver).</summary>
        public bool ShowDriver = true;
        /// <summary>Fahrer-Avatar des aktuellen Autos (null ohne Sitz-Dummy oder ohne Avatardaten).</summary>
        public Avatars.CarAvatar? Driver => _driver;
        bool _liveryHooked;
        VehicleLights? _lights;
        CarDirt? _dirt;
        /// <summary>
        /// Darstellungsabsenkung des Geländes (MapStreamer.TerrainLower, Shader _TDU2TerrainLower): das sichtbare Gelände liegt so viel
        /// tiefer als sein Kollider. Stehen Räder auf dem Gelände, wird das Auto (nur die Darstellung) genauso tief gezeichnet –
        /// sonst schwebt es 10 cm über dem Boden. Physik unverändert (keine Stufe an Straßenkanten).
        /// </summary>
        public Func<float>? TerrainVisualLower;
        float _terrainDrop;
        /// <summary>Gelände-Kollider (MapStreamer: "&lt;Sektor&gt; Gelände").</summary>
        public static bool IsTerrain(StaticCollider? c) => c != null && c.Name != null && c.Name.EndsWith(" Gelände", StringComparison.Ordinal);
        /// <summary>Verschmutzung des aktuellen Autos (Waschanlage: Wash()).</summary>
        public CarDirt? Dirt => _dirt;
        VehicleGauges? _gauges;
        VehicleHud? _hud;
        Minimap? _minimap;
        /// <summary>Minimap (hud.bnk, Straßennetz der Insel).</summary>
        public Minimap? Minimap => _minimap;
        /// <summary>Inselbank für das Straßennetz der Minimap (MapStreamer.Index.IslandBank).</summary>
        public Func<string?>? IslandBank;
        /// <summary>Fahr-HUD (gauges.bnk) an/aus.</summary>
        public bool ShowHud = true;
        int _headlights = -1;   // -1 = noch nie gesetzt: Original-Lichtsteuerung startet im Modus 1 (Wetter schaltet)
        /// <summary>Lichter/Sound des aktuellen Autos (null ohne Auto).</summary>
        public VehicleLights? Lights => _lights;
        public VehicleAudio? Audio => _audio;
        // Interpolation (Unity: RigidbodyInterpolation.Interpolate): Lage vor/nach dem letzten Physikschritt
        Vector3 _prevPos, _curPos; Quaternion _prevRot = Quaternion.Identity, _curRot = Quaternion.Identity;
        string _status = "";
        float _steerInput;

        /// <summary>Bodenhöhe aus der Heightmap (Spielkoordinaten), Unity: Streamer.TryGetGroundHeight. null = unbekannt.
        /// Kein Raycast: der würde auch das eigene Auto treffen.</summary>
        public Func<Vector3, float?>? GroundHeight;
        public float Volume = 0.7f;
        /// <summary>Lautstärke-Faktoren: Reifenquietschen, Schaltgeräusche (Game/Config/AudioConfig).</summary>
        public float SquealVolume = 0.5f, GearShiftVolume = 0.35f;
        /// <summary>Helligkeit der Lichtgläser (Game/Config/VehicleConfig).</summary>
        public float LightBrightness = 3f;

        public string BnkDir => _bnkDir ?? "";
        public int ColorIndex => _colorIndex;
        public int RimId { get; private set; }
        public int InteriorId { get; private set; }
        public CarModel? Model => _model;
        public CarAssets? Assets => _assets;
        public CarPhysics? CurrentCar => _assets?.Car;
        public bool IsLoadingCar => _loadTask != null;
        public bool Ready => Database != null && Available.Count > 0;
        public string Status => _status;

        public VehicleManager(IRenderer renderer, PhysicsWorld world) { _r = renderer; _world = world; }

        public CarPhysics? FindCar(string fileName) => Available.Find(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? (Database != null ? Database.Cars.Find(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase)) : null);

        /// <summary>Datenbank im Hintergrund laden (einmal). bnkDir = Euro/Bnk.</summary>
        public void EnsureDatabase(string? bnkDir)
        {
            if (_dbTask != null) return;
            _bnkDir = bnkDir;
            if (string.IsNullOrEmpty(_bnkDir)) { _status = "Bnk-Ordner nicht gefunden"; return; }
            string bnk = _bnkDir;
            _status = "Lade Fahrzeugdatenbank ...";
            _dbTask = Task.Run(() =>
            {
                var dbFile = CarDatabase.FindDatabase(bnk) ?? throw new System.IO.FileNotFoundException("database/db_data.cpr fehlt");
                var db = CarDatabase.Load(dbFile);
                var cams = CameraDatabase.Load(bnk);
                var phys = PhysicsConfig.Load(bnk);
                var avail = new List<CarPhysics>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in db.Cars)
                {
                    if (!seen.Add(c.FileName)) continue;
                    if (CarDatabase.CarBank(bnk, c) != null) avail.Add(c);
                }
                avail.Sort((a, b) => string.Compare(a.ModelName, b.ModelName, StringComparison.OrdinalIgnoreCase));
                lock (Available) { Available.Clear(); Available.AddRange(avail); }
                Database = db; Cameras = cams; Physics = phys;
                // Avatare (Kleidung aus DBClothe, Bänke unter avatar/, Animationen unter scenarics/avatars) – Unity TDU2VehicleManager
                try { Avatars.AvatarLibrary.Init(bnk, db.Root, _r); }
                catch (Exception ex) { Log.Warn("Avatare: " + ex.Message); }
                CarUpgrades.Init(db.Root);   // Tuning: Zuwachs je Stufe (DBEnvironnement)
                // Sticker-Planchen (DBStickersBanks) für die eigenen Lackierungen (Unity TDU2StickerLibrary.Ensure)
                try { StickerLibrary.Init(bnk, db.Root, _r); }
                catch (Exception ex) { Log.Warn("Sticker-Planchen: " + ex.Message); }
            });
        }

        /// <summary>Pro Frame (Main-Thread): Datenbank-Status, fertig geladene Autos aufbauen.</summary>
        public void Update()
        {
            StickerLibrary.Update();   // fertig geladene Planchen übernehmen, Lackierungen neu zusammensetzen
            if (!_liveryHooked)
            {
                _liveryHooked = true;
                // Lackierung geändert (Sticker-Shop): am gefahrenen Auto sofort zeigen (Unity TDU2Liveries.Changed)
                Liveries.Changed += f => { if (_model != null && _assets != null && string.Equals(_assets.Car.FileName, f, StringComparison.OrdinalIgnoreCase)) LiveryView.ApplyOwn(_model, _assets, f); };
            }
            if (_dbTask != null && _dbTask.IsFaulted)
            {
                _status = "Datenbank-Fehler: " + _dbTask.Exception?.GetBaseException().Message;
                Log.Error(_status);
                _dbTask = Task.CompletedTask; Database = null;
            }
            if (Ready && _carIndex < 0)
            {
                _carIndex = Mathf.Max(0, Available.FindIndex(c => string.Equals(c.FileName, StartCar, StringComparison.OrdinalIgnoreCase)));
            }
            PollLoad();
            TrySettle();
        }

        // ------------------------------------------------------------------ Teleport (Unity TDU2VehicleManager.TeleportSafe/TrySettle)
        Vector3 _settleGame; Quaternion _settleRot; float _settleRefY; bool _settleHasRef;
        double _settleStart, _settleUntil = -1;
        static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        /// <summary>Teleport läuft noch (Auto wartet festgehalten, bis der Boden am Ziel geladen ist).</summary>
        public bool Settling => _settleUntil >= 0;

        /// <summary>
        /// Auto an eine Spielposition setzen (Karte: Spot/Straße). Es bleibt festgehalten (kinematisch), bis am Ziel Boden
        /// gefunden wird (höchstens 12 s) – Zielhöhe aus den Spieldaten, damit es nicht durch noch nicht geladene Kollision fällt.
        /// </summary>
        public void TeleportSafe(Vector3 game, Vector3 forwardGame)
        {
            var v = Current;
            if (v == null || _rb == null) return;
            ShiftTo(ToUnity(game));   // Physik-Ursprung ans Ziel (wie SpawnCar)
            _settleGame = game;
            _settleRefY = game.Y; _settleHasRef = float.IsFinite(game.Y);
            if (!_settleHasRef) { game.Y = _world.Origin.Y; _settleGame = game; }   // Höhe unbekannt: Boden per Strahl suchen
            _settleStart = Now; _settleUntil = Now + 12.0;
            var fwd = U.ProjectOnPlane(U.Mirror(forwardGame), Vec.Up);
            _settleRot = U.LookRotation(fwd.LengthSquared() > 1e-3f ? Vector3.Normalize(fwd) : Vec.Forward, Vec.Up);
            v.ResetVehicle(ToUnity(game) + Vec.Up * 1.5f, _settleRot);
            _rb.isKinematic = true;
            SnapPose();
            TrySettle();
        }

        void TrySettle()
        {
            if (_settleUntil < 0) return;
            var v = Current;
            if (v == null || _rb == null) { _settleUntil = -1; return; }
            var sp = ToUnity(_settleGame);
            float refY = sp.Y;
            bool ok = false; float y = 0f;
            if (_rb.Raycast(new Vector3(sp.X, refY + 500f, sp.Z), Vec.Down, out var hit, 3000f) && hit.normal.Y >= 0.6f) { y = hit.point.Y; ok = true; }
            bool timeout = Now > _settleUntil;
            // Treffer weit über/unter der Zielhöhe (z.B. Dach, Brücke oder noch grobe Insel-LOD) in den ersten 3 s nicht übernehmen
            if (ok && _settleHasRef && System.Math.Abs(y - refY) > 6f && Now - _settleStart < 3.0) ok = false;
            if (!ok && !timeout) return;   // Umgebung noch nicht geladen: weiter festhalten
            if (!ok) y = _settleHasRef ? refY : GroundY(sp);
            _rb.isKinematic = false;
            v.ResetVehicle(new Vector3(sp.X, y + 0.45f, sp.Z), _settleRot);
            SnapPose();
            _settleUntil = -1;
        }

        // ------------------------------------------------------------------ Physik / Darstellung

        /// <summary>Unity FixedUpdate + Physikschritt: Fahrzeugkräfte, dann Bepu-Schritt (gleicher Takt).</summary>
        public void FixedUpdate(float dt)
        {
            if (Current != null && _rb != null)
            {
                Current.FixedUpdate(dt);
                _rb.ApplyForces(dt);
            }
            // World Actors (Unity WorldActorBody-Auslöser): Schilder/Poller vor dem Auto lösen, bevor die Karosserie sie trifft
            if (Current != null && _rb != null)
                Paradise.Core.World.WorldActorBodies.Current?.CheckHits(ToGame(_rb.worldCenterOfMass), U.Mirror(_rb.linearVelocity), 1.6f);
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            var velBefore = Current != null && _rb != null ? _rb.linearVelocity : Vector3.Zero;
            long stepMs = Environment.TickCount64;
            _world.Step(dt);
            if (Current != null) CheckBodyImpact(velBefore, stepMs);
            HopDiag(dt);
            RollDiag();
            // Floating Origin (Unity: FloatingOrigin.Shift): Auto mehr als 500 m vom Ursprung -> Welt verschieben
            if (Current != null && _rb != null && _rb.position.LengthSquared() > 500f * 500f) { ShiftTo(_rb.position); _diagLastShift = _diagTime; }
            if (Current != null && _rb != null)
            {
                _prevPos = _curPos; _prevRot = _curRot;
                _curPos = _rb.position; _curRot = _rb.rotation;
            }
        }

        // ------------------------------------------------------------------ Diagnose: Hüpfer bei schneller Fahrt ([DIAG Hop] im Log)
        /// <summary>Zusatzangaben für die Hüpfer-Diagnose (Streamer: zuletzt eingetragene Kollider …).</summary>
        public Func<string>? DiagContext;
        Vector3 _diagPrevVel;
        double _diagTime, _diagLastShift = -1e9, _diagNextHop;

        // ------------------------------------------------------------------ Diagnose: Umkippen ([DIAG Roll] im Log)
        long _rollNextLog;
        bool _rollTipped;

        /// <summary>
        /// Auto kippt (Neigung &gt; 15°): Kontakte der Karosserie mit der Welt aus den letzten 0,6 s loggen (Gegenstück, Normale,
        /// Höhe am Auto, Tiefe) – zeigt, ob z.B. eine Bordsteinkante die Box anhebt.
        /// </summary>
        void RollDiag()
        {
            long now = Environment.TickCount64;
            var q = PhysicsWorld.RecentContacts;
            while (q.TryPeek(out var c0) && now - c0.ms > 600) q.TryDequeue(out _);
            if (Current == null || _rb == null) return;
            var up = U.Rot(_rb.rotation, Vec.Up);
            bool tipped = up.Y < MathF.Cos(15f * MathF.PI / 180f);
            if (!tipped) { _rollTipped = false; return; }
            if (_rollTipped || now < _rollNextLog) return;
            _rollTipped = true; _rollNextLog = now + 3000;
            var v = _rb.linearVelocity;
            int n = 0;
            foreach (var c in q)
            {
                if (n++ >= 12) break;
                var sc = _world.StaticOf(c.other);
                // Versatz/Normale im Spielraum (Bepu); Höhe des Kontakts relativ zum Schwerpunkt
            }
        }

        /// <summary>Plötzlicher Sprung der Vertikalgeschwindigkeit (&gt; 1,5 m/s in einem Physikschritt) ab 60 km/h: Umstände loggen.</summary>
        void HopDiag(float dt)
        {
            _diagTime += dt;
            if (Current == null || _rb == null) return;
            var v = _rb.linearVelocity;
            float dvy = v.Y - _diagPrevVel.Y;
            _diagPrevVel = v;
            float kmh = v.Length() * 3.6f;
            if (kmh < 60f || System.Math.Abs(dvy) < 1.5f || _diagTime < _diagNextHop) return;
            _diagNextHop = _diagTime + 0.5;

            try { var ctx = DiagContext?.Invoke(); } catch (Exception) { }
        }

        /// <summary>Unity LateUpdate: Räder/Lenkrad, Kamera.</summary>
        // Profiler: Teile von LateUpdate (laut Profiler ~0,5 ms und bis 640 KB Speicher je Bild)
        static readonly ProfilerMarker s_mLuCar = new("Vehicle.LateUpdate (wheels)", ProfilerCategory.Scripts);
        static readonly ProfilerMarker s_mLuFx = new("VehicleFx.LateUpdate", ProfilerCategory.Scripts);
        static readonly ProfilerMarker s_mLuLights = new("VehicleLights/CarDirt.Update", ProfilerCategory.Scripts);
        static readonly ProfilerMarker s_mLuTunnel = new("TunnelLight.Update", ProfilerCategory.Scripts);
        static readonly ProfilerMarker s_mLuHud = new("Gauges/HUD.LateUpdate", ProfilerCategory.UI);
        static readonly ProfilerMarker s_mLuMinimap = new("Minimap.LateUpdate (GPS)", ProfilerCategory.UI);
        static readonly ProfilerMarker s_mLuAudio = new("VehicleAudio.Update", ProfilerCategory.Audio);
        static readonly ProfilerMarker s_mLuCamera = new("VehicleCamera.LateUpdate", ProfilerCategory.Scripts);

        public void LateUpdate(float dt, float time, InputState? input, float fixedAlpha)
        {
            if (Current == null) return;
            float a = Mathf.Clamp01(fixedAlpha);
            Current.VisualPose = new VehiclePose { position = Vector3.Lerp(_prevPos, _curPos, a), rotation = Quaternion.Slerp(_prevRot, _curRot, a) };
            // Gelände-Absenkung der Darstellung: Anteil der Räder auf dem Gelände, weich nachgeführt
            float lower = TerrainVisualLower?.Invoke() ?? 0f;
            int onGround = 0, onTerrain = 0;
            foreach (var w in Current.Wheels) { if (!w.Grounded) continue; onGround++; if (IsTerrain(w.GroundCollider)) onTerrain++; }
            float dropTarget = onGround > 0 ? lower * onTerrain / onGround : _terrainDrop;
            _terrainDrop = Mathf.Lerp(_terrainDrop, dropTarget, 1f - MathF.Exp(-dt * 12f));
            VehicleEffects.TerrainDrop = lower;
            if (_terrainDrop > 1e-4f)
            {
                var vp = Current.VisualPose; vp.position.Y -= _terrainDrop; Current.VisualPose = vp;
            }
            using (s_mLuCar.Auto())
                Current.LateUpdate(dt);
            using (s_mLuFx.Auto())
                _fx?.LateUpdate(dt, U.Rot(Camera.rotation, Vec.Right), U.Rot(Camera.rotation, Vec.Up), Camera.position);
            using (s_mLuLights.Auto())
            {
                _lights?.Update(dt);
                // Unity TDU2Weather.UpdateCarDirt: Regen (Rain > 0,2) wäscht den Schmutz auf der Straße ab (DBCarDirtData)
                if (_dirt != null) _dirt.Raining = Paradise.Core.World.WorldWeather.Rain > 0.2f;
                _dirt?.Update(dt);
            }
            // Original 0x718140: Tunnelfaktor des Spielerautos (Abstand zum nächsten Tunnelende auf der Straße), Kamera im Umkreis von 100 m
            using (s_mLuTunnel.Auto())
                Paradise.Core.World.TunnelLight.Update(dt, _minimap?.Gps.Network, ToGame(Current.VisualPose.position), ToGame(Camera.position));
            using (s_mLuHud.Auto())
            {
                _gauges?.LateUpdate(dt);
                if (_hud != null) { _hud.ShowHud = ShowHud; _hud.LateUpdate(_r.Width, _r.Height); }
            }
            if (_minimap != null)
            {
                var vp = Current.VisualPose;
                using (s_mLuMinimap.Auto())
                    _minimap.LateUpdate(dt, ShowHud, ToGame(vp.position), U.Mirror(vp.forward), U.Mirror(vp.right), vp.forward, Current.SpeedKmh);
            }
            using (s_mLuAudio.Auto())
                if (_audio != null) { _audio.InteriorView = Camera.IsInterior; _audio.Update(dt); }
            using (s_mLuCamera.Auto())
                Camera.LateUpdate(dt, time, input);
        }

        /// <summary>GPS weiterrechnen, wenn das Fahrzeug eingefroren ist (große Karte/Pausemenü; sonst über die Minimap).</summary>
        public void UpdateGps(float dt)
        {
            if (_minimap == null || Current == null) return;
            var vp = Current.VisualPose;
            _minimap.Gps.Update(dt, _minimap.Hud, ToGame(vp.position), U.Mirror(vp.forward));
        }

        /// <summary>
        /// HUD-/Minimap-Sichtbarkeit sofort anwenden – auch wenn <see cref="LateUpdate"/> nicht läuft (Fahrzeug eingefroren,
        /// z. B. große Karte; Unity: TDU2BigMap blendet HUD und Minimap beim Öffnen aus).
        /// </summary>
        public void ApplyHudVisibility()
        {
            if (_hud != null) { _hud.ShowHud = ShowHud; _hud.LateUpdate(_r.Width, _r.Height); }
            if (_minimap != null && Current != null)
            {
                var vp = Current.VisualPose;
                _minimap.LateUpdate(0f, ShowHud, ToGame(vp.position), U.Mirror(vp.forward), U.Mirror(vp.right), vp.forward, Current.SpeedKmh);
            }
        }

        /// <summary>Lage sofort übernehmen (nach Zurücksetzen/Spawnen, keine Interpolation über den Sprung).</summary>
        void SnapPose()
        {
            if (_rb == null || Current == null) return;
            _prevPos = _curPos = _rb.position; _prevRot = _curRot = _rb.rotation;
            Current.VisualPose = new VehiclePose { position = _curPos, rotation = _curRot };
        }

        /// <summary>Unity-lokal -> Spielkoordinaten (Floating Origin).</summary>
        public Vector3 ToGame(Vector3 unityLocal) => U.Mirror(unityLocal) + _world.Origin;
        /// <summary>Spielkoordinaten -> Unity-lokal.</summary>
        public Vector3 ToUnity(Vector3 game) => U.Mirror(game - _world.Origin);

        /// <summary>Matrix Unity-lokal -> Render-Raum (Spiegelung, Ursprung, Render-Wurzel; ohne Genauigkeitsverlust).</summary>
        public Matrix4x4 UnityToRender(Matrix4x4 gameToRender) => Matrix4x4.CreateScale(-1f, 1f, 1f) * Matrix4x4.CreateTranslation(_world.Origin) * gameToRender;

        /// <summary>Kamera im Render-Raum (Position, Blickrichtung, oben).</summary>
        public (Vector3 pos, Vector3 forward, Vector3 up) CameraRender(Matrix4x4 gameToRender)
        {
            var m = UnityToRender(gameToRender);
            return (Vector3.Transform(Camera.position, m), Vector3.TransformNormal(U.Rot(Camera.rotation, Vec.Forward), m), Vector3.TransformNormal(U.Rot(Camera.rotation, Vec.Up), m));
        }

        /// <summary>Fahrzeuglichter (Scheinwerfer, Brems-/Rückfahrlicht) als Lampen im Render-Raum anhängen.</summary>
        public void CollectLamps(List<LampLight> output, Matrix4x4 gameToRender)
        {
            if (Current == null || _lights == null) return;
            _lights.CollectLamps(output, Current.VisualPose, UnityToRender(gameToRender));
        }

        /// <summary>Lage des Fahrzeugs in Spielkoordinaten.</summary>
        public Vector3 PositionGame => Current != null ? ToGame(Current.transform.position) : Vector3.Zero;
        /// <summary>Fahrtrichtung in Spielkoordinaten (Autosave: Richtung, TDU2Login.CaptureState).</summary>
        public Vector3 ForwardGame => Current != null ? U.Mirror(Current.transform.forward) : Vector3.UnitZ;

        /// <summary>Physik-Ursprung auf die Unity-lokale Stelle p legen (Welt verschieben, lokale Lagen nachführen).</summary>
        void ShiftTo(Vector3 unityLocal)
        {
            var before = _world.Origin;
            _world.ShiftOrigin(ToGame(unityLocal));
            var dU = U.Mirror(_world.Origin - before);   // Verschiebung im Unity-Raum
            if (dU == Vector3.Zero) return;
            _prevPos -= dU; _curPos -= dU;
            if (Current != null)
            {
                Current.VisualPose = new VehiclePose { position = Current.VisualPose.position - dU, rotation = Current.VisualPose.rotation };
                // Radkontakte stammen aus dem letzten Physikschritt (alte Koordinaten) – mitverschieben, sonst zieht die nächste
                // Reifenspur einen Streifen über die ganze Verschiebung (Spuren an falscher Stelle nach dem Floating-Origin-Wechsel)
                foreach (var w in Current.Wheels) w.ContactPoint -= dU;
            }
            Camera.Shift(dU);
            _hud?.Shift(dU);
            _fx?.Shift(dU);
            VehicleEffects.ShiftSkids(dU);
        }

        // ------------------------------------------------------------------ Laden / Spawnen

        /// <summary>Unity SpawnAtCamera: 8 m vor der Kamera (waagerecht), auf dem Boden, Blick in Kamerarichtung (Spielkoordinaten).</summary>
        public void SpawnAt(Vector3 cameraGame, Vector3 cameraForwardGame)
        {
            if (!Ready) return;
            if (Current == null)
            {
                var before = _world.Origin;
                _world.ShiftOrigin(cameraGame);   // Ursprung an die Spawnstelle
                VehicleEffects.ShiftSkids(U.Mirror(_world.Origin - before));
            }
            var fwd = U.ProjectOnPlane(U.Mirror(cameraForwardGame), Vec.Up);
            if (fwd.LengthSquared() < 1e-3f) fwd = Vec.Forward;
            var pos = ToUnity(cameraGame) + Vector3.Normalize(fwd) * 8f;
            pos.Y = GroundY(pos) + 0.25f;
            Load(_carIndex, pos, U.LookRotation(Vector3.Normalize(fwd), Vec.Up), Vector3.Zero);
        }

        /// <summary>
        /// Unity TDU2VehicleManager.SpawnCar (TDU2Login.SpawnCar): ein bestimmtes Auto genau an einer Spielposition (Boden + 0,4 m),
        /// Blick in Richtung forwardGame. colorIndex &lt; 0 = Lack behalten. false = Auto nicht in der Datenbank.
        /// </summary>
        bool _colorFromChoice;   // _colorIndex stammt aus einer gekauften Ausstattung (TDU2CarChoices)

        public bool SpawnCar(string fileName, Vector3 game, Vector3 forwardGame, int colorIndex = -1)
        {
            if (!Ready) return false;
            int i = Available.FindIndex(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            if (colorIndex >= 0) _colorIndex = colorIndex;
            if (Current != null || _model != null) Despawn();
            var before = _world.Origin;
            _world.ShiftOrigin(game);   // Ursprung an die Spawnstelle (wie SpawnAt)
            VehicleEffects.ShiftSkids(U.Mirror(_world.Origin - before));
            var fwd = U.ProjectOnPlane(U.Mirror(forwardGame), Vec.Up);
            if (fwd.LengthSquared() < 1e-3f) fwd = Vec.Forward;
            var pos = ToUnity(game);
            pos.Y = GroundY(pos) + 0.4f;
            Load(i, pos, U.LookRotation(Vector3.Normalize(fwd), Vec.Up), Vector3.Zero);
            return true;
        }

        /// <summary>Nächstes/vorheriges Auto (0 = neu laden, z.B. nach Farbwechsel) an derselben Stelle, mit gleicher Geschwindigkeit.</summary>
        public void SwitchCar(int delta)
        {
            if (Available.Count == 0) return;
            if (Current != null && _rb != null)
            {
                var tr = Current.transform;
                var vel = _rb.linearVelocity;
                var f = U.ProjectOnPlane(tr.forward, Vec.Up);
                Load(_carIndex + delta, tr.position + Vec.Up * 0.2f, U.LookRotation(f.LengthSquared() > 1e-3f ? Vector3.Normalize(f) : Vec.Forward, Vec.Up), vel);
            }
            else { _carIndex = (_carIndex + delta + Available.Count) % Available.Count; _status = Available[_carIndex].ModelName; }
        }

        public void NextColor() { _colorIndex++; SwitchCar(0); }

        /// <summary>Unity GroundY (Unity-Raum).</summary>
        float GroundY(Vector3 p)
        {
            if (_rb != null && _rb.Raycast(p + Vec.Up * 200f, Vec.Down, out var hit, 2000f)) return hit.point.Y;
            if (_world.RaycastLocal(U.Mirror(p + Vec.Up * 200f), Vec.Down, 2000f, out var h)) return h.Point.Y;
            var y = GroundHeight?.Invoke(ToGame(p));
            return y.HasValue ? y.Value - _world.Origin.Y : p.Y;
        }

        void Load(int index, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (Available.Count == 0 || _loadTask != null || Database == null || _bnkDir == null) return;
            _carIndex = (index % Available.Count + Available.Count) % Available.Count;
            var car = Available[_carIndex];
            _pendingPos = pos; _pendingRot = rot; _pendingVel = vel;
            string bnk = _bnkDir; var db = Database; int color = _colorIndex; bool snd = LoadSound;
            // gekaufte Ausstattung (Autohändler: Lack, Felgen, Innenraum) – Unity TDU2VehicleManager.Load
            int rim = 0, interior = 0;
            if (CarChoices.TryGet(car.FileName, out var ch)) { color = _colorIndex = ch.Color; rim = ch.Rim; interior = ch.Interior; _colorFromChoice = true; }
            else if (_colorFromChoice) { color = _colorIndex = 0; _colorFromChoice = false; }   // Lack eines anderen Autos nicht übernehmen
            _status = "Lade " + car.ModelName + " ...";
            _loadTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd, rim, interior));
        }

        void PollLoad()
        {
            if (_loadTask == null || !_loadTask.IsCompleted) return;
            var t = _loadTask; _loadTask = null;
            if (t.IsFaulted)
            {
                _status = "Fehler: " + t.Exception?.GetBaseException().Message;
                Log.Error(t.Exception?.GetBaseException().ToString() ?? _status);
                return;
            }
            var a = t.Result;
            foreach (var l in a.Log) Log.Info(a.Car.FileName + ": " + l);
            Despawn();
            try { BuildVehicle(a); }
            catch (Exception ex) { _status = "Fehler beim Aufbau: " + ex.Message; Log.Error(ex.ToString()); }
        }

        /// <summary>Knotenposition im Fahrzeugraum (Unity: go.transform.InverseTransformPoint(node.position)).</summary>
        Vector3 AnchorOf(CarNode n) => U.Mirror(_model!.LocalToVisualParent(n).Translation);

        /// <summary>Determinante "Fahrzeug -> Elternknoten" wie in Unity (das Visual ist dort an X gespiegelt).</summary>
        float UnityDet(CarNode? parent) => parent == null ? -1f : -_model!.LocalToVisualParent(parent).GetDeterminant();

        void BuildVehicle(CarAssets a)
        {
            var car = a.Car;
            _model = CarModel.Build(a, _r);
            RimId = a.Rim != null ? a.Rim.Id : 0;
            InteriorId = a.Interior != null ? a.Interior.Id : 0;

            var rb = new VehicleRigidbody(_world) { isKinematic = true };
            _rb = rb;

            // Karosserie-Collider (Bounds der High-Poly-Karosserie), Unity-Raum = gespiegelt
            var bmin = _model.BodyMin; var bmax = _model.BodyMax;
            var bCenter = U.Mirror((bmin + bmax) * 0.5f); var bSize = bmax - bmin;
            float lift = Physics != null ? Physics.Get("CollisionOffset", 0.1f) : 0.1f;
            float bottom = bmin.Y + lift;
            bool isBike = car != null && car.IsBike;
            Vector3 boxCenter, boxSize;
            if (isBike)
            {
                bottom = bmin.Y + Mathf.Max(lift, 0.2f);
                boxCenter = new Vector3(0f, (bottom + bmax.Y) * 0.5f + 0.02f, bCenter.Z);
                boxSize = new Vector3(Mathf.Max(0.38f, bSize.X * 0.68f), Mathf.Max(0.36f, bmax.Y - bottom), Mathf.Max(0.9f, bSize.Z * 0.84f));
            }
            else
            {
                boxCenter = new Vector3(bCenter.X, (bottom + bmax.Y) * 0.5f, bCenter.Z);
                boxSize = new Vector3(bSize.X * 0.96f, Mathf.Max(0.3f, bmax.Y - bottom), bSize.Z * 0.98f);
            }

            var veh = new Vehicle { Rigidbody = rb };
            veh.Car = car!; veh.Config = Physics!; veh.TorqueMax = a.TorqueMax!; veh.TorqueMin = a.TorqueMin!;
            veh.GripScale = GripScale; veh.Aids = Aids; veh.AutomaticGearbox = AutomaticGearbox;
            CarUpgrades.ApplyTo(veh);   // Tuningstufen des Autos (Tuner)
            veh.OffRoadCar = car!.IsOffroad || car.OffroadReal;
            veh.GroundFallback = p => { var y = GroundHeight?.Invoke(ToGame(p)); return y.HasValue ? (true, y.Value - _world.Origin.Y) : (false, 0f); };

            var wheels = new List<Vehicle.Wheel>();
            if (isBike)
            {
                // Bikes: Vorderrad an FORK/WHR_F, Hinterrad an ARM/WHR_R.
                // Felge (D_RIM_x) und Reifen (TIRE_x) drehen; Bremssattel (BRAKE_x) bleibt stehen.
                var wheelDefs = new[] { ("F", true), ("R", false) };
                foreach (var wd in wheelDefs)
                {
                    string tag = wd.Item1;
                    string name = tag == "F" ? "WHR_F" : "WHR_R";
                    bool front = wd.Item2;
                    var whr = _model.Find(name);
                    var w = new Vehicle.Wheel { Name = front ? "FL" : "RL", Front = front };
                    if (whr != null) w.Anchor = AnchorOf(whr);
                    else
                    {
                        float z = car.Wheelbase * 0.0005f * (front ? 1f : -1f);
                        w.Anchor = new Vector3(0f, 0.35f, z);
                    }
                    w.Left = w.Anchor.X < 0f;
                    w.Radius = _model.TireRadiusFx(front);
                    w.Width = a.Rim != null ? (front ? a.Rim.WidthFront : a.Rim.WidthRear) * 0.001f : 0.14f;
                    // Nur die Vorderradgabel bewegt sich mit der Federung (FORK enthält WHR_F)
                    w.Fork = front ? _model.Find("FORK") : null;
                    // Lenkung läuft beim Bike über BARS (Fahrzeugfeld BikeSteerNode), nicht über STIRRUP
                    w.Stirrup = null;
                    // Felge dreht, Reifen dreht mit
                    w.Disc = _model.Find("D_RIM_" + tag) ?? _model.Find("RIM_" + tag);
                    w.Spin2 = _model.Find("TIRE_" + tag);
                    wheels.Add(w);
                }
                veh.ModelRoot = _model.Visual;
                veh.BikeSteerNode = _model.Find("BARS") ?? _model.Find("FORK");
            }
            else
            {
                foreach (var n in new[] { "FL", "FR", "RL", "RR" })
                {
                    bool front = n[0] == 'F';
                    var fork = _model.Find("FORK_" + n);
                    var w = new Vehicle.Wheel { Name = n, Front = front };
                    if (fork != null) w.Anchor = AnchorOf(fork);
                    else
                    {
                        float x = (front ? car.TrackFront : car.TrackRear) * 0.0005f;
                        float z = front ? car.Wheelbase * 0.0005f : -car.Wheelbase * 0.0005f;
                        w.Anchor = new Vector3(n[1] == 'L' ? -x : x, 0.2f, z);
                    }
                    w.Left = w.Anchor.X < 0f;
                    w.Radius = _model.TireRadiusFx(front);
                    w.Width = a.Rim != null ? (front ? a.Rim.WidthFront : a.Rim.WidthRear) * 0.001f : 0.24f;
                    w.Fork = fork;
                    w.Stirrup = _model.Find("STIRRUP_" + n);
                    w.Disc = _model.Find("DISC_" + n);
                    if (w.Stirrup != null && w.Stirrup.Parent != null)
                        w.SteerSign = UnityDet(w.Stirrup.Parent) < 0f ? -1f : 1f;
                    wheels.Add(w);
                }
            }
            veh.Wheels = wheels.ToArray();
            veh.SteeringWheel = _model.SteeringWheel;
            if (veh.SteeringWheel != null && veh.SteeringWheel.Parent != null)
                veh.SteeringWheelSign = UnityDet(veh.SteeringWheel.Parent) < 0f ? -1f : 1f;
            veh.Setup();

            // Karosserie-Box darf nicht unter die Räder reichen: sonst liegt das Auto auf der Box und die Räder hängen in der Luft
            // (Teile unterhalb der Reifenaufstandsfläche in den Karosserie-Bounds, siehe Log "Fahrzeug-Diagnose").
            float wheelBottom = float.MaxValue;
            foreach (var w in veh.Wheels) wheelBottom = Mathf.Min(wheelBottom, w.Anchor.Y - w.Radius);
            if (veh.Wheels.Length > 0 && bottom < wheelBottom + 0.05f)
            {
                float top = boxCenter.Y + boxSize.Y * 0.5f;
                bottom = wheelBottom + 0.05f;
                boxCenter.Y = (bottom + top) * 0.5f;
                boxSize.Y = Mathf.Max(0.3f, top - bottom);
            }

            // Körper anlegen (Unity: Rigidbody + BoxCollider am Auto-GameObject)
            var pos = _pendingPos ?? Vector3.Zero;
            rb.Create(pos, _pendingRot, boxCenter, boxSize, !isBike);   // Bikes bleiben bei einer Box
            veh.CaptureVisualBase();
            // LogDiagnosis(veh, bmin, bmax, boxCenter, boxSize, wheelBottom);   // Rad-Diagnose bei Bedarf wieder einschalten

            // Position
            rb.isKinematic = false;
            veh.ResetVehicle(pos, _pendingRot);
            if (_pendingVel.LengthSquared() > 0.01f) veh.SetVelocity(_pendingVel);
            _pendingPos = null;

            // Sound
            if (LoadSound)
            {
                _audio = new VehicleAudio { Vehicle = veh, MasterVolume = Volume, SquealFactor = SquealVolume, GearShiftFactor = GearShiftVolume };
                _audio.Setup(a);
                if (_pendingVel.LengthSquared() < 1f && !EngineOffAtSpawn) _audio.Play("EngineStart");
            }
            // Motor aus bis zur ersten Fahreingabe (Spawn im Stand); bei Wechsel in Fahrt läuft er weiter
            EngineRunning = !(EngineOffAtSpawn && _pendingVel.LengthSquared() < 1f);
            if (_audio != null) _audio.EngineOn = EngineRunning;

            // Lichter (Scheinwerfer, Bremse, Rückfahr, Blinker)
            _lights = new VehicleLights { Headlights = _headlights >= 0 ? _headlights : VehicleLights.OriginalControl ? 1 : 0, LensBrightness = LightBrightness };
            _lights.Setup(veh, _model);

            // Cockpit-Nadeln (ND_SPD/ND_RPM) und Fahr-HUD (gauges.bnk)
            _gauges = new VehicleGauges();
            _gauges.Setup(veh, _model, a.Gauges);
            _hud ??= new VehicleHud();
            _hud.Bind(_bnkDir!, veh, a, Camera);
            // Minimap (Unity TDU2Minimap.Bind) und Feldwege aus dem Straßennetz als Untergrund
            _minimap ??= new Minimap();
            _minimap.Database = Database;
            _minimap.Bind(_bnkDir, IslandBank?.Invoke());
            var mm = _minimap;
            veh.RoadSurfaceAt = p => mm.NearestRoadType(ToGame(p), 4f) == 1 ? 2 : 0;

            // Bodenschatten, Reifenspuren, Staub, glühende Bremsscheiben (TDU2VehicleEffects)
            _fx = new VehicleEffects(_r);
            _fx.Setup(veh, _model, a);
            // Fahrer (Unity TDU2CarAvatar.Attach: eigenes Aussehen aus dem Spielstand, Lenkung = Vehicle.Steer)
            _driver?.Dispose(); _driver = null;
            if (ShowDriver)
            {
                _driver = Avatars.CarAvatar.Attach(_r, _model, Avatars.AvatarConfigs.Local, false, () => veh.Steer);
                if (_driver != null) _driver.ConfigCode = () => Avatars.AvatarConfigs.LocalCode;
            }
            // Verschmutzung (dirt_scratch, DBCarDirtData)
            _dirt = new CarDirt();
            _dirt.Setup(veh, _model);
            // eigene Lackierung/Beklebung (Sticker-Shop, lokal gespeichert)
            LiveryView.ApplyOwn(_model, a, car.FileName);

            // Kamera
            Camera.Target = veh; Camera.Cameras = Cameras; Camera.CarId = car.CameraId != 0 ? car.CameraId : car.Id;

            _assets = a;
            Current = veh;
            SnapPose();
            _status = $"{car.ModelName}  ({a.Color?.Name ?? "Standardfarbe"})";
        }

        /// <summary>Maße/Räder ins Log (Abgleich Physik vs. Modell: Luft zwischen Rad und Straße, Lenkrichtung).</summary>
        void LogDiagnosis(Vehicle veh, Vector3 bmin, Vector3 bmax, Vector3 boxCenter, Vector3 boxSize, float wheelBottom)
        {
            if (_model == null) return;
            var sb = new System.Text.StringBuilder();
            sb.Append($"Fahrzeug-Diagnose {veh.Car?.FileName}: Karosserie (Spiel) min {bmin} max {bmax}, Box (Unity) Mitte {boxCenter} Größe {boxSize}, Radaufstand y {wheelBottom:0.000}");
            foreach (var w in veh.Wheels)
            {
                sb.Append($"\n  Rad {w.Name}: Anker (Unity) {w.Anchor}, Radius {w.Radius:0.000}, Federweg +{w.TravelUp:0.000}/-{w.Droop:0.000}, Lenkvorzeichen {w.SteerSign}, ");
                sb.Append($"Fork {(w.Fork != null ? w.Fork.Name + " " + U.Mirror(_model.LocalToVisualParent(w.Fork).Translation) : "-")}, ");
                sb.Append($"Stirrup {(w.Stirrup != null ? w.Stirrup.Name + (w.Stirrup.RawLocal != null ? " (Matrix!)" : "") : "-")}, Disc {(w.Disc != null ? w.Disc.Name : "-")}");
                var tire = _model.Find("D_TIRE_" + w.Name);
                if (tire != null) sb.Append($", D_TIRE {U.Mirror(_model.LocalToVisualParent(tire).Translation)}");
            }
            // Teile der Karosserie unterhalb der Radaufstandsfläche
            var body = _model.Find("BODY_HR") ?? _model.Find("BODY") ?? _model.Visual;
            foreach (var n in body.SelfAndDescendants())
            {
                if (!n.HasMesh) continue;
                var m = _model.LocalToVisualParent(n);
                float low = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    low = Mathf.Min(low, Vector3.Transform(c, m).Y);
                }
                if (low < wheelBottom + 0.05f) sb.Append($"\n  tief: {n.Name} y {low:0.000}");
            }
            Log.Info(sb.ToString());
        }

        public void Despawn()
        {
            Current = null;
            Camera.Target = null;
            _fx?.Dispose(); _fx = null;
            _gauges = null;
            _dirt = null;
            _minimap?.LateUpdate(0f, false, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitZ, 0f);
            if (_hud != null) { _hud.Vehicle = null; _hud.LateUpdate(_r.Width, _r.Height); }
            _audio?.Dispose(); _audio = null;
            if (_lights != null) _headlights = _lights.Headlights;
            _lights?.Dispose();
            _lights = null;
            _rb?.Dispose(); _rb = null;
            _driver?.Dispose(); _driver = null;
            LiveryView.Release(_model);
            _model?.Destroy(); _model = null; RimId = 0; InteriorId = 0;
            _assets?.DestroyTextures(); _assets = null;
        }

        // ------------------------------------------------------------------ Eingaben (Unity DriveInput, Tastatur)

        /// <summary>Pedale/Lenkung je Frame (Unity DriveInput): Lenkrad direkt (der Fahrer hält das Rad), Tastatur/Pad geglättet.</summary>
        public void DriveInput(float accel, float brake, float steer, bool handbrake, float dt, bool analog = false, bool wheel = false)
        {
            if (Current == null) return;
            if (ExternalControl) { AfterInput?.Invoke(Current); return; }   // Unity ExternalControl: Autopilot steuert (RaceAutopilot)
            if (!EngineRunning)
            {
                // erst die Fahreingabe des Spielers lässt den Motor an (Handbremse allein nicht)
                if (accel > 0.05f || brake > 0.05f || Mathf.Abs(steer) > 0.05f) StartEngine();
                else accel = 0f;
            }
            if (wheel) _steerInput = steer;
            else
            {
                float rate = (Mathf.Abs(steer) > Mathf.Abs(_steerInput) ? 4f : 6f) * (analog ? 2.5f : 1f);
                _steerInput = Mathf.MoveTowards(_steerInput, steer, rate * dt);
            }
            Current.AccelPedal = accel;
            Current.BrakePedal = brake;
            Current.Steer = _steerInput;
            Current.HandBrake = handbrake ? 1f : 0f;
            AfterInput?.Invoke(Current);   // Unity: LateUpdate der Rennlogik nach der Eingabe (Startsperre im Countdown)
        }

        /// <summary>Unity TDU2VehicleManager.ExternalControl: Fahreingaben des Spielers werden ignoriert (Autopilot nach dem Ziel).</summary>
        public bool ExternalControl;
        /// <summary>Nach jeder Fahreingabe (Unity: LateUpdate von TDU2RaceManager hält das Auto im Countdown fest).</summary>
        public Action<Vehicle>? AfterInput;
        /// <summary>
        /// Harter Stoß der Karosserie gegen Wand/Verkehr (Unity RaceCarCollisionProbe.OnCollisionEnter: Kontakt mit nennenswert
        /// waagrechter Normale, |n.y| &lt;= 0,7; Wert = relative Geschwindigkeit in m/s). Umfallende World Actors melden sich über
        /// WorldActorBodies.Broken.
        /// </summary>
        public event Action<float>? BodyImpact;
        bool _wallContact;

        void CheckBodyImpact(Vector3 velBefore, long stepStartMs)
        {
            if (BodyImpact == null || _rb == null) { _wallContact = false; return; }
            bool wall = false;
            foreach (var c in PhysicsWorld.RecentContacts)
            {
                if (c.ms < stepStartMs) continue;
                if (MathF.Abs(c.normal.Y) > 0.7f) continue;   // Boden ignorieren
                if (c.other.Mobility == BepuPhysics.Collidables.CollidableMobility.Static)
                {
                    var sc = _world.StaticOf(c.other);
                    if (sc != null && sc.IgnoreRays) continue;   // World Actors/Props melden selbst (Unity: RacePropHit)
                }
                else if (c.other.Mobility == BepuPhysics.Collidables.CollidableMobility.Dynamic && c.other.BodyHandle.Value == _rb.Handle.Value) continue;
                wall = true; break;
            }
            if (wall && !_wallContact) BodyImpact?.Invoke(velBefore.Length());
            _wallContact = wall;
        }

        /// <summary>
        /// Unity TDU2VehicleManager.SpawnCarExact: genau diesen Datensatz laden, auch wenn er nicht in <see cref="Available"/> steht
        /// (z.B. Rennschul-Sondermodelle, die die Fahrzeugbank des Serienautos teilen). false = es lädt gerade ein anderes Auto.
        /// </summary>
        public bool SpawnCarExact(CarPhysics car, Vector3 game, Vector3 forwardGame)
        {
            if (!Ready || car == null) return true;
            if (Available.Contains(car)) { if (_loadTask != null) return false; SpawnCar(car.FileName, game, forwardGame); return true; }
            if (_loadTask != null || Database == null || _bnkDir == null) return false;
            if (Current != null || _model != null) Despawn();
            var before = _world.Origin;
            _world.ShiftOrigin(game);
            VehicleEffects.ShiftSkids(U.Mirror(_world.Origin - before));
            var fwd = U.ProjectOnPlane(U.Mirror(forwardGame), Vec.Up);
            if (fwd.LengthSquared() < 1e-3f) fwd = Vec.Forward;
            var pos = ToUnity(game);
            pos.Y = GroundY(pos) + 0.4f;
            _pendingPos = pos; _pendingRot = U.LookRotation(Vector3.Normalize(fwd), Vec.Up); _pendingVel = Vector3.Zero;
            string bnk = _bnkDir; var db = Database; bool snd = LoadSound;
            if (_colorFromChoice) { _colorIndex = 0; _colorFromChoice = false; }
            int color = _colorIndex;
            _status = "Lade " + car.ModelName + " ...";
            _loadTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd, 0, 0));
            return true;
        }

        /// <summary>Bodenhöhe (Spielkoordinaten) wie Unity TDU2VehicleManager.GroundHeight: Kollision, sonst Höhenraster.</summary>
        public float GroundHeightGame(Vector3 game) => GroundY(ToUnity(game)) + _world.Origin.Y;

        /// <summary>Auto aufrichten (1,5 m angehoben, gleiche Blickrichtung) und anhalten.</summary>
        public void ResetUpright()
        {
            if (Current == null) return;
            var tr = Current.transform;
            var f = U.ProjectOnPlane(tr.forward, Vec.Up);
            var pos = tr.position; pos.Y = GroundY(pos) + 1.5f;
            Current.ResetVehicle(pos, U.LookRotation(f.LengthSquared() > 1e-3f ? Vector3.Normalize(f) : Vec.Forward, Vec.Up));
            SnapPose();
        }

        /// <summary>Auto zeichnen. <paramref name="gameToRender"/> = MapStreamer.RootMatrix (Spiel -> Render-Raum).</summary>
        /// <summary>Unity TDU2VehicleManager.MirrorReflections: Echtzeit-Reflexionssonde am eigenen Auto.</summary>
        public bool MirrorReflections = true;

        // Profiler: Teile von Render (VehicleManager.Render brauchte im Debug-Build ~4,8 ms)
        static readonly ProfilerMarker s_mSkids = new("VehicleEffects.RenderSkids", ProfilerCategory.Rendering);
        static readonly ProfilerMarker s_mTransforms = new("CarModel.UpdateTransforms", ProfilerCategory.Rendering);
        static readonly ProfilerMarker s_mModelDraw = new("CarModel.Draw", ProfilerCategory.Rendering);
        static readonly ProfilerMarker s_mDriver = new("Driver.Render (avatar)", ProfilerCategory.Rendering);
        static readonly ProfilerMarker s_mFxRender = new("VehicleFx.Render", ProfilerCategory.Rendering);
        static readonly ProfilerMarker s_mFlares = new("VehicleLights.RenderFlares", ProfilerCategory.Rendering);

        public void Render(Matrix4x4 gameToRender)
        {
            using (s_mSkids.Auto())
                VehicleEffects.RenderSkids(_r, UnityToRender(gameToRender));
            Paradise.Core.Render.RenderFx.ProbeOn = false;
            Paradise.Core.Render.RenderFx.ProbeStatic = false;   // fährt mit dem Auto: Seite für Seite laufend neu
            if (_model == null || Current == null) return;
            var tr = Current.VisualPose;
            // Unity-Lage gespiegelt in Spielkoordinaten (das Visual ist in der Engine nicht gespiegelt)
            var root = Matrix4x4.CreateFromQuaternion(U.Mirror(tr.rotation)) * Matrix4x4.CreateTranslation(U.Mirror(tr.position)) * Matrix4x4.CreateTranslation(_world.Origin) * gameToRender;
            // Unity MirrorReflections: Echtzeit-Reflexionssonde am Auto (lokal 0 / 1,1 / 0,2 m) für Lack und Scheiben
            if (MirrorReflections)
            {
                Paradise.Core.Render.RenderFx.ProbeOn = true;
                Paradise.Core.Render.RenderFx.ProbePos = Vector3.Transform(new Vector3(0f, 1.1f, 0.2f), root);
            }
            using (s_mTransforms.Auto())
                _model.UpdateTransforms(root);
            using (s_mModelDraw.Auto())
                _model.Draw(_r);
            using (s_mDriver.Auto())
                _driver?.Render(Vector3.Transform(Camera.position, UnityToRender(gameToRender)), Paradise.Core.World.WorldWeather.Inside);
            using (s_mFxRender.Auto())
                _fx?.Render(UnityToRender(gameToRender));
            using (s_mFlares.Auto())
                _lights?.RenderFlares(_r, tr, Camera.position, U.Rot(Camera.rotation, Vec.Up), UnityToRender(gameToRender));
        }

        public IEnumerable<string> DebugLines()
        {
            yield return _status;
            var v = Current;
            if (v != null)
            {
                string gear = v.Gear < 0 ? "R" : v.Gear == 0 ? "N" : v.Gear.ToString();
                yield return $"{v.SpeedKmh:0} km/h   Gang {gear}   {v.Rpm:0} U/min   Gas {v.Throttle:0.00} Bremse {v.Brake:0.00}   Lenkung {v.SteeringAngle:0.0}°   Fahrhilfe {v.Aids}   {(v.AutomaticGearbox ? "Automatik" : "Manuell")}";
                var sb = new System.Text.StringBuilder("Räder:");
                foreach (var w in v.Wheels) sb.Append($"  {w.Name} {(w.Grounded ? "" : "(Luft) ")}Fz {w.Fz / 1000f:0.0}kN Schlupf {w.SlipRatio:0.00}/{w.SlipAngle * Mathf.Rad2Deg:0}°");
                yield return sb.ToString();
            }
        }

        public void Dispose()
        {
            Despawn();
            _hud?.Dispose(); _hud = null;
            _minimap?.Dispose(); _minimap = null;
        }
    }
}