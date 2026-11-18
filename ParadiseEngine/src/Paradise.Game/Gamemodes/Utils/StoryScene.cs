// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2StoryScene.cs).
using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Szene mit Echtzeit-Kinos einer Story-Bank (Unity TDU2StoryScene, OpenTDU2 IntroCinematic):
///   - Bank (z.B. scenarics/cinematics/intro/new_intro.bnk, garage.bnk, meeting_tess.bnk) am Spot-Platz der Insel
///     (Spot-Position + Drehung um Y = Value1, ExitFrame: Position2/Heading2); "Closed" = geschlossener Raum (Garage) hoch über der Insel.
///   - Kinos, Darsteller (Spieler-Avatar, Figuren, Auto), Requisiten und Töne: <see cref="SpotCinematics"/>.
///   - entities.xmb (<see cref="InteriorScene"/>): Entity__Camera (Laufmodus-Start), Entity__PosCam (D_CAR), Entity__CinCar,
///     Entity__Volume ("Tess"), FpSelectables (Garage-Tor, Auto).
///   - Szenen-Modelle der Bank (und ExtraBanks) im Rahmen gebaut, mit Kollision (Laufmodus).
///   - Flash: frontend/&lt;res&gt;/intro.bnk (Credits "flsh:", Avatar-Wahl, Laufmodus) und general.bnk (pop_goal, pop_tutorial).
///   - Nachrichten der Haupt-Cinematic: flsh:&lt;Label&gt; (INTRO-Film), snd:music (Party-Musik-Emitter), snd:timestop,
///     weat:/wmod: (Kino-Wetter aus fx/cinematic_weathers.xmb + cinematic_modifiers.xmb -> WorldWeather.Override).
///   - HideSpotInterior: Interieur-Platzhalter des gestreamten Spot-Exteriors ausblenden (die Bank bringt das Interieur mit).
/// Nicht übernommen (wie Unity): FaceFX-Blickziele, Stil-Animationen, Lichter. Noch nicht portiert (CHECKLIST): FaceFX-Lippen-Sync.
/// </summary>
internal sealed class StoryScene : IDisposable
{
    internal sealed class Setup
    {
        public string? Bank;              // relativ zu Bnk, z.B. "scenarics/cinematics/intro/new_intro.bnk" (null = nur Oberfläche)
        public string? Spot;              // voller Spotname, z.B. "EPH_L3DI_5067I" (null = Inselursprung)
        public bool Closed;               // geschlossener Raum: hoch über die Insel legen
        public float ClosedAltitude = 1500f;
        public bool LoadSets = true;      // Szenen-Modelle der Bank bauen
        public bool LoadUi = true;        // general.vmf (Ziel/Tipp) und, mit IntroUi, intro.vmf
        public bool IntroUi = true;       // intro.vmf (Credits, Avatar-Wahl, Laufmodus-Cursor)
        public string[]? ExtraBanks;      // weitere Szenen-Banken im selben Rahmen (z.B. "interior/ich_ib.bnk")
        public bool HideSpotInterior;     // Interieur-Platzhalter (Low-Poly-LODs) des gestreamten Spot-Exteriors ausblenden
        public bool ExitFrame;            // Rahmen = Auto-Punkt des Spots (Position2/Heading2)
    }

