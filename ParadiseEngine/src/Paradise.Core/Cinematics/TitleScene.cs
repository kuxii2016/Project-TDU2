#nullable disable
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Core.Cinematics
{
    /// <summary>
    /// 3D background of the “Press Start” screen as in the original:
    /// one of the “Press Start” cinematic scenes from the island (islands/&lt;island&gt;/scenarics/press_start.bnk).
    ///   - entities.xmb: Entity__Cinematic without “C_LIFE” (random; -FORCELOGINCINEINDEX n overrides it); the last 4 digits
    ///     of the name determine the .cin file (“C_01_3332” -> *_3332.cin); Entity__CinCar CIN_CAR_xx -> id_car -> DBCarPhysicsData
    ///   - .cin: Shots via a .sce (cameras, car CIN_CAR_xx), “weat:” / “wmod:” messages per cut
    ///   - Weather: fx/cinematic_weathers.xmb (+ cinematic_modifiers.xmb) via <see cref="WorldWeather.Override"/>
    /// The island is streamed around the cinema camera. Loop over the duration of the scene.
    /// </summary>
    public sealed class TitleScene
    {
        /// <summary>Läuft gerade ein Titel-Kino?</summary>
        public static bool Playing => _inst != null && _inst._loaded;
        static TitleScene _inst;

        VehicleManager _mgr;
        MapStreamer _streamer;
        IRenderer _r;
        CinData _cin;
        readonly List<CinScene> _scenes = new List<CinScene>();
        CinObject _carObj;
        string _cinName = "";
        int _index = -1, _count;
        float _time;
        bool _loaded, _started;
        float _clock, _waitSince;
        Task<CarAssets> _carTask;
        CarPhysics _car;
        CarModel _carModel;
        CarAssets _carAssets;
        WheelSpin _wheelSpin;
        bool _carGrounded;
        float _groundOffset;
        int _carId = -1;
        bool _carResolved, _carFallbackTried;
        string _weather = "", _modifier = "";
        WeatherData _weathers;
        Vector3 _carPos;
        Quaternion _carRot = Quaternion.Identity;
        readonly List<(CarNode hub, float radius)> _hubs = new List<(CarNode, float)>();
        static readonly Random _rnd = new Random();

        /// <summary>Kamera des Kinos (Spielraum) – gilt nach <see cref="Update"/>, wenn es true lieferte.</summary>
        public Vector3 CameraGame { get; private set; }
        public Vector3 CameraForward { get; private set; } = Vector3.UnitZ;
        public Vector3 CameraUp { get; private set; } = Vector3.UnitY;
        public float CameraFov { get; private set; } = 60f;

        public static TitleScene Current => _inst;

        /// <summary>Titel-Kino starten (cinematicIndex 0: Zufall wie im Original).</summary>
        public static TitleScene Begin(VehicleManager mgr, MapStreamer streamer, IRenderer r, int cinematicIndex = -1)
        {
            if (mgr == null || streamer == null) return null;
            _inst ??= new TitleScene();
            _inst._mgr = mgr; _inst._streamer = streamer; _inst._r = r;
            if (!_inst.Load(cinematicIndex)) { _inst.Unload(); return null; }
            return _inst;
        }

        /// <summary>Titel-Kino beenden (Wetter zurück, Auto weg).</summary>
        public static void End()
        {
            if (_inst == null) return;
            _inst.Unload();
            _inst = null;
        }

        public static void Next()
        {
            if (_inst == null || _inst._count <= 0) return;
            int i = (_inst._index + 1) % _inst._count;
            _inst.Unload();
            if (!_inst.Load(i)) _inst.Unload();
        }

        string IslandDir => _streamer?.Index?.IslandDir;

        bool Load(int cinematicIndex)
        {
            Unload();
            string islandDir = IslandDir;
            if (string.IsNullOrEmpty(islandDir)) return false;
            string bankPath = Path.Combine(islandDir, "scenarics", "press_start.bnk");
            if (!GameFiles.Exists(bankPath))
            {
                Log.Warn("Cinematic: " + bankPath + " missing");
                return false;
            }
            try
            {
                var bank = BnkArchive.Open(bankPath);
                var ent = bank.Find("entities.xmb");
                if (ent == null) return false;
                var root = XmbFile.Parse(bank.Read(ent));
                var et = root.Get("ExportType_Entity");
                var names = new List<string>();
                var cinCars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (et != null)
                {
                    var arr = et.Get("ArrayOf_Entity__Cinematic");
                    if (arr != null) foreach (var it in arr.Items) { var n = it.S("__Name", ""); if (n.IndexOf("C_LIFE", StringComparison.OrdinalIgnoreCase) < 0) names.Add(n); }
                    var cars = et.Get("ArrayOf_Entity__CinCar");
                    if (cars != null) foreach (var it in cars.Items) cinCars[it.S("__Name", "")] = it.I("id_car", -1);
                }
                if (names.Count == 0) { 
                    Log.Warn("Title Cinema: No “Press Start” Theaters"); return false; }
                int idx = cinematicIndex >= 0 && cinematicIndex < names.Count ? cinematicIndex : _rnd.Next(names.Count);
                int forced = ForcedIndex();
                if (cinematicIndex < 0 && forced >= 0 && forced < names.Count) idx = forced;
                _index = idx; _count = names.Count; _cinName = names[idx];
                string digits = _cinName.Length >= 4 ? _cinName.Substring(_cinName.Length - 4) : "";
                BnkEntry cinEntry = null;
                foreach (var e in bank.All(".cin"))
                {
                    var stem = Path.GetFileNameWithoutExtension(e.Name);
                    if (stem.EndsWith("_" + digits, StringComparison.OrdinalIgnoreCase)) { cinEntry = e; break; }
                }
                _cin = cinEntry != null ? CinData.Parse(bank.Read(cinEntry), cinEntry.Name) : null;
                if (_cin == null) { 
                    Log.Warn("Title Cinema: .cin for " + _cinName + " is missing"); return false; }
                for (int s = 0; s < _cin.SceneReferences.Count; s++)
                {
                    string file = _cin.SceneFile(s);
                    var se = file != null ? bank.Find(file + ".sce") : null;
                    _scenes.Add(se != null ? CinScene.Parse(bank.Read(se), se.Name) : null);
                    if (se == null) 
                        Log.Warn("Title Cinema: Scene " + file + " is missing");
                }
                if (_scenes.Count == 0 || _scenes[0] == null || _scenes[0].Objects.Count == 0) return false;
                // Auto der Szene: Darsteller "CIN_CAR_xx" -> Entity__CinCar id_car -> DBCarPhysicsData
                foreach (var sc in _scenes)
                {
                    if (sc == null) continue;
                    foreach (var o in sc.Objects)
                        if (o.ClassHash == CinIds.ClassActor && o.Name.StartsWith("CIN_CAR", StringComparison.OrdinalIgnoreCase)) { _carObj = o; break; }
                    if (_carObj != null) break;
                }
                _carId = _carObj != null && cinCars.TryGetValue(_carObj.Name, out int idCar) ? idCar : -1;
                _carResolved = false; _carFallbackTried = false;
                LoadWeathers(islandDir);
            }
            catch (Exception ex)
            {
                Log.Error("Title Cinema: " + ex.Message);
                return false;
            }
            _time = 0f; _started = false; _waitSince = _clock; _carGrounded = false;
            _loaded = true;
            ResolveCar();
            return true;
        }

        /// <summary>Auto der Szene laden, sobald die Fahrzeugdatenbank da ist; fehlt es, ein vorhandenes Auto (im Original steht immer eins da).</summary>
        void ResolveCar()
        {
            if (_carResolved || _carObj == null || _mgr?.Database == null) return;
            if (_mgr.Available.Count == 0 && _mgr.Database.Cars.Count > 0) return;
            _carResolved = true;
            _car = _carId >= 0 ? _mgr.Database.Cars.Find(c => c.Id == _carId) : null;
            if (_car != null && _mgr.Available.Count > 0 && !_mgr.Available.Exists(c => c.Id == _car.Id))
            {
                Log.Info($"Title: {_car.ModelName} ({_carId}) not installed – Replacement");
                _car = null;
            }
            if (_car == null) { _car = FallbackCar(); _carFallbackTried = true; }
            StartCarLoad();
        }

        CarPhysics FallbackCar()
        {
            var pool = _mgr.Available.FindAll(c => !c.IsBike && (_car == null || c.Id != _car.Id));
            if (pool.Count == 0) pool = _mgr.Available;
            return pool.Count > 0 ? pool[_rnd.Next(pool.Count)] : null;
        }

        void StartCarLoad()
        {
            if (_car == null) return;
            string bnk = _mgr.BnkDir; var db = _mgr.Database; var car = _car;
            _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, 0, false));
        }

        static int ForcedIndex()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-FORCELOGINCINEINDEX", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int n)) return n;
            return -1;
        }

        void LoadWeathers(string islandDir)
        {
            _weathers = null;
            string path = Path.Combine(islandDir, "fx", "cinematic_weathers.xmb");
            try { if (GameFiles.Exists(path)) _weathers = WeatherData.Load(path); }
            catch (Exception ex) { 
                Log.Error("cinematic_weathers.xmb: " + ex.Message); }
            try { _weathers?.LoadModifiers(Path.Combine(islandDir, "fx", "cinematic_modifiers.xmb")); }
            catch (Exception ex) { 
                Log.Error("cinematic_modifiers.xmb: " + ex.Message); }
        }

        void Unload()
        {
            _loaded = false;
            _cin = null; _scenes.Clear(); _carObj = null; _car = null; _carId = -1; _carResolved = false;
            _carModel?.Destroy(); _carModel = null;
            _carAssets?.DestroyTextures(); _carAssets = null;
            _wheelSpin = null;
            _carTask = null; _hubs.Clear(); _carGrounded = false;
            WorldWeather.Override = null;
            _weather = ""; _modifier = "";
            RenderFx.ProbeOn = false;
            RenderFx.ProbeFar = 600f;
        }

        public bool Update(float dt)
        {
            _clock += dt;
            if (!_loaded || _cin == null || _streamer == null) return false;
            bool cam;
            if (!_started)
            {
                cam = Apply(0f);
                if ((cam && _streamer.ReadyAround(_streamer.GameToRender(CameraGame), 400f)) || _clock - _waitSince > 10f) _started = true;
            }
            else
            {
                _time += dt;
                if (_cin.Duration > 0f && _time >= _cin.Duration) _time %= _cin.Duration;
                cam = Apply(_time);
            }
            UpdateCar();
            return cam;
        }

        bool Apply(float t)
        {
            bool ok = _cin.Camera(_scenes, t, out var gp, out var gr, out float fov);
            if (ok)
            {
                CameraGame = gp;
                CameraForward = Vector3.Transform(Vector3.UnitZ, gr);
                CameraUp = Vector3.Transform(Vector3.UnitY, gr);
                if (fov > 1f && fov < 170f) CameraFov = fov;
            }
            // Wetter: "weat:<name>" und danach "wmod:<modifier>" je Schnitt
            string w = _cin.LastMessage("weat:", t, out float wt) ?? "";
            string m = _cin.LastMessage("wmod:", t, out float mt) ?? "";
            if (mt < wt) m = "";
            if (w != _weather || m != _modifier)
            {
                _weather = w; _modifier = m;
                WorldWeather.Override = _weathers != null && !string.IsNullOrEmpty(w) ? _weathers.Find(w, m) : null;
            }
            return ok;
        }

        void UpdateCar()
        {
            if (!_carResolved) ResolveCar();
            if (_carTask != null && _carTask.IsCompleted)
            {
                var task = _carTask; _carTask = null;
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null && _loaded)
                {
                    try
                    {
                        _carAssets = task.Result;
                        _carModel = CarModel.Build(_carAssets, _r);
                        _wheelSpin = WheelSpin.Attach(_carModel, _carAssets);
                        _hubs.Clear(); _carGrounded = false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Title: Movies, Cars: " + ex.Message);
                        _carModel?.Destroy(); _carModel = null;
                        _carAssets?.DestroyTextures(); _carAssets = null;
                    }
                }
                else if (task.Exception != null) Log.Error("Title: Movies, Cars: " + task.Exception.GetBaseException().Message);
                // Laden fehlgeschlagen: einmal mit einem vorhandenen Auto
                if (_carModel == null && _loaded && !_carFallbackTried)
                {
                    _carFallbackTried = true;
                    _car = FallbackCar();
                    StartCarLoad();
                }
            }
            if (_carModel == null || _carObj == null || _scenes.Count == 0 || _scenes[0] == null) return;
            var sc = _scenes[0];
            float st = _started ? _time : 0f;
            _carPos = sc.ToGame(_carObj.SamplePosition(st));
            _carRot = sc.OriginRotation * _carObj.SampleRotation(st);
            _wheelSpin?.Update(_carPos, Vector3.Transform(Vector3.UnitZ, _carRot));   // Räder drehen mit der Fahrt (Unity TDU2WheelSpin)
        }

        /// <summary>Auto der Szene zeichnen (Spielraum -> Render über gameToRender).</summary>
        public void Render(Matrix4x4 gameToRender)
        {
            if (!_loaded) return;
            RenderFx.ProbeOn = true;
            RenderFx.ProbeStatic = false;
            RenderFx.ProbeFar = 2000f;
            RenderFx.ProbePos = Vector3.Transform(CameraGame, gameToRender);
            if (_carModel == null) return;
            var pos = _carPos;
            // erst ohne Bodenversatz, dann die Naben auf den Boden (FitOnGround)
            _carModel.UpdateTransforms(Matrix4x4.CreateFromQuaternion(_carRot) * Matrix4x4.CreateTranslation(pos) * gameToRender);
            if (FitOnGround(gameToRender))
                _carModel.UpdateTransforms(Matrix4x4.CreateFromQuaternion(_carRot) * Matrix4x4.CreateTranslation(pos + Vector3.UnitY * _groundOffset) * gameToRender);
            _carModel.Draw(_r);
        }

        /// <summary>
        /// Das Kino gibt nur die Lage über der Straße jede Radnabe auf Straße/Gelände
        /// + Reifenradius setzen (Mittelwert, jedes Bild; weich nachgeführt, bei Schnitten springen).
        /// </summary>
        bool FitOnGround(Matrix4x4 gameToRender)
        {
            if (_hubs.Count == 0)
                foreach (var n in new[] { "FL", "FR", "RL", "RR" })
                {
                    var hub = _carModel.Find("DISC_" + n) ?? _carModel.Find("D_RIM_" + n) ?? _carModel.Find("FORK_" + n);
                    if (hub != null) _hubs.Add((hub, _carModel.TireRadiusFx(n[0] == 'F')));
                }
            if (_hubs.Count == 0) return false;
            if (!Matrix4x4.Invert(gameToRender, out var renderToGame)) return false;
            float sum = 0f; int count = 0;
            foreach (var (hub, r) in _hubs)
            {
                var hp = Vector3.Transform(hub.World.Translation, renderToGame);
                if (GroundAt(hp, out float surf)) { sum += surf + r - hp.Y; count++; }
            }
            if (count == 0) return _carGrounded;
            float target = sum / count;
            if (!_carGrounded || Mathf.Abs(target - _groundOffset) > 1.5f) _groundOffset = target;
            else _groundOffset += (target - _groundOffset) * (1f - MathF.Exp(-0.016f * 12f));
            _carGrounded = true;
            return true;
        }

        /// <summary>Oberfläche unter einem Punkt: Straßen-/Objekt-Collider (bis knapp über dem Gelände), sonst Höhenraster.</summary>
        bool GroundAt(Vector3 p, out float y)
        {
            bool terrain = _streamer.TryGetGroundHeight(p, out float th);
            float top = Mathf.Max(p.Y, terrain ? th : p.Y) + 3f;
            if (_streamer.RaycastDown(new Vector3(p.X, top, p.Z), 0f, top - p.Y + 20f, out float hy) && (!terrain || hy >= th - 0.5f)) { y = hy; return true; }
            y = th;
            return terrain;
        }
    }
}