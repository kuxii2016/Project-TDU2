using Paradise.Core.Platform;
using Paradise.Game.Config;

namespace Paradise.Game.Input;

/// <summary>
/// Tastenbelegung – Port von Unity TDU2Controls (Client/Assets/TDU2Map/Runtime/Input/TDU2Controls.cs). Je Aktion zwei Tasten,
/// Standardwerte aus <see cref="KeyConfig"/> (Game/Config). Unity speicherte geänderte Tasten in PlayerPrefs; hier gelten Änderungen
/// aus dem Optionsmenü "Steuerung" (Flash-Liste controls_list, PauseMenu) im Spielstand des Profils (DATA, U3D_Keys) – Grundwerte im Code.
/// Abfrage: <see cref="Held"/> / <see cref="Pressed"/> mit der Aktions-Id ("accel", "horn", "menu_up" …).
/// Gamepad/Lenkrad: TDU2InputDevices (Geräteprofil) und Joystick-Knöpfe als Belegung (Unity KeyCode.Joystick&lt;n&gt;Button&lt;m&gt;,
/// hier Code <see cref="JoyKeyBase"/> + (Joystick − 1) · 20 + Knopf).
/// </summary>
public static class TDU2Controls
{
    public sealed class Action
    {
        public string Id = "", Name = "", NameEn = "";
        public int Default1, Default2, Key1, Key2;
        /// <summary>Zuletzt "gespeicherte" Belegung (Unity PlayerPrefs – hier nur im Speicher für die Sitzung).</summary>
        public int Saved1, Saved2;
        public KeyConfig.Category Cat;
    }

    public static readonly List<Action> All = new();
    static readonly Dictionary<string, Action> _byId = new(StringComparer.OrdinalIgnoreCase);
    static bool _loaded;

    // Tastenzustand je Bild (Pressed = in diesem Bild neu gedrückt)
    static readonly bool[] _cur = new bool[256], _prev = new bool[256];
    static long _frame = -1;

    /// <summary>true, solange eine Texteingabe (Chat, Profilname) die Tastatur hat – dann melden Held/Pressed nichts.</summary>
    public static bool Blocked;

    /// <summary>Einmal je Bild (mehrfacher Aufruf im selben Bild ist harmlos): Tastenzustand übernehmen.</summary>
    public static void Update(InputState input, long frame)
    {
        if (frame == _frame) return;
        _frame = frame;
        Joysticks.Frame = frame;   // Gamepad/Lenkrad: einmal je Bild lesen (Core/Platform/Joysticks)
        Array.Copy(_cur, _prev, 256);
        for (int i = 0; i < 256; i++) _cur[i] = input.IsKeyDown(i);
    }

    static void EnsureLoaded() { if (!_loaded) Init(); }

    /// <summary>Standardbelegung aus KeyConfig (Unity Load() ohne gespeicherte Tasten: doppelte Standardtasten in derselben Gruppe auflösen).</summary>
    static void Init()
    {
        All.Clear(); _byId.Clear();
        foreach (var b in new KeyConfig().Bindings)
        {
            var a = new Action { Id = b.Id, Name = b.Name, NameEn = b.NameEn, Default1 = b.Key1, Default2 = b.Key2, Key1 = b.Key1, Key2 = b.Key2, Cat = b.Cat };
            All.Add(a); _byId[a.Id] = a;
        }
        // Unity: Aktionen ohne gespeicherte Taste nehmen ihre Standardtaste anderen Aktionen derselben Gruppe weg
        foreach (var a in All)
        {
            if (a.Key1 == 0) continue;
            foreach (var o in All)
            {
                if (o == a || o.Cat != a.Cat) continue;   // gleiche Taste in anderen Gruppen ist erlaubt (W fährt und geht)
                if (o.Key1 == a.Key1) o.Key1 = o.Default1 != a.Key1 ? o.Default1 : 0;
                if (o.Key2 == a.Key1) o.Key2 = 0;
            }
        }
        foreach (var a in All) { a.Saved1 = a.Key1; a.Saved2 = a.Key2; }
        _loaded = true;
    }

    /// <summary>Unity Load(): gespeicherte Belegung wiederherstellen (verwirft ungespeicherte Änderungen).</summary>
    public static void Load()
    {
        EnsureLoaded();
        foreach (var a in All) { a.Key1 = a.Saved1; a.Key2 = a.Saved2; }
    }

    /// <summary>Unity Save(): Belegung übernehmen (gilt bis Spielende; dauerhaft nur über KeyConfig im Code).</summary>
    public static void Save()
    {
        EnsureLoaded();
        foreach (var a in All) { a.Saved1 = a.Key1; a.Saved2 = a.Key2; }
    }

