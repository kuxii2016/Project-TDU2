#nullable disable
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Render;
using System.Globalization;

namespace Paradise.Core.Flash;

/// <summary>
/// Manages the loaded Flash movies (VmfPlayer)
/// <see cref="Update"/> and <see cref="Render"/>. The game loads and removes movies only through this class.
/// </summary>
public static class FlashHost
{
    static readonly List<VmfPlayer> _players = new();
    static readonly List<VmfPlayer> _sorted = new();

    /// <summary>Renderer for textures/quads (set by GameLoop).</summary>
    public static IRenderer Renderer { get; private set; }

    /// <summary>Ordner Euro/Bnk (Frontend-Banken, Datenbank für Texte).</summary>
    public static string BnkDir { get => _bnkDir ?? Config.GameData.BnkDir; set => _bnkDir = value; }
    static string _bnkDir;

    /// <summary>Language codes for the original data (us, ge, fr, it, sp …).</summary>
    /// <summary>Applies before logging in and as long as no language is specified in the game settings: English.
    /// After that, the language is determined by the game save (Options > “WhatLanguage”); see Screens/Options.SavedLanguage.</summary>
    public static string Language { get; set; } = DefaultLanguage;

    /// <summary>Default language (not the system language): English.</summary>
    public const string DefaultLanguage = "us";

    public static IReadOnlyList<VmfPlayer> Players => _players;

