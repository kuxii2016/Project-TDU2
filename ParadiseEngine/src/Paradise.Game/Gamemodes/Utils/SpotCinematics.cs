using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Cinematics;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Cinematics einer Spot-Bank (Interieur interior/i*.bnk oder Exterior islands/&lt;insel&gt;/exterior/e*.bnk), gemeinsam für die
/// Spielmodi mit Kino-Abläufen (GAME_MODE_AIRPORT, GAME_MODE_CAR_WASH). Port des Kerns von Unity TDU2StoryScene:
///   - Definitionen: entities.xmb, ArrayOf_Entity__Cinematic (C_*) -> .cin (Dateiname "&lt;bank&gt;_sc_M_&lt;Name ohne C_&gt;[_…].cin") + ihre .sce
///   - eine Haupt-Cinematic mit Kamera (<see cref="CinData.Camera"/>) und beliebig viele Hintergrund-Cinematics (Schleife, ohne Kamera)
///   - Darsteller: Spieler-Avatar ("Avatar", Clip "-m"/"-w" nach Geschlecht), Figuren (Entity__PNJ über __Hc, DBPNJ ConfigName),
///     Spielerauto ("PLAYER_CAR", geladen über <see cref="LoadCar"/>)
///   - Ende-Ereignisse (<see cref="TakeEndEvents"/>), wie im Exe das Ereignis 9 "Kino fertig" an den Spielmodus.
/// Rahmen: <see cref="Frame"/> = Lage der Bank in der Welt (Spielraum), wie Interieur und Exterior: Drehung um Y = Spot.Value1,
/// Position = Spot.Position (Interieur zusätzlich InteriorAltitude).
///   - Requisiten (Unity FixProps/AnimateProps): Platzierungen der Bank, deren Entität ein Darsteller mit Positionsspur spielt (z.B.
///     CF_Plane), werden als eigene Szene gebaut und während des Kinos bewegt (Lage = Spur * Wurzel⁻¹); die gestreamte Ruhe-Kopie ist
///     solange aus.
///   - Töne (<see cref="CinematicAudio"/>): Fx-Ereignisse der .cin und Fx-Spuren der .sce im Intervall (Prev, Time] (Musik der Waschanlage
///     M_WASHING_INTRO/M_WASHING/M_WASHING_OUTRO steckt als Fx-Ereignis in C_WASHING/C_INCAR/C_OUTRO).
/// Noch nicht portiert (CHECKLIST): Partikel-Fx der .cin (Dampf, Wasserstrahl), Überblendungen.
/// </summary>
internal sealed class SpotCinematics : IDisposable
{
    internal sealed class Def
    {
        public string Name = "";
        public CinData Cin = null!;
        public readonly List<CinScene?> Scenes = new();
    }

    sealed class Playing
    {
        public Def Def = null!;
        public float Time, Prev = -1f;
        public bool Loop, Ended, Camera;
        public readonly List<AudioVoice> Voices = new();   // gestartete Kino-Töne (Musik, Fx, Stimmen)
    }

    sealed class Pnj { public ulong Hc; public string Config = ""; public AvatarModel? Avatar; public bool Shown; public Matrix4x4 Pose; public AnmClip? Clip; public float T; }

    readonly IRenderer _r;
    readonly BnkArchive _bank;
    readonly Dictionary<string, Def> _defs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AnmClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Pnj> _pnjs = new();
    readonly List<Playing> _background = new();
    readonly List<string> _ended = new();
    Playing? _main;

    // Kino-Töne (Fx-Ereignisse der .cin, Fx-Spuren der .sce): im Hintergrund geladen
    Task<CinematicAudio>? _audioTask;
    CinematicAudio? _audio;

    // Spieler-Avatar
    AvatarModel? _player;
    bool _playerShown; Matrix4x4 _playerPose; AnmClip? _playerClip; float _playerT;
    int _sex;

    // Spielerauto ("PLAYER_CAR")
    CarModel? _car;
    CarAssets? _carAssets;
    Task<CarAssets>? _carTask;
    bool _carShown; Matrix4x4 _carPose;