    /// <summary>Gespeicherte Belegung als "id=taste1,taste2;…" (nur Abweichungen von KeyConfig) – für den Spielstand.</summary>
    public static string Export()
    {
        EnsureLoaded();
        var sb = new System.Text.StringBuilder();
        foreach (var a in All)
        {
            if (a.Saved1 == a.Default1 && a.Saved2 == a.Default2) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(a.Id).Append('=').Append(a.Saved1).Append(',').Append(a.Saved2);
        }
        // Eigene Achsen-Belegungen der Geräte (Unity PlayerPrefs "TDU2.Axis.<Gerät>.<Aktion>")
        foreach (var kv in TDU2InputDevices.ExportPrefs())
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(kv.Key).Append('=').Append(kv.Value);
        }
        return sb.ToString();
    }

    /// <summary>Belegung aus dem Spielstand übernehmen (Unity: PlayerPrefs "TDU2.Key.&lt;Aktion&gt;.0/1").</summary>
    public static void Import(string? data)
    {
        Init();   // erst Standard, dann die gespeicherten Abweichungen
        TDU2InputDevices.ClearPrefs();
        if (string.IsNullOrEmpty(data)) return;
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.StartsWith("TDU2.Axis.", StringComparison.Ordinal)) { TDU2InputDevices.ImportPref(part.Substring(0, eq), part.Substring(eq + 1)); continue; }
            var a = Get(part.Substring(0, eq));
            var keys = part.Substring(eq + 1).Split(',');
            if (a == null || keys.Length != 2 || !int.TryParse(keys[0], out int k1) || !int.TryParse(keys[1], out int k2)) continue;
            a.Key1 = a.Saved1 = ValidKey(k1) ? k1 : 0;
            a.Key2 = a.Saved2 = ValidKey(k2) ? k2 : 0;
        }
    }

    /// <summary>Aktionen einer Gruppe (Reihenfolge wie in KeyConfig).</summary>
    public static List<Action> InCategory(KeyConfig.Category c)
    {
        EnsureLoaded();
        return All.FindAll(a => a.Cat == c);
    }

    /// <summary>Gruppe zu _root.WhatListe des Films ("driving", "walkmode", "menu").</summary>
    public static KeyConfig.Category CategoryOf(string? whatListe)
    {
        string w = (whatListe ?? "").Trim().ToLowerInvariant();
        if (w.StartsWith("walk") || w.StartsWith("avat")) return KeyConfig.Category.Walk;
        if (w.StartsWith("menu") || w.StartsWith("casino")) return KeyConfig.Category.Menu;
        return KeyConfig.Category.Driving;
    }

    public static void ResetDefaults() { EnsureLoaded(); foreach (var a in All) { a.Key1 = a.Default1; a.Key2 = a.Default2; } }

    public static Action? Get(string id) { EnsureLoaded(); return _byId.TryGetValue(id, out var a) ? a : null; }

    /// <summary>Joystick-Knöpfe als Tastencode: JoyKeyBase + (Joystick − 1) · 20 + Knopf (Unity Joystick1Button0 … Joystick8Button19).</summary>
    public const int JoyKeyBase = 0x1000;
    public static bool IsJoystickKey(int k) => k >= JoyKeyBase && k < JoyKeyBase + Joysticks.MaxJoysticks * Joysticks.MaxButtons;
    static bool ValidKey(int k) => (k >= 0 && k < 256) || IsJoystickKey(k);
    static bool JoyDown(int k) { int n = k - JoyKeyBase; return Joysticks.Button(n / Joysticks.MaxButtons + 1, n % Joysticks.MaxButtons); }
    static bool JoyEdge(int k) { int n = k - JoyKeyBase; return Joysticks.ButtonDown(n / Joysticks.MaxButtons + 1, n % Joysticks.MaxButtons); }

    static bool Down(int vk) => IsJoystickKey(vk) ? JoyDown(vk) : vk > 0 && vk < 256 && _cur[vk];
    /// <summary>Taste (Windows-Tastencode) in diesem Bild gehalten – für feste Tasten außerhalb der Belegung (Umschalt, Tab …).</summary>
    public static bool KeyDown(int vk) => !Blocked && Down(vk);
    /// <summary>Feste Taste (Windows-Tastencode) in diesem Bild gedrückt – z.B. Ziffernblock im Fahrzeugmenü.</summary>
    public static bool KeyPressed(int vk) => !Blocked && Edge(vk);
    static bool Edge(int vk) => IsJoystickKey(vk) ? JoyEdge(vk) : vk > 0 && vk < 256 && _cur[vk] && !_prev[vk];

    /// <summary>Taste gehalten, auch während einer Texteingabe (Unity Input.GetKey – z.B. TDU2Nav, ESC-Halten im Interieur).</summary>
    public static bool RawKeyDown(int vk) => Down(vk);
    /// <summary>Taste in diesem Bild gedrückt, auch während einer Texteingabe (Unity Input.GetKeyDown).</summary>
    public static bool RawKeyPressed(int vk) => Edge(vk);

    /// <summary>Taste gedrückt gehalten.</summary>
    public static bool Held(string id)
    {
        if (Blocked) return false;
        var a = Get(id);
        if (a != null && (Down(a.Key1) || Down(a.Key2))) return true;
        return TDU2InputDevices.ButtonHeld(id);
    }

    /// <summary>Taste in diesem Bild gedrückt.</summary>
    public static bool Pressed(string id)
    {
        if (Blocked) return false;
        var a = Get(id);
        if (a != null && (Edge(a.Key1) || Edge(a.Key2))) return true;
        switch (id)   // Menü-Richtungen: Steuerkreuz / linker Stick (TDU2Nav, mit Wiederholung)
        {
            case "menu_up": return TDU2Nav.PadUp();
            case "menu_down": return TDU2Nav.PadDown();
            case "menu_left": return TDU2Nav.PadLeft();
            case "menu_right": return TDU2Nav.PadRight();
        }
        return TDU2InputDevices.ButtonPressed(id);
    }

    /// <summary>Aktion auf einen Joystick-Knopf gelegt (Unity IsJoystickBound – z.B. H-Schaltung TH8A).</summary>
    public static bool IsJoystickBound(string id)
    {
        var a = Get(id);
        return a != null && (IsJoystickKey(a.Key1) || IsJoystickKey(a.Key2));
    }

    /// <summary>Joystick-Knopf der Aktion gehalten (Unity JoystickHeld).</summary>
    public static bool JoystickHeld(string id)
    {
        var a = Get(id);
        if (a == null || Blocked) return false;
        return (IsJoystickKey(a.Key1) && JoyDown(a.Key1)) || (IsJoystickKey(a.Key2) && JoyDown(a.Key2));
    }

    /// <summary>Bestätigen in Menüs/Filmen/Spots (belegbar, Standard Enter).</summary>
    public static bool ConfirmPressed() => !Blocked && Pressed("validate");

    /// <summary>Zurück in Menüs (belegbar, Standard Rücktaste). ESC bleibt das Pausemenü.</summary>
    public static bool BackPressed() => !Blocked && Pressed("back");

    /// <summary>Neue Haupttaste setzen; dieselbe Taste wird bei anderen Aktionen der Gruppe entfernt.</summary>
    public static void Bind(Action a, int key)
    {
        foreach (var o in All)
        {
            if (o == a || o.Cat != a.Cat) continue;
            if (o.Key1 == key) o.Key1 = 0;
            if (o.Key2 == key) o.Key2 = 0;
        }
        if (a.Key2 == key) a.Key2 = 0;
        a.Key1 = key;
    }

    public static void Unbind(Action a) { a.Key1 = 0; a.Key2 = 0; }

    /// <summary>Erste in diesem Bild gedrückte Taste (für "Taste zuweisen"); 0 wenn keine.</summary>
    public static int AnyKeyDown()
    {
        for (int i = 8; i < 256; i++) if (Edge(i)) return i;
        var (j, b) = Joysticks.AnyButtonDown();   // Unity: auch Joystick<n>Button<m> (gerätegenau)
        if (j > 0) return JoyKeyBase + (j - 1) * Joysticks.MaxButtons + b;
        return 0;
    }

    /// <summary>Anzeigename einer Taste (Unity KeyName; die Spielschrift hat keine Pfeil-Zeichen).</summary>
    public static string KeyName(int k)
    {
        if (IsJoystickKey(k))
        {
            int n = k - JoyKeyBase;
            return "J" + (n / Joysticks.MaxButtons + 1) + " Knopf " + (n % Joysticks.MaxButtons + 1);   // Gerät + Knopf (1-basiert wie in der Windows-Systemsteuerung)
        }
        switch (k)
        {
            case 0: return "";
            case 0x26: return "Up";
            case 0x28: return "Down";
            case 0x25: return "Left";
            case 0x27: return "Right";
            case 0x20: return "Space";
            case 0x10: return "Shift";
            case 0x11: return "Strg";
            case 0x12: return "Alt";
            case 0x0D: return "Enter";
            case 0x08: return "Rücktaste";
            case 0x1B: return "Esc";
            case 0x09: return "Tab";
            case 0xBC: return ",";
            case 0xBE: return ".";
            case 0xBD: return "-";
            case 0xBB: return "+";
        }
        if (k >= 0x30 && k <= 0x39) return ((char)k).ToString();
        if (k >= 0x41 && k <= 0x5A) return ((char)k).ToString();
        if (k >= 0x60 && k <= 0x69) return "Num " + (k - 0x60);
        if (k >= 0x70 && k <= 0x87) return "F" + (k - 0x6F);
        return "0x" + k.ToString("X2");
    }

    public static string BindText(Action a)
    {
        string k1 = KeyName(a.Key1), k2 = KeyName(a.Key2);
        if (k1.Length == 0) return k2.Length == 0 ? "-" : k2;
        return k2.Length == 0 ? k1 : k1 + " / " + k2;
    }
}
