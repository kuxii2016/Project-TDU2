using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Stromleitungen zwischen den Masten (Original: TD_ElectricalNetwork, Daten: Sektor-.pen, docs tdu2-specs.md §8.3).
///   .pen: "SPEN", Segment POST {f32[3] Position (Meter ab Sektorecke, Höhe absolut), f32[3] Pitch/Heading/Roll},
///         Segment LINK {u32 von, u32 nach} je Leitung, Wert = Slot &lt;&lt; 16 | Postindex.
/// Aus den Daten: welche Masten verbunden sind. NICHT in den Daten (Engine-Tabelle/-Code), daher angenähert:
///   - Aufhängepunkt je Slot: oberes Ende des Mast-Modells (Bounds), Slots einer Leitung gleichmäßig über die Breite
///     des Querträgers (lokale X-Achse) verteilt; Masten ohne Modell (Hauswände): Post-Position selbst.
///   - Durchhang: Parabel mit SagFactor × Spannweite.
/// Darstellung wie TD_ElectricalNetwork: Band mit weich auslaufendem Rand (Pixelshader: Alpha = 1 - |u-0.5|^0.2),
/// zur Kamera gedreht, Breite mindestens ~1,5 Pixel.
/// </summary>
public sealed class PowerWires
{
    public float SagFactor = 0.035f;     // Durchhang / Spannweite (angenähert)
    public float WireRadius = 0.012f;    // halbe Kabelstärke in Metern
    public float MinPixels = 1.4f;       // Mindestbreite auf dem Bildschirm
    public float DrawDistance = 600f;
    public int Segments = 14;
    public Vector3 Color = new(0.06f, 0.06f, 0.06f);
    /// <summary>Schattenwurf (Erweiterung: im Original hat TD_ElectricalNetwork keinen Schattenpass).</summary>
    public bool CastShadows = true;
    public float ShadowDistance = 150f;
    public float ShadowHalfWidth = 0.02f;

    public struct Post { public Vector3 Pos; public float Heading; }
    public struct Link { public int FromSlot, FromPost, ToSlot, ToPost; }

    public sealed class PenFile
    {
        public readonly List<Post> Posts = new();
        public readonly List<Link> Links = new();

