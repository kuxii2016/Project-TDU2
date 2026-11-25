using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Sternenhimmel wie im Original: Die exe baut das Dom-Mesh selbst (kein Datei-Mesh) und legt die Sterne in einen zweiten
/// Vertexstrom, den td_dome vs @0xc50 als NORMAL liest:
/// <list type="bullet">
/// <item>0x7751b0: MatDome mit Textur STARS (fx/weatherfx.bnk stars.2db), Mesh "renderListDome" = 0x774c50(30, 30).</item>
/// <item>0x774c50: Würfelkugel – 6 Flächen (Tabelle 0x16622e0: ±X, ±Y, ±Z), je (w+1)·(h+1) Punkte, Startecke
///   (a−b−c, b−a−c, c−a−b), Schritte (2c, 2a, 2b)/h und (2b, 2c, 2a)/w; danach x = sign(x)·√|x|, z = sign(z)·√|z|,
///   y = ((((y+1)·0,5)^0,3)·2 − 1)^5. Dreiecksstreifen je Zeile mit doppelten Randindizes.</item>
/// <item>0x7744b0: Sternkanal je Punkt: n = normalize(Pos), Fläche D: U = n.x·D.z + n.y·D.x + n.z·D.y,
///   V = n.x·D.y + n.y·D.z + n.z·D.x, W = |n·D|, UV = ((U/W)+1)·2,5 und ((V/W)+1)·2,5,
///   Helligkeit = (1 − sat(fBm(n + 1) + 0,25))·1,5.</item>
/// <item>fBm 0x777ee0 (Oktaven 4, Frequenz 3,3 nur auf x/y, Amplitude 1, je Oktave ×2 / ×0,5) über Perlins noise3
///   (0x777aa0, B = 1024, N = 4096) mit Tabellen aus 0x777420 nach srand(3) und MSVC-rand.</item>
/// </list>
/// Gezeichnet wird das Mesh mit td_dome ps @0x1af0 (Himmel + Sterne), nur wenn Skydome_starsPercent &gt; 0.
/// </summary>
public sealed class Stars : IDisposable
{
    readonly IRenderer _r;
    IGpuMesh? _mesh;
    IGpuMaterial? _mat;
    IGpuMesh? _moonMesh;
    IGpuMaterial? _moonMat;

    Stars(IRenderer r) => _r = r;

