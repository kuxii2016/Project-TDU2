using Paradise.Core.Files;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Paradise.Core.Formats;

/// <summary>Weather types as in the game (table from the exe: Sunny, Cloudy, Rainy, Before_rain, After_rain, Tempest).</summary>
public enum WeatherKind { Sunny = 0, Cloudy = 1, Rainy = 2, BeforeRain = 3, AfterRain = 4, Tempest = 5 }

/// <summary>
/// islands/insel/fx/game_weathers.xmb (big-endian XMBF): the game day as 24 keyframes (00, 01:30 ... 23:20),
/// 7 blocks each: Block 0 is time-dependent only (sundirection, tunnel); Blocks 1–6 contain the weather types, each with ~151 parameters
/// (sun_diffuse/ambient, fog_*, rain_*, thunder_*, outside_street_lightmap, spot_*_lightmap_day_night ...).
/// </summary>
public sealed class WeatherData
{
    public sealed class Key
    {
        public string Name = "";
        public float Hour;
        public Dictionary<string, Vector4>[] Blocks = Array.Empty<Dictionary<string, Vector4>>();
        public Dictionary<string, string>[] Strings = Array.Empty<Dictionary<string, string>>();
    }

    public readonly List<Key> Keys = new();

    public static WeatherData Load(string path)
    {
        var b = GameFiles.ReadAllBytes(path);
        var root = new BeXmb(b).Root() as Dictionary<string, object?>;
        if (root == null || !root.TryGetValue("weather", out var wo) || wo is not List<object?> weathers) throw new InvalidDataException("kein WEATHER_EXPORT: " + path);
        var data = new WeatherData();
        foreach (var o in weathers)
        {
            if (o is not Dictionary<string, object?> w) continue;
            var k = new Key
            {
                Name = w.TryGetValue("name", out var n) ? n as string ?? "" : "",
                Hour = ToInt(w, "hour") + ToInt(w, "minutes") / 60f,
            };
            var blocks = w.TryGetValue("intemperys", out var io) ? io as List<object?> : null;
            int nb = blocks != null ? blocks.Count : 0;
            k.Blocks = new Dictionary<string, Vector4>[System.Math.Max(nb, 7)];
            k.Strings = new Dictionary<string, string>[k.Blocks.Length];
            for (int i = 0; i < k.Blocks.Length; i++) { k.Blocks[i] = new Dictionary<string, Vector4>(); k.Strings[i] = new Dictionary<string, string>(); }
            for (int i = 0; i < nb; i++)
            {
                if (blocks![i] is not Dictionary<string, object?> blk || !blk.TryGetValue("weather_params", out var po) || po is not List<object?> ps) continue;
                foreach (var pp in ps)
                {
                    if (pp is not Dictionary<string, object?> p || !p.TryGetValue("name", out var pn) || pn is not string name) continue;
                    if (!p.TryGetValue("param", out var pv) || pv is not Dictionary<string, object?> v) continue;
                    if (v.TryGetValue("value", out var sv) && sv is string s) { k.Strings[i][name] = s; continue; }
                    k.Blocks[i][name] = new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
                }
            }
            data.Keys.Add(k);
        }
        data.Keys.Sort((a, c) => a.Hour.CompareTo(c.Hour));
        return data;
    }

