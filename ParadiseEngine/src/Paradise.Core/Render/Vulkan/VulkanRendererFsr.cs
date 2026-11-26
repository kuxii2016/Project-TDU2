using System.Diagnostics;
using System.Numerics;
using Paradise.Core.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// FSR3-Upscaling über die offiziellen AMD ffx_api-DLLs: Szene in reduzierter Auflösung rendern,
/// Jitter + Velocity erzeugen, FFX-Dispatch, Ergebnis in Display-Größe an PostFx/Flash übergeben.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private Extent2D _renderExtent;
    private UpscalerMode _activeUpscaler;
    private UpscalerQuality _activeQuality;
    private UpscalerMode _wantedUpscaler;
    private UpscalerQuality _wantedQuality;
    private string _fsrStatus = "native";

    private nint _fsrContext;
    private nint _fsrDescMem;
    private nint _fsrDeviceContext;
    private ulong _fsrVersionId;
    private bool _fsrReady;
    private bool _fsrOutputReady;
    private int _fsrJitterIndex;
    private float _jitterX, _jitterY;
    private bool _fsrResetThisFrame;
    private bool _fsrHadFrame;
    private Matrix4x4 _prevViewProj;
    private Vector3 _prevCamPos;
    private long _lastFsrTick;
    private static nint _vulkanLibrary;

    private Image _fsrSceneImage, _fsrVelocityImage, _fsrOutputImage, _fsrExposureImage;
    private GpuAllocator.Allocation _fsrSceneMem, _fsrVelocityMem, _fsrOutputMem, _fsrExposureMem;
    private ImageView _fsrSceneView, _fsrVelocityView, _fsrOutputView;
    private Framebuffer _fsrSceneFb, _fsrVelocityFb;
    private RenderPass _fsrVelocityPass;
    private Pipeline _fsrVelocityPipe;
    private PipelineLayout _fsrVelocityLayout;
    private DescriptorSetLayout _fsrVelocitySetLayout;
    private DescriptorPool _fsrVelocityPool;
    private DescriptorSet _fsrVelocitySet;
    private Sampler _fsrDepthSampler;

    /// <summary>Vom Renderer tatsächlich verwendeter Pfad inkl. interner Auflösung.</summary>
    public string UpscalerStatus => _activeUpscaler switch
    {
        UpscalerMode.Off => _fsrStatus == "error" ? "error" : "native",
        UpscalerMode.Fsr3 => _fsrReady ? $"FSR3 {_renderExtent.Width}x{_renderExtent.Height}" : "error",
        UpscalerMode.Fsr1 => _fsrReady ? $"spatial {_renderExtent.Width}x{_renderExtent.Height}" : "error",
        _ => "error",
    };

    private Extent2D SceneExtent => _activeUpscaler == UpscalerMode.Off ? _extent : _renderExtent;
    private bool UpscaleScene => _activeUpscaler != UpscalerMode.Off && _fsrReady;

    private void ComputeFsrExtents()
    {
        float scale = _activeUpscaler == UpscalerMode.Off ? 1f : _activeQuality switch
        {
            UpscalerQuality.Quality => 1f / 1.5f,
            UpscalerQuality.Balanced => 1f / 1.7f,
            UpscalerQuality.Performance => 0.5f,
            _ => 1f / 3f,
        };
        _renderExtent = new Extent2D
        {
            Width = System.Math.Max(1u, (uint)(_extent.Width * scale)),
            Height = System.Math.Max(1u, (uint)(_extent.Height * scale)),
        };
    }

    private void TakeUpscalerSettings()
    {
        _wantedUpscaler = GraphicsSettings.Upscaler;
        _wantedQuality = GraphicsSettings.UpscalerQuality;
        // Beim ersten Umschalten von Off ist der Loader noch nicht initialisiert.
        // Init() muss hier passieren, sonst wird der Upscaler fälschlich als Off erkannt.
        bool available = _wantedUpscaler == UpscalerMode.Off || FfxApi.Init();
        _activeUpscaler = available ? _wantedUpscaler : UpscalerMode.Off;
        _activeQuality = _wantedQuality;
        if (!available) _fsrStatus = "error";
        if (_extent.Width != 0) ComputeFsrExtents();
    }

    private bool UpscalerSettingsChanged()
        => _wantedUpscaler != GraphicsSettings.Upscaler || _wantedQuality != GraphicsSettings.UpscalerQuality;

    // ------------------------------------------------------------------ Ressourcen

    private void CreateFsr()
    {
        DestroyFsr();
        if (_activeUpscaler == UpscalerMode.Off)
        {
            _fsrStatus = _wantedUpscaler == UpscalerMode.Off ? "native" : "error";
            return;
        }
        if (!FfxApi.Init())
        {
            Log.Warn("FSR: " + (FfxApi.InitError ?? "ffx_api nicht verfügbar") + " – nativer Pfad.");
            _activeUpscaler = UpscalerMode.Off;
            _fsrStatus = "error";
            ComputeFsrExtents();
            return;
        }
        ComputeFsrExtents();
        uint rw = _renderExtent.Width, rh = _renderExtent.Height;

        // Weltdurchgang: sRGB-Ansicht (Hardware kodiert linear -> sRGB). FFX liest dieselben Bytes
        // über eine UNORM-Ansicht und dekodiert/kodiert sRGB genau einmal selbst.
        Format sceneSampleFmt = UnormOf(_format);
        if (sceneSampleFmt == Format.Undefined) sceneSampleFmt = _format;
        if (sceneSampleFmt != _format)
            CreateMutableFormatImage(rw, rh, _format, sceneSampleFmt,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _fsrSceneImage, out _fsrSceneMem);
        else
            CreateImage(rw, rh, 1, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _fsrSceneImage, out _fsrSceneMem);
        _fsrSceneSampleFormat = sceneSampleFmt;
        _fsrSceneView = CreateImageView(_fsrSceneImage, _format, ImageAspectFlags.ColorBit, 1);
        CreateImage(rw, rh, 1, Format.R16G16Sfloat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, out _fsrVelocityImage, out _fsrVelocityMem);
        _fsrVelocityView = CreateImageView(_fsrVelocityImage, Format.R16G16Sfloat, ImageAspectFlags.ColorBit, 1);
        // FFX schreibt sRGB-kodierte Bytes in ein UNORM-Ziel; die Bytekopie ins sRGB-Swapchain-Bild
        // entspricht damit exakt dem nativen Pfad (PostFx/Flash sehen Bildschirmfarben).
        Format outFmt = _uiFormat != Format.Undefined ? _uiFormat : (_format == Format.B8G8R8A8Srgb ? Format.B8G8R8A8Unorm : _format == Format.R8G8B8A8Srgb ? Format.R8G8B8A8Unorm : _format);
        CreateImage(_extent.Width, _extent.Height, 1, outFmt, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit, out _fsrOutputImage, out _fsrOutputMem);
        _fsrOutputView = CreateImageView(_fsrOutputImage, outFmt, ImageAspectFlags.ColorBit, 1);
        _fsrOutputFormat = outFmt;

        // FSR 3.1.6 verlangt eine Exposure-Ressource (auch ohne AUTO_EXPOSURE): 1x1 R32F mit Wert 1.0.
        CreateImage(1, 1, 1, Format.R32Sfloat, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            out _fsrExposureImage, out _fsrExposureMem);
        ClearFsrExposure();

        CreateFsrVelocityPass();
        CreateFsrFramebuffers();
        if (!CreateFsrContext()) return;

        _fsrReady = true;
        _fsrOutputReady = false;
        _fsrJitterIndex = 0;
        _fsrHadFrame = false;
        _fsrStatus = _activeUpscaler == UpscalerMode.Fsr3 ? "FSR3" : "spatial";
        _lastFsrTick = Stopwatch.GetTimestamp();
    }

    private void ClearFsrExposure()
    {
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var cmd = BeginSingleTimeCommands();
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _fsrExposureImage, SubresourceRange = range,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = 0, DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toDst);
        var exposure = new ClearColorValue(1f, 0f, 0f, 0f);
        _vk.CmdClearColorImage(cmd, _fsrExposureImage, ImageLayout.TransferDstOptimal, &exposure, 1, in range);
        var toRead = toDst;
        toRead.OldLayout = ImageLayout.TransferDstOptimal; toRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        toRead.SrcAccessMask = AccessFlags.TransferWriteBit; toRead.DstAccessMask = AccessFlags.ShaderReadBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in toRead);
        EndSingleTimeCommands(cmd);
    }

    private void CreateFsrVelocityPass()
    {
        var color = new AttachmentDescription
        {
            Format = Format.R16G16Sfloat,
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
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit, DstAccessMask = AccessFlags.ShaderReadBit
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit, SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit, DstAccessMask = AccessFlags.ShaderReadBit
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1, PAttachments = &color,
            SubpassCount = 1, PSubpasses = &subpass,
            DependencyCount = 2, PDependencies = deps
        };
        if (_vk.CreateRenderPass(_device, in info, null, out _fsrVelocityPass) != Result.Success)
            throw new Exception("FSR: Velocity-RenderPass fehlgeschlagen.");

        // Fragment-Shader liest die Welt-Tiefe; Nearest + Clamp wie die PostFx-Tiefe.
        var si = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f
        };
        if (_vk.CreateSampler(_device, in si, null, out _fsrDepthSampler) != Result.Success)
            throw new Exception("FSR: Tiefen-Sampler fehlgeschlagen.");

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit
        };
        var sl = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        if (_vk.CreateDescriptorSetLayout(_device, in sl, null, out _fsrVelocitySetLayout) != Result.Success)
            throw new Exception("FSR: Descriptor-Set-Layout fehlgeschlagen.");
        var setLayout = _fsrVelocitySetLayout;
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.FragmentBit, Offset = 0, Size = (uint)sizeof(FsrShaders.VelocityPush) };
        var li = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        if (_vk.CreatePipelineLayout(_device, in li, null, out _fsrVelocityLayout) != Result.Success)
            throw new Exception("FSR: Pipeline-Layout fehlgeschlagen.");

        using (var c = new ShaderCompiler())
        {
            ShaderModule vert = CreateShaderModule(c.Compile(PostFxShaders.FullscreenVert, ShaderKind.VertexShader, "FsrVelocity.vert"));
            ShaderModule frag = CreateShaderModule(c.Compile(FsrShaders.VelocityFrag, ShaderKind.FragmentShader, "FsrVelocity.frag"));
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
                    PolygonMode = PolygonMode.Fill, LineWidth = 1f,
                    CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise
                };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
                var blendAtt = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit, BlendEnable = false };
                var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAtt };
                var dynStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
                var pipeInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2, PStages = stages,
                    PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState, PRasterizationState = &raster,
                    PMultisampleState = &multisample, PDepthStencilState = &depth,
                    PColorBlendState = &blend, PDynamicState = &dynamic,
                    Layout = _fsrVelocityLayout, RenderPass = _fsrVelocityPass, Subpass = 0
                };
                if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipeInfo, null, out _fsrVelocityPipe) != Result.Success)
                    throw new Exception("FSR: Velocity-Pipeline fehlgeschlagen.");
            }
            finally { SilkMarshal.Free((nint)entry); _vk.DestroyShaderModule(_device, vert, null); _vk.DestroyShaderModule(_device, frag, null); }
        }

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 };
        var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 2, PoolSizeCount = 1, PPoolSizes = &poolSize };
        if (_vk.CreateDescriptorPool(_device, in pi, null, out _fsrVelocityPool) != Result.Success)
            throw new Exception("FSR: Descriptor-Pool fehlgeschlagen.");
        var alloc = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _fsrVelocityPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        if (_vk.AllocateDescriptorSets(_device, in alloc, out _fsrVelocitySet) != Result.Success)
            throw new Exception("FSR: Descriptor-Set fehlgeschlagen.");
        var ii = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _depthView, Sampler = _fsrDepthSampler };
        var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _fsrVelocitySet, DstBinding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &ii };
        _vk.UpdateDescriptorSets(_device, 1, &w, 0, null);
    }

    private void CreateFsrFramebuffers()
    {
        var sceneAttachments = stackalloc ImageView[2] { _fsrSceneView, _depthView };
        var si = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo, RenderPass = _renderPass,
            AttachmentCount = 2, PAttachments = sceneAttachments,
            Width = _renderExtent.Width, Height = _renderExtent.Height, Layers = 1
        };
        if (_vk.CreateFramebuffer(_device, in si, null, out _fsrSceneFb) != Result.Success)
            throw new Exception("FSR: Szenen-Framebuffer fehlgeschlagen.");
        var vel = _fsrVelocityView;
        var vi = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo, RenderPass = _fsrVelocityPass,
            AttachmentCount = 1, PAttachments = &vel,
            Width = _renderExtent.Width, Height = _renderExtent.Height, Layers = 1
        };
        if (_vk.CreateFramebuffer(_device, in vi, null, out _fsrVelocityFb) != Result.Success)
            throw new Exception("FSR: Velocity-Framebuffer fehlgeschlagen.");
    }

    private nint VulkanGetDeviceProcAddr()
    {
        if (_vulkanLibrary == 0 && !NativeLibrary.TryLoad("vulkan-1.dll", out _vulkanLibrary))
            throw new Exception("vulkan-1.dll konnte für ffx_api nicht geladen werden.");
        nint p = NativeLibrary.GetExport(_vulkanLibrary, "vkGetDeviceProcAddr");
        if (p == 0) throw new Exception("vkGetDeviceProcAddr fehlt.");
        return p;
    }

    private FfxApi.Resource FfxResource(Image image, FfxApi.ResourceDescription desc, uint state)
        => new FfxApi.Resource { Image = image.Handle, Description = desc, State = state };

    private static FfxApi.ResourceDescription FfxDesc(uint format, uint w, uint h, uint usage)
        => new FfxApi.ResourceDescription
        {
            Type = FfxApi.TypeTexture2D, Format = format,
            Width = w, Height = h, Depth = 1, MipCount = 1, Usage = usage
        };

    private static uint FfxFormat(Format format) => format switch
    {
        Format.R8G8B8A8Unorm => FfxApi.FormatRgba8Unorm,
        Format.R8G8B8A8Srgb => FfxApi.FormatRgba8Srgb,
        Format.B8G8R8A8Unorm => FfxApi.FormatBgra8Unorm,
        Format.B8G8R8A8Srgb => FfxApi.FormatBgra8Srgb,
        Format.R16G16Sfloat => FfxApi.FormatR16G16Float,
        Format.D32Sfloat => FfxApi.FormatR32Float,
        _ => 0,
    };

    private bool CreateFsrContext()
    {
        try
        {
            _fsrDescMem = (nint)NativeMemory.AllocZeroed(256);
            var device = (FfxApi.CreateBackendVk*)_fsrDescMem;
            var create = (FfxApi.CreateContextUpscale*)(_fsrDescMem + 64);
            var version = (FfxApi.CreateContextVersion*)(_fsrDescMem + 128);
            var versionOverride = (FfxApi.OverrideVersion*)(_fsrDescMem + 192);

            // Reihenfolge wie im AMD-Sample: Upscale -> Backend -> Header-Version -> Versions-Override.
            // Der VK-Backend-Deskriptor enthält das VkDeviceContext by value (nicht als Zeiger).
            *device = new FfxApi.CreateBackendVk
            {
                Header = new FfxApi.Header { Type = FfxApi.CreateContextBackendVk },
                DeviceContext = new FfxApi.DeviceContextVk
                {
                    Device = (ulong)_device.Handle,
                    PhysicalDevice = (ulong)_physical.Handle,
                    GetDeviceProcAddr = (ulong)VulkanGetDeviceProcAddr(),
                },
            };
            _fsrDeviceContext = (nint)device + sizeof(FfxApi.Header);
            // FSR 3.1.6 benötigt eine eigene Exposure-Texture; AUTO_EXPOSURE würde sie ignorieren.
            // Die Engine rendert normales Vulkan-Depth [0..1] (near=0, far=1): kein CreateDepthInverted.
            // Die Szene liegt sRGB-kodiert vor, wird aber über die UNORM-Ansicht gelesen.
            // FFX macht deshalb die sRGB-Umrechnung selbst und schreibt wieder sRGB-kodierte Bytes.
            uint flags = _formatSrgb ? FfxApi.CreateNonLinearColorspace : 0u;
            *create = new FfxApi.CreateContextUpscale
            {
                Header = new FfxApi.Header { Type = FfxApi.CreateContextUpscaleType, PNext = (nint)device },
                Flags = flags,
                MaxRenderSize = new FfxApi.Dimensions2D { Width = _renderExtent.Width, Height = _renderExtent.Height },
                MaxUpscaleSize = new FfxApi.Dimensions2D { Width = _extent.Width, Height = _extent.Height },
            };
            *version = new FfxApi.CreateContextVersion
            {
                Header = new FfxApi.Header { Type = FfxApi.CreateContextUpscaleVersion },
                Version = FfxApi.UpscalerVersion,
            };
            device->Header.PNext = (nint)version;

            _fsrVersionId = FfxApi.FindFsr3VersionId(_fsrDeviceContext, out var fsrVersionName);
            if (_fsrVersionId != 0)
            {
                Log.Info("FSR: ffx_api Upscaler-Version " + fsrVersionName);
                *versionOverride = new FfxApi.OverrideVersion
                {
                    Header = new FfxApi.Header { Type = FfxApi.DescOverrideVersion },
                    VersionId = _fsrVersionId,
                };
                version->Header.PNext = (nint)versionOverride;
            }

            nint context = 0;
            uint rc = FfxApi.CreateContext(&context, (nint)create);
            _fsrContext = context;
            if (rc != 0 || _fsrContext == 0)
            {
                Log.Warn("FSR: ffxCreateContext fehlgeschlagen (" + rc + ") – nativer Pfad.");
                _activeUpscaler = UpscalerMode.Off;
                _fsrStatus = "error";
                return false;
            }
            if (_activeUpscaler == UpscalerMode.Fsr3 && !FfxApi.QueryJitter(_fsrContext, _renderExtent.Width, _extent.Width, 0, out _, out _))
                Log.Warn("FSR: Jitter-Abfrage nicht verfügbar.");
            _fsrJitterIndex = 0;
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("FSR: Kontext fehlgeschlagen: " + e.Message);
            _activeUpscaler = UpscalerMode.Off;
            _fsrStatus = "error";
            return false;
        }
    }

    private void DestroyFsr()
    {
        if (_fsrContext != 0) { nint context = _fsrContext; FfxApi.DestroyContext(&context); _fsrContext = 0; }
        if (_fsrDescMem != 0) { NativeMemory.Free((void*)_fsrDescMem); _fsrDescMem = 0; }
        if (_fsrVelocityPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _fsrVelocityPool, null); _fsrVelocityPool = default; }
        if (_fsrVelocityPipe.Handle != 0) { _vk.DestroyPipeline(_device, _fsrVelocityPipe, null); _fsrVelocityPipe = default; }
        if (_fsrVelocityLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _fsrVelocityLayout, null); _fsrVelocityLayout = default; }
        if (_fsrVelocitySetLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _fsrVelocitySetLayout, null); _fsrVelocitySetLayout = default; }
        if (_fsrDepthSampler.Handle != 0) { _vk.DestroySampler(_device, _fsrDepthSampler, null); _fsrDepthSampler = default; }
        if (_fsrVelocityFb.Handle != 0) { _vk.DestroyFramebuffer(_device, _fsrVelocityFb, null); _fsrVelocityFb = default; }
        if (_fsrSceneFb.Handle != 0) { _vk.DestroyFramebuffer(_device, _fsrSceneFb, null); _fsrSceneFb = default; }
        if (_fsrVelocityPass.Handle != 0) { _vk.DestroyRenderPass(_device, _fsrVelocityPass, null); _fsrVelocityPass = default; }
        FreeFsrImage(ref _fsrSceneImage, ref _fsrSceneView, _fsrSceneMem);
        FreeFsrImage(ref _fsrVelocityImage, ref _fsrVelocityView, _fsrVelocityMem);
        FreeFsrImage(ref _fsrOutputImage, ref _fsrOutputView, _fsrOutputMem);
        if (_fsrExposureImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _fsrExposureImage, null);
            _alloc.Free(_fsrExposureMem);
            _fsrExposureImage = default;
        }
        _fsrReady = false;
        _fsrOutputReady = false;
    }

    private void FreeFsrImage(ref Image image, ref ImageView view, GpuAllocator.Allocation mem)
    {
        if (view.Handle != 0) _vk.DestroyImageView(_device, view, null);
        if (image.Handle != 0) { _vk.DestroyImage(_device, image, null); _alloc.Free(mem); }
        image = default; view = default;
    }

    // ------------------------------------------------------------------ Je Bild

    /// <summary>Jitter in die Projektion einbauen (nur FSR3). Läuft vor dem Frame-Ubo-Schreiben.</summary>
    private Matrix4x4 ApplyFsrJitter(Matrix4x4 view, Matrix4x4 projection)
    {
        if (!UpscaleScene || _activeUpscaler != UpscalerMode.Fsr3 || _fsrContext == 0) return projection;
        if (!FfxApi.QueryJitter(_fsrContext, _renderExtent.Width, _extent.Width, _fsrJitterIndex, out _jitterX, out _jitterY))
        {
            _jitterX = _jitterY = 0f;
            return projection;
        }
        // Cauldron/Konvention: NDC-Versatz (-2*jx/renderW, 2*jy/renderH) in M41/M42 (GLSL viewProj[3][0/1]).
        projection.M41 += -2f * _jitterX / _renderExtent.Width;
        projection.M42 += 2f * _jitterY / _renderExtent.Height;
        return projection;
    }

    private void RecordFsr(CommandBuffer cmd)
    {
        if (!UpscaleScene || _fsrContext == 0) return;

        // Weltpass endet in DepthStencilReadOnlyOptimal; Velocity und FFX erwarten ShaderReadOnlyOptimal.
        var depthRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
        var depthBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _depthImage,
            SubresourceRange = depthRange,
            OldLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &depthBarrier);

        // Velocity: Tiefe + aktuelle/inverse ViewProj, Ergebnis in UV-Raum (previous uv - current uv).
        var clear = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        var rp = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _fsrVelocityPass,
            Framebuffer = _fsrVelocityFb,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _renderExtent },
            ClearValueCount = 1, PClearValues = &clear
        };
        _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
        var viewport = new Viewport { X = 0, Y = 0, Width = _renderExtent.Width, Height = _renderExtent.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _renderExtent };
        _vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _vk.CmdSetScissor(cmd, 0, 1, &scissor);
        // Ungejitterte Matrizen: FFX berücksichtigt den Jitter separat über jitterOffset.
        var currentViewProj = _view * _proj;
        if (!Matrix4x4.Invert(currentViewProj, out var inv))
        {
            inv = Matrix4x4.Identity;
            _fsrResetThisFrame = true;
        }
        var push = new FsrShaders.VelocityPush
        {
            InvViewProj = inv,
            PrevViewProj = _fsrHadFrame ? _prevViewProj : currentViewProj,
            DepthParams = new Vector2(_proj.M33, _proj.M43),
            InvSize = new Vector2(1f / _renderExtent.Width, 1f / _renderExtent.Height),
        };
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _fsrVelocityPipe);
        var velSet = _fsrVelocitySet;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _fsrVelocityLayout, 0, 1, &velSet, 0, null);
        _vk.CmdPushConstants(cmd, _fsrVelocityLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(FsrShaders.VelocityPush), &push);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cmd);

        // Szene und Velocity liegen jetzt im ShaderRead-Layout, die Tiefe came from the world pass.
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = _fsrSceneImage,
            SubresourceRange = range,
            OldLayout = ImageLayout.ColorAttachmentOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, &barrier);

        // Ausgangsbild einmalig in GENERAL bringen; FFX zeichnet danach seine eigenen Barriers.
        if (!_fsrOutputReady)
        {
            barrier.Image = _fsrOutputImage;
            barrier.OldLayout = ImageLayout.Undefined;
            barrier.NewLayout = ImageLayout.General;
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderWriteBit;
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, &barrier);
            _fsrOutputReady = true;
        }

        long now = Stopwatch.GetTimestamp();
        float dt = (float)((now - _lastFsrTick) * 1000.0 / Stopwatch.Frequency);
        _lastFsrTick = now;
        if (dt < 0.1f || dt > 250f) dt = 16.7f;

        // Invertierte, unendliche Vulkan-Projektion: d = -M33 - M43/z_eye (near -> 0, far -> 1).
        float a = _proj.M33;
        float b = _proj.M43;
        float near = a > 1e-6f && b > 0f ? b / (a - 1f) : 0.1f;
        float far = near * (a + 1f) / (a - 1f);
        float f = System.Math.Max(0.1f, -_proj.M22);
        float fov = 2f * MathF.Atan(1f / f);
        uint outFmt = OutputFfxFormat();
        uint colorFmt = FfxFormat(_fsrSceneSampleFormat);
        uint depthFmt = FfxFormat(_depthFormat);

        var dispatch = new FfxApi.DispatchUpscale
        {
            Header = new FfxApi.Header { Type = FfxApi.DispatchUpscaleType },
            CommandList = (ulong)cmd.Handle,
            Color = FfxResource(_fsrSceneImage, FfxDesc(colorFmt, _renderExtent.Width, _renderExtent.Height, FfxApi.ResourceUsageReadonly), FfxApi.StatePixelComputeRead),
            Depth = FfxResource(_depthImage, FfxDesc(depthFmt, _renderExtent.Width, _renderExtent.Height, FfxApi.ResourceUsageDepthTarget), FfxApi.StatePixelComputeRead),
            MotionVectors = FfxResource(_fsrVelocityImage, FfxDesc(FfxApi.FormatR16G16Float, _renderExtent.Width, _renderExtent.Height, FfxApi.ResourceUsageReadonly), FfxApi.StatePixelComputeRead),
            Exposure = FfxResource(_fsrExposureImage, FfxDesc(FfxApi.FormatR32Float, 1, 1, FfxApi.ResourceUsageReadonly), FfxApi.StatePixelComputeRead),
            Output = FfxResource(_fsrOutputImage, FfxDesc(outFmt, _extent.Width, _extent.Height, FfxApi.ResourceUsageUav), FfxApi.StateUav),
            JitterOffset = new FfxApi.FloatCoords2D { X = _activeUpscaler == UpscalerMode.Fsr3 ? -_jitterX : 0f, Y = _activeUpscaler == UpscalerMode.Fsr3 ? -_jitterY : 0f },
            MotionVectorScale = new FfxApi.FloatCoords2D { X = _renderExtent.Width, Y = _renderExtent.Height },
            RenderSize = new FfxApi.Dimensions2D { Width = _renderExtent.Width, Height = _renderExtent.Height },
            UpscaleSize = new FfxApi.Dimensions2D { Width = _extent.Width, Height = _extent.Height },
            EnableSharpening = GraphicsSettings.FsrSharpness > 0.001f ? 1u : 0u,
            Sharpness = System.Math.Clamp(GraphicsSettings.FsrSharpness * 0.8f, 0f, 1f),
            FrameTimeDelta = dt,
            PreExposure = 1f,
            Reset = (_activeUpscaler == UpscalerMode.Fsr1 || !_fsrHadFrame || _fsrResetThisFrame) ? 1u : 0u,
            CameraNear = near,
            CameraFar = far,
            CameraFovAngleVertical = fov,
            ViewSpaceToMetersFactor = 1f,
            Flags = _formatSrgb ? FfxApi.DispatchNonLinearColorSrgb : 0u,
        };
        uint rc = FfxApi.Dispatch(_fsrContext, (nint)(&dispatch));
        if (rc != 0) Log.Warn("FSR: ffxDispatch fehlgeschlagen (" + rc + ")");

        // Tiefe zurück in das Layout, das die anschließende PostFx erwartet (PfxDepthBarrier).
        depthBarrier.OldLayout = ImageLayout.ShaderReadOnlyOptimal;
        depthBarrier.NewLayout = ImageLayout.DepthStencilReadOnlyOptimal;
        depthBarrier.SrcAccessMask = AccessFlags.ShaderReadBit;
        depthBarrier.DstAccessMask = AccessFlags.ShaderReadBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, &depthBarrier);

        // FFX leave das Ziel in GENERAL; die sRGB-kodierten Bytes kommen per Bytekopie ins Swapchain-Bild.
        barrier.Image = _fsrOutputImage;
        barrier.OldLayout = ImageLayout.General;
        barrier.NewLayout = ImageLayout.TransferSrcOptimal;
        barrier.SrcAccessMask = AccessFlags.ShaderWriteBit;
        barrier.DstAccessMask = AccessFlags.TransferReadBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);

        var swap = _images[_imageIndex];
        var swapRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var swapBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = swap, SubresourceRange = swapRange,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = 0, DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.BottomOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &swapBarrier);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(_extent.Width, _extent.Height, 1)
        };
        _vk.CmdCopyImage(cmd, _fsrOutputImage, ImageLayout.TransferSrcOptimal, swap, ImageLayout.TransferDstOptimal, 1, in copy);
        swapBarrier.OldLayout = ImageLayout.TransferDstOptimal;
        swapBarrier.NewLayout = _uiViews.Length > 0 ? ImageLayout.ColorAttachmentOptimal : ImageLayout.PresentSrcKhr;
        swapBarrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        swapBarrier.DstAccessMask = AccessFlags.ColorAttachmentWriteBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit, 0, 0, null, 0, null, 1, &swapBarrier);

        // FFX expects the output resource in UAV/General on every dispatch (FFX restores it to the
        // state we declared, not to TransferSrc). Restore it after the copy to the swapchain image.
        barrier.Image = _fsrOutputImage;
        barrier.OldLayout = ImageLayout.TransferSrcOptimal;
        barrier.NewLayout = ImageLayout.General;
        barrier.SrcAccessMask = AccessFlags.TransferReadBit;
        barrier.DstAccessMask = AccessFlags.ShaderWriteBit;
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, &barrier);

        _prevViewProj = currentViewProj;
        _prevCamPos = new Vector3(_view.M41, _view.M42, _view.M43);
        _fsrHadFrame = true;
        _fsrResetThisFrame = false;
    }

    private Format _fsrOutputFormat;
    private Format _fsrSceneSampleFormat;
    private uint OutputFfxFormat() => FfxFormat(_fsrOutputFormat);
}
