using Paradise.Core.Diagnostics;
using System.Runtime.InteropServices;

namespace Paradise.Core.Audio;

public sealed class AudioClip
{
    public readonly string Name;
    public readonly float[] Samples;
    public readonly int Channels, Rate;
    public int Frames => Samples.Length / System.Math.Max(1, Channels);

    public AudioClip(string name, float[] samples, int channels, int rate)
    {
        Name = name; Samples = samples; Channels = System.Math.Clamp(channels, 1, 2); Rate = System.Math.Max(1, rate);
    }

    public static AudioClip? FromWav(string name, byte[]? data) => data != null && WavDecoder.TryDecode(data, out var s, out int ch, out int rate) && s != null && s.Length > 0 ? new AudioClip(name, s, ch, rate) : null;
}

/// <summary>
/// Playing audio source: clip, volume, loop.
/// Volume ramps (fade in/out) run in the mixer thread via <see cref="FadeTo"/>.
/// </summary>
public sealed class AudioVoice
{
    internal AudioClip? _clip;
    internal double _pos;
    internal volatile bool _playing;
    internal float _vol, _target, _fadeRate;
    public bool Loop;
    public float Pitch = 1f;
    /// <summary>Continues to play even when <see cref="AudioEngine.Paused"/> is active (AudioSource.ignoreListenerPause: Menu music/sounds).</summary>
    public bool IgnorePause;
    internal bool _oneShot;

    public AudioClip? Clip
    {
        get => _clip;
        set { lock (AudioEngine.Gate) { _clip = value; _pos = 0; } }
    }
    public bool IsPlaying => _playing;

    public float Time
    {
        get { var c = _clip; return c == null ? 0f : (float)(_pos / c.Rate); }
        set { lock (AudioEngine.Gate) { var c = _clip; if (c != null) _pos = System.Math.Clamp(value * (double)c.Rate, 0.0, System.Math.Max(0, c.Frames - 1)); } }
    }

    public float Length
    {
        get
        {
            var c = _clip;
            return c == null ? 0f : c.Frames / (float)c.Rate;
        }
    }

    public float Volume
    {
        get => _vol;
        set { lock (AudioEngine.Gate) { _vol = value; _target = value; _fadeRate = 0f; } }
    }

    public void Play()
    {
        lock (AudioEngine.Gate)
        {
            _pos = 0;
            _playing = _clip != null;
        }
        AudioEngine.Register(this);
    }

    public void Stop() { lock (AudioEngine.Gate) _playing = false; }

    public void FadeTo(float target, float seconds)
    {
        lock (AudioEngine.Gate)
        {
            _target = target;
            _fadeRate = seconds > 0.001f ? MathF.Abs(target - _vol) / seconds : 0f;
            if (_fadeRate <= 0f) _vol = target;
        }
    }
}

/// <summary>
/// Software mixer for Windows WaveOut (winmm, no additional packages): 44.1 kHz stereo, 16-bit, 4 buffers of 20 ms each.
/// </summary>
public static class AudioEngine
{
    public const int Rate = 44100;
    const int FramesPerBuffer = 882;   // 20 ms
    const int BufferCount = 4;
    public static float MasterVolume = .9f;
    public static volatile bool Paused;
    internal static readonly object Gate = new();
    static readonly List<AudioVoice> _voices = new();
    static Thread? _thread;
    static volatile bool _run;
    static bool _failed;

    /// <summary>Angemeldete Stimmen (Entwicklermenü F1 > Audio).</summary>
    public static int VoiceCount { get { lock (Gate) return _voices.Count; } }

    /// <summary>Play a short sound once (like AudioSource.PlayOneShot).</summary>
    public static void PlayOneShot(AudioClip? clip, float volume, bool ignorePause = false)
    {
        if (clip == null || volume <= 0f) return;
        var v = new AudioVoice { _clip = clip, _vol = volume, _target = volume, _oneShot = true, _playing = true, IgnorePause = ignorePause };
        Register(v);
    }

    internal static void Register(AudioVoice v)
    {
        Start();
        lock (Gate) if (!_voices.Contains(v)) _voices.Add(v);
    }