    // Requisiten
    sealed class Prop
    {
        public ulong Hc;
        public MapStreamer.SceneHandle Handle = null!;
        public Matrix4x4 RootInv = Matrix4x4.Identity;
        public readonly List<(RenderObject O, Matrix4x4 Base, Vector3 Min, Vector3 Max, Vector3 Center)> Parts = new();
        public readonly List<RenderObject> Streamed = new();   // gestreamte Ruhe-Kopie (Spot-Exterior), während des Kinos aus
        public bool Moving;
        public Matrix4x4 Pose;
        // Skelett der Requisite (.XSB gleichen Namens wie die .3DD): die Bewegung steckt in der Wurzel-Spur des Clips
        // (cf_plane_takeoff.anm / cf_landed.anm: Spur 0 = Wurzelknoten mit Rotation + Position)
        public XsbSkeleton? Skel;
        public int Root = -1;
        public Matrix4x4 RestRootInv = Matrix4x4.Identity;
        public Matrix4x4 Anim = Matrix4x4.Identity;
    }
    readonly List<Prop> _props = new();
    MapStreamer? _st;
    Task<BankModel>? _propTask;
    string? _spotCode;

    /// <summary>Bank -> Spielraum (Lage des Spots).</summary>
    public Matrix4x4 Frame = Matrix4x4.Identity;
    public string BankName { get; }
    public IReadOnlyDictionary<string, Def> Definitions => _defs;
    /// <summary>__Hc der Figuren, die gerade von einer Cinematic bewegt werden (InteriorNpcs blendet ihre Laufmodus-Pose dann aus).</summary>
    public readonly HashSet<ulong> ActiveActors = new();

    public SpotCinematics(IRenderer r, BnkArchive bank)
    {
        _r = r; _bank = bank;
        BankName = Path.GetFileNameWithoutExtension(bank.FilePath);
        try { Load(); }
        catch (Exception ex) { Log.Error("Cinematics " + BankName + ": " + ex.Message); }
        string lang = FlashHost.Language; bool woman = AvatarConfigs.Local.Sex == 1;
        _audioTask = Task.Run(() => CinematicAudio.Load(bank, lang, woman));
    }

    public static SpotCinematics? Open(IRenderer r, string? bankPath)
    {
        if (string.IsNullOrEmpty(bankPath)) return null;
        try { return new SpotCinematics(r, BnkArchive.Open(bankPath)); }
        catch (Exception ex) { Log.Error("Cinematics " + bankPath + ": " + ex.Message); return null; }
    }

