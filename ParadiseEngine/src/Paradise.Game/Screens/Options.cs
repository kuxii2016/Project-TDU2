using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using System.Globalization;

namespace Paradise.Game.Screens;

/// <summary>
/// Spielseite der Original-Optionsmenüs (main.bnk, "options") – Port von Unity TDU2Options. Die Flash-Seiten legen ihre Zeilen selbst
/// fest (setParam); das Programm liefert die Werte und reagiert auf die FSCommands:
///   "&lt;präfix&gt;open" Seite öffnet -> _root-Variablen setzen; "&lt;befehl&gt;:scpr"/":scne" Auswahl links/rechts;
///   "&lt;befehl&gt;:slid:updt" Schieberegler; "&lt;präfix&gt;vali"/"canc" Übernehmen/Abbrechen; "opts:vide:test" Grafik prüfen.
/// Werte stehen im Spielstand des Profils (DATA, U3D_Options – Unity: PlayerPrefs); Grundwerte in Game/Config, keine eigene Datei.
/// Einstellungen ohne Gegenstück in der Engine (Auflösung, Kantenglättung …) zeigt das Menü, sie bewirken noch nichts.
/// </summary>
public sealed class Options
{
    public sealed class Entry
    {
        public string Cmd = "", Var = "";
        public bool IsSlider;
        public int Max = 20;
        public Func<string[]>? Choices;
        public int Value, Default;
        public Action<int>? Apply;
    }

    readonly Dictionary<string, Entry> _byCmd = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Entry> _all = new();
    readonly Func<string, string?> _text;
    readonly IGameModeContext _ctx;
    readonly AudioConfig _audio = new();
    readonly VehicleConfig _vehicle = new();
    readonly GraphicsConfig _graphics = new();

    public static readonly string[] LangCodes = { "ge", "us", "fr", "it", "sp", "ru", "pl", "cz", "ja", "ko", "ch" };
    public static readonly string[] LangNames = { "Deutsch", "English", "Français", "Italiano", "Español", "Русский", "Polski", "Čeština", "日本語", "한국어", "中文" };

    VehicleManager? Vm => _ctx.Vehicles?.Manager;
    /// <summary>Nach einem Sprachwechsel: neue Texte (Unity TDU2Options.LanguageChanged).</summary>
    public Action<Dictionary<string, string>>? LanguageChanged;

    /// <summary>Zuletzt erzeugtes Optionsmenü (Spielstand: <see cref="Export"/>).</summary>
    public static Options? Instance { get; private set; }

    /// <summary>
    /// Einstellungen aus dem Spielstand ("var=wert;…", Unity PlayerPrefs "TDU2.Opt.*"), werden beim Erzeugen bzw. nach dem Anmelden
    /// übernommen. Gespeichert wird im Profil (DATA, CEnvironment U3D_Options) – keine eigene Datei.
    /// </summary>
    public static string Saved = "";

