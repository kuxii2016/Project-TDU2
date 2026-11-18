using Paradise.Core.Diagnostics;
using Silk.NET.Vulkan;
using System.Numerics;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Spiegelungen wie im Unity-Projekt:
///  - Bodenspiegelung im Interieur (Unity InteriorFx.InteriorMirror): zweite Kamera an der Bodenebene gespiegelt, halbe Auflösung,
///    alles unter der Ebene weggeschnitten; der Objekt-Shader mischt das Bild nach _ReflMix und Fresnel ein (_TDU2PlanarRefl).
///  - Reflexionssonde am Fahrzeug (Unity TDU2VehicleManager.MirrorReflections: ReflectionProbe Realtime, 256, Faces einzeln,
///    Sichtweite 600 m, ohne das Auto): Würfelkarte für den Lack/die Scheiben (unity_SpecCube0).
/// Beide Durchgänge zeichnen die schon gesammelten opaken Objekte und den Himmel mit den vorhandenen Pipelines (kompatibler
/// Render-Pass: gleiche Formate und Abtastrate wie der Bild-Durchgang) und eigenen Frame-Uniforms (Kamera, Spiegel-Kennung).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const uint ProbeSize = 256;

    private RenderPass _envPass;
    // Bodenspiegelung
    private Image _plColor, _plDepth, _plImage;
    private GpuAllocator.Allocation _plColorMem, _plDepthMem, _plImageMem;
    private ImageView _plColorView, _plDepthView, _plView;
    private Framebuffer _plFb;
    private uint _plW, _plH;
    private bool _planarActive;
    private float _planarY;
    private bool _oceanPlanar;   // dieses Bild: planarRefl enthält das Meer-Spiegelbild (RenderFx.OceanOn)
    // Reflexionssonde (Würfelkarte)
    private Image _prColor, _prDepth, _prCube;
    private GpuAllocator.Allocation _prColorMem, _prDepthMem, _prCubeMem;
    private ImageView _prColorView, _prDepthView, _prCubeView;
    private readonly ImageView[] _prFaceViews = new ImageView[6];
    private readonly Framebuffer[] _prFbs = new Framebuffer[6];
    private int _probeFace, _probeFacesDone;
    private Vector3 _probeLastPos = new(float.NaN); private int _probeLastVer = -1; private bool _probeStaticDone;
    private bool _probeValid;
    // Vorschaubild des Händlers (RenderFx.Lure*)
    private Image _luColor, _luDepth, _luImage;
    private GpuAllocator.Allocation _luColorMem, _luDepthMem, _luImageMem;
    private ImageView _luColorView, _luDepthView, _luView;
    private Framebuffer _luFb;
    private uint _luW, _luH;
    private VulkanTexture? _luTex;
    // eigene Frame-Uniforms je Durchgang (0 = Spiegel, 1 = Sonde, 2 = Vorschaubild) und Bild im Flug
    private const int EnvSlots = 3;
    private readonly Silk.NET.Vulkan.Buffer[] _envUbo = new Silk.NET.Vulkan.Buffer[MaxFramesInFlight * EnvSlots];
    private readonly GpuAllocator.Allocation[] _envUboMem = new GpuAllocator.Allocation[MaxFramesInFlight * EnvSlots];
    private readonly DescriptorSet[] _envSets = new DescriptorSet[MaxFramesInFlight * EnvSlots];
    private DescriptorSet _activeFrameSet;
    private bool _envSetsCreated;

    /// <summary>Render-Pass der Spiegel-Durchgänge: wie der Bild-Durchgang (kompatibel zu dessen Pipelines), Ergebnis lesbar.</summary>
    private void CreateEnvPass()
    {
        var color = new AttachmentDescription
        {
            Format = _format,
            Samples = _msaa,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = Msaa ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = Msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.ShaderReadOnlyOptimal
        };
        var depth = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = _msaa,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };
        var resolve = new AttachmentDescription
        {
            Format = _format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var resolveRef = new AttachmentReference { Attachment = 2, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PResolveAttachments = Msaa ? &resolveRef : null,
            PDepthStencilAttachment = &depthRef
        };
        var deps = stackalloc SubpassDependency[2];
        // vorher: das Bild wurde im vorigen Frame noch gelesen (Fragment-Shader) bzw. Tiefe/Farbe geschrieben
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };
        // nachher: der Bild-Durchgang liest das Ergebnis im Fragment-Shader
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        var attachments = stackalloc AttachmentDescription[3] { color, depth, resolve };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = Msaa ? 3u : 2u,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps
        };
        if (_vk.CreateRenderPass(_device, in info, null, out _envPass) != Result.Success)
            throw new Exception("Spiegel-RenderPass konnte nicht erstellt werden.");
    }

    private ImageView CreateLayerView(Image image, Format format, ImageViewType type, uint baseLayer, uint layers)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = type,
            Format = format,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = baseLayer, LayerCount = layers }
        };
        if (_vk.CreateImageView(_device, in info, null, out var view) != Result.Success)
            throw new Exception("ImageView (Spiegel) konnte nicht erstellt werden.");
        return view;
    }

    private Framebuffer CreateEnvFramebuffer(ImageView color, ImageView depth, ImageView target, uint w, uint h)
    {
        var att = stackalloc ImageView[3];
        if (Msaa) { att[0] = color; att[1] = depth; att[2] = target; }
        else { att[0] = target; att[1] = depth; }
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _envPass,
            AttachmentCount = Msaa ? 3u : 2u,
            PAttachments = att,
            Width = w,
            Height = h,
            Layers = 1
        };
        if (_vk.CreateFramebuffer(_device, in info, null, out var fb) != Result.Success)
            throw new Exception("Framebuffer (Spiegel) konnte nicht erstellt werden.");
        return fb;
    }

    /// <summary>Ziele der Spiegel-Durchgänge anlegen (mit der Swapchain: Größe, Format, Abtastrate) und in die Frame-Sets eintragen.</summary>
    private void CreateEnvTargets()
    {
        CreateEnvPass();
        // Bodenspiegelung: Unity ReflectionScale x Bildschirm
        _plW = System.Math.Max(64u, (uint)(_extent.Width * RenderFx.PlanarScale));
        _plH = System.Math.Max(64u, (uint)(_extent.Height * RenderFx.PlanarScale));
        CreateImage(_plW, _plH, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _plImage, out _plImageMem);
        _plView = CreateImageView(_plImage, _format, ImageAspectFlags.ColorBit, 1);
        CreateImage(_plW, _plH, 1, _depthFormat, ImageUsageFlags.DepthStencilAttachmentBit, out _plDepth, out _plDepthMem, _msaa);
        _plDepthView = CreateImageView(_plDepth, _depthFormat, ImageAspectFlags.DepthBit, 1);
        if (Msaa)
        {
            CreateImage(_plW, _plH, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit, out _plColor, out _plColorMem, _msaa);
            _plColorView = CreateImageView(_plColor, _format, ImageAspectFlags.ColorBit, 1);
        }
        _plFb = CreateEnvFramebuffer(_plColorView, _plDepthView, _plView, _plW, _plH);

        // Reflexionssonde: Würfelkarte 256 (6 Ebenen), je Seite ein Framebuffer
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = (ImageCreateFlags)0x10,   // VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT (Name je nach Silk.NET-Version verschieden)
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(ProbeSize, ProbeSize, 1),
            MipLevels = 1,
            ArrayLayers = 6,
            Format = _format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateImage(_device, in ci, null, out _prCube) != Result.Success) throw new Exception("Würfelkarte konnte nicht erstellt werden.");
        _vk.GetImageMemoryRequirements(_device, _prCube, out var req);
        _prCubeMem = _alloc.Allocate(req, MemoryPropertyFlags.DeviceLocalBit, linear: false);
        _vk.BindImageMemory(_device, _prCube, _prCubeMem.Memory, _prCubeMem.Offset);
        _prCubeView = CreateLayerView(_prCube, _format, ImageViewType.TypeCube, 0, 6);
        for (uint i = 0; i < 6; i++) _prFaceViews[i] = CreateLayerView(_prCube, _format, ImageViewType.Type2D, i, 1);
        CreateImage(ProbeSize, ProbeSize, 1, _depthFormat, ImageUsageFlags.DepthStencilAttachmentBit, out _prDepth, out _prDepthMem, _msaa);
        _prDepthView = CreateImageView(_prDepth, _depthFormat, ImageAspectFlags.DepthBit, 1);
        if (Msaa)
        {
            CreateImage(ProbeSize, ProbeSize, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit, out _prColor, out _prColorMem, _msaa);
            _prColorView = CreateImageView(_prColor, _format, ImageAspectFlags.ColorBit, 1);
        }
        for (int i = 0; i < 6; i++) _prFbs[i] = CreateEnvFramebuffer(_prColorView, _prDepthView, _prFaceViews[i], ProbeSize, ProbeSize);
        _probeValid = false; _probeFacesDone = 0; _probeFace = 0; _probeStaticDone = false;

        // beide Ziele einmal in den lesbaren Zustand bringen (die Frame-Sets zeigen immer darauf)
        var cmd = BeginSingleTimeCommands();
        var b = stackalloc ImageMemoryBarrier[2];
        b[0] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _plImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        b[1] = b[0];
        b[1].Image = _prCube;
        b[1].SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6);
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 2, b);
        EndSingleTimeCommands(cmd);

        // eigene Frame-Uniforms der Durchgänge (einmalig)
        if (!_envSetsCreated)
        {
            for (int i = 0; i < _envSets.Length; i++)
            {
                CreateHostBuffer((ulong)sizeof(FrameUbo), BufferUsageFlags.UniformBufferBit, out _envUbo[i], out _envUboMem[i]);
                var layout = _frameSetLayout;
                var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
                if (_vk.AllocateDescriptorSets(_device, in alloc, out _envSets[i]) != Result.Success)
                    throw new Exception("DescriptorSet (Spiegel) fehlgeschlagen.");
            }
            _envSetsCreated = true;
        }
        WriteEnvDescriptors();
        System.Array.Clear(_rainPudW);   // Regen-Bindings der (neuen) Spiegel-Sets beim nächsten Bild schreiben
        System.Array.Clear(_rainRipW);
    }

    /// <summary>Bindings 1..3 aller Frame-Sets: Schattenkarte, Spiegelbild, Würfelkarte (auch nach neuen Schattenkarten).</summary>
    private void WriteEnvDescriptors()
    {
        var samp = GetSampler(TextureWrap.Clamp, TextureWrap.Clamp);
        var pl = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _plView, Sampler = samp };
        var cu = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _prCubeView, Sampler = samp };
        var sh = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _shadowArrayView, Sampler = _shadowSampler };
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            var w = stackalloc WriteDescriptorSet[2];
            w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &pl };
            w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _frameSets[i], DstBinding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &cu };
            _vk.UpdateDescriptorSets(_device, 2, w, 0, null);
        }
        if (!_envSetsCreated) return;
        for (int i = 0; i < _envSets.Length; i++)
        {
            var bi = new DescriptorBufferInfo { Buffer = _envUbo[i], Offset = 0, Range = (ulong)sizeof(FrameUbo) };
            var ib = new DescriptorBufferInfo { Buffer = _instBuf[i / EnvSlots], Offset = 0, Range = Vk.WholeSize };   // Instanzpuffer des Bildes (si = Bild * EnvSlots + Platz)
            var w = stackalloc WriteDescriptorSet[5];
            w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _envSets[i], DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi };
            w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _envSets[i], DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &sh };
            // Spiegelbild/Würfelkarte werden in diesen Durchgängen nicht gelesen (planar.x = -1, probe.x = 0)
            w[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _envSets[i], DstBinding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &pl };
            w[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _envSets[i], DstBinding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &cu };
            w[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _envSets[i], DstBinding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, PBufferInfo = &ib };
            _vk.UpdateDescriptorSets(_device, 5, w, 0, null);
        }
    }

    /// <summary>Ziel des Vorschaubilds (bei Bedarf, neue Größe -> neu); als Textur für den Flash-Clip.</summary>
    private void EnsureLureTarget(uint w, uint h)
    {
        if (_luTex != null && _luW == w && _luH == h) return;
        DestroyLureTarget();
        _luW = w; _luH = h;
        CreateImage(w, h, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _luImage, out _luImageMem);
        _luView = CreateImageView(_luImage, _format, ImageAspectFlags.ColorBit, 1);
        CreateImage(w, h, 1, _depthFormat, ImageUsageFlags.DepthStencilAttachmentBit, out _luDepth, out _luDepthMem, _msaa);
        _luDepthView = CreateImageView(_luDepth, _depthFormat, ImageAspectFlags.DepthBit, 1);
        if (Msaa)
        {
            CreateImage(w, h, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit, out _luColor, out _luColorMem, _msaa);
            _luColorView = CreateImageView(_luColor, _format, ImageAspectFlags.ColorBit, 1);
        }
        _luFb = CreateEnvFramebuffer(_luColorView, _luDepthView, _luView, w, h);
        // Bild + Speicher gehören der Textur (Dispose gibt sie verzögert frei)
        _luTex = new VulkanTexture(this, _luImage, _luImageMem, _luView,
            new TextureUpload { Width = (int)w, Height = (int)h, Format = GpuTextureFormat.Rgba8, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "Händler Vorschau" });
    }

    private void DestroyLureTarget()
    {
        if (_luTex == null) return;
        if (ReferenceEquals(RenderFx.LureTexture, _luTex)) RenderFx.LureTexture = null;
        var fb = _luFb; var dv = _luDepthView; var di = _luDepth; var dm = _luDepthMem; var cv = _luColorView; var ci = _luColor; var cm = _luColorMem;
        DeferDestroy(() =>
        {
            _vk.DestroyFramebuffer(_device, fb, null);
            _vk.DestroyImageView(_device, dv, null); _vk.DestroyImage(_device, di, null); _alloc.Free(dm);
            if (cv.Handle != 0) { _vk.DestroyImageView(_device, cv, null); _vk.DestroyImage(_device, ci, null); _alloc.Free(cm); }
        });
        _luTex.Dispose();   // Bild, Ansicht, Speicher (verzögert)
        _luTex = null; _luFb = default; _luColorView = default; _luDepthView = default;
    }

    private void DestroyEnvTargets()
    {
        if (_envPass.Handle == 0) return;
        DestroyLureTarget();
        _vk.DestroyFramebuffer(_device, _plFb, null);
        for (int i = 0; i < 6; i++) { _vk.DestroyFramebuffer(_device, _prFbs[i], null); _vk.DestroyImageView(_device, _prFaceViews[i], null); }
        _vk.DestroyImageView(_device, _plView, null); _vk.DestroyImage(_device, _plImage, null); _alloc.Free(_plImageMem);
        _vk.DestroyImageView(_device, _plDepthView, null); _vk.DestroyImage(_device, _plDepth, null); _alloc.Free(_plDepthMem);
        if (_plColorView.Handle != 0) { _vk.DestroyImageView(_device, _plColorView, null); _vk.DestroyImage(_device, _plColor, null); _alloc.Free(_plColorMem); _plColorView = default; }
        _vk.DestroyImageView(_device, _prCubeView, null); _vk.DestroyImage(_device, _prCube, null); _alloc.Free(_prCubeMem);
        _vk.DestroyImageView(_device, _prDepthView, null); _vk.DestroyImage(_device, _prDepth, null); _alloc.Free(_prDepthMem);
        if (_prColorView.Handle != 0) { _vk.DestroyImageView(_device, _prColorView, null); _vk.DestroyImage(_device, _prColor, null); _alloc.Free(_prColorMem); _prColorView = default; }
        _vk.DestroyRenderPass(_device, _envPass, null);
        _envPass = default;
        _probeValid = false;
    }

    private void DestroyEnvUbos()
    {
        if (!_envSetsCreated) return;
        for (int i = 0; i < _envSets.Length; i++) FreeBuffer(_envUbo[i], _envUboMem[i]);
        _envSetsCreated = false;
    }

    /// <summary>Kennungen für den Bild-Durchgang (FrameUbo.Planar/Probe).</summary>
    private Vector4 PlanarUbo => new(_planarActive ? (RenderFx.DebugPlanar ? 2f : 1f) : 0f, _planarY, _extent.Width > 0 ? 1f / _extent.Width : 0f, _extent.Height > 0 ? 1f / _extent.Height : 0f);
    private Vector4 ProbeUbo => new(_probeValid && RenderFx.ProbeOn ? 1f : 0f, 0f, 0f, 0f);

    /// <summary>Unity Matrix4x4 Reflection(plane y = Y) im Zeilenvektor-Raum: y' = 2Y - y.</summary>
    private static Matrix4x4 ReflectY(float y) => new(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 2f * y, 0, 1);

    /// <summary>
    /// Vor dem Bild-Durchgang (nach den Schattenkarten): Bodenspiegelung und eine Seite der Reflexionssonde rendern. Danach
    /// schreibt EndFrame die Frame-Uniforms neu (Kennungen an).
    /// </summary>
    private void RenderEnvironment(CommandBuffer cmd)
    {
        _planarActive = false;
        _oceanPlanar = false;
        if (_envPass.Handle == 0 || _extent.Width == 0) return;
        var mainUbo = *(FrameUbo*)_frameUboMem[_currentFrame].Mapped;
        var keepVP = _viewProj;

        // ---- Bodenspiegelung (nur wenn die Kamera über der Ebene ist, wie Unity InteriorMirror.Render)
        if (RenderFx.PlanarOn && _globals.CameraPosition.Y > RenderFx.PlanarY + 0.05f)
        {
            _planarY = RenderFx.PlanarY;
            var refl = ReflectY(_planarY);
            var view = refl * _view;
            var u = mainUbo;
            var cp = _globals.CameraPosition;
            u.CamPos = new Vector4(cp.X, 2f * _planarY - cp.Y, cp.Z, mainUbo.CamPos.W);
            u.Planar = new Vector4(-1f, _planarY, 0f, 0f);
            u.Probe = Vector4.Zero;
            int si = _currentFrame * EnvSlots;
            *(FrameUbo*)_envUboMem[si].Mapped = u;
            EnvPass(cmd, _plFb, _plW, _plH, _envSets[si], view, _proj, skipCars: false);
            _planarActive = true;
        }
        // ---- Meer-Spiegelbild (TD_Ocean SEAREF_N): gleiche Spiegelkamera, ohne Himmel, gelöscht mit ocean_reflectionColor;
        //      die Wasserflächen selbst sind nicht dabei (nur die opaken Objekte, wie die Bucket-Maske des Originals)
        else if (RenderFx.OceanOn && _globals.CameraPosition.Y > RenderFx.OceanY + 0.05f)
        {
            var refl = ReflectY(RenderFx.OceanY);
            var view = refl * _view;
            var u = mainUbo;
            var cp = _globals.CameraPosition;
            u.CamPos = new Vector4(cp.X, 2f * RenderFx.OceanY - cp.Y, cp.Z, mainUbo.CamPos.W);
            u.Planar = new Vector4(-1f, RenderFx.OceanY, 0f, 0f);
            u.Probe = Vector4.Zero;
            int si = _currentFrame * EnvSlots;
            *(FrameUbo*)_envUboMem[si].Mapped = u;
            EnvPass(cmd, _plFb, _plW, _plH, _envSets[si], view, _proj, skipCars: false, clear: RenderFx.OceanClear, sky: false);
            _oceanPlanar = true;
        }

        // ---- Reflexionssonde: eine Seite pro Bild (Unity timeSlicingMode IndividualFaces); statische Sonde (Interieur) nur bis alle
        //      6 Seiten stehen – danach erst wieder bei neuer Stelle/Version
        bool probeStill = RenderFx.ProbeStatic && Vector3.DistanceSquared(RenderFx.ProbePos, _probeLastPos) < 1e-4f && RenderFx.ProbeVersion == _probeLastVer;
        if (RenderFx.ProbeStatic && !probeStill) { _probeLastPos = RenderFx.ProbePos; _probeLastVer = RenderFx.ProbeVersion; _probeFacesDone = 0; _probeFace = 0; _probeStaticDone = false; }
        if (!RenderFx.ProbeStatic) _probeStaticDone = false;
        RenderFx.ProbeSettled = RenderFx.ProbeOn && RenderFx.ProbeStatic && _probeStaticDone;
        if (RenderFx.ProbeOn && !(RenderFx.ProbeStatic && _probeStaticDone))
        {
            int f = _probeFace;
            var pos = RenderFx.ProbePos;
            // Vulkan-Würfelseiten (+X, -X, +Y, -Y, +Z, -Z): Blickrichtung/oben, Bild in X gespiegelt (s-Achse der Würfelkarte)
            Vector3 fwd = f switch { 0 => Vector3.UnitX, 1 => -Vector3.UnitX, 2 => Vector3.UnitY, 3 => -Vector3.UnitY, 4 => Vector3.UnitZ, _ => -Vector3.UnitZ };
            Vector3 up = f switch { 2 => -Vector3.UnitZ, 3 => Vector3.UnitZ, _ => Vector3.UnitY };
            var view = Matrix4x4.CreateLookAt(pos, pos + fwd, up);
            float near = 0.3f, far = RenderFx.ProbeFar;
            // Reversed-Z wie TerrainModule.PerspectiveReversedZ, 90°, X gespiegelt
            var proj = new Matrix4x4(
                -1f, 0f, 0f, 0f,
                0f, -1f, 0f, 0f,
                0f, 0f, near / (far - near), -1f,
                0f, 0f, far * near / (far - near), 0f);
            var u = mainUbo;
            u.CamPos = new Vector4(pos, mainUbo.CamPos.W);
            u.Planar = new Vector4(0f, 0f, 0f, 0f);
            u.Probe = Vector4.Zero;
            int si = _currentFrame * EnvSlots + 1;
            *(FrameUbo*)_envUboMem[si].Mapped = u;
            EnvPass(cmd, _prFbs[f], ProbeSize, ProbeSize, _envSets[si], view, proj, skipCars: true, farLimit: RenderFx.ProbeFar);
            _probeFace = (f + 1) % 6;
            if (_probeFacesDone < 6) _probeFacesDone++;
            if (_probeFacesDone >= 6) { _probeValid = true; if (RenderFx.ProbeStatic) _probeStaticDone = true; }
        }

        // ---- Vorschaubild im Kauffenster des Händlers (Unity _lureCam -> RenderTexture, mit Reflexionssonde, ohne Bodenspiegelung)
        if (RenderFx.LureOn && RenderFx.LureWidth > 8 && RenderFx.LureHeight > 8)
        {
            EnsureLureTarget((uint)RenderFx.LureWidth, (uint)RenderFx.LureHeight);
            var u = mainUbo;
            u.CamPos = new Vector4(RenderFx.LurePos, mainUbo.CamPos.W);
            u.Planar = Vector4.Zero;
            u.Probe = ProbeUbo;
            int si = _currentFrame * EnvSlots + 2;
            *(FrameUbo*)_envUboMem[si].Mapped = u;
            EnvPass(cmd, _luFb, _luW, _luH, _envSets[si], RenderFx.LureView, RenderFx.LureProj, skipCars: false);
            RenderFx.LureTexture = _luTex;
        }
        _viewProj = keepVP;
    }

    private void EnvPass(CommandBuffer cmd, Framebuffer fb, uint w, uint h, DescriptorSet set, Matrix4x4 view, Matrix4x4 proj, bool skipCars, float farLimit = 0f, Vector3? clear = null, bool sky = true)
    {
        var clears = stackalloc ClearValue[3];
        var cc = clear ?? new Vector3(ClearColor[0], ClearColor[1], ClearColor[2]);
        clears[0] = new ClearValue(new ClearColorValue(ToLin(cc.X), ToLin(cc.Y), ToLin(cc.Z), 1f));
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(0f, 0) };   // Reversed-Z
        var rp = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _envPass,
            Framebuffer = fb,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(w, h) },
            ClearValueCount = Msaa ? 3u : 2u,
            PClearValues = clears
        };
        _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
        var viewport = new Viewport { X = 0, Y = 0, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 };
        _vk.CmdSetViewport(cmd, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = new Extent2D(w, h) };
        _vk.CmdSetScissor(cmd, 0, 1, &scissor);
        _activeFrameSet = set;
        _boundPipeline = null;
        if (sky && !_globals.NoSky) DrawSky(cmd, view, proj);
        _viewProj = view * proj;
        // Sichtkegel dieses Durchgangs (wie die eigene Kamera in Unity: Spiegel-/Sondenkamera culled selbst) – Kugel je Mesh.
        // Ohne das zeichnete jede Sondenseite (90°) alle Objekte des Hauptbilds noch einmal.
        var vp = view * proj;
        Span<Vector4> planes = stackalloc Vector4[4];
        planes[0] = new Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41);   // links
        planes[1] = new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);   // rechts
        planes[2] = new Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42);   // unten/oben
        planes[3] = new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
        for (int i = 0; i < 4; i++) { var n = new Vector3(planes[i].X, planes[i].Y, planes[i].Z); float l = n.Length(); if (l > 1e-12f) planes[i] /= l; }
        Matrix4x4.Invert(view, out var camWorld);
        var eye = camWorld.Translation;
        float far = farLimit > 0f ? farLimit : float.MaxValue;
        foreach (var (mesh, mat, model) in _opaque)
        {
            if (skipCars && mat.Kind == MaterialKind.Car) continue;   // Unity cullingMask: das eigene Auto nicht mitspiegeln
            if (mesh is VulkanMesh vm && vm.BRadius < 1e5f)
            {
                var c = Vector3.Transform(vm.BCenter, model);
                float r = vm.BRadius * MathF.Max(new Vector3(model.M11, model.M12, model.M13).Length(), MathF.Max(new Vector3(model.M21, model.M22, model.M23).Length(), new Vector3(model.M31, model.M32, model.M33).Length()));
                bool outside = false;
                for (int i = 0; i < 4 && !outside; i++) if (planes[i].X * c.X + planes[i].Y * c.Y + planes[i].Z * c.Z + planes[i].W < -r) outside = true;
                if (outside || Vector3.Distance(c, eye) - r > far) continue;
            }
            Batch(mesh, mat, model, PipeFor(mat), false, _viewProj);
        }
        FlushBatch();
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = null;
        _activeFrameSet = _frameSets[_currentFrame];
    }
}