    public Setup Config { get; }
    public bool Loaded { get; private set; }
    public bool Failed { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Bankrahmen -> Spielraum (Unity Holder).</summary>
    public Matrix4x4 Frame { get; private set; } = Matrix4x4.Identity;
    Matrix4x4 _frameInv = Matrix4x4.Identity;
    public Spot? HouseSpot { get; private set; }
    public SpotCinematics? Cin { get; private set; }
    public InteriorScene? Data { get; private set; }
    public VmfPlayer? Ui { get; private set; }
    public VmfPlayer? General { get; private set; }
    public IReadOnlyList<InteriorScene.Selectable> Selectables => (IReadOnlyList<InteriorScene.Selectable>?)Data?.Selectables ?? Array.Empty<InteriorScene.Selectable>();
    /// <summary>FSCommands des INTRO-Films (sbdd wird hier behandelt).</summary>
    public event Action<string, string>? Command;
    /// <summary>Übrige Nachrichten der Haupt-Cinematic (snd:, shad:, avm4: …).</summary>
    public event Action<string>? Message;
    /// <summary>Kamera gehört dem Laufmodus/Cockpit (true) bzw. dem Kino (false).</summary>
    public bool CameraOverride;

    readonly IGameModeContext _ctx;
    Task<(SpotCinematics? cin, InteriorScene? data, BankModel? model, List<BankModel> extra)>? _task;
    readonly List<MapStreamer.SceneHandle> _scenes = new();
    readonly List<StaticCollider> _colliders = new();
    readonly List<AudioVoiceList> _music = new();
    bool _insideSet; bool _wasInside; Vector3 _ambientSaved;
    float _extraFade; Vector4 _extraFadeColor = new(0f, 0f, 0f, 1f);
    float _goalTimer = -1f;
    float _savedNear = -1f;

    sealed class AudioVoiceList { public readonly List<Paradise.Core.Audio.AudioVoice> Voices = new(); }

    StoryScene(IGameModeContext ctx, Setup setup) { _ctx = ctx; Config = setup; }

    VehicleManager? Vm => _ctx.Vehicles?.Manager;
    MapStreamer? Streamer => _ctx.Terrain?.Streamer;
    static string BnkDir => FlashHost.BnkDir;

    // ------------------------------------------------------------------ Laden
    public static StoryScene Create(IGameModeContext ctx, Setup setup)
    {
        var s = new StoryScene(ctx, setup);
        s.StartLoad();
        return s;
    }

    static string? BankPath(string? rel) => string.IsNullOrEmpty(rel) ? null : Path.Combine(BnkDir, rel.Replace('\\', '/'));

    void StartLoad()
    {
        string? path = BankPath(Config.Bank);
        if (path != null && !GameFiles.Exists(path)) { Fail("Bank fehlt: " + path); return; }
        var st = Streamer;
        var r = _ctx.Vehicles?.Renderer;
        if (st == null || r == null) { Fail("keine Welt"); return; }
        bool sets = Config.LoadSets;
        var extra = new List<string>();
        if (Config.ExtraBanks != null)
            foreach (var e in Config.ExtraBanks) { var p = BankPath(e); if (p != null && GameFiles.Exists(p)) extra.Add(p); else Log.Warn("Story: Bank fehlt: " + p); }
        _task = Task.Run(() =>
        {
            SpotCinematics? cin = null; InteriorScene? data = null; BankModel? model = null;
            var extraModels = new List<BankModel>();
            if (path != null)
            {
                var bank = BnkArchive.Open(path);
                cin = new SpotCinematics(r, bank);
                data = InteriorScene.Parse(bank);
                if (sets) model = st.LoadSceneModel(path);
            }
            foreach (var p in extra)
            {
                try { extraModels.Add(st.LoadSceneModel(p)); }
                catch (Exception ex) { Log.Warn("Story: " + p + ": " + ex.Message); }
            }
            return (cin, data, model, extraModels);
        });
    }

    void Fail(string msg)
    {
        Failed = true; Error = msg;
        Log.Error("Story-Szene " + Config.Bank + ": " + msg);
    }

    /// <summary>Jedes Bild (Story.Update): Laden, Kinos, Ziel-Box.</summary>
    public void Update(float dt)
    {
        if (_task != null && _task.IsCompleted)
        {
            var t = _task; _task = null;
            if (t.IsFaulted) { Fail(t.Exception?.GetBaseException().Message ?? "Laden fehlgeschlagen"); return; }
            try { Build(t.Result.cin, t.Result.data, t.Result.model, t.Result.extra); }
            catch (Exception ex) { Fail("Aufbau: " + ex.Message); }
        }
        if (Config.Bank == null && !Loaded && !Failed && _task == null) Build(null, null, null, new List<BankModel>());
        if (!Loaded) return;
        _clock += dt;
        Cin?.Update(dt);
        if (_goalTimer > 0f) { _goalTimer -= dt; if (_goalTimer <= 0f) CloseGoal(); }
        if (_hideScanT >= 0f)
        {
            _hideScanT -= dt;
            if (_hideScanT <= 0f)
            {
                try { HideSpotInterior(); } catch (Exception ex) { Log.Warn("Story: Exterior ausblenden: " + ex.Message); }
                _hideScanT = _clock < _hideScanUntil ? 2f : 3f;   // danach weiter (seltener): Streaming kann das Exterior neu bauen
            }
        }
    }

    void Build(SpotCinematics? cin, InteriorScene? data, BankModel? model, List<BankModel> extra)
    {
        var st = Streamer!;
        InteriorUtils.Init(_ctx);   // Kollision der Szene über InteriorUtils.PrepareStatic
        Cin = cin; Data = data;
        Vector3 pos = Vector3.Zero; float heading = 0f;
        if (!string.IsNullOrEmpty(Config.Spot))
        {
            HouseSpot = Story.FindSpot(Config.Spot);
            if (HouseSpot != null)
            {
                bool exit = Config.ExitFrame && HouseSpot.Position2.LengthSquared() > 1f;
                pos = exit ? HouseSpot.Position2 : HouseSpot.Position;
                heading = exit ? HouseSpot.Heading2 : HouseSpot.Value1;
            }
            else Log.Warn("Story: Spot " + Config.Spot + " nicht gefunden");
        }
        if (Config.Closed) pos += Vector3.UnitY * Config.ClosedAltitude;
        var frame = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, heading)) * Matrix4x4.CreateTranslation(pos);
        // Ursprung der .sce ausgleichen (Meeting Tess: alle Szenen mit demselben Ursprung -> Rahmen x Ursprung⁻¹)
        if (HouseSpot != null && !Config.Closed && cin != null && SceneOrigin(cin, out var origin, out string originName) && Matrix4x4.Invert(origin, out var oInv))
        {
            frame = oInv * frame;
            Log.Info("Story: Szenen-Ursprung von " + originName + " ausgeglichen");
        }
        Frame = frame;
        Matrix4x4.Invert(frame, out _frameInv);
        if (cin != null) { cin.Frame = frame; cin.Indoor = Config.Closed; cin.Message += OnCinMessage; }

