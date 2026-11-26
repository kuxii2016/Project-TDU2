using Paradise.Core.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Photoshop-Nachbearbeitung des Originals (Docs\re\postfx_spec.md §2/§3, Werte: <see cref="PhotoshopFx"/>, Shader:
/// <see cref="PostFxShaders"/>). Läuft nach dem Weltdurchgang und vor dem HUD/Flash (Original: nach "WHEELS RADIAL BLUR",
/// vor Before2D/After2D, §1.5).
///
/// Ablauf je Bild (nur mit UI-Durchgang, d.h. UNORM-Sicht der Swapchain vorhanden):
///   <see cref="RecordPhotoshop"/> – außerhalb eines Render-Passes:
///     a) fertiges Weltbild (Swapchain-Bild bzw. MSAA-Auflösebild) per Bytekopie nach _pfxScene (UNORM, lesbar),
///     b) _pfxScene → _pfxQ1   Viertel (DownsampleFrag),
///     c) _pfxQ1    → _pfxQ2   Gauß waagerecht, d) _pfxQ2 → _pfxQ1 Gauß senkrecht (BlurFrag),
///     e) LUT 256×256          (LutFrag).
///   <see cref="DrawPhotoshopApply"/> – erster Aufruf im UI-Durchgang: überschreibt das ganze Bild mit LUT(Szene) (ApplyFrag),
///     danach zeichnet Flash darüber.
///
/// Zusatz der Engine (nicht im Original, <see cref="PostFx"/>): im selben Ablauf vor b) AO aus der Tiefe (halbe Auflösung,
/// _pfxAoA/_pfxAoB), God Rays aus Helligkeit + großer Tiefe (Viertel, _pfxGod), volumetrisches Licht des Spieler-Scheinwerfers
/// (Viertel, _pfxVolA/_pfxVolB), Sonnen-Volumetrie als Ersatz für die God Rays (derselbe _pfxGod-Puffer) und Bloom
/// (Viertel, _pfxBloomA/_pfxBloomB); <see cref="DrawPhotoshopApply"/> mischt AO/God/Fog/Volumen vor der Tabelle und
/// Bloom danach per Flags ein. Läuft auch, wenn Photoshop aus ist (dann ohne Farbtabelle). Beide Sonneneffekte schließen
/// sich aus, damit die Sonne nicht doppelt addiert wird. Höhenfog kommt direkt im Apply aus der Tiefe, ohne eigenes Ziel.
///
/// Alle Ziele hängen an der Swapchain (Größe, Format, UI-Pass) und werden mit ihr angelegt (<see cref="CreatePostFx"/>, aus
/// CreateFramebuffers) und freigegeben (<see cref="DestroyPostFx"/>, aus CleanupSwapchain).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>LUT-Größe: 0x100 >> "Lut Downscale" (0) = 256 (§2.1).</summary>
    private const uint PfxLutSize = 256;

    /// <summary>Push-Constants aller Photoshop-Durchgänge (Aufbau siehe <see cref="PostFxShaders"/>).</summary>
    private struct PfxPush
    {
        public System.Numerics.Vector4 Vars, Params, Shadows, Midtones, Hightones, Bloom, Gamma;
    }

    /// <summary>Set-0-Binding 7: Scheinwerfer, Sonne und Kameraachsen für die Volumen-Raymarches (std140, 192 Byte).</summary>
    private struct PfxVolumeUbo
    {
        public System.Numerics.Vector4 PosNear;    // xyz = Projektorposition, w = Near
        public System.Numerics.Vector4 DirStrength; // xyz = Richtung, w = Stärke
        public System.Numerics.Vector4 UpCot;      // xyz = Oben, w = cot(Außenwinkel/2)
        public System.Numerics.Vector4 Att;        // cut*ay, ay, -cos(außen)*aw, aw
        public System.Numerics.Vector4 Right;
        public System.Numerics.Vector4 CamUp;
        public System.Numerics.Vector4 Fwd;
        public System.Numerics.Vector4 CamPos;
        public System.Numerics.Vector4 SunDir;     // xyz = Streu-Sonne, w = Stärke
        public System.Numerics.Vector4 SunColor;   // rgb = Sonnenfarbe (Gamma), w = Tagesmaske
        public System.Numerics.Vector4 SunPhase;   // x = 1−g², y = 1+g², z = 2g, w = Phasen-Grenze
        public System.Numerics.Vector4 SunRange;   // x = max. Distanz, y = Fade-Start, z = Jitter, w = unbenutzt
    }

    private bool _pfxReady;                 // alles angelegt (sonst bleibt der Durchgang aus)
    private bool _swapTransferSrc;          // Swapchain-Bilder lassen sich kopieren (nötig ohne MSAA)
    private bool _pfxCrappyLogged;
    private bool _pfxSunVol;                // Sonnen-Volumetrie im letzten Bild (bestimmt den Apply-Multiplikator)
    private uint _pfxQW, _pfxQH;            // Viertelgröße (W>>2, H>>2)

    private Image _pfxScene, _pfxQ1, _pfxQ2, _pfxLut;
    private GpuAllocator.Allocation _pfxSceneMem, _pfxQ1Mem, _pfxQ2Mem, _pfxLutMem;
    private ImageView _pfxSceneView, _pfxQ1View, _pfxQ2View, _pfxLutView;
    private Framebuffer _pfxQ1Fb, _pfxQ2Fb, _pfxLutFb;

    // Zusatz Bloom (Viertel), God Rays (Viertel), Volumen (Viertel) und AO (halbe Größe), PostFx
    private uint _pfxAoW, _pfxAoH;
    private Image _pfxBloomA, _pfxBloomB, _pfxAoA, _pfxAoB, _pfxGod, _pfxVolA, _pfxVolB;
    private GpuAllocator.Allocation _pfxBloomAMem, _pfxBloomBMem, _pfxAoAMem, _pfxAoBMem, _pfxGodMem, _pfxVolAMem, _pfxVolBMem;
    private ImageView _pfxBloomAView, _pfxBloomBView, _pfxAoAView, _pfxAoBView, _pfxGodView, _pfxVolAView, _pfxVolBView;
    private Framebuffer _pfxBloomAFb, _pfxBloomBFb, _pfxAoAFb, _pfxAoBFb, _pfxGodFb, _pfxVolAFb, _pfxVolBFb;
    private Pipeline _pfxBright, _pfxAo, _pfxGodRay, _pfxVolume, _pfxSunVolume;
    private Sampler _pfxDepthSampler;
    private Silk.NET.Vulkan.Buffer _pfxVolUbo;
    private GpuAllocator.Allocation _pfxVolUboMem;
    private DescriptorSet _pfxSetBright, _pfxSetBloomH, _pfxSetBloomV, _pfxSetAo, _pfxSetAoH, _pfxSetAoV, _pfxSetGod, _pfxSetGodH, _pfxSetVol, _pfxSetVolH, _pfxSetVolV;
    private int _pfxFlags;                  // 1 Photoshop, 2 Bloom, 4 AO, 64 God Rays, 128 Fog, 1024 Volumen (dieses Bild)
    private long _pfxAoLogFrame = -100000;  // Diagnose AO (alle 600 Bilder, solange eingeschaltet)

    private RenderPass _pfxPass;            // ein Farbziel, Ergebnis danach im Fragment-Shader lesbar
    private Sampler _pfxSampler;
    private DescriptorSetLayout _pfxSetLayout;
    private PipelineLayout _pfxLayout;
    private Pipeline _pfxDown, _pfxBlur, _pfxLutPipe, _pfxApply;
    private DescriptorPool _pfxPool;
    private DescriptorSet _pfxSetDown, _pfxSetBlurH, _pfxSetBlurV, _pfxSetApply;

    // ------------------------------------------------------------------ Anlegen / Freigeben

    /// <summary>Ziele, Render-Pass, Pipelines und Sets der Nachbearbeitung (mit der Swapchain).</summary>
    private void CreatePostFx()
    {
        _pfxReady = false;
        if (_uiPass.Handle == 0) return;                    // ohne UNORM-Sicht kein Durchgang
        if (!Msaa && !_swapTransferSrc)
        {
            Log.Warn("Photoshop Post-Processing: Swapchain images cannot be copied (TransferSrc) – Process complete.");
            return;
        }
        uint w = _extent.Width, h = _extent.Height;
        _pfxQW = System.Math.Max(1u, w >> 2);
        _pfxQH = System.Math.Max(1u, h >> 2);
        var fmt = _uiFormat;                                // 8 Bit UNORM wie A8R8G8B8 im Original (§2.2)

        CreateImage(w, h, 1, fmt, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, out _pfxScene, out _pfxSceneMem);
        _pfxSceneView = CreateImageView(_pfxScene, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _pfxQ1, out _pfxQ1Mem);
        _pfxQ1View = CreateImageView(_pfxQ1, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _pfxQ2, out _pfxQ2Mem);
        _pfxQ2View = CreateImageView(_pfxQ2, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(PfxLutSize, PfxLutSize, 1, fmt, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _pfxLut, out _pfxLutMem);
        _pfxLutView = CreateImageView(_pfxLut, fmt, ImageAspectFlags.ColorBit, 1);
        _pfxAoW = System.Math.Max(1u, w >> 1);
        _pfxAoH = System.Math.Max(1u, h >> 1);
        var rt = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        CreateImage(_pfxQW, _pfxQH, 1, fmt, rt, out _pfxBloomA, out _pfxBloomAMem);
        _pfxBloomAView = CreateImageView(_pfxBloomA, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, rt, out _pfxBloomB, out _pfxBloomBMem);
        _pfxBloomBView = CreateImageView(_pfxBloomB, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxAoW, _pfxAoH, 1, fmt, rt, out _pfxAoA, out _pfxAoAMem);
        _pfxAoAView = CreateImageView(_pfxAoA, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxAoW, _pfxAoH, 1, fmt, rt, out _pfxAoB, out _pfxAoBMem);
        _pfxAoBView = CreateImageView(_pfxAoB, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, rt, out _pfxGod, out _pfxGodMem);
        _pfxGodView = CreateImageView(_pfxGod, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, rt, out _pfxVolA, out _pfxVolAMem);
        _pfxVolAView = CreateImageView(_pfxVolA, fmt, ImageAspectFlags.ColorBit, 1);
        CreateImage(_pfxQW, _pfxQH, 1, fmt, rt, out _pfxVolB, out _pfxVolBMem);
        _pfxVolBView = CreateImageView(_pfxVolB, fmt, ImageAspectFlags.ColorBit, 1);

        CreatePfxPass(fmt);
        _pfxQ1Fb = CreatePfxFramebuffer(_pfxQ1View, _pfxQW, _pfxQH);
        _pfxQ2Fb = CreatePfxFramebuffer(_pfxQ2View, _pfxQW, _pfxQH);
        _pfxLutFb = CreatePfxFramebuffer(_pfxLutView, PfxLutSize, PfxLutSize);
        _pfxBloomAFb = CreatePfxFramebuffer(_pfxBloomAView, _pfxQW, _pfxQH);
        _pfxBloomBFb = CreatePfxFramebuffer(_pfxBloomBView, _pfxQW, _pfxQH);
        _pfxAoAFb = CreatePfxFramebuffer(_pfxAoAView, _pfxAoW, _pfxAoH);
        _pfxAoBFb = CreatePfxFramebuffer(_pfxAoBView, _pfxAoW, _pfxAoH);
        _pfxGodFb = CreatePfxFramebuffer(_pfxGodView, _pfxQW, _pfxQH);
        _pfxVolAFb = CreatePfxFramebuffer(_pfxVolAView, _pfxQW, _pfxQH);
        _pfxVolBFb = CreatePfxFramebuffer(_pfxVolBView, _pfxQW, _pfxQH);

        // Projektor + Kameraachsen des Volumenpasses (ein UBO, einmal je Bild beschrieben)
        CreateHostBuffer((ulong)sizeof(PfxVolumeUbo), BufferUsageFlags.UniformBufferBit, out _pfxVolUbo, out _pfxVolUboMem);
        *(PfxVolumeUbo*)_pfxVolUboMem.Mapped = default;

        // Linear, Rand geklemmt (D3D9-Standard der PFX-Materialien)
        var si = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f
        };
        if (_vk.CreateSampler(_device, in si, null, out _pfxSampler) != Result.Success) throw new Exception("Photoshop: Sampler fehlgeschlagen.");
        // Tiefe (AO): nur texelFetch, ohne Filter
        si.MagFilter = Filter.Nearest; si.MinFilter = Filter.Nearest;
        if (_vk.CreateSampler(_device, in si, null, out _pfxDepthSampler) != Result.Success) throw new Exception("PostFx: Tiefen-Sampler fehlgeschlagen.");

        // set 0: binding 0 sA, 1 sB, 2 sLut, 3 sBloom, 4 sAo, 5 sGod, 6 sDepth, 7 Volumen-UBO, 8 sVol
        var bindings = stackalloc DescriptorSetLayoutBinding[9];
        for (uint i = 0; i < 9; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = i == 7 ? DescriptorType.UniformBuffer : DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            };
        var sl = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 9, PBindings = bindings };
        if (_vk.CreateDescriptorSetLayout(_device, in sl, null, out _pfxSetLayout) != Result.Success) throw new Exception("Photoshop: SetLayout fehlgeschlagen.");
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.FragmentBit, Offset = 0, Size = (uint)sizeof(PfxPush) };
        var setLayout = _pfxSetLayout;
        var li = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (_vk.CreatePipelineLayout(_device, in li, null, out _pfxLayout) != Result.Success) throw new Exception("Photoshop: PipelineLayout fehlgeschlagen.");

        using (var c = new ShaderCompiler())
        {
            _pfxDown = CreatePfxPipeline(c, "PfxDownsample", PostFxShaders.DownsampleFrag, _pfxPass);
            _pfxBlur = CreatePfxPipeline(c, "PfxBlur", PostFxShaders.BlurFrag, _pfxPass);
            _pfxLutPipe = CreatePfxPipeline(c, "PfxLut", PostFxShaders.LutFrag, _pfxPass);
            _pfxApply = CreatePfxPipeline(c, "PfxApply", PostFxShaders.ApplySource(Msaa), _uiPass);
            _pfxBright = CreatePfxPipeline(c, "PfxBright", PostFxShaders.BrightFrag, _pfxPass);
            _pfxAo = CreatePfxPipeline(c, "PfxAo", PostFxShaders.AoSource(Msaa), _pfxPass);
            _pfxGodRay = CreatePfxPipeline(c, "PfxGodRay", PostFxShaders.GodRaySource(Msaa), _pfxPass);
            _pfxVolume = CreatePfxPipeline(c, "PfxVolume", PostFxShaders.VolumeSource(Msaa), _pfxPass);
            _pfxSunVolume = CreatePfxPipeline(c, "PfxSunVolume", PostFxShaders.SunVolumeSource(Msaa), _pfxPass);
        }

        var poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 128 },
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = 16 }
        };
        var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 16, PoolSizeCount = 2, PPoolSizes = poolSizes };
        if (_vk.CreateDescriptorPool(_device, in pi, null, out _pfxPool) != Result.Success) throw new Exception("Photoshop: DescriptorPool fehlgeschlagen.");
        // Eingänge je Durchgang (unbenutzte Plätze mit irgendeinem gültigen Bild belegt)
        _pfxSetDown = CreatePfxSet(_pfxSceneView, _pfxSceneView, _pfxSceneView);
        _pfxSetBlurH = CreatePfxSet(_pfxQ1View, _pfxQ1View, _pfxQ1View);
        _pfxSetBlurV = CreatePfxSet(_pfxQ2View, _pfxQ2View, _pfxQ2View);
        _pfxSetApply = CreatePfxSet(_pfxSceneView, _pfxQ1View, _pfxLutView, _pfxBloomAView, _pfxAoAView, god: _pfxGodView, depth: _depthView, vol: _pfxVolAView);
        _pfxSetBright = CreatePfxSet(_pfxSceneView, _pfxSceneView, _pfxSceneView);
        _pfxSetBloomH = CreatePfxSet(_pfxBloomAView, _pfxBloomAView, _pfxBloomAView);
        _pfxSetBloomV = CreatePfxSet(_pfxBloomBView, _pfxBloomBView, _pfxBloomBView);
        _pfxSetAo = CreatePfxSet(_depthView, _pfxSceneView, _pfxSceneView, depthIn: true);
        _pfxSetAoH = CreatePfxSet(_pfxAoAView, _pfxAoAView, _pfxAoAView);
        _pfxSetAoV = CreatePfxSet(_pfxAoBView, _pfxAoBView, _pfxAoBView);
        _pfxSetGod = CreatePfxSet(_pfxSceneView, _pfxSceneView, _pfxSceneView, depth: _depthView);
        _pfxSetGodH = CreatePfxSet(_pfxGodView, _pfxGodView, _pfxGodView);   // waagerechter Gauß der Sonnen-Volumetrie
        _pfxSetVol = CreatePfxSet(_pfxSceneView, _pfxSceneView, _pfxSceneView, depth: _depthView);
        _pfxSetVolH = CreatePfxSet(_pfxVolAView, _pfxVolAView, _pfxVolAView);
        _pfxSetVolV = CreatePfxSet(_pfxVolBView, _pfxVolBView, _pfxVolBView);

        // Zwischenziele einmal lesbar machen: das Anwenden bindet alle (auch die, die in einem Bild nicht gerendert werden)
        var initCmd = BeginSingleTimeCommands();
        var init = stackalloc ImageMemoryBarrier[10];
        var imgs = new[] { _pfxQ1, _pfxQ2, _pfxLut, _pfxBloomA, _pfxBloomB, _pfxAoA, _pfxAoB, _pfxGod, _pfxVolA, _pfxVolB };
        for (int i = 0; i < imgs.Length; i++)
            init[i] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                Image = imgs[i],
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ShaderReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored
            };
        _vk.CmdPipelineBarrier(initCmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, (uint)imgs.Length, init);
        EndSingleTimeCommands(initCmd);

        _pfxReady = true;
    }

    private void DestroyPostFx()
    {
        _pfxReady = false;
        if (_pfxPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _pfxPool, null); _pfxPool = default; }
        foreach (var p in new[] { _pfxDown, _pfxBlur, _pfxLutPipe, _pfxApply, _pfxBright, _pfxAo, _pfxGodRay, _pfxVolume, _pfxSunVolume })
            if (p.Handle != 0) _vk.DestroyPipeline(_device, p, null);
        _pfxDown = _pfxBlur = _pfxLutPipe = _pfxApply = _pfxBright = _pfxAo = _pfxGodRay = _pfxVolume = _pfxSunVolume = default;
        if (_pfxLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _pfxLayout, null); _pfxLayout = default; }
        if (_pfxSetLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _pfxSetLayout, null); _pfxSetLayout = default; }
        if (_pfxSampler.Handle != 0) { _vk.DestroySampler(_device, _pfxSampler, null); _pfxSampler = default; }
        if (_pfxDepthSampler.Handle != 0) { _vk.DestroySampler(_device, _pfxDepthSampler, null); _pfxDepthSampler = default; }
        foreach (var fb in new[] { _pfxQ1Fb, _pfxQ2Fb, _pfxLutFb, _pfxBloomAFb, _pfxBloomBFb, _pfxAoAFb, _pfxAoBFb, _pfxGodFb, _pfxVolAFb, _pfxVolBFb })
            if (fb.Handle != 0) _vk.DestroyFramebuffer(_device, fb, null);
        _pfxQ1Fb = _pfxQ2Fb = _pfxLutFb = _pfxBloomAFb = _pfxBloomBFb = _pfxAoAFb = _pfxAoBFb = _pfxGodFb = _pfxVolAFb = _pfxVolBFb = default;
        if (_pfxPass.Handle != 0) { _vk.DestroyRenderPass(_device, _pfxPass, null); _pfxPass = default; }
        FreePfxImage(ref _pfxScene, ref _pfxSceneView, _pfxSceneMem);
        FreePfxImage(ref _pfxQ1, ref _pfxQ1View, _pfxQ1Mem);
        FreePfxImage(ref _pfxQ2, ref _pfxQ2View, _pfxQ2Mem);
        FreePfxImage(ref _pfxLut, ref _pfxLutView, _pfxLutMem);
        FreePfxImage(ref _pfxBloomA, ref _pfxBloomAView, _pfxBloomAMem);
        FreePfxImage(ref _pfxBloomB, ref _pfxBloomBView, _pfxBloomBMem);
        FreePfxImage(ref _pfxAoA, ref _pfxAoAView, _pfxAoAMem);
        FreePfxImage(ref _pfxAoB, ref _pfxAoBView, _pfxAoBMem);
        FreePfxImage(ref _pfxGod, ref _pfxGodView, _pfxGodMem);
        FreePfxImage(ref _pfxVolA, ref _pfxVolAView, _pfxVolAMem);
        FreePfxImage(ref _pfxVolB, ref _pfxVolBView, _pfxVolBMem);
        if (_pfxVolUbo.Handle != 0) { FreeBuffer(_pfxVolUbo, _pfxVolUboMem); _pfxVolUbo = default; _pfxVolUboMem = default; }
    }

    private void FreePfxImage(ref Image image, ref ImageView view, GpuAllocator.Allocation mem)
    {
        if (view.Handle != 0) _vk.DestroyImageView(_device, view, null);
        if (image.Handle != 0) { _vk.DestroyImage(_device, image, null); _alloc.Free(mem); }
        image = default; view = default;
    }

    /// <summary>Render-Pass der Zwischenziele: Inhalt wird ganz überschrieben, danach im Fragment-Shader gelesen.</summary>
    private void CreatePfxPass(Format fmt)
    {
        var color = new AttachmentDescription
        {
            Format = fmt,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var deps = stackalloc SubpassDependency[2];
        // vorher: Ziel wurde zuletzt im Fragment-Shader gelesen (voriger Durchgang / voriges Bild)
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        // nachher: der nächste Durchgang liest das Ergebnis
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps
        };
        if (_vk.CreateRenderPass(_device, in info, null, out _pfxPass) != Result.Success) throw new Exception("Photoshop: RenderPass fehlgeschlagen.");
    }

    private Framebuffer CreatePfxFramebuffer(ImageView view, uint w, uint h)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _pfxPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = w,
            Height = h,
            Layers = 1
        };
        if (_vk.CreateFramebuffer(_device, in info, null, out var fb) != Result.Success) throw new Exception("Photoshop: Framebuffer fehlgeschlagen.");
        return fb;
    }

    /// <summary>Vollbild-Pipeline ohne Vertexpuffer, Tiefe und Mischen (jeder Durchgang überschreibt sein Ziel ganz).</summary>
    private Pipeline CreatePfxPipeline(ShaderCompiler compiler, string name, string fragSrc, RenderPass pass)
    {
        ShaderModule vert = CreateShaderModule(compiler.Compile(PostFxShaders.FullscreenVert, ShaderKind.VertexShader, name + ".vert"));
        ShaderModule frag = CreateShaderModule(compiler.Compile(fragSrc, ShaderKind.FragmentShader, name + ".frag"));
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vert, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = frag, PName = entry };
            var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.Clockwise
            };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
            var blendAtt = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAtt };
            var dynStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _pfxLayout,
                RenderPass = pass,
                Subpass = 0
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out var pipe) != Result.Success)
                throw new Exception($"Photoshop: Pipeline '{name}' fehlgeschlagen.");
            return pipe;
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vert, null);
            _vk.DestroyShaderModule(_device, frag, null);
            SilkMarshal.Free((nint)entry);
        }
    }

    /// <param name="bloom">binding 3 (null = a)</param><param name="ao">binding 4 (null = a)</param>
    /// <param name="god">binding 5 (null = a)</param><param name="depth">binding 6: Welttiefe, DepthStencilReadOnlyOptimal</param>
    /// <param name="vol">binding 8 (null = a); binding 7 ist immer das Volumen-UBO</param>
    /// <param name="depthIn">a ist die Tiefe des Weltdurchgangs (DepthStencilReadOnlyOptimal, Sampler ohne Filter)</param>
    private DescriptorSet CreatePfxSet(ImageView a, ImageView b, ImageView lut, ImageView? bloom = null, ImageView? ao = null,
        ImageView? god = null, ImageView? depth = null, ImageView? vol = null, bool depthIn = false)
    {
        var layout = _pfxSetLayout;
        var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pfxPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        if (_vk.AllocateDescriptorSets(_device, in ai, out var set) != Result.Success) throw new Exception("Photoshop: DescriptorSet fehlgeschlagen.");
        // Unbenutzte Plätze mit einem gültigen Farbbild belegen (gleicher Layout-Typ wie b)
        var fill = depthIn ? b : a;
        var infos = stackalloc DescriptorImageInfo[8]
        {
            depthIn
                ? new DescriptorImageInfo { Sampler = _pfxDepthSampler, ImageView = a, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal }
                : new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = a, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = b, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = lut, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = bloom ?? fill, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = ao ?? fill, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = god ?? fill, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            depth != null
                ? new DescriptorImageInfo { Sampler = _pfxDepthSampler, ImageView = depth.Value, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal }
                : new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = fill, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
            new DescriptorImageInfo { Sampler = _pfxSampler, ImageView = vol ?? fill, ImageLayout = ImageLayout.ShaderReadOnlyOptimal }
        };
        // binding 0..6 und 8 sind Bilder, 7 ist der UBO
        var bufInfo = new DescriptorBufferInfo { Buffer = _pfxVolUbo, Offset = 0, Range = (ulong)sizeof(PfxVolumeUbo) };
        var writes = stackalloc WriteDescriptorSet[9];
        for (int i = 0; i < 9; i++)
        {
            if (i == 7)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = 7,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &bufInfo
                };
            }
            else
            {
                int img = i < 7 ? i : i - 1;
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &infos[img]
                };
            }
        }
        _vk.UpdateDescriptorSets(_device, 9, writes, 0, null);
        return set;
    }

    // ------------------------------------------------------------------ Je Bild

    /// <summary>
    /// Schritte a)–e) aufzeichnen. Aufruf nach dem Weltdurchgang (und bei MSAA nach CopyResolveToSwapchain), vor dem UI-Pass.
    /// Rückgabe: true = im UI-Pass muss <see cref="DrawPhotoshopApply"/> folgen.
    /// </summary>
    private bool RecordPhotoshop(CommandBuffer cmd)
    {
        if (!_pfxReady) { Ts(cmd, 4); Ts(cmd, 5); return false; }
        bool ps = PhotoshopFx.Enabled;
        bool bloom = PostFx.BloomEnabled && PostFx.BloomIntensity > 0f;
        // Tiefenbasierte Effekte nur mit Perspektive (große Karte: NoSky, eigene Kamera) und gültiger Tiefe
        bool perspective = !_globals.NoSky && _proj.M34 < -0.5f && MathF.Abs(_proj.M43) > 1e-6f;
        bool ao = PostFx.AoEnabled && PostFx.AoIntensity > 0f && perspective;
        bool fog = PostFx.FogEnabled && PostFx.FogIntensity > 0f && perspective;
        bool god = PostFx.GodRaysEnabled && PostFx.GodIntensity > 0f && perspective;
        // Sonnen-Volumetrie ersetzt die klassischen God Rays: sonst würde die Sonne im Apply doppelt addiert.
        bool sunVol = PostFx.SunVolumetricEnabled && PostFx.SunVolumetricIntensity > 0f && perspective
                      && !Paradise.Core.World.WorldWeather.Inside && _globals.DayDim > 0.25f
                      && _globals.SunDirection.Y > -0.05f && _globals.SunColor.LengthSquared() > 1e-6f;
        if (sunVol) god = false;
        _pfxSunVol = sunVol;
        PostFx.SunVolumetricActive = sunVol;
        // Eigener PostFX-Projektor des Spielers (Mindeststärke 1), unabhängig vom Original-Lichtpuffer.
        var playerProj = _globals.PlayerProjector;
        bool vol = PostFx.VolumetricEnabled && PostFx.VolumetricIntensity > 0f && perspective
                   && playerProj.HasValue && playerProj.Value.Strength > 0f;
        PostFx.VolumetricProjectorAvailable = playerProj.HasValue;
        PostFx.VolumetricProjectorStrength = playerProj.HasValue ? playerProj.Value.Strength : 0f;
        if (PostFx.AoEnabled && _frameCounter - _pfxAoLogFrame >= 600)
        {
            _pfxAoLogFrame = _frameCounter;
        }
        // Sonne in Bildschirmkoordinaten. Vulkan-NDC-Y zeigt nach unten -> keine Umkehrung wie bei D3D/Unity.
        var sun = new System.Numerics.Vector2(0.5f, 0.5f);
        float sunValid = 0f;
        if (god && _globals.SunDirection.Y > -0.05f && _globals.SunDirection.LengthSquared() > 1e-6f)
        {
            var dir = System.Numerics.Vector3.Normalize(_globals.SunDirection);
            var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(dir, 0f), _viewProj);
            if (clip.W > 1e-6f)
            {
                float u = clip.X / clip.W * 0.5f + 0.5f;
                float v = clip.Y / clip.W * 0.5f + 0.5f;
                if (u > -0.25f && u < 1.25f && v > -0.25f && v < 1.25f)
                {
                    sun = new System.Numerics.Vector2(u, v);
                    sunValid = 1f;   // leicht außerhalb des Bilds dürfen Strahlen von der Kante kommen
                }
            }
        }
        if (sunValid <= 0f) god = false;
        // Flag 64 = sGod: klassische God Rays oder Sonnen-Volumetrie (beide teilen sich denselben Puffer).
        _pfxFlags = (ps ? 1 : 0) | (bloom ? 2 : 0) | (ao ? 4 : 0) | ((god || sunVol) ? 64 : 0) | (fog ? 128 : 0) | (vol ? 1024 : 0);
        if (_pfxFlags == 0) { Ts(cmd, 4); Ts(cmd, 5); return false; }
        if (PostFx.DebugView == 1 && ao) _pfxFlags |= 8;          // nur AO-Bild
        else if (PostFx.DebugView == 2 && bloom) _pfxFlags |= 16; // nur Bloom-Bild
        else if (PostFx.DebugView == 3) _pfxFlags |= 32;          // nur Bloom-Maske (Alpha des Weltbilds)
        else if (PostFx.DebugView == 4 && (god || sunVol)) _pfxFlags |= 256;  // nur God-/Sonnenpuffer
        else if (PostFx.DebugView == 5 && fog) _pfxFlags |= 512;  // nur Nebelanteil
        else if (PostFx.DebugView == 6 && vol) _pfxFlags |= 2048; // nur Volumen-Puffer
        if (ps && PhotoshopFx.CrappyMode && !_pfxCrappyLogged)
        {
            _pfxCrappyLogged = true;
        }

        CopySceneForPhotoshop(cmd);
        if (ao || god || fog || vol || sunVol) PfxDepthBarrier(cmd);
        if (ao) RecordAo(cmd);
        if (god) RecordGod(cmd, sun);
        // Auch wenn kein Volumen läuft, beide Zeitstempel schreiben: sonst sind die Queries "nicht verfügbar".
        Ts(cmd, 4);
        if (vol || sunVol)
        {
            UpdateVolumeUbo(sunVol);
            if (vol) RecordVolumetric(cmd);
            if (sunVol) RecordSunVolumetric(cmd);
        }
        Ts(cmd, 5);   // "GPU Volumetric" = t4 → t5
        if (bloom) RecordBloom(cmd);
        if (!ps) return true;

        // b) Viertel: vars = (1/rw, 1/rh, 0.25/rw, 0.25/rh) – §2.3.1
        float qx = 1f / _pfxQW, qy = 1f / _pfxQH;
        var pc = new PfxPush { Vars = new(qx, qy, 0.25f * qx, 0.25f * qy) };
        PfxPass(cmd, _pfxQ1Fb, _pfxQW, _pfxQH, _pfxDown, _pfxSetDown, pc);

        // c/d) Gauß Methode 1, Sigma 1.0 (§3): w(x) = exp(-x²/(2σ²)), normiert über w0 + 2(w1+w2)
        const float sigma = 1f;
        float w0 = 1f, w1 = MathF.Exp(-1f / (2f * sigma * sigma)), w2 = MathF.Exp(-4f / (2f * sigma * sigma));
        float n = w0 + 2f * (w1 + w2);
        var weights = new System.Numerics.Vector4(w0 / n, w1 / n, w2 / n, 0f);   // (0.402620, 0.244201, 0.054489)
        pc = new PfxPush { Vars = new(qx, qy, qx, 0f), Params = weights };
        PfxPass(cmd, _pfxQ2Fb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetBlurH, pc);
        pc = new PfxPush { Vars = new(qx, qy, 0f, qy), Params = weights };
        PfxPass(cmd, _pfxQ1Fb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetBlurV, pc);

        // e) LUT mit den Werten dieses Bildes (§2.3.4: Unterschied zum Original höchstens ein Bild)
        PfxPass(cmd, _pfxLutFb, PfxLutSize, PfxLutSize, _pfxLutPipe, default, LutPush());
        return true;
    }

    /// <summary>Gauß-Gewichte Sigma 1 (wie Photoshop, §3).</summary>
    private static System.Numerics.Vector4 PfxGauss()
    {
        float w0 = 1f, w1 = MathF.Exp(-0.5f), w2 = MathF.Exp(-2f);
        float n = w0 + 2f * (w1 + w2);
        return new System.Numerics.Vector4(w0 / n, w1 / n, w2 / n, 0f);
    }

    /// <summary>Zusatz Bloom (PostFx): helle Stellen auf ein Viertel, dann zweimal Gauß waagerecht/senkrecht mit wachsender Weite.</summary>
    private void RecordBloom(CommandBuffer cmd)
    {
        float qx = 1f / _pfxQW, qy = 1f / _pfxQH;
        var pc = new PfxPush { Vars = new(qx, qy, 0.25f * qx, 0.25f * qy), Params = new(PostFx.BloomThreshold, PostFx.BloomKnee, PostFx.BloomEmissive, PostFx.BloomEmissiveThreshold) };
        PfxPass(cmd, _pfxBloomAFb, _pfxQW, _pfxQH, _pfxBright, _pfxSetBright, pc);
        var g = PfxGauss();
        float r = System.Math.Clamp(PostFx.BloomRadius, 0.25f, 8f);
        for (int it = 0; it < 2; it++)
        {
            float step = r * (it == 0 ? 1f : 2.5f);
            PfxPass(cmd, _pfxBloomBFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetBloomH, new PfxPush { Vars = new(qx, qy, qx * step, 0f), Params = g });
            PfxPass(cmd, _pfxBloomAFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetBloomV, new PfxPush { Vars = new(qx, qy, 0f, qy * step), Params = g });
        }
    }

    /// <summary>Tiefe: Schreiben im Weltdurchgang -> Lesen im Fragment-Shader (Layout bleibt DepthStencilReadOnlyOptimal).
    /// Einmal je Bild, wenn AO, God Rays, Nebel oder das Volumen die Tiefe brauchen.</summary>
    private void PfxDepthBarrier(CommandBuffer cmd)
    {
        var b = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _depthImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            OldLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, &b);
    }

    /// <summary>Zusatz AO (PostFx): Tiefe des Weltdurchgangs -> _pfxAoA (halbe Größe), danach Gauß waagerecht/senkrecht.</summary>
    private void RecordAo(CommandBuffer cmd)
    {
        float ax = 1f / _pfxAoW, ay = 1f / _pfxAoH;
        // Die Tiefe liegt bei aktivem Upscaler in SceneExtent (intern), das AO-Ziel in halber Displaygröße.
        // Ohne diese Skalierung liest AO falsche Tiefenpunkte und erzeugt Kantenartefakte am Fahrzeug.
        float depthScaleX = (float)SceneExtent.Width / _pfxAoW;
        float depthScaleY = (float)SceneExtent.Height / _pfxAoH;
        var pc = new PfxPush
        {
            Vars = new(ax, ay, depthScaleX, depthScaleY),
            Params = new(_proj.M33, _proj.M43, _proj.M11, _proj.M22),
            Shadows = new(System.Math.Max(0.05f, PostFx.AoRadius), System.Math.Clamp(PostFx.AoBias, 0f, 0.5f), System.Math.Max(5f, PostFx.AoMaxDistance), 1f)
        };
        PfxPass(cmd, _pfxAoAFb, _pfxAoW, _pfxAoH, _pfxAo, _pfxSetAo, pc);
        var g = PfxGauss();
        PfxPass(cmd, _pfxAoBFb, _pfxAoW, _pfxAoH, _pfxBlur, _pfxSetAoH, new PfxPush { Vars = new(ax, ay, ax, 0f), Params = g });
        PfxPass(cmd, _pfxAoAFb, _pfxAoW, _pfxAoH, _pfxBlur, _pfxSetAoV, new PfxPush { Vars = new(ax, ay, 0f, ay), Params = g });
    }

    /// <summary>Zusatz God Rays (PostFx): Helligkeitsmaske auf ferner Himmelstiefe, radial von der Sonne weg (Viertel).</summary>
    private void RecordGod(CommandBuffer cmd, System.Numerics.Vector2 sun)
    {
        float qx = 1f / _pfxQW, qy = 1f / _pfxQH;
        var pc = new PfxPush
        {
            Vars = new(qx, qy, 1f / _extent.Width, 1f / _extent.Height),
            Params = new(sun.X, sun.Y, System.Math.Clamp(PostFx.GodIntensity, 0f, 2f), System.Math.Clamp(PostFx.GodDecay, 0.5f, 0.995f)),
            Shadows = new(System.Math.Clamp(PostFx.GodThreshold, 0f, 1f), 0.18f, 0.6f, 1f),
            Midtones = new(_proj.M33, _proj.M43, System.Math.Clamp(PostFx.GodSamples, 4, 24), 0f)
        };
        PfxPass(cmd, _pfxGodFb, _pfxQW, _pfxQH, _pfxGodRay, _pfxSetGod, pc);
    }

    /// <summary>Gemeinsames Volumen-UBO: Spieler-Projektor (falls vorhanden), Sonne und Kameraachsen. Beide Pässe teilen sich den Puffer.</summary>
    private void UpdateVolumeUbo(bool sunVol)
    {
        var v = default(PfxVolumeUbo);
        var p = _globals.PlayerProjector;
        if (p.HasValue)
        {
            var proj = p.Value;
            v.PosNear = new(proj.Position, proj.Near);
            v.DirStrength = new(proj.Direction, proj.Strength);
            v.UpCot = new(proj.Up, proj.Cot);
            v.Att = proj.Attenuation;
        }
        var fwd = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(_viewRight, _viewUp));
        if (fwd.LengthSquared() < 0.5f) fwd = -System.Numerics.Vector3.UnitZ;
        else fwd = -fwd;
        v.Right = new(_viewRight, 0f);
        v.CamUp = new(_viewUp, 0f);
        v.Fwd = new(fwd, 0f);
        v.CamPos = new(_globals.CameraPosition, 0f);

        // Streu-Sonne des Sky-Domes: deckt sich mit dem sichtbaren Himmel (statt der geometrischen Sonne der God Rays).
        var scatter = _globals.Sky.ScatterSun;
        if (scatter.LengthSquared() < 1e-6f) scatter = _globals.SunDirection;
        var L = System.Numerics.Vector3.Normalize(scatter);
        // Skydome_HGg steht im Original bei ~1000 (g ≈ 1): eine fast deltaförmige Keule. Bei 63° Sonnenhöhe liegt
        // die Sonne außerhalb des Fahrblicks (~±30°), die Phase wäre ~1e-4 und im 8-Bit-Puffer exakt 0.
        // Für das Streumedium deshalb auf g ≤ 0,72 abmildern (und g = 1 wie bei Blitz/Gewitter abfangen).
        float g = System.Math.Clamp(_globals.Sky.HGgSaturation.Z * 0.5f, 0f, 0.72f);
        float g2 = g * g;
        v.SunDir = new(L, System.Math.Clamp(PostFx.SunVolumetricIntensity, 0f, 2f));
        v.SunColor = new(_globals.SunColor, sunVol ? System.Math.Clamp(_globals.DayDim, 0f, 1f) : 0f);
        // Obergrenze bewusst niedrig: direkt in Sonnenrichtung würde die HG-Keule sonst ~20 erreichen und
        // den Puffer trotz geringer Intensität sättigen (8-Bit-UNORM = weißer Fleck / Blendung).
        v.SunPhase = new(1f - g2, 1f + g2, 2f * g, 1.5f);
        v.SunRange = new(System.Math.Clamp(PostFx.FogEnd, 100f, 1500f), 0.35f, 1f, 0f);
        *(PfxVolumeUbo*)_pfxVolUboMem.Mapped = v;
    }

    /// <summary>Volumetrisches Licht des Spieler-Scheinwerfers (Viertel): Raymarch + kleiner Gauß (waagerecht/senkrecht).</summary>
    private void RecordVolumetric(CommandBuffer cmd)
    {
        float qx = 1f / _pfxQW, qy = 1f / _pfxQH;
        var pc = new PfxPush
        {
            Vars = new(qx, qy, 1f / _extent.Width, 1f / _extent.Height),
            Params = new(PostFx.FogHeightDensity, PostFx.FogHeightBase, System.Math.Clamp(PostFx.VolumetricSteps, 4, 16), 0f),
            Shadows = new(System.Math.Clamp(PostFx.VolumetricIntensity, 0f, 2f), 0f, 0f, 0f),
            Midtones = new(_proj.M33, _proj.M43, _proj.M11, _proj.M22)
        };
        PfxPass(cmd, _pfxVolAFb, _pfxQW, _pfxQH, _pfxVolume, _pfxSetVol, pc);
        var g = PfxGauss();
        PfxPass(cmd, _pfxVolBFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetVolH, new PfxPush { Vars = new(qx, qy, qx, 0f), Params = g });
        PfxPass(cmd, _pfxVolAFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetVolV, new PfxPush { Vars = new(qx, qy, 0f, qy), Params = g });
    }

    /// <summary>Sonnen-Volumetrie (Viertel) in den God-Puffer: HG-Streuung im Höhennebel, danach derselbe Gauß wie beim Scheinwerfer.</summary>
    private void RecordSunVolumetric(CommandBuffer cmd)
    {
        float qx = 1f / _pfxQW, qy = 1f / _pfxQH;
        var pc = new PfxPush
        {
            Vars = new(qx, qy, 1f / _extent.Width, 1f / _extent.Height),
            Params = new(PostFx.FogHeightDensity, PostFx.FogHeightBase, System.Math.Clamp(PostFx.SunVolumetricSteps, 4, 24), 0f),
            // Der Puffer trägt die Sonnenstärke bereits; der Apply multipliziert nur noch mit 1 (siehe DrawPhotoshopApply).
            Shadows = new(1f, 0f, 0f, 0f),
            Midtones = new(_proj.M33, _proj.M43, _proj.M11, _proj.M22)
        };
        PfxPass(cmd, _pfxGodFb, _pfxQW, _pfxQH, _pfxSunVolume, _pfxSetGod, pc);
        var g = PfxGauss();
        PfxPass(cmd, _pfxVolBFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetGodH, new PfxPush { Vars = new(qx, qy, qx, 0f), Params = g });
        PfxPass(cmd, _pfxGodFb, _pfxQW, _pfxQH, _pfxBlur, _pfxSetVolV, new PfxPush { Vars = new(qx, qy, 0f, qy), Params = g });
    }

    /// <summary>LUT-Konstanten c9..c14 (§2.4).</summary>
    private static PfxPush LutPush()
    {
        float inv = 1f / (PhotoshopFx.BloomWhite - PhotoshopFx.BloomBlack);   // Original in double gerechnet
        if (!float.IsFinite(inv)) inv = 0f;
        var s = PhotoshopFx.BalanceShadows; var m = PhotoshopFx.BalanceMidtones; var h = PhotoshopFx.BalanceHightones;
        return new PfxPush
        {
            Vars = new(1f / PfxLutSize, 1f / PfxLutSize, 0f, 0f),
            Params = new(PhotoshopFx.SoftlightOpacity, PhotoshopFx.SoftlightBrightness, PhotoshopFx.OverlayOpacity, 0f),
            Shadows = new(s, 0f),
            Midtones = new(m, 0f),
            Hightones = new(h, 0f),
            Bloom = new(PhotoshopFx.BloomBlack, inv, PhotoshopFx.BloomIntensity, 0f),
            Gamma = new(PhotoshopFx.Gamma, 0f, 0f, 0f)
        };
    }

    /// <summary>Ein Vollbild-Durchgang in ein Zwischenziel.</summary>
    private void PfxPass(CommandBuffer cmd, Framebuffer fb, uint w, uint h, Pipeline pipe, DescriptorSet set, PfxPush pc)
    {
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _pfxPass,
            Framebuffer = fb,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(w, h) }
        };
        _vk.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);
        var vp = new Viewport { X = 0, Y = 0, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 };
        var sc = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(w, h) };
        _vk.CmdSetViewport(cmd, 0, 1, &vp);
        _vk.CmdSetScissor(cmd, 0, 1, &sc);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipe);
        if (set.Handle != 0) _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pfxLayout, 0, 1, &set, 0, null);
        _vk.CmdPushConstants(cmd, _pfxLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PfxPush), &pc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cmd);
        _cDraw++;
    }

    /// <summary>
    /// a) Weltbild nach _pfxScene kopieren. Quelle: MSAA-Auflösebild (liegt schon als TransferSrc vor) bzw. ohne MSAA das
    /// Swapchain-Bild (danach zurück in ColorAttachmentOptimal für den UI-Pass). sRGB- und UNORM-Format gleicher Größe sind
    /// kopierkompatibel: die Bytes bleiben, gelesen wird die gespeicherte Bildschirmfarbe.
    /// </summary>
    private void CopySceneForPhotoshop(CommandBuffer cmd)
    {
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var src = Msaa ? _resolveImage : _images[_imageIndex];
        var srcLayout = Msaa ? ImageLayout.TransferSrcOptimal : ImageLayout.ColorAttachmentOptimal;
        var pre = stackalloc ImageMemoryBarrier[2];
        pre[0] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = src,
            SubresourceRange = range,
            OldLayout = srcLayout,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        pre[1] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _pfxScene,
            SubresourceRange = range,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        // Quelle: Farbausgabe (ohne MSAA) bzw. vorige Kopie (MSAA); Ziel: im vorigen Bild noch im Fragment-Shader gelesen
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 2, pre);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(_extent.Width, _extent.Height, 1)
        };
        _vk.CmdCopyImage(cmd, src, ImageLayout.TransferSrcOptimal, _pfxScene, ImageLayout.TransferDstOptimal, 1, in region);

        var post = stackalloc ImageMemoryBarrier[2];
        post[0] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _pfxScene,
            SubresourceRange = range,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        uint nPost = 1;
        if (!Msaa)
        {
            // Swapchain-Bild zurück für den UI-Pass (der lädt den Inhalt; Anwenden überschreibt ihn danach ganz)
            post[1] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                Image = src,
                SubresourceRange = range,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored
            };
            nPost = 2;
        }
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            0, 0, null, 0, null, nPost, post);
    }

    /// <summary>
    /// Anwenden (§2.5 ps_0): im UI-Pass über das ganze Bild, vor Flash. vars = (1/W, 1/H, BlurredLumFactor) wie das Original c60.
    /// </summary>
    private void DrawPhotoshopApply(CommandBuffer cmd)
    {
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pfxApply);
        var set = _pfxSetApply;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pfxLayout, 0, 1, &set, 0, null);
        // Vorwärts aus den Kameraachsen (cross(recht, oben) = rückwärts), wie in WriteFrameUbo.
        var fwd = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(_viewRight, _viewUp));
        if (fwd.LengthSquared() < 0.5f) fwd = -System.Numerics.Vector3.UnitZ;
        else fwd = -fwd;
        var pc = new PfxPush
        {
            Vars = new(1f / _extent.Width, 1f / _extent.Height, PhotoshopFx.BlurredLumFactor, _pfxFlags),
            // Beide Volumen-Puffer sind bereits mit ihrer eigenen Intensität skaliert – nicht erneut multiplizieren.
            Params = new(PostFx.BloomIntensity, System.Math.Clamp(PostFx.AoIntensity, 0f, 1f), 1f, 0f),
            // Der Sonnenpuffer trägt SunVolumetricIntensity, der klassische God-Puffer braucht GodIntensity.
            Shadows = new(_pfxSunVol ? 1f : System.Math.Clamp(PostFx.GodIntensity, 0f, 2f), System.Math.Clamp(PostFx.FogIntensity, 0f, 1f), 0f, 0f),
            Midtones = new(_proj.M33, _proj.M43, _proj.M11, _proj.M22),
            Hightones = new(_viewRight.Y, _viewUp.Y, fwd.Y, _globals.CameraPosition.Y),
            Bloom = new(_globals.FogColor.X, _globals.FogColor.Y, _globals.FogColor.Z, 0f),
            Gamma = new(PostFx.FogStart, PostFx.FogEnd, PostFx.FogHeightDensity, PostFx.FogHeightBase)
        };
        _vk.CmdPushConstants(cmd, _pfxLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PfxPush), &pc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _cDraw++;
        _boundPipeline = null;   // eigene Pipeline: der nächste Aufruf (Flash) bindet neu
    }
}