    public Options(IGameModeContext ctx, Func<string, string?> text)
    {
        _ctx = ctx; _text = text;
        Instance = this;
        string On = T("m_On", "Ein"), Off = T("m_Off", "Aus");
        string[] OffOn = { Off, On };
        string[] LMH = { T("m_Low", "Niedrig"), T("m_Medium", "Mittel"), T("m_High", "Hoch") };

        // ---------------- Grafik (opts:vide:)
        // Unity: Screen.resolutions / Screen.SetResolution – hier übernommen mit "Testen" (A) der Grafikseite, siehe Handle
        Choice("opts:vide:prrs", "resolution", () => Resolutions(), CurrentResolution(), null);
        Choice("opts:vide:prfr", "frequency", () => new[] { "60 Hz", "75 Hz", "100 Hz", "120 Hz", "144 Hz", T("m_Auto", "Auto") }, 5, null);
        Choice("opts:vide:vsyn", "VSync", () => OffOn, _graphics.VSync ? 1 : 0, i => GraphicsSettings.VSync = i == 1);
        int aaDef = _graphics.AntiAliasing >= 8 ? 3 : _graphics.AntiAliasing >= 4 ? 2 : _graphics.AntiAliasing >= 2 ? 1 : 0;
        Choice("opts:vide:praa", "anti_alias", () => new[] { Off, "2x", "4x", "8x" }, aaDef, i => GraphicsSettings.Msaa = new[] { 1, 2, 4, 8 }[i]);
        int lodDef = _graphics.LodBias < 0.8f ? 0 : _graphics.LodBias > 1.5f ? 2 : 1;
        Choice("opts:vide:prlo", "lod", () => LMH, lodDef, i => RenderDistance.SetBaseLodBias(new[] { 0.6f, 1f, 2f }[i]));
        Choice("opts:vide:caop", "car_opponent", () => LMH, 2, null);
        Choice("opts:vide:txfl", "texture", () => LMH, System.Math.Clamp(_graphics.TextureQuality, 0, 2), i => GraphicsSettings.TextureMipLimit = 2 - i);
        Choice("opts:vide:fxqu", "fx_qual", () => LMH, System.Math.Clamp(_graphics.EffectsQuality, 0, 2), GraphicsConfig.ApplyEffects);
        Slider("opts:gama:gams", "gamma", 18, 9, null);

        // ---------------- Audio (opts:audi:) – Regler 0..20 als Faktor auf die Grundwerte aus AudioConfig/VehicleConfig
        Slider("opts:audi:mavo", "master_volume", 20, 20, v => AudioEngine.MasterVolume = _audio.Master * v / 20f);
        Slider("opts:audi:muvo", "music_volume", 20, 20, v => FrontendMusic.Volume = _audio.Music * v / 20f);
        Slider("opts:audi:vevo", "vehicules_volume", 20, 20, v =>
        {
            if (Vm == null) return;
            Vm.Volume = _vehicle.Volume * v / 20f;
            if (Vm.Audio != null) Vm.Audio.MasterVolume = Vm.Volume;   // laufender Motorsound sofort
        });
        Slider("opts:audi:vovo", "voices_volume", 20, 16, null);
        Slider("opts:audi:fxvo", "sfx_volume", 20, 20, v => FrontendSounds.Volume = _audio.Interface * v / 20f);
        Slider("opts:audi:voip", "voiceIP_volume", 20, 16, null);
        Choice("opts:audi:mike", "mike", () => OffOn, 0, null);
        Choice("opts:audi:pkem", "mute_pokemote", () => OffOn, 0, null);
        Choice("opts:audi:sedr", "AudioDriver", () => new[] { "Standard" }, 0, null);

        // ---------------- Spieleinstellungen (opts:gase:) – Fahrhilfen und Getriebe gehen direkt ans Fahrzeug
        Choice("opts:gase:prdh", "DrivingHelp", () => new[] { T("m_Secure", "Fahrhilfe"), T("m_Sport", "Sport"), T("m_Hypersport", "Hypersport"), Off }, (int)_vehicle.Aids, i =>
        {
            var aids = (DrivingAids)i;   // Secure, Sport, HyperSport, Off
            if (Vm != null) { Vm.Aids = aids; if (Vm.Current != null) Vm.Current.Aids = aids; }
        });
        Choice("opts:gase:gear", "gearbox", () => new[] { T("m_Automatic", "Automatik"), T("m_Manual", "Manuell"), T("m_HShifter", "H-Schaltung"), T("m_HShifterClutch", "H-Schaltung + Kupplung") }, _vehicle.AutomaticGearbox ? 0 : 1, i =>
        {
            if (Vm == null) return;
            Vm.AutomaticGearbox = i == 0;   // H-Schaltung/Kupplung folgen mit den Eingabegeräten
            if (Vm.Current != null) Vm.Current.AutomaticGearbox = i == 0;
        });
        Choice("opts:gase:invc", "InvertCam", () => OffOn, 0, null);
        Choice("opts:gase:inax", "InvertYAxis", () => OffOn, 0, null);
        Choice("opts:gase:alch", "AllowChase", () => OffOn, 1, null);
        Choice("opts:gase:valu", "StakeValue", () => new[] { "1 000 $", "5 000 $", "10 000 $", "50 000 $" }, 0, null);

        // ---------------- Anzeige (opts:disp:)
        Choice("opts:disp:usim", "UnitSystem", () => new[] { "Metrisch (km/h)", "Imperial (mph)" }, 0, null);
        Choice("opts:disp:dicp", "DispSpeed", () => OffOn, 1, i => { if (Vm != null) Vm.ShowHud = i == 1; });
        Choice("opts:disp:gpfi", "GPSStatus", () => OffOn, 1, i => { if (Vm?.Minimap != null) Vm.Minimap.ShowMinimap = i == 1; });
        Choice("opts:disp:frim", "DispFrim", () => OffOn, 1, null);

        // ---------------- Sprache (opts:lang:)
        Choice("opts:lang:prlg", "WhatLanguage", () => LangNames, System.Math.Max(0, Array.IndexOf(LangCodes, FlashHost.Language)), i =>
        {
            if (i >= 0 && i < LangCodes.Length) ChangeLanguage(LangCodes[i]);
        });

        // ---------------- Steuerung (opts:cont:...) – Eingabegeräte (Gamepad/Lenkrad, Force Feedback) folgen mit TDU2InputDevices
        // Gerät/Maus/Lenkrad: Werte gehen direkt an TDU2InputDevices (Regler sind sofort wirksam)
        Choice("opts:cont:sede:whde", "WhatDevice", () => new[] { T("m_Keyboard_M", "Tastatur / Maus"), "Gamepad", T("m_Wheel", "Lenkrad") }, 0,
            i => TDU2InputDevices.Mode = (TDU2InputDevices.DeviceMode)System.Math.Clamp(i, 0, 2));
        Slider("opts:cont:mous:mose", "mouse_sensitivity", 100, 50, v => TDU2InputDevices.MouseSensitivity = System.Math.Max(0.1f, v / 50f));
        Choice("opts:cont:mous:moin", "mouse_invert", () => OffOn, 0, i => TDU2InputDevices.MouseInvert = i == 1);
        Slider("opts:cont:weel:vibr", "vibration", 10, 5, null);
        Choice("opts:cont:weel:ffbk", "force_feedback", () => OffOn, 1, null);
        Slider("opts:cont:weel:lisl", "linearity", 10, 5, v => TDU2InputDevices.SteerLinearity = v / 10f);
        Slider("opts:cont:weel:spfa", "speed_dfactor", 10, 5, v => TDU2InputDevices.SpeedFactor = v / 10f);
        Slider("opts:cont:weel:damp", "damping", 10, 5, null);
        Slider("opts:cont:weel:dezo", "dead_zone", 10, 1, v => TDU2InputDevices.DeadZone = v / 20f);
        Slider("opts:cont:weel:clut", "clutch", 10, 5, v => TDU2InputDevices.ClutchLinearity = v / 10f);
        Slider("opts:cont:weel:thro", "throttle", 10, 5, v => TDU2InputDevices.ThrottleLinearity = v / 10f);
        Slider("opts:cont:weel:brak", "brake", 10, 5, v => TDU2InputDevices.BrakeLinearity = v / 10f);

        Import(Saved);   // Einstellungen aus dem Spielstand
    }

