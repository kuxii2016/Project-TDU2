using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Streams a island around the viewer:
///   LOD2 = Island banks (always; hidden area by area as soon as the area is loaded)
///   LOD1 = Area banks (terrain pieces per sector + area objects + spot exteriors “_low”)
///   LOD0 = Sector banks (full terrain + objects + spot exteriors)
/// Instead of GameObjects, <see cref="Node"/>s are created (mesh draw commands + RenderObjects).
/// Coordinates: Game coordinates; render space = game coordinates * <see cref="RootMatrix"/>
/// </summary>
public sealed partial class MapStreamer : IDisposable
{
    #region Settings (values from the Unity scene TDU2Main.unity, TDU2MapStreamer component)
    public string Island = "ibiza";
    public float AreaLoadDistance = 900f;
    public float AreaUnloadMargin = 1000f;
    public float SectorLoadDistance = 1500f;   // Unity: 900 (Objekte/Bäume ploppten zu spät auf)
    public float SectorUnloadMargin = 300f;
    public int MaxParallelLoads = 4;
    public bool ShowIslandLod = true;
    public bool LoadAreaObjects = true;
    public bool LoadSectorObjects = true;
    public bool LoadVegetation = true;
    public bool LoadOtherPlacements = true;
    public bool LoadActors = true;
    public bool LoadWorldActors = true;
    public bool LoadSpotExteriors = true;
    public float VegetationDistance = 1100f;   // Unity: 1000
    /// <summary>Tunnelportale: HMAP-Loch-Kacheln auslassen und das Sektormodell BLOCK als Gelände zeichnen (wie das Original).</summary>
    public bool CutTunnelPortals = true;
    public bool ShowSignText = true;
    public bool ShowWater = true;
    /// <summary>Wolkenkuppel des Originals (fx/clouds.bnk, World/TDU2Clouds).</summary>
    public bool ShowClouds = true;
    /// <summary>Sternenhimmel des Originals (Dom-Mesh der exe, World/TDU2Stars).</summary>
    public bool ShowStars = true;
    /// <summary>Stromleitungen aus den Sektor-.pen (Durchhang/Aufhängepunkte angenähert, siehe PowerWires).</summary>
    public bool ShowPowerWires = true;
    /// <summary>Rotierende Windrad-Rotoren aus den verschachtelten _sc-Bänken der Sektoren (fu_eolea_act).</summary>
    public bool LoadRotors = true;
    public float SeaLevel = 0f;
    /// <summary>Höhe der Meeresfläche relativ zu SeaLevel (Unity-Projekt: −1,5 m; Original-Spiel: 0).</summary>
    public float WaterOffset = -0.15f;
    /// <summary>Wasserhöhe der Insel wie im Original (unpacked.exe 0xcb36a0: Insel-ID 1 = HAWAI → −1,8, sonst 0;
    /// Inseltabelle IBIZA/HAWAI/CASINO bei .data 0x164a820). Die Sektor-Meer-Meshes von Hawaii liegen bereits auf −1,8 (BBox im -o.3dd).</summary>
    public float IslandWaterHeight => (_index?.IslandName ?? Island ?? "").StartsWith("hawai", StringComparison.OrdinalIgnoreCase) ? -1.8f : 0f;
    /// <summary>Höhe der großen Meeresfläche und der Spiegelebene.</summary>
    public float WaterLevel => SeaLevel + IslandWaterHeight + WaterOffset;
    public bool LoadGrass = true;
    // Werte wie in der Unity-Szene TDU2Main.unity (TDU2MapStreamer: GrassRadius 120, GrassDensity 0.9, GrassSpacing 1.2)
    public float GrassRadius = 120f;
    public float GrassDensity = 0.9f;
    public float GrassSpacing = 1.2f;
    public bool LoadInteriors = true;
    public bool CenterWorld = true;
    public bool StartFromGamePc = true;
    public Vector3 StartPositionGame = new(97f, 8f, 363f);
    public float DetailTiling = 0.098f;
    public float DetailStrength = 1f;
    public float ShadowMapStrength = 0.6f;
    public float LodDistanceScale = 4f;   // Unity: 3 (LOD-Wechsel später)
    public bool CullLastLod = false;   // Unity: true – letzte LOD bleibt sichtbar statt zu verschwinden
    public bool OriginalLighting = true;
    public float ObjectBrightness = .5f;
    public float WrapLighting = 0.35f;
    public bool ShowGlowMaterials = true;
    public float WindStrength = 1f;
    public float VertexColorStrength = 0.5f;
    public float SectorSkirt = 12f, AreaSkirt = 40f, IslandSkirt = 150f;
    public float TerrainLower = 0.1f;
    public float TerrainDepthPush = 0.0015f;
    #endregion

    /// <summary>Corresponds to a GameObject in the Unity project: custom meshes and/or objects that can be enabled or disabled.</summary>
    public sealed class Node
    {
        public string Name = "";
        public bool Active = true;
        public readonly List<(IGpuMesh Mesh, IGpuMaterial Material, Matrix4x4 Model)> Meshes = new();
        public readonly List<RenderObject> Objects = new();
        public bool Disposed;
        public Vector3 BoundsMin = new(float.MaxValue), BoundsMax = new(float.MinValue);
        public bool HasBounds => BoundsMin.X <= BoundsMax.X;
        public void Encapsulate(Vector3 min, Vector3 max) { BoundsMin = Vector3.Min(BoundsMin, min); BoundsMax = Vector3.Max(BoundsMax, max); }
    }

    // --- Laufzeit ---
    sealed class AreaPayload { public double LoadMs; public BankModel Model = null!; public List<TerrainPiece> Pieces = new(); public List<(Spot spot, BankModel model)> Exteriors = new(); }
    sealed class SectorPayload
    {
        public double LoadMs;
        public BankModel Model = null!;
        public MeshData? Terrain;
        // Skirt-Stücke (Engine-Zusatz zum LOD1, siehe TerrainMeshBuilder.BuildSectorSkirts) – nicht in der Kollision
        public List<(MeshData Mesh, Vector2 Probe)>? Skirts;
        // Loch-Kacheln des HMAP als Zellen [z * cells + x]: dort liefert TryGetGroundHeight keine Rasterhöhe (BLOCK gilt).
        public bool[]? DriveHoles;
        public PowerWires.PenFile? Pen;
        public WindTurbines.SectorData? Rotors;
        public List<(Spot spot, BankModel model)> Exteriors = new();
    }

    sealed class AreaState
    {
        public AreaKey Key;
        public Task<AreaPayload>? Task;
        public readonly List<Node> Nodes = new();
        public BankModel? Model;
        public readonly List<BankModel> ExtraModels = new();
        public readonly BuildResources Res = new();
        public readonly List<(SectorKey key, Node node)> Registered = new();
        public readonly List<StaticCollider> Colliders = new();
        public bool Built, Failed;
    }

    sealed class SectorState
    {
        public SectorKey Key;
        public Task<SectorPayload>? Task;
        public readonly List<Node> Nodes = new();
        public BankModel? Model;
        public readonly List<BankModel> ExtraModels = new();
        public readonly BuildResources Res = new();
        public bool Built, Failed, VegetationDone, ActorsDone, ActorSignsDone;
        public PlacementRenderer? Placements;
        public GrassRenderer? Grass;
        public int Instances;
        public PowerWires.SectorWires? Wires;
        public WindTurbines.SectorRuntime? Rotors;
        public readonly List<StaticCollider> Colliders = new();
        public SectorPayload? Data;
        // Loch-Kacheln des HMAP (Raster [z * cells + x], cells = Heights.Size - 1): Rückfall-Bodenhöhe meidet sie.
        public bool[]? DriveHoles;
        // Skirt-Stücke mit Probe-Punkt (Welt) jenseits der Kante: aus, solange dort ein LOD0-Sektor steht
        public readonly List<(Node Node, Vector3 Probe)> Skirts = new();
        public float WireWait;
        public double WireNextTry;
    }

    private readonly IRenderer _renderer;
    private IslandIndex? _index;
    private GlobalTextureCache _textures = null!;
    private SpotsFile? _spots;
    private ObjectBuildOptions _opt = new();
    private ObjectBuildOptions _collOpt = new() { Collision = true };
    private readonly Dictionary<AreaKey, Node> _islandPieces = new();
    private readonly BuildResources _islandRes = new();
    private BankModel? _islandModel;
    private readonly Dictionary<AreaKey, AreaState> _areas = new();
    private readonly Dictionary<SectorKey, SectorState> _sectors = new();
    private readonly Dictionary<SectorKey, List<Node>> _lod1BySector = new();
    private readonly Dictionary<Node, List<SectorKey>> _lod1Keys = new();
    private Task<ObjectLibrary>? _libraryTask;
    private ObjectLibrary? _library;
    private Task<WorldActorLib>? _actorsTask;
    private WorldActorLib? _actors;
    private WorldActorBodies? _actorBodies;
    private volatile RoadSignLayout? _signLayout;
    private IGpuMaterial? _signMat;
    private IGpuMesh? _waterMesh;
    private OceanNoise? _oceanNoise;        // NoiseNMap des Meeres (TD_Ocean)
    private IGpuMaterial? _oceanIsland;     // Meer der Inselebene (ibiza-o / Border: Insel-Colormap) für die große Wasserfläche
    private Vector3 _waterPos;
    private Clouds? _clouds;
    private Stars? _stars;
    private WetGround? _wetGround;
    private GrassTypes? _grassTypes;
    private IGpuTexture? _grassAtlas, _bushAtlas;
    private int _grassCells = 16, _bushCells = 4;
    private GrassMap? _grassNoise;   // tallgrass.bnk noise.rle (Größenrauschen, exe 0xc68c28)
    private bool _waterNormalSet;
    private double _time, _nextScan, _nextTexRetry, _nextLodCheck;
    private Vector3 _viewerGame;
    private string _status = "";

    public IslandIndex? Index => _index;
    public SpotsFile? Spots => _spots;
    public GlobalTextureCache Textures => _textures;
    public string Status => _status;
    /// <summary>Spielkoordinaten -> Render-Raum (Unity: _root).</summary>
    public Matrix4x4 RootMatrix { get; private set; } = Matrix4x4.Identity;
    public Vector3 RootOffset => RootMatrix.Translation;
    /// <summary>Startposition im Render-Raum (Unity: Viewer.position = _root.TransformPoint(start)).</summary>
    public Vector3 StartPosition { get; private set; }
    public Vector3 ViewerGame => _viewerGame;

    public MapStreamer(IRenderer renderer) => _renderer = renderer;

    public Vector3 GameToRender(Vector3 g) => Vector3.Transform(g, RootMatrix);

    /// <summary>Zusätzliche Lichter dieses Bilds (Render-Raum), z.B. Scheinwerfer der Autos. Vor Render() füllen.</summary>
    public readonly List<LampLight> ExtraLamps = new();
    LampLight[] _lampMerge = Array.Empty<LampLight>();

    /// <summary>Floating Origin für das Zeichnen (Unity: FloatingOrigin verschiebt _root): ist die Kamera weiter als
    /// <see cref="RenderShiftDistance"/> vom Render-Ursprung entfernt, wird der Ursprung auf sie gelegt.</summary>
    public float RenderShiftDistance = 300f;

    /// <summary>Render-Ursprung bei Bedarf zur Kamera verschieben; delta = Verschiebung, um die Render-Positionen
    /// (Kamera) zu verringern sind.</summary>
    public bool ShiftRenderOrigin(Vector3 cameraRender, out Vector3 delta)
    {
        delta = Vector3.Zero;
        if (cameraRender.X * cameraRender.X + cameraRender.Z * cameraRender.Z < RenderShiftDistance * RenderShiftDistance) return false;
        delta = new Vector3(MathF.Round(cameraRender.X), 0f, MathF.Round(cameraRender.Z));
        RootMatrix = RootMatrix * Matrix4x4.CreateTranslation(-delta);
        return true;
    }
    public Vector3 RenderToGame(Vector3 r) => r - RootOffset;

    bool GamePcStartPoint(out Vector3 start)
    {
        start = default;
        try
        {
            var gp = GamePC.Load(_index?.BnkDir);
            if (gp == null || _index == null) return false;
            bool sameIsland = string.IsNullOrEmpty(gp.StartIsland) || (_index.IslandName ?? Island ?? "").StartsWith(gp.StartIsland, StringComparison.OrdinalIgnoreCase);
            if (gp.HasStartPosition && sameIsland) { start = gp.StartPosition + Vector3.UnitY * 30f; return true; }
            if (string.IsNullOrEmpty(_index.SpotsFile) || !GameFiles.Exists(_index.SpotsFile)) return false;
            var house = gp.FindHouse(SpotsFile.Parse(GameFiles.ReadAllBytes(_index.SpotsFile)).Spots);
            if (house == null) return false;
            start = house.Position + Vector3.UnitY * 30f;
            Log.Info("Startpunkt (GamePC.cpr): Haus " + string.Join("/", gp.Houses) + " bei " + house.Position);
            return true;
        }
        catch (Exception ex) { Log.Error("GamePC.cpr Startpunkt: " + ex.Message); return false; }
    }

