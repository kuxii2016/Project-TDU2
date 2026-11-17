namespace Paradise.Game.Config;

/// <summary>
/// Tastenbelegung (Standardwerte wie Unity TDU2Controls.All, dazu Radio- und Entwicklertasten). Werte nur hier im Code ändern
/// (keine Datei, kein Speichern/Laden), wie AudioConfig. Im Optionsmenü "Steuerung" geänderte Tasten stehen im Spielstand des Profils (DATA, U3D_Keys).
/// Tasten = Windows-Tastencodes (VK). Je Aktion zwei Tasten (0 = keine). Gruppen wie die drei Listen des Original-Menüs
/// (_root.WhatListe: driving / walkmode / menu); Radio und Entwickler erscheinen dort nicht.
/// </summary>
public sealed class KeyConfig
{
    public enum Category { Driving = 0, Walk = 1, Menu = 2, Radio = 3, Debug = 4 }

    public sealed record Binding(string Id, string Name, string NameEn, int Key1, int Key2, Category Cat);

    // Windows-Tastencodes
    public const int None = 0, Backspace = 0x08, Tab = 0x09, Return = 0x0D, Shift = 0x10, Control = 0x11, Alt = 0x12, Escape = 0x1B, Space = 0x20,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28,
        D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34, D5 = 0x35, D6 = 0x36, D7 = 0x37,
        A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47, H = 0x48, I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C,
        M = 0x4D, N = 0x4E, Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58, Y = 0x59,
        Num2 = 0x62, Num4 = 0x64, Num5 = 0x65, Num6 = 0x66, Num8 = 0x68,
        F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79;

