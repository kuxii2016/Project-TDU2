using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using Paradise.Game.World;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Minimap = Paradise.Core.Vehicles.Minimap;
using VmfPlayer = Paradise.Core.Flash.VmfPlayer;

namespace Paradise.Game.Gameplay;

/// <summary>
/// Wrecks (Discovery) und Metalldetektor - Port von game/freeride/discovery_points.cpp und
/// gm_freeride.cpp. Die Daten kommen aus islands/&lt;island&gt;/scenarics/wrecks/wrecks.bnk
/// (entities.xmb, ArrayOf_Entity__Wreck) und scenarics/wrecks/wrecks_config.cpr.
///
/// Die Anzeige ist ausschliesslich das natives HUD aus hud.bnk (/HUD/wrck) bzw. map.bnk:
/// Es wird kein eigener Freeride-HUD-Code verwendet. Alle Positionen bleiben in
/// Spielkoordinaten; RootMatrix/RootOffset werden nur beim Zeichnen von Karte/Minimap benutzt.
/// </summary>
internal sealed class Wrecks : GameModule
{
    /// <summary>Ein Wrack aus der XMB (Index = 0-basierter Array-Index, wie im Original gespeichert).</summary>
    public sealed class Point
    {
        public int Index;
        public int Id;
        public string Name = "";
        public Vector3 Position;
        /// <summary>orient_pos_scale der XMB in Spielkoordinaten (RootMatrix wird erst beim Zeichnen angewandt).</summary>
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public int Area = -1;
        public bool Found;
        /// <summary>Der "Wreck%dS"-Shine-Marker; nur sichtbar, solange das Wrack nicht gefunden ist.</summary>
        public MapStreamer.SceneHandle? Shine;
    }

    sealed class Polygon
    {
        public float[] X = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
    }

    /// <summary>Aktorbank (pack_wrecks.bnk) zum Auflösen von "Wreck%d" und "Wreck%dS".</summary>
    sealed class WreckActorBank
    {
        public string Path = "";
        public BankModel Model = null!;
        public XmbNode? Entities;
        readonly Dictionary<ulong, BnkEntry> _byRsc = new();
        readonly Dictionary<string, ObjectSet?> _sets = new(StringComparer.OrdinalIgnoreCase);

        public static WreckActorBank? Open(string path, GlobalTextureCache textures, List<string> log)
        {
            if (!GameFiles.Exists(path)) return null;
            var bank = new WreckActorBank { Path = path };
            bank.Model = BankModel.Load(path, BankKind.Generic, false, false, false);
            textures.IndexBank(bank.Model.Bank);
            try { TextureAnimations.PrepareUva(bank.Model.Bank); } catch (Exception) { }
            var ent = bank.Model.Bank.Find("entities.xmb");
            var hc = bank.Model.Bank.Find("hashcodes.xmb");
            if (ent == null || hc == null)
            {
                log.Add("pack_wrecks.bnk ohne entities.xmb/hashcodes.xmb");
                return bank;
            }
            bank.Entities = XmbFile.Parse(Unwrap(bank.Model.Bank.Read(ent)));
            var codes = XmbFile.Parse(Unwrap(bank.Model.Bank.Read(hc)));
            var byIndex = new Dictionary<int, BnkEntry>();
            foreach (var e in bank.Model.Bank.Entries) byIndex[e.Index] = e;
            var groups = codes.Get("___CmdSaveFileInfoArray");
            if (groups != null)
                foreach (var g in groups.Items)
                {
                    var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                    if (idx == null || refs == null) continue;
                    int n = Math.Min(idx.Items.Count, refs.Items.Count);
                    for (int i = 0; i < n; i++)
                        if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) bank._byRsc[refs.Items[i].AsULong()] = be;
                }
            return bank;
        }

        public ObjectSet? Find(string actor, List<string> log)
        {
            if (_sets.TryGetValue(actor, out var cached)) return cached;
            ObjectSet? set = null;
            if (Entities != null)
                foreach (var section in Entities.Children)
                {
                    if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
                    foreach (var arr in section.Children)
                    {
                        if (!arr.IsArray) continue;
                        foreach (var it in arr.Items)
                        {
                            if (!string.Equals(it.S("__Name", ""), actor, StringComparison.OrdinalIgnoreCase)) continue;
                            set = Load(it, actor, log);
                            if (set != null) break;
                        }
                        if (set != null) break;
                    }
                    if (set != null) break;
                }
            if (set == null) set = LoadByName(actor, log);
            _sets[actor] = set;
            return set;
        }

        ObjectSet? Load(XmbNode it, string actor, List<string> log)
        {
            var mesh = it.Get("mesh") ?? it.Get("Model") ?? it.Get("Mesh");
            BnkEntry? dd = null, dg = null, dm = null;
            if (mesh != null)
                foreach (var r in mesh.Items)
                {
                    if (!_byRsc.TryGetValue(r.U("rsc"), out var be) || be == null) continue;
                    int sub = r.I("rscSubType", -1);
                    if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
                }
            if (dd == null || dg == null) return LoadByName(actor, log);
            return LoadEntries(dd, dg, dm, actor, log);
        }

        ObjectSet? LoadByName(string actor, List<string> log)
        {
            BnkEntry? dd = null, dg = null, dm = null;
            foreach (var e in Model.Bank.Entries)
            {
                if (!e.Name.StartsWith(actor + "_", StringComparison.OrdinalIgnoreCase) &&
                    !e.Name.StartsWith(actor + ".", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Extension == ".3dd" && dd == null) dd = e;
                else if (e.Extension == ".3dg" && dg == null) dg = e;
                else if (e.Extension == ".2dm" && dm == null) dm = e;
            }
            return dd == null || dg == null ? null : LoadEntries(dd, dg, dm, actor, log);
        }

        ObjectSet? LoadEntries(BnkEntry dd, BnkEntry dg, BnkEntry? dm, string actor, List<string> log)
        {
            try
            {
                var set = ObjectSet.Load(Model.Bank, dd, dg, dm, log);
                set.Placements = null;
                log.Add(actor + " -> " + dd.Name);
                return set;
            }
            catch (Exception ex) { log.Add(actor + ": " + ex.Message); return null; }
        }

        public void Release()
        {
            foreach (var set in _sets.Values.Distinct())
                if (set != null) set.Release();
            _sets.Clear();
            Model.Release();
        }
    }