    // ================================================================== Laden
    void Load()
    {
        var ent = _bank.Find("entities.xmb");
        if (ent == null) { Log.Warn("Cinematics " + BankName + ": entities.xmb fehlt"); return; }
        var root = XmbFile.Parse(_bank.Read(ent));
        var et = root.Get("ExportType_Entity");
        if (et == null) return;
        var names = new List<string>();
        var cine = et.Get("ArrayOf_Entity__Cinematic");
        if (cine != null) foreach (var it in cine.Items) { var n = it.S("__Name", "") ?? ""; if (n.Length > 0) names.Add(n); }
        // Figuren (Entity__PNJ mit ConfigName = DBPNJ._configid)
        foreach (var arr in et.Children)
        {
            if (!arr.IsArray) continue;
            foreach (var it in arr.Items)
            {
                string cfg = it.S("ConfigName", "") ?? "";
                ulong hc = it.U("__Hc");
                if (cfg.Length > 0 && hc != 0) _pnjs.Add(new Pnj { Hc = hc, Config = cfg });
            }
        }
        // .cin je Definition: genauester Name gewinnt (C_OUTRO -> *_M_OUTRO.cin, nicht *_M_OUTRO_FERRARI.cin)
        var cins = new List<BnkEntry>(_bank.All(".cin"));
        foreach (var n in names)
        {
            string rest = n.StartsWith("C_", StringComparison.OrdinalIgnoreCase) ? n[2..] : n;
            BnkEntry? hit = null;
            foreach (var e in cins)
            {
                string stem = Path.GetFileNameWithoutExtension(e.Name);
                if (stem.EndsWith("_M_" + rest, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
            }
            if (hit == null)
                foreach (var e in cins)
                {
                    string stem = Path.GetFileNameWithoutExtension(e.Name);
                    int i = stem.IndexOf("_M_" + rest + "_", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    // Rest nach dem Namen darf keine andere Definition sein (M_OUTRO_FERRARI gehört zu C_OUTRO_FERRARI)
                    string tail = stem[(i + 3 + rest.Length + 1)..];
                    if (names.Exists(o => !ReferenceEquals(o, n) && string.Equals(o, "C_" + rest + "_" + tail, StringComparison.OrdinalIgnoreCase))) continue;
                    hit = e; break;
                }
            if (hit == null) { Log.Debug("Cinematics " + BankName + ": keine .cin zu " + n); continue; }
            var cin = CinData.Parse(_bank.Read(hit), hit.Name);
            if (cin == null) continue;
            var def = new Def { Name = n, Cin = cin };
            for (int s = 0; s < cin.SceneReferences.Count; s++)
            {
                string? file = cin.SceneFile(s);
                var se = file != null ? _bank.Find(file + ".sce") : null;
                def.Scenes.Add(se != null ? CinScene.Parse(_bank.Read(se), se.Name) : null);
                if (se == null) Log.Warn("Cinematics " + BankName + ": Szene " + file + " zu " + n + " fehlt");
            }
            _defs[n] = def;
        }
        Log.Info($"Cinematics {BankName}: {_defs.Count} von {names.Count} ({string.Join(", ", _defs.Keys)}), {_pnjs.Count} Figur(en)");
    }

    // ================================================================== Ablauf
    public Def? Find(string name) => _defs.TryGetValue(name, out var d) ? d : null;
    public string? MainName => _main?.Def.Name;
    /// <summary>Zeit der Haupt-Cinematic (s), z.B. für den Cutscene Tester.</summary>
    public float MainTime => _main?.Time ?? 0f;
    /// <summary>Spieler-Avatar als Darsteller "Avatar" zeigen (Cutscene Tester: "Cast player avatar").</summary>
    public bool CastPlayer = true;
    public bool MainPlaying => _main != null && !_main.Ended;

    /// <summary>Haupt-Cinematic (mit Kamera) starten; false = nicht in der Bank.</summary>
    public bool PlayMain(string name, bool loop = false)
    {
        var d = Find(name);
        if (d == null) { Log.Warn("Cinematics " + BankName + ": " + name + " fehlt"); return false; }
        StopVoices(_main);
        _main = new Playing { Def = d, Loop = loop, Camera = true };
        Launch(_main);
        Log.Info($"Cinematic {name} ({d.Cin.Duration:0.0} s, {d.Cin.Shots.Count} Shots)");
        return true;
    }

    /// <summary>Hintergrund-Cinematic (Figuren in Schleife, ohne Kamera).</summary>
    public bool PlayBackground(string name, bool loop = true)
    {
        var d = Find(name);
        if (d == null) return false;
        foreach (var b in _background) if (ReferenceEquals(b.Def, d)) StopVoices(b);
        _background.RemoveAll(b => ReferenceEquals(b.Def, d));
        var p = new Playing { Def = d, Loop = loop };
        _background.Add(p);
        Launch(p);
        return true;
    }

    public void StopMain() { StopVoices(_main); _main = null; }
    public void StopBackground() { foreach (var b in _background) StopVoices(b); _background.Clear(); }

    /// <summary>Kino-Töne geladen (Spielmodi warten darauf im Ladebildschirm, sonst fehlen die Töne am Kinoanfang).</summary>
    public bool AudioReady => _audioTask == null || _audioTask.IsCompleted;
    public void SkipMain() { if (_main != null && !_main.Loop) _main.Time = _main.Def.Cin.Duration; }

    /// <summary>Seit dem letzten Aufruf beendete Cinematics (Namen).</summary>
    public List<string> TakeEndEvents() { var l = new List<string>(_ended); _ended.Clear(); return l; }

    /// <summary>Jedes Bild: Zeit, Darsteller.</summary>
    public void Update(float dt)
    {
        PollAudio();
        PollCar();
        PollProps();
        ActiveActors.Clear();
        _playerShown = false; _carShown = false;
        foreach (var p in _pnjs) p.Shown = false;
        foreach (var b in _background) Advance(b, dt);
        _background.RemoveAll(b => b.Ended);
        if (_main != null)
        {
            Advance(_main, dt);
            if (_main.Ended) _main = null;
        }
        foreach (var b in _background) Pose(b);
        if (_main != null) Pose(_main);
        AnimateProps();
        _player?.Update();
        foreach (var p in _pnjs) p.Avatar?.Update();
        if (_playerShown) _playerT += dt;
        foreach (var p in _pnjs) if (p.Shown) p.T += dt;
    }

    // Wie Unity TDU2StoryScene.Advance: Töne im Intervall (Prev, Time]; Schleifen-Neustart (Exe FUN_00b402f0) beginnt die Töne von
    // vorn, Ende stoppt sie.
    void Advance(Playing p, float dt)
    {
        float len = p.Def.Cin.Duration;
        if (len <= 0f) { p.Time += dt; if (!p.Loop) End(p); return; }
        float from = p.Prev, t = p.Time + dt;
        if (t > len - 0.001f)
        {
            if (!p.Loop)
            {
                t = len - 0.001f;
                UpdateFx(p, from, t);
                p.Time = len; p.Prev = t;
                StopVoices(p);
                End(p);
                return;
            }
            UpdateFx(p, from, len);
            t = t <= len ? 0.001f : t - len;
            StopVoices(p);
            UpdateFx(p, -1f, t);
        }
        else UpdateFx(p, from, t);
        p.Time = p.Prev = t;
    }

    void Launch(Playing p)
    {
        p.Time = 0f;
        UpdateFx(p, -1f, 0f);
        p.Prev = 0f;
    }

    void PollAudio()
    {
        if (_audioTask == null || !_audioTask.IsCompleted) return;
        if (_audioTask.Status == TaskStatus.RanToCompletion) _audio = _audioTask.Result;
        _audioTask = null;
    }

    static void StopVoices(Playing? p) { if (p != null) CinematicAudio.Stop(p.Voices); }

    /// <summary>Töne: Fx-Ereignisse des .cin und Fx-Spuren der Darsteller der aktuellen Szene (OpenTDU2 IntroCinematic::updateFx).</summary>
    void UpdateFx(Playing p, float from, float to)
    {
        if (_audio == null) PollAudio();
        if (_audio == null || !_audio.HasSounds) return;
        var cin = p.Def.Cin;
        try
        {
            Vector3? eye = Camera(out var e, out _, out _, out _) ? e : null;
            _audio.UpdateCin(cin, from, to, Frame, eye, p.Voices);
            var shot = cin.ShotAt(MathF.Max(to, 0f));
            if (shot == null || shot.Scene < 0 || shot.Scene >= p.Def.Scenes.Count || p.Def.Scenes[shot.Scene] == null) return;
            var prevShot = from >= 0f ? cin.ShotAt(from) : null;
            float st = shot.SceneTime(to);
            float prev = prevShot == shot && from >= 0f ? shot.SceneTime(from) : shot.CameraStart - 1e-4f;
            _audio.UpdateScene(p.Def.Scenes[shot.Scene]!, prev, st, Frame, eye, p.Voices);
        }
        catch (Exception ex) { Log.Warn("Kino-Töne " + p.Def.Name + ": " + ex.Message); }
    }

    void End(Playing p)
    {
        if (p.Ended) return;
        p.Ended = true;
        _ended.Add(p.Def.Name);
        Log.Info("Cinematic " + p.Def.Name + " zu Ende");
    }

    /// <summary>Darsteller der Szene des aktuellen Shots an ihre Lage und ihren Clip setzen.</summary>
    void Pose(Playing p)
    {
        if (!p.Def.Cin.SceneTime(p.Def.Scenes!, p.Time, out var sc, out float st) || sc == null) return;
        foreach (var o in sc.Objects)
        {
            if (o.IsCamera || !o.VisibleAt(st)) continue;
            var m = Matrix4x4.CreateFromQuaternion(sc.OriginRotation * o.SampleRotation(st)) * Matrix4x4.CreateTranslation(sc.ToGame(o.SamplePosition(st)));
            int refIdx = o.AnimationRefAt(st, out float keyTime);
            string? clipName = refIdx >= 0 ? sc.ReferenceName(refIdx) : null;
            if (string.Equals(o.Name, "Avatar", StringComparison.OrdinalIgnoreCase))
            {
                if (!CastPlayer) continue;
                EnsurePlayer();
                _playerShown = true; _playerPose = m;
                SetClip(ref _playerClip, ref _playerT, SexClip(clipName), st - keyTime);
            }
            else if (string.Equals(o.Name, "PLAYER_CAR", StringComparison.OrdinalIgnoreCase))
            {
                _carShown = true; _carPose = m;
            }
            else if (o.EntityHashcode != 0 && o.EntityHashcode != 0x6666666666666666UL)
            {
                var pnj = _pnjs.Find(x => x.Hc == o.EntityHashcode);
                if (pnj == null) continue;
                EnsurePnj(pnj);
                pnj.Shown = true; pnj.Pose = m;
                ActiveActors.Add(pnj.Hc);
                SetClip(ref pnj.Clip, ref pnj.T, clipName, st - keyTime);
            }
        }
    }

    void SetClip(ref AnmClip? clip, ref float t, string? name, float since)
    {
        var c = Clip(name);
        if (c == null) return;
        // Clip läuft ab seinem Schlüssel in der Szene (Szenenzeit - Schlüsselzeit)
        clip = c; t = MathF.Max(0f, since);
    }

    string? SexClip(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (_sex == 1 && name.EndsWith("-m", StringComparison.Ordinal)) return name[..^2] + "-w";
        if (_sex == 0 && name.EndsWith("-w", StringComparison.Ordinal)) return name[..^2] + "-m";
        return name;
    }

    AnmClip? Clip(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_clips.TryGetValue(name, out var c)) return c;
        c = CineScene.LoadClip(_bank, name) ?? AvatarLibrary.Clip(name);
        _clips[name] = c;
        return c;
    }

    void EnsurePlayer()
    {
        if (_player != null || !AvatarLibrary.Ready) return;
        var cfg = AvatarConfigs.Local;
        _sex = cfg.Sex;
        _player = AvatarModel.Create(_r, cfg);
    }

    void EnsurePnj(Pnj p)
    {
        if (p.Avatar != null || !AvatarLibrary.Ready) return;
        var cfg = AvatarLibrary.PnjConfig(p.Config);
        if (cfg == null) { Log.Debug("Cinematic-Figur ohne DBPNJ: " + p.Config); p.Config = ""; return; }
        p.Avatar = AvatarModel.Create(_r, cfg);
    }

    /// <summary>Innenräume: Avatare mit Innenlicht.</summary>
    public bool Indoor
    {
        set
        {
            if (_player != null) _player.Indoor = value;
            foreach (var p in _pnjs) if (p.Avatar != null) p.Avatar.Indoor = value;
            _indoor = value;
        }
    }
    bool _indoor;

    /// <summary>Geladenes Spielerauto (null solange es lädt).</summary>
    public CarModel? Car => _car;
    public bool CarLoading => _carTask != null;
    /// <summary>Lage des Spielerautos im Spielraum (Wurzel des Modells), wenn eine laufende Szene es zeigt.</summary>
    public Matrix4x4? CarPoseGame => _carShown ? _carPose * Frame : null;

    /// <summary>Spielerauto der Szene ("PLAYER_CAR") im Hintergrund laden (Farbe/Felgen wie gewählt).</summary>
    public void LoadCar(VehicleManager? vm, CarPhysics? car)
    {
        if (vm?.Database == null || car == null || _carTask != null || _car != null) return;
        int color = 0, rim = 0, interior = 0;
        if (CarChoices.TryGet(car.FileName, out var ch)) { color = ch.Color; rim = ch.Rim; interior = ch.Interior; }
        string bnk = vm.BnkDir; var db = vm.Database;
        _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, false, rim, interior));
    }