    public static void Start()
    {
        if (_thread != null || _failed) return;
        _run = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Paradise Audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static void Shutdown()
    {
        _run = false;
        _thread?.Join(500);
        _thread = null;
        lock (Gate) _voices.Clear();
    }

    static void Mix(short[] outBuf, float[] mix)
    {
        Array.Clear(mix);
        lock (Gate)
        {
            float dtFade = FramesPerBuffer / (float)Rate;
            for (int vi = _voices.Count - 1; vi >= 0; vi--)
            {
                var v = _voices[vi];
                var c = v._clip;
                if (!v._playing || c == null) { if (v._oneShot || c == null) _voices.RemoveAt(vi); continue; }
                if (Paused && !v.IgnorePause) continue;   // angehalten: Position und Lautstärke bleiben
                float vol0 = v._vol;
                if (v._fadeRate > 0f)
                {
                    float d = v._target - v._vol, st = v._fadeRate * dtFade;
                    v._vol = MathF.Abs(d) <= st ? v._target : v._vol + MathF.Sign(d) * st;
                    if (v._vol == v._target) v._fadeRate = 0f;
                }
                float vol1 = v._vol;
                double step = c.Rate / (double)Rate * System.Math.Max(0.01f, v.Pitch);
                int frames = c.Frames, ch = c.Channels;
                var s = c.Samples;
                double pos = v._pos;
                for (int i = 0; i < FramesPerBuffer; i++)
                {
                    if (pos >= frames - 1)
                    {
                        if (v.Loop && frames > 1) pos %= frames - 1;
                        else { v._playing = false; break; }
                    }
                    int p0 = (int)pos; float f = (float)(pos - p0);
                    int p1 = System.Math.Min(p0 + 1, frames - 1);
                    float g = (vol0 + (vol1 - vol0) * (i / (float)FramesPerBuffer)) * MasterVolume;
                    float l, r;
                    if (ch == 1) { l = r = s[p0] + (s[p1] - s[p0]) * f; }
                    else
                    {
                        l = s[p0 * 2] + (s[p1 * 2] - s[p0 * 2]) * f;
                        r = s[p0 * 2 + 1] + (s[p1 * 2 + 1] - s[p0 * 2 + 1]) * f;
                    }
                    mix[i * 2] += l * g; mix[i * 2 + 1] += r * g;
                    pos += step;
                }
                v._pos = pos;
                if (!v._playing && v._oneShot) _voices.RemoveAt(vi);
            }
        }
        for (int i = 0; i < mix.Length; i++)
        {
            float x = mix[i];
            if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
            outBuf[i] = (short)(x * 32767f);
        }
    }

    static void Run()
    {
        var fmt = new WaveFormatEx
        {
            wFormatTag = 1,
            nChannels = 2,
            nSamplesPerSec = Rate,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = Rate * 4
        };
        using var ev = new AutoResetEvent(false);
        int r = waveOutOpen(out var dev, WAVE_MAPPER, ref fmt, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (r != 0)
        {
            _failed = true;
            Log.Warn($"Audio: waveOutOpen failed({r}) – no sound.");
            _thread = null;
            return;
        }
        int bytes = FramesPerBuffer * 4;
        var hdrs = new IntPtr[BufferCount];
        var data = new IntPtr[BufferCount];
        var pcm = new short[FramesPerBuffer * 2];
        var mix = new float[FramesPerBuffer * 2];
        int hsz = Marshal.SizeOf<WaveHdr>();
        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                data[i] = Marshal.AllocHGlobal(bytes);
                hdrs[i] = Marshal.AllocHGlobal(hsz);
                var h = new WaveHdr { lpData = data[i], dwBufferLength = (uint)bytes };
                Marshal.StructureToPtr(h, hdrs[i], false);
                waveOutPrepareHeader(dev, hdrs[i], hsz);
                Mix(pcm, mix);
                Marshal.Copy(pcm, 0, data[i], pcm.Length);
                waveOutWrite(dev, hdrs[i], hsz);
            }
            while (_run)
            {
                ev.WaitOne(50);
                for (int i = 0; i < BufferCount; i++)
                {
                    var h = Marshal.PtrToStructure<WaveHdr>(hdrs[i]);
                    if ((h.dwFlags & WHDR_DONE) == 0) continue;
                    Mix(pcm, mix);
                    Marshal.Copy(pcm, 0, data[i], pcm.Length);
                    waveOutWrite(dev, hdrs[i], hsz);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audio: " + ex.Message);
        }
        finally
        {
            waveOutReset(dev);
            for (int i = 0; i < BufferCount; i++)
            {
                if (hdrs[i] != IntPtr.Zero) { waveOutUnprepareHeader(dev, hdrs[i], hsz); Marshal.FreeHGlobal(hdrs[i]); }
                if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
            }
            waveOutClose(dev);
        }
    }

    const int WAVE_MAPPER = -1;
    const int CALLBACK_EVENT = 0x50000;
    const uint WHDR_DONE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx { public ushort wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
}