        if (model != null) BuildSet(st, model);
        foreach (var m in extra) BuildSet(st, m);
        if (Config.Closed)
        {
            _insideSet = true; _wasInside = WorldWeather.Inside; _ambientSaved = WorldWeather.InteriorAmbient;
            WorldWeather.Inside = true;
            WorldWeather.InteriorAmbient = new Vector3(0.55f, 0.55f, 0.58f);
        }
        LoadWeathers();
        if (Config.HideSpotInterior && !Config.Closed && HouseSpot != null && model != null) { _hideScanT = 0f; _hideScanUntil = _clock + 45f; }
        if (Config.LoadUi) LoadFlash();
        var t = _ctx.Terrain;
        if (t != null) { _savedNear = t.NearClip; t.NearClip = MathF.Min(t.NearClip, 0.05f); }
        Loaded = true;
        Log.Info($"Story-Szene {Path.GetFileName(Config.Bank ?? "(Oberfläche)")}: {cin?.Definitions.Count ?? 0} Kinos, {data?.Selectables.Count ?? 0} Auswahlpunkte, " +
                 $"{data?.Cameras.Count ?? 0} Kameras, {data?.Volumes.Count ?? 0} Volumen, {_scenes.Count} Szenen-Modelle" + (HouseSpot != null ? ", Spot " + Config.Spot : "") +
                 $", Rahmen {frame.Translation}");
    }

    void BuildSet(MapStreamer st, BankModel model)
    {
        var h = st.BuildScene(model, Frame, true);
        st.Scenes.Add(h);
        _scenes.Add(h);
        AddColliders(h);
    }

    /// <summary>Kollision der Szene (Unity AddColliders = PrepareStatic der Interieurs) für den Laufmodus.</summary>
    void AddColliders(MapStreamer.SceneHandle h)
    {
        var sc = InteriorUtils.PrepareStatic(h, false);
        if (sc != null) _colliders.Add(sc);
    }

    // ------------------------------------------------------------------ Interieur-Platzhalter des Spot-Exteriors (Unity HideSpotInterior)
    float _clock, _hideScanT = -1f, _hideScanUntil;
    List<string>? _extInteriorSets;
    readonly HashSet<RenderObject> _hidden = new();

    /// <summary>
    /// Im gestreamten Exterior des Spots (Knoten Spot_&lt;Code&gt;, Spot_&lt;Code&gt;_low) den Interieur-Platzhalter (Low-Poly-LODs, "INTERIOR",
    /// Entity__Actor__Interior des Exteriors) und Teile, die ganz in der Szene liegen, ausblenden – die Bank bringt das Interieur mit.
    /// Läuft wiederholt, weil das Exterior auch nach der Szene gestreamt werden kann. Kollision bleibt (Laufmodus).
    /// </summary>
    void HideSpotInterior()
    {
        var st = Streamer;
        if (st == null || HouseSpot == null) return;
        string n1 = "Spot_" + HouseSpot.Code, n2 = n1 + "_low";
        // Ausdehnung der Szene (Spielraum), 1 m größer
        Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
        foreach (var h in _scenes) foreach (var o in h.Objects) { bmin = Vector3.Min(bmin, o.BoundsMin); bmax = Vector3.Max(bmax, o.BoundsMax); }
        bool haveBounds = bmin.X <= bmax.X;
        if (haveBounds) { bmin -= Vector3.One; bmax += Vector3.One; }
        var names = new List<string>();
        foreach (var h in _scenes)
            if (h.Model != null) foreach (var set in h.Model.AllObjectSets()) if (set != null && !string.IsNullOrEmpty(set.Name)) names.Add(Path.GetFileNameWithoutExtension(set.Name));
        _extInteriorSets ??= ExteriorInteriorSets(HouseSpot.Code);
        foreach (var nn in new[] { n1, n2 })
            foreach (var node in st.NodesNamed(nn))
            {
                if (node.HasBounds && Vector3.DistanceSquared((node.BoundsMin + node.BoundsMax) * 0.5f, HouseSpot.Position) > 200f * 200f) continue;
                foreach (var o in node.Objects)
                {
                    if (!o.Active || _hidden.Contains(o)) continue;
                    bool hide = string.Equals(o.Name, "INTERIOR", StringComparison.OrdinalIgnoreCase) || IsExteriorInterior(o.Name);
                    if (!hide) foreach (var n in names) if (o.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { hide = true; break; }
                    if (!hide && haveBounds)
                        hide = o.BoundsMin.X >= bmin.X && o.BoundsMin.Y >= bmin.Y && o.BoundsMin.Z >= bmin.Z && o.BoundsMax.X <= bmax.X && o.BoundsMax.Y <= bmax.Y && o.BoundsMax.Z <= bmax.Z;
                    if (hide) { o.Active = false; _hidden.Add(o); }
                }
            }
    }

    bool IsExteriorInterior(string name)
    {
        if (_extInteriorSets == null || string.IsNullOrEmpty(name)) return false;
        foreach (var s in _extInteriorSets) if (name.StartsWith(s + "#", StringComparison.OrdinalIgnoreCase) || string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Szenen (.3dd-Name ohne Endung) der Entity__Actor__Interior in der Exterior-Bank des Spots.</summary>
    List<string> ExteriorInteriorSets(string code)
    {
        var list = new List<string>();
        try
        {
            string island = Streamer?.Index?.IslandName ?? "ibiza";
            string path = Path.Combine(BnkDir, "islands", island, "exterior", (code ?? "").ToLowerInvariant() + ".bnk");
            if (!GameFiles.Exists(path)) return list;
            var bank = BnkArchive.Open(path);
            var ent = bank.Find("entities.xmb"); var hc = bank.Find("hashcodes.xmb");
            if (ent == null || hc == null) return list;
            var byIndex = new Dictionary<int, BnkEntry>();
            foreach (var e in bank.Entries) byIndex[e.Index] = e;
            var byRsc = new Dictionary<ulong, BnkEntry>();
            var groups = XmbFile.Parse(bank.Read(hc)).Get("___CmdSaveFileInfoArray");
            if (groups != null)
                foreach (var g in groups.Items)
                {
                    var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                    if (idx == null || refs == null) continue;
                    int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
                    for (int i = 0; i < n; i++)
                        if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) byRsc[refs.Items[i].AsULong()] = be;
                }
            var arr = XmbFile.Parse(bank.Read(ent)).Get("ExportType_Entity")?.Get("ArrayOf_Entity__Actor__Interior");
            if (arr != null)
                foreach (var it in arr.Items)
                {
                    var mesh = it.Get("mesh");
                    if (mesh == null) continue;
                    foreach (var r in mesh.Items)
                        if (r.I("rscSubType", -1) == 0 && byRsc.TryGetValue(r.U("rsc"), out var dd))
                        {
                            string stem = Path.GetFileNameWithoutExtension(dd.Name);
                            if (!list.Contains(stem)) list.Add(stem);
                            break;
                        }
                }
            Log.Debug("Story: Interieur-LODs im Exterior " + Path.GetFileName(path) + ": " + string.Join(", ", list));
        }
        catch (Exception ex) { Log.Warn("Story: Exterior-Interieur: " + ex.Message); }
        return list;
    }

    /// <summary>Gemeinsamer Szenen-Ursprung (.sce, D3D-Zeilen) – nur wenn alle Szenen denselben, nicht-einheitlichen haben.</summary>
    static bool SceneOrigin(SpotCinematics cin, out Matrix4x4 m, out string name)
    {
        m = Matrix4x4.Identity; name = "";
        float[]? first = null;
        foreach (var d in cin.Definitions.Values)
            foreach (var sc in d.Scenes)
            {
                if (sc == null) continue;
                var o = sc.Origin;
                bool ident = true;
                for (int i = 0; i < 16 && ident; i++) ident = MathF.Abs(o[i] - ((i % 5) == 0 ? 1f : 0f)) < 1e-3f;
                if (ident) return false;
                if (first == null) { first = o; name = sc.Name; continue; }
                for (int i = 0; i < 16; i++) if (MathF.Abs(o[i] - first[i]) > 1e-2f) return false;
            }
        if (first == null) return false;
        m = new Matrix4x4(first[0], first[1], first[2], first[3], first[4], first[5], first[6], first[7],
                          first[8], first[9], first[10], first[11], first[12], first[13], first[14], first[15]);
        return true;
    }

    // ------------------------------------------------------------------ Flash
    /// <summary>Unity LoadFlash: eigene Filme intro.vmf (GuiDepth -2) und general.vmf (GuiDepth -3, nur pop_goal/pop_tutorial).</summary>
    void LoadFlash()
    {
        var texts = FlashLocalization.Load(BnkDir, FlashHost.Language);
        string? intro = Config.IntroUi ? FlashHost.FindFrontendBank("intro.bnk") : null;
        if (intro != null)
        {
            Ui = FlashHost.Load(intro, -2, false);
            if (Ui != null)
            {
                Ui.FSCommand += OnUiCommand;
                FlashLocalization.Apply(Ui, texts);
                Ui.SetValue("/:IsCasinoSaveData", false);
                Ui.SetValue("/:WhatPlatform", 1);
                Ui.SetValue("/:IsTransition", 1);
                Ui.MouseEnabled = true;
                FrontendSounds.Attach(Ui);
            }
        }
        string? general = FlashHost.FindFrontendBank("general.bnk");
        if (general != null)
        {
            General = FlashHost.Load(general, -3, false);
            if (General != null)
            {
                FlashLocalization.Apply(General, texts);
                General.FSCommand += OnGeneralCommand;
                General.SetValue("/:WhatPlatform", 1);
                General.SetValue("/:IsWalkmode", false);
                // nur Ziel- und Tipp-Boxen zeigen
                if (General.Root != null) foreach (var d in General.Root.List.Values) d.Visible = d.Name == "pop_goal" || d.Name == "pop_tutorial";
                FrontendSounds.Attach(General);
            }
        }
    }

    void OnGeneralCommand(string c, string a)
    {
        if (c == "sbdd" && General != null) FlashLocalization.Apply(General, FlashLocalization.Load(BnkDir, FlashHost.Language));
    }

    void OnUiCommand(string c, string a)
    {
        if (c == "sbdd") { if (Ui != null) FlashLocalization.Apply(Ui, FlashLocalization.Load(BnkDir, FlashHost.Language)); return; }
        Command?.Invoke(c, a);
    }

    /// <summary>Nachrichten der Haupt-Cinematic (Unity ProcessMessages).</summary>
    void OnCinMessage(string m, float since)
    {
        if (m.StartsWith("flsh:", StringComparison.Ordinal))
        {
            string label = m.Substring(5);
            if (label == "generik5" && Ui != null) Ui.SetVariable("/:generik_date", FlashLocalization.Text(BnkDir, FlashHost.Language, 0x139b) ?? "");
            Ui?.GotoLabel("/", label, true);
            Message?.Invoke(m);
        }
        else if (m.StartsWith("weat:", StringComparison.Ordinal)) { _weather = m.Substring(5); _modifier = ""; ApplyWeather(); }
        else if (m.StartsWith("wmod:", StringComparison.Ordinal)) { _modifier = m.Substring(5); ApplyWeather(); }
        else if (m == "snd:timestop") { Cin?.PlayEmitter("S_TimeStop", since); Message?.Invoke(m); }
        else if (m == "snd:music")
        {
            // die 4 Party-Musik-Emitter (Schleife)
            StopMusic();
            var l = new AudioVoiceList();
            foreach (var n in new[] { "S_WALKMODE_Music", "S_WALKMODE_Music_01", "S_WALKMODE_Music_02", "S_WALKMODE_Music_03" })
                Cin?.PlayEmitter(n, since, true, l.Voices);
            _music.Add(l);
            Message?.Invoke(m);
        }
        else Message?.Invoke(m);
    }

    // ------------------------------------------------------------------ Kino-Wetter (Unity LoadWeathers/ApplyWeather)
    WeatherData? _weathers;
    string _weather = "", _modifier = "";

    /// <summary>fx/cinematic_weathers.xmb der Insel und die Modifikatoren der Schnitte (cinematic_modifiers.xmb, "wmod:Meeting_Tess_02" …).</summary>
    void LoadWeathers()
    {
        var idx = Streamer?.Index;
        if (idx == null) return;
        string path = Path.Combine(idx.FxDir, "cinematic_weathers.xmb");
        try { if (GameFiles.Exists(path)) _weathers = WeatherData.Load(path); }
        catch (Exception ex) { Log.Warn("Story: cinematic_weathers.xmb: " + ex.Message); }
        try { if (_weathers != null) _weathers.LoadModifiers(Path.Combine(idx.FxDir, "cinematic_modifiers.xmb")); }
        catch (Exception ex) { Log.Warn("Story: cinematic_modifiers.xmb: " + ex.Message); }
    }

    void ApplyWeather()
    {
        if (_weathers == null || string.IsNullOrEmpty(_weather)) { WorldWeather.Override = null; return; }
        var k = _weathers.Find(_weather, _modifier);
        WorldWeather.Override = k;
        Log.Debug($"Story: Kino-Wetter weat:{_weather} wmod:{_modifier} -> {k?.Name ?? "nicht gefunden"}");
    }

    public void StopMusic()
    {
        foreach (var l in _music) CinematicAudio.Stop(l.Voices);
        _music.Clear();
    }

    // ------------------------------------------------------------------ Kinos
    public string MainName => Cin?.MainName ?? "";
    public float MainTime => Cin?.MainTime ?? 0f;
    public bool PlayMain(string name) => Cin != null && Cin.PlayMainProps(name);
    public bool PlayBackground(string name, bool loop = true) => Cin != null && Cin.PlayBackground(name, loop);
    public void StopMain() => Cin?.StopMain();
    public void StopBackground() => Cin?.StopBackground();
    public bool IsBackgroundPlaying(string name) => Cin != null && Cin.IsBackgroundPlaying(name);
    public bool PlayEmitter(string name) => Cin != null && Cin.PlayEmitter(name);
    public float PlayFx(string rootName) => Cin?.PlayFx(rootName) ?? 0f;
    public void SkipMain() => Cin?.SkipMain();
    public List<string> TakeEndEvents() => Cin?.TakeEndEvents() ?? new List<string>();

    /// <summary>Spieler-Avatar (Darsteller "Avatar") aus einer DBPNJ-Konfiguration (AVT_Male_Intro …) oder einer Konfiguration.</summary>
    public bool SetPlayerAvatar(string? configId, AvatarConfig? cfg = null)
    {
        cfg ??= AvatarLibrary.PnjConfig(configId);
        if (cfg == null || Cin == null) return false;
        Cin.SetPlayer(cfg);
        return true;
    }

    /// <summary>
    /// Auto der Szene: ruht an einer Platzierung (Entity__PosCam, z.B. D_CAR); spielt ein Kino den Darsteller einer Entity__CinCar
    /// (bzw. mit diesem Namen, z.B. CIN_CAR / PLAYER_CAR), folgt es dessen Spuren.
    /// </summary>
    public void SetCar(CarPhysics? car, string? restEntity, string cinCarEntity)
    {
        if (Cin == null || car == null) return;
        Cin.CarActor = cinCarEntity;
        Cin.CarActorHc = 0;
        if (Data != null) foreach (var c in Data.CinCars) if (string.Equals(c.Name, cinCarEntity, StringComparison.OrdinalIgnoreCase)) Cin.CarActorHc = c.Hc;
        var rest = restEntity != null ? Data?.CarSlot(restEntity) : null;
        Cin.CarRest = rest?.M;
        Cin.LoadCar(Vm, car);
    }

    public CarModel? CarModel => Cin?.Car;
    public bool CarLoading => Cin != null && Cin.CarLoading;
    /// <summary>Lage des Autos der Szene im Spielraum.</summary>
    public Matrix4x4? CarPoseGame => Cin?.CarPoseGame;

    // ------------------------------------------------------------------ Raum
    /// <summary>Entity__Camera (z.B. cam_entree_01) im Spielraum.</summary>
    public bool EntityCamera(string name, out Vector3 eye, out Vector3 forward)
    {
        eye = Frame.Translation + Vector3.UnitY * 1.6f; forward = Vector3.TransformNormal(Vector3.UnitZ, Frame);
        var c = Data?.Camera(name);
        if (c == null) return false;
        eye = Vector3.Transform(c.Position, Frame);
        forward = Vector3.Normalize(Vector3.TransformNormal(c.Forward, Frame));
        return true;
    }

    public Vector3 ToLocal(Vector3 game) => Vector3.Transform(game, _frameInv);
    public Vector3 ToLocalDir(Vector3 game) => Vector3.TransformNormal(game, _frameInv);
    public InteriorScene.Volume? FindVolume(string name) => Data?.FindVolume(name);

    // ------------------------------------------------------------------ Kamera / Blende
    /// <summary>Kino-Kamera an die Engine-Kamera (Unity LateUpdate), außer der Laufmodus/das Cockpit hat sie.</summary>
    public void ApplyCamera()
    {
        if (!Loaded || CameraOverride || Cin == null) return;
        var t = _ctx.Terrain; var cam = t?.Camera; var st = Streamer;
        if (t == null || cam == null || st == null) return;
        if (!Cin.Camera(out var eye, out var fwd, out var up, out float fov)) return;
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z) || !float.IsFinite(fwd.X)) return;   // NaN nie an die Kamera
        t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (fwd, up);
        if (fov > 1f && fov < 170f) t.FieldOfView = fov;
    }

    /// <summary>Kamera aus einer Spielposition setzen (Laufmodus, Cockpit).</summary>
    public void SetCamera(Vector3 eye, Vector3 forward, Vector3 up, float fov)
    {
        var t = _ctx.Terrain; var cam = t?.Camera; var st = Streamer;
        if (t == null || cam == null || st == null) return;
        t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (forward, up);
        t.FieldOfView = fov;
    }

    /// <summary>Zusätzliche Blende (0 = aus, 1 = voll) in einer Farbe.</summary>
    public void SetFade(float alpha, Vector4 color) { _extraFade = System.Math.Clamp(alpha, 0f, 1f); _extraFadeColor = color; }

    /// <summary>Blende dieses Bildes: Einblenden am Shot-Anfang (Typ 1) bzw. die zusätzliche Blende.</summary>
    public void UpdateFade()
    {
        float a = 0f; var c = new Vector4(0f, 0f, 0f, 1f);
        if (!CameraOverride && Cin != null && Cin.ShotFade(out float sa, out var sc)) { a = sa; c = sc; }
        if (_extraFade > 0f && _extraFade >= a) { a = _extraFade; c = _extraFadeColor; }
        StoryFade.Set(a, c);
    }

    public void Render(Matrix4x4 gameToRender) { if (Loaded) Cin?.Render(gameToRender); }

    // ------------------------------------------------------------------ GENERAL: Ziel- und Tipp-Box
    /// <summary>pop_goal (FUN_00d61790): /:GoalTxt, Label "open", 30 s.</summary>
    public void ShowGoal(string text)
    {
        Story.CurrentObjective = text;
        if (General == null) return;
        General.SetVariable("/:GoalTxt", text ?? "");
        General.SetValue("/:IsWalkmode", false);
        General.GotoLabel("/pop_goal", "open", true);
        _goalTimer = 30f;
    }

    public void CloseGoal()
    {
        _goalTimer = -1f;
        if (General != null && !General.Call("/pop_goal", "close_popup")) General.GotoLabel("/pop_goal", "close", true);
    }

    /// <summary>pop_tutorial ("Tipp"): /:TutText, Label "open_tuto".</summary>
    public void ShowTutorial(string text)
    {
        Story.CurrentObjective = text;
        if (General == null) return;
        General.SetVariable("/:TutText", text ?? "");
        General.SetValue("/:IsWalkmode", false);
        General.GotoLabel("/pop_tutorial", "open_tuto", true);
    }

    public void CloseTutorial()
    {
        if (General != null && !General.Call("/pop_tutorial", "close_popup")) General.GotoLabel("/pop_tutorial", "close", true);
    }

    /// <summary>Lokalisierter Text (db_&lt;lang&gt;, HashCode/Zeile).</summary>
    public static string Loc(int id) => FlashLocalization.Text(BnkDir, FlashHost.Language, id) ?? "";

    // ------------------------------------------------------------------ Aufräumen
    public void Dispose()
    {
        StopMusic();
        if (Cin != null) { Cin.Message -= OnCinMessage; Cin.Dispose(); Cin = null; }
        var st = Streamer;
        foreach (var h in _scenes) st?.DestroyScene(h);
        _scenes.Clear();
        var phys = _ctx.Terrain?.Physics;
        foreach (var c in _colliders) phys?.Remove(c);
        _colliders.Clear();
        foreach (var o in _hidden) o.Active = true;
        _hidden.Clear();
        _hideScanT = -1f;
        if (_weathers != null) WorldWeather.Override = null;
        if (_insideSet) { WorldWeather.Inside = _wasInside; WorldWeather.InteriorAmbient = _ambientSaved; _insideSet = false; }
        if (Ui != null) { Ui.FSCommand -= OnUiCommand; FrontendSounds.Detach(Ui); FlashHost.Remove(Ui); Ui = null; }
        if (General != null) { General.FSCommand -= OnGeneralCommand; FrontendSounds.Detach(General); FlashHost.Remove(General); General = null; }
        var t = _ctx.Terrain;
        if (t != null && _savedNear > 0f) t.NearClip = _savedNear;
        StoryFade.Set(0f, default);
        if (_task != null) { var tk = _task; _task = null; tk.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) { x.Result.cin?.Dispose(); x.Result.model?.Release(); foreach (var m in x.Result.extra) m.Release(); } }); }
        Loaded = false;
    }
}