    void PollCar()
    {
        if (_carTask == null || !_carTask.IsCompleted) return;
        var t = _carTask; _carTask = null;
        if (t.Status != TaskStatus.RanToCompletion || t.Result == null) { Log.Warn("Cinematics " + BankName + ": Auto nicht geladen – " + t.Exception?.GetBaseException().Message); return; }
        try { _carAssets = t.Result; _car = CarModel.Build(_carAssets, _r); }
        catch (Exception ex) { Log.Error("Cinematics " + BankName + ": Auto – " + ex.Message); _car = null; _carAssets?.DestroyTextures(); _carAssets = null; }
    }

    // ================================================================== Requisiten (Unity TDU2StoryScene.FixProps / AnimateProps)
    /// <summary>Requisiten der Bank vorbereiten (Exterior-Bank des Spots; die gestreamte Kopie liegt im Knoten Spot_&lt;Code&gt;).</summary>
    public void AttachProps(MapStreamer st, string bankPath, string? spotCode)
    {
        if (_propTask != null || _props.Count > 0) return;
        _st = st; _spotCode = spotCode;
        _propTask = Task.Run(() => st.LoadSceneModel(bankPath));
    }

    void PollProps()
    {
        if (_propTask == null || !_propTask.IsCompleted || _st == null) return;
        var t = _propTask; _propTask = null;
        if (t.Status != TaskStatus.RanToCompletion || t.Result == null) { Log.Warn("Cinematics " + BankName + ": Requisiten – " + t.Exception?.GetBaseException().Message); return; }
        var model = t.Result;
        try { BuildProps(model); }
        catch (Exception ex) { Log.Error("Cinematics " + BankName + ": Requisiten – " + ex.Message); }
    }