        public static PenFile? Parse(byte[]? d)
        {
            if (d == null || d.Length < 16 || !Bin.TagIs(d, 12, "SPEN")) return null;
            var f = new PenFile();
            int o = 16;
            while (o + 32 <= d.Length)
            {
                string tag = Bin.Tag(d, o);
                int size = (int)Bin.U32(d, o + 8);
                if (size < 32 || o + size > d.Length) break;
                int count = (int)Bin.U32(d, o + 16);
                int p = o + 32;
                if (tag == "POST")
                    for (int i = 0; i < count && p + 24 <= o + size; i++, p += 24)
                        f.Posts.Add(new Post { Pos = new Vector3(Bin.F32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8)), Heading = Bin.F32(d, p + 16) });
                else if (tag == "LINK")
                    for (int i = 0; i < count && p + 8 <= o + size; i++, p += 8)
                    {
                        uint a = Bin.U32(d, p), b = Bin.U32(d, p + 4);
                        f.Links.Add(new Link { FromSlot = (int)(a >> 16), FromPost = (int)(a & 0xFFFF), ToSlot = (int)(b >> 16), ToPost = (int)(b & 0xFFFF) });
                    }
                o += size;
            }
            return f;
        }
    }

    /// <summary>Leitungen eines Sektors (Spielkoordinaten). Resolved = Aufhängepunkte bestimmt.</summary>
    public sealed class SectorWires
    {
        public PenFile Pen = null!;
        public Vector3 Origin;
        public bool Resolved, HadMissing;
        public readonly List<Vector3[]> Curves = new();
        public Vector3 Min, Max;
    }

    // Masten-Namen sind Keys (z.B. "~85L_AG..."), daher nicht nach Namen filtern: nächste Platzierung am Post (Doku: 98,9 % innerhalb 10 cm)
    static readonly string[] PolePrefixes = { "" };
    readonly List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)> _hits = new();
    readonly Dictionary<(int x, int y, int z), List<int>> _grid = new();
    static (int x, int y, int z) Cell(Vector3 p) => ((int)MathF.Floor(p.X / 1.5f), (int)MathF.Floor(p.Y / 1.5f), (int)MathF.Floor(p.Z / 1.5f));

    /// <summary>
    /// Aufhängepunkte bestimmen und Kurven bauen. findPoles = Platzierungssuche (MapStreamer.FindPlacementInstances).
    /// allowBare: auch ohne gefundenen Mast bauen (Nachbarsektor nicht geladen / Hauswand).
    /// </summary>
    public void Resolve(SectorWires w, Action<string[], Vector3, float, List<(Matrix4x4 m, Vector3 bmin, Vector3 bmax, string name)>> findPoles, bool allowBare)
    {
        var pen = w.Pen;
        int n = pen.Posts.Count;
        var poles = new (bool ok, Matrix4x4 m, Vector3 bmin, Vector3 bmax)[n];
        bool missing = false;
        int found = 0;
        if (n == 0) return;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++) { var gp = w.Origin + pen.Posts[i].Pos; mn = Vector3.Min(mn, gp); mx = Vector3.Max(mx, gp); }
        _hits.Clear();
        findPoles(PolePrefixes, (mn + mx) * 0.5f, (mx - mn).Length() * 0.5f + 1.6f, _hits);
        _grid.Clear();
        for (int k = 0; k < _hits.Count; k++)
        {
            var p = _hits[k].m.Translation;
            var key = Cell(p);
            if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>(2);
            l.Add(k);
        }
        for (int i = 0; i < n; i++)
        {
            var gp = w.Origin + pen.Posts[i].Pos;
            var c = Cell(gp);
            float best = 1.5f * 1.5f; bool any = false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!_grid.TryGetValue((c.x + dx, c.y + dy, c.z + dz), out var l)) continue;
                        foreach (int k in l)
                        {
                            var h = _hits[k];
                            float dd = (h.m.Translation - gp).LengthSquared();
                            if (dd <= best) { best = dd; poles[i] = (true, h.m, h.bmin, h.bmax); any = true; }
                        }
                    }
            if (!any) { missing = true; continue; }
            found++;
        }
        if (missing && !allowBare) return;

        var slots = new List<int>[n];
        for (int i = 0; i < n; i++) slots[i] = new List<int>();
        foreach (var l in pen.Links)
        {
            if (l.FromPost < n && !slots[l.FromPost].Contains(l.FromSlot)) slots[l.FromPost].Add(l.FromSlot);
            if (l.ToPost < n && !slots[l.ToPost].Contains(l.ToSlot)) slots[l.ToPost].Add(l.ToSlot);
        }
        foreach (var s in slots) s.Sort();

        Vector3 Attach(int post, int slot)
        {
            var gp = w.Origin + pen.Posts[post].Pos;
            var pl = poles[post];
            if (!pl.ok) return gp;
            var s = slots[post];
            int rank = System.Math.Max(0, s.IndexOf(slot));
            float t = s.Count <= 1 ? 0.5f : rank / (float)(s.Count - 1);
            float x0 = pl.bmin.X + 0.08f, x1 = pl.bmax.X - 0.08f;
            if (x1 < x0) { x0 = x1 = (pl.bmin.X + pl.bmax.X) * 0.5f; }
            var local = new Vector3(x0 + (x1 - x0) * t, pl.bmax.Y - 0.15f, (pl.bmin.Z + pl.bmax.Z) * 0.5f);
            return Vector3.Transform(local, pl.m);
        }

        w.Curves.Clear();
        w.Min = new Vector3(float.MaxValue); w.Max = new Vector3(float.MinValue);
        foreach (var l in pen.Links)
        {
            if (l.FromPost >= n || l.ToPost >= n) continue;
            var a = Attach(l.FromPost, l.FromSlot);
            var b = Attach(l.ToPost, l.ToSlot);
            float span = Vector3.Distance(a, b);
            if (span < 0.5f || span > 400f) continue;
            float sag = span * SagFactor;
            var pts = new Vector3[Segments + 1];
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                var p = Vector3.Lerp(a, b, t);
                p.Y -= 4f * sag * t * (1f - t);
                pts[i] = p;
                w.Min = Vector3.Min(w.Min, p); w.Max = Vector3.Max(w.Max, p);
            }
            w.Curves.Add(pts);
        }
        w.Resolved = true;
        w.HadMissing = missing;
    }

    // ------------------------------------------------------------------ Zeichnen
    IGpuTexture? _tex;
    IGpuMaterial? _mat, _shadowMat;
    ObjectVertex[] _v = new ObjectVertex[4096];
    uint[] _i = new uint[6144];
    ObjectVertex[] _sv = new ObjectVertex[4096];
    uint[] _si = new uint[6144];

    void EnsureMaterial(IRenderer r)
    {
        if (_mat != null) return;
        // Querprofil wie TD_ElectricalNetwork-Pixelshader: Alpha = 1 - |u - 0.5|^0.2
        const int W = 16;
        var px = new byte[W * 4];
        for (int x = 0; x < W; x++)
        {
            float u = (x + 0.5f) / W;
            float a = 1f - MathF.Pow(MathF.Abs(u - 0.5f), 0.2f);
            a = System.Math.Clamp(a / (1f - MathF.Pow(0.5f / W, 0.2f)), 0f, 1f);
            px[x * 4] = 255; px[x * 4 + 1] = 255; px[x * 4 + 2] = 255; px[x * 4 + 3] = (byte)(a * 255);
        }
        _tex = r.CreateTexture(new TextureUpload { Width = W, Height = 1, Format = GpuTextureFormat.Rgba8, Mips = 1, Blocks = px, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Repeat, Name = "Wire" });
        _mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Transparent, Name = "PowerWire", Main = _tex, Lit = 0f, VcMul = true });
        _shadowMat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.Object, Name = "PowerWireShadow" });
    }

    public void Draw(IRenderer r, IEnumerable<SectorWires> sectors, Vector3 cameraGame, Vector3 rootOffset, Vector3 sunDir)
    {
        int nv = 0, ni = 0, snv = 0, sni = 0;
        float sd2 = ShadowDistance * ShadowDistance;
        var sun = sunDir.LengthSquared() > 1e-6f ? Vector3.Normalize(sunDir) : Vector3.UnitY;
        float pix = 2f * MathF.Tan(MathF.PI / 6f) / System.Math.Max(1, r.Height) * MinPixels;   // FOV 60°
        float d2max = DrawDistance * DrawDistance;
        byte cr = (byte)(Color.X * 255), cg = (byte)(Color.Y * 255), cb = (byte)(Color.Z * 255);
        uint col = ObjectVertex.PackColor(cr, cg, cb, 255);
        foreach (var w in sectors)
        {
            if (!w.Resolved || w.Curves.Count == 0) continue;
            var c = Vector3.Clamp(cameraGame, w.Min, w.Max);
            if ((c - cameraGame).LengthSquared() > d2max) continue;
            foreach (var pts in w.Curves)
            {
                var mid = pts[pts.Length / 2];
                if ((mid - cameraGame).LengthSquared() > d2max * 1.2f) continue;
                int need = pts.Length * 2;
                if (nv + need > _v.Length) { Array.Resize(ref _v, _v.Length * 2); Array.Resize(ref _i, _i.Length * 2); }
                int start = nv;
                for (int k = 0; k < pts.Length; k++)
                {
                    var p = pts[k];
                    var dir = k < pts.Length - 1 ? pts[k + 1] - p : p - pts[k - 1];
                    var toCam = cameraGame - p;
                    float dist = toCam.Length();
                    var side = Vector3.Cross(dir, toCam);
                    float sl = side.Length();
                    side = sl > 1e-5f ? side / sl : Vector3.UnitY;
                    float half = MathF.Max(WireRadius, dist * pix * 0.5f);
                    var pr = p + rootOffset;
                    float v = k / (float)(pts.Length - 1);
                    _v[nv++] = new ObjectVertex { Position = pr - side * half, Normal = Vector3.UnitY, Color = col, Uv0 = new Vector2(0f, v) };
                    _v[nv++] = new ObjectVertex { Position = pr + side * half, Normal = Vector3.UnitY, Color = col, Uv0 = new Vector2(1f, v) };
                }
                if (ni + (pts.Length - 1) * 6 > _i.Length) Array.Resize(ref _i, _i.Length * 2);
                for (int k = 0; k < pts.Length - 1; k++)
                {
                    uint a = (uint)(start + k * 2);
                    _i[ni++] = a; _i[ni++] = a + 2; _i[ni++] = a + 1;
                    _i[ni++] = a + 1; _i[ni++] = a + 2; _i[ni++] = a + 3;
                }

                // Schattenband: zur Sonne gedreht, feste Breite
                if (CastShadows && (mid - cameraGame).LengthSquared() < sd2)
                {
                    if (snv + need > _sv.Length) { Array.Resize(ref _sv, _sv.Length * 2); Array.Resize(ref _si, _si.Length * 2); }
                    if (sni + (pts.Length - 1) * 6 > _si.Length) Array.Resize(ref _si, _si.Length * 2);
                    int s0 = snv;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        var p = pts[k];
                        var dir = k < pts.Length - 1 ? pts[k + 1] - p : p - pts[k - 1];
                        var side = Vector3.Cross(dir, sun);
                        float sl = side.Length();
                        side = sl > 1e-5f ? side / sl : Vector3.UnitX;
                        var pr = p + rootOffset;
                        _sv[snv++] = new ObjectVertex { Position = pr - side * ShadowHalfWidth, Normal = sun, Color = col };
                        _sv[snv++] = new ObjectVertex { Position = pr + side * ShadowHalfWidth, Normal = sun, Color = col };
                    }
                    for (int k = 0; k < pts.Length - 1; k++)
                    {
                        uint a = (uint)(s0 + k * 2);
                        _si[sni++] = a; _si[sni++] = a + 2; _si[sni++] = a + 1;
                        _si[sni++] = a + 1; _si[sni++] = a + 2; _si[sni++] = a + 3;
                    }
                }
            }
        }
        if (ni == 0) return;
        EnsureMaterial(r);
        var mesh = r.CreateMesh(_v.AsSpan(0, nv), _i.AsSpan(0, ni));
        r.DrawMesh(mesh, _mat!, Matrix4x4.Identity);
        mesh.Dispose();   // Freigabe verzögert (wie Regen)
        if (sni > 0 && _shadowMat != null)
        {
            var sm = r.CreateMesh(_sv.AsSpan(0, snv), _si.AsSpan(0, sni));
            r.DrawShadowOnly(sm, _shadowMat, Matrix4x4.Identity);
            sm.Dispose();
        }
    }

    public void Dispose()
    {
        _mat?.Dispose(); _mat = null;
        _shadowMat?.Dispose(); _shadowMat = null;
        _tex?.Dispose(); _tex = null;
    }
}