    static int ToInt(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
    static float F(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 0f;

    /// <summary>A modifier (“wmod:Name” in the .cin file): overrides individual parameters of a cinema weather model on a per-cut basis,
    /// usually the sun's direction, as well as light, shadows, fog, and bloom. weather_name = the weather model to which it applies.</summary>
    public sealed class Modifier
    {
        public string Name = "", Weather = "";
        public int Block = -1;
        public readonly List<(string name, Vector4 v, string? s)> Params = new();
    }

    public readonly List<Modifier> Modifiers = new();
    readonly Dictionary<string, Key> _modified = new(StringComparer.OrdinalIgnoreCase);

    /// Load <summary>fx/cinematic_modifiers.xmb (MODIFIER_EXPORT: modifier[] {name, type, weather_name, intempery_index, param[]}).</summary>
    public int LoadModifiers(string path)
    {
        if (string.IsNullOrEmpty(path) || !GameFiles.Exists(path)) return 0;
        var root = new BeXmb(GameFiles.ReadAllBytes(path)).Root() as Dictionary<string, object?>;
        if (root == null || !root.TryGetValue("modifier", out var mo) || mo is not List<object?> list) return 0;
        foreach (var o in list)
        {
            if (o is not Dictionary<string, object?> m) continue;
            var mod = new Modifier
            {
                Name = m.TryGetValue("name", out var n) ? n as string ?? "" : "",
                Weather = m.TryGetValue("weather_name", out var wn) ? wn as string ?? "" : "",
                Block = m.TryGetValue("intempery_index", out var ii) && ii != null ? Convert.ToInt32(ii, CultureInfo.InvariantCulture) : -1,
            };
            if (m.TryGetValue("param", out var po) && po is List<object?> ps)
                foreach (var pp in ps)
                {
                    if (pp is not Dictionary<string, object?> p || !p.TryGetValue("name", out var pn) || pn is not string name) continue;
                    if (!p.TryGetValue("param", out var pv) || pv is not Dictionary<string, object?> v) continue;
                    if (v.TryGetValue("value", out var sv) && sv is string s) mod.Params.Add((name, default, s));
                    else mod.Params.Add((name, new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w")), null));
                }
            Modifiers.Add(mod);
        }
        _modified.Clear();
        return Modifiers.Count;
    }

    /// <summary>
    /// Movie theater weather “weat:weather” with modifier “wmod:modifier”: weather key, followed by the parameters of the
    /// modifier (first the one with the matching weather_name, otherwise any one with that name). A key that shares the same name
    /// as the modifier takes precedence. Result cached per pair (same reference per cut).
    /// </summary>
    public Key? Find(string weather, string modifier)
    {
        if (string.IsNullOrEmpty(weather)) return null;
        Key? b = null, named = null;
        foreach (var k in Keys)
        {
            if (!string.IsNullOrEmpty(modifier) && string.Equals(k.Name, modifier, StringComparison.OrdinalIgnoreCase)) named = k;
            if (string.Equals(k.Name, weather, StringComparison.OrdinalIgnoreCase)) b = k;
        }
        if (named != null) return named;
        if (b == null || string.IsNullOrEmpty(modifier)) return b;
        string id = weather + "/" + modifier;
        if (_modified.TryGetValue(id, out var cached)) return cached;
        Modifier? mod = null;
        foreach (var m in Modifiers)
            if (string.Equals(m.Name, modifier, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(m.Weather, weather, StringComparison.OrdinalIgnoreCase)) { mod = m; break; }
                mod ??= m;
            }
        if (mod == null) { _modified[id] = b; return b; }
        var r = new Key { Name = id, Hour = b.Hour, Blocks = new Dictionary<string, Vector4>[b.Blocks.Length], Strings = new Dictionary<string, string>[b.Blocks.Length] };
        for (int i = 0; i < b.Blocks.Length; i++) { r.Blocks[i] = new Dictionary<string, Vector4>(b.Blocks[i]); r.Strings[i] = new Dictionary<string, string>(b.Strings[i]); }
        foreach (var (name, v, s) in mod.Params)
        {
            bool any = false;
            for (int i = 0; i < r.Blocks.Length; i++)
            {
                if (mod.Block >= 0 && i != mod.Block) continue;
                if (s != null ? !r.Strings[i].ContainsKey(name) : !r.Blocks[i].ContainsKey(name)) continue;
                if (s != null) r.Strings[i][name] = s; else r.Blocks[i][name] = v;
                any = true;
            }
            if (any) continue;
            // Parameter fehlt im Wetter: Sonnenrichtung/Tunnel stehen in Block 0, alles andere in Block 1
            int blk = mod.Block >= 0 && mod.Block < r.Blocks.Length ? mod.Block : (name == "sundirection" || name.StartsWith("tunnel_", StringComparison.Ordinal) ? 0 : 1);
            if (s != null) r.Strings[blk][name] = s; else r.Blocks[blk][name] = v;
        }
        _modified[id] = r;
        return r;
    }

    /// <summary>Two key images representing the time (0–24) and the blend ratio.</summary>
    public void Bracket(float hour, out Key a, out Key b, out float f)
    {
        int n = Keys.Count;
        a = b = Keys[0]; f = 0f;
        if (n == 1) return;
        int i = n - 1;
        for (int k = 0; k < n; k++) if (Keys[k].Hour <= hour) i = k;
        a = Keys[i]; b = Keys[(i + 1) % n];
        float h0 = a.Hour, h1 = b.Hour, h = hour;
        if (h1 <= h0) { h1 += 24f; if (h < h0) h += 24f; }
        f = h1 > h0 ? System.Math.Clamp((h - h0) / (h1 - h0), 0f, 1f) : 0f;
    }

    public Vector4 Value(Key k, int block, string name, Vector4 def)
    {
        if (block < 0 || block >= k.Blocks.Length) return def;
        return k.Blocks[block].TryGetValue(name, out var v) ? v : def;
    }

    /// <summary>Time parameters, mixed between two weather types (Block = Type + 1; Block 0 = time only).</summary>
    public Vector4 Sample(string name, float hour, int blockA, int blockB, float t, Vector4 def = default)
    {
        var va = Smooth(name, hour, blockA, def);
        if (blockB == blockA || t <= 0f) return va;
        var vb = Smooth(name, hour, blockB, def);
        return Vector4.Lerp(va, vb, t);
    }

    /// <summary>
    /// Value between hourly keyframes as a Catmull-Rom curve (instead of linear): The sun's path, light, and
    /// fog colors change smoothly at every full hour. Runs through all key values.
    /// </summary>
    Vector4 Smooth(string name, float hour, int block, Vector4 def)
    {
        int n = Keys.Count;
        if (n < 4) { Bracket(hour, out var a, out var b, out float lf); return Vector4.Lerp(Value(a, block, name, def), Value(b, block, name, def), lf); }
        int i = n - 1;
        for (int k = 0; k < n; k++) if (Keys[k].Hour <= hour) i = k;
        Bracket(hour, out _, out _, out float f);
        var p0 = Value(Keys[(i - 1 + n) % n], block, name, def);
        var p1 = Value(Keys[i], block, name, def);
        var p2 = Value(Keys[(i + 1) % n], block, name, def);
        var p3 = Value(Keys[(i + 2) % n], block, name, def);
        float f2 = f * f, f3 = f2 * f;
        return 0.5f * (2f * p1 + (p2 - p0) * f + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f2 + (3f * p1 - p0 - 3f * p2 + p3) * f3);
    }

    /// <summary>Minimal reader for big-endian XMBF (weather exports). Children of an object type: {type, name} pairs.</summary>
    sealed class BeXmb
    {
        readonly byte[] _b; readonly int _desc, _meta, _data;
        public BeXmb(byte[] b)
        {
            if (b.Length < 28 || b[0] != 'X' || b[1] != 'M' || b[2] != 'B' || b[3] != 'F') throw new InvalidDataException("keine XMBF-Datei");
            _b = b; _desc = I(8); _meta = I(12); _data = I(16);
        }
        int I(int o) => (_b[o] << 24) | (_b[o + 1] << 16) | (_b[o + 2] << 8) | _b[o + 3];
        uint W(int i) => (uint)I(_meta + 4 * i);
        string CStr(int o)
        {
            int e = o; while (e < _b.Length && _b[e] != 0) e++;
            return Encoding.UTF8.GetString(_b, o, e - o);
        }
        public object? Root() => Val(I(24), _data, 0);
        int SizeOf(int t)
        {
            uint tw = W(t); int ty = (int)(tw & 15);
            if (ty == 12) return 4;
            if (ty == 13 || ty == 14) return 8;
            return (int)((tw >> 15) & 0x1FFFF);
        }
        object? Field(uint tp, int addr, int depth)
        {
            int t = (int)(tp & 0x3FFFFFFF);
            if ((tp & 0x80000000u) == 0) return Val(t, addr, depth);
            int n = I(addr), bas = _data + I(addr + 4), sz = SizeOf(t);
            var list = new List<object?>(System.Math.Max(0, n));
            for (int i = 0; i < n; i++) list.Add(Val(t, bas + sz * i, depth + 1));
            return list;
        }
        object? Val(int t, int addr, int depth)
        {
            if (depth > 32) return null;
            uint tw = W(t); int ty = (int)(tw & 15);
            switch (ty)
            {
                case 0:
                    {
                        int n = (int)((tw >> 4) & 0x7FF), off = 0;
                        var obj = new Dictionary<string, object?>(n);
                        for (int i = 0; i < n; i++)
                        {
                            uint tp = W(t + 2 + 2 * i); string name = CStr(_desc + (int)W(t + 3 + 2 * i));
                            obj[name] = Field(tp, addr + off, depth + 1);
                            off += (tp & 0x80000000u) != 0 ? 8 : SizeOf((int)(tp & 0x3FFFFFFF));
                        }
                        return obj;
                    }
                case 1: return _b[addr] != 0;
                case 2: return (int)(sbyte)_b[addr];
                case 3: return (int)(short)((_b[addr] << 8) | _b[addr + 1]);
                case 4: return I(addr);
                case 6: return (int)_b[addr];
                case 7: return (_b[addr] << 8) | _b[addr + 1];
                case 8: return (long)(uint)I(addr);
                case 10: return BitConverter.ToSingle(BitConverter.GetBytes(I(addr)), 0);
                case 12: { int so = I(addr); return so == -1 ? null : CStr(_data + so); }
                case 13:
                case 14: return Val(I(addr), _data + I(addr + 4), depth + 1);
                default: return null;
            }
        }
    }
}