    void BuildProps(BankModel model)
    {
        // Entitäten mit Platzierung (orient_pos_scale) und __Hc, die ein Darsteller (mit Positionsspur) einer Cinematic spielt
        var played = new HashSet<ulong>();
        foreach (var d in _defs.Values)
            foreach (var sc in d.Scenes)
                if (sc != null) foreach (var o in sc.Objects)
                        if (!o.IsCamera && o.EntityHashcode != 0 && o.Find(CinIds.TrackPosition) != null) played.Add(o.EntityHashcode);
        var ents = new List<(ulong hc, Matrix4x4 m)>();
        var ent = _bank.Find("entities.xmb");
        var et = ent != null ? XmbFile.Parse(_bank.Read(ent)).Get("ExportType_Entity") : null;
        if (et != null)
            foreach (var arr in et.Children)
            {
                if (!arr.IsArray) continue;
                foreach (var it in arr.Items)
                {
                    ulong hc = it.U("__Hc"); var ops = it.Get("orient_pos_scale");
                    if (hc != 0 && ops != null && played.Contains(hc)) ents.Add((hc, BankModel.EntityMatrix(ops)));
                }
            }
        if (ents.Count == 0) { model.Release(); return; }
        foreach (var set in model.AllObjectSets())
        {
            if (set?.Placements == null || set.Scene == null) continue;
            var rootInv = Matrix4x4.Identity;
            // Wurzelknoten wie im Original (IntroCinematic): erster Hierarchie-Knoten ohne Eltern
            foreach (var h in set.Scene.Hierarchy)
                if (h.ParentOffset == 0) { Matrix4x4.Invert(set.Scene.GetIns(h.InsOffset).Matrix, out rootInv); break; }
            foreach (var pm in set.Placements)
            {
                if (pm.IsIdentity) continue;
                int k = ents.FindIndex(e => Near(e.m, pm));
                if (k < 0) continue;
                var hc = ents[k].hc; ents.RemoveAt(k);
                // nur diese eine Requisite als eigene Szene (Weltmatrizen der Knoten, Eltern = Identität)
                var saved = set.Placements;
                var wrap = new BankModel { Path = model.Path, Kind = BankKind.Exterior, Bank = model.Bank, Objects = set };
                set.Placements = new List<Matrix4x4> { Matrix4x4.Identity };
                MapStreamer.SceneHandle h2;
                try { h2 = _st!.BuildScene(wrap, Matrix4x4.Identity, false); }
                finally { set.Placements = saved; }
                h2.Active = false;
                _st!.Scenes.Add(h2);
                var prop = new Prop { Hc = hc, Handle = h2, RootInv = rootInv };
                var xsb = _bank.Find(Path.GetFileNameWithoutExtension(set.Name) + ".XSB") ?? model.Bank.Find(Path.GetFileNameWithoutExtension(set.Name) + ".XSB");
                if (xsb != null)
                {
                    prop.Skel = XsbSkeleton.Parse(_bank.Read(xsb));
                    if (prop.Skel != null)
                        for (int i = 0; i < prop.Skel.Count; i++)
                            if (prop.Skel.Parent[i] < 0)
                            {
                                prop.Root = i;
                                var rest = Matrix4x4.CreateFromQuaternion(prop.Skel.Rot[i]) * Matrix4x4.CreateTranslation(prop.Skel.Pos[i]);
                                Matrix4x4.Invert(rest, out prop.RestRootInv);
                                break;
                            }
                }
                foreach (var o in h2.Objects) prop.Parts.Add((o, o.Model, o.BoundsMin, o.BoundsMax, o.Center));
                // gestreamte Ruhe-Kopie im Spot-Exterior: gleiche Objektnamen
                var names = new HashSet<string>(prop.Parts.Select(x => x.O.Name), StringComparer.OrdinalIgnoreCase);
                if (_spotCode != null)
                    foreach (var nn in new[] { "Spot_" + _spotCode, "Spot_" + _spotCode + "_low" })
                        foreach (var node in _st.NodesNamed(nn))
                            foreach (var o in node.Objects) if (names.Contains(o.Name)) prop.Streamed.Add(o);
                _props.Add(prop);
                Log.Info($"Cinematics {BankName}: Requisite {set.Name} ({h2.Objects.Count} Teile, Ruhe-Kopie {prop.Streamed.Count}, Skelett {(prop.Skel != null ? prop.Skel.Count + " Knochen, Wurzel " + prop.Root : "keins")})");
            }
        }
    }

