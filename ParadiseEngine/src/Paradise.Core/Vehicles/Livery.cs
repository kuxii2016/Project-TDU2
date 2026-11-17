using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Render;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Paradise.Core.Vehicles;

// Port Unity TDU2Livery.cs: eigene Lackierung/Beklebung der Autos (Sticker-Shop), Speicher, Sticker-Planchen und Anzeige am Auto.

/// <summary>Lack-Finish (Sticker-Shop: /:ColorType, im Spiel GLOSSY / METAL / MATT).</summary>
public enum PaintFinish : byte { Original = 0, Gloss = 1, Metallic = 2, Matt = 3 }

/// <summary>Ein aufgeklebter Sticker in der Karosserie-Abwicklung (UV-Satz 1): Mitte (U,V) und Halbachsen A (rechts) / B (oben).</summary>
public sealed class StickerPlacement
{
    public byte Board;          // DBStickersBanks._hashcode
    public byte Index;          // 0..23 (MAT_01..MAT_24 der Planche)
    public float U, V;          // Mitte (UV1)
    public Vector2 A, B;        // Halbachsen in UV (A = Bildbreite/2 nach rechts, B = Bildhöhe/2 nach oben)
    public Rgba32 Tint = new(255, 255, 255, 255);   // Farbe (einfarbige Planchen) + Alpha
    public byte Flags;          // 1 = gespiegelt (nur Info, steckt schon in A)

    public StickerPlacement Clone() => (StickerPlacement)MemberwiseClone();
}

public sealed class CarLivery
{
    /// <summary>Höchstzahl Sticker je Auto (Unity Gameconfig.MaxStickers; gesetzt von Paradise.Game/Config).</summary>
    public static int MaxStickers = 120;
    public bool HasPaint;
    public Rgba32 Paint = new(200, 200, 200, 255);
    public Rgba32 Paint2 = new(30, 30, 30, 255);
    public bool Bicolour;
    public PaintFinish Finish;
    /// <summary>Werks-Beklebung (DBCarPhysicsData._sticker) entfernt ("Effacer").</summary>
    public bool ClearFactory;
    public readonly List<StickerPlacement> Stickers = new();

    public bool IsEmpty => !HasPaint && Finish == PaintFinish.Original && !ClearFactory && Stickers.Count == 0;

    public CarLivery Clone()
    {
        var c = new CarLivery { HasPaint = HasPaint, Paint = Paint, Paint2 = Paint2, Bicolour = Bicolour, Finish = Finish, ClearFactory = ClearFactory };
        foreach (var s in Stickers) c.Stickers.Add(s.Clone());
        return c;
    }

    // ---------------------------------------------------------------- Binärformat (Version 1, identisch mit Unity)
    // [0] Version, [1] Flags (1 Lack, 2 Bicolor, 4 Werksbeklebung entfernt), [2] Finish, [3..5] Lack RGB, [6..8] Lack 2 RGB,
    // [9] Anzahl, je Sticker 19 Byte: Planche, Nummer, U u16, V u16, Ax/Ay/Bx/By i16 (×2/32767), R, G, B, A, Flags.
    const int Head = 10, Per = 19;
    const float AxisScale = 2f / 32767f;

