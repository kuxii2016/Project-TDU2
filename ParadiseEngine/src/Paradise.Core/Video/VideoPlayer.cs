using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Render;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Paradise.Core.Video;

/// <summary>
/// Video abspielen (Intros Euro\Bnk\Video): Bild über Windows Media Foundation (Source Reader, H.264 -> RGB32) in einem
/// Hintergrund-Thread, Ton aus der AVI-Datei (<see cref="AviAudio"/>, MS-ADPCM) über den Software-Mixer.
/// Die Dateien heißen .mkv, sind aber AVI – deshalb erkennt der Source Resolver den Container am Inhalt, nicht an der Endung.
/// Aufrufer: <see cref="Start"/>, jedes Bild <see cref="Update"/> (lädt das fällige Bild in <see cref="Texture"/>),
/// fertig wenn <see cref="Finished"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class VideoPlayer : IDisposable
{
    readonly string _path;
    readonly IRenderer _r;
    readonly BlockingCollection<Frame> _frames = new(new ConcurrentQueue<Frame>(), 6);
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;
    AudioVoice? _voice;
    Frame? _pending;
    double _clock;
    volatile bool _eos, _failed;

    public IGpuTexture? Texture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Abspielzeit in Sekunden.</summary>
    public double Time => _clock;
    public bool Finished => _failed || (_eos && _frames.Count == 0 && _pending == null);
    public string? Error { get; private set; }

    sealed class Frame { public long Ts; public byte[] Rgba = Array.Empty<byte>(); public int W, H; }

    public VideoPlayer(IRenderer renderer, string path) { _r = renderer; _path = path; }

    public void Start(float volume = 1f)
    {
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "Video " + Path.GetFileName(_path) };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try
        {
            var clip = AviAudio.Load(_path);
            if (clip != null) { _voice = new AudioVoice { Clip = clip }; _voice.Volume = volume; _voice.Play(); }
            else Log.Info("Video: keine Tonspur in " + Path.GetFileName(_path));
        }
        catch (Exception ex) { Log.Warn("Video-Ton: " + ex.Message); }
    }

    /// <summary>Uhr weiterstellen und das fällige Bild hochladen (Hauptthread).</summary>
    public void Update(float dt)
    {
        _clock += dt;
        long now = (long)(_clock * 1e7);
        Frame? show = null;
        while (true)
        {
            if (_pending == null && !_frames.TryTake(out _pending)) break;
            if (_pending!.Ts > now) break;
            show = _pending; _pending = null;
        }
        if (show == null) return;
        if (Texture == null || Width != show.W || Height != show.H)
        {
            Texture?.Dispose();
            Width = show.W; Height = show.H;
            Texture = _r.CreateTexture(new TextureUpload { Width = show.W, Height = show.H, Format = GpuTextureFormat.Rgba8, Blocks = show.Rgba, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "Video", Deferred = true });
        }
        else _r.UpdateTexture(Texture, show.Rgba);
    }

    public void Stop()
    {
        _cts.Cancel();
        _voice?.Stop();
    }

    public void Dispose()
    {
        Stop();
        while (_frames.TryTake(out _)) { }
        _thread?.Join(500);
        Texture?.Dispose(); Texture = null;
    }

    // ------------------------------------------------------------------ Media Foundation (Hintergrund)

    void DecodeLoop()
    {
        IMFSourceReader? reader = null;
        bool started = false;
        try
        {
            Mf.Check(Mf.MFStartup(Mf.MF_VERSION, 0), "MFStartup"); started = true;
            Mf.Check(Mf.MFCreateSourceResolver(out var resolver), "MFCreateSourceResolver");
            Mf.Check(resolver.CreateObjectFromURL(_path, Mf.MF_RESOLUTION_MEDIASOURCE | Mf.MF_RESOLUTION_READ | Mf.MF_RESOLUTION_CONTENT_DOES_NOT_HAVE_TO_MATCH_EXTENSION_OR_MIME_TYPE,
                IntPtr.Zero, out _, out var source), "CreateObjectFromURL");
            Mf.Check(Mf.MFCreateAttributes(out var attr, 1), "MFCreateAttributes");
            Mf.Check(attr.SetUINT32(Mf.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1), "SetUINT32");
            Mf.Check(Mf.MFCreateSourceReaderFromMediaSource(source, attr, out reader), "MFCreateSourceReaderFromMediaSource");
            reader.SetStreamSelection(Mf.ALL_STREAMS, false);
            Mf.Check(reader.SetStreamSelection(Mf.FIRST_VIDEO_STREAM, true), "SetStreamSelection");
            Mf.Check(Mf.MFCreateMediaType(out var type), "MFCreateMediaType");
            var major = Mf.MFMediaType_Video; var sub = Mf.MFVideoFormat_RGB32;
            Mf.Check(type.SetGUID(Mf.MF_MT_MAJOR_TYPE, ref major), "SetGUID major");
            Mf.Check(type.SetGUID(Mf.MF_MT_SUBTYPE, ref sub), "SetGUID sub");
            Mf.Check(reader.SetCurrentMediaType(Mf.FIRST_VIDEO_STREAM, IntPtr.Zero, type), "SetCurrentMediaType");
            Mf.Check(reader.GetCurrentMediaType(Mf.FIRST_VIDEO_STREAM, out var cur), "GetCurrentMediaType");
            Mf.Check(cur.GetUINT64(Mf.MF_MT_FRAME_SIZE, out ulong fs), "FrameSize");
            int w = (int)(fs >> 32), h = (int)(fs & 0xFFFFFFFF);
            int stride = cur.GetUINT32(Mf.MF_MT_DEFAULT_STRIDE, out uint st) >= 0 ? unchecked((int)st) : w * 4;

            while (!_cts.IsCancellationRequested)
            {
                int hr = reader.ReadSample(Mf.FIRST_VIDEO_STREAM, 0, out _, out uint flags, out long ts, out var sample);
                if (hr < 0) { Error = $"ReadSample 0x{hr:X8}"; break; }
                if ((flags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
                if (sample == null) continue;
                try
                {
                    if (sample.ConvertToContiguousBuffer(out var buf) >= 0)
                    {
                        try
                        {
                            if (buf.Lock(out IntPtr ptr, out _, out uint len) >= 0)
                            {
                                try { _frames.Add(ToRgba(ptr, (int)len, w, h, stride, ts), _cts.Token); }
                                finally { buf.Unlock(); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(buf); }
                    }
                }
                finally { Marshal.ReleaseComObject(sample); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; _failed = true; Log.Warn("Video " + Path.GetFileName(_path) + ": " + ex.Message); }
        finally
        {
            if (reader != null) Marshal.ReleaseComObject(reader);
            if (started) Mf.MFShutdown();
            _eos = true;
        }
    }

    /// <summary>RGB32 (Speicher B,G,R,X; Schrittweite negativ = unten zuerst) -> RGBA, Zeile 0 = oben.</summary>
    static unsafe Frame ToRgba(IntPtr p, int len, int w, int h, int stride, long ts)
    {
        var f = new Frame { Ts = ts, W = w, H = h, Rgba = new byte[w * h * 4] };
        int pitch = System.Math.Abs(stride);
        byte* src = (byte*)p;
        fixed (byte* dst0 = f.Rgba)
        {
            for (int y = 0; y < h; y++)
            {
                int sy = stride < 0 ? h - 1 - y : y;
                if ((sy + 1) * pitch > len) break;
                byte* s = src + sy * pitch; byte* d = dst0 + y * w * 4;
                for (int x = 0; x < w; x++, s += 4, d += 4) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = 255; }
            }
        }
        return f;
    }
}

// ---------------------------------------------------------------------- Media-Foundation-Schnittstellen (nur das Nötige)

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class Mf
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MF_RESOLUTION_MEDIASOURCE = 0x1, MF_RESOLUTION_CONTENT_DOES_NOT_HAVE_TO_MATCH_EXTENSION_OR_MIME_TYPE = 0x10, MF_RESOLUTION_READ = 0x10000;
    public const uint FIRST_VIDEO_STREAM = 0xFFFFFFFC, ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attr, uint size);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] public static extern int MFCreateSourceResolver(out IMFSourceResolver resolver);
    [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromMediaSource([MarshalAs(UnmanagedType.IUnknown)] object source, IMFAttributes attr, out IMFSourceReader reader);

    public static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} 0x{hr:X8}"); }
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buf, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr str, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint size, out uint blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, ref Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
    [PreserveSig] int SetUnknown(in Guid key, IntPtr unk);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint items);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IntPtr dest);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaType
{
    // IMFAttributes
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buf, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr str, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint size, out uint blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, ref Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
    [PreserveSig] int SetUnknown(in Guid key, IntPtr unk);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint items);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IntPtr dest);
    // IMFMediaType
    [PreserveSig] int GetMajorType(out Guid major);
    [PreserveSig] int IsCompressedFormat(out bool compressed);
    [PreserveSig] int IsEqual(IntPtr type, out uint flags);
    [PreserveSig] int GetRepresentation(Guid rep, out IntPtr repr);
    [PreserveSig] int FreeRepresentation(Guid rep, IntPtr repr);
}

[ComImport, Guid("fbe5a32d-a497-4b61-bb85-97b1a848a6e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSourceResolver
{
    [PreserveSig] int CreateObjectFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr props, out uint objectType, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint stream, out bool selected);
    [PreserveSig] int SetStreamSelection(uint stream, bool selected);
    [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
    [PreserveSig] int SetCurrentPosition(in Guid format, IntPtr position);
    [PreserveSig] int ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint streamFlags, out long timestamp, out IMFSample? sample);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
{
    // IMFAttributes (30)
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buf, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr str, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint size, out uint blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, ref Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
    [PreserveSig] int SetUnknown(in Guid key, IntPtr unk);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint items);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IntPtr dest);
    // IMFSample
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
}
