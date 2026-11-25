using Paradise.Core.Diagnostics;
using Paradise.Core.Render.Original;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using System.Numerics;

namespace Paradise.Core.Render.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private const int OrigVsBytes = 256 * 16 + 16 + 64, OrigPsBytes = 224 * 16 + 16;   // VS: c[256], ext, Weltmatrix (Lichtpuffer)
    private const ulong OrigRingPerFrame = 32UL << 20;
    private const int OrigMaxSets = 16384;

    private DescriptorSetLayout _origSetLayout;
    private PipelineLayout _origLayout;
    private DescriptorPool _origPool;
    private Silk.NET.Vulkan.Buffer _origRing, _origZeroVb;
    private GpuAllocator.Allocation _origRingMem, _origZeroMem;
    private ulong _origAlign = 256, _origUsed, _origVsSize, _origPsSize;
    private bool _origInit, _origFailed;
    private VulkanTexture? _origWhite, _origBlack, _origClear, _origLightBuf, _origLightBufIn;
    private OriginalScene _origScene;
    private Image _origCube; private GpuAllocator.Allocation _origCubeMem; private ImageView _origCubeView;
    private readonly Dictionary<(OriginalProgram, int, int), Pipeline> _origPipes = new();
    private readonly List<(VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model)> _origDraws = new();
    private ShaderCompiler? _origCompiler;

    /// <summary>Zeichnet der Original-Renderer dieses Material (sonst eigener Shader)?</summary>
    private bool UseOriginal(VulkanMaterial m) => m.Orig != null && m.OrigSet.Handle != 0 && GraphicsSettings.RenderUseOriginal && !_origFailed;

    private void EnsureOriginal()
    {
        if (_origInit) return;
        _origInit = true;
        try
        {
            _vk.GetPhysicalDeviceProperties(_physical, out var props);
            _origAlign = System.Math.Max(16UL, props.Limits.MinUniformBufferOffsetAlignment);
            _origVsSize = Align((ulong)OrigVsBytes);
            _origPsSize = Align((ulong)OrigPsBytes);

            var b = stackalloc DescriptorSetLayoutBinding[18];
            for (int i = 0; i < 18; i++)
                b[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)i,
                    DescriptorType = i < 2 ? DescriptorType.UniformBufferDynamic : DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
                };
            var li = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 18, PBindings = b };
            if (_vk.CreateDescriptorSetLayout(_device, in li, null, out _origSetLayout) != Result.Success) throw new Exception("Original: SetLayout");
            // Set 1 = Frame-Set der Engine (Schattenkaskaden für den Lichtpuffer s15, siehe DxsoToGlsl)
            var sls = stackalloc DescriptorSetLayout[2] { _origSetLayout, _frameSetLayout };
            var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = sls };
            if (_vk.CreatePipelineLayout(_device, in pli, null, out _origLayout) != Result.Success) throw new Exception("Original: PipelineLayout");

            var sizes = stackalloc DescriptorPoolSize[2];
            sizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = OrigMaxSets * 2 };
            sizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = OrigMaxSets * 16 };
            var pi = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = OrigMaxSets, PoolSizeCount = 2, PPoolSizes = sizes
            };
            if (_vk.CreateDescriptorPool(_device, in pi, null, out _origPool) != Result.Success) throw new Exception("Original: DescriptorPool");

            CreateHostBuffer(OrigRingPerFrame * MaxFramesInFlight, BufferUsageFlags.UniformBufferBit, out _origRing, out _origRingMem);
            CreateHostBuffer(64, BufferUsageFlags.VertexBufferBit, out _origZeroVb, out _origZeroMem);
            new Span<byte>((void*)_origZeroMem.Mapped, 64).Clear();
            ((float*)_origZeroMem.Mapped)[4] = 1f;   // Byte 16..31 = (1, 0, 0, 0): BLENDWEIGHT der vorgehäuteten Avatare (Knochen 0 = Einheit)

            // Platzhalter: fehlende Textur weiß; Lichtpuffer s15 = (Sonne sichtbar 1, kein Zusatzlicht, Faktor 1).
            // s295 hatte alle leeren Slots schwarz gebunden -> Cockpit dunkel, Spiegel falsch (GLOSS/SHADOW ohne Schlüssel). Schwarz nur
            // noch für Nachtlicht-Karten (OriginalMaterial.BlackMask), dort kam das nächtliche Leuchten der PMI-Objekte her.
            _origWhite = (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 255, 255, 255, 255 }, Linear = true, Name = "orig_white" });
            _origBlack = (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 0, 0, 0, 255 }, Linear = true, Name = "orig_black" });
            _origClear = (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 0, 0, 0, 0 }, Linear = true, Name = "orig_clear" });
            _origLightBuf = (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 255, 0, 0, 255 }, Linear = true, Name = "orig_lightbuffer", WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp });
            // Innenräume: Sonne verdeckt (x = 0), nur Umgebungslicht und Lightmaps [vermutet bis zum echten Lichtpuffer]
            _origLightBufIn = (VulkanTexture)CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 0, 0, 0, 255 }, Linear = true, Name = "orig_lightbuffer_in", WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp });
            CreateOriginalCube();
        }
        catch (Exception ex)
        {
            _origFailed = true;
            OriginalMaterial.LastError = ex.Message;
            Log.Error("Renderer: " + ex);
        }
    }

    private ulong Align(ulong v) => (v + _origAlign - 1) / _origAlign * _origAlign;

    /// <summary>Schwarzer 1x1-Würfel für Reflexions-Sampler (samplerCube), solange keine Umgebungs-Würfel geladen werden.</summary>
    private void CreateOriginalCube()
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, Flags = ImageCreateFlags.CreateCubeCompatibleBit, ImageType = ImageType.Type2D,
            Extent = new Extent3D(1, 1, 1), MipLevels = 1, ArrayLayers = 6, Format = Format.R8G8B8A8Unorm, Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined, Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            Samples = SampleCountFlags.Count1Bit, SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateImage(_device, in info, null, out _origCube) != Result.Success) throw new Exception("Original: Würfel");
        _vk.GetImageMemoryRequirements(_device, _origCube, out var req);
        _origCubeMem = _alloc.Allocate(req, MemoryPropertyFlags.DeviceLocalBit, linear: false);
        _vk.BindImageMemory(_device, _origCube, _origCubeMem.Memory, _origCubeMem.Offset);
        var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 6 };
        var vi = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = _origCube, ViewType = ImageViewType.TypeCube, Format = Format.R8G8B8A8Unorm, SubresourceRange = range };
        if (_vk.CreateImageView(_device, in vi, null, out _origCubeView) != Result.Success) throw new Exception("Original: Würfel-View");
        var cmd = BeginSingleTimeCommands();
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = _origCube, SubresourceRange = range,
            SrcAccessMask = 0, DstAccessMask = AccessFlags.TransferWriteBit
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toDst);
        var black = new ClearColorValue(0f, 0f, 0f, 0f);
        _vk.CmdClearColorImage(cmd, _origCube, ImageLayout.TransferDstOptimal, &black, 1, &range);
        var toRead = toDst;
        toRead.OldLayout = ImageLayout.TransferDstOptimal; toRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        toRead.SrcAccessMask = AccessFlags.TransferWriteBit; toRead.DstAccessMask = AccessFlags.ShaderReadBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in toRead);
        EndSingleTimeCommands(cmd);
    }

    /// <summary>Descriptor-Set des Original-Materials (bei jedem WriteMaterial neu, das alte wird verzögert freigegeben).</summary>
    private void WriteOriginalSet(VulkanMaterial mat, MaterialDesc d)
    {
        var old = mat.OrigSet;
        mat.OrigSet = default;
        mat.Orig = d.Original;
        if (old.Handle != 0) { var pool = _origPool; DeferDestroy(() => _vk.FreeDescriptorSets(_device, pool, 1, in old)); }
        if (d.Original == null) return;
        EnsureOriginal();
        if (_origFailed) return;
        var om = d.Original;
        // Lichtgläser der Autos: GLOSSFCT kommt je Bild aus VehicleLights (MaterialDesc.Lgt*), nicht aus der .2DM
        om.GlossFct = d.UseMask > 0.5f ? new Vector4(d.LgtBrake, d.LgtReverse, d.LgtLights, d.LgtBlink) : null;
        // Laufzeit-Textur eines Slots (Bilderrahmen-Foto, GAME_MODE_LIVING.SetFrameTexture)
        if (d.OriginalOverrideTex != null && d.OriginalOverrideSlot >= 0 && d.OriginalOverrideSlot < 16)
        {
            om.Slots[d.OriginalOverrideSlot] = d.OriginalOverrideTex;
            om.SlotWrapMask &= ~(1u << d.OriginalOverrideSlot);
        }
        if (d.DirtTex != null && !om.DirtDiag)
        {
            om.DirtDiag = true;
            bool vsDirt = false;
            foreach (var c in om.Program.Vs.Parsed!.Constants) 
                if (c.Name.StartsWith("DirtColor", StringComparison.Ordinal)) vsDirt = true;
        }
        // Beklebung (Livery/Sticker, MaterialDesc.StickerTex): Ebene STIKERS des Lacks (TD_CarPaint2 gStickerTexSampler s5)
        if (d.StickerTex != null && d.UseSticker > 0.5f) om.SetSlot(OriginalMaterial.SemStickers, d.StickerTex);
        // Dreck (Vehicles/CarDirt): dirt_scratch.2db in die DIRTCOLOR-Ebene, COLOR1 = (Dreckfarbe, Dreckstufe) – die Original-Lacke
        // mischen tex.x · COLOR1.a mit COLOR1.rgb (TD_CarPaint2 PS: mul r4, r4.x, v4; lrp r5, r4.w, …)
        if (d.DirtTex != null)
        {
            om.SetSlot(OriginalMaterial.SemDirtColor, d.DirtTex);
            om.SetSlot(OriginalMaterial.SemDirtScratch, d.DirtTex);   // TD_CarPaint2 gDirtScratchTexSampler s3
            static uint B(float x) => (uint)System.Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
            om.Dirt = new Vector4(d.DirtColor.X, d.DirtColor.Y, d.DirtColor.Z, d.DirtLevel);
            om.Color1 = B(d.DirtColor.X) | (B(d.DirtColor.Y) << 8) | (B(d.DirtColor.Z) << 16) | (B(d.DirtLevel) << 24);
        }
        BuildOriginalSet(mat, om);
    }

    /// <summary>
    /// Würfel-Sampler (Material-Ebene „CUBEMAP“ = Platzhalter, den das Spiel zur Laufzeit mit dem Umgebungswürfel belegt; Log:
    /// „TEXTUR FEHLT … Schlüssel 0050414d45425543“): Würfel der Reflexionssonde der Engine (folgt dem Spielerauto bzw. dem
    /// Interieur) [vermutet: entspricht dem Umgebungswürfel des Originals], solange sie gültig ist, sonst schwarz.
    /// </summary>
    private ImageView OriginalCubeView => _prCubeView.Handle != 0 && _probeValid ? _prCubeView : _origCubeView;

    private void BuildOriginalSet(VulkanMaterial mat, OriginalMaterial om)
    {
        var old = mat.OrigSet;
        mat.OrigSet = default;
        if (old.Handle != 0) { var pool = _origPool; DeferDestroy(() => _vk.FreeDescriptorSets(_device, pool, 1, in old)); }
        var cubeView = OriginalCubeView;
        bool cubeSrgb = cubeView.Handle == _prCubeView.Handle && _format.ToString().EndsWith("Srgb", StringComparison.Ordinal);
        om.CubeBound = 0;
        om.ProjBound = 0;
        om.RippleBound = 0;
        // TD_Road-Regen (exe 0xa630f0/0xa63440): s6 = aktuelles RIPPLE_xx, s8 = PUDDLES (WFX_WetGrd, World/WetGround)
        var rainRip = om.RainProgram != null && RenderFx.RainRipple is VulkanTexture rr && !rr.Disposed ? rr : null;
        var rainPud = om.RainProgram != null && RenderFx.RainPuddles is VulkanTexture rp && !rp.Disposed ? rp : null;
        bool plSrgb = _format.ToString().EndsWith("Srgb", StringComparison.Ordinal);
        var layout = _origSetLayout;
        var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _origPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        if (_vk.AllocateDescriptorSets(_device, in alloc, out var set) != Result.Success)
        {
            OriginalMaterial.LastError = "Descriptor-Pool voll";
            return;
        }
        var bufs = stackalloc DescriptorBufferInfo[2];
        bufs[0] = new DescriptorBufferInfo { Buffer = _origRing, Offset = 0, Range = OrigVsBytes };
        bufs[1] = new DescriptorBufferInfo { Buffer = _origRing, Offset = 0, Range = OrigPsBytes };
        var imgs = stackalloc DescriptorImageInfo[16];
        var writes = stackalloc WriteDescriptorSet[18];
        uint mask = 0;
        for (int i = 0; i < 16; i++)
        {
            int type = om.SamplerType(i);
            if (type == 3)
            {
                imgs[i] = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = cubeView, Sampler = GetSampler(TextureWrap.Clamp, TextureWrap.Clamp) };
                om.CubeBound = cubeView.Handle;
                if (cubeSrgb) mask |= 1u << i;   // sRGB-Sonde: Shader rechnet wie bei den Texturen auf Gammawerte zurück
            }
            else if (((om.ProjMask >> i) & 1) != 0 && _plView.Handle != 0)
            {
                // Bodenspiegelbild (gleiches Bildformat wie die Swapchain) als projizierte Spiegelung
                imgs[i] = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _plView, Sampler = GetSampler(TextureWrap.Clamp, TextureWrap.Clamp) };
                om.ProjBound = _plView.Handle;
                if (plSrgb) mask |= 1u << i;
            }
            else
            {
                var t = om.Slots[i] as VulkanTexture;
                // Adressmodus aus der Material-Ebene (s309, OriginalMaterial.SlotWrapU/V), nicht aus der (geteilten) Textur
                bool layerWrap = t != null && !t.Disposed && ((om.SlotWrapMask >> i) & 1) != 0;
                if ((t == null || t.Disposed) && i == 6 && rainRip != null) { t = rainRip; om.RippleBound = rainRip.View.Handle; }
                if ((t == null || t.Disposed) && i == 8 && rainPud != null) t = rainPud;
                if (t == null || t.Disposed) t = i == 15 ? (om.Interior ? _origLightBufIn! : _origLightBuf!) : ((om.ClearMask >> i) & 1) != 0 ? _origClear! : ((om.BlackMask >> i) & 1) != 0 ? _origBlack! : _origWhite!;
                else if (!t.Linear) mask |= 1u << i;   // sRGB-Bild: Shader rechnet auf die gespeicherten Werte zurück
                var smp = layerWrap ? GetSampler(om.SlotWrapU[i], om.SlotWrapV[i]) : GetSampler(t.WrapU, t.WrapV);
                imgs[i] = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = t.View, Sampler = smp };
            }
        }
        for (int i = 0; i < 18; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DstArrayElement = 0, DescriptorCount = 1,
                DescriptorType = i < 2 ? DescriptorType.UniformBufferDynamic : DescriptorType.CombinedImageSampler,
                PBufferInfo = i < 2 ? bufs + i : null,
                PImageInfo = i < 2 ? null : imgs + (i - 2)
            };
        }
        _vk.UpdateDescriptorSets(_device, 18, writes, 0, null);
        mat.OrigSet = set;
        mat.OrigMask = mask;
    }

    private void FreeOriginalSet(VulkanMaterial mat)
    {
        var set = mat.OrigSet;
        mat.OrigSet = default;
        if (set.Handle == 0) return;
        var pool = _origPool;
        DeferDestroy(() => _vk.FreeDescriptorSets(_device, pool, 1, in set));
    }

    // Pipelines entstehen im Hintergrund (vorher synchron beim ersten Zeichnen: Shader laden + Pipeline bauen im Render-Thread ->
    // Ruckler bei jedem neu gestreamten Material). Bis sie fertig ist, zeichnet der eigene Shader das Objekt.
    private readonly Dictionary<(OriginalProgram, int, int), Task<Pipeline>> _origPending = new();
    private int _origGen;
    private readonly object _origCompileLock = new();

    /// <summary>Pipeline je Shader-Paar und Cull-Modus (0 keiner, 1 Front, 2 Back – wie _cullPipes/_cullPipesInv);
    /// default = noch nicht fertig bzw. fehlgeschlagen (Aufrufer zeichnet dann mit dem eigenen Shader).</summary>
    private Pipeline GetOriginalPipeline(OriginalProgram prog, int cull, int states)
    {
        var key = (prog, cull, states);
        if (_origPipes.TryGetValue(key, out var p)) return p;
        if (_origPending.TryGetValue(key, out var task))
        {
            if (!task.IsCompleted) return default;
            _origPending.Remove(key);
            p = task.IsCompletedSuccessfully ? task.Result : default;
            _origPipes[key] = p;   // auch fehlgeschlagene merken (nicht jedes Bild neu versuchen)
            if (p.Handle != 0) OriginalMaterial.Pipelines++;
            return p;
        }
        int gen = _origGen;
        _origPending[key] = Task.Run(() =>
        {
            var built = BuildOriginalPipeline(prog, cull, states);
            // Render-Pass inzwischen neu (MSAA/Swapchain): veraltete Pipeline gleich wieder weg
            if (gen != _origGen && built.Handle != 0) { _vk.DestroyPipeline(_device, built, null); built = default; }
            return built;
        });
        return default;
    }

    private Pipeline BuildOriginalPipeline(OriginalProgram prog, int cull, int states)
    {
        Pipeline p = default;
        ShaderModule vm = default, fm = default;
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var vs = prog.Vs.Parsed!; var ps = prog.Ps.Parsed!;
            vm = CreateShaderModule(OriginalSpirv(prog.Vs.Bytecode, vs, prog.Name + ".vert"));
            fm = CreateShaderModule(OriginalSpirv(prog.Ps.Bytecode, ps, prog.Name + ".frag"));
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vm, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fm, PName = entry };

            // VS-Eingänge (dcl vN, Usage) -> Vertexformat der Engine: Binding 0 = ObjectVertex bzw. MeshVertex (Gelände, States 0x800),
            // Binding 1 = Tangentenpuffer (TANGENT @0, BINORMAL @16, berechnet), Binding 2 = Konstantenpuffer mit Schrittweite 0
            // (BLENDWEIGHT = (1,0,0,0), sonst 0: Avatare kommen vorgehäutet, Knochen 0 = Einheitsmatrix)
            bool meshFmt = (states & 0x800) != 0;
            // States 0x1000: TD_HeightMap-Datenstrom (Binding 1, je Vertex Höhe, Index x, Index z, 0 – siehe EnsureHeightMapStream)
            bool hmFmt = (states & 0x1000) != 0;
            var attrs = stackalloc VertexInputAttributeDescription[16];
            uint na = 0;
            foreach (var d in vs.Decls)
            {
                if (d.RegType != 1 || na >= 16) continue;
                if (d.Usage == 10 && d.UsageIndex == 1) continue;   // COLOR1 kommt aus VS ext.z (DxsoToGlsl, Dreck)
                var a = new VertexInputAttributeDescription { Location = (uint)d.Register, Binding = 0 };
                switch (d.Usage)
                {
                    case 0 when d.UsageIndex == 0 && hmFmt: a.Binding = 1; a.Format = Format.R32G32B32Sfloat; a.Offset = 0; break;   // POSITION.x = Höhe
                    case 5 when d.UsageIndex == 0 && hmFmt: a.Binding = 1; a.Format = Format.R32G32Sfloat; a.Offset = 4; break;      // TEXCOORD0 = Rasterindex
                    case 0 when d.UsageIndex == 0: a.Format = Format.R32G32B32Sfloat; a.Offset = 0; break;    // POSITION
                    case 3 when d.UsageIndex == 0: a.Format = Format.R32G32B32Sfloat; a.Offset = 12; break;   // NORMAL
                    case 10 when d.UsageIndex == 0 && !meshFmt: a.Format = Format.R8G8B8A8Unorm; a.Offset = 24; break;    // COLOR0
                    // TEXCOORDk = ObjectVertex.Uv k; bei 4 UV-Sätzen (States 0x2000) liegen Satz 2/3 vertauscht in Uv3/Uv2 (MeshData)
                    case 5 when d.UsageIndex < 4 && !meshFmt:
                        a.Format = Format.R32G32Sfloat;
                        int uvSet = (states & 0x2000) != 0 && d.UsageIndex >= 2 ? 5 - d.UsageIndex : d.UsageIndex;
                        a.Offset = (uint)(28 + 8 * uvSet);
                        break;
                    case 5 when d.UsageIndex == 0 && meshFmt: a.Format = Format.R32G32Sfloat; a.Offset = 24; break;   // MeshVertex.Uv
                    case 6 when d.UsageIndex == 0: a.Binding = 1; a.Format = Format.R32G32B32A32Sfloat; a.Offset = 0; break;    // TANGENT
                    case 7 when d.UsageIndex == 0: a.Binding = 1; a.Format = Format.R32G32B32A32Sfloat; a.Offset = 16; break;   // BINORMAL
                    case 1: a.Binding = 2; a.Format = Format.R32G32B32A32Sfloat; a.Offset = 16; break;                          // BLENDWEIGHT
                    default: a.Binding = 2; a.Format = Format.R32G32B32A32Sfloat; a.Offset = 0; break;
                }
                attrs[na++] = a;
            }
            var binds = stackalloc VertexInputBindingDescription[3];
            binds[0] = new VertexInputBindingDescription { Binding = 0, Stride = (uint)(meshFmt ? (int)MeshVertex.SizeInBytes : (int)ObjectVertex.SizeInBytes), InputRate = VertexInputRate.Vertex };
            binds[1] = new VertexInputBindingDescription { Binding = 1, Stride = hmFmt ? 16u : 32u, InputRate = VertexInputRate.Vertex };
            binds[2] = new VertexInputBindingDescription { Binding = 2, Stride = 0, InputRate = VertexInputRate.Vertex };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 3, PVertexBindingDescriptions = binds,
                VertexAttributeDescriptionCount = na, PVertexAttributeDescriptions = attrs
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1f,
                CullMode = cull == 0 ? CullModeFlags.None : cull == 1 ? CullModeFlags.FrontBit : CullModeFlags.BackBit,
                FrontFace = FrontFace.Clockwise
            };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = _msaa };
            // Render-States der .2DM (OriginalMaterial.States): D3DBLEND Quelle/Ziel. Bloom-Maske (Alpha, Zusatz PostFx):
            // opak = 0 wie die übrigen nicht leuchtenden Objekte, gemischt = unverändert
            bool blended = (states & 0x100) != 0;
            var cba = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = blended ? D3DBlend(states & 0xF) : BlendFactor.One,
                DstColorBlendFactor = blended ? D3DBlend((states >> 4) & 0xF) : BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero, DstAlphaBlendFactor = blended ? BlendFactor.One : BlendFactor.Zero, AlphaBlendOp = BlendOp.Add
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cba };
            var dynStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = (states & 0x400) != 0, DepthWriteEnable = (states & 0x200) != 0,
                DepthCompareOp = CompareOp.GreaterOrEqual   // Reversed-Z wie der Hauptdurchgang
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depth,
                PColorBlendState = &colorBlend, PDynamicState = &dynamic, Layout = _origLayout, RenderPass = _renderPass, Subpass = 0
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out p) != Result.Success) throw new Exception("Pipeline " + prog.Name);
        }
        catch (Exception ex)
        {
            p = default;
            OriginalMaterial.LastError = prog.Name + ": " + ex.Message.Split('\n')[0];
            Log.Error("Renderer: " + prog.Name + ": " + ex.Message);
        }
        finally
        {
            if (vm.Handle != 0) _vk.DestroyShaderModule(_device, vm, null);
            if (fm.Handle != 0) _vk.DestroyShaderModule(_device, fm, null);
            SilkMarshal.Free((nint)entry);
        }
        return p;
    }

    /// <summary>D3DBLEND (1 ZERO, 2 ONE, 3 SRCCOLOR, 4 INVSRCCOLOR, 5 SRCALPHA, 6 INVSRCALPHA, 7 DESTALPHA, 8 INVDESTALPHA, 9 DESTCOLOR, 10 INVDESTCOLOR).</summary>
    private static BlendFactor D3DBlend(int b) => b switch
    {
        1 => BlendFactor.Zero, 2 => BlendFactor.One, 3 => BlendFactor.SrcColor, 4 => BlendFactor.OneMinusSrcColor,
        5 => BlendFactor.SrcAlpha, 6 => BlendFactor.OneMinusSrcAlpha, 7 => BlendFactor.DstAlpha, 8 => BlendFactor.OneMinusDstAlpha,
        9 => BlendFactor.DstColor, 10 => BlendFactor.OneMinusDstColor, _ => BlendFactor.One,
    };

    private byte[] OriginalSpirv(byte[] bytecode, DxsoShader sh, string name)
    {
        var spv = ShaderPrecompile.TryLoad(bytecode);
        if (spv != null) return spv;
        lock (_origCompileLock)
        {
            _origCompiler ??= new ShaderCompiler();
            return _origCompiler.Compile(DxsoToGlsl.Translate(sh).Glsl, sh.IsPixel ? ShaderKind.FragmentShader : ShaderKind.VertexShader, name);
        }
    }

    private void DestroyOriginalPipelines()
    {
        _origGen++;
        foreach (var t in _origPending.Values)
        {
            try { t.Wait(); } catch { }
            if (t.IsCompletedSuccessfully && t.Result.Handle != 0) _vk.DestroyPipeline(_device, t.Result, null);
        }
        _origPending.Clear();
        foreach (var p in _origPipes.Values) if (p.Handle != 0) _vk.DestroyPipeline(_device, p, null);
        _origPipes.Clear();
        OriginalMaterial.Pipelines = 0;
    }

    /// <summary>Szenenwerte in der Konvention des Originals (siehe <see cref="OriginalScene"/>).</summary>
    private OriginalScene BuildOriginalScene()
    {
        var g = _globals;
        var o = g.OrigLight;
        // Engine: rechtshändige View, Projektion mit Vulkan-Y-Spiegelung und Reversed-Z.
        // Original: linkshändige View (+Z nach vorn) -> Z der View spiegeln (und in der Projektion zurück); die Y-Spiegelung
        // macht der übersetzte VS (gl_Position.y = -y) -> hier herausnehmen. View x Proj ergibt dieselben Clip-Koordinaten.
        var flipZ = Matrix4x4.CreateScale(1, 1, -1);
        var flipY = Matrix4x4.CreateScale(1, -1, 1);
        var view = _view * flipZ;
        // Tiefe wie D3D (nah 0, fern 1): Die Original-VS verschieben die Clip-Tiefe selbst – Kennzeichen td_carplate -0,002,
        // Lack TD_CarPaint -0,0002, td_pubneon/TD_NEONDECAL -8e-5 (Abziehbilder nach vorn), td_dome z = w (Himmel auf die ferne Ebene),
        // TD_Vegetation Nebel aus z. Mit der Reversed-Z-Projektion der Engine wirkten diese Werte umgekehrt (Kennzeichen hinter der
        // Karosserie). D3D-Clip = Engine-Clip mit z' = w - z; der übersetzte VS rechnet für den Tiefenpuffer zurück (DxsoToGlsl, v9).
        var toD3dDepth = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1);
        var proj = flipZ * _proj * flipY * toD3dDepth;
        var fwd = -new Vector3(_view.M13, _view.M23, _view.M33);
        float t = g.Time > 0 ? g.Time : (float)_clock.Elapsed.TotalSeconds;
        return new OriginalScene
        {
            View = view, Proj = proj, ViewProj = view * proj,
            CameraPosition = g.CameraPosition,
            CameraDirection = fwd.LengthSquared() > 0 ? Vector3.Normalize(fwd) : Vector3.UnitZ,
            SunDirection = -Vector3.Normalize(g.SunDirection),   // Engine: zur Sonne; Original "sundirection": Lichtrichtung
            // Wetterwerte unverändert (FrameGlobals.OrigLight, Gamma-Raum); ohne Wetter die Engine-Werte
            SunDiffuse = o.Set ? o.SunDiffuse : g.SunColor + g.Ambient,
            SunSpecular = o.Set ? o.SunSpecular : g.SunColor + g.Ambient,
            Ambient = o.Set ? o.Ambient : g.Ambient,
            FogColor = o.Set ? o.FogColor : g.FogColor,
            // sunflare_core_emissive.x (FrameGlobals.CloudMisc.y); nur x im Wetter gelesen -> grau [vermutet: Farbe = x,x,x]
            SunEmissive = new Vector3(g.CloudMisc.Y),
            FogConfig = o.Set ? o.FogConfig : new Vector4(g.FogStart, g.FogEnd, g.FogEnd > g.FogStart ? 1f : 0f, 1f / System.Math.Max(g.FogEnd - g.FogStart, 1f)),
            Time = t,
            // FUN_00d769e0: UserVector1 = (1, outside_street_lightmap, 1, building_nightwindow_frequency), UserVector2 = (shop.xy, 0, 0)
            User1 = o.Set ? new Vector4(1f, o.Street, 1f, o.NightWindows) : new Vector4(1f, g.StreetLight, 1f, g.NightWindows),
            User2 = o.Set ? new Vector4(o.Shop, 0f, 0f) : Vector4.Zero,
            Width = _extent.Width, Height = _extent.Height,
            Origin = g.Origin,
            CarOn = g.CarLight.On,
            CarAmbient = g.CarLight.Ambient, CarDiffuse = g.CarLight.Diffuse, CarSpecular = g.CarLight.Specular,
            CarEnv = g.CarLight.EnvCoeff, CarEnvFactor = g.CarLight.EnvFactor,
            DashHeadlights = g.DashHeadlights,
            // WFX_WetGrd (exe 0xa63440): RainSpecs = (+0x364 rain_ground_global_intensity, +0x374 rain_ground_wet, 1, +0x370 puddle_height),
            // RainRippleSpecs = (+0x310 scroll_u, +0x314 scroll_v, +0x368 ripple_intensity, +0x36c ripple_scale)
            RainSpecs = g.RainSpecs,
            RainRipple = new Vector4(RenderFx.RainScroll.X, RenderFx.RainScroll.Y, g.RainRipple.Z, g.RainRipple.W),
            // s309: NIGHTLMAP der Spot-Exteriors (ohne Wetter: Tag = (1, 0), Grundwert der Registrierung)
            SpotExteriorDayNight = o.Set ? o.SpotExteriorDayNight : new Vector2(1f, 0f),
        };
    }

    /// <summary>Gesammelte opake Original-Aufrufe zeichnen (nach den übrigen opaken Objekten im Hauptdurchgang).
    /// Setzt auch Szene und Ring des Bildes zurück – die transparenten Original-Aufrufe (<see cref="DrawOriginalNow"/>) folgen danach.</summary>
    private void DrawOriginals(CommandBuffer cmd)
    {
        _origUsed = 0;
        _origScene = BuildOriginalScene();
        int drawn = 0;
        foreach (var (mesh, mat, model) in _origDraws)
            if (DrawOriginalNow(cmd, mesh, mat, model)) drawn++;
        _origDraws.Clear();
        OriginalMaterial.DrawnLastFrame = drawn;
        EndOriginal();
    }

    /// <summary>Danach zeichnet wieder der eigene Renderer: Pipeline, Frame-Set, Material-Set und Vertexpuffer neu binden.</summary>
    private void EndOriginal()
    {
        _boundPipeline = null;
        _lastSet = default;
        _lastVb = default;
        _origBound = default;
        _origLastVb = 0;
        _origLastHm = false;
    }

    private Pipeline _origBound;
    private ulong _origLastVb;
    private bool _origLastHm;

    /// <summary>Ein Original-Aufruf (false = mit dem eigenen Shader gezeichnet: Pipeline fehlt oder Ring voll).</summary>
    private bool DrawOriginalNow(CommandBuffer cmd, VulkanMesh mesh, VulkanMaterial mat, Matrix4x4 model, bool sorted = false)
    {
        var om = mat.Orig!;
        // Würfel-Sampler: Sonde wurde gültig bzw. neu angelegt (Größenänderung) -> Set neu schreiben
        // TD_Road bei Regen (RainSpecs.x = rain_ground_global_intensity > 0, exe 0xa630f0): Regen-Permutation; das RIPPLE-Bild wechselt
        // alle 0,025 s (exe 0xa63440 setzt den Slot-Zeiger je Bild) -> Set dann neu schreiben
        bool wet = om.RainProgram != null && _origScene.RainSpecs.X > 0f;
        var prog = wet ? om.RainProgram! : om.Program;
        ulong ripNow = wet && RenderFx.RainRipple is VulkanTexture rq && !rq.Disposed ? rq.View.Handle : 0;
        if ((om.CubeBound != 0 && om.CubeBound != OriginalCubeView.Handle) || (om.ProjBound != 0 && om.ProjBound != _plView.Handle)
            || (ripNow != 0 && om.RippleBound != ripNow)) BuildOriginalSet(mat, om);
        int cull = mat.TwoSided ? 0 : (Det3(_viewProj) > 0f) ^ (Det3(model) < 0f) ? 2 : 1;
        bool hm = om.HeightMapInput && !mesh.IsObjectFormat;
        int states = om.States | (mesh.IsObjectFormat ? 0 : 0x800) | (hm ? 0x1000 : 0) | (mesh.IsObjectFormat && mesh.FourUvSets ? 0x2000 : 0);
        var pipe = _origFailed ? default : GetOriginalPipeline(prog, cull, states);
        bool tanOk = !om.NeedsTangents || EnsureTangents(mesh);
        bool hmOk = !hm || EnsureHeightMapStream(mesh);
        //if (RoadDiagWanted(om, prog))
        //    RoadDiag(mesh, om, prog, states, wet, pipe.Handle == 0 ? "Pipeline noch nicht fertig" : !tanOk ? "Tangenten fehlen" : !hmOk ? "HeightMap-Strom fehlt"
        //        : mat.OrigSet.Handle == 0 ? "kein Descriptor-Set" : _origUsed + _origVsSize + _origPsSize > OrigRingPerFrame ? "Ring voll" : null);
        if (pipe.Handle == 0 || !tanOk || !hmOk || mat.OrigSet.Handle == 0 || _origUsed + _origVsSize + _origPsSize > OrigRingPerFrame)
        {
            EndOriginal();
            if (sorted) DrawNow(mesh, mat, model);
            else { Batch(mesh, mat, model, PipeFor(mat), false, _viewProj); FlushBatch(); }
            EndOriginal();
            return false;
        }
        ulong frameBase = (ulong)_currentFrame * OrigRingPerFrame;
        ulong vsOff = _origUsed, psOff = _origUsed + _origVsSize;
        _origUsed += _origVsSize + _origPsSize;
        byte* ring = (byte*)_origRingMem.Mapped;
        byte* vsp = ring + frameBase + vsOff, psp = ring + frameBase + psOff;
        var fam = om.Avatar != null ? om.Family : OriginalMaterial.AvatarFamily.None;
        OriginalConstants.Fill((float*)vsp, 256, OriginalConstants.EntriesOf(prog.Vs.Parsed!), om.Def, model, _origScene, om.Over, om.Avatar, fam, om.GlossFct, om);
        OriginalConstants.Fill((float*)psp, 224, OriginalConstants.EntriesOf(prog.Ps.Parsed!), om.Def, model, _origScene, om.Over, om.Avatar, fam, om.GlossFct, om);
        var ve = (uint*)(vsp + 256 * 16); ve[0] = mat.OrigMask; ve[1] = hm ? 1u : 0u; ve[2] = om.Color1; ve[3] = 0;
        *(Matrix4x4*)(vsp + 256 * 16 + 16) = model;   // Weltlage für den Lichtpuffer (Render-Raum wie die Schattenkaskaden)
        // PS ext: x sRGB-Sampler, y Ergebnis nach linear, z Alpha-Referenz (float), w Alpha-Test an (D3DRS_ALPHATESTENABLE)
        var pe = (uint*)(psp + 224 * 16); pe[0] = mat.OrigMask; pe[1] = 1; pe[2] = BitConverter.SingleToUInt32Bits(om.AlphaRef); pe[3] = om.AlphaTest ? 1u : 0u;

        if (pipe.Handle != _origBound.Handle) { _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipe); _origBound = pipe; }
        var sets = stackalloc DescriptorSet[2] { mat.OrigSet, _activeFrameSet };
        var dyn = stackalloc uint[2];
        dyn[0] = (uint)(frameBase + vsOff); dyn[1] = (uint)(frameBase + psOff);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _origLayout, 0, 2, sets, 2, dyn);
        if (mesh.VertexBuffer.Handle != _origLastVb || hm != _origLastHm)
        {
            var vbs = stackalloc Silk.NET.Vulkan.Buffer[3];
            var offs = stackalloc ulong[3];
            vbs[0] = mesh.VertexBuffer; vbs[1] = hm ? mesh.HmBuf : mesh.TanBuf.Handle != 0 ? mesh.TanBuf : _origZeroVb; vbs[2] = _origZeroVb;
            offs[0] = 0; offs[1] = 0; offs[2] = 0;
            _vk.CmdBindVertexBuffers(cmd, 0, 3, vbs, offs);
            _vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
            _origLastVb = mesh.VertexBuffer.Handle;
            _origLastHm = hm;
        }
        _vk.CmdDrawIndexed(cmd, (uint)mesh.IndexCount, 1, 0, 0, 0);
        _cDraw++; _cTris += mesh.IndexCount / 3;
        // der eigene Renderer muss danach neu binden
        _boundPipeline = null; _lastSet = default; _lastVb = default;
        return true;
    }

    // ------------------------------------------------------------------ s308 Prüf-Log Straßendetails (TD_Road MARKS/MARKS2)
    /// <summary>Höchstens so viele TD_Road-Materialien protokollieren (je Material einmal, dazu bis zu 3 Meshes).</summary>
    private const int RoadDiagMax = 40;
    private readonly HashSet<OriginalMaterial> _roadDiagDone = new(), _roadDiagFallback = new();
    private readonly Dictionary<OriginalMaterial, int> _roadDiagMeshCount = new();
    private readonly HashSet<ulong> _roadDiagMeshSeen = new();

    /// <summary>TD_Road-Material mit Markierungen (Ebene MARKS*/Sampler gTexSamplerMarks*) – nur diese gehen ins Prüf-Log.</summary>
    private bool RoadDiagWanted(OriginalMaterial om, OriginalProgram prog)
    {
        if (!string.Equals(prog.Library.Name, "TD_Road", StringComparison.OrdinalIgnoreCase)) return false;
        if (_roadDiagDone.Contains(om)) return _roadDiagMeshCount.TryGetValue(om, out int n) && n < 3;
        if (_roadDiagDone.Count >= RoadDiagMax) return false;
        foreach (var l in om.Def.Layers) if (l.SemanticName.StartsWith("MARKS", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in prog.Ps.Parsed!.Constants) if (c.RegisterSet == 3 && c.Name.StartsWith("gTexSamplerMarks", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Prüf-Log „[DIAG Straße]“ (s308): gewählte Permutation, Sampler des PS, VS-Eingänge -> Engine-UV-Satz (mit Tausch 0x2000),
    /// Ebenen der .2DM, tatsächlich gebundene Texturen je Slot (Größe, Mip-Stufen, ausgelassene Stufen, Adressmodus, sRGB bzw.
    /// Platzhalter) und der UV-Bereich der ersten Meshes. <paramref name="fallback"/> != null: noch mit dem eigenen Shader gezeichnet.
    /// </summary>
    private void RoadDiag(VulkanMesh mesh, OriginalMaterial om, OriginalProgram prog, int states, bool wet, string? fallback)
    {
        bool first = !_roadDiagDone.Contains(om);
        if (fallback != null)
        {
            if (first && _roadDiagFallback.Add(om)) Log.Info($"[DIAG Straße] '{om.Def.Name}' {prog.Name}: noch eigener Shader ({fallback})");
            return;
        }
        var sb = new System.Text.StringBuilder();
        if (first)
        {
            _roadDiagDone.Add(om);
            _roadDiagMeshCount[om] = 0;
            sb.Append($"[DIAG Straße] '{om.Def.Name}' {prog.Name} (Abweichungen {prog.Differences}{(wet ? ", Regen" : "")}) States 0x{states:x}, ")
              .Append((states & 0x2000) != 0 ? "4 UV-Sätze (TEXCOORD2/3 getauscht)" : "ohne 4-UV-Kennung");
            sb.Append("\n    PS-Sampler:");
            foreach (var c in prog.Ps.Parsed!.Constants) if (c.RegisterSet == 3) sb.Append($" s{c.Register}={c.Name}");
            sb.Append("\n    VS-Eingänge:");
            foreach (var d in prog.Vs.Parsed!.Decls)
            {
                if (d.RegType != 1 || d.Usage != 5) continue;
                int uvSet = (states & 0x2000) != 0 && d.UsageIndex >= 2 ? 5 - d.UsageIndex : d.UsageIndex;
                sb.Append($" TEXCOORD{d.UsageIndex}->Uv{uvSet}");
            }
            sb.Append("\n    Ebenen (.2DM):");
            foreach (var l in om.Def.Layers) sb.Append($" {l.SemanticName}@s{l.Slot} {l.TextureKey:x16} Adr {l.AddressU}/{l.AddressV};");
            for (int i = 0; i < 16; i++)
            {
                int type = om.SamplerType(i);
                if (type == 0) continue;
                sb.Append($"\n    s{i}: ");
                if (type == 3) { sb.Append("Würfel (Umgebung)"); continue; }
                if (om.Slots[i] is VulkanTexture t && !t.Disposed)
                {
                    bool lw = ((om.SlotWrapMask >> i) & 1) != 0;
                    sb.Append($"{t.Name ?? "?"} {t.Width}x{t.Height} {t.Format} Mips {t.Levels} (ausgelassen {t.SkipLevels}) Sampler {(lw ? om.SlotWrapU[i] : t.WrapU)}/{(lw ? om.SlotWrapV[i] : t.WrapV)}")
                      .Append(lw ? " (Ebene)" : " (Textur)").Append($" Textur angelegt {t.WrapU}/{t.WrapV} {(t.Linear ? "linear" : "sRGB")}");
                }
                else if (i == 15) sb.Append("Lichtpuffer (im PS berechnet)");
                else if (i == 6 && wet) sb.Append("RIPPLE (Regen, Renderer)");
                else if (i == 8 && wet) sb.Append("PUDDLES (Regen, Renderer)");
                else if (((om.BlackMask >> i) & 1) != 0) sb.Append("Platzhalter schwarz (keine Textur)");
                else sb.Append("Platzhalter WEISS (keine Textur!)");
            }
        }
        if (mesh.IsObjectFormat && mesh.VMem.Mapped != null && mesh.VCount > 0 && _roadDiagMeshSeen.Add((ulong)mesh.SortId))
        {
            _roadDiagMeshCount[om] = (_roadDiagMeshCount.TryGetValue(om, out int nm) ? nm : 0) + 1;
            var lo = new Vector2[4]; var hi = new Vector2[4];
            for (int k = 0; k < 4; k++) { lo[k] = new Vector2(float.MaxValue); hi[k] = new Vector2(float.MinValue); }
            byte* vb = (byte*)mesh.VMem.Mapped;
            int n = System.Math.Min(mesh.VCount, 65536);
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 4; k++)
                {
                    var uv = *(Vector2*)(vb + (long)i * ObjectVertex.SizeInBytes + 28 + 8 * k);
                    lo[k] = Vector2.Min(lo[k], uv); hi[k] = Vector2.Max(hi[k], uv);
                }
            if (sb.Length == 0) sb.Append($"[DIAG Straße] '{om.Def.Name}'");
            sb.Append($"\n    Mesh {mesh.SortId} ({mesh.VCount} Vertices{(mesh.FourUvSets ? ", 4 UV-Sätze" : "")}) UV-Bereich:");
            for (int k = 0; k < 4; k++) sb.Append($" Uv{k} [{lo[k].X:0.###}; {lo[k].Y:0.###}]..[{hi[k].X:0.###}; {hi[k].Y:0.###}]");
        }
        if (sb.Length > 0) Log.Info(sb.ToString());
    }

    /// <summary>
    /// TANGENT/BINORMAL für die Original-Shader (die Engine-Formate haben keine): je Dreieck aus Position und UV0, je Vertex
    /// gemittelt und zur Normalen orthogonalisiert; Binormale = Normale x Tangente · Vorzeichen [vermutet: im Original vorberechnet
    /// in den .3DG-Daten]. Einmal je Mesh beim ersten Zeichnen (dynamische Meshes: Stand dieses Bildes).
    /// </summary>
    // Tangenten im Hintergrund (vorher im Render-Thread je neuem Mesh -> Ruckler beim Streamen); Daten vorher kopiert, damit ein
    // inzwischen freigegebenes Mesh nicht gelesen wird
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<VulkanMesh, Task<Vector4[]?>> _tanJobs = new();

    private bool EnsureTangents(VulkanMesh m)
    {
        if (m.TanBuf.Handle != 0) return true;
        if (_tanJobs.TryGetValue(m, out var job))
        {
            if (!job.IsCompleted) return false;
            _tanJobs.Remove(m);
            var res = job.IsCompletedSuccessfully ? job.Result : null;
            if (res == null) return false;
            CreateHostBuffer((ulong)(res.Length * 16), BufferUsageFlags.VertexBufferBit, out m.TanBuf, out m.TanMem);
            fixed (Vector4* src = res) System.Buffer.MemoryCopy(src, m.TanMem.Mapped, res.Length * 16, res.Length * 16);
            return true;
        }
        if (m.TanTried) return false;
        m.TanTried = true;
        int n = m.VCount;
        if (n <= 0 || m.VMem.Mapped == null || m.IMem.Mapped == null) return false;
        int stride = m.IsObjectFormat ? (int)ObjectVertex.SizeInBytes : (int)MeshVertex.SizeInBytes;
        int uvOff = m.IsObjectFormat ? 28 : 24;
        var vb = new byte[n * stride];
        var ib = new uint[m.IndexCount];
        fixed (byte* d = vb) System.Buffer.MemoryCopy(m.VMem.Mapped, d, vb.Length, vb.Length);
        fixed (uint* d = ib) System.Buffer.MemoryCopy(m.IMem.Mapped, d, ib.Length * 4L, ib.Length * 4L);
        _tanJobs.AddOrUpdate(m, Task.Run(() => ComputeTangents(vb, ib, n, stride, uvOff)));
        return false;
    }

    /// <summary>
    /// TD_HeightMap-Eingänge (siehe <see cref="OriginalMaterial.HeightMapInput"/>) aus dem Gelände-Mesh der Engine (MeshVertex,
    /// Sektor-lokal): (Höhe = Position.y, Index x = Position.x / 3, Index z = Position.z / 3, 0). Gilt ebenso für die eingesetzten
    /// BLOCK-Teile und Schürzen (beliebige Lage: der VS rechnet Index · 3 zurück). Einmal je Mesh, im Hintergrund wie die Tangenten.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<VulkanMesh, Task<Vector4[]?>> _hmJobs = new();

    private bool EnsureHeightMapStream(VulkanMesh m)
    {
        if (m.HmBuf.Handle != 0) return true;
        if (_hmJobs.TryGetValue(m, out var job))
        {
            if (!job.IsCompleted) return false;
            _hmJobs.Remove(m);
            var res = job.IsCompletedSuccessfully ? job.Result : null;
            if (res == null) return false;
            CreateHostBuffer((ulong)(res.Length * 16), BufferUsageFlags.VertexBufferBit, out m.HmBuf, out m.HmMem);
            fixed (Vector4* src = res) System.Buffer.MemoryCopy(src, m.HmMem.Mapped, res.Length * 16, res.Length * 16);
            return true;
        }
        if (m.HmTried) return false;
        m.HmTried = true;
        int n = m.VCount;
        if (n <= 0 || m.VMem.Mapped == null) return false;
        int stride = (int)MeshVertex.SizeInBytes;
        var vb = new byte[n * stride];
        fixed (byte* d = vb) System.Buffer.MemoryCopy(m.VMem.Mapped, d, vb.Length, vb.Length);
        _hmJobs.AddOrUpdate(m, Task.Run(() => ComputeHeightMapStream(vb, n, stride)));
        return false;
    }

    private static Vector4[]? ComputeHeightMapStream(byte[] vbData, int n, int stride)
    {
        var o = new Vector4[n];
        const float inv = 1f / OriginalMaterial.HeightMapSpacing;
        fixed (byte* vb = vbData)
            for (int i = 0; i < n; i++)
            {
                var p = *(Vector3*)(vb + i * stride);
                o[i] = new Vector4(p.Y, p.X * inv, p.Z * inv, 0f);
            }
        return o;
    }

    private static Vector4[]? ComputeTangents(byte[] vbData, uint[] ib, int n, int stride, int uvOff)
    {
        var tan = new Vector3[n]; var bit = new Vector3[n];
        var o = new Vector4[n * 2];
        fixed (byte* vb = vbData)
        {
            for (int t = 0; t + 2 < ib.Length; t += 3)
            {
                uint a = ib[t], b = ib[t + 1], c = ib[t + 2];
                if (a >= n || b >= n || c >= n) continue;
                Vector3 pa = *(Vector3*)(vb + a * stride), pb = *(Vector3*)(vb + b * stride), pc = *(Vector3*)(vb + c * stride);
                Vector2 ta = *(Vector2*)(vb + a * stride + uvOff), tb = *(Vector2*)(vb + b * stride + uvOff), tc = *(Vector2*)(vb + c * stride + uvOff);
                Vector3 e1 = pb - pa, e2 = pc - pa;
                Vector2 d1 = tb - ta, d2 = tc - ta;
                float det = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(det) < 1e-12f) continue;
                float r = 1f / det;
                var sdir = (e1 * d2.Y - e2 * d1.Y) * r;
                var tdir = (e2 * d1.X - e1 * d2.X) * r;
                tan[a] += sdir; tan[b] += sdir; tan[c] += sdir;
                bit[a] += tdir; bit[b] += tdir; bit[c] += tdir;
            }
            for (int i = 0; i < n; i++)
            {
                var nr = *(Vector3*)(vb + i * stride + 12);
                if (nr.LengthSquared() < 1e-12f) nr = Vector3.UnitY; else nr = Vector3.Normalize(nr);
                var t = tan[i] - nr * Vector3.Dot(nr, tan[i]);
                if (t.LengthSquared() < 1e-12f) t = Vector3.Cross(MathF.Abs(nr.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, nr);
                t = Vector3.Normalize(t);
                float w = Vector3.Dot(Vector3.Cross(nr, t), bit[i]) < 0f ? -1f : 1f;
                o[i * 2] = new Vector4(t, w);
                o[i * 2 + 1] = new Vector4(Vector3.Cross(nr, t) * w, 0f);
            }
        }
        return o;
    }

    private void DestroyOriginal()
    {
        DestroyOriginalPipelines();
        _origCompiler?.Dispose(); _origCompiler = null;
        if (!_origInit) return;
        if (_origCubeView.Handle != 0) _vk.DestroyImageView(_device, _origCubeView, null);
        if (_origCube.Handle != 0) { _vk.DestroyImage(_device, _origCube, null); _alloc.Free(_origCubeMem); }
        _origWhite?.Dispose(); _origBlack?.Dispose(); _origClear?.Dispose(); _origLightBuf?.Dispose(); _origLightBufIn?.Dispose();
        if (_origRing.Handle != 0) FreeBuffer(_origRing, _origRingMem);
        if (_origZeroVb.Handle != 0) FreeBuffer(_origZeroVb, _origZeroMem);
        if (_origPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _origPool, null);
        if (_origLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _origLayout, null);
        if (_origSetLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _origSetLayout, null);
        _origInit = false;
    }
}