using Paradise.Core.Math;
using Paradise.Core.Online;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Verschmutzung der Karosserie wie im Spiel (DBCarDirtData, Fälle DirtSun / DirtRain / NoDirtRain):
    ///   abseits der Straße steigt die Schmutzstufe mit _bodydirtfactor je Sekunde (Sonne 0.003, Regen 0.02),
    ///   auf der Straße im Regen wäscht _watercleanfactor (0.003/s) sie wieder ab.
    /// Darstellung: common_car.bnk/dirt_scratch.2db (R) über die Lack-Materialien (TDU2/Car: _DirtLevel).
    /// Stand je Fahrzeug (Datei) für die Sitzung gemerkt und im Spielstand gespeichert (SaveData.carDirt, Engine – Unity nur je
    /// Sitzung); die Garage zeigt ihn an den abgestellten Autos (<see cref="ApplyTo"/>). Portiert aus TDU2CarDirt (Unity).
    /// </summary>
    public sealed class CarDirt
    {
        public float Level;
        public float Scratches;
        /// <summary>Anzahl Schmutzstufen (0 = stufenlos)</summary>
        public int Steps = 0;
        /// <summary>Regen: schneller schmutzig abseits der Straße, sauber auf der Straße</summary>
        public bool Raining;
        /// <summary>Faktor auf die Original-Raten (1 = wie im Spiel)</summary>
        public float RateScale = 1f;
        public Vector4 DryColor = new Vector4(0.45f, 0.38f, 0.28f, 1f);
        public Vector4 WetColor = new Vector4(0.26f, 0.21f, 0.15f, 1f);

        // DBCarDirtData (db_data.cpr): _bodydirtfactor / _watercleanfactor
        const float DirtSun = 0.003f, DirtRain = 0.02f, CleanRain = 0.003f;

        static readonly Dictionary<string, float> Saved = new Dictionary<string, float>();

        Vehicle? _veh;
        CarModel? _model;
        string? _key;
        float _shown = -1f, _shownScr = -1f, _written = -1f, _writtenScr = -1f;
        bool _wetShown;
        int _diagStep = -1; bool _diagOff;

        public void Setup(Vehicle veh, CarModel? model)
        {
            _veh = veh; _model = model;
            _key = KeyOf(veh?.Car);
            if (_key != null && Saved.TryGetValue(_key, out var l)) { Level = l; Scratches = SavedScr.TryGetValue(_key, out var s) ? s : 0f; }
            else if (FromSave(veh?.Car?.FileName, out var lv, out var sc)) { Level = lv; Scratches = sc; if (_key != null) { Saved[_key] = lv; SavedScr[_key] = sc; } }
            _written = Level; _writtenScr = Scratches;
            Apply(true);
        }

        /// <summary>Mitspieler-Auto: Stand kommt aus dem Netz, keine eigene Berechnung.</summary>
        public void SetExternal(float level, float scratches, bool wet)
        {
            Level = Mathf.Clamp01(level); Scratches = Mathf.Clamp01(scratches); Raining = wet;
            Apply(false);
        }

        static readonly Dictionary<string, float> SavedScr = new Dictionary<string, float>();

        static string? KeyOf(CarPhysics? car) => car != null ? car.FileName + "#" + car.Id : null;

        /// <summary>Gemerkte Schmutzstufe eines Fahrzeugs (0 = sauber/unbekannt) – Sitzung, sonst Spielstand.</summary>
        public static float GetSaved(CarPhysics? car)
        {
            var k = KeyOf(car);
            if (k != null && Saved.TryGetValue(k, out var l)) return l;
            return FromSave(car?.FileName, out var lv, out _) ? lv : 0f;
        }

        /// <summary>Gemerkte Kratzer eines Fahrzeugs (0..1).</summary>
        public static float GetSavedScratches(CarPhysics? car)
        {
            var k = KeyOf(car);
            if (k != null && SavedScr.TryGetValue(k, out var s)) return s;
            return FromSave(car?.FileName, out _, out var sc) ? sc : 0f;
        }

        /// <summary>Schmutzstufe eines Fahrzeugs setzen (z.B. Waschanlage: 0) – auch im Spielstand.</summary>
        public static void SetSaved(CarPhysics? car, float level)
        {
            var k = KeyOf(car);
            if (k == null) return;
            Saved[k] = Mathf.Clamp01(level);
            ToSave(car!.FileName, Saved[k], SavedScr.TryGetValue(k, out var s) ? s : 0f);
        }

        /// <summary>Spielstand (SaveData.carDirt) lesen.</summary>
        static bool FromSave(string? file, out float level, out float scratches)
        {
            level = scratches = 0f;
            var list = TDU2Session.Save?.carDirt;
            if (string.IsNullOrEmpty(file) || list == null) return false;
            foreach (var e in list)
                if (e != null && string.Equals(e.car, file, StringComparison.OrdinalIgnoreCase)) { level = Mathf.Clamp01(e.level); scratches = Mathf.Clamp01(e.scratches); return true; }
            return false;
        }

        /// <summary>Spielstand (SaveData.carDirt) schreiben; gespeichert wird mit dem übrigen Spielstand.</summary>
        static void ToSave(string? file, float level, float scratches)
        {
            var sv = TDU2Session.Save;
            if (string.IsNullOrEmpty(file) || sv == null) return;
            sv.carDirt ??= new List<DirtEntry>();
            foreach (var e in sv.carDirt)
                if (e != null && string.Equals(e.car, file, StringComparison.OrdinalIgnoreCase)) { e.level = level; e.scratches = scratches; return; }
            sv.carDirt.Add(new DirtEntry { car = file, level = level, scratches = scratches });
        }

        /// <summary>Gespeicherten Schmutz an einem abgestellten Auto zeigen (Garage), ohne eigene Berechnung.</summary>
        public static void ApplyTo(CarModel? model, CarPhysics? car)
        {
            if (model == null || car == null) return;
            float lvl = GetSaved(car), scr = GetSavedScratches(car);
            var col = new CarDirt().DryColor;
            foreach (var m in model.PaintMaterials)
            {
                if (m == null || !m.IsCarShader) continue;
                m.Desc.DirtLevel = lvl;
                m.Desc.ScratchLevel = scr;
                m.Desc.DirtColor = col;
                model.ApplyMaterial(m);
            }
        }

        /// <summary>Auto waschen (Stufe 0).</summary>
        public void Wash() { Level = 0f; Scratches = 0f; Apply(true); Store(true); }

        /// <summary>Stand merken (Sitzung) und bei Änderung in den Spielstand schreiben.</summary>
        void Store(bool force)
        {
            if (_key == null) return;
            Saved[_key] = Level; SavedScr[_key] = Scratches;
            if (!force && System.Math.Abs(Level - _written) < 0.01f && System.Math.Abs(Scratches - _writtenScr) < 0.01f) return;
            _written = Level; _writtenScr = Scratches;
            ToSave(_veh?.Car?.FileName, Level, Scratches);
        }

        public void FullDirt() { Level = 1f; Apply(true); Store(true); }

        /// <summary>Unity Update.</summary>
        public void Update(float dt)
        {
            if (_veh == null) return;
            if (dt > 0f && _veh.AnyWheelGrounded)
            {
                float speed = Mathf.Abs(_veh.SpeedKmh);
                int off = 0, n = 0;
                foreach (var w in _veh.Wheels)
                {
                    if (!w.Grounded) continue;
                    n++;
                    if (w.Surface != 0 || IsOffroad(w.GroundCollider)) off++;
                }
                float moving = Mathf.Clamp01(speed / 40f);
                if (n > 0 && off > 0)
                    Level += (Raining ? DirtRain : DirtSun) * RateScale * moving * off / n * dt;
                else if (Raining)
                    Level -= CleanRain * RateScale * dt;
                Level = Mathf.Clamp01(Level);
                // s316 Prüf-Log: je 5 % Dreckstufe bzw. Wechsel Gelände/Straße einmal
                int step = (int)(Level * 20f);
                bool offNow = n > 0 && off > 0;
                if (step != _diagStep || offNow != _diagOff)
                {
                    _diagStep = step; _diagOff = offNow;
                }
            }
            Apply(false);
            Store(false);
        }

        /// <summary>Gelände = Terrain-Mesh des Streamers (Straßen liegen als eigene Sektor-Meshes darüber).</summary>
        static bool IsOffroad(Physics.StaticCollider? c)
            => VehicleManager.IsTerrain(c);

        void Apply(bool force)
        {
            if (_model == null) return;
            float lvl = Steps > 0 ? Mathf.Ceil(Level * Steps - 0.001f) / Steps : Level;
            if (!force && Mathf.Abs(lvl - _shown) < 0.002f && Mathf.Abs(Scratches - _shownScr) < 0.002f && _wetShown == Raining) return;
            _shown = lvl; _shownScr = Scratches; _wetShown = Raining;
            var col = Raining ? WetColor : DryColor;
            foreach (var m in _model.PaintMaterials)
            {
                if (m == null || !m.IsCarShader) continue;
                m.Desc.DirtLevel = lvl;
                m.Desc.ScratchLevel = Scratches;
                m.Desc.DirtColor = col;
                _model.ApplyMaterial(m);
            }
        }
    }
}