    public bool Initialize(string root)
    {
        try
        {
            Log.Info("MapStreamer: Initialisierung gestartet.");
            MaterialFactory.Renderer = _renderer;
            MaterialFactory.ShadowMapStrength = ShadowMapStrength;
            MaterialFactory.ObjectBrightness = ObjectBrightness;
            BankModel.IncludeInteriors = LoadInteriors;
            MaterialFactory.WrapLighting = WrapLighting;
            if (OriginalLighting)
            {
                // Original-Beleuchtung: Ambient + (Sonne − Ambient) x N.L, ohne Wrap. Helligkeit der Objekte aus der Config
                // (LightingConfig.ObjectBrightness, 1 = unverändert)
                MaterialFactory.WrapLighting = 0f;
            }
            MaterialFactory.VertexColorStrength = VertexColorStrength;
            MaterialFactory.ShowGlowMaterials = ShowGlowMaterials;
            MaterialFactory.WindStrength = WindStrength;

            _opt = new ObjectBuildOptions { LodDistanceScale = LodDistanceScale, CullLastLod = CullLastLod };
            _collOpt = new ObjectBuildOptions { LodDistanceScale = LodDistanceScale, CullLastLod = CullLastLod, Collision = true };
            _textures = new GlobalTextureCache(_renderer);

            _index = IslandIndex.Scan(root, Island);
            Log.Info("Island-Index: " + _index);

            var center = new Vector3(_index.AreasX * WorldLayout.AreaSize * 0.5f, 0, _index.AreasZ * WorldLayout.AreaSize * 0.5f);
            RootMatrix = CenterWorld ? Matrix4x4.CreateTranslation(-center.X, 0f, -center.Z) : Matrix4x4.Identity;

            var start = StartPositionGame;
            if (StartFromGamePc && GamePcStartPoint(out var gpStart)) start = gpStart;
            else if (start.X < 0 || start.Z < 0) start = new Vector3(center.X, StartPositionGame.Y, center.Z);
            StartPosition = GameToRender(start);
            _viewerGame = start;

            // World Actors (Ampeln, Schilder, Bänke ...) im Hintergrund
            var tex = _textures;
            if (LoadWorldActors && _index.WorldActorsBank != null)
            {
                string wp = _index.WorldActorsBank;
                _actorsTask = Task.Run(() => WorldActorLib.Load(wp, tex));
            }

            // Library (Bäume/Büsche/Props) im Hintergrund
            if (_index.CommonWorldBank != null && (LoadVegetation || LoadOtherPlacements))
            {
                string p = _index.CommonWorldBank;
                Log.Debug("Load CommonWorldBank: " + p);
                if (!GameFiles.Exists(p)) Log.Error("CommonWorldBank does not exist: " + p);
                else
                    _libraryTask = Task.Run(() =>
                    {
                        try
                        {
                            var lib = ObjectLibrary.Load(p, tex);
                            try { _signLayout = RoadSignLayout.Load(p); } catch (Exception ex) { Log.Warn("panel.ppl: " + ex.Message); }
                            return lib;
                        }
                        catch (Exception ex) { Log.Error("Error loading the ObjectLibrary", ex); throw; }
                    });
            }

            if (!string.IsNullOrEmpty(_index.SpotsFile))
            {
                try
                {
                    if (!GameFiles.Exists(_index.SpotsFile)) Log.Warn("Spot file not found: " + _index.SpotsFile);
                    else _spots = SpotsFile.Parse(GameFiles.ReadAllBytes(_index.SpotsFile));
                }
                catch (Exception ex) { Log.Warn("spots.bin error: " + ex); }
            }

            Log.Debug("Build Island LOD...");
            BuildIslandLod();
            LoadAircraft();

            if (ShowWater)
            {
                Log.Debug("Build Water...");
                BuildWater(center);
            }
            if (ShowClouds) _clouds = Clouds.Load(_index.FxDir, _renderer, _textures);
            if (ShowStars) _stars = Stars.Create(_index.BnkDir, _renderer, _textures);
            _wetGround = WetGround.Create(_index.BnkDir, _textures);   // Regen auf der Straße (WFX_WetGrd)
            if (LoadGrass)
            {
                Log.Debug("Initialize Grass...");
                SetupGrass();
            }

            _status = "ready";
            Log.Info("MapStreamer: Initialisierung abgeschlossen.");
            return true;
        }
        catch (Exception ex)
        {
            _status = "error: " + ex.Message;
            Log.Error("MapStreamer: Initialisierung fehlgeschlagen", ex);
            return false;
        }
    }

    void BuildIslandLod()
    {
        if (_index?.IslandBank == null) { Log.Warn("Island Bank is missing"); return; }
        var model = BankModel.Load(_index.IslandBank, BankKind.Island, loadObjects: false, loadPlacements: false, loadGrass: false);
        if (model.Heights == null) { Log.Warn("Island heightmap not found"); return; }
        int ax = System.Math.Max(1, _index.AreasX), az = System.Math.Max(1, _index.AreasZ);
        var pieces = TerrainMeshBuilder.BuildIslandPieces(model.Heights, ax, az, IslandSkirt);
        var color = model.GetTexture(model.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, null, _renderer);
        var mat = MaterialFactory.CreateTerrainMaterial(color, null, null, null, DetailTiling, 0f);
        _islandRes.Owned.Add(mat);
        foreach (var p in pieces)
        {
            var node = CreateMeshNode(p.Area + "_LOD2", p.WorldOrigin, p.Mesh, mat, _islandRes);
            node.Active = ShowIslandLod;
            _islandPieces[p.Area] = node;
        }
        _islandModel = model;
    }

    /// <summary>
    /// Sea material from a sector (level 0, 768 m) or area bank (level 1, 6144 m) with its colormap, just like the original
    /// (FUN_008be1e0: levelObj+0x670 = COLOR of the level's TD_HeightMap). null without NoiseNMap/Renderer.
    /// </summary>
    IGpuMaterial? OceanFor(BankModel model, Vector3 origin, int level, BuildResources res)
    {
        if (_oceanNoise == null) return null;
        var color = model.ColorMapKey != 0 ? model.GetTexture(model.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, _textures, _renderer) : null;
        float ext = level == 0 ? WorldLayout.SectorSize : WorldLayout.AreaSize;
        var m = MaterialFactory.CreateOceanMaterial(color, new Vector2(origin.X, origin.Z), new Vector2(ext, ext), level);
        if (m != null) res.Owned.Add(m);
        return m;
    }

    /// <summary>Sea area (300 km, 128x128 quads, world UVs) at WaterLevel = SeaLevel + island height + WaterOffset (Unity: BuildWater).</summary>
    void BuildWater(Vector3 center)
    {
        const float half = 150000f;
        const int segments = 128;
        float step = half * 2f / segments;
        var verts = new ObjectVertex[(segments + 1) * (segments + 1)];
        var tris = new uint[segments * segments * 6];
        uint white = ObjectVertex.PackColor(255, 255, 255, 255);
        int idx = 0;
        for (int z = 0; z <= segments; z++)
            for (int x = 0; x <= segments; x++)
            {
                float wx = -half + x * step, wz = -half + z * step;
                var uv = new Vector2(wx * 0.00005f, wz * 0.00005f);
                verts[idx++] = new ObjectVertex { Position = new Vector3(wx, 0f, wz), Normal = Vector3.UnitY, Color = white, Uv0 = uv, Uv1 = uv, Uv2 = uv, Uv3 = uv };
            }
        int t = 0;
        for (int z = 0; z < segments; z++)
            for (int x = 0; x < segments; x++)
            {
                uint i0 = (uint)(z * (segments + 1) + x), i1 = i0 + 1, i2 = i0 + segments + 1, i3 = i2 + 1;
                tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
            }
        _waterMesh = _renderer.CreateMesh(verts, tris);
        _islandRes.Owned.Add(_waterMesh);
        _waterPos = new Vector3(center.X, WaterLevel, center.Z);
        // Ocean like TD_Ocean: NoiseNMap + island colormap (ibiza-o vs_2: colormap UV = Welt.xz / (6144 · AreaSize), border clipped
        // like the border meshes). Sector and Area oceans (…-o) use their own colormaps from the banks (OceanFor).
        try
        {
            _oceanNoise = new OceanNoise(_renderer);
            _islandRes.Owned.Add(_oceanNoise);
            MaterialFactory.OceanNoiseTexture = _oceanNoise.Texture;
            var islandColor = _islandModel?.GetTexture(_islandModel.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, null, _renderer);
            int ax = System.Math.Max(1, _index?.AreasX ?? 1), az = System.Math.Max(1, _index?.AreasZ ?? 1);
            _oceanIsland = MaterialFactory.CreateOceanMaterial(islandColor, Vector2.Zero, new Vector2(ax * WorldLayout.AreaSize, az * WorldLayout.AreaSize), 2);
            if (_oceanIsland != null) _islandRes.Owned.Add(_oceanIsland);
        }
        catch (Exception ex) { Log.Warn("Meer (TD_Ocean): " + ex.Message); }
    }

