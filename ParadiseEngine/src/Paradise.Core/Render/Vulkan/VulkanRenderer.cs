using Paradise.Core.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Vulkan-Renderer. Stellt bereit, was im Unity-Projekt die Engine geliefert hat:
/// Texturen (komplette Mip-Kette, Wrap-Modus, trilinear + Anisotropie 4), Materialien mit den
/// portierten Shadern (TDU2/Terrain, TDU2/Object, TDU2/ObjectTransparent), Meshes, Transparent-Queue
/// (hinten→vorne), globale Shader-Werte (Shader.SetGlobal*) und GPU-Speicherverwaltung.
/// Tiefe: Reversed-Z (Near = 1, Far = 0) gegen Z-Fighting bei 60 km Sichtweite.
/// </summary>
public sealed unsafe partial class VulkanRenderer : IRenderer
{
    private const int MaxFramesInFlight = 2;
    private const int MaterialUboFloats = 112;                   // 28 x vec4 (TDU2/Car: 24, TDU2/Avatar: 28)
    private static readonly float[] ClearColor = { 0.33f, 0.37f, 0.42f, 1f };

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants { public Matrix4x4 ViewProj; public Matrix4x4 Model; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FrameUbo
    {
        public Vector4 CamPos, SunDir, SunColor, Ambient, Misc, ViewRight, ViewUp;
        public Vector4 Shadow;      // x Stärke, y Distanz, z Normal-Bias (Texel), w Texelgröße (UV)
        public Vector4 Splits;      // Ende der Kaskaden 0..3 (m)
        public Vector4 TexelWorld;  // Texelgröße der Kaskaden in Metern
        public Matrix4x4 ShadowVP0, ShadowVP1, ShadowVP2, ShadowVP3;
        public Vector4 Env;          // x _TDU2NightWindows, y Nebel-Start, z Nebel-Ende, w Nebel an
        public Vector4 FogColor;     // rgb Nebelfarbe, w Himmel-Exposure
        public Vector4 OceanFresnel; // xyz, w _TDU2OceanSet
        public Vector4 OceanSpec;    // rgb, Schwelle
        public Vector4 OceanRefl;    // rgb, w SpecPower
        public Vector4 OceanMisc;    // x NMap fein, y NMap grob, z Unschärfe, w Lampenzahl
        public fixed float LampPos[MaxLamps * 4];   // xyz, w Reichweite
        public fixed float LampCol[MaxLamps * 4];   // rgb x Intensität
        public fixed float LampCone[MaxLamps * 4];  // x cos(außen), y cos(innen)
        public Vector4 Planar;       // Bodenspiegelung: x 1 an / -1 Spiegel-Durchgang, y Ebene, zw 1/Bildgröße
        public Vector4 Probe;        // Reflexionssonde: x 1 gültig
        // Autolicht des Originals (FrameGlobals.CarLight, Gamma-Raum): Ambient (w 1 = an), Diffus (w ENVCOEFF.x TD_CarPaint),
        // Glanz, ENVCOEFF TD_CarPaint2
        public Vector4 CarAmbient, CarDiffuse, CarSpecular, CarEnv;
        public Vector4 OceanParam;   // TD_Ocean: x 2·NMapLR.y, y 2·NMapHR.y, z attenuationBluriness, w attenuationFresnel
        public Vector4 OceanParam2;  // x specularPower/sunIntensity², y 1 = Meer-Spiegelbild in planarRefl
        // Original-Himmel td_dome (FrameGlobals.Sky, World/TDU2SkyDome): Var_PS c13..c23 + Streu-Sonne, Licht (Gamma-Raum)
        public Vector4 SkyBetaRM, SkyHG, SkyBetaDashR, SkyBetaDashM, SkyOneOver, SkyMul, SkySun;
        public Vector4 SkyS2R0, SkyS2R1, SkyS2R2, SkyS2R3;
        public Vector4 SkyScatter, SkyDiffuse, SkyAmbient;
        // TD_CloudMesh (FrameGlobals.Cloud*): Instance_Color, ColorDown, AlphaBlend, x Haze, y DirLight_Emissive.x
        public Vector4 CloudColor, CloudDown, CloudAlpha, CloudMisc;
        public Vector4 SkyStars;     // x StarsIntensity, y StarsFade.x, z StarsFade.y, w Skydome_moonPercent
        public Vector4 RainSpecs;    // TD_Road: (GI, wet, 1, puddle_height)
        public Vector4 RainRipple;   // TD_Road: (scroll_u, scroll_v, ripple_intensity, ripple_scale)
    }
    private const int MaxLamps = 32;

    // Schatten (Unity: Directional Light, Soft Shadows, 4 Kaskaden, QualitySettings.shadowDistance)
    private const int ShadowCascades = 4;
    private uint ShadowSize = 2048;   // GraphicsSettings.ShadowResolution (Unity QualitySettings.shadowResolution)

    // Grafik-Einstellungen (GraphicsSettings, Optionsmenü): Kantenglättung (MSAA), VSync, Texturstufe
    private SampleCountFlags _msaa = SampleCountFlags.Count1Bit;
    private SampleCountFlags _msaaCounts = SampleCountFlags.Count1Bit;
    private bool _vsync = true;
    private int _texMipLimit;
    private Image _msaaImage;
    private GpuAllocator.Allocation _msaaMem;
    private ImageView _msaaView;
    // MSAA: Auflösen in ein eigenes sRGB-Bild, danach Bytekopie ins Swapchain-Bild. Direktes Auflösen in das Swapchain-Bild
    // (MUTABLE_FORMAT wegen der UNORM-Sicht des Flash-Durchgangs) lieferte ein viel zu dunkles Bild.
    private Image _resolveImage;
    private GpuAllocator.Allocation _resolveMem;
    private ImageView _resolveView;
    private bool Msaa => _msaa != SampleCountFlags.Count1Bit;
    private Image _shadowImage;
    private GpuAllocator.Allocation _shadowMem;
    private ImageView _shadowArrayView;
    private readonly ImageView[] _shadowLayerViews = new ImageView[ShadowCascades];
    private readonly Framebuffer[] _shadowFramebuffers = new Framebuffer[ShadowCascades];
    private RenderPass _shadowPass;
    private Sampler _shadowSampler;
    private readonly Matrix4x4[] _shadowVP = new Matrix4x4[ShadowCascades];
    private readonly Vector4[] _shadowSphere = new Vector4[ShadowCascades];   // xyz Mitte, w Radius (Render-Raum)
    private Vector4 _shadowSplits, _shadowTexelWorld;
    private Matrix4x4 _view = Matrix4x4.Identity, _proj = Matrix4x4.Identity;
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _opaque = new();
    // zuletzt gebundene Material-/Mesh-Bindung (nur gültig, solange die Pipeline nicht gewechselt hat)
    private DescriptorSet _lastSet;
    private Silk.NET.Vulkan.Buffer _lastVb;
    // Schatten: Reihenfolge egal (nur Tiefe) -> nach Pipeline/Material/Mesh sortiert, damit kaum neu gebunden werden muss
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model, PipeId pipe, Vector4 sphere)> _shadowSorted = new();
    // Instanzpuffer (Modellmatrizen) je Bild im Flug + offener Lauf
    private const int InstCap = 65536;
    private readonly Silk.NET.Vulkan.Buffer[] _instBuf = new Silk.NET.Vulkan.Buffer[MaxFramesInFlight];
    private readonly GpuAllocator.Allocation[] _instMem = new GpuAllocator.Allocation[MaxFramesInFlight];
    private int _instUsed, _cInst;
    private VulkanMesh? _rMesh;
    private VulkanMaterial? _rMat;
    private PipeId _rPipe;
    private bool _rShadow, _rFlip;
    private Matrix4x4 _rFirst, _rVP;
    private int _rBase, _rCount;
    // Opake Objekte: nach Pipeline/Material/Mesh sortiert (wie Unitys Opaque-Sortierung nach Material), damit Läufe entstehen
    private struct OpaqueKey : IComparable<OpaqueKey>
    {
        public int Pipe, Index;
        public ulong Set, Vb;
        public int CompareTo(OpaqueKey o)
        {
            int c = Pipe.CompareTo(o.Pipe);
            if (c != 0) return c;
            c = Set.CompareTo(o.Set);
            if (c != 0) return c;
            c = Vb.CompareTo(o.Vb);
            return c != 0 ? c : Index.CompareTo(o.Index);
        }
    }
    private OpaqueKey[] _opaqueKeys = Array.Empty<OpaqueKey>();
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _opaqueTmp = new();