    static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        return Vector3.DistanceSquared(a.Translation, b.Translation) < 0.01f
            && MathF.Abs(a.M11 - b.M11) < 0.01f && MathF.Abs(a.M13 - b.M13) < 0.01f && MathF.Abs(a.M31 - b.M31) < 0.01f && MathF.Abs(a.M33 - b.M33) < 0.01f;
    }

    /// <summary>Requisiten an ihre Darsteller-Spur (Szene des aktuellen Shots) setzen.</summary>
    void AnimateProps()
    {
        foreach (var p in _props) p.Moving = false;
        foreach (var pl in Order())
        {
            if (!pl.Def.Cin.SceneTime(pl.Def.Scenes!, pl.Time, out var sc, out float st) || sc == null) continue;
            foreach (var o in sc.Objects)
            {
                if (o.IsCamera || o.EntityHashcode == 0 || o.Find(CinIds.TrackPosition) == null) continue;
                var p = _props.Find(x => x.Hc == o.EntityHashcode);
                if (p == null) continue;
                p.Moving = true;
                p.Pose = Matrix4x4.CreateFromQuaternion(sc.OriginRotation * o.SampleRotation(st)) * Matrix4x4.CreateTranslation(sc.ToGame(o.SamplePosition(st)));
                // Wurzelbewegung aus dem Clip des Darstellers (Ruhe der Wurzel heraus, Clip-Lage hinein)
                p.Anim = Matrix4x4.Identity;
                int ri = o.AnimationRefAt(st, out float kt);
                var clip = p.Skel != null && p.Root >= 0 && ri >= 0 ? Clip(sc.ReferenceName(ri)) : null;
                if (clip != null)
                {
                    uint rh = p.Skel!.Hash[p.Root];
                    foreach (var tr in clip.Tracks)
                    {
                        if (tr.Hash != rh) continue;
                        float ct = System.Math.Clamp(st - kt, 0f, clip.Length);
                        var rot = tr.Rot != null ? tr.Rot.SampleRot(ct) : p.Skel.Rot[p.Root];
                        var pos = tr.Pos != null ? tr.Pos.SampleVec(ct) : p.Skel.Pos[p.Root];
                        p.Anim = p.RestRootInv * (Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos));
                        break;
                    }
                }
            }
        }
        foreach (var p in _props)
        {
            p.Handle.Active = p.Moving;
            foreach (var o in p.Streamed) o.Active = !p.Moving;
            if (!p.Moving) continue;
            var m = p.Anim * p.RootInv * p.Pose * Frame;
            foreach (var (o, b, mn, mx, c) in p.Parts)
            {
                o.Model = b * m;
                ObjectBuilder.TransformBounds(mn, mx, m, out o.BoundsMin, out o.BoundsMax);
                o.Center = Vector3.Transform(c, m);
            }
        }
    }

    IEnumerable<Playing> Order()
    {
        foreach (var b in _background) yield return b;
        if (_main != null) yield return _main;
    }

    // ================================================================== Kamera / Zeichnen
    /// <summary>Kamera der Haupt-Cinematic im Spielraum der Welt; false = keine (dann gilt die eigene Kamera des Modus).</summary>
    public bool Camera(out Vector3 eye, out Vector3 forward, out Vector3 up, out float fov)
    {
        eye = Vector3.Zero; forward = Vector3.UnitZ; up = Vector3.UnitY; fov = 60f;
        var p = _main;
        if (p == null || !p.Camera) return false;
        if (!p.Def.Cin.Camera(p.Def.Scenes!, p.Time, out var gp, out var gr, out float f)) return false;
        eye = Vector3.Transform(gp, Frame);
        forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.Transform(Vector3.UnitZ, gr), Frame));
        up = Vector3.Normalize(Vector3.TransformNormal(Vector3.Transform(Vector3.UnitY, gr), Frame));
        if (f > 1f && f < 170f) fov = f;
        return true;
    }

    /// <summary>Darsteller zeichnen. <paramref name="gameToRender"/> = Spielraum -> Render-Raum (MapStreamer).</summary>
    public void Render(Matrix4x4 gameToRender)
    {
        var toRender = Frame * gameToRender;
        if (_playerShown) DrawAvatar(_player, _playerClip, _playerT, _playerPose * toRender);
        foreach (var p in _pnjs) if (p.Shown) DrawAvatar(p.Avatar, p.Clip, p.T, p.Pose * toRender);
        if (_carShown && _car != null)
        {
            _car.UpdateTransforms(_carPose * toRender);
            _car.Draw(_r);
        }
    }

    void DrawAvatar(AvatarModel? a, AnmClip? clip, float t, Matrix4x4 model)
    {
        if (a == null || !a.Ready || a.Rig == null) return;
        if (a.Indoor != _indoor) a.Indoor = _indoor;
        a.Rig.BeginPose();
        if (clip != null)
        {
            float len = MathF.Max(0.05f, clip.Length);
            a.Rig.ApplyClip(clip, MathF.Min(t, len), 1f, true);   // Cinematic-Clips nicht schleifen: letzte Pose halten
        }
        a.Draw(model);
    }

    public void Dispose()
    {
        _player?.Dispose(); _player = null;
        foreach (var p in _pnjs) { p.Avatar?.Dispose(); p.Avatar = null; }
        StopVoices(_main); foreach (var b in _background) StopVoices(b);
        _audio?.StopAll(); _audio = null; _audioTask = null;
        _main = null; _background.Clear(); _ended.Clear(); ActiveActors.Clear();
        _car?.Destroy(); _car = null; _carAssets?.DestroyTextures(); _carAssets = null; _carTask = null;
        foreach (var p in _props) { foreach (var o in p.Streamed) o.Active = true; _st?.DestroyScene(p.Handle); }
        _props.Clear();
        if (_propTask != null) { var pt = _propTask; _propTask = null; pt.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.Release(); }); }
    }
}