    public List<Binding> Bindings { get; } = new()
    {
        // ---- Fahren (Unity TDU2Controls, _root.WhatListe "driving")
        new("accel",      "Gas",                         "Accelerate",           W, Up,      Category.Driving),
        new("brake",      "Bremse / Rückwärts",          "Brake / Reverse",      S, Down,    Category.Driving),
        new("left",       "Lenken links",                "Steer left",           A, Left,    Category.Driving),
        new("right",      "Lenken rechts",               "Steer right",          D, Right,   Category.Driving),
        new("handbrake",  "Handbremse",                  "Handbrake",            Space, None, Category.Driving),
        new("clutch",     "Kupplung",                    "Clutch",               Alt, None,  Category.Driving),
        new("gearup",     "Hochschalten",                "Gear up",              E, Shift,   Category.Driving),
        new("geardown",   "Runterschalten",              "Gear down",            Q, Control, Category.Driving),
        new("gear1",      "1. Gang (H-Schaltung)",       "1st gear (H-shifter)", D1, None,   Category.Driving),
        new("gear2",      "2. Gang (H-Schaltung)",       "2nd gear (H-shifter)", D2, None,   Category.Driving),
        new("gear3",      "3. Gang (H-Schaltung)",       "3rd gear (H-shifter)", D3, None,   Category.Driving),
        new("gear4",      "4. Gang (H-Schaltung)",       "4th gear (H-shifter)", D4, None,   Category.Driving),
        new("gear5",      "5. Gang (H-Schaltung)",       "5th gear (H-shifter)", D5, None,   Category.Driving),
        new("gear6",      "6. Gang (H-Schaltung)",       "6th gear (H-shifter)", D6, None,   Category.Driving),
        new("gear7",      "7. Gang (H-Schaltung)",       "7th gear (H-shifter)", D7, None,   Category.Driving),
        new("gearR",      "Rückwärtsgang (H-Schaltung)", "Reverse (H-shifter)",  D0, None,   Category.Driving),
        new("horn",       "Hupe",                        "Horn",                 H, None,    Category.Driving),
        new("starter",    "Motor an/aus",                "Engine on/off",        I, None,    Category.Driving),
        new("doors",      "Türen öffnen/schließen",      "Open/close doors",     K, None,    Category.Driving),
        new("windows",    "Fenster öffnen/schließen",    "Open/close windows",   N, None,    Category.Driving),
        new("lookback",   "Nach hinten schauen",         "Look back",            B, None,    Category.Driving),
        new("camera",     "Kamera wechseln",             "Change camera",        C, None,    Category.Driving),
        new("lights",     "Licht",                       "Headlights",           L, None,    Category.Driving),
        new("flash",      "Lichthupe",                   "Flash headlights",     F, None,    Category.Driving),
        new("blinkl",     "Blinker links",               "Indicator left",       Q, None,    Category.Driving),   // wie Unity (Q auch Runterschalten)
        new("blinkr",     "Blinker rechts",              "Indicator right",      Y, None,    Category.Driving),   // M ist die Karte
        new("hazard",     "Warnblinker",                 "Hazard lights",        X, None,    Category.Driving),
        new("reset",      "Zurück auf die Straße",       "Back on track",        R, None,    Category.Driving),
        new("gearbox",    "Automatik / Manuell",         "Gearbox mode",         G, None,    Category.Driving),
        new("aids",       "Fahrhilfen wechseln",         "Driving aids",         U, None,    Category.Driving),   // T ist der Chat
        new("minimap",    "GPS-Karte ein/aus",           "Minimap on/off",       J, None,    Category.Driving),
        new("map",        "Karte",                       "Map",                  M, None,    Category.Driving),
        new("chat",       "Chat",                        "Chat",                 T, None,    Category.Driving),
        // ---- Laufen / Avatar ("walkmode")
        new("walk_forward", "Vorwärts gehen",            "Move forward",         W, Up,      Category.Walk),
        new("walk_back",    "Rückwärts gehen",           "Move backward",        S, Down,    Category.Walk),
        new("walk_left",    "Nach links gehen",          "Move left",            A, Left,    Category.Walk),
        new("walk_right",   "Nach rechts gehen",         "Move right",           D, Right,   Category.Walk),
        new("walk_run",     "Rennen",                    "Run",                  Shift, None, Category.Walk),
        new("walk_use",     "Benutzen / Ansprechen",     "Use / Talk",           E, Return,  Category.Walk),
        new("walk_turnl",   "Drehen links",              "Turn left",            Q, None,    Category.Walk),
        new("walk_turnr",   "Drehen rechts",             "Turn right",           None, None, Category.Walk),
        // ---- Menü ("menu"): eine Bestätigungstaste für alle Menüs/Filme
        new("validate",   "Bestätigen",                  "Validate",             Return, None, Category.Menu),
        new("back",       "Zurück",                      "Back",                 Backspace, None, Category.Menu),
        new("menu_up",    "Menü hoch",                   "Menu up",              Up, W,      Category.Menu),
        new("menu_down",  "Menü runter",                 "Menu down",            Down, S,    Category.Menu),
        new("menu_left",  "Menü links",                  "Menu left",            Left, A,    Category.Menu),
        new("menu_right", "Menü rechts",                 "Menu right",           Right, D,   Category.Menu),
        new("menu_opt1",  "Option 1",                    "Option 1",             X, None,    Category.Menu),
        new("menu_opt2",  "Option 2",                    "Option 2",             Y, None,    Category.Menu),
        // ---- Autoradio (Unity TDU2Radio: Inspektor-Tasten Keypad5/4/6/8/2)
        new("radio_open", "Radio-Anzeige",               "Radio display",        Num5, None, Category.Driving),
        new("radio_prev", "Sender zurück",               "Previous station",     Num4, None, Category.Driving),
        new("radio_next", "Sender vor",                  "Next station",         Num6, None, Category.Driving),
        new("radio_volup",   "Radio lauter",             "Radio volume up",      Num8, None, Category.Driving),
        new("radio_voldown", "Radio leiser",             "Radio volume down",    Num2, None, Category.Driving),
        // ---- Entwickler (Testumschalter bis die Abläufe portiert sind)
        new("dev_menuctrl","Entwicklermenü: Steuerung umschalten (Menü/Fahren)", "Dev menu: toggle control (menu/drive)", Tab, None, Category.Menu),
        new("dev_car",     "Ein-/Aussteigen (Test)",     "Enter/exit car",       F2, None,   Category.Menu),
        new("dev_carnext", "Nächstes Auto (Test)",       "Next car",             F3, None,   Category.Menu),
        new("dev_carprev", "Voriges Auto (Test)",        "Previous car",         F4, None,   Category.Menu),
        new("dev_color",   "Nächste Farbe (Test)",       "Next color",           F8, None,   Category.Menu),
        new("dev_reload",  "Auto neu laden (Test)",      "Reload car",           F9, None,   Category.Menu),
        new("dev_login",   "Modus Login (Test)",         "Login mode",           F5, None,   Category.Menu),
        new("dev_freeride","Modus Freifahrt (Test)",     "Freeride mode",        F6, None,   Category.Menu),
        new("dev_map",     "Modus Karte (Test)",         "Map mode",             F7, None,   Category.Menu),
    };
}