/// <summary>
/// Vollbild-Blende der Geschichte (Unity FadeOverlay/UpdateFade: Quad vor der Kamera) als Engine-Ebene über der Welt und unter den
/// Flash-Filmen der Geschichte (wie die Unity-Quad-Blende vor der Kamera; keine eigene Oberfläche).
/// </summary>
internal sealed class StoryFade : FlashHost.IGuiLayer
{
    static StoryFade? _inst;
    float _a; Vector4 _c = new(0f, 0f, 0f, 1f);
    IGpuTexture? _white;
    public int GuiDepth => 5;

    public static void Set(float alpha, Vector4 color)
    {
        if (alpha <= 0.001f) { if (_inst != null) _inst._a = 0f; return; }
        if (_inst == null) { _inst = new StoryFade(); FlashHost.AddLayer(_inst); }
        _inst._a = System.Math.Clamp(alpha, 0f, 1f); _inst._c = color;
    }

    static readonly ObjectVertex[] _q = new ObjectVertex[4];

    public void Render(IRenderer r)
    {
        if (_a <= 0.001f) return;
        _white ??= r.CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 255, 255, 255, 255 }, Name = "Story-Blende", Linear = true });
        float w = Screen.width, h = Screen.height;
        _q[0] = new ObjectVertex { Position = new Vector3(0, 0, 0), Uv0 = new Vector2(0, 0) };
        _q[1] = new ObjectVertex { Position = new Vector3(w, 0, 0), Uv0 = new Vector2(1, 0) };
        _q[2] = new ObjectVertex { Position = new Vector3(w, h, 0), Uv0 = new Vector2(1, 1) };
        _q[3] = new ObjectVertex { Position = new Vector3(0, h, 0), Uv0 = new Vector2(0, 1) };
        var p = new FlashQuadParams { Mul = new Vector4(_c.X, _c.Y, _c.Z, _a), Add = Vector4.Zero, Clip = new Vector4(-1e7f, -1e7f, 1e7f, 1e7f), MaskRect = Vector4.Zero, UnityGui = true };
        r.DrawFlash(_q, _white, null, p);
    }
}