    /// <summary>
    /// Search for the frontend bank as specified in the Unity project (frontend/hires → medres → lowres; case-insensitive).
    /// null = not found.
    /// </summary>
    public static string FindFrontendBank(string fileName)
    {
        if (string.IsNullOrEmpty(BnkDir)) return null;
        foreach (var res in new[] { "hires", "medres", "lowres" })
        {
            var f = FindFile(Path.Combine(BnkDir, "frontend", res), fileName);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>File in a folder, case-insensitive (CarDatabase.FindFile).</summary>
    public static string FindFile(string dir, string fileName)
    {
        if (!GameFiles.DirectoryExists(dir)) return null;
        var direct = Path.Combine(dir, fileName);
        if (GameFiles.Exists(direct)) return direct;
        foreach (var f in GameFiles.EnumerateFiles(dir))
            if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    public static void Attach(IRenderer renderer) => Renderer = renderer;

    /// <summary>
    /// Load a film from a frontend bank (path is absolute or relative to <see cref="BnkDir"/>, e.g., “frontend/hires/gauges.bnk”).
    /// guiDepth is like GUI.depth in Unity: a smaller value is on top. Returns null on error.
    /// </summary>
    public static VmfPlayer Load(string bank, int guiDepth = 0, bool localize = true, Action<VmfPlayer> beforeLoad = null)
    {
        string path = Path.IsPathRooted(bank) || string.IsNullOrEmpty(BnkDir) ? bank : Path.Combine(BnkDir, bank);
        var p = new VmfPlayer { GuiDepth = guiDepth };
        try
        {
            beforeLoad?.Invoke(p);   // z.B. SetValue vor dem ersten Bild (Unity: vor Load)
            p.Load(path);
        }
        catch (Exception ex)
        {
            Log.Error("Flash: " + path + " cannot be loaded: " + ex.Message);
            p.Dispose();
            return null;
        }
        if (localize && !string.IsNullOrEmpty(BnkDir)) FlashLocalization.Apply(p, FlashLocalization.Load(BnkDir, Language));
        _players.Add(p);
        //RegisterMemoryLog();
        return p;
    }

    static bool _memRegistered;

    static void RegisterMemoryLog()
    {
        if (_memRegistered) return;
        _memRegistered = true;
        MemoryLog.Register("Flash", () =>
        {
            var sb = new System.Text.StringBuilder();
            long raw = 0, dec = 0, sg = 0, pend = 0;
            foreach (var p in _players.ToArray())
            {
                var s = p.MemoryStats();
                raw += s.raw; dec += s.decoded; sg += s.shapeGpu; pend += s.pending;
                sb.Append(", ").Append(Path.GetFileName(p.BankPath ?? "?")).Append(' ')
                  .Append(MemoryLog.Mb(s.raw)).Append('/').Append(MemoryLog.Mb(s.decoded)).Append('/').Append(s.shapes);
            }
            return $"{_players.Count} movies, raw data {MemoryLog.Mb(raw)}, decoded {MemoryLog.Mb(dec)}, GPU shapes {MemoryLog.Mb(sg)}, pending {MemoryLog.Mb(pend)}, raster buffers {MemoryLog.Mb(VmfRaster.BufferBytes)}{sb}";
        });
    }

    /// <summary>Remove and share a video.</summary>
    public static void Remove(VmfPlayer p)
    {
        if (p == null) return;
        _players.Remove(p);
        p.Dispose();
    }

    /// <summary>At the start of the video: Set the time and screen size, apply the settings, and update all movies.</summary>
    public static void Update(int screenWidth, int screenHeight)
    {
        Time.Tick();
        if (screenWidth > 0 && screenHeight > 0) { Screen.width = screenWidth; Screen.height = screenHeight; }
        FlashInput.BeginFrame();
        for (int i = 0; i < _players.Count; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { _players[i].Update(); }
            catch (Exception ex) { Log.Error("Flash-Update: " + ex.Message); }
            //PerfLog.Add("Flash-Update " + System.IO.Path.GetFileName(_players[i].BankPath ?? "?"), System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Additional UI layers outside the films (Unity: OnGUI of other components with GUI.depth, e.g., loading screen) –
    /// are drawn along with the films, sorted by GuiDepth.
    /// </summary>
    public interface IGuiLayer
    {
        int GuiDepth { get; }
        void Render(IRenderer r);
    }

    static readonly List<IGuiLayer> _layers = new();
    static readonly List<(int depth, VmfPlayer film, IGuiLayer layer)> _order = new();

    public static void AddLayer(IGuiLayer l) { if (!_layers.Contains(l)) _layers.Add(l); }
    public static void RemoveLayer(IGuiLayer l) => _layers.Remove(l);

    /// <summary>Draw all visible films and layers—those with a higher GuiDepth first (at the bottom)</summary>
    public static void Render()
    {
        if (Renderer == null || (_players.Count == 0 && _layers.Count == 0)) return;
        _order.Clear();
        foreach (var p in _players) _order.Add((p.GuiDepth, p, null));
        foreach (var l in _layers) _order.Add((l.GuiDepth, null, l));
        // stabil: bei gleicher Tiefe Filme in Ladereihenfolge, Ebenen danach
        for (int i = 1; i < _order.Count; i++)
        {
            var x = _order[i]; int j = i - 1;
            while (j >= 0 && _order[j].depth < x.depth) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = x;
        }
        foreach (var (_, film, layer) in _order)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { if (film != null) film.Render(); else layer.Render(Renderer); }
            catch (Exception ex) { Log.Error("Flash-Render: " + ex.Message); }
            ///if (film != null) 
            //    PerfLog.Add("Flash-Render " + System.IO.Path.GetFileName(film.BankPath ?? "?"), System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
    }

    public static void Shutdown()
    {
        foreach (var p in _players) p.Dispose();
        _players.Clear();
        Renderer = null;
    }

    static SystemLanguage SystemLanguageFromCulture(CultureInfo c)
    {
        // Veröffentlichte exe läuft mit InvariantGlobalization – dann die Windows-Oberflächensprache fragen
        if (c.TwoLetterISOLanguageName == "iv" || string.IsNullOrEmpty(c.Name))
        {
            try
            {
                return (GetUserDefaultUILanguage() & 0x3FF) switch
                {
                    0x07 => SystemLanguage.German,
                    0x0C => SystemLanguage.French,
                    0x10 => SystemLanguage.Italian,
                    0x0A => SystemLanguage.Spanish,
                    0x19 => SystemLanguage.Russian,
                    0x11 => SystemLanguage.Japanese,
                    0x12 => SystemLanguage.Korean,
                    0x04 => SystemLanguage.Chinese,
                    0x05 => SystemLanguage.Czech,
                    0x15 => SystemLanguage.Polish,
                    _ => SystemLanguage.English,
                };
            }
            catch { return SystemLanguage.English; }
        }
        return FromIso(c.TwoLetterISOLanguageName);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

    static SystemLanguage FromIso(string iso) => iso switch
    {
        "de" => SystemLanguage.German,
        "fr" => SystemLanguage.French,
        "it" => SystemLanguage.Italian,
        "es" => SystemLanguage.Spanish,
        "ru" => SystemLanguage.Russian,
        "ja" => SystemLanguage.Japanese,
        "ko" => SystemLanguage.Korean,
        "zh" => SystemLanguage.Chinese,
        "cs" => SystemLanguage.Czech,
        "pl" => SystemLanguage.Polish,
        _ => SystemLanguage.English,
    };
}

/// <summary>UnityEngine.SystemLanguage (nur die von FlashLocalization benutzten Werte).</summary>
public enum SystemLanguage
{
    English, German, French, Italian, Spanish, Russian, Japanese, Korean, Chinese, ChineseSimplified, ChineseTraditional, Czech, Polish
}