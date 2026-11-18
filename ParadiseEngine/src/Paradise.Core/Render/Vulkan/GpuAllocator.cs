using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Einfacher Suballokator: große Speicherblöcke (64 MB) je Speichertyp, darin First-Fit mit
/// Zusammenfassen freier Bereiche. Ohne das stößt das Streaming an das Treiberlimit
/// maxMemoryAllocationCount (NVIDIA: 4096) – jedes Mesh hätte 2, jede Textur 1 Allokation.
/// Buffer (linear) und Images (optimal) liegen in getrennten Blöcken (bufferImageGranularity).
/// Host-sichtbare Blöcke bleiben dauerhaft gemappt. Nur vom Render-Thread benutzen.
/// </summary>
internal sealed unsafe class GpuAllocator : IDisposable
{
    private const ulong BlockSize = 64UL * 1024 * 1024;

    internal sealed class Block
    {
        public DeviceMemory Memory;
        public ulong Size;
        public byte* Mapped;
        public bool Dedicated;
        public int Live;
        public readonly List<(ulong Off, ulong Size)> Free = new();
        public (uint type, bool linear) Key;
    }

    internal readonly struct Allocation
    {
        public readonly Block Block;
        public readonly ulong Offset, Size;
        public Allocation(Block b, ulong o, ulong s) { Block = b; Offset = o; Size = s; }
        public DeviceMemory Memory => Block.Memory;
        public byte* Mapped => Block.Mapped == null ? null : Block.Mapped + Offset;
    }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDeviceMemoryProperties _props;
    private readonly Dictionary<(uint, bool), List<Block>> _blocks = new();

    public int BlockCount { get; private set; }

    public GpuAllocator(Vk vk, PhysicalDevice physical, Device device)
    {
        _vk = vk; _device = device;
        vk.GetPhysicalDeviceMemoryProperties(physical, out _props);
    }

    private uint FindType(uint filter, MemoryPropertyFlags flags)
    {
        for (uint i = 0; i < _props.MemoryTypeCount; i++)
            if ((filter & (1u << (int)i)) != 0 && (_props.MemoryTypes[(int)i].PropertyFlags & flags) == flags)
                return i;
        throw new Exception("No matching GPU memory type found.");
    }

    private static ulong AlignUp(ulong v, ulong a) => a <= 1 ? v : (v + a - 1) / a * a;

    public Allocation Allocate(MemoryRequirements req, MemoryPropertyFlags flags, bool linear)
    {
        uint type = FindType(req.MemoryTypeBits, flags);
        bool hostVisible = (flags & MemoryPropertyFlags.HostVisibleBit) != 0;
        var key = (type, linear);
        if (!_blocks.TryGetValue(key, out var list)) _blocks[key] = list = new List<Block>();

        // Große Anforderungen bekommen einen eigenen Block
        if (req.Size > BlockSize / 2)
        {
            var big = NewBlock(type, req.Size, hostVisible, key, true);
            list.Add(big);
            big.Free.Clear();
            big.Live++;
            return new Allocation(big, 0, req.Size);
        }

        foreach (var b in list)
            if (!b.Dedicated && TryCarve(b, req.Size, req.Alignment, out ulong off))
            { b.Live++; return new Allocation(b, off, req.Size); }

        var nb = NewBlock(type, BlockSize, hostVisible, key, false);
        list.Add(nb);
        if (!TryCarve(nb, req.Size, req.Alignment, out ulong o2))
            throw new Exception("GPU suballocation failed.");
        nb.Live++;
        return new Allocation(nb, o2, req.Size);
    }

    private Block NewBlock(uint type, ulong size, bool hostVisible, (uint, bool) key, bool dedicated)
    {
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type };
        if (_vk.AllocateMemory(_device, in info, null, out var mem) != Result.Success)
            throw new Exception($"GPU memory ({size / (1024 * 1024)} MB) could not be allocated.");
        var b = new Block { Memory = mem, Size = size, Dedicated = dedicated, Key = key };
        AllocatedBytes += (long)size;
        b.Free.Add((0, size));
        if (hostVisible)
        {
            void* p;
            _vk.MapMemory(_device, mem, 0, size, 0, &p);
            b.Mapped = (byte*)p;
        }
        BlockCount++;
        return b;
    }

    private static bool TryCarve(Block b, ulong size, ulong align, out ulong offset)
    {
        for (int i = 0; i < b.Free.Count; i++)
        {
            var (fo, fs) = b.Free[i];
            ulong a = AlignUp(fo, align);
            if (a + size > fo + fs) continue;
            b.Free.RemoveAt(i);
            if (a > fo) b.Free.Insert(i++, (fo, a - fo));
            ulong end = a + size, fend = fo + fs;
            if (fend > end) b.Free.Insert(i, (end, fend - end));
            offset = a;
            return true;
        }
        offset = 0;
        return false;
    }

    public void Free(Allocation a)
    {
        var b = a.Block;
        if (b == null) return;
        b.Live--;
        if (b.Dedicated) { ReleaseBlock(b); return; }

        // Leerer Block: freigeben, solange noch ein anderer Block dieses Typs existiert (einen behalten)
        if (b.Live <= 0 && _blocks.TryGetValue(b.Key, out var siblings) && siblings.Count > 1)
        {
            ReleaseBlock(b);
            return;
        }

        // Bereich zurückgeben (sortiert einfügen, Nachbarn zusammenfassen)
        int idx = 0;
        while (idx < b.Free.Count && b.Free[idx].Off < a.Offset) idx++;
        b.Free.Insert(idx, (a.Offset, a.Size));
        if (idx + 1 < b.Free.Count && b.Free[idx].Off + b.Free[idx].Size == b.Free[idx + 1].Off)
        {
            b.Free[idx] = (b.Free[idx].Off, b.Free[idx].Size + b.Free[idx + 1].Size);
            b.Free.RemoveAt(idx + 1);
        }
        if (idx > 0 && b.Free[idx - 1].Off + b.Free[idx - 1].Size == b.Free[idx].Off)
        {
            b.Free[idx - 1] = (b.Free[idx - 1].Off, b.Free[idx - 1].Size + b.Free[idx].Size);
            b.Free.RemoveAt(idx);
        }
    }

    private void ReleaseBlock(Block b)
    {
        if (_blocks.TryGetValue(b.Key, out var list)) list.Remove(b);
        if (b.Mapped != null) _vk.UnmapMemory(_device, b.Memory);
        _vk.FreeMemory(_device, b.Memory, null);
        b.Mapped = null;
        BlockCount--;
        AllocatedBytes -= (long)b.Size;
    }

    public long AllocatedBytes { get; private set; }

    public void Dispose()
    {
        foreach (var list in _blocks.Values)
            foreach (var b in list.ToArray())
            {
                if (b.Mapped != null) _vk.UnmapMemory(_device, b.Memory);
                _vk.FreeMemory(_device, b.Memory, null);
            }
        _blocks.Clear();
        BlockCount = 0;
    }
}