    string T(string key, string fallback) { var s = _text(key); return string.IsNullOrEmpty(s) ? fallback : s; }

    Entry Choice(string cmd, string var, Func<string[]> choices, int def, Action<int>? apply)
    {
        var e = new Entry { Cmd = cmd, Var = var, Choices = choices, Default = System.Math.Max(0, def), Value = System.Math.Max(0, def), Apply = apply };
        _byCmd[cmd] = e; _all.Add(e);
        return e;
    }

    Entry Slider(string cmd, string var, int max, int def, Action<int>? apply)
    {
        var e = new Entry { Cmd = cmd, Var = var, IsSlider = true, Max = max, Default = def, Value = System.Math.Clamp(def, 0, max), Apply = apply };
        _byCmd[cmd] = e; _all.Add(e);
        return e;
    }

    /// <summary>Alle Werte als "var=wert;…" (für den Spielstand).</summary>
    public string Export()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in _all)
        {
            if (e.Var == "resolution") continue;   // Fenstergröße bestimmt das System
            if (sb.Length > 0) sb.Append(';');
            sb.Append(e.Var).Append('=').Append(e.Value.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Werte aus dem Spielstand übernehmen und anwenden (Unity TDU2Options.ApplyAll).</summary>
    public void Import(string? data)
    {
        if (string.IsNullOrEmpty(data)) return;
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || !int.TryParse(part.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) continue;
            var e = Get(part.Substring(0, eq));
            if (e == null || e.Var == "resolution") continue;
            int n = e.IsSlider ? e.Max + 1 : (e.Choices?.Invoke()?.Length ?? 0);
            if (n <= 0) continue;
            e.Value = System.Math.Clamp(v, 0, n - 1);
            try { e.Apply?.Invoke(e.Value); } catch (Exception ex) { Log.Warn("Option " + e.Var + ": " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ Auflösungen (Unity ResolutionList/Resolutions/CurrentResolution)
    static List<(int W, int H, int Hz)> ResolutionList()
    {
        var list = Paradise.Core.Platform.Win32Window.DisplayResolutions();
        if (list.Count == 0) list.Add((Screen.width, Screen.height, 0));
        return list;
    }

    static string[] Resolutions()
    {
        var list = ResolutionList();
        var s = new string[list.Count];
        for (int i = 0; i < s.Length; i++) s[i] = list[i].W + " x " + list[i].H;
        return s;
    }

    static int CurrentResolution()
    {
        var list = ResolutionList();
        int i = list.FindIndex(r => r.W == Screen.width && r.H == Screen.height);
        return i >= 0 ? i : list.Count - 1;
    }

    /// <summary>Gewählte Auflösung setzen, falls sie von der aktuellen abweicht (Unity: Screen.SetResolution).</summary>
    void ApplyResolution()
    {
        var e = Get("resolution");
        var win = Paradise.Core.Platform.Win32Window.Current;
        if (e == null || win == null) return;
        var list = ResolutionList();
        if (e.Value < 0 || e.Value >= list.Count) return;
        var r = list[e.Value];
        if (r.W == Screen.width && r.H == Screen.height) return;
        win.SetResolution(r.W, r.H);
    }

    public Entry? Get(string var) => _all.Find(e => string.Equals(e.Var, var, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sprache umschalten: Texte aller geladenen Filme neu (Unity TDU2PauseMenu.ChangeLanguage).</summary>
    void ChangeLanguage(string code)
    {
        var texts = SetLanguage(code);
        if (texts == null) return;
        LanguageChanged?.Invoke(texts);
        // Im Optionen-Flash geändert: sofort in den Spielstand eintragen (von dort wird sie gelesen)
        Saved = Export();
        Online.SessionAutosave.Schedule(0.5f);
    }

    /// <summary>
    /// Sprache aus dem Spielstand (Optionen "WhatLanguage"); ohne Eintrag Englisch (FlashHost.DefaultLanguage).
    /// </summary>
    public static string SavedLanguage()
    {
        string sys = FlashHost.DefaultLanguage;
        int i = Array.IndexOf(LangCodes, sys);
        foreach (var part in (Saved ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq) == "WhatLanguage" && int.TryParse(part.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) i = v;
        }
        return i >= 0 && i < LangCodes.Length ? LangCodes[i] : sys;
    }

    /// <summary>Sprache setzen und die Texte aller geladenen Filme erneuern; null = unverändert/nicht möglich.</summary>
    public static Dictionary<string, string>? SetLanguage(string code)
    {
        if (code == FlashHost.Language || string.IsNullOrEmpty(FlashHost.BnkDir)) return null;
        try
        {
            FlashHost.Language = code;
            var texts = FlashLocalization.Load(FlashHost.BnkDir, code);
            foreach (var p in FlashHost.Players) FlashLocalization.Apply(p, texts);
            Log.Info("Sprache: " + code);
            return texts;
        }
        catch (Exception ex) { Log.Warn("Sprache " + code + ": " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ Werte in den Film schreiben
    public void Publish(VmfPlayer ui, string? prefix = null)
    {
        foreach (var e in _all)
        {
            if (prefix != null && !e.Cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.IsSlider) ui.SetValue("/:" + e.Var, e.Value);
            else ui.SetVariable("/:" + e.Var, Text(e));
        }
    }

    string Text(Entry e)
    {
        var o = e.Choices?.Invoke();
        if (o == null || o.Length == 0) return "";
        e.Value = System.Math.Clamp(e.Value, 0, o.Length - 1);
        return o[e.Value];
    }

    /// <summary>FSCommand der Optionsseiten verarbeiten; true = erkannt.</summary>
    public bool Handle(VmfPlayer ui, string cmd)
    {
        if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith("opts:", StringComparison.OrdinalIgnoreCase)) return false;
        if (cmd.EndsWith("open", StringComparison.OrdinalIgnoreCase))
        {
            var res = Get("resolution");
            if (res != null && cmd.StartsWith("opts:vide", StringComparison.OrdinalIgnoreCase)) res.Value = CurrentResolution();   // aktuelle Größe anzeigen
            Publish(ui, cmd.Substring(0, cmd.Length - 4));
            return true;
        }
        // Grafikseite, Taste A: das Spiel meldet "geändert + in Ordnung" (sonst spielt der Film nur "sndp:wrong")
        if (cmd.Equals("opts:vide:test", StringComparison.OrdinalIgnoreCase))
        {
            ApplyResolution();
            ui.SetValue("/:IsVideoChanged", true);
            ui.SetValue("/:IsVideoChangeOk", true);
            return true;
        }
        if (cmd.EndsWith("vali", StringComparison.OrdinalIgnoreCase) || cmd.EndsWith("canc", StringComparison.OrdinalIgnoreCase))
        {
            // Unity: PlayerPrefs.Save() – hier in den Spielstand (Autosave gleich danach)
            Saved = Export();
            Online.SessionAutosave.Schedule(0.5f);
            return true;
        }

        string baseCmd = cmd; int dir = 0; bool slider = false;
        if (cmd.EndsWith(":scne", StringComparison.OrdinalIgnoreCase)) { dir = 1; baseCmd = cmd.Substring(0, cmd.Length - 5); }
        else if (cmd.EndsWith(":scpr", StringComparison.OrdinalIgnoreCase)) { dir = -1; baseCmd = cmd.Substring(0, cmd.Length - 5); }
        else if (cmd.EndsWith(":slid:updt", StringComparison.OrdinalIgnoreCase)) { slider = true; baseCmd = cmd.Substring(0, cmd.Length - 10); }
        if (!_byCmd.TryGetValue(baseCmd, out var e)) return true;   // unbekannte Zeile: ignorieren

        if (slider || e.IsSlider)
        {
            if (slider)
            {
                var s = ui.GetVariable("/:" + e.Var);
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    e.Value = System.Math.Clamp((int)System.Math.Round(d), 0, e.Max);
            }
            else e.Value = System.Math.Clamp(e.Value + dir, 0, e.Max);
        }
        else
        {
            int n = e.Choices?.Invoke()?.Length ?? 0;
            if (n > 0) e.Value = ((e.Value + dir) % n + n) % n;
            ui.SetVariable("/:" + e.Var, Text(e));   // Auswahlfeld liest den Text direkt danach (updateText)
        }
        if (e.Apply == null) Log.Debug($"Option {e.Var} = {e.Value} (in der Engine noch ohne Wirkung)");
        try { e.Apply?.Invoke(e.Value); } catch (Exception ex) { Log.Warn("Option " + e.Var + ": " + ex.Message); }
        return true;
    }
}