    /// <summary>Kleine Schlüssel sortieren statt der großen Einträge (Matrix je Eintrag), dann einmal umkopieren.</summary>
    private void SortOpaque()
    {
        int n = _opaque.Count;
        if (n < 2) return;
        if (_opaqueKeys.Length < n) _opaqueKeys = new OpaqueKey[System.Math.Max(n, _opaqueKeys.Length * 2)];
        var src = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_opaque);
        for (int i = 0; i < n; i++)
        {
            ref var e = ref src[i];
            _opaqueKeys[i] = new OpaqueKey { Pipe = (int)PipeFor(e.mat), Set = e.mat.Set.Handle, Vb = e.mesh.VertexBuffer.Handle, Index = i };
        }
        Array.Sort(_opaqueKeys, 0, n);
        _opaqueTmp.Clear();
        for (int i = 0; i < n; i++) _opaqueTmp.Add(src[_opaqueKeys[i].Index]);
        _opaque.Clear();
        _opaque.AddRange(_opaqueTmp);
    }

    /// <summary>Additive Overlays nach Pipeline/Material/Mesh sortieren – Insertion-invariante Blende erlaubt Batching.</summary>
    private void SortAdditive()
    {
        int n = _additive.Count;
        if (n < 2) return;
        if (_additiveKeys.Length < n) _additiveKeys = new OpaqueKey[System.Math.Max(n, _additiveKeys.Length * 2)];
        var src = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_additive);
        for (int i = 0; i < n; i++)
        {
            ref var e = ref src[i];
            _additiveKeys[i] = new OpaqueKey { Pipe = (int)PipeFor(e.mat), Set = e.mat.Set.Handle, Vb = e.mesh.VertexBuffer.Handle, Index = i };
        }
        Array.Sort(_additiveKeys, 0, n);
        _additiveTmp.Clear();
        for (int i = 0; i < n; i++) _additiveTmp.Add(src[_additiveKeys[i].Index]);
        _additive.Clear();
        _additive.AddRange(_additiveTmp);
    }

    private static readonly Comparison<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model, PipeId pipe, Vector4 sphere)> ShadowOrder = (a, b) =>
    {
        int c = ((int)a.pipe).CompareTo((int)b.pipe);
        if (c != 0) return c;
        c = a.mat.Set.Handle.CompareTo(b.mat.Set.Handle);
        return c != 0 ? c : a.mesh.VertexBuffer.Handle.CompareTo(b.mesh.VertexBuffer.Handle);
    };
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _shadowOnly = new();
    private int _cShadow;

    private enum PipeId { Simple, Terrain, Object, TransparentAlpha, TransparentAdd, Foliage, Sprite, GlowAlpha, GlowAdd, Overlay, Grass, Water, Sky, ShadowObject, ShadowFoliage, ShadowSprite, ShadowTerrain, Flash, Car, CarGlass, CarShadow, ShadowCar, Skidmark, Particle, ParticleAdd, Flare, FlashUi, MapTerrain, MapRoad, Cloud, SkyStars, SkyMoon, Lightning, Avatar, AvatarBlend, ShadowAvatar, OverlayUi, TransparentAlphaGlow }
    private const int PipeCount = 38;

    // ------------------------------------------------------------------ Zustand
    private readonly bool _validation;
    private Vk _vk = null!;
    private Instance _instance;
    private KhrSurface _khrSurface = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _graphicsQueue, _presentQueue;
    private uint _graphicsFamily, _presentFamily;
    private bool _anisotropy;
    private float _maxAniso = 1f;
    private GpuAllocator _alloc = null!;

    private KhrSwapchain _khrSwapchain = null!;
    private SwapchainKHR _swapchain;
    private Image[] _images = Array.Empty<Image>();
    private ImageView[] _imageViews = Array.Empty<ImageView>();
    private Format _format;
    // Flash wie Unity-IMGUI im linearen Farbraum: in den Bildpuffer ohne sRGB-Kodierung schreiben und dort mischen.
    // Dafür eine UNORM-Sicht auf die (sRGB-)Swapchain-Bilder (VK_KHR_swapchain_mutable_format) und ein eigener Durchgang.
    private bool _mutableSwap;
    private Format _uiFormat;
    private ImageView[] _uiViews = Array.Empty<ImageView>();
    private Framebuffer[] _uiFramebuffers = Array.Empty<Framebuffer>();
    private RenderPass _uiPass;
    private bool _uiActive;
    private const string MutableFormatExt = "VK_KHR_swapchain_mutable_format";
    private static Format UnormOf(Format f) => f == Format.B8G8R8A8Srgb ? Format.B8G8R8A8Unorm : f == Format.R8G8B8A8Srgb ? Format.R8G8B8A8Unorm : Format.Undefined;
    private Extent2D _extent;
    private RenderPass _renderPass;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();

    private DescriptorSetLayout _simpleSetLayout, _terrainSetLayout, _objectSetLayout, _frameSetLayout;
    private readonly PipelineLayout[] _layouts = new PipelineLayout[PipeCount];
    private readonly Pipeline[] _pipelines = new Pipeline[PipeCount];
    // Varianten mit Rückseiten-Culling (Original: D3DRS_CULLMODE je Material, 0xf871e0): [0] normal, [1] gespiegelter Durchgang
    // bzw. gespiegeltes Objekt (Wicklung umgekehrt, wie das Invert-Argument im Exe). Handle 0 = Pipeline zeichnet immer beidseitig.
    private readonly Pipeline[] _cullPipes = new Pipeline[PipeCount], _cullPipesInv = new Pipeline[PipeCount];
    private DescriptorPool _descriptorPool;
    private readonly Dictionary<(TextureWrap, TextureWrap), Sampler> _samplers = new();

    private readonly Silk.NET.Vulkan.Buffer[] _frameUbo = new Silk.NET.Vulkan.Buffer[MaxFramesInFlight];
    private readonly GpuAllocator.Allocation[] _frameUboMem = new GpuAllocator.Allocation[MaxFramesInFlight];
    private readonly DescriptorSet[] _frameSets = new DescriptorSet[MaxFramesInFlight];

    private VulkanTexture _white = null!, _gray = null!, _black = null!;
    private VulkanMaterial _defaultMaterial = null!;

    private Image _depthImage;
    private GpuAllocator.Allocation _depthMem;
    private ImageView _depthView;
    private Format _depthFormat;

    private Matrix4x4 _viewProj = Matrix4x4.Identity;
    private Vector3 _viewRight = Vector3.UnitX, _viewUp = Vector3.UnitY;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat)> _overlay = new();
    /// <summary>Overlay über allem (auch über Flash/HUD): Entwicklermenü (DrawOverlayTop).</summary>
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat)> _overlayTop = new();
    // Flash-Oberfläche (TDU2/FlashQuad): alle Quads eines Bildes in einem Puffer, Stapel in Aufrufreihenfolge
    private readonly List<ObjectVertex> _flashV = new();
    private readonly List<uint> _flashI = new();
    private readonly List<(VulkanMaterial mat, Matrix4x4 vp, Matrix4x4 prm, int first, int count)> _flashDraws = new();
    private readonly Dictionary<(IGpuTexture, IGpuTexture?), (VulkanMaterial mat, long used)> _flashMats = new();
    private int _cFlash;
    private long _dgGui, _dgOther, _dgSrgb, _dgLin;
    private long _dgLast;
    private readonly RenderStats _stats = new();
    private int _cDraw, _cTris, _cBinds, _cTransp, _cAdditive, _cOverlay;
    private FrameGlobals _globals = FrameGlobals.Default;
    private PipeId? _boundPipeline;
    private int _boundCull;   // 0 beidseitig, 1 Culling, 2 Culling gespiegelt (gilt zusammen mit _boundPipeline)
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model, float dist)> _transparent = new();
    // Additive Checkpoint-Overlays sind reihenfolgeunabhängig (SrcAlpha/One, kein Tiefenschreiben):
    // diese können wie opake Objekte nach Material/Mesh sortiert und instanziert werden.
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _additive = new();
    private OpaqueKey[] _additiveKeys = Array.Empty<OpaqueKey>();
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _additiveTmp = new();
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _background = new();

    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();
    private Silk.NET.Vulkan.Semaphore[] _imageAvailable = Array.Empty<Silk.NET.Vulkan.Semaphore>();
    private Silk.NET.Vulkan.Semaphore[] _renderFinished = Array.Empty<Silk.NET.Vulkan.Semaphore>();
    private Fence[] _inFlight = Array.Empty<Fence>();
    private Fence[] _imagesInFlight = Array.Empty<Fence>();

    private int _currentFrame;
    private uint _imageIndex;
    private bool _skipFrame, _inFrame;
    private bool _framebufferResized;
    private long _frameCounter;
    private bool _warnedFormat;

    // Verzögertes Freigeben statt DeviceWaitIdle pro Objekt (Entladen eines Sektors ruckelte sonst)
    private readonly List<(long frame, Action destroy)> _pendingDestroy = new();
    private readonly HashSet<IDisposable> _live = new();

    private nint _hwnd, _hinstance;
    private int _width, _height;
    private bool _disposed;

    public string DeviceName { get; private set; } = "Vulkan";
    public float AspectRatio => _extent.Height == 0 ? 1f : (float)_extent.Width / _extent.Height;
    public int Width => (int)_extent.Width;
    public int Height => (int)_extent.Height;
    public RenderStats Stats => _stats;

    public VulkanRenderer(bool? validation = null)
    {
        _validation = validation ?? Environment.GetEnvironmentVariable("PARADISE_VK_VALIDATION") == "1";
    }

    public void Initialize(nint windowHandle, int width, int height)
    {
        _hwnd = windowHandle;
        _hinstance = GetModuleHandle(null);
        _width = width; _height = height;

        _vk = Vk.GetApi();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        _alloc = new GpuAllocator(_vk, _physical, _device);
        TakeGraphicsSettings();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateRenderPass();
        CreateCommandPool();
        CreateDescriptorSetLayouts();
        CreateDescriptorPool();
        CreateTimestamps();
        CreateShadowResources();
        CreateFrameBuffers();
        CreateDefaultTextures();
        CreatePipelines();
        CreateFramebuffers();
        CreateCommandBuffers();
        CreateShadowCommandBuffers();
        CreateSyncObjects();

        Log.Info($"Vulkan initialized to ‘{DeviceName}’, swapchain {_extent.Width}x{_extent.Height} ({_format}), {_images.Length} images, Aniso={(_anisotropy ? _maxAniso : 0)}.");
    }

    public void Resize(int width, int height)
    {
        _width = width; _height = height;
        _framebufferResized = true;
    }

    public void BeginFrame()
    {
        _skipFrame = false;
        long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        fixed (Fence* pFence = &_inFlight[_currentFrame])
            _vk.WaitForFences(_device, 1, pFence, true, ulong.MaxValue);
        PollSceneCapture();   // Bildaufnahme dieses Bildes im Flug fertig (VulkanRendererCapture)
        long tq = System.Diagnostics.Stopwatch.GetTimestamp();
        ReadTimestamps();
        RunPendingDestroys(_frameCounter);

        long acqStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Result acquire = _khrSwapchain.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailable[_currentFrame], default, ref _imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr) { RecreateSwapchain(); _skipFrame = true; return; }
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            throw new Exception($"AcquireNextImage failed: {acquire}");

        // Kein CPU-Warten auf das Swapchain-Bild: Command-Buffer, UBO und Instanzpuffer gehören zum Bild im Flug (_currentFrame)
        // und sind durch _inFlight[_currentFrame] oben geschützt; die GPU-Reihenfolge am Bild regelt das Acquire-Semaphor.
        // (Vorher: Fence je Swapchain-Bild – bei 3 Bildern und 2 Fences war das der Fence des VORIGEN Bildes,
        //  die CPU wartete also jedes Mal, bis die GPU ganz fertig war: CPU und GPU liefen nacheinander statt parallel.)
        tq = System.Diagnostics.Stopwatch.GetTimestamp();

        var cmd = _commandBuffers[_currentFrame];
        _vk.ResetCommandBuffer(cmd, 0);
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (_vk.BeginCommandBuffer(cmd, in begin) != Result.Success)
            throw new Exception("BeginCommandBuffer failed.");

        _boundPipeline = null;
        _transparent.Clear();
        _additive.Clear();
        _overlay.Clear();
        foreach (var (om, _) in _overlayTop) om.Dispose();
        _overlayTop.Clear();
        _flashV.Clear(); _flashI.Clear(); _flashDraws.Clear(); _cFlash = 0;
        _opaque.Clear();
        _shadowOnly.Clear();
        _instUsed = 0; _rCount = 0; _rMesh = null; _rMat = null;
        _cDraw = _cTris = _cBinds = _cTransp = _cAdditive = _cOverlay = _cShadow = 0;
        _inFrame = true;
        UpdateRainDescriptors(_currentFrame);
        _activeFrameSet = _frameSets[_currentFrame];
        _background.Clear();
        WriteFrameUbo();
    }

    public void SetCamera(Matrix4x4 view, Matrix4x4 projection)
    {
        _view = view; _proj = projection;
        _viewProj = view * projection;
        // UNITY_MATRIX_V[0]/[1]: Kamera rechts/oben in Weltkoordinaten (Zeilenvektor-View -> Spalten)
        _viewRight = new Vector3(view.M11, view.M21, view.M31);
        _viewUp = new Vector3(view.M12, view.M22, view.M32);
        if (_inFrame) WriteFrameUbo();
    }

    public void SetFrameGlobals(in FrameGlobals globals)
    {
        _globals = globals;
        if (_globals.SunDirection.LengthSquared() < 1e-6f) _globals.SunDirection = Vector3.UnitY;
        if (_inFrame) WriteFrameUbo();
    }

    private void WriteFrameUbo()
    {
        var g = _globals;
        var u = new FrameUbo
        {
            CamPos = new Vector4(g.CameraPosition, g.TerrainLower),
            SunDir = new Vector4(Vector3.Normalize(g.SunDirection), g.TerrainPush),
            //Light.color / RenderSettings.ambientLight / fogColor werden im linearen Farbraum umgerechnet
            SunColor = new Vector4(ToLin(g.SunColor), g.Wet),
            Ambient = new Vector4(ToLin(g.Ambient), g.DayDim),
            Misc = new Vector4(g.NightK, g.StreetLight, g.Origin.X, g.Origin.Y),
            ViewRight = new Vector4(_viewRight, g.Time > 0 ? g.Time : (float)_clock.Elapsed.TotalSeconds),
            ViewUp = new Vector4(_viewUp, 0),
            Shadow = new Vector4(g.ShadowStrength, g.ShadowDistance, 1.5f, 1f / ShadowSize),
            Splits = _shadowSplits,
            TexelWorld = _shadowTexelWorld,
            ShadowVP0 = _shadowVP[0],
            ShadowVP1 = _shadowVP[1],
            ShadowVP2 = _shadowVP[2],
            ShadowVP3 = _shadowVP[3],
            Env = new Vector4(g.NightWindows, g.FogStart, g.FogEnd, g.FogEnd > g.FogStart ? 1f : 0f),
            FogColor = new Vector4(ToLin(g.FogColor), g.SkyExposure > 0f ? g.SkyExposure : 1f),
            OceanFresnel = new Vector4(g.OceanFresnel, g.OceanSet ? 1f : 0f),
            OceanSpec = g.OceanSpec,
            OceanRefl = new Vector4(g.OceanRefl, g.OceanSpecPower),
            Planar = PlanarUbo,
            Probe = ProbeUbo,
            CarAmbient = new Vector4(g.CarLight.Ambient, g.CarLight.On ? 1f : 0f),
            CarDiffuse = new Vector4(g.CarLight.Diffuse, g.CarLight.EnvFactor),
            CarSpecular = new Vector4(g.CarLight.Specular, 0f),
            CarEnv = g.CarLight.EnvCoeff,
            OceanParam = g.OceanParam,
            OceanParam2 = new Vector4(g.OceanSpecPow, _oceanPlanar ? 1f : 0f, 0f, 0f),
            SkyBetaRM = g.Sky.BetaRPlusBetaM,
            SkyHG = g.Sky.HGgSaturation,
            SkyBetaDashR = g.Sky.BetaDashR,
            SkyBetaDashM = g.Sky.BetaDashM,
            SkyOneOver = g.Sky.OneOverBetaRPlusBetaM,
            SkyMul = new Vector4(g.Sky.Multipliers.X, g.Sky.On ? 1f : 0f, 0f, 0f),
            SkySun = g.Sky.SunColorAndIntensity,
            SkyS2R0 = g.Sky.SpectrumToRgb0,
            SkyS2R1 = g.Sky.SpectrumToRgb1,
            SkyS2R2 = g.Sky.SpectrumToRgb2,
            SkyS2R3 = g.Sky.SpectrumToRgb3,
            SkyScatter = new Vector4(g.Sky.ScatterSun, 0f),
            SkyDiffuse = new Vector4(g.Sky.DirLightDiffuse, 0f),
            SkyAmbient = new Vector4(g.Sky.AmbLight, 0f),
            CloudColor = g.CloudColor,
            CloudDown = g.CloudDown,
            CloudAlpha = g.CloudAlpha,
            CloudMisc = new Vector4(g.CloudMisc, 0f, 0f),
            SkyStars = new Vector4(g.Sky.StarsIntensity, g.Sky.StarsFadeStart, g.Sky.StarsFadeScale, g.Sky.MoonPercent),
            RainSpecs = g.RainSpecs,
            RainRipple = new Vector4(RenderFx.RainScroll.X, RenderFx.RainScroll.Y, g.RainRipple.Z, g.RainRipple.W),
        };
        int lc = g.Lamps != null ? System.Math.Min(System.Math.Min(g.LampCount, g.Lamps.Length), MaxLamps) : 0;
        u.OceanMisc = new Vector4(g.OceanNMap, g.OceanBlur, lc);
        for (int i = 0; i < lc; i++)
        {
            var l = g.Lamps![i];
            u.LampPos[i * 4] = l.Position.X; u.LampPos[i * 4 + 1] = l.Position.Y; u.LampPos[i * 4 + 2] = l.Position.Z; u.LampPos[i * 4 + 3] = l.Range;
            var dir = l.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(l.Direction) : new Vector3(0f, -1f, 0f);
            var lcol = ToLin(l.Color);   //Light.color linear
            u.LampCol[i * 4] = lcol.X; u.LampCol[i * 4 + 1] = lcol.Y; u.LampCol[i * 4 + 2] = lcol.Z; u.LampCol[i * 4 + 3] = dir.Y;
            u.LampCone[i * 4] = l.CosOuter; u.LampCone[i * 4 + 1] = l.CosInner; u.LampCone[i * 4 + 2] = dir.X; u.LampCone[i * 4 + 3] = dir.Z;
        }
        *(FrameUbo*)_frameUboMem[_currentFrame].Mapped = u;
    }

    public void DrawMesh(IGpuMesh mesh, Matrix4x4 model)
    {
        if (mesh is VulkanMesh vm) DrawMesh(vm, vm.OwnedMaterial, model);
    }

    public void SetMaterialUv(IGpuMaterial material, Vector4 mainST, Vector4 detailST)
    {
        if (material is not VulkanMaterial vm || vm.UboMem.Mapped == null) return;
        if (vm.Kind is not (MaterialKind.Object or MaterialKind.Transparent)) return;
        float* f = (float*)vm.UboMem.Mapped;
        SetVec(f, 9, mainST.X, mainST.Y, mainST.Z, mainST.W);
        SetVec(f, 10, detailST.X, detailST.Y, detailST.Z, detailST.W);
    }

    private readonly List<VulkanMesh> _dynamicMeshes = new();

    public void DrawDynamic(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material, Matrix4x4 model)
    {
        if (_skipFrame || !_inFrame || indices.Length == 0 || vertices.Length == 0 || material is not VulkanMaterial) return;
        var mesh = UploadMesh(MemoryMarshal.AsBytes(vertices), indices, true);
        _dynamicMeshes.Add(mesh);
        DrawMesh(mesh, material, model);
    }

    public void DrawOverlay(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material)
    {
        if (_skipFrame || !_inFrame || indices.Length == 0 || material is not VulkanMaterial vm || vm.Kind != MaterialKind.Overlay) return;
        var mesh = UploadMesh(MemoryMarshal.AsBytes(vertices), indices, true);
        _overlay.Add((mesh, vm));
    }

    public void DrawOverlayTop(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material)
    {
        if (_skipFrame || !_inFrame || indices.Length == 0 || material is not VulkanMaterial vm || vm.Kind != MaterialKind.Overlay) return;
        var mesh = UploadMesh(MemoryMarshal.AsBytes(vertices), indices, true);
        _overlayTop.Add((mesh, vm));
    }

    public void DrawFlash(ReadOnlySpan<ObjectVertex> quadVertices, IGpuTexture texture, IGpuTexture? mask, in FlashQuadParams p)
    {
        if (_skipFrame || !_inFrame || quadVertices.Length < 4 || _extent.Width == 0 || _extent.Height == 0) return;
        // Material je (Textur, Maske) – Descriptor-Set mit beiden Samplern; ungenutzte werden in EndFrame freigegeben
        var key = (texture, mask);
        if (!_flashMats.TryGetValue(key, out var e))
        {
            var mat = (VulkanMaterial)CreateMaterial(new MaterialDesc { Kind = MaterialKind.Flash, Name = "Flash", Main = texture, Detail = mask });
            e = (mat, _frameCounter);
        }
        e.used = _frameCounter;
        _flashMats[key] = e;

        // viewProj: Pixel -> NDC (x/y-Skalierung + Versatz); [2][0] = LumAlpha, [2][1] = Maske an, [2][3] = Unity-IMGUI-Ausgabe
        var vp = new Matrix4x4(2f / _extent.Width, 0, 0, 0, 0, 2f / _extent.Height, 0, 0, p.LumAlpha ? 1f : 0f, mask != null ? 1f : 0f, 1, p.UnityGui ? 1f : 0f, -1, -1, 0, 1);
        // model-Zeilen: Mul, Add, ClipRect, MaskRect
        var prm = new Matrix4x4(p.Mul.X, p.Mul.Y, p.Mul.Z, p.Mul.W, p.Add.X, p.Add.Y, p.Add.Z, p.Add.W,
                                p.Clip.X, p.Clip.Y, p.Clip.Z, p.Clip.W, p.MaskRect.X, p.MaskRect.Y, p.MaskRect.Z, p.MaskRect.W);
        int quads = quadVertices.Length / 4;
        // Diagnose ([DIAG Flash] im Log): welche Ausgabe und welches Texturformat die Flash-Quads haben
        if (p.UnityGui) _dgGui += quads; else _dgOther += quads;
        if (texture is VulkanTexture dvt) { if (dvt.Linear) _dgLin += quads; else _dgSrgb += quads; }
        int first = _flashI.Count;
        for (int q = 0; q < quads; q++)
        {
            uint b = (uint)_flashV.Count;
            for (int k = 0; k < 4; k++) _flashV.Add(quadVertices[q * 4 + k]);
            _flashI.Add(b); _flashI.Add(b + 1); _flashI.Add(b + 2);
            _flashI.Add(b); _flashI.Add(b + 2); _flashI.Add(b + 3);
        }
        int n = _flashDraws.Count;
        if (n > 0)
        {
            var last = _flashDraws[n - 1];
            if (ReferenceEquals(last.mat, e.mat) && last.vp == vp && last.prm == prm && last.first + last.count == first)
            {
                _flashDraws[n - 1] = (last.mat, vp, prm, last.first, last.count + quads * 6);
                return;
            }
        }
        _flashDraws.Add((e.mat, vp, prm, first, quads * 6));
    }

    /// <summary>Overlay über allem (DrawOverlayTop): nach Flash, im UI-Durchgang mit eigener Pipeline (UNORM-Ziel, sRGB im Shader).</summary>
    private void DrawOverlayTopList(PipeId id)
    {
        if (_overlayTop.Count == 0) return;
        if (_extent.Width == 0 || _extent.Height == 0 || _pipelines[(int)id].Handle == 0)
        {
            foreach (var (om, _) in _overlayTop) om.Dispose();
            _overlayTop.Clear();
            return;
        }
        var vp = new Matrix4x4(2f / _extent.Width, 0, 0, 0, 0, 2f / _extent.Height, 0, 0, 0, 0, 1, 0, -1, -1, 0, 1);
        _boundPipeline = null;
        foreach (var (mesh, mat) in _overlayTop) { Emit(mesh, mat, Matrix4x4.Identity, id, false, vp, 0, 1); mesh.Dispose(); }
        _cOverlay += _overlayTop.Count;
        _overlayTop.Clear();
        _boundPipeline = null;
    }

    private void DrawFlashBatches()
    {
        if (_flashDraws.Count == 0 || _flashI.Count == 0) return;
        var verts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_flashV);
        var idx = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_flashI);
        var mesh = UploadMesh(MemoryMarshal.AsBytes(verts), idx, true);
        var cmd = _commandBuffers[_currentFrame];
        var fid = _uiActive ? PipeId.FlashUi : PipeId.Flash;
        var layout = _layouts[(int)fid];
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines[(int)fid]);
        var fs = _frameSets[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 1, 1, &fs, 0, null);
        _boundPipeline = fid;
        _lastSet = default; _lastVb = default;
        _cBinds++;
        var vbuf = mesh.VertexBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &vbuf, &offset);
        _vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
        foreach (var (mat, vp, prm, first, count) in _flashDraws)
        {
            var vpx = vp;
            vpx.M24 = _uiActive ? 1f : 0f;   // [1][3]: Ziel ohne sRGB-Kodierung (UNORM-Sicht)
            var push = new PushConstants { ViewProj = vpx, Model = prm };
            _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
            var set = mat.Set;
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
            _vk.CmdDrawIndexed(cmd, (uint)count, 1, (uint)first, 0, 0);
            _cDraw++;
            _cTris += count / 3;
        }
        _cFlash = _flashDraws.Count;
        if (_frameCounter - _dgLast >= 600 || _dgLast == 0)
        {
            _dgLast = _frameCounter == 0 ? 1 : _frameCounter;
            _dgGui = _dgOther = _dgSrgb = _dgLin = 0;
        }
        _flashDraws.Clear(); _flashV.Clear(); _flashI.Clear();
        mesh.Dispose();   // verzögert freigegeben

        // Materialien, die länger nicht benutzt wurden (z.B. Textur entsorgt), freigeben
        if ((_frameCounter & 255) == 0 && _flashMats.Count > 0)
        {
            List<(IGpuTexture, IGpuTexture?)>? old = null;
            foreach (var kv in _flashMats) if (_frameCounter - kv.Value.used > 600) (old ??= new()).Add(kv.Key);
            if (old != null) foreach (var k in old) { _flashMats[k].mat.Dispose(); _flashMats.Remove(k); }
        }
    }

    public void DrawMesh(IGpuMesh mesh, IGpuMaterial? material, Matrix4x4 model)
    {
        if (_skipFrame || !_inFrame || mesh is not VulkanMesh vm || vm.IndexCount == 0) return;
        var mat = material as VulkanMaterial ?? _defaultMaterial;

        bool objectMesh = vm.IsObjectFormat;
        bool objectMat = mat.Kind is MaterialKind.Object or MaterialKind.Transparent or MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.SpriteGlow or MaterialKind.Grass or MaterialKind.Water
            or MaterialKind.Car or MaterialKind.CarGlass or MaterialKind.CarShadow or MaterialKind.Skidmark or MaterialKind.Particle or MaterialKind.Flare or MaterialKind.MapRoad or MaterialKind.Cloud or MaterialKind.SkyStars or MaterialKind.SkyMoon
            or MaterialKind.Lightning or MaterialKind.Avatar;
        if (mat.Kind == MaterialKind.Overlay) return;
        if (objectMesh != objectMat)
        {
            if (!_warnedFormat) { _warnedFormat = true; Log.Warn($"DrawMesh: Material {mat.Kind} does not match the vertex format—skipped."); }
            return;
        }
        if (mat.Kind == MaterialKind.Water)
        {
            // Unity: Queue "Transparent-10" -> vor allen anderen transparenten Objekten; Meer: Insel-, dann Area-, dann Sektorebene
            _transparent.Add((vm, mat, model, mat.WaterSort));
            return;
        }
        if (mat.Kind is MaterialKind.Cloud or MaterialKind.SkyStars or MaterialKind.SkyMoon)
        {
            // Himmelskuppel-Teile (Sterne, Wolken): direkt nach dem Himmel und vor allem anderen, in Aufrufreihenfolge
            // (Tiefe im VS auf die Fernebene, kein Tiefenschreiben) – sonst überdeckten sie das Meer (schreibt keine Tiefe)
            _background.Add((vm, mat, model));
            return;
        }
        if (mat.Kind == MaterialKind.Skidmark)
        {
            // Unity: Queue "Transparent-60" -> vor allen anderen transparenten
            _transparent.Add((vm, mat, model, float.MaxValue));
            return;
        }
        if (mat.Kind == MaterialKind.Flare)
        {
            _transparent.Add((vm, mat, model, -1f));   // Unity: Queue "Transparent+20" -> nach allem
            return;
        }
        if (mat.Kind == MaterialKind.MapRoad)
        {
            // Unity: Queue "Overlay" -> ganz zuletzt; untereinander in Aufrufreihenfolge (Straßen, darüber die GPS-Route)
            _transparent.Add((vm, mat, model, -2f - _transparent.Count * 1e-4f));
            return;
        }
        if (mat.Kind == MaterialKind.Particle)
        {
            _transparent.Add((vm, mat, model, 0f));   // Staubwolke: zuletzt
            return;
        }
        if (mat.Kind == MaterialKind.CarShadow)
        {
            // Unity: Queue "Transparent-50" -> vor den übrigen transparenten Objekten, direkt auf dem Boden
            _transparent.Add((vm, mat, model, float.MaxValue));
            return;
        }
        if (mat.Kind == MaterialKind.Transparent && mat.Additive)
        {
            // Checkpoint-Tore: keine Entfernungs-Sortierung nötig, dafür sortierbar und instanzierbar.
            _additive.Add((vm, mat, model));
            return;
        }
        if (mat.Kind is MaterialKind.Transparent or MaterialKind.SpriteGlow or MaterialKind.CarGlass or MaterialKind.Lightning)
        {
            var p = new Vector3(model.M41, model.M42, model.M43);
            _transparent.Add((vm, mat, model, Vector3.DistanceSquared(p, _globals.CameraPosition)));
            return;
        }
        if (mat.Kind == MaterialKind.Avatar && mat.Blend)
        {
            // Unity TDU2/AvatarBlend: Queue "Transparent", ZWrite Off (Augenbrauen, Wimpern, Haar-Strähnen)
            var p = new Vector3(model.M41, model.M42, model.M43);
            _transparent.Add((vm, mat, model, Vector3.DistanceSquared(p, _globals.CameraPosition)));
            return;
        }
        _opaque.Add((vm, mat, model));   // gezeichnet in EndFrame (erst Schattenkarten, dann Bild)
    }

    public void DrawShadowOnly(IGpuMesh mesh, IGpuMaterial material, Matrix4x4 model)
    {
        if (_skipFrame || !_inFrame || mesh is not VulkanMesh vm || vm.IndexCount == 0 || material is not VulkanMaterial mat) return;
        if (!vm.IsObjectFormat || ShadowPipe(mat.Kind) == null) return;
        _shadowOnly.Add((vm, mat, model));
    }

    private static PipeId? ShadowPipe(MaterialKind k) => k switch
    {
        MaterialKind.Object => PipeId.ShadowObject,
        MaterialKind.Foliage => PipeId.ShadowFoliage,
        MaterialKind.Sprite => PipeId.ShadowSprite,
        MaterialKind.Terrain => PipeId.ShadowTerrain,
        MaterialKind.Car => PipeId.ShadowCar,      // TDU2/Car: fullforwardshadows addshadow
        MaterialKind.Avatar => PipeId.ShadowAvatar,   // TDU2/Avatar: addshadow (AvatarBlend wirft keinen Schatten)
        _ => null,   // Gras, Wasser, Transparent, Overlay: wie im Unity-Projekt ohne Schattenwurf
    };

    private void DrawShadow(VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model, PipeId id, Matrix4x4 lightVP)
        => Emit(vm, mat, model, id, true, lightVP, 0, 1);

    /// <summary>
    /// Kaskaden wie Unity (4 Stück, Aufteilung 6,7 % / 20 % / 46,7 % / 100 % der Schattendistanz): je Kaskade die
    /// Bounding-Sphere des Kamerafrustum-Abschnitts, orthografisch aus Sonnenrichtung, auf Texel gerastert (kein Flimmern).
    /// </summary>
    private void ComputeCascades()
    {
        float dist = MathF.Max(1f, _globals.ShadowDistance);
        float[] ends = { dist * 0.067f, dist * 0.2f, dist * 0.467f, dist };
        _shadowSplits = new Vector4(ends[0], ends[1], ends[2], ends[3]);
        if (!Matrix4x4.Invert(_view, out var camWorld)) camWorld = Matrix4x4.Identity;
        float tanY = MathF.Abs(_proj.M22) > 1e-6f ? 1f / MathF.Abs(_proj.M22) : 1f;
        float tanX = MathF.Abs(_proj.M11) > 1e-6f ? 1f / MathF.Abs(_proj.M11) : 1f;
        var sun = _globals.SunDirection.LengthSquared() > 1e-6f ? Vector3.Normalize(_globals.SunDirection) : Vector3.UnitY;
        var up = MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        float near = 0.5f;
        var tw = new float[ShadowCascades];
        Span<Vector3> c = stackalloc Vector3[8];
        for (int k = 0; k < ShadowCascades; k++)
        {
            float n = k == 0 ? near : ends[k - 1], f = ends[k];
            int ci = 0;
            foreach (float d in new[] { n, f })
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sx = -1; sx <= 1; sx += 2)
                        c[ci++] = Vector3.Transform(new Vector3(sx * tanX * d, sy * tanY * d, -d), camWorld);
            var center = Vector3.Zero;
            for (int i = 0; i < 8; i++) center += c[i];
            center /= 8f;
            float r = 0f;
            for (int i = 0; i < 8; i++) r = MathF.Max(r, Vector3.Distance(center, c[i]));
            r = MathF.Ceiling(r);                                  // stabile Größe
            const float back = 2000f;                              // Schattenwerfer bis 2 km Richtung Sonne
            var lightView = Matrix4x4.CreateLookAt(center + sun * back, center, up);
            var ortho = Matrix4x4.CreateOrthographicOffCenter(-r, r, -r, r, 0.5f, back + r + 500f);
            var vp = lightView * ortho;
            // auf Texel einrasten
            var o = Vector4.Transform(new Vector4(0, 0, 0, 1), vp);
            float half = ShadowSize * 0.5f;
            float ox = MathF.Round(o.X * half) - o.X * half, oy = MathF.Round(o.Y * half) - o.Y * half;
            ortho.M41 += ox / half; ortho.M42 += oy / half;
            _shadowVP[k] = lightView * ortho;
            _shadowSphere[k] = new Vector4(center, r);
            tw[k] = 2f * r / ShadowSize;
        }
        _shadowTexelWorld = new Vector4(tw[0], tw[1], tw[2], tw[3]);
    }

    // ------------------------------------------------------------------ Schatten: Aufzeichnung auf eigenem Thread
    // Die Schattenkaskaden werden in einen eigenen Command-Buffer (eigener Pool) auf einem Arbeits-Thread aufgezeichnet,
    // während der Hauptthread Spiegel/Sonde und das Bild aufzeichnet. Abgeschickt werden beide in EINEM Submit
    // (Schatten zuerst) – die Reihenfolge auf der GPU ist dieselbe wie vorher in einem Command-Buffer.
    // Der Arbeits-Thread hat eigenen Bindungs-/Lauf-Zustand und die obere Hälfte des Instanzpuffers.

    private sealed class ShadowRec
    {
        public CommandBuffer Cmd;
        public PipeId? Bound;
        public ulong LastSet, LastVb;
        public VulkanMesh? RMesh;
        public VulkanMaterial? RMat;
        public PipeId RPipe;
        public Matrix4x4 RFirst, RVP;
        public int RBase, RCount, InstUsed, Draws, Inst;
        public double Ms;
        public Exception? Error;
    }

    private const int InstHalf = InstCap / 2;
    private CommandPool _shadowPool;
    private readonly CommandBuffer[] _shadowCmds = new CommandBuffer[MaxFramesInFlight];
    private readonly ShadowRec _srec = new();
    private Thread? _shadowThread;
    private readonly SemaphoreSlim _shadowGo = new(0), _shadowDone = new(0);
    private volatile bool _shadowQuit;
    private bool _shadowEnabled, _shadowRunning;

    private void CreateShadowCommandBuffers()
    {
        var info = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _graphicsFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        if (_vk.CreateCommandPool(_device, in info, null, out _shadowPool) != Result.Success)
            throw new Exception("CommandPool (Shadow) could not be created.");
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _shadowPool, Level = CommandBufferLevel.Primary, CommandBufferCount = MaxFramesInFlight };
        fixed (CommandBuffer* p = _shadowCmds)
            if (_vk.AllocateCommandBuffers(_device, in alloc, p) != Result.Success)
                throw new Exception("CommandBuffers (shadows) could not be allocated.");
    }

    private void EnsureShadowThread()
    {
        if (_shadowThread != null) return;
        _shadowThread = new Thread(() =>
        {
            while (true)
            {
                _shadowGo.Wait();
                if (_shadowQuit) { _shadowDone.Release(); return; }
                try { RecordShadows(_srec); }
                catch (Exception ex) { _srec.Error = ex; }
                _shadowDone.Release();
            }
        })
        { IsBackground = true, Name = "Schatten-Aufzeichnung" };
        _shadowThread.Start();
    }

    private void StopShadowThread()
    {
        if (_shadowThread == null) return;
        if (_shadowRunning) { _shadowDone.Wait(); _shadowRunning = false; }
        _shadowQuit = true;
        _shadowGo.Release();
        _shadowDone.Wait();
        _shadowThread = null;
    }

    /// <summary>Hauptthread: Kaskaden berechnen und den Arbeits-Thread starten (cmd ist bereits begonnen).</summary>
    private void StartShadows(CommandBuffer cmd)
    {
        _shadowEnabled = _globals.ShadowStrength > 0f && _globals.ShadowDistance > 0f;
        if (_shadowEnabled) ComputeCascades();
        var r = _srec;
        r.Cmd = cmd; r.Bound = null; r.LastSet = 0; r.LastVb = 0; r.RMesh = null; r.RMat = null; r.RCount = 0;
        r.InstUsed = InstHalf; r.Draws = 0; r.Inst = 0; r.Ms = 0; r.Error = null;
        EnsureShadowThread();
        _shadowRunning = true;
        _shadowGo.Release();
    }

    /// <summary>Hauptthread: auf den Arbeits-Thread warten (vor dem Leeren von _opaque und vor dem Abschicken).</summary>
    private void JoinShadows()
    {
        if (!_shadowRunning) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _shadowDone.Wait();
        _shadowRunning = false;
        if (_srec.Error != null) throw new Exception("Shadow recording failed: " + _srec.Error.Message, _srec.Error);
        _cShadow = _srec.Draws;
        _cInst += _srec.Inst;
    }

    /// <summary>Arbeits-Thread: alle Kaskaden in r.Cmd aufzeichnen, Zeitstempel 1, Command-Buffer beenden.</summary>
    private void RecordShadows(ShadowRec r)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cmd = r.Cmd;
        bool enabled = _shadowEnabled;
        _shadowSorted.Clear();
        if (enabled)
        {
            foreach (var (mesh, mat, model) in _opaque)
            {
                var pid = ShadowPipe(mat.Kind);
                if (pid == null) continue;
                // Welt-Kugel einmal pro Bild statt einmal pro Kaskade
                var mc = Vector3.Transform(mesh.BCenter, model);
                float sx = new Vector3(model.M11, model.M12, model.M13).LengthSquared();
                float sy = new Vector3(model.M21, model.M22, model.M23).LengthSquared();
                float sz = new Vector3(model.M31, model.M32, model.M33).LengthSquared();
                float mr = mesh.BRadius * MathF.Sqrt(MathF.Max(sx, MathF.Max(sy, sz)));
                _shadowSorted.Add((mesh, mat, model, pid.Value, new Vector4(mc, mr)));
            }
            // _opaque ist bereits nach Pipeline/Material/Mesh sortiert -> Reihenfolge bleibt gruppiert, kein zweites Sortieren
        }
        var clear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var vp = new Viewport { X = 0, Y = 0, Width = ShadowSize, Height = ShadowSize, MinDepth = 0, MaxDepth = 1 };
        var sc = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(ShadowSize, ShadowSize) };
        for (int k = 0; k < ShadowCascades; k++)
        {
            var rp = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _shadowPass,
                Framebuffer = _shadowFramebuffers[k],
                RenderArea = sc,
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
            if (enabled)
            {
                _vk.CmdSetViewport(cmd, 0, 1, &vp);
                _vk.CmdSetScissor(cmd, 0, 1, &sc);
                r.Bound = null;
                var sph = _shadowSphere[k];
                var lvp = _shadowVP[k];
                var sunN = Vector3.Normalize(_globals.SunDirection);
                var sc3 = new Vector3(sph.X, sph.Y, sph.Z);
                foreach (var (mesh, mat, model, pipe, ws) in _shadowSorted)
                {
                    // grob: Mesh-Kugel gegen Kaskaden-Zylinder entlang der Sonnenrichtung
                    float rr = sph.W + ws.W;
                    var dv = new Vector3(ws.X, ws.Y, ws.Z) - sc3;
                    float along = Vector3.Dot(dv, sunN);
                    if (along < -rr) continue;   // unter/hinter der Kaskade (vom Licht aus)
                    var perp = dv - along * sunN;
                    if (perp.LengthSquared() > rr * rr) continue;
                    SBatch(r, mesh, mat, model, pipe, lvp);
                }
                foreach (var (mesh, mat, model) in _shadowOnly)
                    SBatch(r, mesh, mat, model, ShadowPipe(mat.Kind)!.Value, lvp);
                SFlush(r);
            }
            _vk.CmdEndRenderPass(cmd);
        }
        Ts(cmd, 1);
        if (_vk.EndCommandBuffer(cmd) != Result.Success) throw new Exception("EndCommandBuffer (Shadow) failed.");
        r.Ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void SEmit(ShadowRec r, VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model, PipeId id, Matrix4x4 viewProj, int firstInstance, int count)
    {
        var cmd = r.Cmd;
        var layout = _layouts[(int)id];
        if (r.Bound != id)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines[(int)id]);
            var fs = _frameSets[_currentFrame];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 1, 1, &fs, 0, null);
            r.Bound = id;
            r.LastSet = 0; r.LastVb = 0;
        }
        r.Draws++;
        var push = new PushConstants { ViewProj = viewProj, Model = model };
        _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
        var set = mat.Set;
        if (set.Handle != r.LastSet) { _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null); r.LastSet = set.Handle; }
        if (vm.VertexBuffer.Handle != r.LastVb)
        {
            var vbuf = vm.VertexBuffer;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, &vbuf, &offset);
            _vk.CmdBindIndexBuffer(cmd, vm.IndexBuffer, 0, IndexType.Uint32);
            r.LastVb = vm.VertexBuffer.Handle;
        }
        _vk.CmdDrawIndexed(cmd, (uint)vm.IndexCount, (uint)count, 0, 0, (uint)firstInstance);
    }

    private void SBatch(ShadowRec r, VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model, PipeId id, Matrix4x4 viewProj)
    {
        bool same = r.RCount > 0 && ReferenceEquals(vm, r.RMesh) && ReferenceEquals(mat, r.RMat) && id == r.RPipe && viewProj == r.RVP;
        var inst = (Matrix4x4*)_instMem[_currentFrame].Mapped;
        if (same && inst != null)
        {
            if (r.RCount == 1 && r.InstUsed + 2 <= InstCap)
            {
                r.RBase = r.InstUsed;
                inst[r.InstUsed++] = r.RFirst;
                inst[r.InstUsed++] = model;
                r.RCount = 2;
                return;
            }
            if (r.RCount > 1 && r.InstUsed < InstCap)
            {
                inst[r.InstUsed++] = model;
                r.RCount++;
                return;
            }
        }
        SFlush(r);
        if (!Instanceable(id)) { SEmit(r, vm, mat, model, id, viewProj, 0, 1); return; }
        r.RMesh = vm; r.RMat = mat; r.RPipe = id; r.RVP = viewProj; r.RFirst = model; r.RCount = 1;
    }

    private void SFlush(ShadowRec r)
    {
        if (r.RCount == 0) return;
        if (r.RCount == 1) SEmit(r, r.RMesh!, r.RMat!, r.RFirst, r.RPipe, r.RVP, 0, 1);
        else { SEmit(r, r.RMesh!, r.RMat!, default, r.RPipe, r.RVP, r.RBase, r.RCount); r.Inst += r.RCount; }
        r.RCount = 0; r.RMesh = null; r.RMat = null;
    }

    private void DrawSky(CommandBuffer cmd) => DrawSky(cmd, _view, _proj);

    private void DrawSky(CommandBuffer cmd, Matrix4x4 view, Matrix4x4 proj)
    {
        var rot = view; rot.M41 = 0; rot.M42 = 0; rot.M43 = 0;
        if (!Matrix4x4.Invert(rot * proj, out var inv)) return;
        var layout = _layouts[(int)PipeId.Sky];
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines[(int)PipeId.Sky]);
        var fs = _activeFrameSet;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 1, 1, &fs, 0, null);
        var push = new PushConstants { ViewProj = inv, Model = Matrix4x4.Identity };
        _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _boundPipeline = PipeId.Sky;
        _lastSet = default; _lastVb = default;
    }

    private static PipeId PipeFor(VulkanMaterial mat) => mat.Kind switch
    {
        MaterialKind.Terrain => PipeId.Terrain,
        MaterialKind.Object => PipeId.Object,
        MaterialKind.Transparent => mat.Additive ? PipeId.TransparentAdd : mat.Glow ? PipeId.TransparentAlphaGlow : PipeId.TransparentAlpha,
        MaterialKind.Foliage => PipeId.Foliage,
        MaterialKind.Sprite => PipeId.Sprite,
        MaterialKind.SpriteGlow => mat.Additive ? PipeId.GlowAdd : PipeId.GlowAlpha,
        MaterialKind.Overlay => PipeId.Overlay,
        MaterialKind.Grass => PipeId.Grass,
        MaterialKind.Water => PipeId.Water,
        MaterialKind.Car => PipeId.Car,
        MaterialKind.CarGlass => PipeId.CarGlass,
        MaterialKind.CarShadow => PipeId.CarShadow,
        MaterialKind.Skidmark => PipeId.Skidmark,
        MaterialKind.Cloud => PipeId.Cloud,
        MaterialKind.SkyStars => PipeId.SkyStars,
        MaterialKind.SkyMoon => PipeId.SkyMoon,
        MaterialKind.Lightning => PipeId.Lightning,
        MaterialKind.Avatar => mat.Blend ? PipeId.AvatarBlend : PipeId.Avatar,
        MaterialKind.Particle => mat.Additive ? PipeId.ParticleAdd : PipeId.Particle,
        MaterialKind.Flare => PipeId.Flare,
        MaterialKind.MapTerrain => PipeId.MapTerrain,
        MaterialKind.MapRoad => PipeId.MapRoad,
        _ => PipeId.Simple,
    };

    private void DrawNow(VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model)
        => Emit(vm, mat, model, PipeFor(mat), false, _viewProj, 0, 1);

    /// <summary>Ein Zeichenaufruf (count Instanzen ab firstInstance im Instanzpuffer, sonst die Modellmatrix als Push-Constant).</summary>
    private void Emit(VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model, PipeId id, bool shadow, Matrix4x4 viewProj, int firstInstance, int count, bool? modelFlip = null)
    {
        var cmd = _commandBuffers[_currentFrame];
        var layout = _layouts[(int)id];
        // Cull-Modus wie im Original: Material nicht beidseitig -> Rückseiten weg; gespiegelter Durchgang (Bodenspiegel, Sonde)
        // oder gespiegelte Modellmatrix kehrt die Wicklung um (Exe-Tabelle {2,3,2,1 | 3,2,3,1}, Index Modus + Invert*4).
        int cull = 0;
        if (!shadow && !mat.TwoSided && _cullPipes[(int)id].Handle != 0)
            cull = (Det3(viewProj) > 0f) ^ (modelFlip ?? (count == 1 && Det3(model) < 0f)) ? 2 : 1;
        if (_boundPipeline != id || _boundCull != cull)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, cull == 0 ? _pipelines[(int)id] : cull == 1 ? _cullPipes[(int)id] : _cullPipesInv[(int)id]);
            _boundCull = cull;
            var fs = shadow ? _frameSets[_currentFrame] : _activeFrameSet;   // Schatten- bzw. Bild-/Spiegel-Durchgang
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 1, 1, &fs, 0, null);
            _boundPipeline = id;
            _lastSet = default; _lastVb = default;
            if (!shadow) _cBinds++;
        }
        if (shadow) _cShadow++;
        else { _cDraw++; _cTris += vm.IndexCount / 3 * count; }

        var push = new PushConstants { ViewProj = viewProj, Model = model };
        _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
        // gleiche Textur-/Materialbindung bzw. gleiches Mesh wie der vorige Aufruf: nicht neu binden (spart CPU beim Aufzeichnen)
        var set = mat.Set;
        if (set.Handle != _lastSet.Handle) { _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null); _lastSet = set; }
        if (vm.VertexBuffer.Handle != _lastVb.Handle)
        {
            var vbuf = vm.VertexBuffer;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, &vbuf, &offset);
            _vk.CmdBindIndexBuffer(cmd, vm.IndexBuffer, 0, IndexType.Uint32);
            _lastVb = vm.VertexBuffer;
        }
        _vk.CmdDrawIndexed(cmd, (uint)vm.IndexCount, (uint)count, 0, 0, (uint)firstInstance);
    }

    // ------------------------------------------------------------------ Instancing
    // Aufeinanderfolgende Aufrufe mit gleichem Mesh, Material und Pipeline (Objekte, Vegetation, Sprites und ihre Schatten)
    // werden zu einem Instanz-Aufruf zusammengefasst; die Modellmatrizen liegen im Instanzpuffer des Bildes.
    /// <summary>Determinante des 3x3-Anteils (Vorzeichen = Händigkeit; Hauptbild: View x Projektion &lt; 0, gespiegelt &gt; 0).</summary>
    private static float Det3(in Matrix4x4 m)
        => m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    private static bool Instanceable(PipeId id) => id is PipeId.Object or PipeId.Foliage or PipeId.Sprite
        or PipeId.ShadowObject or PipeId.ShadowFoliage or PipeId.ShadowSprite
        or PipeId.TransparentAdd;   // additives Overlay: kommutativ, kein ZWrite -> Reihenfolge egal

    private void Batch(VulkanMesh vm, VulkanMaterial mat, Matrix4x4 model, PipeId id, bool shadow, Matrix4x4 viewProj)
    {
        bool flip = !mat.TwoSided && Det3(model) < 0f;   // gespiegelte Instanz: eigener Lauf (andere Cull-Richtung)
        bool same = _rCount > 0 && ReferenceEquals(vm, _rMesh) && ReferenceEquals(mat, _rMat) && id == _rPipe && shadow == _rShadow && viewProj == _rVP && flip == _rFlip;
        var inst = (Matrix4x4*)_instMem[_currentFrame].Mapped;
        if (same && inst != null)
        {
            if (_rCount == 1 && _instUsed + 2 <= InstHalf)
            {
                _rBase = _instUsed;
                inst[_instUsed++] = _rFirst;
                inst[_instUsed++] = model;
                _rCount = 2;
                return;
            }
            if (_rCount > 1 && _instUsed < InstHalf)
            {
                inst[_instUsed++] = model;
                _rCount++;
                return;
            }
        }
        FlushBatch();
        if (!Instanceable(id)) { Emit(vm, mat, model, id, shadow, viewProj, 0, 1); return; }
        _rMesh = vm; _rMat = mat; _rPipe = id; _rShadow = shadow; _rVP = viewProj; _rFirst = model; _rCount = 1; _rFlip = flip;
    }

    /// <summary>Offenen Lauf zeichnen (vor jedem anderen Aufruf, Durchgangsende oder Pipeline-Wechsel von außen).</summary>
    private void FlushBatch()
    {
        if (_rCount == 0) return;
        if (_rCount == 1) Emit(_rMesh!, _rMat!, _rFirst, _rPipe, _rShadow, _rVP, 0, 1);
        else
        {
            // Modellmatrix nur Null: [3][3] == 0 -> Shader liest inst.m[gl_InstanceIndex]
            Emit(_rMesh!, _rMat!, default, _rPipe, _rShadow, _rVP, _rBase, _rCount, _rFlip);
            _cInst += _rCount;
        }
        _rCount = 0; _rMesh = null; _rMat = null;
    }

    public void EndFrame()
    {
        if (_skipFrame) { _inFrame = false; return; }
        var cmd0 = _commandBuffers[_currentFrame];
        long recStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // 1) Schattenkarten (Kaskaden, eigener Thread + Command-Buffer), 2) Bild: Himmel, opake Objekte, transparente, Overlay
        // Texturkopien und Zeitstempel-Start gehen in den Schatten-Command-Buffer: er wird als erster abgeschickt.
        var cmdS = _shadowCmds[_currentFrame];
        _vk.ResetCommandBuffer(cmdS, 0);
        var beginS = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (_vk.BeginCommandBuffer(cmdS, in beginS) != Result.Success) throw new Exception("BeginCommandBuffer (Schatten) fehlgeschlagen.");
        FlushTextureInits(cmdS);
        FlushTextureUpdates(cmdS);
        TsBegin(cmdS);
        long tcs = System.Diagnostics.Stopwatch.GetTimestamp();
        SortOpaque();   // gleiche Mesh+Material-Paare hintereinander -> Instanz-Aufrufe (Schatten, Spiegel, Bild)
        _cInst = 0;
        StartShadows(cmdS);   // ab hier zeichnet der Arbeits-Thread die Kaskaden auf (liest _opaque nur)
        WriteFrameUbo();
        _activeFrameSet = _frameSets[_currentFrame];
        tcs = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderEnvironment(cmd0);   // Bodenspiegelung / Reflexionssonde (Unity InteriorMirror, ReflectionProbe)
        Ts(cmd0, 2);
        WriteFrameUbo();           // Kennungen Planar/Probe für den Bild-Durchgang
        var clears = stackalloc ClearValue[3];
        clears[0] = _globals.NoSky
            ? new ClearValue(new ClearColorValue(ToLin(_globals.BackgroundColor.X), ToLin(_globals.BackgroundColor.Y), ToLin(_globals.BackgroundColor.Z), 0f))   // Unity SolidColor (große Karte: Meer)
            : new ClearValue(new ClearColorValue(ToLin(ClearColor[0]), ToLin(ClearColor[1]), ToLin(ClearColor[2]), 0f));   // Alpha 0 = Bloom-Maske leer   // Kamera-Hintergrund (Unity: linear)
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(0f, 0) };   // Reversed-Z
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[_imageIndex],
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent },
            ClearValueCount = Msaa ? 3u : 2u,
            PClearValues = clears
        };
        _vk.CmdBeginRenderPass(cmd0, in rpBegin, SubpassContents.Inline);
        var viewport = new Viewport { X = 0, Y = 0, Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
        _vk.CmdSetViewport(cmd0, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent };
        _vk.CmdSetScissor(cmd0, 0, 1, &scissor);

        _boundPipeline = null;
        if (!_globals.NoSky) DrawSky(cmd0);
        if (!_globals.NoSky) foreach (var (mesh, mat, model) in _background) DrawNow(mesh, mat, model);
        _background.Clear();
        foreach (var (mesh, mat, model) in _opaque) Batch(mesh, mat, model, PipeFor(mat), false, _viewProj);
        FlushBatch();
        JoinShadows();   // Schatten-Thread fertig, bevor _opaque geleert wird
        _opaque.Clear();

        // Additive Checkpoint-Overlays: wie opake Objekte sortieren/instanzieren statt einzeln DrawNow.
        // Reihenfolge ist bei SrcAlpha/One ohne ZWrite irrelevant; das spart beim Rennen mehrere ms CPU pro Bild.
        if (_additive.Count > 0)
        {
            SortAdditive();
            foreach (var (mesh, mat, model) in _additive) Batch(mesh, mat, model, PipeFor(mat), false, _viewProj);
            FlushBatch();
            _cTransp += _additive.Count;
            _cAdditive = _additive.Count;
            _additive.Clear();
        }

        // Transparente Objekte hinten→vorne (Unity: Queue "Transparent")
        if (_transparent.Count > 0)
        {
            _transparent.Sort((a, b) => b.dist.CompareTo(a.dist));
            foreach (var (mesh, mat, model, _) in _transparent) DrawNow(mesh, mat, model);
            _cTransp = _transparent.Count;
            _transparent.Clear();
        }
        Ts(cmd0, 3);
        // Overlay (Debug-UI): Pixel -> NDC, ohne Tiefentest
        void DrawOverlay()
        {
            if (_overlay.Count == 0 || _extent.Width == 0 || _extent.Height == 0) return;
            var keep = _viewProj;
            _viewProj = new Matrix4x4(2f / _extent.Width, 0, 0, 0, 0, 2f / _extent.Height, 0, 0, 0, 0, 1, 0, -1, -1, 0, 1);
            foreach (var (mesh, mat) in _overlay) { DrawNow(mesh, mat, Matrix4x4.Identity); mesh.Dispose(); }
            _cOverlay = _overlay.Count;
            _overlay.Clear();
            _viewProj = keep;
        }
        bool uiPassUsed = _uiPass.Handle != 0 && _imageIndex < _uiFramebuffers.Length;
        if (uiPassUsed)
        {
            // Flash wie Unity-IMGUI (Color Space Linear): eigener Durchgang auf der UNORM-Sicht, Mischen ohne sRGB-Umrechnung.
            // Das Debug-Overlay bleibt im Welt-Durchgang (liegt damit unter der Flash-Oberfläche).
            DrawOverlay();
            _vk.CmdEndRenderPass(cmd0);
            if (Msaa) CopyResolveToSwapchain(cmd0, true);
            RecordSceneCapture(cmd0);   // Weltbild auf die CPU (Foto), vor Photoshop/Flash (VulkanRendererCapture)
            bool photoshop = RecordPhotoshop(cmd0);   // Nachbearbeitung wie das Original vor dem HUD (VulkanRendererPostFx)
            var uiBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _uiPass,
                Framebuffer = _uiFramebuffers[_imageIndex],
                RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent }
            };
            _vk.CmdBeginRenderPass(cmd0, in uiBegin, SubpassContents.Inline);
            _vk.CmdSetViewport(cmd0, 0, 1, &viewport);
            _vk.CmdSetScissor(cmd0, 0, 1, &scissor);
            if (photoshop) DrawPhotoshopApply(cmd0);   // ganzes Bild = LUT(Weltbild), Flash darüber
            _boundPipeline = null;
            _uiActive = true;
            DrawFlashBatches();
            DrawOverlayTopList(PipeId.OverlayUi);   // Entwicklermenü über HUD/Minimap
            _uiActive = false;
        }
        else
        {
            RejectSceneCaptureWithoutUi();
            // Flash-Oberfläche (Menüs/HUD) über der Welt, unter dem Debug-Overlay
            DrawFlashBatches();
            DrawOverlay();
            DrawOverlayTopList(PipeId.Overlay);
        }
        foreach (var m in _dynamicMeshes) m.Dispose();   // wie Overlay: Freigabe verzögert bis die GPU fertig ist
        _dynamicMeshes.Clear();
        _inFrame = false;
        _stats.DrawCalls = _cDraw; _stats.Triangles = _cTris; _stats.PipelineBinds = _cBinds;
        _stats.TransparentDraws = _cTransp; _stats.OverlayDraws = _cOverlay; _stats.ShadowDraws = _cShadow;
        _stats.GpuMemoryAllocated = _alloc.AllocatedBytes;
        _stats.Width = (int)_extent.Width; _stats.Height = (int)_extent.Height;
        int nt = 0, nm = 0, nme = 0;
        foreach (var o in _live) { if (o is VulkanTexture) nt++; else if (o is VulkanMaterial) nm++; else if (o is VulkanMesh) nme++; }
        _stats.Textures = nt; _stats.Materials = nm; _stats.Meshes = nme;

        var cmd = _commandBuffers[_currentFrame];
        _vk.CmdEndRenderPass(cmd);
        if (Msaa && !uiPassUsed) CopyResolveToSwapchain(cmd, false);
        Ts(cmd, 4);
        if (_vk.EndCommandBuffer(cmd) != Result.Success)
            throw new Exception("EndCommandBuffer failed.");

        var waitSem = _imageAvailable[_currentFrame];
        var sigSem = _renderFinished[_currentFrame];
        // MSAA: Schatten, Spiegel und Szene gehen in eigene Ziele; erst die Kopie ins Swapchain-Bild muss auf das Bild warten.
        // Ohne MSAA zeichnet die Szene direkt ins Swapchain-Bild -> Farbausgabe muss warten.
        var waitStage = Msaa ? PipelineStageFlags.TransferBit : PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit;
        var submitCmds = stackalloc CommandBuffer[2] { _shadowCmds[_currentFrame], cmd };   // Schatten (+Texturkopien) zuerst
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSem,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 2,
            PCommandBuffers = submitCmds,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &sigSem
        };
        fixed (Fence* pFence = &_inFlight[_currentFrame])
            _vk.ResetFences(_device, 1, pFence);
        if (_vk.QueueSubmit(_graphicsQueue, 1, in submit, _inFlight[_currentFrame]) != Result.Success)
            throw new Exception("QueueSubmit failed.");

        var swapchain = _swapchain;
        uint imageIndex = _imageIndex;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &sigSem,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };
        Result pr = _khrSwapchain.QueuePresent(_presentQueue, in present);
        if (pr == Result.ErrorOutOfDateKhr || pr == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }
        else if (pr == Result.Success && GraphicsSettingsChanged()) ApplyGraphicsSettings();
        else if (pr != Result.Success)
            throw new Exception($"QueuePresent failed: {pr}");

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        _frameCounter++;
    }


    SampleCountFlags WantedMsaa()
    {
        int want = GraphicsSettings.Msaa;
        foreach (var (n, f) in new[] { (8, SampleCountFlags.Count8Bit), (4, SampleCountFlags.Count4Bit), (2, SampleCountFlags.Count2Bit) })
            if (want >= n && (_msaaCounts & f) != 0) return f;
        return SampleCountFlags.Count1Bit;
    }

    // Unity (Built-in): die Schattenauflösung gilt für EINE Schattenkarte, in der bei 4 Kaskaden alle vier als 2x2-Atlas liegen –
    // jede Kaskade hat also die halbe Kantenlänge (VeryHigh 4096 -> 2048 je Kaskade). Vorher: 4 volle 4096er = 4-fache Fläche.
    static uint WantedShadowSize() => (uint)System.Math.Clamp(GraphicsSettings.ShadowResolution, 256, 4096) / (ShadowCascades == 4 ? 2u : 1u);

    bool GraphicsSettingsChanged()
        => WantedMsaa() != _msaa || GraphicsSettings.VSync != _vsync || WantedShadowSize() != ShadowSize
           || System.Math.Clamp(GraphicsSettings.TextureMipLimit, 0, 3) != _texMipLimit;

    void TakeGraphicsSettings()
    {
        _msaa = WantedMsaa();
        _vsync = GraphicsSettings.VSync;
        ShadowSize = WantedShadowSize();
        _texMipLimit = System.Math.Clamp(GraphicsSettings.TextureMipLimit, 0, 3);
        _stats.VSync = _vsync;
    }

    /// <summary>Geänderte Einstellungen übernehmen: Schattenkarten, Swapchain, Render-Pass und Pipelines neu (nach dem Präsentieren).</summary>
    void ApplyGraphicsSettings()
    {
        var oldMsaa = _msaa; bool oldVsync = _vsync; uint oldShadow = ShadowSize; int oldTex = _texMipLimit;
        TakeGraphicsSettings();
        bool rebuild = oldMsaa != _msaa || oldVsync != _vsync || oldShadow != ShadowSize;
        if (!rebuild) return;
        _vk.DeviceWaitIdle(_device);
        if (oldShadow != ShadowSize)
        {
            DestroyShadowResources();
            CreateShadowResources();
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                var ii = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _shadowArrayView, Sampler = _shadowSampler };
                var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &ii };
                _vk.UpdateDescriptorSets(_device, 1, &w, 0, null);
            }
            WriteEnvDescriptors();   // Spiegel-Durchgänge lesen dieselbe Schattenkarte
        }
        RecreateSwapchain();
    }

    private void DeferDestroy(Action destroy)
    {
        if (_disposed) return;
        _pendingDestroy.Add((_frameCounter + MaxFramesInFlight + 1, destroy));
    }

    private void RunPendingDestroys(long frame)
    {
        for (int i = _pendingDestroy.Count - 1; i >= 0; i--)
        {
            if (_pendingDestroy[i].frame > frame) continue;
            try { _pendingDestroy[i].destroy(); } catch (Exception ex) { Log.Error("GPU Sharing:" + ex.Message); }
            _pendingDestroy.RemoveAt(i);
        }
    }

    private void CreateInstance()
    {
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Paradise Engine"),
            ApplicationVersion = new Version32(0, 1, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("ParadiseEngine"),
            EngineVersion = new Version32(0, 1, 0),
            ApiVersion = Vk.Version12
        };
        var extensions = new List<string> { KhrSurface.ExtensionName, "VK_KHR_win32_surface" };
        var layers = new List<string>();
        if (_validation && ValidationLayerAvailable("VK_LAYER_KHRONOS_validation"))
            layers.Add("VK_LAYER_KHRONOS_validation");
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = layers.Count > 0 ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null
        };
        if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
            throw new Exception("Unable to create the Vulkan instance (driver/loader present?).");
        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new Exception("VK_KHR_surface is not available.");
        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
    }

    private bool ValidationLayerAvailable(string name)
    {
        uint count = 0;
        _vk.EnumerateInstanceLayerProperties(ref count, null);
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props) _vk.EnumerateInstanceLayerProperties(ref count, p);
        foreach (var lp in props)
            if (Marshal.PtrToStringAnsi((nint)lp.LayerName) == name) return true;
        return false;
    }

    private void CreateSurface()
    {
        if (!_vk.TryGetInstanceExtension(_instance, out KhrWin32Surface win32))
            throw new Exception("VK_KHR_win32_surface is not available.");
        var info = new Win32SurfaceCreateInfoKHR { SType = StructureType.Win32SurfaceCreateInfoKhr, Hinstance = _hinstance, Hwnd = _hwnd };
        if (win32.CreateWin32Surface(_instance, in info, null, out _surface) != Result.Success)
            throw new Exception("Win32-Surface could not be created.");
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0) throw new Exception("No Vulkan-compatible GPU found.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) _vk.EnumeratePhysicalDevices(_instance, ref count, p);

        // Diskrete GPU bevorzugen
        PhysicalDevice? best = null; int bestScore = -1;
        foreach (var dev in devices)
        {
            if (!TryFindQueueFamilies(dev, out _, out _) || !SwapchainAdequate(dev)) continue;
            _vk.GetPhysicalDeviceProperties(dev, out var pr);
            int score = pr.DeviceType == PhysicalDeviceType.DiscreteGpu ? 2 : pr.DeviceType == PhysicalDeviceType.IntegratedGpu ? 1 : 0;
            if (score > bestScore) { best = dev; bestScore = score; }
        }
        if (best == null) throw new Exception("No suitable GPU (graphics + present + swapchain) found.");
        _physical = best.Value;
        TryFindQueueFamilies(_physical, out _graphicsFamily, out _presentFamily);
        _vk.GetPhysicalDeviceProperties(_physical, out var props);
        DeviceName = Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "Vulkan";
        _stats.DeviceName = DeviceName;
        _stats.DeviceType = props.DeviceType.ToString();
        _stats.Api = $"Vulkan {props.ApiVersion >> 22}.{(props.ApiVersion >> 12) & 0x3FF}.{props.ApiVersion & 0xFFF}";
        _stats.Driver = props.VendorID == 0x10DE
            ? $"{props.DriverVersion >> 22}.{(props.DriverVersion >> 14) & 0xFF}"          // NVIDIA-Schema
            : $"{props.DriverVersion >> 22}.{(props.DriverVersion >> 12) & 0x3FF}.{props.DriverVersion & 0xFFF}";
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memProps);
        long vram = 0;
        for (int hi = 0; hi < memProps.MemoryHeapCount; hi++)
            if (memProps.MemoryHeaps[hi].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit)) vram = System.Math.Max(vram, (long)memProps.MemoryHeaps[hi].Size);
        _stats.VramTotal = vram;
        _vk.GetPhysicalDeviceFeatures(_physical, out var feats);
        _anisotropy = feats.SamplerAnisotropy;
        _maxAniso = System.Math.Min(4f, props.Limits.MaxSamplerAnisotropy);   // wie Unity anisoLevel = 4
        _msaaCounts = props.Limits.FramebufferColorSampleCounts & props.Limits.FramebufferDepthSampleCounts;
        if (!feats.TextureCompressionBC) Log.Warn("GPU does not report BC texture compression—textures may be corrupted.");
    }

    private bool TryFindQueueFamilies(PhysicalDevice dev, out uint graphics, out uint present)
    {
        graphics = uint.MaxValue; present = uint.MaxValue;
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families) _vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, p);
        for (uint i = 0; i < count; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit)) graphics = i;
            _khrSurface.GetPhysicalDeviceSurfaceSupport(dev, i, _surface, out Bool32 supported);
            if (supported) present = i;
            if (graphics != uint.MaxValue && present != uint.MaxValue) return true;
        }
        return graphics != uint.MaxValue && present != uint.MaxValue;
    }

    private bool HasDeviceExtension(PhysicalDevice dev, string name)
    {
        uint extCount = 0;
        _vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, null);
        var exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = exts) _vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, p);
        foreach (var e in exts) if (Marshal.PtrToStringAnsi((nint)e.ExtensionName) == name) return true;
        return false;
    }

    private bool SwapchainAdequate(PhysicalDevice dev)
    {
        uint extCount = 0;
        _vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, null);
        var exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = exts) _vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, p);
        bool hasSwap = false;
        foreach (var e in exts)
            if (Marshal.PtrToStringAnsi((nint)e.ExtensionName) == KhrSwapchain.ExtensionName) { hasSwap = true; break; }
        if (!hasSwap) return false;
        uint fmtCount = 0, pmCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(dev, _surface, ref fmtCount, null);
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(dev, _surface, ref pmCount, null);
        return fmtCount > 0 && pmCount > 0;
    }

    private void CreateLogicalDevice()
    {
        var unique = _graphicsFamily == _presentFamily ? new[] { _graphicsFamily } : new[] { _graphicsFamily, _presentFamily };
        float priority = 1f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[unique.Length];
        for (int i = 0; i < unique.Length; i++)
            queueInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = unique[i],
                QueueCount = 1,
                PQueuePriorities = &priority
            };
        _vk.GetPhysicalDeviceFeatures(_physical, out var supported);
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = supported.SamplerAnisotropy,
            TextureCompressionBC = supported.TextureCompressionBC,
        };
        var extList = new List<string> { KhrSwapchain.ExtensionName };
        _mutableSwap = HasDeviceExtension(_physical, MutableFormatExt);
        if (_mutableSwap) extList.Add(MutableFormatExt);
        var deviceExts = extList.ToArray();
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)unique.Length,
            PQueueCreateInfos = queueInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)deviceExts.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExts)
        };
        if (_vk.CreateDevice(_physical, in createInfo, null, out _device) != Result.Success)
            throw new Exception("The logical device could not be created.");
        _vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new Exception("VK_KHR_swapchain is not available.");
    }

    // ------------------------------------------------------------------ Swapchain

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physical, _surface, out var caps);
        uint fmtCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physical, _surface, ref fmtCount, null);
        var formats = new SurfaceFormatKHR[fmtCount];
        fixed (SurfaceFormatKHR* p = formats) _khrSurface.GetPhysicalDeviceSurfaceFormats(_physical, _surface, ref fmtCount, p);
        uint pmCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, ref pmCount, null);
        var modes = new PresentModeKHR[pmCount];
        fixed (PresentModeKHR* p = modes) _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, ref pmCount, p);

        var surfaceFormat = ChooseFormat(formats);
        var presentMode = ChoosePresentMode(modes, _vsync);
        _extent = ChooseExtent(caps);
        _format = surfaceFormat.Format;

        _swapTransferSrc = (caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount) imageCount = caps.MaxImageCount;
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            // TransferSrc: Photoshop-Nachbearbeitung kopiert ohne MSAA das fertige Weltbild heraus (VulkanRendererPostFx)
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ((caps.SupportedUsageFlags & ImageUsageFlags.TransferDstBit) != 0 ? ImageUsageFlags.TransferDstBit : 0)
                       | (_swapTransferSrc ? ImageUsageFlags.TransferSrcBit : 0),
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };
        var indices = stackalloc uint[] { _graphicsFamily, _presentFamily };
        if (_graphicsFamily != _presentFamily)
        {
            info.ImageSharingMode = SharingMode.Concurrent;
            info.QueueFamilyIndexCount = 2;
            info.PQueueFamilyIndices = indices;
        }
        else info.ImageSharingMode = SharingMode.Exclusive;
        _uiFormat = _mutableSwap ? UnormOf(_format) : Format.Undefined;
        var viewFormats = stackalloc Format[2] { _format, _uiFormat };
        var fmtList = new ImageFormatListCreateInfo { SType = StructureType.ImageFormatListCreateInfo, ViewFormatCount = 2, PViewFormats = viewFormats };
        if (_uiFormat != Format.Undefined)
        {
            info.Flags = SwapchainCreateFlagsKHR.MutableFormatBitKhr;
            info.PNext = &fmtList;
        }
        if (_khrSwapchain.CreateSwapchain(_device, in info, null, out _swapchain) != Result.Success)
            throw new Exception("The swap chain could not be created.");
        uint cnt = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref cnt, null);
        _images = new Image[cnt];
        fixed (Image* p = _images) _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref cnt, p);
    }

    /// <summary>
    /// sRGB-Ausgabe wie das Unity-Projekt (Color Space Linear): die Shader rechnen linear, die Swapchain wandelt nach sRGB.
    /// </summary>
    private static SurfaceFormatKHR ChooseFormat(SurfaceFormatKHR[] formats)
    {
        foreach (var f in formats)
            if (f.Format == Format.B8G8R8A8Srgb && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr) return f;
        foreach (var f in formats)
            if (f.Format == Format.R8G8B8A8Srgb) return f;
        foreach (var f in formats)
            if (f.Format == Format.B8G8R8A8Unorm && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr) return f;
        return formats[0];
    }

    /// <summary>VSync an: FIFO (an die Bildwiederholrate gebunden, wie Unity vSyncCount 1). Aus: Mailbox (ohne Zerreißen), sonst Immediate.</summary>
    private static PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes, bool vsync)
    {
        if (vsync) return PresentModeKHR.FifoKhr;
        foreach (var m in modes) if (m == PresentModeKHR.MailboxKhr) return m;
        foreach (var m in modes) if (m == PresentModeKHR.ImmediateKhr) return m;
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps)
    {
        if (caps.CurrentExtent.Width != uint.MaxValue) return caps.CurrentExtent;
        return new Extent2D
        {
            Width = System.Math.Clamp((uint)_width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Height = System.Math.Clamp((uint)_height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height)
        };
    }

    private void CreateImageViews()
    {
        _imageViews = new ImageView[_images.Length];
        for (int i = 0; i < _images.Length; i++)
            _imageViews[i] = CreateImageView(_images[i], _format, ImageAspectFlags.ColorBit, 1);
        _uiViews = new ImageView[_uiFormat != Format.Undefined ? _images.Length : 0];
        for (int i = 0; i < _uiViews.Length; i++)
            _uiViews[i] = CreateImageView(_images[i], _uiFormat, ImageAspectFlags.ColorBit, 1);
    }

    // ------------------------------------------------------------------ Speicher / Bilder / Buffer

    private void CreateDepthResources()
    {
        _depthFormat = Format.D32Sfloat;
        // SampledBit: Tiefe wird nach dem Weltdurchgang von der AO-Nachbearbeitung gelesen (VulkanRendererPostFx, PostFx.AoEnabled)
        CreateImage(_extent.Width, _extent.Height, 1, _depthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, out _depthImage, out _depthMem, _msaa);
        _depthView = CreateImageView(_depthImage, _depthFormat, ImageAspectFlags.DepthBit, 1);
        if (Msaa)
        {
            // Mehrfach abgetastetes Farbbild; wird am Ende des Weltdurchgangs in das Swapchain-Bild aufgelöst
            CreateImage(_extent.Width, _extent.Height, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
                out _msaaImage, out _msaaMem, _msaa);
            _msaaView = CreateImageView(_msaaImage, _format, ImageAspectFlags.ColorBit, 1);
            CreateImage(_extent.Width, _extent.Height, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                out _resolveImage, out _resolveMem);
            _resolveView = CreateImageView(_resolveImage, _format, ImageAspectFlags.ColorBit, 1);
        }
    }

    /// <summary>MSAA: aufgelöstes Bild per Bytekopie ins Swapchain-Bild (danach Flash-Durchgang bzw. Präsentieren).</summary>
    private void CopyResolveToSwapchain(CommandBuffer cmd, bool toUi)
    {
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var src = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _resolveImage,
            SubresourceRange = range,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        var dst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _images[_imageIndex],
            SubresourceRange = range,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        var pre = stackalloc ImageMemoryBarrier[2] { src, dst };
        // Transfer im Quell-Bereich: verkettet mit dem Semaphor-Warten (Transfer), damit der Layoutwechsel erst nach dem Acquire passiert
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 2, pre);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(_extent.Width, _extent.Height, 1)
        };
        _vk.CmdCopyImage(cmd, _resolveImage, ImageLayout.TransferSrcOptimal, _images[_imageIndex], ImageLayout.TransferDstOptimal, 1, in region);
        var post = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _images[_imageIndex],
            SubresourceRange = range,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = toUi ? ImageLayout.ColorAttachmentOptimal : ImageLayout.PresentSrcKhr,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = toUi ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit : 0,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, toUi ? PipelineStageFlags.ColorAttachmentOutputBit : PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 0, null, 1, in post);
    }

    private void CreateImage(uint w, uint h, uint mips, Format format, ImageUsageFlags usage, out Image image, out GpuAllocator.Allocation mem,
                             SampleCountFlags samples = SampleCountFlags.Count1Bit)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(w, h, 1),
            MipLevels = mips,
            ArrayLayers = 1,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = samples,
            SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateImage(_device, in info, null, out image) != Result.Success)
            throw new Exception("The image could not be created.");
        _vk.GetImageMemoryRequirements(_device, image, out var req);
        mem = _alloc.Allocate(req, MemoryPropertyFlags.DeviceLocalBit, linear: false);
        _vk.BindImageMemory(_device, image, mem.Memory, mem.Offset);
    }

    private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect, uint mips)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange { AspectMask = aspect, BaseMipLevel = 0, LevelCount = mips, BaseArrayLayer = 0, LayerCount = 1 }
        };
        if (_vk.CreateImageView(_device, in info, null, out var view) != Result.Success)
            throw new Exception("Could not create ImageView.");
        return view;
    }

    /// <summary>Host-sichtbarer Buffer (Vertex/Index/Uniform/Staging) im Suballokator, dauerhaft gemappt.</summary>
    private void CreateHostBuffer(ulong size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out GpuAllocator.Allocation mem)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = System.Math.Max(size, 16UL), Usage = usage, SharingMode = SharingMode.Exclusive };
        if (_vk.CreateBuffer(_device, in info, null, out buffer) != Result.Success)
            throw new Exception("The buffer could not be created.");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var req);
        mem = _alloc.Allocate(req, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, linear: true);
        _vk.BindBufferMemory(_device, buffer, mem.Memory, mem.Offset);
    }

    private void FreeBuffer(Silk.NET.Vulkan.Buffer b, GpuAllocator.Allocation mem)
    {
        _vk.DestroyBuffer(_device, b, null);
        _alloc.Free(mem);
    }

    private CommandBuffer BeginSingleTimeCommands()
    {
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _commandPool, CommandBufferCount = 1 };
        _vk.AllocateCommandBuffers(_device, in alloc, out var cmd);
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, in begin);
        return cmd;
    }

    private void EndSingleTimeCommands(CommandBuffer cmd)
    {
        _vk.EndCommandBuffer(cmd);
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        _vk.QueueSubmit(_graphicsQueue, 1, in submit, default);
        _vk.QueueWaitIdle(_graphicsQueue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, in cmd);
    }

    // ------------------------------------------------------------------ Texturen

    // Unity-Projekt: Color Space = Linear (ProjectSettings m_ActiveColorSpace 1). Texturen sind sRGB (Texture2D linear = false),
    // die Grafikkarte wandelt beim Lesen nach linear, gerechnet wird linear, die Swapchain (sRGB) wandelt zurück.
    private static Format VkFormat(GpuTextureFormat f, bool linear) => f switch
    {
        GpuTextureFormat.Bc1 => linear ? Format.BC1RgbaUnormBlock : Format.BC1RgbaSrgbBlock,
        GpuTextureFormat.Bc3 => linear ? Format.BC3UnormBlock : Format.BC3SrgbBlock,
        _ => linear ? Format.R8G8B8A8Unorm : Format.R8G8B8A8Srgb,
    };

    /// <summary>Unity GammaToLinearSpace (sRGB-Kurve) – für Farben, die Unity per SetColor/Light.color/RenderSettings bekommt.</summary>
    private static float ToLin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static Vector3 ToLin(Vector3 c) => new(ToLin(c.X), ToLin(c.Y), ToLin(c.Z));
    private static Vector4 ToLin(Vector4 c) => new(ToLin(c.X), ToLin(c.Y), ToLin(c.Z), c.W);

    /// <summary>Byte-Größe eines Mip-Levels (BC: 4x4-Blöcke, RGBA8: 4 Byte/Pixel).</summary>
    public static int MipSize(GpuTextureFormat f, int w, int h) => f switch
    {
        GpuTextureFormat.Bc1 => System.Math.Max(1, (w + 3) / 4) * System.Math.Max(1, (h + 3) / 4) * 8,
        GpuTextureFormat.Bc3 => System.Math.Max(1, (w + 3) / 4) * System.Math.Max(1, (h + 3) / 4) * 16,
        _ => w * h * 4,
    };

    private readonly Dictionary<VulkanTexture, byte[]> _texUpdates = new();

    /// <summary>
    /// Inhalt einer Rgba8-Textur (nur Mip 0, gleiche Größe) ersetzen – ohne Warten: die Kopie läuft im nächsten Bild vor dem Zeichnen
    /// (je Bild im Flug ein eigener Staging-Puffer). Für häufig neu gezeichnete Texturen (Minimap).
    /// </summary>
    public void UpdateTexture(IGpuTexture texture, ReadOnlySpan<byte> rgba)
    {
        if (texture is not VulkanTexture vt || vt.Disposed || vt.Format != GpuTextureFormat.Rgba8) return;
        int size = vt.Width * vt.Height * 4;
        if (rgba.Length < size) return;
        if (!_texUpdates.TryGetValue(vt, out var buf) || buf.Length != size) _texUpdates[vt] = buf = new byte[size];
        rgba[..size].CopyTo(buf);
    }

    private void FlushTextureUpdates(CommandBuffer cmd)
    {
        if (_texUpdates.Count == 0) return;
        foreach (var (vt, data) in _texUpdates)
        {
            if (vt.Disposed) continue;
            if (vt.Staging == null)
            {
                vt.Staging = new Silk.NET.Vulkan.Buffer[MaxFramesInFlight];
                vt.StagingMem = new GpuAllocator.Allocation[MaxFramesInFlight];
                for (int i = 0; i < MaxFramesInFlight; i++) CreateHostBuffer((ulong)data.Length, BufferUsageFlags.TransferSrcBit, out vt.Staging[i], out vt.StagingMem[i]);
            }
            var mem = vt.StagingMem![_currentFrame];
            fixed (byte* src = data) System.Buffer.MemoryCopy(src, mem.Mapped, data.Length, data.Length);
            var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
            var toDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = vt.Image,
                SubresourceRange = range,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferWriteBit
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toDst);
            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)vt.Width, (uint)vt.Height, 1)
            };
            _vk.CmdCopyBufferToImage(cmd, vt.Staging![_currentFrame], vt.Image, ImageLayout.TransferDstOptimal, 1, in region);
            var toRead = toDst;
            toRead.OldLayout = ImageLayout.TransferDstOptimal; toRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            toRead.SrcAccessMask = AccessFlags.TransferWriteBit; toRead.DstAccessMask = AccessFlags.ShaderReadBit;
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in toRead);
        }
        _texUpdates.Clear();
    }

    public IGpuTexture CreateTexture(TextureUpload t)
    {
        int mips = 0, total = 0;
        for (int i = 0; i < System.Math.Max(1, t.Mips); i++)
        {
            int s = MipSize(t.Format, System.Math.Max(1, t.Width >> i), System.Math.Max(1, t.Height >> i));
            if (total + s > t.Blocks.Length) break;
            total += s; mips++;
        }
        if (mips == 0) throw new Exception($"Texture ‘{t.Name}’: Insufficient data.");

        // Texturqualität (Unity QualitySettings.globalTextureMipmapLimit): oberste Stufen weglassen – nur Texturen mit Mip-Kette,
        // nicht kleiner als 64 Pixel, nicht Flash (Deferred)
        int skip = 0, skipBytes = 0;
        if (_texMipLimit > 0 && !t.Deferred)
            while (skip < _texMipLimit && skip + 1 < mips && (t.Width >> (skip + 1)) >= 64 && (t.Height >> (skip + 1)) >= 64)
            {
                skipBytes += MipSize(t.Format, System.Math.Max(1, t.Width >> skip), System.Math.Max(1, t.Height >> skip));
                skip++;
            }
        mips -= skip; total -= skipBytes;
        int tw = System.Math.Max(1, t.Width >> skip), th = System.Math.Max(1, t.Height >> skip);

        var fmt = VkFormat(t.Format, t.Linear);
        CreateHostBuffer((ulong)total, BufferUsageFlags.TransferSrcBit, out var staging, out var stagingMem);
        fixed (byte* src = t.Blocks) System.Buffer.MemoryCopy(src + skipBytes, stagingMem.Mapped, total, total);

        CreateImage((uint)tw, (uint)th, (uint)mips, fmt, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, out var image, out var mem);

        var regions = new BufferImageCopy[mips];
        ulong off = 0;
        for (int i = 0; i < mips; i++)
        {
            int mw = System.Math.Max(1, tw >> i), mh = System.Math.Max(1, th >> i);
            regions[i] = new BufferImageCopy
            {
                BufferOffset = off,
                ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = (uint)i, BaseArrayLayer = 0, LayerCount = 1 },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)mw, (uint)mh, 1)
            };
            off += (ulong)MipSize(t.Format, mw, mh);
        }

        var view = CreateImageView(image, fmt, ImageAspectFlags.ColorBit, (uint)mips);
        var tex = new VulkanTexture(this, image, mem, view, t);
        _live.Add(tex);

        // Sobald Bilder laufen, IMMER verzögert: die Einzel-Kopie wartete per QueueWaitIdle auf die ganze GPU (laufendes Bild),
        // je Textur – ein neuer Sektor mit vielen Texturen kostete so 50–250 ms (Ruckler beim Fahren, schlechte 1%-Werte).
        if (t.Deferred || _frameCounter > 0)
        {
            // Kopie läuft im Command-Buffer des nächsten Bildes vor allem Zeichnen (FlushTextureInits) – kein QueueWaitIdle
            _texInits.Add((tex, staging, stagingMem, regions, (uint)mips));
            return tex;
        }

        // Übergang + Kopie aller Mips + Übergang in EINEM Command-Buffer
        var cmd = BeginSingleTimeCommands();
        RecordTextureInit(cmd, image, staging, regions, (uint)mips);
        EndSingleTimeCommands(cmd);
        FreeBuffer(staging, stagingMem);
        return tex;
    }

    private readonly List<(VulkanTexture tex, Silk.NET.Vulkan.Buffer staging, GpuAllocator.Allocation mem, BufferImageCopy[] regions, uint mips)> _texInits = new();

    private void RecordTextureInit(CommandBuffer cmd, Image image, Silk.NET.Vulkan.Buffer staging, BufferImageCopy[] regions, uint mips)
    {
        var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = mips, BaseArrayLayer = 0, LayerCount = 1 };
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toDst);
        fixed (BufferImageCopy* pr = regions)
            _vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, mips, pr);
        var toRead = toDst;
        toRead.OldLayout = ImageLayout.TransferDstOptimal; toRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        toRead.SrcAccessMask = AccessFlags.TransferWriteBit; toRead.DstAccessMask = AccessFlags.ShaderReadBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in toRead);
    }

    /// <summary>Aufgeschobene Erst-Uploads (TextureUpload.Deferred) in den Bild-Command-Buffer; Staging nach Ablauf der Bilder im Flug frei.</summary>
    private void FlushTextureInits(CommandBuffer cmd)
    {
        if (_texInits.Count == 0) return;
        foreach (var (tex, staging, mem, regions, mips) in _texInits)
        {
            if (!tex.Disposed) RecordTextureInit(cmd, tex.Image, staging, regions, mips);
            var b = staging; var m = mem;
            DeferDestroy(() => FreeBuffer(b, m));
        }
        _texInits.Clear();
    }

    private Sampler GetSampler(TextureWrap u, TextureWrap v)
    {
        if (_samplers.TryGetValue((u, v), out var s)) return s;
        static SamplerAddressMode Mode(TextureWrap w) => w switch
        {
            TextureWrap.Clamp => SamplerAddressMode.ClampToEdge,
            TextureWrap.Mirror => SamplerAddressMode.MirroredRepeat,
            _ => SamplerAddressMode.Repeat,
        };
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = Mode(u),
            AddressModeV = Mode(v),
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = _anisotropy,
            MaxAnisotropy = _anisotropy ? _maxAniso : 1f,
            BorderColor = BorderColor.FloatOpaqueBlack,
            MipmapMode = SamplerMipmapMode.Linear,
            MinLod = 0,
            MaxLod = 1000f,
            MipLodBias = 0   // trilinear wie Unity
        };
        if (_vk.CreateSampler(_device, in info, null, out s) != Result.Success)
            throw new Exception("Sampler failed.");
        _samplers[(u, v)] = s;
        return s;
    }

    private VulkanTexture Pixel(byte r, byte g, byte b, byte a, string name) =>
        (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new[] { r, g, b, a }, Name = name, Linear = true });   // Unity-Standardtexturen: Werte unverändert (grey = 0.5)

    // ------------------------------------------------------------------ Regen-Texturen (WFX_WetGrd: PUDDLES, RIPPLE_01..16)
    private readonly VulkanTexture?[] _rainPudW = new VulkanTexture?[MaxFramesInFlight], _rainRipW = new VulkanTexture?[MaxFramesInFlight];

    private void WriteRainSet(DescriptorSet set, VulkanTexture pud, VulkanTexture rip)
    {
        var samp = GetSampler(TextureWrap.Repeat, TextureWrap.Repeat);
        var pi = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = pud.View, Sampler = samp };
        var ri = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = rip.View, Sampler = samp };
        var w = stackalloc WriteDescriptorSet[2];
        w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 5, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &pi };
        w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 6, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &ri };
        _vk.UpdateDescriptorSets(_device, 2, w, 0, null);
    }

    /// <summary>Bindings 5/6 des Frame-Sets (und der Spiegel-Sets) dieses Bildes, nur bei Wechsel (nach dem Warten auf das Bild).</summary>
    private void UpdateRainDescriptors(int frame)
    {
        var pud = RenderFx.RainPuddles is VulkanTexture p && !p.Disposed ? p : _black;
        var rip = RenderFx.RainRipple is VulkanTexture q && !q.Disposed ? q : _gray;
        if (ReferenceEquals(_rainPudW[frame], pud) && ReferenceEquals(_rainRipW[frame], rip)) return;
        WriteRainSet(_frameSets[frame], pud, rip);
        if (_envSetsCreated)
            for (int s = 0; s < EnvSlots; s++) WriteRainSet(_envSets[frame * EnvSlots + s], pud, rip);
        _rainPudW[frame] = pud; _rainRipW[frame] = rip;
    }

    private void CreateDefaultTextures()
    {
        // Unity-Standardtexturen: white, gray, black
        _white = Pixel(255, 255, 255, 255, "white");
        _gray = Pixel(128, 128, 128, 255, "gray");
        _black = Pixel(0, 0, 0, 0, "black");
        _defaultMaterial = (VulkanMaterial)CreateMaterial(new MaterialDesc { Kind = MaterialKind.Simple, Name = "default" });
    }

    // ------------------------------------------------------------------ Materialien

    public IGpuMaterial CreateMaterial(MaterialDesc d)
    {
        CreateHostBuffer((ulong)(MaterialUboFloats * 4), BufferUsageFlags.UniformBufferBit, out var ubo, out var uboMem);
        var mat = new VulkanMaterial(this, d.Kind, ubo, uboMem);
        WriteMaterial(mat, d);
        _live.Add(mat);
        return mat;
    }

    public void UpdateMaterial(IGpuMaterial material, MaterialDesc desc)
    {
        if (material is not VulkanMaterial vm) return;
        // Typwechsel nur innerhalb desselben Descriptor-Layouts (z.B. Object <-> Foliage, wenn eine Textur nachgereicht wurde)
        if (vm.Kind != desc.Kind)
        {
            if (SetLayoutOf(vm.Kind).Handle != SetLayoutOf(desc.Kind).Handle) return;
            vm.Kind = desc.Kind;
        }
        // Neues Set (das alte kann noch von einem laufenden Frame benutzt werden) + neuer Uniform-Buffer
        var oldSet = vm.Set;
        var oldUbo = vm.Ubo; var oldMem = vm.UboMem;
        CreateHostBuffer((ulong)(MaterialUboFloats * 4), BufferUsageFlags.UniformBufferBit, out vm.Ubo, out vm.UboMem);
        WriteMaterial(vm, desc);
        var vk = _vk; var dev = _device; var pool = _descriptorPool;
        DeferDestroy(() => { vk.FreeDescriptorSets(dev, pool, 1, in oldSet); FreeBuffer(oldUbo, oldMem); });
    }

    private DescriptorSetLayout SetLayoutOf(MaterialKind k) => k switch
    {
        MaterialKind.Terrain => _terrainSetLayout,
        MaterialKind.Object or MaterialKind.Transparent or MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.SpriteGlow or MaterialKind.Grass or MaterialKind.Water or MaterialKind.Flash
            or MaterialKind.Car or MaterialKind.CarGlass or MaterialKind.CarShadow or MaterialKind.Skidmark or MaterialKind.Particle or MaterialKind.Flare or MaterialKind.MapRoad or MaterialKind.Cloud or MaterialKind.SkyStars or MaterialKind.SkyMoon
            or MaterialKind.Lightning or MaterialKind.Avatar => _objectSetLayout,
        _ => _simpleSetLayout,
    };

    private void WriteMaterial(VulkanMaterial mat, MaterialDesc d)
    {
        var layout = d.Kind switch
        {
            MaterialKind.Terrain => _terrainSetLayout,
            MaterialKind.Object or MaterialKind.Transparent or MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.SpriteGlow or MaterialKind.Grass or MaterialKind.Water or MaterialKind.Flash
                or MaterialKind.Car or MaterialKind.CarGlass or MaterialKind.CarShadow or MaterialKind.Skidmark or MaterialKind.Particle or MaterialKind.Flare or MaterialKind.MapRoad or MaterialKind.Cloud or MaterialKind.SkyStars or MaterialKind.SkyMoon
                or MaterialKind.Lightning or MaterialKind.Avatar => _objectSetLayout,
            _ => _simpleSetLayout,
        };
        var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        if (_vk.AllocateDescriptorSets(_device, in alloc, out var set) != Result.Success)
            throw new Exception("DescriptorSet allocation failed (pool full?).");

        // Texturen wie im Unity-Shader: Default white/gray/black je Property
        VulkanTexture T(IGpuTexture? t, VulkanTexture def) => t as VulkanTexture ?? def;
        VulkanTexture[] texs = d.Kind switch
        {
            MaterialKind.Terrain => new[] { T(d.Main, _white), T(d.Splat, _white), T(d.Detail1, _gray), T(d.Detail2, _gray) },
            MaterialKind.Object => d.WinMode && !d.MarksMode && !d.Marks2Mode
                ? new[] { T(d.Main, _white), T(d.Shadow ?? d.NightLm, _white), T(d.Detail, _gray), T(d.Tint, _gray), T(d.GlossTex, _black), T(d.WinTex, _black) }   // Fenster: Gloss/WindowMap
                : new[] { T(d.Main, _white), T(d.Shadow ?? d.NightLm, _white), T(d.Detail, _gray), T(d.Tint, _gray), T(d.Marks, _black), T(d.Marks2, _black) },   // Binding 1: SHADOW oder NIGHTLIGHTMAP
            MaterialKind.Transparent => new[] { T(d.Main, _white), _white, T(d.Detail, _white), _gray, _black, _black },
            MaterialKind.Foliage or MaterialKind.Sprite or MaterialKind.SpriteGlow => new[] { T(d.Main, _white), _white, _gray, _gray, _black, _black },
            MaterialKind.Grass => new[] { T(d.Main, _white), T(d.Ground, _white), T(d.Splat, _white), _gray, _black, _black },
            MaterialKind.Water => new[] { T(d.Main, _gray), T(d.Ground, _gray), _gray, _gray, _black, _black },   // 1: Colormap (TD_Ocean samplerColorMap)
            MaterialKind.Flash => new[] { T(d.Main, _white), T(d.Detail, _white), _gray, _gray, _black, _black },   // _MainTex, _MaskTex
            // TDU2/Car: _MainTex white, _EmissionTex black, _LightMask white, _StickerTex black, _DirtTex black
            MaterialKind.Skidmark or MaterialKind.Particle or MaterialKind.Flare or MaterialKind.MapRoad or MaterialKind.Cloud or MaterialKind.SkyStars or MaterialKind.SkyMoon or MaterialKind.Lightning => new[] { T(d.Main, _white), _white, _white, _black, _black, _black },
            MaterialKind.Car or MaterialKind.CarShadow => new[] { T(d.Main, _white), T(d.EmissionTex, _black), T(d.LightMask, _white), T(d.StickerTex, _black), T(d.DirtTex, _black), T(d.FlakeTex, _gray) },   // 5: FLAKE (grau = Flocke 0)
            MaterialKind.CarGlass => new[] { T(d.Main, _white), T(d.EmissionTex, _black), T(d.LightMask, _white), T(d.StickerTex, _black), T(d.DirtTex, _black), T(d.Detail2Tex, _black) },   // 5: DETAIL2 (Original-Glas; schwarz Alpha 0 = ohne)
            // TDU2/Avatar: Kleidung OCCLUSION, DETAIL_GRAIN, LAYER1..3; sonst SKIN/TEX/HAIR, GLOSSDETAILCUT, SPECTRALSCATTERING, NORMAL
            MaterialKind.Avatar => d.AvMode > 0.5f && d.AvMode < 1.5f
                ? new[] { T(d.AvBump, _gray), T(d.Detail, _gray), T(d.AvLayer1, _black), T(d.AvLayer2, _black), T(d.AvLayer3, _black), _white }
                : new[] { T(d.Main, _white), T(d.AvGloss, _black), T(d.AvRamp, _white), T(d.AvBump, _gray), _black, _black },
            _ => new[] { T(d.Main, _white) },
        };

        // Uniform-Werte (Layout siehe TDU2Shaders)
        float* f = (float*)mat.UboMem.Mapped;
        new Span<float>(f, MaterialUboFloats).Clear();
        static float B(bool b) => b ? 1f : 0f;
        // Unity Material.SetColor-Farben im linearen Farbraum (SetVector-Werte bleiben); Flash/Overlay: Werte wie gesetzt
        bool ui = d.Kind is MaterialKind.Flash or MaterialKind.Overlay;
        Vector4 col = ui ? d.Color : ToLin(d.Color);
        switch (d.Kind)
        {
            case MaterialKind.Terrain:
                SetVec(f, 0, d.DetailTiling, d.DetailStrength, d.ShadowStrength, B(d.UseSplat));
                break;
            case MaterialKind.Object:
            case MaterialKind.Transparent:
                float tx = d.Marks != null && d.Marks.Width > 0 ? 1f / d.Marks.Width : 0f;
                float ty = d.Marks != null && d.Marks.Height > 0 ? 1f / d.Marks.Height : 0f;
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.ShadowStrength, d.VertexColorStrength, d.VcScale, d.Cutoff);
                SetVec(f, 2, B(d.DetailMode), B(d.DetailUvLast), d.ColorStrength, d.DetailBoost);
                SetVec(f, 3, B(d.DetailGray), B(d.TintMode), d.TintScale, B(d.MarksMode));
                SetVec(f, 4, B(d.Marks2Mode), d.Brightness, d.Wrap, B(d.PlantMode));
                SetVec(f, 5, B(d.DetailAlpha), d.Unlit, tx, ty);
                SetVec(f, 6, B(d.WetSurface), d.NightEmit, B(d.WinMode && !d.MarksMode && !d.Marks2Mode), B(d.Kind == MaterialKind.Object && d.Shadow == null && d.NightLm != null));
                SetVec(f, 7, B(d.AlphaFromDetail), d.Lit, d.Intensity, B(d.NightOnly));
                SetVec(f, 8, B(d.Kind == MaterialKind.Transparent ? d.VcMul : d.InteriorLm), d.NightLmap.X, d.NightLmap.Y, d.NightLmDayBoost);
                SetVec(f, 9, d.MainST.X, d.MainST.Y, d.MainST.Z, d.MainST.W);
                SetVec(f, 10, d.DetailST.X, d.DetailST.Y, d.DetailST.Z, d.DetailST.W);
                SetVec(f, 11, d.WinParams.X, d.WinParams.Y, d.WinParams.Z, d.WinParams.W);
                SetVec(f, 12, d.ReflMix, 0f, 0f, 0f);   // p11.x _ReflMix (Bodenspiegelung)
                break;
            case MaterialKind.Grass:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.Cutoff, d.GrassWind, B(d.Ground != null), d.GroundBoost);
                SetVec(f, 2, d.GroundTipMix, B(d.Splat != null), d.ShadowStrength, 0);
                break;
            case MaterialKind.Water:
                SetVec(f, 0, ToLin(d.PoolTint).X, ToLin(d.PoolTint).Y, ToLin(d.PoolTint).Z, d.PoolTint.W);
                SetVec(f, 1, d.NMapScale.X, d.NMapScale.Y, d.NMapWeight.X, d.NMapWeight.Y);
                SetVec(f, 2, d.WaveStrength, d.WaveSpeed, d.FlatDistance, B(d.Main != null));
                SetVec(f, 3, d.Fresnel.X, d.Fresnel.Y, d.Fresnel.Z, d.ReflBlur);
                SetVec(f, 4, d.ReflColor.X, d.ReflColor.Y, d.ReflColor.Z, d.SpecPower);
                SetVec(f, 5, d.SpecColorCut.X, d.SpecColorCut.Y, d.SpecColorCut.Z, d.SpecColorCut.W);
                SetVec(f, 6, d.OceanColorUv.X, d.OceanColorUv.Y, d.OceanColorUv.Z, d.OceanColorUv.W);
                SetVec(f, 7, B(d.Ocean), B(d.Ground != null), d.OceanLevel, 0f);
                break;
            case MaterialKind.Foliage:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.VertexColorStrength, d.NormalUp, d.FoliageWrap, d.Translucency);
                SetVec(f, 2, d.SelfShadow, d.UseVertexColor, d.WindStrength, d.WindSpeed);
                SetVec(f, 3, d.FlutterStrength, d.Cutoff, 0, 0);
                break;
            case MaterialKind.Sprite:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.Cutoff, d.CrownHeight, B(d.CrownOn), d.SpriteVertexColor);
                SetVec(f, 2, d.WindStrength, d.WindSpeed, 0, 0);
                break;
            case MaterialKind.SpriteGlow:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.Intensity, B(d.NightOnly), 0, 0);
                break;
            case MaterialKind.Car:
            case MaterialKind.CarGlass:
            case MaterialKind.CarShadow:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, ToLin(d.Color2).X, ToLin(d.Color2).Y, ToLin(d.Color2).Z, d.Color2.W);
                SetVec(f, 2, d.Bicolour, d.RampPow, d.MainGray, d.AlphaFromRed);
                SetVec(f, 3, d.Heat, d.Glossiness, d.Metallic, d.Cutoff);
                SetVec(f, 4, ToLin(d.HeatColor).X, ToLin(d.HeatColor).Y, ToLin(d.HeatColor).Z, d.HeatColor.W);
                SetVec(f, 5, ToLin(d.EmissionTint).X, ToLin(d.EmissionTint).Y, ToLin(d.EmissionTint).Z, d.EmissionTint.W);
                SetVec(f, 6, d.Emission, d.EmissionDiff, d.SelfIllum, d.Occlusion);
                SetVec(f, 7, d.ClearCoat, d.ClearCoatPower, d.Flake, d.FlakeScale);
                SetVec(f, 8, d.UseMask, d.LgtLights, d.LgtBrake, d.LgtReverse);
                SetVec(f, 9, d.LgtBlink, d.UseSticker, d.StickerUV, d.StickerAlphaOnly);
                SetVec(f, 10, d.DirtLevel, d.ScratchLevel, d.MaxAlpha, d.Strength);
                SetVec(f, 11, ToLin(d.DirtColor).X, ToLin(d.DirtColor).Y, ToLin(d.DirtColor).Z, d.DirtColor.W);
                SetVec(f, 12, ToLin(d.TailLightColor).X, ToLin(d.TailLightColor).Y, ToLin(d.TailLightColor).Z, d.TailLightColor.W);
                SetVec(f, 13, ToLin(d.BrakeLightColor).X, ToLin(d.BrakeLightColor).Y, ToLin(d.BrakeLightColor).Z, d.BrakeLightColor.W);
                SetVec(f, 14, ToLin(d.ReverseLightColor).X, ToLin(d.ReverseLightColor).Y, ToLin(d.ReverseLightColor).Z, d.ReverseLightColor.W);
                SetVec(f, 15, ToLin(d.BlinkLightColor).X, ToLin(d.BlinkLightColor).Y, ToLin(d.BlinkLightColor).Z, d.BlinkLightColor.W);
                SetVec(f, 16, d.LightIntensity.X, d.LightIntensity.Y, d.LightIntensity.Z, d.LightIntensity.W);
                SetVec(f, 17, d.TdMatParam.X, d.TdMatParam.Y, d.TdMatParam.Z, d.TdMatParam.W);
                SetVec(f, 18, d.TdEnvCoeff.X, d.TdEnvCoeff.Y, d.TdEnvCoeff.Z, d.TdEnvCoeff.W);
                SetVec(f, 19, d.TdSpecParm.X, d.TdSpecParm.Y, d.TdSpecParm.Z, d.TdSpecParm.W);
                SetVec(f, 20, ToLin(d.TdLayrClr).X, ToLin(d.TdLayrClr).Y, ToLin(d.TdLayrClr).Z, d.TdLayrClr.W);
                SetVec(f, 21, d.TdPaint, d.TdLayer, d.TdRampPowY, d.TdFlake);
                SetVec(f, 22, d.TdRampFlake.X, d.TdRampFlake.Y, d.TdRampFlake.Z, d.TdRampFlake.W);   // roh (Gamma-Raum wie das Original)
                SetVec(f, 23, d.TdFlakeABNS.X, d.TdFlakeABNS.Y, d.TdFlakeABNS.Z, d.TdFlakeABNS.W);
                break;
            case MaterialKind.Avatar:
                // Unity: SetColor-Werte linear (SetCol), SetVector-Werte roh (SetRaw) – TDU2Avatar.shader-Properties
                SetCol(f, 0, d.Color);
                SetVec(f, 1, d.AvMode, d.Cutoff, d.AvBrightness, d.AvAlphaLum);
                SetCol(f, 2, d.AvSkinClr); SetCol(f, 3, d.AvDclClr); SetRaw(f, 4, d.AvCoeffs); SetRaw(f, 5, d.AvUVScale); SetCol(f, 6, d.AvBaseClr);
                SetCol(f, 7, d.AvClrL1); SetCol(f, 8, d.AvClrL2); SetCol(f, 9, d.AvClrL3);
                SetVec(f, 10, d.AvLayerOn.X, d.AvLayerOn.Y, d.AvLayerOn.Z, d.AvGlossOn);
                SetVec(f, 11, d.AvLayerUV.X, d.AvLayerUV.Y, d.AvLayerUV.Z, d.AvRampOn);
                SetVec(f, 12, d.AvUVOff.X, d.AvUVOff.Y, d.AvOmbre, d.AvBumpOn);
                SetCol(f, 13, d.AvBColor); SetCol(f, 14, d.AvTColor); SetCol(f, 15, d.AvPrimClr); SetCol(f, 16, d.AvScdClr);
                SetRaw(f, 17, d.AvSpec); SetRaw(f, 18, d.AvCarInteg); SetRaw(f, 19, d.AvAtteFact);
                SetVec(f, 20, d.AvInCar, d.AvDetailOn, d.AvRampScale, d.AvIndoor);
                SetRaw(f, 21, d.AvIndoorDir); SetCol(f, 22, d.AvIndoorLight); SetCol(f, 23, d.AvIndoorAmbTop); SetCol(f, 24, d.AvIndoorAmbBottom);
                SetRaw(f, 25, d.AvTweak); SetVec(f, 26, d.AvHairSheen, 0f, 0f, 0f);
                break;
            case MaterialKind.Cloud:
                SetVec(f, 0, d.CloudWeight, 0f, 0f, 0f);
                break;
            case MaterialKind.Particle:
                SetVec(f, 0, d.Lit, 0f, 0f, 0f);   // td_particle: 1 = VS mit __AmbLight_Ambient_0 / __DirLight_Diffuse_0
                break;
            case MaterialKind.MapTerrain:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, d.Brightness, d.Relief, 0, 0);
                break;
            default:
                SetVec(f, 0, col.X, col.Y, col.Z, col.W);
                SetVec(f, 1, 0, 0, 0, d.Cutoff);
                break;
        }

        int n = texs.Length;
        var infos = stackalloc DescriptorImageInfo[n];
        var writes = stackalloc WriteDescriptorSet[n + 1];
        for (int i = 0; i < n; i++)
        {
            infos[i] = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = texs[i].View, Sampler = GetSampler(texs[i].WrapU, texs[i].WrapV) };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = infos + i
            };
        }
        var bufInfo = new DescriptorBufferInfo { Buffer = mat.Ubo, Offset = 0, Range = (ulong)(MaterialUboFloats * 4) };
        writes[n] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = (uint)n,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &bufInfo
        };
        _vk.UpdateDescriptorSets(_device, (uint)(n + 1), writes, 0, null);

        mat.Set = set;
        mat.Additive = (d.Kind == MaterialKind.Transparent || d.Kind == MaterialKind.SpriteGlow || d.Kind == MaterialKind.Particle) && d.Additive;
        mat.Glow = d.Glow;
        mat.Blend = d.Kind == MaterialKind.Avatar && d.AvBlend;
        mat.TwoSided = d.TwoSided;
        mat.WaterSort = d.Kind == MaterialKind.Water && d.Ocean ? (d.OceanLevel >= 2 ? float.MaxValue : d.OceanLevel == 1 ? 3e38f : 2e38f) : float.MaxValue;
    }

    private static void SetCol(float* f, int i, Vector4 c) { var l = ToLin(c); SetVec(f, i, l.X, l.Y, l.Z, c.W); }
    private static void SetRaw(float* f, int i, Vector4 v) => SetVec(f, i, v.X, v.Y, v.Z, v.W);
    private static void SetVec(float* f, int i, float x, float y, float z, float w)
    {
        f[i * 4] = x; f[i * 4 + 1] = y; f[i * 4 + 2] = z; f[i * 4 + 3] = w;
    }

    // ------------------------------------------------------------------ Meshes

    public IGpuMesh CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, TextureUpload? texture = null)
    {
        var mesh = UploadMesh(MemoryMarshal.AsBytes(vertices), indices, false);
        if (texture != null)
        {
            try
            {
                mesh.OwnedTexture = (VulkanTexture)CreateTexture(texture);
                mesh.OwnedMaterial = (VulkanMaterial)CreateMaterial(new MaterialDesc { Kind = MaterialKind.Simple, Main = mesh.OwnedTexture });
            }
            catch (Exception ex) { Log.Error($"Could not create texture: {ex.Message}"); }
        }
        return mesh;
    }

    public IGpuMesh CreateMesh(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices)
        => UploadMesh(MemoryMarshal.AsBytes(vertices), indices, true);

    private VulkanMesh UploadMesh(ReadOnlySpan<byte> vbytes, ReadOnlySpan<uint> indices, bool objectFormat)
    {
        var ibytes = MemoryMarshal.AsBytes(indices);
        CreateHostBuffer((ulong)vbytes.Length, BufferUsageFlags.VertexBufferBit, out var vbuf, out var vmem);
        CreateHostBuffer((ulong)ibytes.Length, BufferUsageFlags.IndexBufferBit, out var ibuf, out var imem);
        vbytes.CopyTo(new Span<byte>(vmem.Mapped, vbytes.Length));
        ibytes.CopyTo(new Span<byte>(imem.Mapped, ibytes.Length));
        var mesh = new VulkanMesh(this, vbuf, vmem, ibuf, imem, indices.Length, objectFormat);
        // Bounding-Kugel (Position = erste 12 Byte jedes Vertex) für das Schatten-Culling
        int stride = objectFormat ? (int)ObjectVertex.SizeInBytes : (int)MeshVertex.SizeInBytes;
        int vc = stride > 0 ? vbytes.Length / stride : 0;
        if (vc > 0)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i < vc; i++)
            {
                var pv = MemoryMarshal.Read<Vector3>(vbytes.Slice(i * stride, 12));
                mn = Vector3.Min(mn, pv); mx = Vector3.Max(mx, pv);
            }
            mesh.BCenter = (mn + mx) * 0.5f;
            mesh.BRadius = (mx - mn).Length() * 0.5f;
        }
        _live.Add(mesh);
        return mesh;
    }

    // ------------------------------------------------------------------ GPU-Objekte

    private sealed class VulkanTexture : IGpuTexture
    {
        private readonly VulkanRenderer _r;
        public readonly Image Image;
        public readonly GpuAllocator.Allocation Mem;
        public readonly ImageView View;
        public readonly TextureWrap WrapU, WrapV;
        public int Width { get; }
        public int Height { get; }
        public string? Name { get; }
        public GpuTextureFormat Format { get; }
        /// <summary>UNORM statt sRGB (TextureUpload.Linear) – für die Diagnose.</summary>
        public readonly bool Linear;
        private bool _disposed;
        public bool Disposed => _disposed;
        /// <summary>Staging-Puffer je Bild im Flug für UpdateTexture (erst bei Bedarf angelegt).</summary>
        public Silk.NET.Vulkan.Buffer[]? Staging;
        public GpuAllocator.Allocation[]? StagingMem;

        public VulkanTexture(VulkanRenderer r, Image image, GpuAllocator.Allocation mem, ImageView view, TextureUpload t)
        { _r = r; Image = image; Mem = mem; View = view; Width = t.Width; Height = t.Height; WrapU = t.WrapU; WrapV = t.WrapV; Name = t.Name; Format = t.Format; Linear = t.Linear; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _r._live.Remove(this);
            var r = _r; var view = View; var img = Image; var mem = Mem; var st = Staging; var stm = StagingMem;
            r.DeferDestroy(() =>
            {
                r._vk.DestroyImageView(r._device, view, null);
                r._vk.DestroyImage(r._device, img, null);
                r._alloc.Free(mem);
                if (st != null && stm != null) for (int i = 0; i < st.Length; i++) r.FreeBuffer(st[i], stm[i]);
            });
        }
    }

    private sealed class VulkanMaterial : IGpuMaterial
    {
        private readonly VulkanRenderer _r;
        public DescriptorSet Set;
        public Silk.NET.Vulkan.Buffer Ubo;
        public GpuAllocator.Allocation UboMem;
        public bool Additive;
        public bool Glow;   // Bloom-Maske (MaterialDesc.Glow)
        /// <summary>MaterialDesc.TwoSided (Cull-Modus des .2DM-Materials).</summary>
        public bool TwoSided = true;
        /// <summary>TDU2/AvatarBlend (Alpha-Blend, ZWrite Off) statt TDU2/Avatar.</summary>
        public bool Blend;
        /// <summary>Sortierwert in der Transparent-Liste für Wasser (Meer: Insel vor Area vor Sektor).</summary>
        public float WaterSort = float.MaxValue;
        public MaterialKind Kind { get; set; }
        private bool _disposed;

        public VulkanMaterial(VulkanRenderer r, MaterialKind kind, Silk.NET.Vulkan.Buffer ubo, GpuAllocator.Allocation mem)
        { _r = r; Kind = kind; Ubo = ubo; UboMem = mem; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _r._live.Remove(this);
            var r = _r; var set = Set; var ubo = Ubo; var mem = UboMem;
            r.DeferDestroy(() => { r._vk.FreeDescriptorSets(r._device, r._descriptorPool, 1, in set); r.FreeBuffer(ubo, mem); });
        }
    }

    private sealed class VulkanMesh : IGpuMesh
    {
        private readonly VulkanRenderer _r;
        public readonly Silk.NET.Vulkan.Buffer VertexBuffer, IndexBuffer;
        public readonly GpuAllocator.Allocation VMem, IMem;
        public readonly bool IsObjectFormat;
        public int IndexCount { get; }
        public Vector3 BCenter;
        public float BRadius = 1e6f;
        public VulkanTexture? OwnedTexture;     // Altpfad (CreateMesh mit Textur)
        public VulkanMaterial? OwnedMaterial;
        private bool _disposed;

        public VulkanMesh(VulkanRenderer r, Silk.NET.Vulkan.Buffer vb, GpuAllocator.Allocation vm, Silk.NET.Vulkan.Buffer ib, GpuAllocator.Allocation im, int ic, bool objectFormat)
        { _r = r; VertexBuffer = vb; VMem = vm; IndexBuffer = ib; IMem = im; IndexCount = ic; IsObjectFormat = objectFormat; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _r._live.Remove(this);
            OwnedMaterial?.Dispose();
            OwnedTexture?.Dispose();
            var r = _r; var vb = VertexBuffer; var vm = VMem; var ib = IndexBuffer; var im = IMem;
            r.DeferDestroy(() => { r.FreeBuffer(vb, vm); r.FreeBuffer(ib, im); });
        }
    }

    // ------------------------------------------------------------------ Descriptoren / Frame-Buffer

    private void CreateDescriptorSetLayouts()
    {
        _simpleSetLayout = CreateSetLayout(1);
        _terrainSetLayout = CreateSetLayout(4);
        _objectSetLayout = CreateSetLayout(6);
        // set 1: Frame-Globals
        var fb = stackalloc DescriptorSetLayoutBinding[7];
        fb[0] = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };
        fb[1] = new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };   // Schattenkarte
        fb[2] = new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };   // Bodenspiegelung
        fb[3] = new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };   // Reflexionssonde
        fb[4] = new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit };   // Instanz-Modellmatrizen
        fb[5] = new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };   // Regen: PUDDLES (WFX_WetGrd)
        fb[6] = new DescriptorSetLayoutBinding { Binding = 6, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };   // Regen: aktuelles RIPPLE_xx
        var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 7, PBindings = fb };
        if (_vk.CreateDescriptorSetLayout(_device, in info, null, out _frameSetLayout) != Result.Success)
            throw new Exception("DescriptorSetLayout (Frame) failed.");
    }

    /// <summary>Layout: samplers x Textur (Binding 0..n-1) + Material-Uniform-Buffer (Binding n).</summary>
    private DescriptorSetLayout CreateSetLayout(int samplers)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[samplers + 1];
        for (int i = 0; i < samplers; i++)
            bindings[i] = new DescriptorSetLayoutBinding { Binding = (uint)i, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        bindings[samplers] = new DescriptorSetLayoutBinding { Binding = (uint)samplers, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };
        var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)(samplers + 1), PBindings = bindings };
        if (_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout) != Result.Success)
            throw new Exception("DescriptorSetLayout failed.");
        return layout;
    }

    private void CreateDescriptorPool()
    {
        const uint sets = 32768;
        var sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = sets * 4 };
        sizes[1] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = sets + 8 };
        sizes[2] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 32 };   // Instanzpuffer der Frame-/Spiegel-Sets
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
            MaxSets = sets + 8,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };
        if (_vk.CreateDescriptorPool(_device, in info, null, out _descriptorPool) != Result.Success)
            throw new Exception("DescriptorPool failed.");
    }

    private void CreateFrameBuffers()
    {
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            CreateHostBuffer((ulong)sizeof(FrameUbo), BufferUsageFlags.UniformBufferBit, out _frameUbo[i], out _frameUboMem[i]);
            var layout = _frameSetLayout;
            var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
            if (_vk.AllocateDescriptorSets(_device, in alloc, out _frameSets[i]) != Result.Success)
                throw new Exception("DescriptorSet (Frame) failed.");
            var bi = new DescriptorBufferInfo { Buffer = _frameUbo[i], Offset = 0, Range = (ulong)sizeof(FrameUbo) };
            var ii = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _shadowArrayView, Sampler = _shadowSampler };
            CreateHostBuffer((ulong)(InstCap * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit, out _instBuf[i], out _instMem[i]);
            var ib = new DescriptorBufferInfo { Buffer = _instBuf[i], Offset = 0, Range = Vk.WholeSize };
            var w = stackalloc WriteDescriptorSet[3];
            w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi };
            w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &ii };
            w[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, PBufferInfo = &ib };
            _vk.UpdateDescriptorSets(_device, 3, w, 0, null);
        }
    }

    // ------------------------------------------------------------------ Schatten-Ressourcen

    private void CreateShadowResources()
    {
        // Tiefen-Array (eine Ebene je Kaskade)
        var ici = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(ShadowSize, ShadowSize, 1),
            MipLevels = 1,
            ArrayLayers = ShadowCascades,
            Format = Format.D32Sfloat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateImage(_device, in ici, null, out _shadowImage) != Result.Success) throw new Exception("Shadow map failed.");
        _vk.GetImageMemoryRequirements(_device, _shadowImage, out var req);
        _shadowMem = _alloc.Allocate(req, MemoryPropertyFlags.DeviceLocalBit, linear: false);
        _vk.BindImageMemory(_device, _shadowImage, _shadowMem.Memory, _shadowMem.Offset);

        var avi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _shadowImage,
            ViewType = ImageViewType.Type2DArray,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = ShadowCascades }
        };
        if (_vk.CreateImageView(_device, in avi, null, out _shadowArrayView) != Result.Success) throw new Exception("Shadow View failed.");

        // Render-Pass: nur Tiefe, danach im Shader lesbar
        var depth = new AttachmentDescription
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var dref = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var sub = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, PDepthStencilAttachment = &dref };
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        var rpi = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &depth,
            SubpassCount = 1,
            PSubpasses = &sub,
            DependencyCount = 2,
            PDependencies = deps
        };
        if (_vk.CreateRenderPass(_device, in rpi, null, out _shadowPass) != Result.Success) throw new Exception("Shadow RenderPass failed.");

        for (int k = 0; k < ShadowCascades; k++)
        {
            var lvi = avi;
            lvi.ViewType = ImageViewType.Type2D;
            lvi.SubresourceRange.BaseArrayLayer = (uint)k;
            lvi.SubresourceRange.LayerCount = 1;
            if (_vk.CreateImageView(_device, in lvi, null, out _shadowLayerViews[k]) != Result.Success) throw new Exception("Shadow View failed.");
            var view = _shadowLayerViews[k];
            var fbi = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _shadowPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = ShadowSize,
                Height = ShadowSize,
                Layers = 1
            };
            if (_vk.CreateFramebuffer(_device, in fbi, null, out _shadowFramebuffers[k]) != Result.Success) throw new Exception("Shadow framebuffer failed.");
        }

        // Vergleichs-Sampler (Hardware-PCF), außerhalb = beleuchtet
        var si = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            BorderColor = BorderColor.FloatOpaqueWhite,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
            MipmapMode = SamplerMipmapMode.Nearest,
            MinLod = 0,
            MaxLod = 0
        };
        if (_vk.CreateSampler(_device, in si, null, out _shadowSampler) != Result.Success) throw new Exception("Shadow sampler failed.");
    }

    private void DestroyShadowResources()
    {
        foreach (var fb in _shadowFramebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        foreach (var v in _shadowLayerViews) _vk.DestroyImageView(_device, v, null);
        _vk.DestroyImageView(_device, _shadowArrayView, null);
        _vk.DestroyRenderPass(_device, _shadowPass, null);
        _vk.DestroySampler(_device, _shadowSampler, null);
        _vk.DestroyImage(_device, _shadowImage, null);
        _alloc.Free(_shadowMem);
    }

    // ------------------------------------------------------------------ Pipelines

    private void CreatePipelines()
    {
        using var c = new ShaderCompiler();
        CreatePipeline(c, PipeId.Simple, GameShaders.SimpleVert, GameShaders.SimpleFrag, _simpleSetLayout, false, blend: 0);
        CreatePipeline(c, PipeId.Terrain, GameShaders.TerrainVert, GameShaders.TerrainFrag, _terrainSetLayout, false, blend: 0);
        CreatePipeline(c, PipeId.MapTerrain, GameShaders.SimpleVert, GameShaders.MapTerrainFrag, _simpleSetLayout, false, blend: 0);
        CreatePipeline(c, PipeId.MapRoad, GameShaders.FxVert, GameShaders.ParticleFrag, _objectSetLayout, true, blend: 1, depthTest: false);
        // Welt-/Interieur-Materialien aus .2DM: zusätzlich Varianten mit Rückseiten-Culling (MaterialDesc.TwoSided)
        CreatePipeline(c, PipeId.Object, GameShaders.ObjectVert, GameShaders.ObjectFrag, _objectSetLayout, true, blend: 0, cullVariants: true);
        CreatePipeline(c, PipeId.TransparentAlpha, GameShaders.TransparentVert, GameShaders.TransparentFrag, _objectSetLayout, true, blend: 1, cullVariants: true);
        CreatePipeline(c, PipeId.TransparentAdd, GameShaders.TransparentVert, GameShaders.TransparentFrag, _objectSetLayout, true, blend: 2, cullVariants: true);
        CreatePipeline(c, PipeId.TransparentAlphaGlow, GameShaders.TransparentVert, GameShaders.TransparentFrag, _objectSetLayout, true, blend: 1, cullVariants: true);
        CreatePipeline(c, PipeId.Foliage, GameShaders.FoliageVert, GameShaders.FoliageFrag, _objectSetLayout, true, blend: 0, cullVariants: true);
        CreatePipeline(c, PipeId.Sprite, GameShaders.SpriteVert, GameShaders.SpriteFrag, _objectSetLayout, true, blend: 0);
        CreatePipeline(c, PipeId.GlowAlpha, GameShaders.SpriteGlowVert, GameShaders.SpriteGlowFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.GlowAdd, GameShaders.SpriteGlowVert, GameShaders.SpriteGlowFrag, _objectSetLayout, true, blend: 2);
        CreatePipeline(c, PipeId.Grass, GameShaders.GrassVert, GameShaders.GrassFrag, _objectSetLayout, true, blend: 0);
        CreatePipeline(c, PipeId.Water, GameShaders.WaterVert, GameShaders.WaterFrag, _objectSetLayout, true, blend: 3);
        CreatePipeline(c, PipeId.Sky, GameShaders.SkyVert, GameShaders.SkyFrag, _simpleSetLayout, false, blend: 0, depthTest: false, noVertices: true);
        CreatePipeline(c, PipeId.ShadowObject, GameShaders.ObjectVert, GameShaders.ShadowObjectFrag, _objectSetLayout, true, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.ShadowFoliage, GameShaders.FoliageVert, GameShaders.ShadowFoliageFrag, _objectSetLayout, true, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.ShadowSprite, GameShaders.SpriteVert, GameShaders.ShadowSpriteFrag, _objectSetLayout, true, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.ShadowTerrain, GameShaders.ShadowTerrainVert, GameShaders.ShadowEmptyFrag, _terrainSetLayout, false, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.Overlay, GameShaders.OverlayVert, GameShaders.OverlayFrag, _simpleSetLayout, true, blend: 1, depthTest: false);
        CreatePipeline(c, PipeId.Flash, GameShaders.FlashVert, GameShaders.FlashFrag, _objectSetLayout, true, blend: 1, depthTest: false);
        if (_uiPass.Handle != 0)
        {
            CreatePipeline(c, PipeId.FlashUi, GameShaders.FlashVert, GameShaders.FlashFrag, _objectSetLayout, true, blend: 1, depthTest: false, ui: true);
            CreatePipeline(c, PipeId.OverlayUi, GameShaders.OverlayVert, GameShaders.OverlayUiFrag, _simpleSetLayout, true, blend: 1, depthTest: false, ui: true);
        }
        CreatePipeline(c, PipeId.Car, GameShaders.CarVert, GameShaders.CarFrag, _objectSetLayout, true, blend: 0);
        CreatePipeline(c, PipeId.CarGlass, GameShaders.CarVert, GameShaders.CarGlassFrag, _objectSetLayout, true, blend: 4);
        CreatePipeline(c, PipeId.CarShadow, GameShaders.CarVert, GameShaders.CarShadowFrag, _objectSetLayout, true, blend: 5);
        CreatePipeline(c, PipeId.ShadowCar, GameShaders.CarVert, GameShaders.ShadowCarFrag, _objectSetLayout, true, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.Avatar, GameShaders.AvatarVert, GameShaders.AvatarFrag, _objectSetLayout, true, blend: 0);
        CreatePipeline(c, PipeId.AvatarBlend, GameShaders.AvatarVert, GameShaders.AvatarBlendFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.ShadowAvatar, GameShaders.AvatarVert, GameShaders.ShadowAvatarFrag, _objectSetLayout, true, blend: 0, shadow: true);
        CreatePipeline(c, PipeId.Skidmark, GameShaders.FxVert, GameShaders.SkidmarkFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.Cloud, GameShaders.CloudVert, GameShaders.CloudFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.SkyStars, GameShaders.SkyStarsVert, GameShaders.SkyStarsFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.SkyMoon, GameShaders.MoonVert, GameShaders.MoonFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.Lightning, GameShaders.FxVert, GameShaders.LightningFrag, _objectSetLayout, true, blend: 2);   // TD_Lightning: SRCALPHA/ONE
        CreatePipeline(c, PipeId.Particle, GameShaders.FxVert, GameShaders.TdParticleFrag, _objectSetLayout, true, blend: 1);
        CreatePipeline(c, PipeId.ParticleAdd, GameShaders.FxVert, GameShaders.TdParticleFrag, _objectSetLayout, true, blend: 2);   // .2DM SrcAlpha/One
        CreatePipeline(c, PipeId.Flare, GameShaders.FxVert, GameShaders.FlareFrag, _objectSetLayout, true, blend: 6);
    }

    /// <param name="blend">0 = opak (Tiefe schreiben), 1 = SrcAlpha/OneMinusSrcAlpha, 2 = SrcAlpha/One (beide ohne Tiefenschreiben, Offset)</param>
    private void CreatePipeline(ShaderCompiler compiler, PipeId id, string vertSrc, string fragSrc, DescriptorSetLayout setLayout, bool objectVertex, int blend, bool depthTest = true, bool shadow = false, bool noVertices = false, bool ui = false, bool cullVariants = false)
    {
        string name = id.ToString();
        ShaderModule vertModule = CreateShaderModule(compiler.Compile(vertSrc, ShaderKind.VertexShader, name + ".vert"));
        ShaderModule fragModule = CreateShaderModule(compiler.Compile(fragSrc, ShaderKind.FragmentShader, name + ".frag"));
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entry };

            var attrs = stackalloc VertexInputAttributeDescription[7];
            uint attrCount;
            VertexInputBindingDescription bindingDesc;
            if (objectVertex)
            {
                bindingDesc = new VertexInputBindingDescription { Binding = 0, Stride = ObjectVertex.SizeInBytes, InputRate = VertexInputRate.Vertex };
                attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
                attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 };
                attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 24 };
                attrs[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32G32Sfloat, Offset = 28 };
                attrs[4] = new VertexInputAttributeDescription { Location = 4, Binding = 0, Format = Format.R32G32Sfloat, Offset = 36 };
                attrs[5] = new VertexInputAttributeDescription { Location = 5, Binding = 0, Format = Format.R32G32Sfloat, Offset = 44 };
                attrs[6] = new VertexInputAttributeDescription { Location = 6, Binding = 0, Format = Format.R32G32Sfloat, Offset = 52 };
                attrCount = 7;
            }
            else
            {
                bindingDesc = new VertexInputBindingDescription { Binding = 0, Stride = MeshVertex.SizeInBytes, InputRate = VertexInputRate.Vertex };
                attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
                attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 };
                attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 };
                attrCount = 3;
            }
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = noVertices ? 0u : 1u,
                PVertexBindingDescriptions = &bindingDesc,
                VertexAttributeDescriptionCount = noVertices ? 0u : attrCount,
                PVertexAttributeDescriptions = attrs
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.Clockwise,
                // ObjectTransparent: "Offset -1, -1" (D3D) -> bei Reversed-Z positiv (zur Kamera hin)
                // Water: "Offset 2, 2" (hinter flachem Strand-Terrain)
                // CarGlass (4): ohne Offset; CarShadow (5): "Offset -2, -2" -> +2
                DepthBiasEnable = shadow || (blend != 0 && blend != 4 && depthTest),
                DepthBiasConstantFactor = shadow ? 1.25f : blend == 3 ? -2f : blend == 5 ? 2f : blend == 6 ? -1f : 1f,
                DepthBiasSlopeFactor = shadow ? 1.75f : blend == 3 ? -2f : blend == 5 ? 2f : blend == 6 ? -1f : 1f
            };
            // Weltdurchgang mit Kantenglättung (MSAA); Schattenkarten und Flash-Durchgang immer einfach abgetastet
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = shadow || ui ? SampleCountFlags.Count1Bit : _msaa };
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = blend != 0,
                // 3/4 vormultipliziert (One, OneMinusSrcAlpha), 5 multiplikativ (DstColor, Zero)
                SrcColorBlendFactor = blend is 3 or 4 or 6 ? BlendFactor.One : blend == 5 ? BlendFactor.DstColor : BlendFactor.SrcAlpha,
                DstColorBlendFactor = blend is 2 or 6 ? BlendFactor.One : blend == 5 ? BlendFactor.Zero : BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add
            };
            // Bloom-Maske (Zusatz PostFx): Alpha-Kanal des Weltbilds = "leuchtet" (Löschwert 0). Leuchtende Pipelines schreiben ihr
            // Alpha (opak direkt, transparent aufaddiert), opake andere setzen 0 (verdecken), transparente andere lassen es stehen.
            if (!shadow && !ui)
            {
                bool glow = id is PipeId.Object or PipeId.Car or PipeId.GlowAlpha or PipeId.GlowAdd or PipeId.TransparentAdd
                    or PipeId.TransparentAlphaGlow or PipeId.Flare or PipeId.Lightning;
                if (blend == 0)
                {
                    if (!glow)
                    {
                        colorBlendAttachment.BlendEnable = true;   // Farbe wie ohne Mischen (One/Zero), Alpha = 0
                        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
                        colorBlendAttachment.DstColorBlendFactor = BlendFactor.Zero;
                        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
                        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.Zero;
                    }
                }
                else
                {
                    colorBlendAttachment.SrcAlphaBlendFactor = glow ? BlendFactor.One : BlendFactor.Zero;
                    colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.One;
                }
            }
            var colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = shadow ? 0u : 1u, PAttachments = &colorBlendAttachment };
            var dynStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = depthTest,
                DepthWriteEnable = blend == 0 && depthTest,
                // Hauptbild Reversed-Z, Schattenkarten normale Tiefe
                DepthCompareOp = shadow ? CompareOp.LessOrEqual : CompareOp.GreaterOrEqual
            };

            var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = (uint)sizeof(PushConstants) };
            var setLayouts = stackalloc DescriptorSetLayout[2] { setLayout, _frameSetLayout };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _layouts[(int)id]) != Result.Success)
                throw new Exception("PipelineLayout failed.");

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = _layouts[(int)id],
                RenderPass = shadow ? _shadowPass : ui ? _uiPass : _renderPass,
                Subpass = 0
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _pipelines[(int)id]) != Result.Success)
                throw new Exception($"GraphicsPipeline ‘{name}’ failed.");
            if (cullVariants)
            {
                // Geprüft an itun_a: nach StripToList zeigt das Kreuzprodukt (b-a)x(c-a) zu 98 % in Richtung der Vertexnormalen.
                // Mit RH-LookAt und Projektion (Y gespiegelt) liegt die Vorderseite damit bei Vulkan gegen den Uhrzeigersinn.
                // FrontFace bleibt Clockwise (gl_FrontFacing wie bisher), daher wird hier FrontBit (= Vorderseite der Daten von hinten) entfernt.
                raster.CullMode = CullModeFlags.FrontBit;
                if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _cullPipes[(int)id]) != Result.Success)
                    throw new Exception($"Graphics pipeline ‘{name}’ (Cull) failed.");
                raster.CullMode = CullModeFlags.BackBit;
                if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _cullPipesInv[(int)id]) != Result.Success)
                    throw new Exception($"GraphicsPipeline ‘{name}’ (Cull mirrored) failed.");
            }
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entry);
        }
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            if (_vk.CreateShaderModule(_device, in info, null, out var module) != Result.Success)
                throw new Exception("The ShaderModule could not be created.");
            return module;
        }
    }

    // ------------------------------------------------------------------ Render-Pass / Framebuffer / Sync

    private void CreateRenderPass()
    {
        var finalLayout = _uiViews.Length > 0 ? ImageLayout.ColorAttachmentOptimal : ImageLayout.PresentSrcKhr;
        var colorAttachment = new AttachmentDescription
        {
            Format = _format,
            Samples = _msaa,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = Msaa ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = Msaa ? ImageLayout.ColorAttachmentOptimal : finalLayout
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        // MSAA: Auflösen in das Swapchain-Bild (Anhang 2)
        var resolveAttachment = new AttachmentDescription
        {
            Format = _format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal   // danach Kopie ins Swapchain-Bild
        };
        var resolveRef = new AttachmentReference { Attachment = 2, Layout = ImageLayout.ColorAttachmentOptimal };
        var depthAttachment = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = _msaa,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,                       // AO liest die Tiefe danach (PostFx)
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal   // im Fragment-Shader lesbar
        };
        var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PResolveAttachments = Msaa ? &resolveRef : null,
            PDepthStencilAttachment = &depthRef
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            // Transfer: das Auflösebild (MSAA) wurde im Vorbild noch kopiert
            // FragmentShader: die Tiefe wurde im Vorbild noch von der AO-Nachbearbeitung gelesen
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };
        var attachments = stackalloc AttachmentDescription[3] { colorAttachment, depthAttachment, resolveAttachment };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = Msaa ? 3u : 2u,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };
        if (_vk.CreateRenderPass(_device, in info, null, out _renderPass) != Result.Success)
            throw new Exception("The RenderPass could not be created.");
        if (_uiViews.Length > 0) CreateUiPass();
    }

    /// <summary>Durchgang für Flash/Overlay: UNORM-Sicht des Swapchain-Bildes, Inhalt laden, danach Präsentieren.</summary>
    private void CreateUiPass()
    {
        var color = new AttachmentDescription
        {
            Format = _uiFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.PresentSrcKhr
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };
        if (_vk.CreateRenderPass(_device, in info, null, out _uiPass) != Result.Success)
            throw new Exception("UI-RenderPass could not be created.");
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_imageViews.Length];
        for (int i = 0; i < _imageViews.Length; i++)
        {
            var attachments = stackalloc ImageView[3];
            if (Msaa) { attachments[0] = _msaaView; attachments[1] = _depthView; attachments[2] = _resolveView; }
            else { attachments[0] = _imageViews[i]; attachments[1] = _depthView; }
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = Msaa ? 3u : 2u,
                PAttachments = attachments,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1
            };
            if (_vk.CreateFramebuffer(_device, in info, null, out _framebuffers[i]) != Result.Success)
                throw new Exception("The framebuffer could not be created.");
        }
        _uiFramebuffers = new Framebuffer[_uiViews.Length];
        for (int i = 0; i < _uiViews.Length; i++)
        {
            var v = _uiViews[i];
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _uiPass,
                AttachmentCount = 1,
                PAttachments = &v,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1
            };
            if (_vk.CreateFramebuffer(_device, in info, null, out _uiFramebuffers[i]) != Result.Success)
                throw new Exception("The UI framebuffer could not be created.");
        }
        CreateEnvTargets();   // Bodenspiegelung / Reflexionssonde (Größe, Format und Abtastrate wie die Swapchain)
        CreatePostFx();       // Photoshop-Nachbearbeitung (braucht den UI-Pass und die Bildgröße)
    }

    private void CreateCommandPool()
    {
        var info = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _graphicsFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        if (_vk.CreateCommandPool(_device, in info, null, out _commandPool) != Result.Success)
            throw new Exception("CommandPool could not be created.");
    }

    private void CreateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[MaxFramesInFlight];   // je Bild im Flug (nicht je Swapchain-Bild)
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _commandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = (uint)_commandBuffers.Length };
        fixed (CommandBuffer* p = _commandBuffers)
            if (_vk.AllocateCommandBuffers(_device, in alloc, p) != Result.Success)
                throw new Exception("CommandBuffers could not be allocated.");
    }

    private void CreateSyncObjects()
    {
        _imageAvailable = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        _renderFinished = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        _inFlight = new Fence[MaxFramesInFlight];
        _imagesInFlight = new Fence[_images.Length];
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (int i = 0; i < MaxFramesInFlight; i++)
            if (_vk.CreateSemaphore(_device, in semInfo, null, out _imageAvailable[i]) != Result.Success ||
                _vk.CreateSemaphore(_device, in semInfo, null, out _renderFinished[i]) != Result.Success ||
                _vk.CreateFence(_device, in fenceInfo, null, out _inFlight[i]) != Result.Success)
                throw new Exception("Sync objects could not be created.");
    }

    // ------------------------------------------------------------------ Recreate / Cleanup

    private void RecreateSwapchain()
    {
        if (_disposed) return;
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physical, _surface, out var caps);
        uint cw = caps.CurrentExtent.Width, ch = caps.CurrentExtent.Height;
        if (cw != uint.MaxValue && (cw == 0 || ch == 0)) return;
        if (_width == 0 || _height == 0) return;

        _vk.DeviceWaitIdle(_device);
        CleanupSwapchain();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateRenderPass();
        CreatePipelines();
        CreateFramebuffers();
        CreateCommandBuffers();
        _imagesInFlight = new Fence[_images.Length];
    }

    private void CleanupSwapchain()
    {
        DestroyPostFx();
        DestroyEnvTargets();
        foreach (var fb in _framebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        foreach (var fb in _uiFramebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        _uiFramebuffers = Array.Empty<Framebuffer>();
        fixed (CommandBuffer* p = _commandBuffers)
            _vk.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, p);
        for (int i = 0; i < _pipelines.Length; i++)
        {
            _vk.DestroyPipeline(_device, _pipelines[i], null);
            if (_cullPipes[i].Handle != 0) { _vk.DestroyPipeline(_device, _cullPipes[i], null); _cullPipes[i] = default; }
            if (_cullPipesInv[i].Handle != 0) { _vk.DestroyPipeline(_device, _cullPipesInv[i], null); _cullPipesInv[i] = default; }
            _vk.DestroyPipelineLayout(_device, _layouts[i], null);
        }
        _vk.DestroyRenderPass(_device, _renderPass, null);
        if (_uiPass.Handle != 0) { _vk.DestroyRenderPass(_device, _uiPass, null); _uiPass = default; }
        foreach (var v in _uiViews) _vk.DestroyImageView(_device, v, null);
        _uiViews = Array.Empty<ImageView>();
        _vk.DestroyImageView(_device, _depthView, null);
        _vk.DestroyImage(_device, _depthImage, null);
        _alloc.Free(_depthMem);
        if (_msaaView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _msaaView, null); _msaaView = default;
            _vk.DestroyImage(_device, _msaaImage, null); _msaaImage = default;
            _alloc.Free(_msaaMem);
        }
        if (_resolveView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _resolveView, null); _resolveView = default;
            _vk.DestroyImage(_device, _resolveImage, null); _resolveImage = default;
            _alloc.Free(_resolveMem);
        }
        foreach (var v in _imageViews) _vk.DestroyImageView(_device, v, null);
        _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
    }

    public void Dispose()
    {
        if (_disposed || _vk is null) return;
        StopShadowThread();
        _vk.DeviceWaitIdle(_device);
        foreach (var o in _live.ToArray()) o.Dispose();
        RunPendingDestroys(long.MaxValue);
        DestroySceneCapture();
        _disposed = true;

        CleanupSwapchain();
        DestroyShadowResources();
        DestroyEnvUbos();
        DestroyTimestamps();
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _vk.DestroySemaphore(_device, _imageAvailable[i], null);
            _vk.DestroySemaphore(_device, _renderFinished[i], null);
            _vk.DestroyFence(_device, _inFlight[i], null);
            _vk.DestroyBuffer(_device, _frameUbo[i], null);
            _vk.DestroyBuffer(_device, _instBuf[i], null);
        }
        foreach (var s in _samplers.Values) _vk.DestroySampler(_device, s, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _simpleSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _terrainSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _objectSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _frameSetLayout, null);
        _alloc.Dispose();
        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyCommandPool(_device, _shadowPool, null);
        _vk.DestroyDevice(_device, null);
        _khrSurface.DestroySurface(_instance, _surface, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}