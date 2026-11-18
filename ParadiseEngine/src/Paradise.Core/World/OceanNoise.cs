using Paradise.Core.Render;

namespace Paradise.Core.World;

/// <summary>
/// Normalmap des Meeres wie im Original (TD_Ocean "NoiseNMap", OpenTDU2 docs/re/ocean_spec.md §3, Shader td_noisecompose /
/// td_noiseassembly / TD_ocean_nmap_from_hmap). Im Original drei GPU-Durchgänge je Bild; hier auf der CPU (256², 30 Hz):
///  1. NoiseSlice: 32³ Bytes weißes Rauschen (LCG ab 0x42424242 [INF]), je Schicht 3x3 geglättet (8·Mitte + 8 Nachbarn) / 16.
///  2. NoizComp (32²): drei Schichten s0..s2 mit sin²-Gewichten (Phase t, Periode 32 s); die z-Koordinate s/32 liegt auf der
///     Schichtgrenze -> jede Abtastung mittelt Schicht s−1 und s (lineare Filterung).
///  3. NoizAss (256²): 4 Oktaven (uv·2^i, Amplitude 1, ½, ¼, ⅛, Summe · 8/15), bilinear mit Wiederholung.
///  4. NoiseNMap: N = normalize(−s·(hR−hL), 2d, −s·(hU−hD)), d = 1/256, s = ocean_NMapScaler; Ausgabe N·0,5+0,5 (R = x, G = oben, B = z).
/// </summary>
public sealed class OceanNoise : IDisposable
{
    const int S = 32, N = 256;
    readonly float[] _slices = new float[S * S * S];
    readonly float[] _comp = new float[S * S];
    readonly float[] _height = new float[N * N];
    readonly byte[] _rgba = new byte[N * N * 4];
    // Feste Bilinear-Abtastpositionen der 4 Oktaven (nur von x/y abhngig, nicht von der Zeit):
    // einmalig im Konstruktor berechnen statt 30x/s 262.144x MathF.Floor pro Update.
    readonly int[] _si0 = new int[N * 4];
    readonly float[] _sw = new float[N * 4];
    static readonly float[] OctaveAmp = { 1f, 0.5f, 0.25f, 0.125f };
    // Worker rechnet die Normalmap, der Hauptthread lädt nur das fertige Bild hoch (wie beim Schatten-Thread).
    readonly byte[] _work = new byte[N * N * 4];
    readonly AutoResetEvent _go = new(false);
    Thread? _thread;
    volatile bool _quit;
    int _state; // 0 = frei, 1 = Worker rechnet, 2 = fertig (nur ber Volatile.Read/Write)
    float _buildT;
    readonly IRenderer _r;
    float _t, _acc = 1f;
    public IGpuTexture Texture { get; }
    /// <summary>ocean_NMapScaler des Wetters (Intro 0,13; WorldWeather).</summary>
    public static float Scaler = 0.13f;
    /// <summary>Aktualisierungen je Sekunde (Original: jedes Bild).</summary>
    public float Rate = 30f;

