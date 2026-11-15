#nullable disable
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Render;
using System.Globalization;

namespace Paradise.Core.Flash;

/// <summary>
/// Verwaltet die geladenen Flash-Filme (VmfPlayer) – im Unity-Projekt waren das MonoBehaviours, die Unity selbst
/// aktualisiert (Update) und gezeichnet (OnGUI, sortiert nach GUI.depth) hat. Hier ruft der GameLoop
/// <see cref="Update"/> und <see cref="Render"/> auf. Das Spiel lädt/entfernt Filme nur über diese Klasse.
/// </summary>
public static class FlashHost
{
    static readonly List<VmfPlayer> _players = new();
    static readonly List<VmfPlayer> _sorted = new();

    /// <summary>Renderer für Texturen/Quads (vom GameLoop gesetzt).</summary>
    public static IRenderer Renderer { get; private set; }

    /// <summary>Ordner Euro/Bnk (Frontend-Banken, Datenbank für Texte).</summary>
    public static string BnkDir { get => _bnkDir ?? Config.GameData.BnkDir; set => _bnkDir = value; }
    static string _bnkDir;

    /// <summary>Sprachkürzel der Original-Daten (us, ge, fr, it, sp …).</summary>
    /// <summary>Gilt vor der Anmeldung und solange im Spielstand keine Sprache eingetragen ist: Englisch.
    /// Danach aus dem Spielstand (Optionen "WhatLanguage"), siehe Screens/Options.SavedLanguage.</summary>
    public static string Language { get; set; } = DefaultLanguage;

    /// <summary>Vorgabe-Sprache (keine Systemsprache): Englisch.</summary>
    public const string DefaultLanguage = "us";

    public static IReadOnlyList<VmfPlayer> Players => _players;

    /// <summary>
    /// Frontend-Bank suchen wie im Unity-Projekt (frontend/hires → medres → lowres, Groß-/Kleinschreibung egal).
    /// null = nicht gefunden.
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

    /// <summary>Datei in einem Ordner ohne Rücksicht auf Groß-/Kleinschreibung (CarDatabase.FindFile).</summary>
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
    /// Film aus einer Frontend-Bank laden (Pfad absolut oder relativ zu <see cref="BnkDir"/>, z.B. "frontend/hires/gauges.bnk").
    /// guiDepth wie GUI.depth in Unity: kleinerer Wert liegt oben. Rückgabe null bei Fehler.
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
            Log.Warn("Flash: " + path + " nicht ladbar: " + ex.Message);
            p.Dispose();
            return null;
        }
        if (localize && !string.IsNullOrEmpty(BnkDir)) FlashLocalization.Apply(p, FlashLocalization.Load(BnkDir, Language));
        _players.Add(p);
        RegisterMemoryLog();
        Log.Info("Flash: " + Path.GetFileName(path) + " geladen");
        return p;
    }

    static bool _memRegistered;
    /// <summary>[DIAG Mem] „Flash“: je Film Rohdaten/dekodierte Bitmaps/Shapes, Raster-Arbeitspuffer aller Threads.</summary>
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
            return $"{_players.Count} Filme, Rohdaten {MemoryLog.Mb(raw)}, dekodiert {MemoryLog.Mb(dec)}, Shapes GPU {MemoryLog.Mb(sg)}, wartend {MemoryLog.Mb(pend)}, Raster-Puffer {MemoryLog.Mb(VmfRaster.BufferBytes)}{sb}";
        });
    }

    /// <summary>Film entfernen und freigeben (Unity: Destroy).</summary>
    public static void Remove(VmfPlayer p)
    {
        if (p == null) return;
        _players.Remove(p);
        p.Dispose();
    }

    /// <summary>Zu Bildbeginn: Zeit, Bildschirmgröße, Eingaben übernehmen und alle Filme fortschreiben.</summary>
    public static void Update(int screenWidth, int screenHeight)
    {
        Time.Tick();
        if (screenWidth > 0 && screenHeight > 0) { Screen.width = screenWidth; Screen.height = screenHeight; }
        FlashInput.BeginFrame();
        for (int i = 0; i < _players.Count; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { _players[i].Update(); }
            catch (Exception ex) { Log.Warn("Flash-Update: " + ex.Message); }
            PerfLog.Add("Flash-Update " + System.IO.Path.GetFileName(_players[i].BankPath ?? "?"), System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Weitere Oberflächen-Ebenen außerhalb der Filme (Unity: OnGUI anderer Komponenten mit GUI.depth, z.B. Ladebildschirm) –
    /// werden mit den Filmen nach GuiDepth sortiert gezeichnet.
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

    /// <summary>Alle sichtbaren Filme und Ebenen zeichnen – höherer GuiDepth zuerst (liegt unten), wie Unity GUI.depth.</summary>
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
            catch (Exception ex) { Log.Warn("Flash-Render: " + ex.Message); }
            if (film != null) PerfLog.Add("Flash-Render " + System.IO.Path.GetFileName(film.BankPath ?? "?"), System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
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