    public static Wrecks? Current { get; private set; }

    /// <summary>Gezielte Diagnoseausgaben – vorübergehend immer aktiv, damit auch VS-Starts protokollieren.</summary>
    void Dbg(string msg) { }// => Log.Info("[WreckDbg] " + msg);

    /// <summary>
    /// Test-Schalter: Das Retail-Spiel verlangt "Racing level 3" für den Metalldetektor; unsere Engine
    /// kennt keine Levelprüfung. true (Standard) = Levelminimum ignorieren, false = Gate aktiv.
    /// Umstellen auf false (Retail-Zustand, Gate aktiv) über das DevMenü.
    /// </summary>
    public static bool TestBypassLevelMinimum = true;
    /// <summary>Retail-Voraussetzung für den Detektor (alter TODO nannte Level 6).</summary>
    public const int RequiredRacingLevel = 3;
    /// <summary>DBRewardXP CRUISING_WRECK_PART_IBIZA_AREA1 = 25 Punkte (C++ docs/STATE.md 5927).</summary>
    public const int XpPerWreck = 25;

    /// <summary>Welches Ereignis den wrck-Dialog geoeffnet hat (bestimmt den Close-Pfad).</summary>
    public enum WreckDialogEvent
    {
        Found,
        AlreadyFound
    }

    /// <summary>Neuer Fund: false = open_small (AlreadyFound=false, "1/28 (A)").</summary>
    public const bool Dialog_Found_Big = false;
    /// <summary>Bereits gefunden: true = open_big (AlreadyFound=true). Auf FALSE setzen, wenn auch hier der kleine Dialog kommen soll.</summary>
    public const bool Dialog_AlreadyFound_Big = false;
    /// <summary>false = nach dem Schliessen bleibt der Wrack "angeschaut" (kein Reopen beim Stehenbleiben). true = Reopen erlaubt.</summary>
    public const bool Dialog_AlreadyFound_RepeatWhenStaying = false;

    readonly TerrainModule _terrain;
    readonly VehicleModule _vehicles;
    readonly List<Point> _points = new();
    readonly List<Polygon> _zones = new();
    readonly List<int> _found = new();
    readonly List<MapStreamer.SceneHandle> _wreckScenes = new();
    readonly HashSet<int> _refindBlocked = new();

    WreckActorBank? _actors;
    float _range = 250f, _activation = 10f, _bipFar = 1.35f, _bipNear = 0.20f;
    float _foundUiHold, _beepTimer, _pulseTimer, _dialogAge;
    bool _foundDialogOpen, _pendingHide, _foundDialogBig;
    WreckDialogEvent? _dialogEvent;
    int _nearFoundIndex = -1;
    bool _detectorOn, _log, _disabled, _hudAttached, _lockedLogged, _showMinimapBlips;
    bool _hudCountsDirty = true, _pauseDirty = true, _saveImported;
    string _island = "";
    float _logTimer;
    int _lastLoggedFound = -1;

    public IReadOnlyList<Point> Points => _points;
    public string Island => _island;
    public int Total => _points.Count;
    public int FoundCount => _found.Count;
    public float DetectorRange { get => _range; set => _range = Math.Clamp(value, 1f, 5000f); }
    public float ActivationDistance { get => _activation; set => _activation = Math.Clamp(value, 0.5f, 100f); }
    public float BipFar { get => _bipFar; set => _bipFar = Math.Clamp(value, 0.05f, 10f); }
    public float BipNear { get => _bipNear; set => _bipNear = Math.Clamp(value, 0.05f, 10f); }
    public bool DetailedLog { get => _log; set => _log = value; }
    public bool Enabled
    {
        get => !_disabled;
        set
        {
            if (value == !_disabled) return;
            _disabled = !value;
            if (value) _pauseDirty = true;   // Pausemenü beim Wiederaktivieren auffrischen
        }
    }
    public bool ShowMinimapBlips
    {
        get => _showMinimapBlips;
        set
        {
            _showMinimapBlips = value;
            if (!_showMinimapBlips) ClearMinimapBlips();
        }
    }
    public bool IsFound(int index) => index >= 0 && index < _points.Count && _points[index].Found;

    static void ResetWreckVars(VmfPlayer hud)
    {
        hud.SetValue("/:num_loop", 0f);
        hud.SetValue("/HUD/wrck:num_loop", 0f);
        hud.SetValue("/:fake", false);
        hud.SetValue("/HUD/wrck:AlreadyFound", false);
        hud.SetValue("/HUD/wrck:AllWrecksFound", false);
    }

    public Wrecks(TerrainModule terrain, VehicleModule vehicles)
    {
        _terrain = terrain;
        _vehicles = vehicles;
    }

    public override void Load()
    {
        Current = this;
        FrontendSounds.Preload("wrckon", "wrckoff", "wrckwarn", "wrckfund", "wrckrwd", "wrckextd");
        FrontendSounds.SetSuppressed("wrckwarn", true);
    }

    public override void Unload()
    {
        ClearMinimapBlips();
        ClearModels();
        FrontendSounds.SetSuppressed("wrckwarn", false);
        _pulseTimer = 0f; _beepTimer = 0f;
        { var hud = Hud; if (hud != null) ResetWreckVars(hud); }
        _foundDialogOpen = false; _pendingHide = false; _dialogEvent = null;
        if (ReferenceEquals(Current, this)) Current = null;
        _points.Clear(); _zones.Clear(); _found.Clear(); _refindBlocked.Clear();
        _island = "";
    }