    public byte[] Encode()
    {
        int n = System.Math.Min(Stickers.Count, System.Math.Min(MaxStickers, 255));
        var b = new byte[Head + n * Per];
        b[0] = 1;
        b[1] = (byte)((HasPaint ? 1 : 0) | (Bicolour ? 2 : 0) | (ClearFactory ? 4 : 0));
        b[2] = (byte)Finish;
        b[3] = Paint.R; b[4] = Paint.G; b[5] = Paint.B;
        b[6] = Paint2.R; b[7] = Paint2.G; b[8] = Paint2.B;
        b[9] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var s = Stickers[i]; int o = Head + i * Per;
            b[o] = s.Board; b[o + 1] = s.Index;
            U16(b, o + 2, (int)MathF.Round(System.Math.Clamp(s.U, 0f, 1f) * 65535f));
            U16(b, o + 4, (int)MathF.Round(System.Math.Clamp(s.V, 0f, 1f) * 65535f));
            I16(b, o + 6, s.A.X); I16(b, o + 8, s.A.Y); I16(b, o + 10, s.B.X); I16(b, o + 12, s.B.Y);
            b[o + 14] = s.Tint.R; b[o + 15] = s.Tint.G; b[o + 16] = s.Tint.B; b[o + 17] = s.Tint.A; b[o + 18] = s.Flags;
        }
        return b;
    }

    public static CarLivery? Decode(byte[]? b)
    {
        if (b == null || b.Length < Head || b[0] != 1) return null;
        var l = new CarLivery
        {
            HasPaint = (b[1] & 1) != 0,
            Bicolour = (b[1] & 2) != 0,
            ClearFactory = (b[1] & 4) != 0,
            Finish = b[2] <= 3 ? (PaintFinish)b[2] : PaintFinish.Original,
            Paint = new Rgba32(b[3], b[4], b[5], 255),
            Paint2 = new Rgba32(b[6], b[7], b[8], 255),
        };
        int n = System.Math.Min((int)b[9], MaxStickers);
        for (int i = 0; i < n; i++)
        {
            int o = Head + i * Per;
            if (o + Per > b.Length) break;
            l.Stickers.Add(new StickerPlacement
            {
                Board = b[o],
                Index = b[o + 1],
                U = (b[o + 2] | (b[o + 3] << 8)) / 65535f,
                V = (b[o + 4] | (b[o + 5] << 8)) / 65535f,
                A = new Vector2(RI16(b, o + 6), RI16(b, o + 8)),
                B = new Vector2(RI16(b, o + 10), RI16(b, o + 12)),
                Tint = new Rgba32(b[o + 14], b[o + 15], b[o + 16], b[o + 17]),
                Flags = b[o + 18],
            });
        }
        return l;
    }

    static void U16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void I16(byte[] b, int o, float f) { int v = System.Math.Clamp((int)MathF.Round(f / AxisScale), -32767, 32767); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static float RI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8)) * AxisScale;

    /// <summary>FNV-1a über die Daten (0 = keine Beklebung).</summary>
    public static uint Hash(byte[]? d)
    {
        if (d == null || d.Length == 0) return 0;
        uint h = 2166136261;
        foreach (var x in d) { h ^= x; h *= 16777619; }
        return h == 0 ? 1u : h;
    }

    public string ToBase64() => Convert.ToBase64String(Encode());
    public static CarLivery? FromBase64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Decode(Convert.FromBase64String(s)); } catch (Exception) { return null; }
    }
}

// =====================================================================================================================
/// <summary>
/// Speicher der eigenen Lackierungen (je Auto-Datei), gekaufte Planchen und "Meine Farben" (Unity TDU2Liveries).
/// Engine (Offline-Modus): maßgeblich ist die lokale Datei je Profil (Unity PlayerPrefs "TDU2.Liveries.&lt;Profil&gt;", hier
/// Documents\KuxiiSoft\Test Drive Unlimited 2\Config). Der Spielstand (SaveData.liveries, stickerBoards, favoriteColors) wird nur
/// mitgeschrieben und ergänzt Fehlendes; ans Netz geht nichts (Online-Abgleich folgt später).
/// </summary>
public static class Liveries
{
    static readonly Dictionary<string, CarLivery> _map = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, byte[]?> _blob = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<int> _boards = new();
    static readonly int[] _fav = { -1, -1, -1, -1, -1, -1 };
    static string? _loadedFor;
    static bool _hooked;
    /// <summary>Lackierung eines Autos geändert (Auto-Datei).</summary>
    public static event Action<string>? Changed;

    sealed class Prefs { public List<LiveryEntry> liveries { get; set; } = new(); public List<int> boards { get; set; } = new(); public List<int> fav { get; set; } = new(); }

    // LiveryEntry hat öffentliche Felder (car, data) wie in Unity (JsonUtility)
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    static string Profile => TDU2Session.LoggedIn && !string.IsNullOrEmpty(TDU2Session.Nick) ? TDU2Session.Nick : "local";
    static string PrefsPath
    {
        get
        {
            string key = "TDU2.Liveries." + Profile;
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return Path.Combine(UserPaths.Config, key + ".json");
        }
    }

