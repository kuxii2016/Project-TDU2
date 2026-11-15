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
        Build();
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
    }

    /// <summary>Jedes Bild (Phase läuft mit 1/s, Periode 32 s).</summary>
    public void Update(float dt)
    {
        if (dt <= 0f) return;
        _t += dt;
        if (_t >= 32f) _t -= 32f;
        _acc += dt;
        if (_acc < 1f / MathF.Max(1f, Rate)) return;
        _acc = 0f;
        Build();
        _r.UpdateTexture(Texture, _rgba);
    }

    void Build()
    {
        // NoizComp
        int s0 = (int)MathF.Floor(_t) % S, s1 = (s0 + 1) % S, s2 = (s1 + 1) % S;
        float f = _t - MathF.Floor(_t);
        float W(float a) { float s = MathF.Sin(a); return s * s / 1.5f; }
        float w0 = W(MathF.PI * (2f + f) / 3f), w1 = W(MathF.PI * (1f + f) / 3f), w2 = W(MathF.PI * f / 3f);
        int a0 = ((s0 + S - 1) % S) * S * S, b0 = s0 * S * S;
        int a1 = ((s1 + S - 1) % S) * S * S, b1 = s1 * S * S;
        int a2 = ((s2 + S - 1) % S) * S * S, b2 = s2 * S * S;
        for (int i = 0; i < S * S; i++)
            _comp[i] = 0.5f * ((_slices[a0 + i] + _slices[b0 + i]) * w0 + (_slices[a1 + i] + _slices[b1 + i]) * w1 + (_slices[a2 + i] + _slices[b2 + i]) * w2);

        // NoizAss: 4 Oktaven, bilinear, Wiederholung
        for (int y = 0; y < N; y++)
        {
            float v = (y + 0.5f) / N;
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N, acc = 0f, amp = 1f, k = 1f;
                for (int o = 0; o < 4; o++, amp *= 0.5f, k *= 2f)
                    acc += amp * Bilinear(u * k * S - 0.5f, v * k * S - 0.5f);
                _height[y * N + x] = acc * (8f / 15f);
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
                _rgba[o] = (byte)System.Math.Clamp((int)((nx * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                _rgba[o + 1] = (byte)System.Math.Clamp((int)((ny * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                _rgba[o + 2] = (byte)System.Math.Clamp((int)((nz * inv * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                _rgba[o + 3] = 0;
            }
    }

    float Bilinear(float fx, float fy)
    {
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int xa = x0 & 31, xb = (x0 + 1) & 31, ya = y0 & 31, yb = (y0 + 1) & 31;
        float a = _comp[ya * S + xa] + (_comp[ya * S + xb] - _comp[ya * S + xa]) * tx;
        float b = _comp[yb * S + xa] + (_comp[yb * S + xb] - _comp[yb * S + xa]) * tx;
        return a + (b - a) * ty;
    }

    public void Dispose() => Texture.Dispose();
}