    public override void Update(in GameTime time)
    {
        if (_pauseDirty) { _pauseDirty = false; if (!_disabled) PushPauseMenu(); }
        if (_disabled) { ClearMinimapBlips(); SetDetector(false); return; }   // auch Abschalten aus dem DevMenu schliesst das HUD
        var streamer = _terrain.Streamer;
        string? island = streamer?.Index?.IslandName;
        if (streamer == null || string.IsNullOrEmpty(island)) return;
        if (!string.Equals(island, _island, StringComparison.OrdinalIgnoreCase))
        {
            if (!LoadIsland(island, streamer.Index!)) return;
        }
        // Beim Start kann der Spielstand noch nicht angemeldet sein – dann später nachholen.
        if (!_saveImported && TDU2Session.Save != null) ImportSave();
        // Retail-Gate (nur aktiv, wenn der Test-Schalter aus ist)
        if (!DetectorUnlocked)
        {
            if (!_lockedLogged)
            {
                Log.Info($"Wrecks: Detektor gesperrt - Racing Level {CurrentRacingLevel()} < {RequiredRacingLevel}");
                _lockedLogged = true;
            }
            ClearMinimapBlips();
            SetDetector(false);
            return;
        }
        _lockedLogged = false;
        if (_points.Count == 0 || _vehicles.Manager?.Current == null)
        {
            ClearMinimapBlips();
            SetDetector(false);
            return;
        }

        var car = _vehicles.Manager.PositionGame;
        float nearest = -1f;
        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            if (p.Found) continue;
            float dx = p.Position.X - car.X, dz = p.Position.Z - car.Z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            // DevMenu-Entfernen: erst wieder foundbar, wenn man den Wrack-Radius verlassen hat.
            if (_refindBlocked.Count > 0 && d > _activation * 1.5f) _refindBlocked.Remove(p.Index);
            if (_refindBlocked.Contains(p.Index)) continue;
            if (d < _activation)
            {
                OnFound(p);
                continue;
            }
            if (d < _range && (nearest < 0f || d < nearest)) nearest = d;
        }