    static void Ensure()
    {
        if (!_hooked) { _hooked = true; TDU2Session.Changed += () => _loadedFor = null; }
        string who = Profile + (TDU2Session.Save != null ? "#s" : "");
        if (_loadedFor == who) return;
        _loadedFor = who;
        _map.Clear(); _blob.Clear(); _boards.Clear();
        for (int i = 0; i < _fav.Length; i++) _fav[i] = -1;
        // 1) lokale Kopie
        try
        {
            var path = PrefsPath;
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path), Json);
                if (p != null)
                {
                    foreach (var e in p.liveries) { var l = CarLivery.FromBase64(e.data); if (l != null && !string.IsNullOrEmpty(e.car)) _map[e.car] = l; }
                    foreach (var b in p.boards) _boards.Add(b);
                    for (int i = 0; i < _fav.Length && i < p.fav.Count; i++) _fav[i] = p.fav[i];
                }
            }
        }
        catch (Exception ex) { Log.Warn("Lackierungen (lokal): " + ex.Message); }
        // 2) Spielstand ergänzt nur, was lokal fehlt (lokal ist maßgeblich)
        var sv = TDU2Session.Save;
        if (sv != null)
        {
            if (sv.liveries != null) foreach (var e in sv.liveries) { var l = CarLivery.FromBase64(e.data); if (l != null && !string.IsNullOrEmpty(e.car) && !_map.ContainsKey(e.car)) _map[e.car] = l; }
            if (sv.stickerBoards != null) foreach (var b in sv.stickerBoards) _boards.Add(b);
            if (sv.favoriteColors != null) for (int i = 0; i < _fav.Length && i < sv.favoriteColors.Count; i++) if (_fav[i] < 0) _fav[i] = sv.favoriteColors[i];
            WriteSave(sv);
        }
    }

    static void WriteSave(SaveData sv)
    {
        sv.liveries = new List<LiveryEntry>();
        foreach (var kv in _map) if (!kv.Value.IsEmpty) sv.liveries.Add(new LiveryEntry { car = kv.Key, data = kv.Value.ToBase64() });
        sv.stickerBoards = new List<int>(_boards);
        sv.favoriteColors = new List<int>(_fav);
    }

    static void Persist()
    {
        var p = new Prefs();
        foreach (var kv in _map) if (!kv.Value.IsEmpty) p.liveries.Add(new LiveryEntry { car = kv.Key, data = kv.Value.ToBase64() });
        p.boards.AddRange(_boards); p.fav.AddRange(_fav);
        try { Directory.CreateDirectory(UserPaths.Config); File.WriteAllText(PrefsPath, JsonSerializer.Serialize(p, Json)); }
        catch (Exception ex) { Log.Warn("Lackierungen speichern (lokal): " + ex.Message); }
        if (TDU2Session.Save != null) WriteSave(TDU2Session.Save);
    }

    /// <summary>Lackierung eines Autos (nicht verändern!), null = Werkszustand.</summary>
    public static CarLivery? Get(string? carFile)
    {
        if (string.IsNullOrEmpty(carFile)) return null;
        Ensure();
        return _map.TryGetValue(carFile, out var l) ? l : null;
    }

    public static void Set(string? carFile, CarLivery? l)
    {
        if (string.IsNullOrEmpty(carFile)) return;
        Ensure();
        if (l == null || l.IsEmpty) _map.Remove(carFile); else _map[carFile] = l.Clone();
        _blob.Remove(carFile);
        Persist();
        try { Changed?.Invoke(carFile); } catch (Exception ex) { Log.Warn("Lackierung geändert: " + ex.Message); }
    }

    /// <summary>Codierte Lackierung (für die Online-Synchronisation), null = keine.</summary>
    public static byte[]? Blob(string? carFile)
    {
        if (string.IsNullOrEmpty(carFile)) return null;
        Ensure();
        if (_blob.TryGetValue(carFile, out var b)) return b;
        b = _map.TryGetValue(carFile, out var l) && !l.IsEmpty ? l.Encode() : null;
        _blob[carFile] = b;
        return b;
    }

    public static bool HasBoard(int id) { Ensure(); return _boards.Contains(id); }
    public static void AddBoards(IEnumerable<int> ids) { Ensure(); foreach (var i in ids) _boards.Add(i); Persist(); }

    public static int Favorite(int slot) { Ensure(); return slot >= 0 && slot < _fav.Length ? _fav[slot] : -1; }
    public static void SetFavorite(int slot, int rgb) { Ensure(); if (slot < 0 || slot >= _fav.Length) return; _fav[slot] = rgb; Persist(); }
}

// =====================================================================================================================
/// <summary>
/// Sticker-Planchen (Unity TDU2StickerLibrary): DBStickersBanks, scenarics/packs/stickers/&lt;_bankfile&gt;.bnk, je Bank 24 Sticker
/// (Material MAT_01..MAT_24 der .2dm bestimmt die Reihenfolge), einfarbige Planchen (_monochrome) sind weiß + Alpha und werden
/// eingefärbt. Preise: DBStickersPricing (Labour/Paint/Erase). Namen: _name_hc (Textdatenbank).
/// Hier zusätzlich: Sticker als RGBA8 im Speicher (die Stickermap wird auf der CPU zusammengesetzt) und als GPU-Textur (Flash-Vorschau).
/// </summary>
public static class StickerLibrary
{
    public sealed class Sticker
    {
        public int Width, Height;
        public byte[] Rgba = Array.Empty<byte>();   // Zeile 0 = oben (.2db)
        public IGpuTexture? Gpu;
    }

    public sealed class Board
    {
        public int Id;                  // _hashcode
        public string BankFile = "";    // z.B. st_basic_nb
        public int Type;                // 1 Shop, 3 DLC, 4 Casino, 5 Verfolgung
        public bool Mono;
        public int Price;
        public ulong NameHc;
        public string Path = "";
        public Sticker[]? Stickers;
        public bool Loading, Loaded, Failed;
        internal Task<List<TextureDef>>? Task;
        public int Count => Stickers?.Length ?? 0;
    }

    public static readonly List<Board> Boards = new();
    public static int LabourPrice = 1000, PaintPrice = 1000, ErasePrice = 1000;
    /// <summary>Zählt jede fertig geladene Planche (Ansichten setzen dann neu zusammen).</summary>
    public static int Version { get; private set; }
    static string? _bnkDir;
    static IRenderer? _r;
    public static bool Ready { get; private set; }