    public static Stars? Create(string? bnkDir, IRenderer r, GlobalTextureCache textures)
    {
        var s = new Stars(r);
        try
        {
            IGpuTexture? tex = null, moon = null;
            string? path = bnkDir != null ? Path.Combine(bnkDir, "fx", "weatherfx.bnk") : null;
            if (path != null && GameFiles.Exists(path))
            {
                textures.IndexBank(BnkArchive.Open(path));
                tex = textures.Get(textures.KeyByName("stars.2db"), TextureWrap.Repeat, TextureWrap.Repeat);
                moon = textures.Get(textures.KeyByName("moon.2db"), TextureWrap.Clamp, TextureWrap.Clamp);
            }
            if (moon == null) Log.Warn("Moon: moon.2db (fx/weatherfx.bnk) not found");
            if (tex == null) Log.Warn("Stars: stars.2db (fx/weatherfx.bnk) not found");
            BuildDome(30, 30, out var verts, out var idx);
            s._mesh = r.CreateMesh(verts, idx);
            s._mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.SkyStars, Name = "MatDome_Stars", Main = tex });
            // Mond-Quad wie 0x77110d: (−1,−1)/(0,0), (−1,1)/(0,1), (1,1)/(1,1), (1,−1)/(1,0), Streifen 0,1,3,2
            uint white = ObjectVertex.PackColor(255, 255, 255, 255);
            var mv = new[]
            {
                new ObjectVertex { Position = new Vector3(-1f, -1f, 0f), Uv0 = new Vector2(0f, 0f), Color = white },
                new ObjectVertex { Position = new Vector3(-1f, 1f, 0f), Uv0 = new Vector2(0f, 1f), Color = white },
                new ObjectVertex { Position = new Vector3(1f, 1f, 0f), Uv0 = new Vector2(1f, 1f), Color = white },
                new ObjectVertex { Position = new Vector3(1f, -1f, 0f), Uv0 = new Vector2(1f, 0f), Color = white },
            };
            s._moonMesh = r.CreateMesh(mv, new uint[] { 0, 1, 3, 1, 2, 3 });
            s._moonMat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.SkyMoon, Name = "MatMoon", Main = moon });
            return s;
        }
        catch (Exception ex) { Log.Error("Stars: " + ex.Message); s.Dispose(); return null; }
    }

    /// <summary>Zeichnen um die Kamera (Render-Raum). Die Tiefe setzt der Shader auf die Fernebene.</summary>
    public void Draw(Vector3 cameraRender)
    {
        if (_mesh == null || _mat == null) return;
        _r.DrawMesh(_mesh, _mat, Matrix4x4.CreateTranslation(cameraRender));
    }

    /// <summary>
    /// Mond (td_moon vs @0x4c0 / ps @0xfe0, Werte 0xd01630): PosMoon = (−L·100000, Skydome_moonPercent), L = Lichtrichtung
    /// (sundirection, nachts der Mond), SizeMoon.x = Skydome_moonSize·10. Billboard: Achse f = normalize(Kamera − Mond),
    /// rechts = (f.z, 0, −f.x) (unnormiert wie im Shader), oben = f × rechts; Pos = Mond + Größe·(x·rechts + y·oben).
    /// Hier um die Kamera gelegt (Mond 100 km entfernt in Lichtrichtung).
    /// </summary>
    public void DrawMoon(Vector3 cameraRender, Vector3 toLight, float size)
    {
        if (_moonMesh == null || _moonMat == null || toLight.LengthSquared() < 1e-12f) return;
        var f = -Vector3.Normalize(toLight);                 // Kamera − Mond
        var right = new Vector3(f.Z, 0f, -f.X);
        var up = Vector3.Cross(f, right);
        var pos = cameraRender - f * 100000f;
        var m = new Matrix4x4(
            right.X * size, right.Y * size, right.Z * size, 0f,
            up.X * size, up.Y * size, up.Z * size, 0f,
            f.X * size, f.Y * size, f.Z * size, 0f,
            pos.X, pos.Y, pos.Z, 1f);
        _r.DrawMesh(_moonMesh, _moonMat, m);
    }

    public void Dispose()
    {
        _mesh?.Dispose(); _mesh = null;
        _mat?.Dispose(); _mat = null;
        _moonMesh?.Dispose(); _moonMesh = null;
        _moonMat?.Dispose(); _moonMat = null;
    }

    // ------------------------------------------------------------------ Mesh (0x774c50 + 0x7744b0)
    static readonly Vector3[] Faces =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    static void BuildDome(int w, int h, out ObjectVertex[] verts, out uint[] tris)
    {
        int cols = w + 1, rows = h + 1, perFace = cols * rows;
        verts = new ObjectVertex[perFace * 6];
        var noise = new PerlinNoise(3);
        uint white = ObjectVertex.PackColor(255, 255, 255, 255);
        int vi = 0;
        foreach (var D in Faces)
        {
            float a = D.X, b = D.Y, c = D.Z;
            var start = new Vector3(a - b - c, b - a - c, c - a - b);   // (−c−b + a, −a−c + b, −b−a + c)
            var stepRow = new Vector3(2f * c, 2f * a, 2f * b) / h;
            var stepCol = new Vector3(2f * b, 2f * c, 2f * a) / w;
            var row = start;
            for (int j = 0; j < rows; j++)
            {
                for (int i = 0; i < cols; i++)
                {
                    var p = stepCol * i + row;
                    float x = (p.X < 0f ? -1f : 1f) * MathF.Sqrt(MathF.Abs(p.X));
                    float z = (p.Z < 0f ? -1f : 1f) * MathF.Sqrt(MathF.Abs(p.Z));
                    float y = (float)System.Math.Pow(System.Math.Pow((p.Y + 1.0) * 0.5, 0.3) * 2.0 - 1.0, 5.0);
                    var pos = new Vector3(x, y, z);
                    verts[vi++] = new ObjectVertex { Position = pos, Normal = StarChannel(pos, D, noise), Color = white };
                }
                row += stepRow;
            }
        }
        // Dreiecksstreifen je Zeile (0x7750c7) als Dreiecksliste, ohne entartete Dreiecke
        var list = new List<uint>();
        for (int f = 0; f < 6; f++)
            for (int j = 0; j < h; j++)
            {
                uint r0 = (uint)(f * perFace + j * cols), r1 = r0 + (uint)cols;
                for (int i = 0; i < w; i++)
                {
                    uint a0 = r0 + (uint)i, b0 = r1 + (uint)i, a1 = a0 + 1, b1 = b0 + 1;
                    list.Add(a0); list.Add(b0); list.Add(a1);
                    list.Add(a1); list.Add(b0); list.Add(b1);
                }
            }
        tris = list.ToArray();
    }

    static Vector3 StarChannel(Vector3 pos, Vector3 D, PerlinNoise noise)
    {
        var n = Vector3.Normalize(pos);
        float U = n.X * D.Z + n.Y * D.X + n.Z * D.Y;
        float V = n.X * D.Y + n.Y * D.Z + n.Z * D.X;
        float W = MathF.Abs(Vector3.Dot(n, D));
        float u = (U / W + 1f) * 2.5f, v = (V / W + 1f) * 2.5f;
        float r = noise.Fbm(n + Vector3.One, 4, 3.3f, 1f) + 0.25f;
        float c = r < 0f ? 0f : r > 1f ? 1f : r;
        return new Vector3(u, v, (1f - c) * 1.5f);
    }

    /// <summary>Ken Perlins noise3 (Referenz 2002 „noise1/2/3“), wie in der exe: Tabellen nach srand(seed) mit MSVC-rand.</summary>
    sealed class PerlinNoise
    {
        const int B = 0x400, BM = 0x3ff, N = 0x1000;
        readonly int[] p = new int[B + B + 2];
        readonly float[,] g3 = new float[B + B + 2, 3];
        uint _seed;

        int Rand() { _seed = _seed * 214013u + 2531011u; return (int)((_seed >> 16) & 0x7fff); }

        public PerlinNoise(uint seed)
        {
            _seed = seed;
            var g1 = new float[B + B + 2];
            var g2 = new float[B + B + 2, 2];
            for (int i = 0; i < B; i++)
            {
                p[i] = i;
                g1[i] = (Rand() % (B + B) - B) / (float)B;
                float x = (Rand() % (B + B) - B) / (float)B, y = (Rand() % (B + B) - B) / (float)B;
                float s = 1f / MathF.Sqrt(x * x + y * y);
                g2[i, 0] = x * s; g2[i, 1] = y * s;
                float a = (Rand() % (B + B) - B) / (float)B, b = (Rand() % (B + B) - B) / (float)B, c = (Rand() % (B + B) - B) / (float)B;
                s = 1f / MathF.Sqrt(a * a + b * b + c * c);
                g3[i, 0] = a * s; g3[i, 1] = b * s; g3[i, 2] = c * s;
            }
            for (int i = B - 1; i > 0; i--)
            {
                int k = p[i], j = Rand() % B;
                p[i] = p[j]; p[j] = k;
            }
            for (int i = 0; i < B + 2; i++)
            {
                p[B + i] = p[i];
                for (int k = 0; k < 3; k++) g3[B + i, k] = g3[i, k];
            }
        }

        static float SCurve(float t) => t * t * (3f - 2f * t);
        static float Lerp(float t, float a, float b) => a + t * (b - a);

        public float Noise3(Vector3 v)
        {
            static void Setup(float x, out int b0, out int b1, out float r0, out float r1)
            {
                float t = x + N;
                int it = (int)t;
                b0 = it & BM; b1 = (b0 + 1) & BM;
                r0 = t - it; r1 = r0 - 1f;
            }
            Setup(v.X, out int bx0, out int bx1, out float rx0, out float rx1);
            Setup(v.Y, out int by0, out int by1, out float ry0, out float ry1);
            Setup(v.Z, out int bz0, out int bz1, out float rz0, out float rz1);
            int i = p[bx0], j = p[bx1];
            int b00 = p[i + by0], b10 = p[j + by0], b01 = p[i + by1], b11 = p[j + by1];
            float t = SCurve(rx0), sy = SCurve(ry0), sz = SCurve(rz0);
            float At(int q, float rx, float ry, float rz) => rx * g3[q, 0] + ry * g3[q, 1] + rz * g3[q, 2];
            float a = Lerp(t, At(b00 + bz0, rx0, ry0, rz0), At(b10 + bz0, rx1, ry0, rz0));
            float b = Lerp(t, At(b01 + bz0, rx0, ry1, rz0), At(b11 + bz0, rx1, ry1, rz0));
            float c = Lerp(sy, a, b);
            a = Lerp(t, At(b00 + bz1, rx0, ry0, rz1), At(b10 + bz1, rx1, ry0, rz1));
            b = Lerp(t, At(b01 + bz1, rx0, ry1, rz1), At(b11 + bz1, rx1, ry1, rz1));
            float d = Lerp(sy, a, b);
            return Lerp(sz, c, d);
        }

        /// <summary>0x777ee0: Frequenz nur auf x/y, Summe amp·noise3, je Oktave Punkt ×2, Amplitude ×0,5.</summary>
        public float Fbm(Vector3 v, int octaves, float freq, float amp)
        {
            v.X *= freq; v.Y *= freq;
            float sum = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Noise3(v) * amp;
                v *= 2f;
                amp *= 0.5f;
            }
            return sum;
        }
    }
}