    public OceanNoise(IRenderer r)
    {
        _r = r;
        var raw = new byte[S * S * S];
        uint state = 0x42424242u;
        for (int i = 0; i < raw.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            float f = BitConverter.Int32BitsToSingle((int)(0x3f800000u | (state >> 9))) - 1f;
            raw[i] = (byte)(int)(f * 255f);
        }
        for (int z = 0; z < S; z++)
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int Get(int dx, int dy) => raw[z * S * S + ((y + dy) & 31) * S + ((x + dx) & 31)];
                    int sum = 8 * Get(0, 0) + Get(-1, 0) + Get(1, 0) + Get(0, -1) + Get(0, 1) + Get(-1, -1) + Get(1, -1) + Get(-1, 1) + Get(1, 1);
                    _slices[z * S * S + y * S + x] = (sum >> 4) / 255f;
                }
        // Abtastposition: q = ((i+0.5)/N)*k*S - 0.5, k = 1,2,4,8.
        // Fr u und v identisch -> ein Satz Indizes/Gewichte fr beide Achsen.
        for (int o = 0, k = 1; o < 4; o++, k *= 2)
            for (int i = 0; i < N; i++)
            {
                float q = ((i + 0.5f) / N) * k * S - 0.5f;
                int i0 = (int)MathF.Floor(q);
                _si0[o * N + i] = i0;
                _sw[o * N + i] = q - i0;
            }
        Build(_t, _rgba);
        Texture = r.CreateTexture(new TextureUpload
        {
            Width = N,
            Height = N,
            Mips = 1,
            Format = GpuTextureFormat.Rgba8,
            Blocks = (byte[])_rgba.Clone(),
            WrapU = TextureWrap.Repeat,
            WrapV = TextureWrap.Repeat,
            Linear = true,
            Name = "NoiseNMap",
        });
        _thread = new Thread(Worker) { IsBackground = true, Name = "OceanNoise" };
        _thread.Start();
    }

    /// <summary>Jedes Bild (Phase läuft mit 1/s, Periode 32 s). Nur Start und Upload laufen im Hauptthread.</summary>
    public void Update(float dt)
    {
        if (dt <= 0f) return;
        _t += dt;
        if (_t >= 32f) _t -= 32f;
        _acc += dt;
        if (_acc >= 1f / MathF.Max(1f, Rate))
        {
            _acc = 0f;
            // Wenn der Worker noch rechnet, wird dieses Update ausgelassen (kein Warten im Hauptthread).
            if (Volatile.Read(ref _state) == 0)
            {
                _buildT = _t;
                Volatile.Write(ref _state, 1);
                _go.Set();
            }
        }
        UploadIfReady();
    }

    void UploadIfReady()
    {
        if (Volatile.Read(ref _state) != 2) return;
        _r.UpdateTexture(Texture, _work);
        Volatile.Write(ref _state, 0);
    }

    void Worker()
    {
        while (true)
        {
            _go.WaitOne();
            if (_quit) return;
            Build(_buildT, _work);
            Volatile.Write(ref _state, 2);
        }
    }

    void Build(float t, byte[] dst)
    {
        // NoizComp
        int s0 = (int)MathF.Floor(t) % S, s1 = (s0 + 1) % S, s2 = (s1 + 1) % S;
        float f = t - MathF.Floor(t);
        float W(float a) { float s = MathF.Sin(a); return s * s / 1.5f; }
        float w0 = W(MathF.PI * (2f + f) / 3f), w1 = W(MathF.PI * (1f + f) / 3f), w2 = W(MathF.PI * f / 3f);
        int a0 = ((s0 + S - 1) % S) * S * S, b0 = s0 * S * S;
        int a1 = ((s1 + S - 1) % S) * S * S, b1 = s1 * S * S;
        int a2 = ((s2 + S - 1) % S) * S * S, b2 = s2 * S * S;
        for (int i = 0; i < S * S; i++)
            _comp[i] = 0.5f * ((_slices[a0 + i] + _slices[b0 + i]) * w0 + (_slices[a1 + i] + _slices[b1 + i]) * w1 + (_slices[a2 + i] + _slices[b2 + i]) * w2);

        // NoizAss: 4 Oktaven, bilinear, Wiederholung (Indizes/Gewichte vorberechnet, Ergebnisse wie zuvor)
        for (int y = 0; y < N; y++)
        {
            int row = y * N;
            for (int x = 0; x < N; x++)
            {
                float acc = 0f;
                int p = 0;
                for (int o = 0; o < 4; o++, p += N)
                {
                    int x0 = _si0[p + x], y0 = _si0[p + y];
                    float tx = _sw[p + x], ty = _sw[p + y];
                    int xa = x0 & 31, xb = (x0 + 1) & 31;
                    int ya = (y0 & 31) * S, yb = ((y0 + 1) & 31) * S;
                    float c = _comp[ya + xa], d = _comp[ya + xb];
                    float a = c + (d - c) * tx;
                    c = _comp[yb + xa]; d = _comp[yb + xb];
                    float b = c + (d - c) * tx;
                    acc += OctaveAmp[o] * (a + (b - a) * ty);
                }
                _height[row + x] = acc * (8f / 15f);
            }
        }

        // NoiseNMap
        float d2 = 2f / N;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float hl = _height[y * N + ((x + N - 1) & (N - 1))], hr = _height[y * N + ((x + 1) & (N - 1))];
                float hd = _height[((y + N - 1) & (N - 1)) * N + x], hu = _height[((y + 1) & (N - 1)) * N + x];
                float nx = -Scaler * (hr - hl), ny = d2, nz = -Scaler * (hu - hd);
                float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                int o = (y * N + x) * 4;
                dst[o] = (byte)System.Math.Clamp((int)((nx * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                dst[o + 1] = (byte)System.Math.Clamp((int)((ny * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                dst[o + 2] = (byte)System.Math.Clamp((int)((nz * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                dst[o + 3] = 0;
            }
    }

    public void Dispose()
    {
        if (_thread != null)
        {
            _quit = true;
            _go.Set();
            _thread.Join();
            _thread = null;
            _go.Dispose();
        }
        Texture.Dispose();
    }
}