    /// <summary>Planchen aus der Datenbank (Unity Ensure + ReadDb; hier mit der bereits gelesenen Datenbank).</summary>
    public static void Init(string bnkDir, XmbNode? root, IRenderer? r)
    {
        if (r != null) _r = r;
        if (string.IsNullOrEmpty(bnkDir) || _bnkDir == bnkDir && Ready) return;
        _bnkDir = bnkDir;
        var list = new List<Board>();   // im Hintergrund aufbauen (VehicleManager.EnsureDatabase), dann tauschen
        string dir = System.IO.Path.Combine(bnkDir, "scenarics", "packs", "stickers");
        var banks = root?.Get("DBStickersBanks");
        if (banks != null)
            foreach (var row in banks.Items)
            {
                var b = new Board
                {
                    Id = row.I("_hashcode"),
                    BankFile = row.S("_bankfile", "") ?? "",
                    Type = row.I("_type"),
                    Mono = row.B("_monochrome"),
                    Price = row.I("_price"),
                    NameHc = row.U("_name_hc"),
                };
                if (!row.B("_isvalid", true) || b.Id <= 0 || b.Id > 255 || string.IsNullOrEmpty(b.BankFile)) continue;
                var path = CarDatabase.FindFile(dir, b.BankFile + ".bnk");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log.Warn("Sticker-Planche fehlt: " + b.BankFile + ".bnk"); continue; }
                b.Path = path;
                list.Add(b);
            }
        var pr = root?.Get("DBStickersPricing");
        if (pr != null)
            foreach (var row in pr.Items)
            {
                string n = (row.S("_actionname", "") ?? "").ToLowerInvariant();
                if (n == "labour") LabourPrice = row.I("_price", LabourPrice);
                else if (n == "paint") PaintPrice = row.I("_price", PaintPrice);
                else if (n == "erase") ErasePrice = row.I("_price", ErasePrice);
            }
        lock (Boards) { Boards.Clear(); Boards.AddRange(list); }
        Ready = true;
        Log.Info("Sticker-Planchen: " + list.Count + " (Arbeit $" + LabourPrice + ", Lack $" + PaintPrice + ", Entfernen $" + ErasePrice + ")");
    }

    /// <summary>Je Bild: fertig geladene Planchen übernehmen, wartende Ansichten neu zusammensetzen (Unity Pump.Update + LateUpdate).</summary>
    public static void Update()
    {
        Board[] all;
        lock (Boards) all = Boards.ToArray();
        foreach (var b in all)
        {
            if (b.Task == null || !b.Task.IsCompleted) continue;
            var t = b.Task; b.Task = null; b.Loading = false;
            if (t.IsFaulted || t.Result == null || t.Result.Count == 0)
            {
                b.Failed = true;
                Log.Warn("Sticker-Planche " + b.BankFile + ": " + (t.Exception?.GetBaseException().Message ?? "keine Texturen"));
                continue;
            }
            var list = new Sticker[t.Result.Count];
            for (int i = 0; i < list.Length; i++)
            {
                var def = t.Result[i];
                var s = new Sticker { Width = def.Width, Height = def.Height, Rgba = def.DecodeRgba() ?? Array.Empty<byte>() };
                var up = def.ToUpload(TextureWrap.Clamp, TextureWrap.Clamp);
                if (up != null && _r != null) { up.Name = b.BankFile + "_" + (i + 1).ToString("00"); s.Gpu = _r.CreateTexture(up); }
                list[i] = s;
            }
            b.Stickers = list; b.Loaded = true;
            Version++;
            Log.Info("Sticker-Planche " + b.BankFile + ": " + list.Length + " Sticker");
        }
        LiveryView.UpdateAll();
    }

    public static Board? ById(int id)
    {
        lock (Boards) foreach (var b in Boards) if (b.Id == id) return b;
        return null;
    }

    /// <summary>Planche laden (Hintergrund). true, wenn schon geladen.</summary>
    public static bool Request(Board? b)
    {
        if (b == null) return false;
        if (b.Loaded) return true;
        if (b.Loading || b.Failed) return false;
        b.Loading = true;
        string path = b.Path;
        b.Task = System.Threading.Tasks.Task.Run(() => LoadBank(path));
        return false;
    }

    public static Sticker? Get(int boardId, int index)
    {
        var b = ById(boardId);
        if (b == null || !Request(b) || b.Stickers == null || index < 0 || index >= b.Stickers.Length) return null;
        var s = b.Stickers[index];
        return s.Rgba.Length > 0 ? s : null;
    }

    public static bool IsMono(int boardId) => ById(boardId)?.Mono == true;

    /// <summary>Texturen der Bank in Sticker-Reihenfolge (MAT_01..MAT_24 der .2dm; sonst nach Dateinamen).</summary>
    static List<TextureDef> LoadBank(string path)
    {
        var bank = BnkArchive.Open(path);
        var byKey = new Dictionary<ulong, TextureDef>();
        var byName = new List<TextureDef>();
        foreach (var e in bank.All(".2db"))
        {
            TextureDef? t = null;
            try { t = TextureDef.Parse(bank.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name)); } catch (Exception) { }
            if (t == null) continue;
            byKey[t.Key] = t; byName.Add(t);
        }
        var result = new List<TextureDef>();
        BnkEntry? m = null;
        foreach (var e in bank.All(".2dm")) { m = e; break; }
        if (m != null)
        {
            Material2DM? lib = null;
            try { lib = Material2DM.Parse(bank.Read(m)); } catch (Exception) { }
            var mats = new SortedDictionary<int, ulong>();
            if (lib != null)
                foreach (var def in lib.Materials.Values)
                {
                    var n = def.Name ?? "";
                    if (!n.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase) || !int.TryParse(n.Substring(4), out int num)) continue;
                    foreach (var l in def.Layers) if (l.TextureKey != 0 && byKey.ContainsKey(l.TextureKey)) { mats[num] = l.TextureKey; break; }
                }
            foreach (var kv in mats) result.Add(byKey[kv.Value]);
        }
        if (result.Count == 0)
        {
            byName.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            result.AddRange(byName);
        }
        return result;
    }
}