    /// <summary>Load grass types (fx/grasstypes.xml) and atlases (fx/tallgrass.bnk).</summary>
    void SetupGrass()
    {
        try
        {
            if (_index!.GrassTypesXml != null) _grassTypes = GrassTypes.Parse(GameFiles.ReadAllText(_index.GrassTypesXml));
            if (_index.TallGrassBank == null || _grassTypes == null) return;
            var bank = BnkArchive.Open(_index.TallGrassBank);
            _textures.IndexBank(bank);
            _grassAtlas = _textures.Get(_textures.KeyByName("setgrass.2db"), TextureWrap.Clamp, TextureWrap.Clamp);
            _bushAtlas = _textures.Get(_textures.KeyByName("setbush.2db"), TextureWrap.Clamp, TextureWrap.Clamp);
            // noise.rle: 256×256-Byteraster im selben RLE-Format wie die .pgr der Sektoren
            var noiseEntry = bank.Find("noise.rle");
            _grassNoise = noiseEntry != null ? GrassMap.Parse(bank.Read(noiseEntry)) : null;
            int iniGrass = 0, iniBush = 0;
            if (_index.FxIni != null)
            {
                // fx.ini [GRASS] NBGRASSBITMAPS / NBBUSHBITMAPS = Anzahl Sprites im Atlas (Ibiza 9/4, Hawaii 5/1)
                foreach (var line in GameFiles.ReadAllLines(_index.FxIni))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim().ToUpperInvariant();
                    int.TryParse(parts[1].Split('/')[0].Trim(), out int val);
                    if (key == "NBGRASSBITMAPS") iniGrass = val;
                    else if (key == "NBBUSHBITMAPS") iniBush = val;
                }
            }
            if (_grassAtlas != null) _grassCells = iniGrass > 0 ? iniGrass : System.Math.Max(1, _grassAtlas.Width / System.Math.Max(1, _grassAtlas.Height));
            if (_bushAtlas != null) _bushCells = iniBush > 0 ? iniBush : System.Math.Max(1, _bushAtlas.Width / System.Math.Max(1, _bushAtlas.Height));
            Log.Info($"Gras: {_grassTypes.Ordered.Count} Typen, Atlas {_grassCells}/{_bushCells}, Rauschen {(_grassNoise != null ? _grassNoise.Width + "x" + _grassNoise.Height : "fehlt")}");
        }
        catch (Exception ex) { Log.Warn("Gras konnte nicht geladen werden: " + ex.Message); }
    }

    /// <summary>Call per frame. viewerRender = camera position in the render space.</summary>
    public void Update(Vector3 viewerRender, float dt)
    {
        if (_index == null) return;
        _time += dt;
        _viewerGame = RenderToGame(viewerRender);
        long tp = System.Diagnostics.Stopwatch.GetTimestamp();
        // Meer (TD_Ocean): NoiseNMap weiterlaufen lassen, Spiegelbild an der Wasserebene (SEAREF_N)
        _oceanNoise?.Update(dt);
        _clouds?.Update(dt, _time);
        _wetGround?.Update(dt);
        RenderFx.OceanOn = ShowWater && _oceanIsland != null && !WorldWeather.Inside;
        RenderFx.OceanY = GameToRender(new Vector3(0f, WaterLevel, 0f)).Y;
        if (_time >= _nextTexRetry) { _nextTexRetry = _time + 1.0; MaterialFactory.RetryPendingTextures((k, u, v) => _textures?.Get(k, u, v)); }
        tp = Lap("Streamer: Texturen nachladen", tp);
        PollLibrary();
        tp = Lap("Streamer: Bibliothek", tp);
        PollAircraft(dt);
        tp = Lap("Streamer: Flugzeuge", tp);
        PollActors();
        _actorBodies?.Sync();
        tp = Lap("Streamer: Akteure", tp);
        TextureAnimations.Update(_renderer, (float)_time);
        tp = Lap("Streamer: Textur-Animationen", tp);
        TrafficLights.Tick();
        UpdatePowerWires(dt);
        tp = Lap("Streamer: Ampeln/Stromleitungen", tp);
        // Rotorwinkel laufen in Spielkoordinaten weiter; RootMatrix/RootOffset werden erst beim Zeichnen angewandt.
        foreach (var st in _sectors.Values) st.Rotors?.Tick((float)_time);
        tp = Lap("Streamer: Windräder", tp);
        long tc = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateColliders();
        PerfLog.Add("Kollider eintragen", System.Diagnostics.Stopwatch.GetElapsedTime(tc).TotalMilliseconds);
        tp = System.Diagnostics.Stopwatch.GetTimestamp();
        // Gras: Stücke aller Sektoren teilen sich ein Zeitbudget je Bild (mehrere neue Sektoren bauten sonst im selben Bild je 6 Stücke)
        foreach (var st in _sectors.Values)
        {
            if (!st.Built || st.Grass == null) continue;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds > GrassBudgetMs) break;
            st.Grass.Update(_renderer, _viewerGame, _time);
        }
        tp = Lap("Streamer: Gras", tp);
        if (_time >= _nextScan)
        {
            _nextScan = _time + 0.25;
            ScanAreas();
            ScanSectors();
        }
        tp = Lap("Streamer: Bereiche/Sektoren prüfen", tp);
        if (_time >= _nextLodCheck) { _nextLodCheck = _time + 1.0; EnforceLod1(); if (_lod1Blocked) ApplyIslandLod(); }
        Lap("Streamer: LOD prüfen", tp);
        long tb = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildCompleted();
        PerfLog.Add("Kacheln bauen (GPU-Upload)", System.Diagnostics.Stopwatch.GetElapsedTime(tb).TotalMilliseconds);
        tb = System.Diagnostics.Stopwatch.GetTimestamp();
        ProcessVegetationWork();
        PerfLog.Add("Streamer: Platzierungen einbauen", System.Diagnostics.Stopwatch.GetElapsedTime(tb).TotalMilliseconds);
        // Kartengelände im Hintergrund vorbereiten, sobald die Welt um den Spieler steht (dann ist die Karte beim ersten Öffnen scharf)
        tp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (PreloadMapTerrain && _mapTask == null && _time > 15.0) RequestMapTerrain();   // ein Hintergrund-Thread
        PollMapTerrain();
        Lap("Streamer: Kartengelände", tp);
    }

    /// <summary>Measurement point for [DIAG Perf]: Enter time since t0; return a new starting point.</summary>
    static long Lap(string name, long t0)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        PerfLog.Add(name, (now - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        return now;
    }

    /// <summary>
    /// Terrain tile for the large map: an entire area from its own bank (elevation 24 m, color map 1024² = 6 m per pixel)
    /// instead of the coarse island LOD. Is only rendered by the map (<see cref="RenderMapTerrain"/>), invisible in-game.
    /// </summary>
    public sealed class MapTile { public AreaKey Key; public IGpuMesh? Mesh; public IGpuMaterial? Material; public IGpuTexture? Color; public Vector3 Origin; }
    /// <summary>Kartengelände (alle Areas mit eigener Colormap) nach dem Start im Hintergrund laden.</summary>
    public bool PreloadMapTerrain = true;
    /// <summary>Raster der Kartenkacheln in Area-Zellen (1 = 24 m, 2 = 48 m, 4 = 96 m).</summary>
    public int MapTerrainStep = 2;
    readonly List<MapTile> _mapTiles = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<(AreaKey key, MeshData mesh, BankModel model)> _mapQueue = new();
    Task? _mapTask; volatile bool _mapCancel;

    /// <summary>Fertige Kartenkacheln (Spielkoordinaten: Origin = Area-Ecke).</summary>
    public IReadOnlyList<MapTile> MapTiles => _mapTiles;
    /// <summary>Alle Kartenkacheln gebaut.</summary>
    public bool MapTerrainReady => _mapTask != null && _mapTask.IsCompleted && _mapQueue.IsEmpty && _mapTiles.Count > 0;

    /// <summary>Kartengelände laden (einmal je Insel; ein Hintergrund-Thread, höchstens zwei Bänke gleichzeitig im Speicher).</summary>
    public void RequestMapTerrain()
    {
        if (_mapTask != null || _index == null || _index.AreaBanks.Count == 0) return;
        var banks = new List<KeyValuePair<AreaKey, string>>(_index.AreaBanks);
        int step = System.Math.Clamp(MapTerrainStep, 1, 8);
        var q = _mapQueue;
        _mapTask = Task.Run(() =>
        {
            foreach (var kv in banks)
            {
                if (_mapCancel) return;
                while (q.Count >= 2 && !_mapCancel) Thread.Sleep(20);
                try
                {
                    var model = BankModel.Load(kv.Value, BankKind.Area, false, false, false);
                    if (model.Heights == null || model.ColorMapKey == 0) continue;
                    q.Enqueue((kv.Key, BuildMapGrid(model.Heights, step), model));
                }
                catch (Exception) { }
            }
        });
    }

    /// <summary>At most one tile per image on the graphics card (texture, material, mesh).</summary>
    void PollMapTerrain()
    {
        if (!_mapQueue.TryDequeue(out var it)) return;
        try
        {
            var color = it.model.GetTexture(it.model.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, null, _renderer);
            foreach (var d in it.model.Textures.Defs.Values) d.Data = null;   // übrige Rohdaten der Bank nicht festhalten
            if (color == null) return;
            // unbeleuchteter Karten-Shader (Tageszeit/Licht/Spiegelung der Welt spielen auf der Karte keine Rolle)
            var mat = _renderer.CreateMaterial(new MaterialDesc { Kind = MaterialKind.MapTerrain, Name = "TDU2 Karte " + it.key, Main = color, Brightness = 1f, Relief = 0.35f });
            var (v, i) = TerrainMeshBuilder.ToVertices(it.mesh);
            var mesh = _renderer.CreateMesh(v, i);
            _mapTiles.Add(new MapTile { Key = it.key, Mesh = mesh, Material = mat, Color = color, Origin = WorldLayout.AreaOrigin(it.key) });
            if (_mapTask != null && _mapTask.IsCompleted && _mapQueue.IsEmpty) Log.Debug("Kartengelände: " + _mapTiles.Count + " Areas");
        }
        catch (Exception ex) { Log.Warn("Kartengelände " + it.key + ": " + ex.Message); }
    }

    /// <summary>The entire area raster as a grid, with every step-te cell (UV coordinates like the LOD1 pieces: 0..1 across the area colormap).</summary>
    static MeshData BuildMapGrid(HeightGrid g, int step)
    {
        var md = new MeshData { Name = "MapTerrain" };
        int n = System.Math.Max(1, (g.Size - 1) / step), v = n + 1;
        float ext = MathF.Max(1f, g.Extent);
        for (int z = 0; z < v; z++)
            for (int x = 0; x < v; x++)
            {
                int gx = System.Math.Min(x * step, g.Size - 1), gz = System.Math.Min(z * step, g.Size - 1);
                md.Positions.Add(new Vector3(gx * g.Spacing, g.Get(gx, gz), gz * g.Spacing));
                md.Normals.Add(g.NormalAt(gx, gz));
                md.UV0.Add(new Vector2(gx * g.Spacing / ext, gz * g.Spacing / ext));
            }
        var tris = new List<int>(n * n * 6);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int v00 = z * v + x, v10 = v00 + 1, v01 = v00 + v, v11 = v01 + 1;
                tris.Add(v00); tris.Add(v01); tris.Add(v10);
                tris.Add(v10); tris.Add(v01); tris.Add(v11);
            }
        md.SubMeshes.Add(tris);
        md.SubMeshMaterialIndex.Add(0);
        return md;
    }

    /// <summary>Unity TDU2BigMap.DrawMapTerrain: only the map tiles (map camera, no ocean over them—the color maps include it).</summary>
    public void RenderMapTerrain(Vector3 cameraRender, FrameGlobals globals)
    {
        globals.CameraPosition = cameraRender;
        globals.Origin = new Vector2(RootOffset.X, RootOffset.Z);
        _renderer.SetFrameGlobals(globals);
        var root = RootMatrix;
        foreach (var t in _mapTiles)
            if (t.Mesh != null && t.Material != null)
                _renderer.DrawMesh(t.Mesh, t.Material, Matrix4x4.CreateTranslation(t.Origin) * root);
    }

    void DestroyMapTerrain()
    {
        _mapCancel = true;
        try { _mapTask?.Wait(3000); } catch (Exception) { }
        while (_mapQueue.TryDequeue(out _)) { }
        foreach (var t in _mapTiles) { t.Mesh?.Dispose(); t.Material?.Dispose(); t.Color?.Dispose(); }
        _mapTiles.Clear();
    }

    public bool ReadyAround(Vector3 renderPos, float radius)
    {
        if (_index == null) return false;
        if (_libraryTask != null && !_libraryTask.IsCompleted) return false;
        var g = RenderToGame(renderPos);
        var p = new Vector2(g.X, g.Z);
        float ra = System.Math.Min(radius, AreaLoadDistance), rs = System.Math.Min(radius, SectorLoadDistance);
        foreach (var k in _index.AreaBanks.Keys)
            if (WorldLayout.DistanceToRect(p, WorldLayout.AreaRect(k)) < ra && !(_areas.TryGetValue(k, out var a) && (a.Built || a.Failed))) return false;
        foreach (var k in _index.SectorBanks.Keys)
            if (WorldLayout.DistanceToRect(p, WorldLayout.SectorRect(k)) < rs && !(_sectors.TryGetValue(k, out var st) && (st.Built || st.Failed))) return false;
        return true;
    }

    int RunningJobs()
    {
        int n = 0;
        foreach (var a in _areas.Values) if (a.Task != null && !a.Built && !a.Failed) n++;
        foreach (var s in _sectors.Values) if (s.Task != null && !s.Built && !s.Failed) n++;
        return n;
    }

    void ScanAreas()
    {
        var p = new Vector2(_viewerGame.X, _viewerGame.Z);
        var wanted = new List<(float d, AreaKey k)>();
        var drop = new List<AreaKey>();
        foreach (var k in _index!.AreaBanks.Keys)
        {
            float d = WorldLayout.DistanceToRect(p, WorldLayout.AreaRect(k));
            if (d < AreaLoadDistance && !_areas.ContainsKey(k)) wanted.Add((d, k));
            else if (d > AreaLoadDistance + AreaUnloadMargin && _areas.ContainsKey(k)) drop.Add(k);
        }
        foreach (var k in drop) UnloadArea(k);
        wanted.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var (d, k) in wanted)
        {
            if (RunningJobs() >= MaxParallelLoads) break;
            StartArea(k);
        }
    }

    void ScanSectors()
    {
        var p = new Vector2(_viewerGame.X, _viewerGame.Z);
        var wanted = new List<(float d, SectorKey k)>();
        var drop = new List<SectorKey>();
        foreach (var k in _index!.SectorBanks.Keys)
        {
            float d = WorldLayout.DistanceToRect(p, WorldLayout.SectorRect(k));
            bool loaded = _sectors.ContainsKey(k);
            if (d < SectorLoadDistance && !loaded && AreasReadyFor(k)) wanted.Add((d, k));
            else if (loaded && d > SectorLoadDistance + SectorUnloadMargin) drop.Add(k);
        }
        foreach (var k in drop) UnloadSector(k);
        wanted.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var (d, k) in wanted)
        {
            if (RunningJobs() >= MaxParallelLoads) break;
            StartSector(k);
        }
    }

    bool AreasReadyFor(SectorKey k)
    {
        var r = WorldLayout.SectorRect(k);
        var a0 = WorldLayout.AreaAt(r.XMin + 1, r.YMin + 1);
        var a1 = WorldLayout.AreaAt(r.XMax - 1, r.YMax - 1);
        for (int ax = a0.X; ax <= a1.X; ax++)
            for (int az = a0.Z; az <= a1.Z; az++)
            {
                var ak = new AreaKey(ax, az);
                if (!_index!.AreaBanks.ContainsKey(ak)) continue;
                if (!_areas.TryGetValue(ak, out var st) || !st.Built) return false;
            }
        return true;
    }

    void StartArea(AreaKey k)
    {
        string path = _index!.AreaBanks[k];
        bool objs = LoadAreaObjects;
        float skirt = AreaSkirt;
        var st = new AreaState { Key = k };
        var exteriors = LoadSpotExteriors ? SpotsInArea(k) : new List<(Spot, string)>();
        var tex = _textures;
        st.Task = Task.Run(() =>
        {
            var swl = System.Diagnostics.Stopwatch.StartNew();
            var model = BankModel.Load(path, BankKind.Area, objs, false, false);
            tex.IndexBank(model.Bank);
            var pieces = model.Heights != null ? TerrainMeshBuilder.BuildAreaPieces(model.Heights, k, skirt) : new List<TerrainPiece>();
            var payload = new AreaPayload { Model = model, Pieces = pieces };
            foreach (var (spot, file) in exteriors)
            {
                try { var em = BankModel.Load(file, BankKind.Exterior); tex.IndexBank(em.Bank); TextureAnimations.PrepareUva(em.Bank); payload.Exteriors.Add((spot, em)); }
                catch (Exception) { }
            }
            payload.LoadMs = swl.Elapsed.TotalMilliseconds;
            return payload;
        });
        _areas[k] = st;
    }

    void StartSector(SectorKey k)
    {
        string path = _index!.SectorBanks[k];
        bool objs = LoadSectorObjects, place = LoadVegetation || LoadOtherPlacements || LoadActors || LoadWorldActors, grass = LoadGrass;
        float skirt = SectorSkirt;
        bool cutTunnels = CutTunnelPortals && objs;
        bool wires = ShowPowerWires;
        bool rotors = LoadRotors;
        var st = new SectorState { Key = k };
        var exteriors = LoadSpotExteriors ? SpotsInSector(k) : new List<(Spot, string)>();
        var tex = _textures;
        var origin = WorldLayout.SectorOrigin(k);
        st.Task = Task.Run(() =>
        {
            var swl = System.Diagnostics.Stopwatch.StartNew();
            // Gras folgt mit GrassRenderer
            var model = BankModel.Load(path, BankKind.Sector, objs, place, grass);
            if (objs) { try { TextureAnimations.PrepareUva(model.Bank); } catch (Exception) { } }   // Textur-Animationen (.uva)
            // Sektortexturen global bekannt machen (Library-Objekte wie Felsen nutzen Texturen aus der Sektorbank)
            tex.IndexBank(model.Bank);
            bool[]? holes = null;
            // Tunnelportale wie das Original: Loch-Kacheln aus dem HMAP (LOD-Fehler −1) werden nicht gezeichnet, an ihrer
            // Stelle liegt das Sektormodell BLOCK (Gelände mit ausgespartem Portal), gleiches Material und gleiche Colormap.
            if (cutTunnels && model.Heights != null && model.TerrainScene?.Heightmap.HoleTiles is { } holeTiles)
                holes = TerrainMeshBuilder.HoleCells(model.Heights, model.TerrainScene.Heightmap.Tiles, holeTiles);
            var terrain = model.Heights != null ? TerrainMeshBuilder.BuildSector(model.Heights, holes) : null;
            if (terrain != null && holes != null && model.Objects != null)
            {
                int bt = TerrainMeshBuilder.AppendTerrainBlock(terrain, model.Objects, model.Heights!);
                if (bt == 0) Log.Warn($"Sektor {k}: Loch-Kacheln ohne BLOCK-Gelände");
            }
            var skirts = model.Heights != null ? TerrainMeshBuilder.BuildSectorSkirts(model.Heights, skirt, holes) : null;
            var payload = new SectorPayload
            {
                Model = model,
                Terrain = terrain,
                Skirts = skirts,
                DriveHoles = holes,
            };
            // Rotoren liegen in einer verschachtelten _sc-Bank des Sektors (C++: RotorBank.loadFromMemory).
            if (rotors)
            {
                try { payload.Rotors = WindTurbines.Load(model, path, tex); }
                catch (Exception ex) { Log.Warn($"Windräder {k}: {ex.Message}"); }
            }
            if (wires && model.Bank != null)
            {
                try
                {
                    foreach (var pe in model.Bank.All(".pen")) { payload.Pen = PowerWires.PenFile.Parse(model.Bank.Read(pe)); break; }
                }
                catch (Exception ex) { Log.Warn($"Stromleitungen {k}: {ex.Message}"); }
            }
            foreach (var (spot, file) in exteriors)
            {
                try { var em = BankModel.Load(file, BankKind.Exterior); tex.IndexBank(em.Bank); TextureAnimations.PrepareUva(em.Bank); payload.Exteriors.Add((spot, em)); }
                catch (Exception) { }
            }
            payload.LoadMs = swl.Elapsed.TotalMilliseconds;
            return payload;
        });
        _sectors[k] = st;
    }

    /// <summary>Render at most one finished tile per frame (prevents stuttering).</summary>
    void BuildCompleted()
    {
        foreach (var st in _areas.Values)
        {
            if (st.Built || st.Failed || st.Task == null || !st.Task.IsCompleted) continue;
            if (st.Task.IsFaulted) { st.Failed = true; Log.Warn($"{st.Key}: {st.Task.Exception?.GetBaseException().Message}"); continue; }
            var swa = System.Diagnostics.Stopwatch.StartNew();
            BuildArea(st, st.Task.Result);
            AreaStats.Add(st.Task.Result.LoadMs, swa.Elapsed.TotalMilliseconds);
            return;
        }
        float best = float.MaxValue; SectorState? next = null;
        foreach (var st in _sectors.Values)
        {
            if (st.Built || st.Failed || st.Task == null || !st.Task.IsCompleted) continue;
            if (st.Task.IsFaulted) { st.Failed = true; Log.Warn($"{st.Key}: {st.Task.Exception?.GetBaseException().Message}"); continue; }
            float d = WorldLayout.DistanceToRect(new Vector2(_viewerGame.X, _viewerGame.Z), WorldLayout.SectorRect(st.Key));
            if (d < best) { best = d; next = st; }
        }
        if (next != null)
        {
            var sws = System.Diagnostics.Stopwatch.StartNew();
            BuildSector(next, next.Task!.Result);
            SectorStats.Add(next.Task!.Result.LoadMs, sws.Elapsed.TotalMilliseconds);
        }
    }

    void BuildArea(AreaState st, AreaPayload data)
    {
        st.Model = data.Model;
        var origin = WorldLayout.AreaOrigin(st.Key);

        var color = st.Model.GetTexture(st.Model.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, _textures, _renderer);
        var mat = MaterialFactory.CreateTerrainMaterial(color, null, null, null, DetailTiling, 0f);
        st.Res.Owned.Add(mat);
        foreach (var p in data.Pieces)
        {
            var node = CreateMeshNode(p.Sector.ToString(), p.WorldOrigin, p.Mesh, mat, st.Res);
            st.Nodes.Add(node);
            Register(st, p.Sector, node);
        }

        if (LoadAreaObjects && st.Model.Objects != null)
        {
            ObjectBuilder.BuildObjects(st.Model, origin, _textures, st.Res, _opt.WithOcean(OceanFor(st.Model, origin, 1, st.Res)), _renderer, obj =>
            {
                var node = new Node { Name = obj.Name };
                node.Objects.Add(obj);
                node.Encapsulate(obj.BoundsMin, obj.BoundsMax);
                st.Nodes.Add(node);
                RegisterSpan(st, WorldLayout.SectorAt(obj.Center.X, obj.Center.Z), node);
            });
        }
        foreach (var (spot, em) in data.Exteriors)
        {
            st.ExtraModels.Add(em);
            var node = BuildExterior(spot, em, st.Res, "_low");
            if (node != null) { TextureAnimations.Apply(em.Bank?.FilePath, node.Objects); st.Nodes.Add(node); Register(st, SpotSector(spot), node); }
        }
        st.Built = true;
        if (_islandPieces.TryGetValue(st.Key, out var ip)) ip.Active = false;
    }

    void Register(AreaState st, SectorKey key, Node node)
    {
        st.Registered.Add((key, node));
        if (!_lod1BySector.TryGetValue(key, out var list)) _lod1BySector[key] = list = new List<Node>();
        list.Add(node);
        node.Active = Lod1Wanted(node, key);
    }

    /// <summary>
    /// LOD objects in the area often span multiple sectors -> registered for all sectors they overlap:
    /// hidden as soon as one of them is built.
    /// </summary>
    void RegisterSpan(AreaState st, SectorKey center, Node node)
    {
        var keys = new List<SectorKey> { center };
        if (node.HasBounds)
        {
            // Bounds in Spielkoordinaten; 20 % Überstand ignorieren
            float x0 = node.BoundsMin.X, x1 = node.BoundsMax.X, z0 = node.BoundsMin.Z, z1 = node.BoundsMax.Z;
            float mx = (x1 - x0) * 0.2f, mz = (z1 - z0) * 0.2f;
            x0 += mx; x1 -= mx; z0 += mz; z1 -= mz;
            float step = WorldLayout.SectorSize * 0.5f;
            if ((x1 - x0) < WorldLayout.SectorSize * 3f && (z1 - z0) < WorldLayout.SectorSize * 3f)
                for (float x = x0; x <= x1 + step * 0.5f; x += step)
                    for (float z = z0; z <= z1 + step * 0.5f; z += step)
                    {
                        var k = WorldLayout.SectorAt(System.Math.Min(x, x1), System.Math.Min(z, z1));
                        if (!keys.Contains(k)) keys.Add(k);
                    }
        }
        _lod1Keys[node] = keys;
        foreach (var k in keys) Register(st, k, node);
    }

    bool SectorBuilt(SectorKey k) => _sectors.TryGetValue(k, out var s) && s.Built;

    /// <summary>LOD1 object is visible as long as none of its sectors are built as LOD0.</summary>
    bool Lod1Wanted(Node node, SectorKey key)
    {
        bool wanted;
        if (_lod1Keys.TryGetValue(node, out var keys))
        {
            wanted = true;
            foreach (var k in keys) if (SectorBuilt(k)) { wanted = false; break; }
        }
        else wanted = !SectorBuilt(key);
        if (wanted && _lod1Blocked && node.HasBounds && Intersects(node.BoundsMin, node.BoundsMax, _lod1BlockMin, _lod1BlockMax)) return false;
        return wanted;
    }

    bool _lod1Blocked;
    Vector3 _lod1BlockMin, _lod1BlockMax;   // Spielraum

    static bool Intersects(Vector3 amin, Vector3 amax, Vector3 bmin, Vector3 bmax)
        => amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;

    /// <summary>Hide LOD1 objects within the box (play area); null = unhide.</summary>
    public void BlockLod1(Vector3? min, Vector3? max)
    {
        if (min.HasValue && max.HasValue) { _lod1BlockMin = min.Value; _lod1BlockMax = max.Value; _lod1Blocked = true; }
        else _lod1Blocked = false;
        foreach (var kv in _lod1BySector)
            foreach (var node in kv.Value) node.Active = Lod1Wanted(node, kv.Key);
        ApplyIslandLod();
    }

    /// <summary>Unity ApplyIslandLod: Applies coarse island terrain to each area as long as the area is not built—unless it intersects with an open interior.</summary>
    void ApplyIslandLod()
    {
        foreach (var kv in _islandPieces)
        {
            bool want = ShowIslandLod && !(_areas.TryGetValue(kv.Key, out var st) && st.Built);
            if (want && _lod1Blocked && kv.Value.HasBounds && Intersects(kv.Value.BoundsMin, kv.Value.BoundsMax, _lod1BlockMin, _lod1BlockMax)) want = false;
            kv.Value.Active = want;
        }
    }

    /// <summary>Built World nodes with this name (sectors and areas, e.g., “Spot_EPH_L0C” / “Spot_EPH_L0C_low”).</summary>
    public IEnumerable<Node> NodesNamed(string name)
    {
        foreach (var s in _sectors.Values) if (s.Built) foreach (var n in s.Nodes) if (n.Name == name) yield return n;
        foreach (var a in _areas.Values) if (a.Built) foreach (var n in a.Nodes) if (n.Name == name) yield return n;
    }

    public void EnforceLod1()
    {
        foreach (var kv in _lod1BySector)
            foreach (var node in kv.Value)
                if (node.Active && !Lod1Wanted(node, kv.Key)) node.Active = false;
    }

    void SetLod1Visible(SectorKey key)
    {
        if (!_lod1BySector.TryGetValue(key, out var list)) return;
        foreach (var node in list) node.Active = Lod1Wanted(node, key);
    }

    /// <summary>
    /// Only display sector skirts where there is no LOD0 sector beyond the edge (where the coarser LOD1 sector begins).
    /// Between two LOD0 sectors, the edges are at the same elevation (verified)—otherwise, a skirt would hang, for example, in tunnels
    /// below sector boundaries (Ibiza Street 869/907).
    /// </summary>
    void UpdateSkirts()
    {
        foreach (var st in _sectors.Values)
        {
            if (!st.Built && st.Skirts.Count == 0) continue;
            foreach (var (node, probe) in st.Skirts)
            {
                var nk = WorldLayout.SectorAt(probe.X, probe.Z);
                bool lod0 = !nk.Equals(st.Key) && _sectors.TryGetValue(nk, out var ns) && ns.Built;
                node.Active = !lod0;
            }
        }
    }

    void BuildSector(SectorState st, SectorPayload data)
    {
        st.Model = data.Model;
        st.DriveHoles = data.DriveHoles;        // Rückfall-Bodenhöhe (TryGetGroundHeight) meidet die Löcher
        if (Physics != null) st.Data = data;   // Geländenetz für die Kollision (UpdateColliders)
        var origin = WorldLayout.SectorOrigin(st.Key);
        long tbs = System.Diagnostics.Stopwatch.GetTimestamp();

        IGpuTexture? groundColor = null, groundSplat = null;
        if (data.Terrain != null)
        {
            var m = st.Model;
            var color = m.GetTexture(m.ColorMapKey, TextureWrap.Clamp, TextureWrap.Clamp, _textures, _renderer);
            var splat = m.GetTexture(m.SplatMapKey, TextureWrap.Clamp, TextureWrap.Clamp, _textures, _renderer);
            groundColor = color; groundSplat = splat;
            IGpuTexture? d1 = null, d2 = null;
            if (m.TerrainMaterial != null)
            {
                if (m.TerrainMaterial.TryGetLayer(MaterialDef.SemDetail1, out var l1)) d1 = m.GetTexture(l1.TextureKey, TextureWrap.Repeat, TextureWrap.Repeat, _textures, _renderer);
                if (m.TerrainMaterial.TryGetLayer(MaterialDef.SemDetail2, out var l2)) d2 = m.GetTexture(l2.TextureKey, TextureWrap.Repeat, TextureWrap.Repeat, _textures, _renderer);
            }
            var mat = MaterialFactory.CreateTerrainMaterial(color, splat, d1, d2, DetailTiling, DetailStrength);
            st.Res.Owned.Add(mat);
            st.Nodes.Add(CreateMeshNode("Terrain", origin, data.Terrain, mat, st.Res));
            if (data.Skirts != null)
                foreach (var (sm, probe) in data.Skirts)
                {
                    if (sm.SubMeshes[0].Count == 0) continue;
                    var sn = CreateMeshNode("TerrainSkirt", origin, sm, mat, st.Res);
                    st.Nodes.Add(sn);
                    st.Skirts.Add((sn, origin + new Vector3(probe.X, 0f, probe.Y)));
                }
        }
        tbs = Lap("Sektor bauen: Gelände", tbs);

        if (LoadSectorObjects && st.Model.Objects != null)
        {
            var node = new Node { Name = "Objects" };
            node.Objects.AddRange(ObjectBuilder.BuildObjects(st.Model, origin, _textures, st.Res, (Physics != null ? _collOpt : _opt).WithOcean(OceanFor(st.Model, origin, 0, st.Res)), _renderer));
            TextureAnimations.Apply(st.Model.Bank?.FilePath, node.Objects);   // Textur-Animationen wie im Spiel
            st.Nodes.Add(node);
        }
        tbs = Lap("Sektor bauen: Objekte", tbs);

        // Windrad-Rotoren: eigene, pro Frame bewegte RenderObjects im selben Spielkoordinatensystem.
        // Der Node wird wie jeder andere Sektor-Node gezeichnet; CollectSector/Render hängen RootMatrix dran.
        if (LoadRotors && data.Rotors != null)
        {
            st.Rotors = WindTurbines.Build(data.Rotors, origin, _textures, st.Res, _opt, _renderer);
            if (st.Rotors != null)
            {
                st.Nodes.Add(st.Rotors.Node);
                Log.Info($"Sektor {st.Key}: {st.Rotors.Count} Windrad-Rotoren");
            }
            else
            {
                foreach (var b in data.Rotors.Banks) b.Model.Release();
            }
        }
        tbs = Lap("Sektor bauen: Windräder", tbs);

        if (LoadGrass && st.Model.Grass != null && _grassTypes != null && _grassAtlas != null && st.Model.Heights != null)
        {
            // wie TD_TallGrass: Gras mit der Bodenfarbe (Colormap) des Sektors einfärben, gebackener Schatten aus der Splatmap
            var gm = MaterialFactory.CreateGrassMaterial(_grassAtlas, 0.4f, groundColor, groundSplat, MaterialFactory.ShadowMapStrength);
            var bm = MaterialFactory.CreateGrassMaterial(_bushAtlas, 0.4f, groundColor, groundSplat, MaterialFactory.ShadowMapStrength);
            if (gm != null) st.Res.Owned.Add(gm);
            if (bm != null) st.Res.Owned.Add(bm);
            st.Grass = new GrassRenderer
            {
                Origin = origin,
                Heights = st.Model.Heights,
                Map = st.Model.Grass,
                Types = _grassTypes,
                GrassMaterial = gm,
                BushMaterial = bm,
                GrassAtlas = _grassAtlas,
                BushAtlas = _bushAtlas,
                GrassAtlasCells = _grassCells,
                BushAtlasCells = _bushCells,
                Radius = GrassRadius,
                Density = GrassDensity,
                Spacing = GrassSpacing,
                Seed = HashCode.Combine(st.Key.GetHashCode(), 0x6A55),
                Noise = _grassNoise,
            };
        }

        tbs = Lap("Sektor bauen: Gras", tbs);
        foreach (var (spot, em) in data.Exteriors)
        {
            st.ExtraModels.Add(em);
            var node = BuildExterior(spot, em, st.Res, "");
            if (node != null) { TextureAnimations.Apply(em.Bank?.FilePath, node.Objects); st.Nodes.Add(node); }   // dieselbe Bank steht oft mehrfach
        }
        tbs = Lap("Sektor bauen: Spots/Außenmodelle", tbs);
        if (data.Pen != null && data.Pen.Links.Count > 0)
            st.Wires = new PowerWires.SectorWires { Pen = data.Pen, Origin = WorldLayout.SectorOrigin(st.Key) };
        st.Built = true;
        SetLod1Visible(st.Key);
        UpdateSkirts();
        TryAddVegetation(st);
        tbs = Lap("Sektor bauen: Vegetation/LOD", tbs);
        // Leitungen der Nachbarn, deren Masten hier stehen, neu auflösen
        foreach (var o in _sectors.Values)
            if (o != st && o.Wires is { Resolved: true, HadMissing: true }) { o.Wires.Resolved = false; o.WireWait = 0f; }
        TryAddActors(st);
        if (Physics != null) _colliderQueue.Enqueue(st);
        Lap("Sektor bauen: Akteure", tbs);
    }


    void PollActors()
    {
        if (_actors != null || _actorsTask == null || !_actorsTask.IsCompleted) return;
        if (_actorsTask.IsFaulted)
        {
            Log.Warn("worldactors.bnk: " + _actorsTask.Exception?.GetBaseException().Message);
            _actorsTask = null;
            return;
        }
        _actors = _actorsTask.Result;
        Log.Debug($"World Actors: {_actors.ByHc.Count} Modelle" + (_actors.Log.Count > 0 ? " – " + string.Join("; ", _actors.Log.Take(8)) : ""));
        foreach (var st in _sectors.Values) if (st.Built) TryAddActors(st);
    }

    /// <summary>World Actors in a sector (A-rankings) as objects.</summary>
    void TryAddActors(SectorState st)
    {
        if (st.ActorsDone || _actors == null || st.Model == null || !LoadWorldActors) return;
        st.ActorsDone = true;
        if (st.Model.Actors == null) return;
        try
        {
            var origin = WorldLayout.SectorOrigin(st.Key);
            var node = new Node { Name = "WorldActors" };
            Action<PmiInstance, Matrix4x4>? onInst = null;
            var signV = new List<ObjectVertex>(); var signI = new List<uint>();
            var lay = _signLayout;
            if (ShowSignText && lay != null && SignMaterial() != null)
            {
                onInst = (inst, world) =>
                {
                    // Schild kann umfallen: Text als eigenes Netz, das der Körper mitbewegt (Unity: BuildSignTextOn als Kind des Schilds)
                    if (_actorBodies != null && _actorBodies.LastLoose && inst.SignCodes != null)
                    {
                        var v1 = new List<ObjectVertex>(); var i1 = new List<uint>();
                        lay.AddSign(inst.SignCodes, world, v1, i1);
                        int idx = node.Meshes.Count;
                        AddSignMesh(st, node, v1, i1, Matrix4x4.Identity);
                        if (node.Meshes.Count > idx) _actorBodies.AttachToLast(m => { var x = node.Meshes[idx]; node.Meshes[idx] = (x.Mesh, x.Material, m); });
                        return;
                    }
                    lay.AddSign(inst.SignCodes, world, signV, signI);
                };
                st.ActorSignsDone = true;
            }
            // Unity WorldActorBody.Physicalize je Instanz: Box-Kollider (Radstrahlen treffen ihn nicht), leichte Teile fallen um
            Action<string, List<RenderObject>, Matrix4x4>? onActor = null;
            if (Physics != null)
            {
                if (_actorBodies == null) { _actorBodies = new WorldActorBodies(Physics); WorldActorBodies.Current = _actorBodies; }
                string sk = st.Key.ToString();
                onActor = (name, objs, world) => { var c = _actorBodies.Register(sk, name, objs, world); if (c != null) st.Colliders.Add(c); };
            }
            int n = _actors.Build(st.Model.Actors, Matrix4x4.CreateTranslation(origin), _textures, st.Res, Physics != null ? _collOpt : _opt, _renderer, node.Objects, onInst, () => !node.Disposed, onActor);
            if (n == 0) return;
            TextureAnimations.Apply(_actors.Model?.Bank?.FilePath, node.Objects);
            // Rand für umgefahrene Teile, die sich aus ihrer Lage bewegen
            foreach (var o in node.Objects) node.Encapsulate(o.BoundsMin - new Vector3(4f), o.BoundsMax + new Vector3(4f));
            AddSignMesh(st, node, signV, signI, Matrix4x4.Identity);
            st.Nodes.Add(node);
        }
        catch (Exception ex) { Log.Warn("World Actors " + st.Key + ": " + ex.Message); }
    }

    void PollLibrary()
    {
        if (_library != null || _libraryTask == null || !_libraryTask.IsCompleted) return;
        if (_libraryTask.IsFaulted)
        {
            Log.Warn("commonworld.bnk: " + _libraryTask.Exception?.GetBaseException().Message);
            _libraryTask = null;
            return;
        }
        _library = _libraryTask.Result;
        Log.Debug($"Library geladen: {_library.Scene?.InsHash.Count ?? 0} Objekte, {_textures.Count} Texturen");
        foreach (var st in _sectors.Values) if (st.Built) TryAddVegetation(st);
        // Meer: Normalmap aus commonworld (water_n.2db)
        if (!_waterNormalSet)
        {
            var wn = _textures.Get(_textures.KeyByName("water_n.2db"), TextureWrap.Repeat, TextureWrap.Repeat);
            if (wn != null) { MaterialFactory.SetWaterNormalMap(wn); _waterNormalSet = true; }
        }
        // World Actors schon vor der Library gebaut: Schildertext jetzt nachholen (Layout/Atlas kommen aus commonworld)
        if (ShowSignText)
            foreach (var st in _sectors.Values)
                if (st.Built && st.ActorsDone && st.Model?.Actors != null) RebuildActorSignTexts(st);
    }

    void TryAddVegetation(SectorState st)
    {
        if (st.VegetationDone || _library == null || st.Model == null) return;
        st.VegetationDone = true;
        var origin = WorldLayout.SectorOrigin(st.Key);
        var veg = new PlacementRenderer { MaxDistance = VegetationDistance, CullLastLod = CullLastLod };
        // Gruppen (je Bibliotheksobjekt) nicht alle im selben Bild einbauen, sondern über ProcessVegetationWork mit Zeitbudget
        // (ein Sektor mit Bäumen, Laternen, Leitplanken und ihren Kollidern kostete sonst bis 80 ms am Stück).
        void Queue(PmiFile? pmi, BuildResources? res)
        {
            if (pmi == null) return;
            foreach (var g in pmi.Groups) _vegWork.Enqueue(new VegWork(st, veg, g, origin, res));
        }
        if (LoadVegetation) Queue(st.Model.Vegetation, null);
        if (LoadOtherPlacements) Queue(st.Model.Placements, null);
        if (LoadOtherPlacements && ShowSignText) BuildSignTexts(st, st.Model.Placements, "SignText");
        if (LoadOtherPlacements) Queue(st.Model.Rails, st.Res);   // Leitplanken (-S.pmi)
        if (LoadActors) Queue(st.Model.Actors, null);
        st.Placements = veg;
        st.Instances = veg.InstanceCount;
    }

    /// <summary>Time budget per image for inserting placement groups or grass patches (ms).</summary>
    public double VegetationBudgetMs = 3.0, GrassBudgetMs = 2.0;
    readonly record struct VegWork(SectorState St, PlacementRenderer Veg, PmiGroup G, Vector3 Origin, BuildResources? Res);
    readonly Queue<VegWork> _vegWork = new();

    void ProcessVegetationWork()
    {
        if (_vegWork.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int done = 0;
        while (_vegWork.Count > 0)
        {
            if (done > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > VegetationBudgetMs) break;
            var w = _vegWork.Dequeue();
            // Sektor inzwischen entladen (oder neu geladen): Auftrag verwerfen
            if (!_sectors.TryGetValue(w.St.Key, out var cur) || !ReferenceEquals(cur, w.St) || !ReferenceEquals(w.St.Placements, w.Veg)) continue;
            AddPlacementGroup(w.Veg, w.G, w.Origin, w.Res, w.St);
            w.St.Instances = w.Veg.InstanceCount;
            done++;
        }
    }

    void AddPlacements(PlacementRenderer veg, PmiFile? pmi, Vector3 origin, BuildResources? res = null, SectorState? coll = null)
    {
        if (pmi == null || _library == null) return;
        foreach (var g in pmi.Groups) AddPlacementGroup(veg, g, origin, res, coll);
    }

    void AddPlacementGroup(PlacementRenderer veg, PmiGroup g, Vector3 origin, BuildResources? res, SectorState? coll)
    {
        if (_library == null) return;
        {
            var obj = _library.Get(g.Key, _textures, Physics != null ? _collOpt : _opt, _renderer);
            if (obj == null) return;
            bool skin = res != null && SkinnedRail(obj);
            var list = new List<Matrix4x4>(g.Instances.Count);
            foreach (var inst in g.Instances)
            {
                // Unity: MathUtil.TRS(origin + pos, rot, scale)
                var m = inst.Matrix * Matrix4x4.CreateTranslation(origin);
                if (skin && inst.Bones != null && inst.Bones.Length > 0)
                {
                    // Leitplanke: Mesh mit den Knochenmatrizen dieser Instanz biegen (Pre steckt in Knochen 0)
                    var baked = BakeRail(obj, inst.Bones, res!);
                    if (baked != null)
                    {
                        veg.Add(baked, new List<Matrix4x4> { m });
                        // Kollision der gebogenen Planke (eigenes Netz je Instanz)
                        if (coll != null && Physics != null && baked.Lods.Length > 0)
                        {
                            _collTris.Clear(); _collSurf.Clear();
                            foreach (var part in baked.Lods[0].Parts)
                            {
                                var t = part.Collision;
                                if (t == null) continue;
                                bool flip = m.GetDeterminant() < 0f;
                                for (int i = 0; i + 2 < t.Length; i += 3)
                                {
                                    _collTris.Add(Vector3.Transform(t[i], m));
                                    _collTris.Add(Vector3.Transform(t[flip ? i + 2 : i + 1], m));
                                    _collTris.Add(Vector3.Transform(t[flip ? i + 1 : i + 2], m));
                                    _collSurf.Add(0);
                                }
                            }
                            var c = Physics.AddStaticMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_collTris), obj.Name + " Leitplanke",
                                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_collSurf));
                            if (c != null) coll.Colliders.Add(c);
                            _collTris.Clear(); _collSurf.Clear();
                        }
                        continue;
                    }
                }
                var wm = inst.Pre.HasValue ? inst.Pre.Value * m : m;
                list.Add(wm);
                if (coll != null && Physics != null)
                {
                    var (tris, surf) = LibraryCollision(obj);
                    if (tris.Length >= 3)
                    {
                        var c = Physics.AddStaticInstance(obj, tris, wm, obj.Name, surf);
                        if (c != null) coll.Colliders.Add(c);
                    }
                }
            }
            if (list.Count > 0) veg.Add(obj, list);
        }
    }

    /// <summary>Does the library object have skinning data (e.g., SF_GLISS)?</summary>
    static bool SkinnedRail(LibraryObject obj)
    {
        foreach (var l in obj.Lods)
        {
            var md = l?.Source;
            if (md == null) continue;
            if (md.HasBones && md.BoneIdx.Count == md.Positions.Count && md.BoneIdx.Count > 0) return true;
        }
        return false;
    }

    static long _bakeCounter;

    /// <summary>
    /// Guardrails as in the original (TD_Ground, 4-bone skinning): v' = sum of w_i * (v * bone_i). The matrices of each
    /// -S.pmi-instance adjust the start and end of each plank to match its neighbors. The result is a separate object for each instance.
    /// </summary>
    LibraryObject? BakeRail(LibraryObject src, Matrix4x4[] bones, BuildResources res)
    {
        var lods = new LibraryLod[src.Lods.Length];
        Vector3 bmin = Vector3.Zero, bmax = Vector3.Zero;
        for (int l = 0; l < lods.Length; l++)
        {
            var sl = src.Lods[l];
            var md = sl?.Source;
            if (sl == null || md == null) return null;
            int n = md.Positions.Count;
            bool has = md.BoneIdx.Count == n && md.BoneW.Count == n;
            bool hasN = md.Normals.Count == n;
            var baked = new MeshData { Name = md.Name + "_skin", HasUV1 = md.HasUV1, HasUV2 = md.HasUV2, HasUV3 = md.HasUV3, HasColors = md.HasColors };
            for (int i = 0; i < n; i++)
            {
                var p = md.Positions[i];
                var q = hasN ? md.Normals[i] : Vector3.UnitY;
                Vector3 ap = Vector3.Zero, an = Vector3.Zero;
                if (has)
                {
                    var bi = md.BoneIdx[i]; var bw = md.BoneW[i];
                    float ws = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = k == 0 ? bw.X : k == 1 ? bw.Y : k == 2 ? bw.Z : bw.W;
                        if (w <= 0f) continue;
                        int b = (int)(k == 0 ? bi.X : k == 1 ? bi.Y : k == 2 ? bi.Z : bi.W);
                        var M = b >= 0 && b < bones.Length ? bones[b] : bones[0];
                        ap += w * Vector3.Transform(p, M); an += w * Vector3.TransformNormal(q, M); ws += w;
                    }
                    if (ws < 1e-4f) { ap = Vector3.Transform(p, bones[0]); an = Vector3.TransformNormal(q, bones[0]); }
                    else if (MathF.Abs(ws - 1f) > 1e-3f) { ap /= ws; }
                }
                else { ap = Vector3.Transform(p, bones[0]); an = Vector3.TransformNormal(q, bones[0]); }
                baked.Positions.Add(ap);
                baked.Normals.Add(an.LengthSquared() > 1e-8f ? Vector3.Normalize(an) : Vector3.UnitY);
            }
            baked.Colors.AddRange(md.Colors); baked.UV0.AddRange(md.UV0); baked.UV1.AddRange(md.UV1);
            baked.UV2.AddRange(md.UV2); baked.UV3.AddRange(md.UV3);
            foreach (var sm in md.SubMeshes) baked.SubMeshes.Add(sm);
            baked.SubMeshMaterialIndex.AddRange(md.SubMeshMaterialIndex);
            baked.SubMeshIsSprite.AddRange(md.SubMeshIsSprite);
            baked.SubMeshUvSets.AddRange(md.SubMeshUvSets);
            ulong key = sl.GeoKey ^ (0x9E3779B97F4A7C15UL * (ulong)Interlocked.Increment(ref _bakeCounter));
            IGpuTexture? GetTex(ulong k, TextureWrap u, TextureWrap v) => _textures.Get(k, u, v);
            var rr = ObjectBuilder.GetRenderable(baked, key, sl.BankKeys, _library!.Materials, GetTex, res, l == 0 && Physics != null ? _collOpt : _opt, sl.BankOffset, _renderer);
            if (rr == null) return null;
            if (l == 0) { bmin = rr.Min; bmax = rr.Max; }
            lods[l] = new LibraryLod { Parts = rr.Parts, MaxDistance = sl.MaxDistance };
        }
        return new LibraryObject { Key = src.Key, Name = src.Name, Lods = lods, Radius = src.Radius + 1f, BoundsMin = bmin, BoundsMax = bmax };
    }

    /// <summary>Sign text material (glyph atlas “rsalphab” from CommonWorld); set to null until loaded.</summary>
    IGpuMaterial? SignMaterial()
    {
        if (_signMat != null) return _signMat;
        var atlas = _textures.Get(_textures.KeyByName("rsalphab.2db"), TextureWrap.Clamp, TextureWrap.Clamp);
        if (atlas == null) return null;
        // TDU2/ObjectTransparent: _VcMul 1, _Lit 1, _AlphaSource 0, Cull Off, Blend SrcAlpha/OneMinusSrcAlpha
        _signMat = _renderer.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Transparent, Main = atlas, VcMul = true, Lit = 1f, AlphaFromDetail = false, Additive = false });
        return _signMat;
    }

    void AddSignMesh(SectorState st, Node node, List<ObjectVertex> v, List<uint> i, Matrix4x4 model)
    {
        if (i.Count == 0) return;
        var mat = SignMaterial();
        if (mat == null) return;
        var mesh = _renderer.CreateMesh(v.ToArray(), i.ToArray());
        st.Res.Owned.Add(mesh);
        node.Meshes.Add((mesh, mat, model));
    }

    /// <summary>Label text for the Art-4 groups in a .pmi file (intersection tables in the -O.pmi file). Placement: sector-local.</summary>
    void BuildSignTexts(SectorState st, PmiFile? pmi, string name)
    {
        var lay = _signLayout;
        if (lay == null || pmi == null) return;
        var v = new List<ObjectVertex>(); var tri = new List<uint>();
        foreach (var g in pmi.Groups)
        {
            if (g.Version != 4) continue;
            foreach (var inst in g.Instances)
            {
                if (inst.SignCodes == null) continue;
                var m = inst.Matrix;
                if (inst.Pre.HasValue) m = inst.Pre.Value * m;
                lay.AddSign(inst.SignCodes, m, v, tri);
            }
        }
        if (tri.Count == 0) return;
        var node = new Node { Name = name };
        AddSignMesh(st, node, v, tri, Matrix4x4.CreateTranslation(WorldLayout.SectorOrigin(st.Key)));
        if (node.Meshes.Count > 0) st.Nodes.Add(node);
    }

    /// <summary>Add sign text to World Actors that have already been created.</summary>
    void RebuildActorSignTexts(SectorState st)
    {
        var lay = _signLayout;
        if (lay == null || _actors == null || st.Model?.Actors == null) return;
        if (st.ActorSignsDone) return;
        st.ActorSignsDone = true;
        var origin = Matrix4x4.CreateTranslation(WorldLayout.SectorOrigin(st.Key));
        var v = new List<ObjectVertex>(); var tri = new List<uint>();
        foreach (var g in st.Model.Actors.Groups)
        {
            if (!_actors.ByHc.ContainsKey(g.Key)) continue;
            foreach (var inst in g.Instances)
            {
                if (inst.SignCodes == null) continue;
                var m = inst.Matrix;
                if (inst.Pre.HasValue) m = inst.Pre.Value * m;
                lay.AddSign(inst.SignCodes, m * origin, v, tri);
            }
        }
        var node = new Node { Name = "WorldActorSigns" };
        AddSignMesh(st, node, v, tri, Matrix4x4.Identity);
        if (node.Meshes.Count > 0) st.Nodes.Add(node);
    }

    static SectorKey SpotSector(Spot s)
    {
        var k = new SectorKey(s.AreaX, s.AreaZ, s.SectorX, s.SectorZ);
        var r = WorldLayout.SectorRect(k);
        if (r.Contains(new Vector2(s.Position.X, s.Position.Z))) return k;
        return WorldLayout.SectorAt(s.Position.X, s.Position.Z);
    }

    string? ExteriorFile(Spot s, string suffix)
    {
        if (string.IsNullOrEmpty(s.Code) || _index?.ExteriorDir == null) return null;
        var f = Path.Combine(_index.ExteriorDir, s.Code.ToLowerInvariant() + suffix + ".bnk");
        return GameFiles.Exists(f) ? f : null;
    }

    List<(Spot, string)> SpotsInSector(SectorKey k)
    {
        var list = new List<(Spot, string)>();
        if (_spots == null) return list;
        foreach (var s in _spots.Spots)
            if (SpotSector(s).Equals(k)) { var f = ExteriorFile(s, ""); if (f != null) list.Add((s, f)); }
        return list;
    }

    List<(Spot, string)> SpotsInArea(AreaKey a)
    {
        var list = new List<(Spot, string)>();
        if (_spots == null) return list;
        foreach (var s in _spots.Spots)
        {
            if (!WorldLayout.AreaAt(s.Position.X, s.Position.Z).Equals(a)) continue;
            var f = ExteriorFile(s, "_low") ?? ExteriorFile(s, "");
            if (f != null) list.Add((s, f));
        }
        return list;
    }

    //TODO: Remove and Cleanup
    static readonly HashSet<string> SpecialObjects = new()
    {
        "PORTAIL", "INTERIOR", "~a1~8aTAL_GA", "PORTAILG", "DOOR", "~85~96~94~b1~8fxAR",
        "~85~96~94R_GAR", "~97~90~a4~95~88~8eL_", "~94ORTAIL_", "~97ORTAIL_", "ECW_A",
    };

    /// <summary>
    /// Placement as in the game: Exterior scenes are modeled locally around (0,0,0) and are positioned at the spot location
    /// with a rotation around the Y axis equal to Value1 (radians). Gates/doors: see ObjectBuilder (root matrix for actor placement).
    /// local position 0.
    /// </summary>
    Node? BuildExterior(Spot spot, BankModel em, BuildResources res, string suffix)
    {
        if (em?.Objects == null) return null;
        var node = new Node { Name = $"Spot_{spot.Code}{suffix}" };
        // Unity: localRotation = Euler(0, Value1*Rad2Deg, 0), localPosition = spot.Position (relativ zum Parent-Ursprung)
        var parent = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, spot.Value1)) * Matrix4x4.CreateTranslation(spot.Position);
        foreach (var obj in ObjectBuilder.BuildObjects(em, parent, _textures, res, suffix.Length == 0 && Physics != null ? _collOpt : _opt, _renderer,
                     null, SpecialObjects.Contains))
        {
            node.Objects.Add(obj);
            node.Encapsulate(obj.BoundsMin, obj.BoundsMax);
        }
        return node;
    }

    /// <summary>Built Scene (Interior, Furniture): Objects in game coordinates, custom resources.</summary>
    public sealed class SceneHandle
    {
        public readonly List<RenderObject> Objects = new();
        public readonly BuildResources Res = new();
        public BankModel? Model;
        public Vector3 BoundsMin = new(float.MaxValue), BoundsMax = new(float.MinValue);
        public bool Active = true;
    }

    /// <summary>Background thread: Load a scene bank (like an exterior) and register its textures.</summary>
    public BankModel LoadSceneModel(string bankPath)
    {
        var em = BankModel.Load(bankPath, BankKind.Exterior);
        _textures.IndexBank(em.Bank);
        try { TextureAnimations.PrepareUva(em.Bank); } catch (Exception) { }   // Unity InteriorFx.Prepare: Textur-Animationen (.uva)
        return em;
    }

    /// <summary>Main thread: Build a scene under the parent (play area) – using collision triangles for each part. interior = always lit.</summary>
    public SceneHandle BuildScene(BankModel em, Matrix4x4 parent, bool interior, bool glassOnly = false)
    {
        // Interieur: Kollisionsdreiecke aller Meshes – welche davon kollidieren, entscheidet das Spiel (Unity PrepareStatic)
        var opt = new ObjectBuildOptions { LodDistanceScale = _opt.LodDistanceScale, CullLastLod = _opt.CullLastLod, Collision = true, CollisionAll = true };
        var h = new SceneHandle { Model = em };
        bool glow = MaterialFactory.ShowGlowMaterials;
        MaterialFactory.ShowGlowMaterials = true;   // Innenräume: Neon/Lichtbänder mit aufbauen (Unity BuildInterior)
        MaterialFactory.Interior = interior;
        MaterialFactory.InteriorGlass = glassOnly;
        try
        {
            if (em?.Objects != null)
                foreach (var o in ObjectBuilder.BuildObjects(em, parent, _textures, h.Res, opt, _renderer))
                {
                    h.Objects.Add(o);
                    h.BoundsMin = Vector3.Min(h.BoundsMin, o.BoundsMin); h.BoundsMax = Vector3.Max(h.BoundsMax, o.BoundsMax);
                }
        }
        finally { MaterialFactory.ShowGlowMaterials = glow; MaterialFactory.Interior = false; MaterialFactory.InteriorGlass = false; }
        TextureAnimations.Apply(em?.Bank?.FilePath, h.Objects);   // Unity InteriorFx.Apply: .uva-Animationen
        return h;
    }

    /// <summary>Collision triangles of a built scene (play area, 3 points each).</summary>
    public static List<Vector3> SceneTriangles(SceneHandle h)
    {
        var tris = new List<Vector3>();
        foreach (var ro in h.Objects)
        {
            if (ro.Lods.Length == 0) continue;
            bool flip = ro.Model.GetDeterminant() < 0f;
            foreach (var part in ro.Lods[0].Parts)
            {
                var t = part.Collision;
                if (t == null) continue;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    tris.Add(Vector3.Transform(t[i], ro.Model));
                    tris.Add(Vector3.Transform(t[flip ? i + 2 : i + 1], ro.Model));
                    tris.Add(Vector3.Transform(t[flip ? i + 1 : i + 2], ro.Model));
                }
            }
        }
        return tris;
    }

    public void DestroyScene(SceneHandle? h)
    {
        if (h == null) return;
        Scenes.Remove(h);
        h.Res.Destroy();
        h.Model?.Release();
    }

    /// <summary>Drawn scenes (interiors, furniture).</summary>
    public readonly List<SceneHandle> Scenes = new();
    /// <summary>Unity TDU2Interiors.HideWorld: Do not render the outside world (only scenes and the sky).</summary>
    public bool HideWorld;

    Node CreateMeshNode(string name, Vector3 gamePos, MeshData md, IGpuMaterial mat, BuildResources res)
    {
        var (v, i) = TerrainMeshBuilder.ToVertices(md);
        var mesh = _renderer.CreateMesh(v, i);
        res.Owned.Add(mesh);
        var node = new Node { Name = name };
        node.Meshes.Add((mesh, mat, Matrix4x4.CreateTranslation(gamePos)));
        // Ausdehnung (Spielraum) – LOD-Sperre im Interieur (Unity Lod1LocalBounds aus den Meshes)
        foreach (var p in md.Positions) node.Encapsulate(gamePos + p, gamePos + p);
        return node;
    }

    void UnloadSector(SectorKey k)
    {
        if (!_sectors.TryGetValue(k, out var st)) return;
        foreach (var nd in st.Nodes) nd.Disposed = true;
        SectorStats.Unloaded++;
        _sectors.Remove(k);
        st.Nodes.Clear();
        st.Placements = null;
        st.Rotors?.Release(); st.Rotors = null;
        st.Grass?.Dispose(); st.Grass = null;
        foreach (var c in st.Colliders) Physics?.Remove(c);
        st.Colliders.Clear();
        _actorBodies?.RemoveSector(k.ToString());
        st.Res.Destroy();
        st.Model?.Release();
        foreach (var em in st.ExtraModels) em.Release();
        SetLod1Visible(k);
        UpdateSkirts();
    }

    void UnloadArea(AreaKey k)
    {
        if (!_areas.TryGetValue(k, out var st)) return;
        AreaStats.Unloaded++;
        // Sektoren, die diese Area überlappen, zuerst entladen
        var drop = new List<SectorKey>();
        var ar = WorldLayout.AreaRect(k);
        foreach (var s in _sectors.Keys) if (WorldLayout.SectorRect(s).Overlaps(ar)) drop.Add(s);
        foreach (var s in drop) UnloadSector(s);

        _areas.Remove(k);
        foreach (var c in st.Colliders) Physics?.Remove(c);
        st.Colliders.Clear();
        foreach (var (key, node) in st.Registered)
        {
            if (_lod1BySector.TryGetValue(key, out var list)) list.Remove(node);
            _lod1Keys.Remove(node);
        }
        st.Nodes.Clear();
        st.Res.Destroy();
        st.Model?.Release();
        foreach (var em in st.ExtraModels) em.Release();
        if (_islandPieces.TryGetValue(k, out var ip)) ip.Active = ShowIslandLod;
    }

    public int DrawnObjects { get; private set; }

    /// <summary>Loading/Building Statistics (Debug UI): Loading = Background (read bank, build meshes), Building = Main Thread (GPU upload).</summary>
    public sealed class LoadStats
    {
        public int Loaded, Unloaded;
        public double LastLoadMs, LastBuildMs, SumLoadMs, SumBuildMs, MaxBuildMs;
        public double AvgLoadMs => Loaded == 0 ? 0 : SumLoadMs / Loaded;
        public double AvgBuildMs => Loaded == 0 ? 0 : SumBuildMs / Loaded;
        public void Add(double load, double build)
        {
            Loaded++; LastLoadMs = load; LastBuildMs = build; SumLoadMs += load; SumBuildMs += build;
            if (build > MaxBuildMs) MaxBuildMs = build;
        }
    }
    public readonly LoadStats AreaStats = new(), SectorStats = new();
    public int PendingJobs => RunningJobs();
    public int PlacementInstances { get { int n = 0; foreach (var s in _sectors.Values) n += s.Instances; return n; } }
    public bool LibraryReady => _library != null;
    public bool ActorsReady => _actors != null;
    public int PendingTextures => MaterialFactory.PendingTextures;

    /// <summary>Lines for the debug UI.</summary>
    /// <summary>Counters for memory diagnostics (MemoryLog).</summary>
    public string MemoryInfo()
    {
        int nodes = 0, objs = 0, cols = 0;
        foreach (var a in _areas.Values) { nodes += a.Nodes.Count; foreach (var n in a.Nodes) objs += n.Objects.Count; cols += a.Colliders.Count; }
        foreach (var s in _sectors.Values) { nodes += s.Nodes.Count; foreach (var n in s.Nodes) objs += n.Objects.Count; cols += s.Colliders.Count; }
        int lodKeys = 0; foreach (var l in _lod1BySector.Values) lodKeys += l.Count;
        return $"Areas {_areas.Count}, Sektoren {_sectors.Count}, Knoten {nodes}, Objekte {objs}, Kollider der Kacheln {cols}, Kollider-Aufträge {ColliderJobs}, " +
               $"LOD1-Listen {_lod1BySector.Count}/{lodKeys}/{_lod1Keys.Count}, Texturen global {_textures?.LoadedCount ?? 0}/{_textures?.Count ?? 0}, " +
               $"Bibliothek {_library?.BuiltCount ?? 0}, Kollisionsnetze Bibliothek {_libColl.Count}, " +
               $"Textur-Animationen {TextureAnimations.ActiveCount}, Ampeln {TrafficLights.Count}, Material-Nachladen {MaterialFactory.PendingTextures}";
    }

    public IEnumerable<string> DebugLines()
    {
        var g = _viewerGame;
        yield return $"Pos {g.X:0} / {g.Y:0} / {g.Z:0}";
        yield return $"Areas {_areas.Count}   Sektoren {_sectors.Count}   Jobs {RunningJobs()}/{MaxParallelLoads}";
        yield return $"Objekte gezeichnet {DrawnObjects}   PMI-Instanzen {PlacementInstances}";
        yield return $"Area   geladen {AreaStats.Loaded} entladen {AreaStats.Unloaded}";
        yield return $"       Laden {AreaStats.LastLoadMs:0} ms (Ø {AreaStats.AvgLoadMs:0})  Bauen {AreaStats.LastBuildMs:0.0} ms (Ø {AreaStats.AvgBuildMs:0.0}, max {AreaStats.MaxBuildMs:0})";
        yield return $"Sektor geladen {SectorStats.Loaded} entladen {SectorStats.Unloaded}";
        yield return $"       Laden {SectorStats.LastLoadMs:0} ms (Ø {SectorStats.AvgLoadMs:0})  Bauen {SectorStats.LastBuildMs:0.0} ms (Ø {SectorStats.AvgBuildMs:0.0}, max {SectorStats.MaxBuildMs:0})";
        yield return $"Library {(_library != null ? "ok" : "lädt")}   Actors {(_actors != null ? "ok" : "lädt")}   Texturen global {_textures?.Count ?? 0}  fehlend {MaterialFactory.PendingTextures}";
    }
    public int LoadedSectors => _sectors.Count;
    public int LoadedAreas => _areas.Count;

    /// <summary>Draw all active objects. viewProj is in render space (for frustum culling).</summary>
    readonly List<SectorState> _renderSectors = new();
    readonly List<List<DrawItem>> _sectorDraws = new();
    int[] _sectorDrawn = Array.Empty<int>();
    Matrix4x4 _collectRoot;
    Vector3 _collectCamera;
    Vector4[] _collectPlanes = Array.Empty<Vector4>();
    Action<int>? _collectSector;

    /// <summary>Work thread: Collecting visible objects (LOD) and placements in a sector – like DrawNode/PlacementRenderer.Render.</summary>
    void CollectSector(int i)
    {
        var s = _renderSectors[i];
        var list = _sectorDraws[i];
        var planes = _collectPlanes; var root = _collectRoot; var cam = _collectCamera;
        int drawn = 0;
        foreach (var n in s.Nodes)
        {
            if (!n.Active) continue;
            foreach (var (mesh, mat, model) in n.Meshes) list.Add(new DrawItem(mesh, mat, model * root));
            foreach (var o in n.Objects)
            {
                if (!o.Active) continue;
                if (!InFrustum(planes, o.BoundsMin + RootOffset, o.BoundsMax + RootOffset)) continue;
                var lod = o.SelectLod(cam);
                if (lod == null) continue;
                var m = o.Model * root;
                foreach (var p in lod.Parts) list.Add(new DrawItem(p.Mesh, p.Material, m));
                drawn++;
            }
        }
        s.Placements?.Collect(list, root, cam, planes);
        _sectorDrawn[i] = drawn;
    }

    public void Render(Vector3 cameraRender, Matrix4x4 viewProj, FrameGlobals globals)
    {
        globals.CameraPosition = cameraRender;
        globals.TerrainLower = TerrainLower;
        globals.TerrainPush = TerrainDepthPush;
        globals.Origin = new Vector2(RootOffset.X, RootOffset.Z);
        if (globals.Time <= 0f) globals.Time = (float)_time;
        if (ExtraLamps.Count > 0)
        {
            // Fahrzeuglichter vor den Straßenlampen (Unity: Light-Komponenten der Autos)
            int n0 = globals.Lamps != null ? System.Math.Min(globals.LampCount, globals.Lamps.Length) : 0;
            int n = ExtraLamps.Count + n0;
            if (_lampMerge.Length < n) _lampMerge = new LampLight[n];
            ExtraLamps.CopyTo(_lampMerge, 0);
            if (n0 > 0) Array.Copy(globals.Lamps!, 0, _lampMerge, ExtraLamps.Count, n0);
            globals.Lamps = _lampMerge;
            globals.LampCount = n;
        }
        _renderer.SetFrameGlobals(globals);

        var planes = Frustum(viewProj);
        var cameraGame = RenderToGame(cameraRender);
        var root = RootMatrix;
        int drawn = 0;

        void DrawNode(Node n)
        {
            if (!n.Active) return;
            foreach (var (mesh, mat, model) in n.Meshes)
                _renderer.DrawMesh(mesh, mat, model * root);
            foreach (var o in n.Objects)
            {
                if (!o.Active) continue;
                if (!InFrustum(planes, o.BoundsMin + RootOffset, o.BoundsMax + RootOffset)) continue;
                var lod = o.SelectLod(cameraGame);
                if (lod == null) continue;
                var m = o.Model * root;
                foreach (var p in lod.Parts) _renderer.DrawMesh(p.Mesh, p.Material, m);
                drawn++;
            }
        }

        // Frei platzierte Szenen (Interieurs, Möbel, Renn-Tore) erst NACH der opaken Welt zeichnen.
        // Unity zeichnet TDU2/Checkpoint mit RenderQueue 3000 (nach allen opaken Objekten); die additiven
        // Checkpoint-Tore wurden vorher von Straße/Terrain überdeckt und waren dadurch unsichtbar.
        void DrawScenes()
        {
            foreach (var sc in Scenes)
            {
                if (!sc.Active) continue;
                foreach (var o in sc.Objects)
                {
                    if (!o.Active || !InFrustum(planes, o.BoundsMin + RootOffset, o.BoundsMax + RootOffset)) continue;
                    var lod = o.SelectLod(cameraGame);
                    if (lod == null) continue;
                    var m = o.Model * root;
                    foreach (var p in lod.Parts) _renderer.DrawMesh(p.Mesh, p.Material, m);
                    drawn++;
                }
            }
        }

        if (HideWorld) { DrawScenes(); DrawnObjects = drawn; return; }
        foreach (var n in _islandPieces.Values) DrawNode(n);
        foreach (var a in _areas.Values) if (a.Built && !EditorIsHidden(a)) foreach (var n in a.Nodes) DrawNode(n);   // EditorIsHidden: Szenen-Editor (F1 > Open Editor)
        // Sektoren: Sichtbarkeit/LOD der Objekte und Platzierungen parallel je Sektor (jeder Sektor hat nur eigenen Zustand),
        // danach DrawMesh in fester Reihenfolge im Hauptthread (Renderer-Listen sind nicht threadsicher).
        _renderSectors.Clear();
        foreach (var s in _sectors.Values) if (s.Built && !EditorIsHidden(s)) _renderSectors.Add(s);
        int ns = _renderSectors.Count;
        while (_sectorDraws.Count < ns) _sectorDraws.Add(new List<DrawItem>(1024));
        if (_sectorDrawn.Length < ns) _sectorDrawn = new int[ns * 2];
        _collectRoot = root; _collectCamera = cameraGame; _collectPlanes = planes;
        if (ns > 1) Parallel.For(0, ns, _collectSector ??= CollectSector);
        else if (ns == 1) CollectSector(0);
        for (int i = 0; i < ns; i++)
        {
            var list = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sectorDraws[i]);
            foreach (ref readonly var d in list) _renderer.DrawMesh(d.Mesh, d.Material, d.Model);
            _sectorDraws[i].Clear();
            drawn += _sectorDrawn[i];
            _renderSectors[i].Grass?.Render(_renderer, root);
        }
        DrawAircraft(_renderer, cameraGame, root, planes);
        var waterMat = _oceanIsland ?? MaterialFactory.Water;
        if (_waterMesh != null && waterMat != null && !EditorHideWater)
            _renderer.DrawMesh(_waterMesh, waterMat, Matrix4x4.CreateTranslation(_waterPos) * root);
        // Himmelskuppel: erst die Sterne (td_dome mit Sternen ersetzt den Himmel), dann die Wolken
        if (ShowStars && !WorldWeather.Inside && globals.Sky.On && globals.Sky.StarsIntensity > 0f) _stars?.Draw(globals.CameraPosition);
        if (ShowStars && !WorldWeather.Inside && globals.Sky.On && globals.Sky.MoonPercent > 0f) _stars?.DrawMoon(globals.CameraPosition, globals.SunDirection, globals.Sky.MoonSize);
        if (ShowClouds && !WorldWeather.Inside) _clouds?.Draw(root);
        if (ShowPowerWires) _powerWires.Draw(_renderer, WireSets(), cameraGame, RootOffset, globals.SunDirection);
        DrawScenes();   // Checkpoint-Tore / Overlays zuletzt (Unity RenderQueue 3000)
        DrawnObjects = drawn;
    }

    /// <summary>
    /// Physics world for collision meshes of loaded sectors. null = no collision. Set by the game before sectors are loaded.
    /// </summary>
    public PhysicsWorld? Physics { get; set; }
    readonly Queue<SectorState> _colliderQueue = new();
    readonly List<Vector3> _collTris = new();
    /// <summary>Bauzeit des letzten Sektor-Kollisionsnetzes im Hintergrund (ms).</summary>
    public double ColliderBuildMs { get; private set; }
    /// <summary>Zeit im Hauptthread für das Eintragen (ms).</summary>
    public double ColliderAddMs { get; private set; }
    /// <summary>Streamer-Zeit, zu der zuletzt Kollider eingetragen wurden (Diagnose Hüpfer).</summary>
    public double LastColliderAddTime { get; private set; } = -1;
    public double Time => _time;
    public int ColliderJobs => _colliderJobs.Count + _colliderQueue.Count;

    sealed class ColliderJob
    {
        public SectorState St = null!;
        public Task<(PhysicsWorld.PreparedMesh? terrain, PhysicsWorld.PreparedMesh? objects)> Task = null!;
    }
    readonly List<ColliderJob> _colliderJobs = new();
    const int MaxColliderJobs = 3;

    /// <summary>
    /// Collision meshes for the constructed sectors (terrain + fixed objects, such as MapStreamer.AddColliders). Triangles and the Bepu search tree
    /// are generated in the background; this function only takes over finished meshes and starts new tasks (data snapshot in the main thread).
    /// </summary>
    void UpdateColliders()
    {
        if (Physics == null) return;
        var phys = Physics;
        // 1) fertige Netze eintragen
        for (int i = _colliderJobs.Count - 1; i >= 0; i--)
        {
            var job = _colliderJobs[i];
            if (!job.Task.IsCompleted) continue;
            _colliderJobs.RemoveAt(i);
            if (job.Task.IsFaulted) { Log.Warn($"Kollider {job.St.Key}: {job.Task.Exception?.GetBaseException().Message}"); continue; }
            var (terrain, objects) = job.Task.Result;
            var st = job.St;
            bool alive = st.Built && _sectors.TryGetValue(st.Key, out var cur) && ReferenceEquals(cur, st);
            if (!alive) { phys.Discard(terrain); phys.Discard(objects); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var c = phys.AddPrepared(terrain, st.Key + " Gelände");
            if (c != null) { c.Surface = 2; st.Colliders.Add(c); }   // Unity SurfaceOf: "Terrain" = Erde
            var co = phys.AddPrepared(objects, st.Key + " Objekte");
            if (co != null) st.Colliders.Add(co);
            ColliderAddMs = sw.Elapsed.TotalMilliseconds;
            ColliderBuildMs = (terrain?.BuildMs ?? 0) + (objects?.BuildMs ?? 0);
            LastColliderAddTime = _time;
        }
        // 2) neue Aufträge
        while (_colliderQueue.Count > 0 && _colliderJobs.Count < MaxColliderJobs)
        {
            var st = _colliderQueue.Dequeue();
            if (!st.Built || !_sectors.TryGetValue(st.Key, out var cur) || !ReferenceEquals(cur, st)) { st.Data = null; continue; }
            var origin = WorldLayout.SectorOrigin(st.Key);
            // Kollisionsnetz = sichtbares Gelände (Raster ohne Loch-Kacheln + BLOCK), ohne Skirts
            var terrain = st.Data?.Terrain;
            // Das sichtbare Gelände wird im Shader um TerrainLower abgesenkt (_TDU2TerrainLower) -> Kollider genauso tief,
            // sonst fahren die Räder auf Gelände ~10 cm über dem sichtbaren Boden
            var to = origin - new Vector3(0f, TerrainColliderLower, 0f);
            // feste Objekte (LOD0): Kollisionsdreiecke + Lage + Untergrund (Materialauswertung im Hauptthread)
            var parts = new List<(Vector3[] tris, Matrix4x4 m, byte surf)>();
            foreach (var nd in st.Nodes)
            {
                if (nd.Name == "WorldActors") continue;   // haben ein eigenes Netz (TryAddActors)
                foreach (var ro in nd.Objects)
                {
                    if (ro.Lods.Length == 0) continue;
                    byte surf = SurfaceOf(ro.Lods[0].Parts);
                    foreach (var part in ro.Lods[0].Parts)
                        if (part.Collision != null && part.Collision.Length >= 3) parts.Add((part.Collision, ro.Model, surf));
                }
            }
            st.Data = null;
            _colliderJobs.Add(new ColliderJob
            {
                St = st,
                Task = Task.Run(() =>
                {
                    PhysicsWorld.PreparedMesh? pt = null, po = null;
                    // Arrays in genauer Größe (keine wachsenden Listen: jede Verdopplung landete als Müll im Large Object Heap)
                    if (terrain != null)
                    {
                        int cnt = 0;
                        foreach (var sub in terrain.SubMeshes) cnt += sub.Count / 3 * 3;
                        var tris = new Vector3[cnt];
                        int w = 0;
                        foreach (var sub in terrain.SubMeshes)
                            for (int i = 0; i + 2 < sub.Count; i += 3)
                            {
                                tris[w++] = terrain.Positions[sub[i]] + to;
                                tris[w++] = terrain.Positions[sub[i + 1]] + to;
                                tris[w++] = terrain.Positions[sub[i + 2]] + to;
                            }
                        pt = phys.PrepareStaticMesh(tris.AsSpan(0, w));
                    }
                    int ocnt = 0;
                    foreach (var (t, _, _) in parts) ocnt += t.Length / 3;
                    var otris = new Vector3[ocnt * 3]; var osurf = new byte[ocnt];
                    // Debug-Index: Hülle + Dreiecksbereich je Objekt, damit einzelne Steine/Schilder im Debug erscheinen.
                    var debugParts = new List<PhysicsWorld.PreparedMesh.DebugPart>(parts.Count);
                    int ow = 0;
                    foreach (var (t, m, surf) in parts)
                    {
                        bool flip = m.GetDeterminant() < 0f;   // gespiegelte Platzierung kehrt die Wicklung um
                        int start = ow;
                        var pmin = new Vector3(float.MaxValue); var pmax = new Vector3(float.MinValue);
                        for (int i = 0; i + 2 < t.Length; i += 3)
                        {
                            osurf[ow] = surf;
                            var a = Vector3.Transform(t[i], m);
                            var b = Vector3.Transform(t[flip ? i + 2 : i + 1], m);
                            var c = Vector3.Transform(t[flip ? i + 1 : i + 2], m);
                            otris[ow * 3] = a; otris[ow * 3 + 1] = b; otris[ow * 3 + 2] = c;
                            pmin = Vector3.Min(pmin, Vector3.Min(a, Vector3.Min(b, c)));
                            pmax = Vector3.Max(pmax, Vector3.Max(a, Vector3.Max(b, c)));
                            ow++;
                        }
                        if (ow > start)
                            debugParts.Add(new PhysicsWorld.PreparedMesh.DebugPart((pmin + pmax) * 0.5f, (pmax - pmin) * 0.5f, start, ow - start));
                    }
                    po = phys.PrepareStaticMesh(otris.AsSpan(0, ow * 3), osurf.AsSpan(0, ow));
                    if (po != null) po.DebugParts = debugParts.ToArray();
                    return (pt, po);
                })
            });
        }
    }

    readonly List<byte> _collSurf = new();
    /// <summary>
    /// Lower terrain collider (m).
    /// With TerrainLower enabled, the wheels are visibly in contact with dirt roads, but curbs become steps (the car bounces).
    /// </summary>
    public float TerrainColliderLower = 0f;
    readonly Dictionary<LibraryObject, (Vector3[] tris, byte surf)> _libColl = new();
    /// <summary>
    /// Surface of an object such as TDU2Vehicle.SurfaceOf: material name + main/detail texture;
    /// mostly “grass” = grass (1), “earth/sand/gravel/terre/d2_/dirt_road” = earth (2), otherwise asphalt (0).
    /// </summary>
    static byte SurfaceOf(RenderPart[] parts)
    {
        int grass = 0, dirt = 0, n = 0;
        foreach (var p in parts)
        {
            n++;
            if (!MaterialFactory.TryGetInfo(p.Material, out var info)) continue;
            var d = info.Desc;
            string s = ((d.Name ?? "") + " " + (d.Main?.Name ?? "") + " " + (d.Detail?.Name ?? "")).ToLowerInvariant();
            if (s.Contains("grass")) grass++;
            else if (s.Contains("earth") || s.Contains("sand") || s.Contains("gravel") || s.Contains("terre") || s.Contains("d2_") || s.Contains("dirt_road")) dirt++;
        }
        if (grass * 2 > n) return 1;
        if (dirt * 2 > n) return 2;
        return 0;
    }

    /// <summary>Collision triangles (object space) of a library object (LOD0), cached.</summary>
    (Vector3[] tris, byte surf) LibraryCollision(LibraryObject obj)
    {
        if (_libColl.TryGetValue(obj, out var c)) return c;
        var list = new List<Vector3>();
        if (obj.Lods.Length > 0)
        {
            // Unity VegetationRenderer.CollisionMeshFor: Pflanzen (ein Teil mit Laub/Sprite/Gras-Material) -> Alpha-Test-Teile
            // ohne Kollision (Grasbüschel, Sträucher); sonst (Zäune, Gitter, Schilder) mit Kollision
            var parts = obj.Lods[0].Parts;
            bool plant = false;
            foreach (var p in parts) if (p.Material.Kind is MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.Grass) { plant = true; break; }
            foreach (var p in parts)
            {
                if (p.Collision == null) continue;
                if (plant && MaterialFactory.TryGetInfo(p.Material, out var info) && info.Desc.Cutoff > 0.01f) continue;
                list.AddRange(p.Collision);
            }
            // flache Bodenteile (Gullydeckel, Markierungen, Bordsteinkanten < 25 cm) nicht blockieren lassen
            if (list.Count > 0)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var q in list) { lo = System.MathF.Min(lo, q.Y); hi = System.MathF.Max(hi, q.Y); }
                if (hi - lo < 0.25f) list.Clear();
            }
        }
        c = (list.ToArray(), obj.Lods.Length > 0 ? SurfaceOf(obj.Lods[0].Parts) : (byte)0);
        _libColl[obj] = c;
        return c;
    }


    /// <summary>Ground height determined by physics raycast (Unity: Physics.Raycast pointing downward). false = nothing hit.</summary>
    public bool RaycastDown(Vector3 game, float above, float maxDistance, out float y)
    {
        y = 0f;
        if (Physics == null) return false;
        if (!Physics.Raycast(game + new Vector3(0f, above, 0f), -Vector3.UnitY, maxDistance, out var hit)) return false;
        y = hit.Point.Y;
        return true;
    }

    /// <summary>
    /// Ground height from the heightmap of the loaded sector – fallback for wheel contacts,
    /// as long as there is no collider at that location yet. Does not hit any objects (bridges/roofs), only the terrain.
    /// </summary>
    public bool TryGetGroundHeight(Vector3 game, out float y)
    {
        y = 0f;
        var k = WorldLayout.SectorAt(game.X, game.Z);
        if (!_sectors.TryGetValue(k, out var st) || !st.Built || st.Model?.Heights == null) return false;
        var o = WorldLayout.SectorOrigin(k);
        var g = st.Model.Heights;
        // Loch-Kacheln (Tunnelportale): dort gilt BLOCK statt des Rasters – keine Rasterhöhe liefern.
        if (st.DriveHoles != null)
        {
            int cells = g.Size - 1;
            int gx = (int)MathF.Floor((game.X - o.X) / g.Spacing);
            int gz = (int)MathF.Floor((game.Z - o.Z) / g.Spacing);
            if (gx >= 0 && gz >= 0 && gx < cells && gz < cells && st.DriveHoles[gz * cells + gx]) return false;
        }
        y = g.Sample(game.X - o.X, game.Z - o.Z) + o.Y - TerrainColliderLower;
        return true;
    }

    /// <summary>
    /// Environment zone (Environment.TypeID from ambiences.xmb) at a game position from the sector.psa:
    /// Zones of the sector and its neighbors that contain the point; the smallest (most specific) wins.
    /// -1 = no zone (sector not yet loaded or has no zones).
    /// </summary>
    public int AmbienceZoneAt(float gx, float gz)
    {
        int best = -1; float bestArea = float.MaxValue;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var k = WorldLayout.SectorAt(gx + dx * WorldLayout.SectorSize, gz + dz * WorldLayout.SectorSize);
                if (!_sectors.TryGetValue(k, out var st) || !st.Built || st.Model?.SoundZones == null) continue;
                foreach (var z in st.Model.SoundZones)
                    if (z.Area < bestArea && z.Contains(gx, gz)) { best = z.Type; bestArea = z.Area; }
            }
        return best;
    }

    /// <summary>
    /// Ground elevation for the large map: sector, otherwise loaded area,
    /// otherwise rough island elevation map. Separate from <see cref="TryGetGroundHeight"/>, which uses only sectors (wheel contacts).
    /// </summary>
    public bool TryGetMapGroundHeight(float gx, float gz, out float y)
    {
        y = 0f;
        var sk = WorldLayout.SectorAt(gx, gz);
        if (_sectors.TryGetValue(sk, out var st) && st.Built && st.Model?.Heights != null)
        {
            var o = WorldLayout.SectorOrigin(sk);
            y = st.Model.Heights.Sample(gx - o.X, gz - o.Z) + o.Y;
            return true;
        }
        var ak = WorldLayout.AreaAt(gx, gz);
        if (_areas.TryGetValue(ak, out var at) && at.Built && at.Model?.Heights != null)
        {
            var o = WorldLayout.AreaOrigin(ak);
            y = at.Model.Heights.Sample(gx - o.X, gz - o.Z);
            return true;
        }
        if (_islandModel?.Heights != null) { y = _islandModel.Heights.Sample(gx, gz); return true; }
        return false;
    }

    private readonly PowerWires _powerWires = new();

    IEnumerable<PowerWires.SectorWires> WireSets()
    {
        foreach (var s in _sectors.Values)
            if (s.Built && s.Wires != null) yield return s.Wires;
    }

    /// <summary>
    /// Determine attachment points once the masts (placements) are in place. Masts in the neighboring sector: wait up to 4 seconds,
    /// then build using the post-position; if the neighboring sector is loaded later, recalculate.
    /// </summary>
    void UpdatePowerWires(float dt)
    {
        if (!ShowPowerWires) return;
        int budget = 2;
        foreach (var st in _sectors.Values)
        {
            var w = st.Wires;
            if (w == null || !st.Built || !st.VegetationDone || w.Resolved) continue;
            st.WireWait += dt;   // Wartezeit läuft für alle offenen Sektoren (nach 4 s auch ohne Masten bauen)
            // höchstens 2 Sektoren je Bild und jeder Sektor höchstens alle 0,5 s (Masten kommen mit Nachbarsektoren, nicht jedes Bild)
            if (budget <= 0 || _time < st.WireNextTry) continue;
            st.WireNextTry = _time + 0.5;
            _powerWires.Resolve(w, FindPlacementInstances, st.WireWait > 4f);
            budget--;
        }
    }

    /// <summary>Placed library objects (e.g., SF_LIGH* streetlights) within the vicinity, game coordinates (for TDU2Weather lamps).</summary>
    public void FindPlacementInstances(string[] prefixes, Vector3 posGame, float radius, List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)> result)
    {
        foreach (var s in _sectors.Values)
            if (s.Built) s.Placements?.FindInstances(prefixes, posGame, radius, result);
    }

    /// <summary>
    /// Debug: the next object along the ray (bounds test) and its materials (name, shader, blend, textures,
    /// parameters) – for comparing faulty textures with the Unity project. Lines are also written to the log.
    /// </summary>
    public List<string> Pick(Vector3 originRender, Vector3 dir)
    {
        var lines = new List<string>();
        var o = RenderToGame(originRender);
        dir = Vector3.Normalize(dir);
        // Alle getroffenen Bounds sammeln. Liegt die Kamera in einer Box (t = 0, z.B. große BLOCK-Objekte),
        // wäre der Treffer nichtssagend: zuerst Boxen vor der Kamera nach Abstand, danach umschließende nach Größe.
        var hits = new List<(RenderObject ro, string where, float t, float vol)>();
        void Test(IEnumerable<Node> nodes, string label)
        {
            foreach (var n in nodes)
            {
                if (!n.Active) continue;
                foreach (var ro in n.Objects)
                {
                    if (!ro.Active || ro.BoundsMin.X > ro.BoundsMax.X) continue;
                    if (RayBox(o, dir, ro.BoundsMin, ro.BoundsMax, out float t))
                    {
                        var e = ro.BoundsMax - ro.BoundsMin;
                        hits.Add((ro, label + "/" + n.Name, t, MathF.Max(e.X, 0.01f) * MathF.Max(e.Y, 0.01f) * MathF.Max(e.Z, 0.01f)));
                    }
                }
            }
        }
        foreach (var a in _areas.Values) if (a.Built) Test(a.Nodes, a.Key.ToString());
        foreach (var s in _sectors.Values) if (s.Built) Test(s.Nodes, s.Key.ToString());
        if (hits.Count == 0) { lines.Add("Kein Objekt im Blick."); return lines; }
        hits.Sort((x, y) =>
        {
            bool xi = x.t <= 0f, yi = y.t <= 0f;
            if (xi != yi) return xi ? 1 : -1;
            return xi ? x.vol.CompareTo(y.vol) : x.t.CompareTo(y.t);
        });

        var seen = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
        for (int h = 0; h < hits.Count && h < 4; h++)
        {
            var (best, where, bestT, _) = hits[h];
            lines.Add($"{best.Name}  ({where}, {bestT:0.0} m, LODs {best.Lods.Length})");
            foreach (var lod in best.Lods)
                foreach (var p in lod.Parts)
                {
                    if (!seen.Add(p.Material)) continue;
                    if (!MaterialFactory.TryGetInfo(p.Material, out var mi)) { lines.Add($"- {p.Material.Kind} (ohne Info)"); continue; }
                    var d = mi.Desc;
                    lines.Add($"- {mi.Def.Name}  {d.Kind}/{mi.Blend}  Shader {mi.Def.ShaderSet:x16}  Flags {Convert.ToHexString(mi.Def.Flags)}");
                    lines.Add($"  COLOR {mi.MainTexture ?? "-"}{(mi.Lightmap ? " (Lightmap)" : "")}  DETAIL {mi.DetailTexture ?? "-"}{(mi.Packed ? " (packed)" : "")}");
                    lines.Add($"  SHADOW {mi.ShadowTexture ?? "-"}  TINT {mi.TintTexture ?? "-"}  Straße {mi.Def.IsRoad} Boden {mi.Def.IsGround}");
                    lines.Add($"  DetailUV {(d.DetailUvLast ? "letzter" : "2")}  Boost {d.DetailBoost:0.00} Gray {d.DetailGray} ColorStr {d.ColorStrength:0.00} Unlit {d.Unlit:0} Cutoff {d.Cutoff:0.00} AlphaDetail {d.AlphaFromDetail} Lit {d.Lit:0}");
                }
        }
        foreach (var l in lines) Log.Info("[Auswahl] " + l);
        return lines;
    }

    static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t)
    {
        float t0 = 0f, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float a = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, b = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-8f) { if (oi < a || oi > b) { t = 0; return false; } continue; }
            float u0 = (a - oi) / di, u1 = (b - oi) / di;
            if (u0 > u1) (u0, u1) = (u1, u0);
            t0 = MathF.Max(t0, u0); t1 = MathF.Min(t1, u1);
            if (t0 > t1) { t = 0; return false; }
        }
        t = t0;
        return true;
    }

    static Vector4[] Frustum(Matrix4x4 m)
    {
        // Zeilenvektoren: clip = v * M -> Ebenen aus den Spalten
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        return new[] { c3 + c0, c3 - c0, c3 + c1, c3 - c1, c2, c3 - c2 };
    }

    public static bool SphereInFrustum(Vector4[] planes, Vector3 c, float r)
    {
        foreach (var p in planes)
        {
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len < 1e-12f) continue;
            if ((p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W) / len < -r) return false;
        }
        return true;
    }

    /// <summary>Umkreis der Reflexionssonde, in dem Objekte unabhängig vom Bildausschnitt gezeichnet werden (m).</summary>
    public static float ProbeCullRadius = 120f;

    static bool InFrustum(Vector4[] planes, Vector3 min, Vector3 max)
    {
        if (min.X > max.X) return true;
        // Reflexionssonde am Auto (Unity ReflectionProbe, eigenes Culling): Umgebung auch außerhalb des Bildes zeichnen
        if (RenderFx.ProbeOn && !RenderFx.ProbeSettled)   // statische Sonde fertig (Interieur): nur noch das Bild
        {
            var c = Vector3.Clamp(RenderFx.ProbePos, min, max);
            if (Vector3.DistanceSquared(c, RenderFx.ProbePos) < ProbeCullRadius * ProbeCullRadius) return true;
        }
        foreach (var p in planes)
        {
            var v = new Vector3(p.X >= 0 ? max.X : min.X, p.Y >= 0 ? max.Y : min.Y, p.Z >= 0 ? max.Z : min.Z);
            if (p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W < 0) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _clouds?.Dispose();
        _clouds = null;
        _stars?.Dispose();
        _stars = null;
        _wetGround?.Dispose();
        _wetGround = null;
        DestroyMapTerrain();
        _powerWires.Dispose();
        DisposeAircraft();
        foreach (var job in _colliderJobs)
        {
            try
            {
                if (job.Task.Wait(3000)) { Physics?.Discard(job.Task.Result.terrain); Physics?.Discard(job.Task.Result.objects); }
            }
            catch (Exception) { }
        }
        _colliderJobs.Clear(); _colliderQueue.Clear();
        foreach (var k in _sectors.Keys.ToList()) UnloadSector(k);
        foreach (var k in _areas.Keys.ToList()) UnloadArea(k);
        foreach (var n in _islandPieces.Values) n.Meshes.Clear();
        _islandPieces.Clear();
        _islandRes.Destroy();
        _islandModel?.Release();
        _signMat?.Dispose(); _signMat = null;
        TextureAnimations.Clear();
        TrafficLights.Clear();
        if (_libraryTask != null && _library == null && _libraryTask.IsCompletedSuccessfully) _library = _libraryTask.Result;
        _library?.Destroy();
        _textures?.Clear();
        MaterialFactory.Reset();
    }
}
