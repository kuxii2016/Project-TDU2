using Paradise.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Weltbild auf die CPU (<see cref="IRenderer.RequestSceneCapture"/>, MyTDULife-Foto). Aufzeichnung in <see cref="EndFrame"/> nach dem
/// Weltdurchgang (bei MSAA nach CopyResolveToSwapchain), vor Photoshop und Flash: Bild -> Host-Puffer (Bytekopie). Abholen in
/// <see cref="BeginFrame"/>, sobald der Fence dieses Bildes im Flug wieder erreicht ist (kein zusätzliches Warten auf die GPU).
/// Nur mit UI-Durchgang (sonst liegt Flash schon im selben Bild); ohne MSAA muss die Swapchain kopierbar sein (TransferSrc).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private readonly List<(Action<SceneCapture?> done, bool square)> _capWaiting = new();
    private Action<SceneCapture?>? _capInFlight;
    private int _capSlot = -1;
    private int _capW, _capH;
    private bool _capBgra;
    private Silk.NET.Vulkan.Buffer _capBuf;
    private GpuAllocator.Allocation _capMem;
    private ulong _capSize;
    private bool _capWarned;

    public void RequestSceneCapture(Action<SceneCapture?> done, bool centerSquare = false)
    {
        if (done == null) return;
        _capWaiting.Add((done, centerSquare));
    }

    private void FailSceneCaptures(string why)
    {
        if (!_capWarned) { _capWarned = true; Log.Warn("Cannot take a picture (photo):" + why); }
        var l = _capWaiting.ToArray();
        _capWaiting.Clear();
        foreach (var (done, _) in l) { try { done(null); } catch (Exception ex) { Log.Warn("picture: " + ex.Message); } }
    }

    /// <summary>EndFrame, ohne UI-Durchgang: Flash liegt im Weltdurchgang – keine Aufnahme ohne HUD möglich.</summary>
    private void RejectSceneCaptureWithoutUi()
    {
        if (_capWaiting.Count > 0 && _capInFlight == null) FailSceneCaptures("without its own UI flow (VK_KHR_swapchain_mutable_format is missing)");
    }

    /// <summary>EndFrame, außerhalb eines Render-Passes, nach dem Weltdurchgang (MSAA: nach CopyResolveToSwapchain).</summary>
    private void RecordSceneCapture(CommandBuffer cmd)
    {
        if (_capWaiting.Count == 0 || _capInFlight != null) return;
        if (!Msaa && !_swapTransferSrc) { FailSceneCaptures("Swapchain images cannot be copied (TransferSrc)"); return; }
        var (done, square) = _capWaiting[0];
        _capWaiting.RemoveAt(0);

        uint w = _extent.Width, h = _extent.Height;
        if (w == 0 || h == 0) { done(null); return; }
        int ox = 0;
        if (square && w > h) { ox = (int)((w - h) / 2); w = h; }
        else if (square && h > w) h = w;   // Hochformat: oberes Quadrat genügt nicht – mittig
        int oy = square && _extent.Height > h ? (int)((_extent.Height - h) / 2) : 0;

        ulong size = (ulong)w * h * 4;
        if (_capBuf.Handle == 0 || _capSize < size)
        {
            if (_capBuf.Handle != 0) { var b = _capBuf; var m = _capMem; DeferDestroy(() => FreeBuffer(b, m)); _capBuf = default; }
            CreateReadbackBuffer(size);
            _capSize = size;
        }

        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var src = Msaa ? _resolveImage : _images[_imageIndex];
        if (!Msaa)
        {
            // Weltdurchgang endet in ColorAttachmentOptimal (wie CopySceneForPhotoshop): zum Kopieren umschalten
            var pre = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                Image = src,
                SubresourceRange = range,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &pre);
        }
        // MSAA: das Auflösebild liegt nach CopyResolveToSwapchain schon als TransferSrc vor (Lesen nach Lesen)
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(ox, oy, 0),
            ImageExtent = new Extent3D(w, h, 1)
        };
        _vk.CmdCopyImageToBuffer(cmd, src, ImageLayout.TransferSrcOptimal, _capBuf, 1, in region);
        if (!Msaa)
        {
            var post = new ImageMemoryBarrier
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
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &post);
        }
        var host = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            Buffer = _capBuf,
            Offset = 0,
            Size = size,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &host, 0, null);

        _capInFlight = done;
        _capSlot = _currentFrame;
        _capW = (int)w; _capH = (int)h;
        _capBgra = _format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm;
    }

    /// <summary>BeginFrame nach dem Warten auf den Fence von _currentFrame: fertige Aufnahme abholen.</summary>
    private void PollSceneCapture()
    {
        if (_capInFlight == null || _capSlot != _currentFrame) return;
        var done = _capInFlight;
        _capInFlight = null; _capSlot = -1;
        SceneCapture? r = null;
        byte* p = _capMem.Mapped;
        if (p != null)
        {
            int n = _capW * _capH * 4;
            var px = new byte[n];
            new ReadOnlySpan<byte>(p, n).CopyTo(px);
            for (int i = 0; i < n; i += 4)
            {
                if (_capBgra) (px[i], px[i + 2]) = (px[i + 2], px[i]);
                px[i + 3] = 255;
            }
            r = new SceneCapture { Width = _capW, Height = _capH, Rgba = px };
        }
        try { done(r); } catch (Exception ex) { Log.Error("Image Capture: " + ex.Message); }
    }

    /// <summary>Host-sichtbarer Lese-Puffer (möglichst mit CPU-Cache, sonst wie CreateHostBuffer).</summary>
    private void CreateReadbackBuffer(ulong size)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = System.Math.Max(size, 16UL), Usage = BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
        if (_vk.CreateBuffer(_device, in info, null, out _capBuf) != Result.Success)
            throw new Exception("The read buffer (image capture) could not be created.");
        _vk.GetBufferMemoryRequirements(_device, _capBuf, out var req);
        try { _capMem = _alloc.Allocate(req, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit, linear: true); }
        catch (Exception) { _capMem = _alloc.Allocate(req, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, linear: true); }
        _vk.BindBufferMemory(_device, _capBuf, _capMem.Memory, _capMem.Offset);
    }

    /// <summary>Dispose (nach DeviceWaitIdle).</summary>
    private void DestroySceneCapture()
    {
        if (_capBuf.Handle != 0) { FreeBuffer(_capBuf, _capMem); _capBuf = default; }
        _capInFlight = null;
        _capWaiting.Clear();
    }
}