// =====================================================================================================================
/// <summary>
/// Zeigt eine <see cref="CarLivery"/> an einem gebauten Auto (Unity TDU2LiveryView): Lackfarbe(n) + Finish auf den Lack-Materialien
/// (CarModel.PaintMaterials: Paint/Paint_LR = Farbe 1, Paint2 = Farbe 2 bei zweifarbiger Lackierung; Color, Color2, Bicolour,
/// Metallic, Glossiness, ClearCoat, Flake) und eine Stickermap 1024² (UV-Satz 1):
///   ohne Werks-Beklebung: transparente Map, Sticker als Dekor über dem Lack (StickerAlphaOnly = 1)
///   mit Werks-Beklebung: Map = Werks-Beklebung + Sticker, ersetzt den Lack wie im Original (StickerAlphaOnly = 0).
/// Unity rendert die Map mit GL in eine RenderTexture (Shader Hidden/TDU2/Sticker, sRGB-Ziel = Mischen im linearen Raum);
/// hier wird sie gleichwertig auf der CPU zusammengesetzt (linear, vormultipliziert, bilinear) und als sRGB-Textur mit Mips hochgeladen.
/// </summary>
public sealed class LiveryView
{
    public CarModel Model { get; }
    public CarLivery? Livery { get; private set; }
    /// <summary>Vorschau (Sticker unter der Maus im Shop), wird nicht gespeichert.</summary>
    public readonly List<StickerPlacement> Preview = new();
    public static int MapSize = 1024;

    struct Orig { public CarMaterial M; public bool Second; public Vector4 Color, Color2; public float Bicolour, Metal, Gloss, Coat, Flake, UseSticker, AlphaOnly, TdPaint, TdFlake; public IGpuTexture? Sticker; }
    readonly List<Orig> _orig = new();
    readonly IRenderer _r;
    readonly TextureDef? _factoryDef;
    byte[]? _factory;   // Werks-Beklebung RGBA (Mip 0)
    int _fw, _fh;
    IGpuTexture? _map;
    bool _dirty, _waiting;
    int _libVersion = -1;

    static readonly ConditionalWeakTable<CarModel, LiveryView> _views = new();
    static readonly List<WeakReference<LiveryView>> _all = new();

    /// <summary>Ansicht am Modell (einmal je Modell, sonst die vorhandene). Werks-Beklebung aus den Auto-Daten (DBCarPhysicsData._sticker).</summary>
    public static LiveryView? For(CarModel? model, CarAssets? assets, IRenderer? r)
    {
        if (model == null) return null;
        if (_views.TryGetValue(model, out var v)) return v;
        r ??= assets?.Renderer;
        if (r == null) return null;
        v = new LiveryView(model, assets?.Sticker, r);
        _views.Add(model, v);
        lock (_all) _all.Add(new WeakReference<LiveryView>(v));
        return v;
    }

    /// <summary>Gespeicherte Lackierung des Autos anzeigen (eigenes Auto, Garage, Shop).</summary>
    public static LiveryView? ApplyOwn(CarModel? model, CarAssets? assets, string? carFile)
    {
        var l = Liveries.Get(carFile);
        if (l == null)
        {
            if (model != null && _views.TryGetValue(model, out var ex)) { ex.Show(null); return ex; }
            return null;
        }
        var v = For(model, assets, null);
        v?.Show(l);
        return v;
    }