        // Bereits gefundenes Wrack im Annex-Bereich: kleiner Open-/Info-Dialog (A -> Bigmap),
        // aber nur einmal pro Annäherung - nicht bei jedem Frame und nicht nach einem echten Fund.
        int nearFound = -1;
        float foundDist = float.MaxValue;
        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            if (!p.Found) continue;
            float dx = p.Position.X - car.X, dz = p.Position.Z - car.Z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < foundDist) { foundDist = d; nearFound = i; }
        }
        bool nearFoundNow = nearFound >= 0 && foundDist <= _activation * 1.5f;
        if (nearFoundNow && nearFound != _nearFoundIndex && !_foundDialogOpen && _foundUiHold <= 0f && !_disabled && DetectorUnlocked)
        {
            OnAlreadyFound(nearFound);
        }
        else if (foundDist > _activation * 2f) _nearFoundIndex = -1;

        if (_foundUiHold > 0f) _foundUiHold = Math.Max(0f, _foundUiHold - time.DeltaTime);
        if (_foundDialogOpen)
        {
            _dialogAge += time.DeltaTime;
            if (FlashNav.Pressed(FlashKey.Confirm) || _dialogAge > 8f) OnDialogClosed();
        }
        else UpdateHud(nearest, time.DeltaTime);
        if (_pendingHide && _foundUiHold <= 0f)
        {
            _pendingHide = false;
            var hud = Hud;
            if (hud != null)
            {
                var clip = hud.Find("/HUD/wrck");
                if (clip != null) clip.Visible = false;
            }
        }

        // Pieps-Takt aus wrecks_config.cpr: fern 1,35 s, nah 0,20 s (lineare Interpolation
        // ueber die Entfernung). Der Film liefert die sichtbare Kreis-Animation; sein
        // wrckwarn wird unterdrueckt und hier exakt getaktet. Bei jedem Pieps startet der
        // native Crossfade (frame 15..19) neu, damit das Blinken zum Ton passt.
        if (nearest >= 0f && !_foundDialogOpen && !_disabled && DetectorUnlocked && _detectorOn)
        {
            float t = Math.Clamp(nearest / Math.Max(1f, _range), 0f, 1f);
            float period = _bipNear + (_bipFar - _bipNear) * t;
            _beepTimer -= time.DeltaTime;
            if (_beepTimer <= 0f)
            {
                FrontendSounds.Play("wrckwarn");
                _beepTimer = period;
                var hud = Hud;
                if (hud != null)
                {
                    hud.GotoFrameNumber("/HUD/wrck", 14, true);   // Label loop (0-basiert)
                    hud.SetValue("/HUD/wrck:num_loop", 0f);
                    hud.SetValue("/:num_loop", 0f);
                    _pulseTimer = 3f / Math.Max(1f, hud.Movie.FrameRate);   // 15..19, vor frame 20 anhalten
                }
            }
        }
        else _beepTimer = 0f;

        if (_pulseTimer > 0f)
        {
            _pulseTimer -= time.DeltaTime;
            if (_pulseTimer <= 0f)
            {
                var hud = Hud;
                if (hud != null) hud.GotoFrameNumber("/HUD/wrck", 18, false);  // frame 19 halten
            }
        }

        UpdateMinimapBlips(nearest);

        if (!_log) return;
        _logTimer += time.DeltaTime;
        if (_logTimer < 2f) return;
        _logTimer = 0f;
        if (_found.Count != _lastLoggedFound)
        {
            _lastLoggedFound = _found.Count;
            Log.Info($"Wrecks: {_found.Count}/{_points.Count} gefunden");
        }
    }

    bool LoadIsland(string island, IslandIndex index)
    {
        _island = island;
        ClearMinimapBlips();
        ClearModels();
        _points.Clear(); _zones.Clear(); _found.Clear(); _refindBlocked.Clear();
        _detectorOn = false; _foundUiHold = 0f; _beepTimer = 0f; _pulseTimer = 0f; _dialogAge = 0f;
        _foundDialogOpen = false; _pendingHide = false; _nearFoundIndex = -1;
        _hudCountsDirty = true; _saveImported = false;
        // Der Flash-Clip bleibt beim Inselwechsel sonst sichtbar, obwohl _detectorOn schon false ist.
        var hud = Hud;
        if (hud != null)
        {
            var clip = hud.Find("/HUD/wrck");
            if (clip != null) clip.Visible = false;
            ResetWreckVars(hud);
        }

        LoadConfig(index.BnkDir);
        LoadZones(index.LevelDir);

        string path = Path.Combine(index.IslandDir, "scenarics", "wrecks", "wrecks.bnk");
        if (!GameFiles.Exists(path))
        {
            Log.Warn($"Wrecks: {path} fehlt");
            return false;
        }
        try
        {
            var bank = BnkArchive.Open(path);
            var entry = bank.Find("entities.xmb");
            if (entry == null) { Log.Warn($"Wrecks: {path} hat kein entities.xmb"); return false; }
            var root = XmbFile.Parse(bank.Read(entry));
            var array = root.Get("ExportType_Entity")?.Get("ArrayOf_Entity__Wreck");
            if (array == null) { Log.Warn($"Wrecks: {path} hat keine ArrayOf_Entity__Wreck"); return false; }
            int i = 0;
            foreach (var e in array.Items)
            {
                var ops = e.Get("orient_pos_scale");
                var pos = ops?.Get("pos");
                if (pos == null) continue;
                var p = new Point
                {
                    Index = i,
                    Id = e.I("ID"),
                    Name = e.S("__Name", "") ?? "",
                    Position = new Vector3(pos.F("X"), pos.F("Y"), pos.F("Z")),
                    Transform = ops != null ? BankModel.EntityMatrix(ops) : Matrix4x4.CreateTranslation(new Vector3(pos.F("X"), pos.F("Y"), pos.F("Z"))),
                };
                p.Area = AreaAt(p.Position.X, p.Position.Z);
                _points.Add(p);
                i++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Wrecks: {path}: {ex.Message}");
            return false;
        }

        ImportSave();
        SpawnModels(index);
        return true;
    }

    // ---------------------------------------------------------------- Modelle

    /// <summary>XMBF ggf. aus dem 12-Byte-Wrapper lösen (wie in RaceProps).</summary>
    static byte[] Unwrap(byte[] b)
    {
        if (b == null || XmbFile.IsXmb(b)) return b!;
        for (int i = 1; i + 4 <= b.Length && i < 64; i++)
            if (b[i] == 'X' && b[i + 1] == 'M' && b[i + 2] == 'B' && b[i + 3] == 'F')
            {
                var r = new byte[b.Length - i];
                Array.Copy(b, i, r, 0, r.Length);
                return r;
            }
        return b;
    }

    void ClearModels()
    {
        var st = _terrain.Streamer;
        if (st != null)
            foreach (var h in _wreckScenes)
            {
                st.Scenes.Remove(h);
                h.Res.Destroy();
            }
        _wreckScenes.Clear();
        foreach (var p in _points) p.Shine = null;
        if (_actors != null)
        {
            try { _actors.Release(); }
            catch (Exception ex) { Log.Warn($"Wrecks: Modelle freigeben: {ex.Message}"); }
            _actors = null;
        }
    }

    /// <summary>
    /// Retail:Pro Wrack wird "Wreck&lt;ID&gt;" (Vollmodell) und "Wreck&lt;ID&gt;S" (Shine-Marker) am
    /// orient_pos_scale der wrecks.bnk instanziiert. Der Shine-Marker verschwindet bei Found.
    /// Alle Matrizen bleiben in Spielkoordinaten; RootMatrix wird erst beim Zeichnen angewendet.
    /// </summary>
    void SpawnModels(IslandIndex index)
    {
        var st = _terrain.Streamer;
        if (st == null) return;
        string path = Path.Combine(index.IslandDir, "scenarics", "wrecks", "pack_wrecks.bnk");
        if (!GameFiles.Exists(path))
        {
            Log.Warn($"Wrecks: {path} fehlt – keine Wrack-Modelle");
            return;
        }
        var log = new List<string>();
        try { _actors = WreckActorBank.Open(path, st.Textures, log); }
        catch (Exception ex) { Log.Warn($"Wrecks: {path}: {ex.Message}"); return; }
        if (_actors == null) return;

        foreach (var p in _points)
        {
            string id = p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var model = _actors.Find("Wreck" + id, log);
            if (model == null)
            {
                if (_log) Log.Warn($"Wrecks: #{p.Index} kein Modell für Wreck{id}");
                continue;
            }
            var h = BuildScene(st, model, p.Transform);
            if (h != null) _wreckScenes.Add(h);

            var shineModel = _actors.Find("Wreck" + id + "S", log);
            if (shineModel == null) continue;
            var shine = BuildScene(st, shineModel, p.Transform);
            if (shine == null) continue;
            shine.Active = !p.Found;
            p.Shine = shine;
            _wreckScenes.Add(shine);
        }
    }

    MapStreamer.SceneHandle? BuildScene(MapStreamer st, ObjectSet set, Matrix4x4 transform)
    {
        if (_actors == null) return null;
        var saved = set.Placements;
        var wrap = new BankModel { Path = _actors.Path, Kind = BankKind.Exterior, Bank = _actors.Model.Bank, Objects = set };
        set.Placements = new List<Matrix4x4> { transform };
        try
        {
            var h = st.BuildScene(wrap, Matrix4x4.Identity, false);
            h.Model = null;   // gemeinsame Bank – in ClearModels() einmal freigeben
            st.Scenes.Add(h); // ohne Registrierung in der Zeichenliste werden die Wracks nie gerendert
            return h;
        }
        catch (Exception ex) { Log.Warn($"Wrecks: Szene {set.Name}: {ex.Message}"); return null; }
        finally { set.Placements = saved; }
    }

    static void UpdateShine(Point p)
    {
        if (p.Shine != null) p.Shine.Active = !p.Found;
    }

    void LoadConfig(string? bnkDir)
    {
        _range = 250f; _activation = 10f; _bipFar = 1.35f; _bipNear = 0.20f;
        if (string.IsNullOrEmpty(bnkDir)) return;
        string path = Path.Combine(bnkDir, "scenarics", "wrecks", "wrecks_config.cpr");
        if (!GameFiles.Exists(path)) return;
        try
        {
            var plain = CprCrypto.Decrypt(GameFiles.ReadAllBytes(path));
            string xml = Encoding.UTF8.GetString(plain).Trim('\0', ' ', '\r', '\n', '\t');
            _range = Attr(xml, "max_detector_range", _range);
            _activation = Attr(xml, "min_activation_distance", _activation);
            _bipFar = Attr(xml, "max_detector_frequency", _bipFar);
            _bipNear = Attr(xml, "min_detector_frequency", _bipNear);
        }
        catch (Exception ex) { Log.Warn($"Wrecks: wrecks_config.cpr: {ex.Message}"); }
    }

    static float Attr(string xml, string name, float def)
    {
        var m = Regex.Match(xml, name + "\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
    }

    /// <summary>level/cruisingzones.bin (CRZODATA), gleiche Polygon-Reihenfolge wie GetCruisingZones().find().</summary>
    void LoadZones(string? levelDir)
    {
        _zones.Clear();
        if (!string.IsNullOrEmpty(levelDir)) _zones.AddRange(ReadZones(levelDir));
    }

    /// <summary>Zonen einer Insel lesen, ohne den aktuellen Inselzustand zu ändern (für die Progressliste).</summary>
    static List<Polygon> ReadZones(string? levelDir)
    {
        var zones = new List<Polygon>();
        if (string.IsNullOrEmpty(levelDir)) return zones;
        string path = Path.Combine(levelDir, "cruisingzones.bin");
        if (!GameFiles.Exists(path)) return zones;
        try
        {
            byte[] d = GameFiles.ReadAllBytes(path);
            if (d.Length < 48 || Encoding.ASCII.GetString(d, 12, 8) != "CRZODATA") return zones;
            int count = (int)Bin.U32(d, 32);
            int o = 48;
            for (int z = 0; z < count && o + 16 <= d.Length; z++)
            {
                int n = (int)Bin.U32(d, o + 4);
                o += 16;
                if (n <= 0 || n > 4096 || o + n * 8 + 8 > d.Length) break;
                var xs = new float[n]; var zs = new float[n];
                for (int i = 0; i < n; i++) { xs[i] = Bin.F32(d, o + i * 8); zs[i] = Bin.F32(d, o + i * 8 + 4); }
                o += n * 8 + 8;
                zones.Add(new Polygon { X = xs, Z = zs });
            }
        }
        catch (Exception ex) { Log.Warn($"Wrecks: cruisingzones.bin: {ex.Message}"); }
        return zones;
    }

    public int AreaAt(float x, float z) => AreaIn(_zones, x, z);

    static int AreaIn(List<Polygon> zones, float x, float z)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var p = zones[i];
            bool inside = false;
            for (int a = 0, b = p.X.Length - 1; a < p.X.Length; b = a++)
                if ((p.Z[a] > z) != (p.Z[b] > z) &&
                    x < (p.X[b] - p.X[a]) * (z - p.Z[a]) / (p.Z[b] - p.Z[a]) + p.X[a])
                    inside = !inside;
            if (inside) return i;
        }
        return -1;
    }

    public int AreaAt(Vector3 game) => AreaAt(game.X, game.Z);

    /// <summary>
    /// Wrack-Zaehler pro Area fuer eine beliebige Insel – auch die gerade nicht aktive.
    /// Liefert (area 0-basiert -> found,total). Liest Punkte und Save-Funde direkt aus den
    /// Inseldateien, ohne den laufenden Wrecks-Zustand zu veraendern.
    /// </summary>
    public static Dictionary<int, (int found, int total)> AreaCounts(string island)
    {
        var result = new Dictionary<int, (int found, int total)>();
        var idx = IslandIndex.Scan(GameFiles.Root, island);
        if (string.IsNullOrEmpty(idx.IslandDir)) return result;

        var zones = ReadZonesFor(idx.LevelDir);
        string path = Path.Combine(idx.IslandDir, "scenarics", "wrecks", "wrecks.bnk");
        if (!GameFiles.Exists(path)) return result;
        try
        {
            var bank = BnkArchive.Open(path);
            var entry = bank.Find("entities.xmb");
            if (entry == null) return result;
            var root = XmbFile.Parse(bank.Read(entry));
            var array = root.Get("ExportType_Entity")?.Get("ArrayOf_Entity__Wreck");
            if (array == null) return result;
            var points = new List<Vector3>();
            foreach (var e in array.Items)
            {
                var pos = e.Get("orient_pos_scale")?.Get("pos");
                if (pos == null) continue;
                points.Add(new Vector3(pos.F("X"), pos.F("Y"), pos.F("Z")));
            }
            var foundIds = new HashSet<int>();
            var saveEntry = TDU2Session.Save?.foundWrecks?
                .Find(f => string.Equals(f.island, island, StringComparison.OrdinalIgnoreCase));
            if (saveEntry != null) foreach (int i in saveEntry.indices) foundIds.Add(i);

            for (int i = 0; i < points.Count; i++)
            {
                int area = AreaIn(zones, points[i].X, points[i].Z);
                if (area < 0) continue;
                if (!result.TryGetValue(area, out var c)) c = (0, 0);
                result[area] = (c.found + (foundIds.Contains(i) ? 1 : 0), c.total + 1);
            }
        }
        catch (Exception ex) { Log.Warn($"Wrecks: AreaCounts({island}): {ex.Message}"); }
        return result;
    }

    static List<Polygon> ReadZonesFor(string? levelDir)
    {
        var zones = new List<Polygon>();
        if (string.IsNullOrEmpty(levelDir)) return zones;
        string path = Path.Combine(levelDir, "cruisingzones.bin");
        if (!GameFiles.Exists(path)) return zones;
        try
        {
            byte[] d = GameFiles.ReadAllBytes(path);
            int count = (int)Bin.U32(d, 32);
            int o = 48;
            for (int z = 0; z < count && o + 16 <= d.Length; z++)
            {
                while (o + 16 <= d.Length)
                {
                    int n = (int)Bin.U32(d, o + 4);
                    if (n >= 3 && n <= 4096 && o + 16 + n * 8 <= d.Length) break;
                    o += 8;
                }
                if (o + 16 > d.Length) break;
                int len = (int)Bin.U32(d, o + 4);
                int po = o + 16;
                var xs = new float[len]; var zs = new float[len];
                for (int i = 0; i < len; i++) { xs[i] = Bin.F32(d, po + i * 8); zs[i] = Bin.F32(d, po + i * 8 + 4); }
                zones.Add(new Polygon { X = xs, Z = zs });
                o = po + len * 8;
            }
        }
        catch (Exception ex) { Log.Warn($"Wrecks: Zonen {path}: {ex.Message}"); }
        return zones;
    }

    public (int total, int found) CountsInArea(int area)
    {
        if (area < 0) return (_points.Count, _found.Count);
        int total = 0, found = 0;
        foreach (var p in _points)
        {
            if (p.Area != area) continue;
            total++;
            if (p.Found) found++;
        }
        return (total, found);
    }

    /// <summary>Retail-Voraussetzung erfüllt? Standardmäßig durch den Test-Schalter umgangen.</summary>
    public bool DetectorUnlocked => TestBypassLevelMinimum || CurrentRacingLevel() >= RequiredRacingLevel;

    /// <summary>
    /// Racinglevel aus dem Spielstand. Der Port schreibt derzeit noch keine Rennfortschrittsklasse,
    /// daher steht hier 1 - mit aktivem Gate (DevMenü-Schalter aus) bleibt der Detektor folglich zu. Sobald
    /// Rennen/Skill-Level gespeichert werden, genügt ein Eintrag "TDU2.RacingLevel" in SaveData.racing.
    /// </summary>
    public static int CurrentRacingLevel()
    {
        var save = TDU2Session.Save;
        if (save?.racing != null)
            foreach (var e in save.racing)
                if (e != null && string.Equals(e.k, "TDU2.RacingLevel", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(e.v, out int lv)) return Math.Max(1, lv);
        return 1;
    }

    // ---------------------------------------------------------------- Finden / Speichern

    void OnFound(Point p)
    {
        if (p.Found) return;
        p.Found = true;
        _found.Add(p.Index);
        string ev = WreckEvent(p);
        _refindBlocked.Remove(p.Index);
        UpdateShine(p);
        _hudCountsDirty = true;
        _pauseDirty = true;
        PushPauseMenu();

        var save = TDU2Session.Save;
        if (save != null)
        {
            var found = save.foundWrecks ??= new List<WreckFoundEntry>();
            var events = save.wreckEvents ??= new List<HouseEntry>();
            var entry = found.Find(f => string.Equals(f.island, _island, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new WreckFoundEntry { island = _island };
                found.Add(entry);
            }
            if (!entry.indices.Contains(p.Index)) entry.indices.Add(p.Index);
            var done = events.Find(e => e.k == ev);
            if (done == null) events.Add(new HouseEntry { k = ev, v = "1" });
            else done.v = (int.TryParse(done.v, out int n) ? n + 1 : 1).ToString();
        }
        SessionAutosave.Schedule(2f);
        // Der Fund-Dialog blockiert den Already-found-Dialog ohnehin (_foundDialogOpen).
        _nearFoundIndex = -1;
        OnFoundDialog(p);
    }

    /// <summary>
    /// Entscheidung fuer einen NEUEN FUND. Hier festlegen, welcher native wrck-Film kommt.
    /// Standard: Dialog_Found_Big = false -> open_small ("1/28 (A)", AlreadyFound=false).
    /// </summary>
    void OnFoundDialog(Point p)
    {
        ShowWreckDialog(WreckDialogEvent.Found, Dialog_Found_Big,
            "New wreck found!!", "Bravo t'as trouvé une carcasse!", playSound: true);
    }

    /// <summary>
    /// Ein bereits gefundener Wrack liegt im Annex-Bereich: Index merken und Dialog oeffnen.
    /// Der Index verhindert, dass der Dialog bei jedem Frame erneut kommt.
    /// </summary>
    void OnAlreadyFound(int index)
    {
        _nearFoundIndex = index;
        OnAlreadyFoundDialog(index);
    }

    /// <summary>
    /// Entscheidung fuer "BEREITS GEFUNDEN". Hier festlegen, welcher native wrck-Film kommt.
    /// Dialog_AlreadyFound_Big = FALSE -> open_small statt des grossen open_big-Menues.
    /// </summary>
    void OnAlreadyFoundDialog(int index)
    {
        ShowWreckDialog(WreckDialogEvent.AlreadyFound, Dialog_AlreadyFound_Big,
            "Wreck already found", "Wrack bereits gefunden - A oeffnet die Karte", playSound: false);
    }

    void ImportSave()
    {
        var save = TDU2Session.Save;
        if (save == null) return;
        _saveImported = true;
        var entry = save.foundWrecks?.Find(f => string.Equals(f.island, _island, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        foreach (int idx in entry.indices)
        {
            if (idx < 0 || idx >= _points.Count || _points[idx].Found) continue;
            _points[idx].Found = true;
            _found.Add(idx);
            UpdateShine(_points[idx]);
        }
        _pauseDirty = true;
        PushPauseMenu();   // gespeicherte Funde auch im Pausemenü anzeigen
    }

    /// <summary>Event-Code eines Wracks (Original: Max(1, Area + 1)).</summary>
    string WreckEvent(Point p) =>
        "CRUISING_WRECK_PART_" + _island.ToUpperInvariant() + "_AREA" + System.Math.Max(1, p.Area + 1);

    // ---------------------------------------------------------------- DevMenu-API

    /// <summary>DevMenu: Detektor-Werte aus wrecks_config.cpr wiederherstellen, ohne die Insel neu zu laden.</summary>
    public void RestoreConfig()
    {
        LoadConfig(_terrain.Streamer?.Index?.BnkDir);
        Log.Info($"Wrecks: Config wiederhergestellt: {_range:0} m / {_activation:0} m / {_bipFar:0.00}/{_bipNear:0.00} s");
    }

    /// <summary>Wrack-Daten der aktuellen Insel neu laden (Config, Zonen, Savegame-Funde).</summary>
    public bool ReloadCurrentIsland()
    {
        var streamer = _terrain.Streamer;
        string? island = streamer?.Index?.IslandName;
        if (streamer == null || string.IsNullOrEmpty(island))
        {
            Log.Warn("Wrecks: Reload – keine Insel geladen");
            return false;
        }
        bool ok = LoadIsland(island, streamer.Index!);
        Log.Info($"Wrecks: {island} neu geladen ({(ok ? "OK" : "fehlgeschlagen")})");
        return ok;
    }

    /// <summary>
    /// Fund setzen oder entfernen (DevMenu). Aktualisiert Liste, Savegame, Event-Zaehler,
    /// HUD-Zaehler, Shine-Marker und plant einen Autosave ein. Ein entfernter Fund wird erst
    /// wieder gefunden, wenn man den Aktivierungsradius verlassen hat.
    /// </summary>
    public void SetFound(int index, bool found)
    {
        if (index < 0 || index >= _points.Count) return;
        var p = _points[index];
        if (p.Found == found) return;
        if (found) { _refindBlocked.Remove(index); OnFound(p); return; }

        p.Found = false;
        _refindBlocked.Add(index);
        UpdateShine(p);
        _found.Remove(p.Index);
        string ev = WreckEvent(p);

        var save = TDU2Session.Save;
        if (save != null)
        {
            if (save.foundWrecks != null)
            {
                var entry = save.foundWrecks.Find(f => string.Equals(f.island, _island, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    entry.indices.Remove(p.Index);
                    if (entry.indices.Count == 0) save.foundWrecks.Remove(entry);
                }
            }
            var done = save.wreckEvents?.Find(e => e.k == ev);
            if (done != null)
            {
                int n = (int.TryParse(done.v, out int v) ? v : 1) - 1;
                if (n > 0) done.v = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                else save.wreckEvents!.Remove(done);
            }
        }
        _hudCountsDirty = true;
        _pauseDirty = true;
        PushPauseMenu();
        SessionAutosave.Schedule(2f);
        var hud = Hud;
        if (hud != null) { PushHudCounts(hud); ResetWreckVars(hud); }
    }

    // ---------------------------------------------------------------- Pausemenü (main.bnk)
    /// <summary>
    /// Fundstand, Gebietszähler, geprüfte Zeilen und Level/XP an das native Pausemenü geben.
    /// Der XP-Gewinn kommt direkt aus den persistent gespeicherten Funden (25 Punkte je Wrack
    /// laut DBRewardXP) – ein zusätzliches Savegame-Feld ist dafür nicht nötig.
    /// </summary>
    internal void PushPauseMenu()
    {
        if (!Enabled || Current == null) return;
        var menu = GameModes.Get(Gamemode.GAME_MODE_PAUSE) is GAME_MODE_PAUSE pause ? pause.Menu : null;
        if (menu == null) return;   // InsertWrecks merkt den Stand bis main.bnk attachiert ist
        int xp = _found.Count * XpPerWreck;
        var info = new wreckinfo
        {
            Island = _island,
            Enabled = Enabled,
            Found = _found.Count,
            Total = _points.Count,
            CruisingPoints = xp,
            CruisingLevel = wreckinfo.LevelForPoints(xp),
            GlobalLevel = wreckinfo.LevelForPoints(xp),
        };
        var manager = _vehicles.Manager;
        var car = manager != null && manager.Current != null ? manager.PositionGame : Vector3.Zero;
        int area = AreaAt(car);
        if (area < 0) area = 0;
        var counts = CountsInArea(area);
        info.Area = area;
        info.AreaTotal = counts.total;
        info.AreaFound = counts.found;
        var rows = new List<wreckinfo.Row>();
        for (int i = 0; i < _points.Count; i++)   // gameprogress_fl ist scrollbar (8 sichtbare Zeilen)
        {
            var p = _points[i];
            rows.Add(new wreckinfo.Row
            {
                Zone = ZoneLabel(p.Area),
                CarName = p.Name,
                // "abgehakt"/geprüft: nativer Zustandstext aus db_<lang>.cpr (ID 3386).
                State = p.Found ? (FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, 3386) ?? "Wrack gefunden") : "--",
                PartsFound = p.Found ? 1 : 0,
                PartsTotal = 1,
            });
        }
        info.Rows = rows.ToArray();
        menu.InsertWrecks(info);
    }

    /// <summary>Zonenlabel wie im Retail-Binary (%c%d, 'h' Hawaii / 'i' Ibiza, 1-basiert).</summary>
    string ZoneLabel(int area)
    {
        char c = _island.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 'h' : 'i';
        return area < 0 ? c.ToString() : c + (area + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- natives HUD

    VmfPlayer? Hud => _vehicles.Manager?.Minimap?.Hud;

    void UpdateHud(float nearest, float dt)
    {
        var hud = Hud;
        if (hud == null) return;
        if (!_hudAttached) { FrontendSounds.Attach(hud); _hudAttached = true; }
        if (_hudCountsDirty)
        {
            PushHudCounts(hud);
            _hudCountsDirty = false;
        }
        // Der Fund-Dialog darf nicht durch einen zweiten Detektor-Zyklus ueberschrieben werden,
        // wenn noch ein anderes Wrack in Reichweite liegt. Beide num_loop-Pfade werden jeden
        // Frame zurueckgesetzt: der Film inkrementiert /HUD/wrck:num_loop in frame 20 und
        // wuerde sonst nach fuenf frames den Dialog von selbst oeffnen.
        if (_foundDialogOpen || _foundUiHold > 0f) return;
        hud.SetValue("/HUD/wrck:num_loop", 0f);
        hud.SetValue("/:num_loop", 0f);

        bool on = nearest >= 0f;
        var clip = hud.Find("/HUD/wrck");
        // Bereits gefundenes Wrack in Reichweite: nearest bleibt -1, _detectorOn war nach
        // dem Fund-Dialog schon false - ohne diesen Fall bleibt der Clip haengen.
        if (!on && clip != null && clip.Visible)
        {
            clip.Visible = false;
            ResetWreckVars(hud);
            hud.GotoLabel("/HUD/wrck", "detect_off", false);
        }
        if (on != _detectorOn)
        {
            _detectorOn = on;
            if (clip != null) clip.Visible = on;
            ResetWreckVars(hud);
            if (on)
            {
                hud.SetValue("/:NbWrecksFound", _found.Count);
                hud.SetValue("/:NbWrecksTotal", _points.Count);
            }
            // fake=true laesst den nativen Loop (frame 15..20) laufen: dadurch sind Kreis,
            // Icon und Puls-Animation sichtbar. Der Film meldet dort auch wrckwarn, das wird
            // aber unterdrueckt und in Update() entfernungsabhaengig getaktet.
            // num_loop wird oben jeden Frame auf 0 gehalten, damit frame 20 nicht selbst
            // open_small ausloest.
            hud.SetValue("/:fake", on);
            hud.GotoLabel("/HUD/wrck", on ? "detect_on" : "detect_off", true);
        }
    }

    void PushHudCounts(VmfPlayer hud)
    {
        hud.SetValue("/:NbWrecksFound", _found.Count);
        hud.SetValue("/:NbWrecksTotal", _points.Count);
        hud.SetValue("/HUD/wrck:AllWrecksFound", _points.Count > 0 && _found.Count >= _points.Count);
    }

    /// <summary>
    /// Niedrigste Ebene: setzt die Flash-Variablen und springt zum open_big / open_small Label.
    /// Die Entscheidung, ob big oder small, trifft der Aufrufer (OnFoundDialog / OnAlreadyFoundDialog).
    /// </summary>
    void ShowWreckDialog(WreckDialogEvent ev, bool big, string title, string text, bool playSound)
    {
        var hud = Hud;
        if (hud == null) return;
        PushHudCounts(hud);
        _hudCountsDirty = false;
        _dialogEvent = ev;
        _foundDialogBig = big;
        hud.SetValue("/:fake", false);
        hud.SetValue("/:num_loop", 0f);
        hud.SetValue("/HUD/wrck:num_loop", 0f);
        // AlreadyFound=false -> Film zeigt den Gefunden-Zaehler (small), true -> "Wreck already found" (big).
        hud.SetValue("/HUD/wrck:AlreadyFound", big);
        hud.SetValue("/HUD/wrck:AllWrecksFound", _points.Count > 0 && _found.Count >= _points.Count);
        string openLabel = big ? "open_big" : "open_small";
        hud.SetVariable("/:title_wrck_box", title);
        hud.SetVariable("/:txt_wrck_box", text);
        var clip = hud.Find("/HUD/wrck");
        if (clip != null) clip.Visible = true;
        hud.GotoLabel("/HUD/wrck", openLabel, true);
        if (playSound) FrontendSounds.Play("wrckfund");
        // Kein detect_off in diesem/dem naechsten Frame: sonst waere direkt der "Verloren"-Sound
        // zu horen, statt der Fund-Dialog. Der naechste Detect-Zyklus startet mit detect_on.
        _detectorOn = false;
        _beepTimer = 0f;
        _pulseTimer = 0f;
        _dialogAge = 0f;
        _foundUiHold = 2f;
        _foundDialogOpen = true;
        _pendingHide = false;
    }

    /// <summary>
    /// Wird beim Schliessen des Fund-Dialogs gerufen (Confirm oder Timeout).
    /// Der close-Label wird aus dem gemerkten Oeffnungs-Dialog abgeleitet.
    /// </summary>
    void OnDialogClosed()
    {
        var hud = Hud;
        if (hud == null) return;
        string closeLabel = _foundDialogBig ? "close_big" : "close_small";
        hud.GotoLabel("/HUD/wrck", closeLabel, true);
        ResetWreckVars(hud);
        _foundDialogOpen = false;
        _foundDialogBig = false;
        // Standard: Index behalten -> kein Reopen, solange man am Wrack steht.
        // Nur mit Dialog_AlreadyFound_RepeatWhenStaying = true wird er freigegeben.
        if (_dialogEvent == WreckDialogEvent.AlreadyFound && Dialog_AlreadyFound_RepeatWhenStaying)
            _nearFoundIndex = -1;
        _dialogEvent = null;
        _detectorOn = false;
        _beepTimer = 0f;
        _pulseTimer = 0f;
        _dialogAge = 0f;
        _foundUiHold = 0.8f;
        _pendingHide = true;
    }

    void SetDetector(bool on)
    {
        if (!on && !_detectorOn && !_foundDialogOpen) return;
        _detectorOn = on;
        _beepTimer = 0f;
        _pulseTimer = 0f;
        ClearMinimapBlips();
        if (!on) { _foundDialogOpen = false; _pendingHide = false; }
        var hud = Hud;
        if (hud == null) return;
        var clip = hud.Find("/HUD/wrck");
        if (clip != null) clip.Visible = on;
        ResetWreckVars(hud);
        hud.SetValue("/:fake", on);
        hud.GotoLabel("/HUD/wrck", on ? "detect_on" : "detect_off", false);
    }

    void UpdateMinimapBlips(float nearest)
    {
        if (!_showMinimapBlips) return;
        ClearMinimapBlips();
        if (nearest < 0f) return;
        var car = _vehicles.Manager.PositionGame;
        var near = new List<(float d, Point p)>();
        foreach (var p in _points)
        {
            if (p.Found) continue;
            float dx = p.Position.X - car.X, dz = p.Position.Z - car.Z;
            float d = dx * dx + dz * dz;
            if (d <= _range * _range * 4f) near.Add((d, p));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < near.Count && i < 4; i++)
            Minimap.SetObjective(4 + i, near[i].p.Position, "shop", -1);   // hud.bnk hat kein eigenes wreck-Symbol
    }

    void ClearMinimapBlips()
    {
        for (int i = 4; i < Minimap.Objectives.Length; i++) Minimap.ClearObjective(i);
    }
}