    LiveryView(CarModel model, TextureDef? factory, IRenderer r)
    {
        Model = model; _r = r;
        foreach (var m in model.PaintMaterials)
        {
            if (m == null) continue;
            var d = m.Desc;
            _orig.Add(new Orig
            {
                M = m,
                Second = CarModel.IsPaint2(m),
                Color = d.Color,
                Color2 = d.Color2,
                Bicolour = d.Bicolour,
                Metal = d.Metallic,
                Gloss = d.Glossiness,
                Coat = d.ClearCoat,
                Flake = d.Flake,
                UseSticker = d.UseSticker,
                AlphaOnly = d.StickerAlphaOnly,
                TdPaint = d.TdPaint,
                TdFlake = d.TdFlake,
                Sticker = d.StickerTex,
            });
        }
        bool usesFactory = false;
        foreach (var o in _orig) if (o.UseSticker > 0.5f && o.Sticker != null) usesFactory = true;
        _factoryDef = usesFactory ? factory : null;
    }

    /// <summary>Werks-Beklebung vorhanden (DBCarPhysicsData._sticker).</summary>
    public bool HasFactory => _factoryDef != null;
    /// <summary>Grundfarbe des Lacks (für den Farbwähler, sRGB).</summary>
    public Vector4 OriginalPaint { get { foreach (var o in _orig) if (!o.Second) return o.Color; return _orig.Count > 0 ? _orig[0].Color : new Vector4(0.5f, 0.5f, 0.5f, 1f); } }
    /// <summary>Werks-Farbe 2 (Material Paint2), sonst Farbe 1.</summary>
    public Vector4 OriginalPaint2 { get { foreach (var o in _orig) if (o.Second) return o.Color; return OriginalPaint; } }
    /// <summary>Auto hat eine zweite Lackfläche (Paint2).</summary>
    public bool HasSecondPaint { get { foreach (var o in _orig) if (o.Second) return true; return false; } }

    public void Show(CarLivery? l) { Livery = l; _dirty = true; Update(); }
    public void Refresh() { _dirty = true; Update(); }

    /// <summary>Wartende Ansichten nach dem Laden einer Planche neu zusammensetzen (aus StickerLibrary.Update).</summary>
    internal static void UpdateAll()
    {
        lock (_all)
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                if (!_all[i].TryGetTarget(out var v)) { _all.RemoveAt(i); continue; }
                v.Update();
            }
    }

    /// <summary>Unity LateUpdate: nur bei Änderung (oder fertig geladener Planche) neu aufbauen.</summary>
    public void Update()
    {
        if (_waiting && StickerLibrary.Version != _libVersion) _dirty = true;
        if (!_dirty) return;
        _dirty = false;
        Render();
    }

    void Render()
    {
        var l = Livery;
        // ---- Lack + Finish
        foreach (var o in _orig)
        {
            var d = o.M.Desc;
            // eigene Lackierung: eine Farbe auf allen Lackflächen; zweifarbig = Bicolor-Lack (Farbwechsel je nach Blickwinkel)
            Vector4 c = o.Color;
            if (l != null && l.HasPaint) c = new Vector4(l.Paint.R / 255f, l.Paint.G / 255f, l.Paint.B / 255f, o.Color.W);
            d.Color = c;
            bool bi = l != null && l.HasPaint && l.Bicolour;
            d.Color2 = bi ? new Vector4(l!.Paint2.R / 255f, l.Paint2.G / 255f, l.Paint2.B / 255f, 1f) : l != null && l.HasPaint ? c : o.Color2;
            d.Bicolour = bi ? 1f : l != null && l.HasPaint ? 0f : o.Bicolour;
            float metal = o.Metal, gloss = o.Gloss, coat = o.Coat, flake = o.Flake, tdPaint = o.TdPaint, tdFlake = o.TdFlake;
            var finish = l != null ? l.Finish : PaintFinish.Original;
            switch (finish)
            {
                case PaintFinish.Gloss: metal = 0.05f; gloss = 0.93f; coat = 1f; flake = 0f; break;        // normaler Uni-Lack
                case PaintFinish.Metallic: metal = 0.35f; gloss = 0.86f; coat = 1f; flake = 0.7f; break;     // Metallic mit Flakes
                case PaintFinish.Matt: metal = 0.02f; gloss = 0.18f; coat = 0f; flake = 0f; break;         // matt
            }
            // Eigenes Finish: die Werte gehören zur Unity-Lackformel (Standard + Klarlack), nicht zur TD_CarPaint-Formel des Originals
            if (finish != PaintFinish.Original) { tdPaint = 0f; tdFlake = 0f; }
            d.Metallic = metal; d.Glossiness = gloss; d.ClearCoat = coat; d.Flake = flake; d.TdPaint = tdPaint; d.TdFlake = tdFlake;
        }
        // ---- Stickermap
        bool factory = _factoryDef != null && (l == null || !l.ClearFactory);
        int count = (l != null ? l.Stickers.Count : 0) + Preview.Count;
        _waiting = false;
        if (count == 0)
        {
            foreach (var o in _orig)
            {
                var d = o.M.Desc;
                if (factory) { d.StickerTex = o.Sticker; d.UseSticker = o.UseSticker; d.StickerAlphaOnly = o.AlphaOnly; }
                else d.UseSticker = 0f;
                o.M.Apply(_r);
            }
            return;
        }
        if (factory && _factory == null && _factoryDef != null)
        {
            _factory = _factoryDef.DecodeRgba();
            _fw = _factoryDef.Width; _fh = _factoryDef.Height;
        }
        int S = MapSize;
        var acc = new float[S * S * 4];   // linear, vormultipliziert
        if (factory && _factory != null)
        {
            // Werks-Beklebung (enthält die Grundfarbe) deckend als Grund (Unity Pass 1), Sticker darüber -> ersetzt den Lack
            Parallel.For(0, S, y =>
            {
                float v = (y + 0.5f) / S;
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S;
                    SampleLinear(_factory, _fw, _fh, u * _fw - 0.5f, v * _fh - 0.5f, out float r, out float g, out float b, out float a);
                    int o = (y * S + x) * 4;
                    acc[o] = r * a; acc[o + 1] = g * a; acc[o + 2] = b * a; acc[o + 3] = a;
                }
            });
        }
        if (l != null) foreach (var s in l.Stickers) Stamp(acc, s);
        foreach (var s in Preview) Stamp(acc, s);
        // Ergebnis: ohne Werks-Beklebung RGB / Alpha (Unity Pass 2), dann sRGB + Mips
        var px = new byte[S * S * 4];
        Parallel.For(0, S, y =>
        {
            for (int x = 0; x < S; x++)
            {
                int o = (y * S + x) * 4;
                float a = acc[o + 3];
                float k = a > 1e-5f ? 1f / a : 0f;
                px[o] = ToSrgb(acc[o] * k); px[o + 1] = ToSrgb(acc[o + 1] * k); px[o + 2] = ToSrgb(acc[o + 2] * k);
                px[o + 3] = (byte)System.Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            }
        });
        var chain = TextureDef.BuildRgbaMips(px, S, S, out int mips);
        var old = _map;
        _map = _r.CreateTexture(new TextureUpload
        {
            Width = S,
            Height = S,
            Mips = mips,
            Format = GpuTextureFormat.Rgba8,
            Blocks = chain,
            WrapU = TextureWrap.Clamp,
            WrapV = TextureWrap.Clamp,
            Name = "TDU2 Stickermap",
        });
        foreach (var o in _orig)
        {
            var d = o.M.Desc;
            d.StickerTex = _map;
            d.UseSticker = 1f;
            d.StickerAlphaOnly = factory ? 0f : 1f;
            o.M.Apply(_r);
        }
        old?.Dispose();
        _libVersion = StickerLibrary.Version;
    }

    /// <summary>Sticker über die Map legen (Unity Pass 0: vormultipliziert "über", Farbe × Tönung).</summary>
    void Stamp(float[] acc, StickerPlacement s)
    {
        var st = StickerLibrary.Get(s.Board, s.Index);
        if (st == null) { _waiting = true; return; }
        float tr = 1f, tg = 1f, tb = 1f, ta = s.Tint.A / 255f;
        if (StickerLibrary.IsMono(s.Board)) { tr = SrgbToLinear(s.Tint.R / 255f); tg = SrgbToLinear(s.Tint.G / 255f); tb = SrgbToLinear(s.Tint.B / 255f); }
        int S = MapSize;
        // Viereck: Mitte c, Halbachsen a (rechts) / b (oben) in UV; Punkt p = c + x·a + y·b mit x,y ∈ [-1,1]
        var c = new Vector2(s.U, s.V); var a = s.A; var b = s.B;
        float det = a.X * b.Y - a.Y * b.X;
        if (MathF.Abs(det) < 1e-9f) return;
        float ext = (MathF.Abs(a.X) + MathF.Abs(b.X)), eyt = (MathF.Abs(a.Y) + MathF.Abs(b.Y));
        int x0 = System.Math.Max(0, (int)MathF.Floor((c.X - ext) * S)), x1 = System.Math.Min(S - 1, (int)MathF.Ceiling((c.X + ext) * S));
        int y0 = System.Math.Max(0, (int)MathF.Floor((c.Y - eyt) * S)), y1 = System.Math.Min(S - 1, (int)MathF.Ceiling((c.Y + eyt) * S));
        if (x0 > x1 || y0 > y1) return;
        float inv = 1f / det;
        int w = st.Width, h = st.Height; var src = st.Rgba;
        // Pixelgröße des Stickers auf der Map -> passende Glättung (Mip-ähnlich): Schritt je Map-Pixel in Sticker-Pixeln
        float stepX = w * 0.5f * MathF.Sqrt(b.Y * b.Y + b.X * b.X) * MathF.Abs(inv) / S;
        float stepY = h * 0.5f * MathF.Sqrt(a.Y * a.Y + a.X * a.X) * MathF.Abs(inv) / S;
        int ss = System.Math.Clamp((int)MathF.Ceiling(MathF.Max(stepX, stepY)), 1, 4);
        Parallel.For(y0, y1 + 1, py =>
        {
            for (int px = x0; px <= x1; px++)
            {
                float sr = 0f, sg = 0f, sb = 0f, sa = 0f;
                for (int jy = 0; jy < ss; jy++)
                    for (int jx = 0; jx < ss; jx++)
                    {
                        float du = (px + (jx + 0.5f) / ss) / S - c.X, dv = (py + (jy + 0.5f) / ss) / S - c.Y;
                        float x = (du * b.Y - dv * b.X) * inv, y = (a.X * dv - a.Y * du) * inv;
                        if (x < -1f || x > 1f || y < -1f || y > 1f) continue;
                        // .2db: Zeile 0 = oben (y = +1)
                        float tu = (x + 1f) * 0.5f * w - 0.5f, tv = (1f - y) * 0.5f * h - 0.5f;
                        SampleLinear(src, w, h, tu, tv, out float r, out float g, out float bb, out float al);
                        sr += r * al; sg += g * al; sb += bb * al; sa += al;
                    }
                if (sa <= 0f) continue;
                float n = 1f / (ss * ss);
                float pa = sa * n * ta;
                float pr = sr * n * ta * tr, pg = sg * n * ta * tg, pb = sb * n * ta * tb;   // vormultipliziert
                int o = (py * S + px) * 4;
                float k = 1f - pa;
                acc[o] = pr + acc[o] * k; acc[o + 1] = pg + acc[o + 1] * k; acc[o + 2] = pb + acc[o + 2] * k; acc[o + 3] = pa + acc[o + 3] * k;
            }
        });
    }

    /// <summary>Bilinear (Clamp), RGB linear, Alpha linear.</summary>
    static void SampleLinear(byte[] px, int w, int h, float fx, float fy, out float r, out float g, out float b, out float a)
    {
        fx = System.Math.Clamp(fx, 0f, w - 1); fy = System.Math.Clamp(fy, 0f, h - 1);
        int ix = (int)fx, iy = (int)fy;
        int ix1 = System.Math.Min(ix + 1, w - 1), iy1 = System.Math.Min(iy + 1, h - 1);
        float tx = fx - ix, ty = fy - iy;
        int o00 = (iy * w + ix) * 4, o10 = (iy * w + ix1) * 4, o01 = (iy1 * w + ix) * 4, o11 = (iy1 * w + ix1) * 4;
        float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        r = Lin[px[o00]] * w00 + Lin[px[o10]] * w10 + Lin[px[o01]] * w01 + Lin[px[o11]] * w11;
        g = Lin[px[o00 + 1]] * w00 + Lin[px[o10 + 1]] * w10 + Lin[px[o01 + 1]] * w01 + Lin[px[o11 + 1]] * w11;
        b = Lin[px[o00 + 2]] * w00 + Lin[px[o10 + 2]] * w10 + Lin[px[o01 + 2]] * w01 + Lin[px[o11 + 2]] * w11;
        a = (px[o00 + 3] * w00 + px[o10 + 3] * w10 + px[o01 + 3] * w01 + px[o11 + 3] * w11) / 255f;
    }

    static readonly float[] Lin = BuildLin();
    static float[] BuildLin() { var t = new float[256]; for (int i = 0; i < 256; i++) t[i] = SrgbToLinear(i / 255f); return t; }
    static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    static byte ToSrgb(float l)
    {
        l = System.Math.Clamp(l, 0f, 1f);
        float s = l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
        return (byte)System.Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
    }

    /// <summary>Ansicht des Modells freigeben (vor CarModel.Destroy), falls vorhanden.</summary>
    public static void Release(CarModel? model)
    {
        if (model != null && _views.TryGetValue(model, out var v)) v.Dispose();
    }

    /// <summary>Ansicht entfernen (Auto wird abgebaut): Lack-Materialien zurück, Stickermap freigeben.</summary>
    public void Dispose()
    {
        foreach (var o in _orig)
        {
            var d = o.M.Desc;
            d.Color = o.Color; d.Color2 = o.Color2; d.Bicolour = o.Bicolour;
            d.Metallic = o.Metal; d.Glossiness = o.Gloss; d.ClearCoat = o.Coat; d.Flake = o.Flake; d.TdPaint = o.TdPaint; d.TdFlake = o.TdFlake;
            d.StickerTex = o.Sticker; d.UseSticker = o.UseSticker; d.StickerAlphaOnly = o.AlphaOnly;
        }
        _map?.Dispose(); _map = null;
        _views.Remove(Model);
    }
}
