using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Flughafen (Spots EAIR_*, Interieur iair_*; Port aus der Original-exe, Klasse mit VTable 0x14FDAF0, Code 0xBF2C20–0xBF38xx).
///   Init (0xBF2EC0): Cinematics C_TAKE_OFF (Exterior-Bank des Spots), C_FIRST_TAKE_OFF (nur Erstflug), C_BUY_TICKET (Interieur).
///   Start (0xBF3050): Oberfläche "AIRPORT" (frontend/&lt;res&gt;/airport.bnk), _root.destination = andere Insel (0 -> 1, 1 -> 0),
///     _root.TicketIsSold = false, Label "airport" (-> "airport_wm": Laufmodus, Titel, camf).
///   Film: Hostess (Auswahlpunkt Typ 32, SelItemType "npc_airport_hostess") "valid" -> camn + "ai_open" (Flugauswahl),
///     A -> DBXO "buy_ticket" (m_TakeFlight) -> BuyTicket -> CloseAirport -> "close" -> Bild 65: FSCommand "flok";
///     B in der Flugauswahl -> CloseAirport ohne valid -> BackToWM (wmon); B im Laufmodus -> DBXO "quit" -> "back_fr" -> "gofr".
///   flok (0xBF31D0): C_BUY_TICKET abspielen, Oberfläche zu. Kino-Ende (0xBF34F0, Ereignis 9): nach C_BUY_TICKET -> C_TAKE_OFF,
///     nach C_TAKE_OFF -> Inselwechsel (0x41300C) = <see cref="IslandTravel.Go"/> mit Ankunft am Flughafen der Zielinsel.
///   Preis: der Flughafen-Code zieht kein Geld ab (TicketIsSold bleibt false: im Film nur die Geld-Animation "bought").
///   Ankunft: Exterior-Bank des Ankunfts-Flughafens hat C_LANDED (exe-Name 0x14FD1AC, Kino-Auswahl 0xBE9A10) – Landung mit Flugzeug,
///     Spielerauto und Avatar; danach steht das Auto am Spot (Position2) und es geht in die Freifahrt.
/// Nicht portiert (CHECKLIST): Erstflug (Flags 0x1AE973E/0x1723184 beide 0: C_FIRST_INTRO/ISL_firstintro, C_FIRST_TAKE_OFF),
///   camn/camf (Kamera auf die Hostess, Ziel im Exe nicht gefunden), Requisiten (Ticket, Flugzeug) und Ton der Cinematics.
/// </summary>
public class GAME_MODE_AIRPORT : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_AIRPORT;
    static readonly ScreenDef[] _screens = { ScreenDefs.Airport };
    // Landung (C_LANDED) läuft draußen ohne Flughafen-Oberfläche
    public override IReadOnlyList<ScreenDef> Screens => _phase == Phase.Landed ? Array.Empty<ScreenDef>() : _screens;
    protected override ScreenDef Screen => ScreenDefs.Airport;

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        if (_phase == Phase.Landed) { _ctx = ctx; return; }
        base.Enter(ctx, from);
    }

    enum Phase { Walk, BuyTicket, TakeOff, Landed }
    static Phase _phase;
    static bool _hooked, _menu;
    static string _destination = "hawai";
    static SpotCinematics? _intCin, _extCin;

    /// <summary>Cinematic läuft: kein Laufen/Umsehen, Kamera vom Kino (InteriorUtils.UpdateInside).</summary>
    internal static bool CinematicActive => _phase != Phase.Walk;
    /// <summary>Flugauswahl (ai_open) offen: Maus frei, Tasten an den Film.</summary>
    internal static bool MenuOpen => _menu;

    // ================================================================== Raum-Modus
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        if (_phase == Phase.Landed) { if (!ctx.Paused) UpdateLanded(time.DeltaTime); return; }
        if (_phase == Phase.Walk && !_menu)
        {
            // Laufmodus: Rücktaste = B() des Films ("airport_wm": DBXO quit "m_QuitAirport"), ESC bleibt Pausemenü
            if (!ctx.Paused && _ui != null && !FlashDialog.Active && TDU2Controls.BackPressed()) FlashNav.Send(_ui, FlashKey.Back);
            base.Run(ctx, time);
            return;
        }
        _ctx = ctx;
        if (_t == null || !UpdateInside(time.DeltaTime, ctx.Paused)) { EndCinematics(); GameMode.Set(Gamemode.GAME_MODE_FREERIDE); return; }
        if (ctx.Paused) return;
        if (_phase != Phase.Walk) UpdateCinematics(time.DeltaTime);
        else UpdateMenu();
    }

    /// <summary>Flugauswahl: Pfeile/A/B an den Film (A -> DBXO buy_ticket, B -> CloseAirport -> wmon).</summary>
    internal static void UpdateMenu()
    {
        var ui = _ui;
        if (!_menu || ui == null || FlashDialog.Active) return;
        FlashNav.Directions(ui);
        if (FlashNav.Pressed(FlashKey.Confirm)) FlashNav.Send(ui, FlashKey.Confirm);
        if (FlashNav.Pressed(FlashKey.Back)) FlashNav.Send(ui, FlashKey.Back);
    }

    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        if (_phase != Phase.Landed) base.Render(ctx, time);
        var st = _t?.Streamer;
        if (st == null) return;
        _intCin?.Render(st.RootMatrix);
        _extCin?.Render(st.RootMatrix);
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to != Gamemode.GAME_MODE_PAUSE) EndCinematics();
        base.Exit(ctx, to);
    }

    // ================================================================== Oberfläche (0xBF30FC–0xBF31AF)
    /// <summary>Flughafen betreten: Ziel, Ticket, Film ab "airport" (Laufmodus "airport_wm").</summary>
    internal static void LoadAirportUi()
    {
        var ui = _ui; if (ui == null) return;
        if (!_hooked) { ui.FSCommand += OnCommand; _hooked = true; }
        _phase = Phase.Walk; _menu = false;
        FlashLocalization.Apply(ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language));
        ui.MouseEnabled = false;
        ui.SetValue("/:fake", false);
        // exe: [0x16E0A48] aktuelle Insel 0 -> Ziel 1, 1 -> 0; Name (0x40CD24) = Label im Clip "destination" ("ibiza"/"hawai")
        _destination = IslandTravel.OtherIsland(_t.Streamer?.Index?.IslandName);
        ui.SetVariable("/:destination", _destination);
        ui.SetValue("/:TicketIsSold", false);
        ui.GotoLabel("/", "airport", true);
        ResetUiAim();
        LoadCinematics();
        Log.Info("Flughafen: Oberfläche airport, Ziel " + IslandTravel.DisplayName(_destination));
    }

    /// <summary>Hostess (Typ 32) bestätigt: wie im Spiel _root.cursor.ItemAction("valid") -> ItemType -> camn + "ai_open".</summary>
    internal static void TalkToHostess()
    {
        var ui = _ui; if (ui == null) return;
        ui.SetVariable("/:SelItemType", "npc_airport_hostess");
        if (!ui.CallClip("/cursor", "ItemAction", "valid")) { Log.Warn("Flughafen: cursor.ItemAction fehlt im Film"); return; }
        _uiAim = null;
        _menu = true;
        ui.MouseEnabled = true;
    }

    // ================================================================== Befehle des Films
    static void OnCommand(string cmd, string args)
    {
        if (_kind != Kind.Airport || _ui == null) return;
        var ui = _ui;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        switch (c)
        {
            case "dbxo": FlashDialog.Open(ui); return;                    // buy_ticket / quit (general.bnk), Antwort an _root.DialogBox
            case "flok": BuyTicket(); return;                              // Bild 65: Flug bestätigt (0xBF31D0)
            case "gofr": Leave(); return;                                  // quit "Ja" -> back_fr -> Flughafen verlassen
            case "wmon": _menu = false; ui.MouseEnabled = false; return;   // BackToWM: zurück in den Laufmodus
            case "camn":
            case "camf":
                Log.NotImplemented("Flughafen: " + c + " (Kamera Hostess/frei – nicht portiert)"); return;
        }
    }

    static void Leave()
    {
        _menu = false;
        EndCinematics();
        ExitInterior(_lastCar);
    }

    // ================================================================== Cinematics
    static string? _cinBank;

    static void LoadCinematics()
    {
        if (_intCin != null && _cinBank == _pendingBank) return;   // LoadUi läuft beim Aufbau und beim Moduswechsel
        EndCinematics();
        _cinBank = _pendingBank;
        if (_spot == null || _v?.Renderer == null) return;
        var frameInt = _holder;
        // Interieur: C_BUY_TICKET (Bank des Raums, schon geladen); Exterior: C_TAKE_OFF (islands/<insel>/exterior/<spot>.bnk)
        if (_pendingBank != null) _intCin = SpotCinematics.Open(_v.Renderer, _pendingBank);
        if (_intCin != null) { _intCin.Frame = frameInt; _intCin.Indoor = true; }
        _extCin = OpenExterior(_spot);
        if (_extCin != null && Vm != null) _extCin.LoadCar(Vm, _lastCar);   // PLAYER_CAR in C_TAKE_OFF (Auffahrt zum Flugzeug)
    }

    /// <summary>Cinematics der Exterior-Bank eines Spots (Rahmen wie das gestreamte Exterior), mit Requisiten (CF_Plane).</summary>
    static SpotCinematics? OpenExterior(Spot s) => SpotCinematics.OpenExterior(_v.Renderer, _t.Streamer, s);

    /// <summary>flok: C_BUY_TICKET (falls vorhanden), Oberfläche zu; danach C_TAKE_OFF.</summary>
    static void BuyTicket()
    {
        _menu = false;
        FlashDialog.Cancel();
        if (_ui != null) { _ui.MouseEnabled = false; foreach (var d in _ui.Root.List.Values) d.Visible = false; }
        UpdateUiAim(null);
        if (_intCin != null && _intCin.PlayMain("C_BUY_TICKET")) { _phase = Phase.BuyTicket; return; }
        TakeOff();
    }

    /// <summary>
    /// Ende C_BUY_TICKET (0xBF351E): Modus-Aufruf [vtbl+0x54], dann C_TAKE_OFF. C_TAKE_OFF spielt draußen am Flugfeld (Exterior-Bank):
    /// Interieur-Szene weg, Exterior wieder vollständig, Außenlicht.
    /// </summary>
    static void TakeOff()
    {
        _intCin?.StopMain();
        InteriorNpcs.Suppressed = null;
        if (_extCin != null && _extCin.Find("C_TAKE_OFF") != null)
        {
            ClearScene();
            Paradise.Core.World.WorldWeather.Inside = false;
            _extCin.PlayMain("C_TAKE_OFF");
            _phase = Phase.TakeOff;
            return;
        }
        Fly();
    }

    static void UpdateCinematics(float dt)
    {
        var cin = _phase == Phase.TakeOff ? _extCin : _intCin;
        if (cin == null) { Fly(); return; }
        cin.Update(dt);
        InteriorNpcs.Suppressed = _phase == Phase.BuyTicket ? cin.ActiveActors : null;   // Figur spielt im Kino, nicht zusätzlich im Laufmodus
        foreach (var n in cin.TakeEndEvents())
        {
            if (_phase == Phase.BuyTicket && n == "C_BUY_TICKET") { TakeOff(); return; }
            if (_phase == Phase.TakeOff && n == "C_TAKE_OFF") { Fly(); return; }
        }
        ApplyCinemaCamera(cin);
    }

    static void ApplyCinemaCamera(SpotCinematics cin)
    {
        if (!cin.Camera(out var eye, out var fwd, out var up, out float fov)) return;
        var cam = _t.Camera; var st = _t.Streamer;
        if (cam == null || st == null) return;
        _t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (fwd, up);
        _t.FieldOfView = fov;
        _t.NearClip = 0.1f;
    }

    // ================================================================== Ankunft (C_LANDED)
    static string? _landCar; static Vector3 _landPos, _landDir; static int _landColor;

    /// <summary>
    /// Inselwechsel per Flug ist am Ziel angekommen (IslandTravel, Welt um den Spot geladen): C_LANDED des Ankunfts-Flughafens spielen,
    /// danach das Auto an die Ankunftsstelle. false = kein C_LANDED (dann setzt IslandTravel das Auto selbst).
    /// </summary>
    internal static bool BeginLanded(IGameModeContext ctx, Spot arrival, string? carFile, Vector3 pos, Vector3 dir, int color)
    {
        if (!Init(ctx) || _v.Renderer == null) return false;
        EndCinematics();
        var c = OpenExterior(arrival);
        if (c == null || c.Find("C_LANDED") == null) { c?.Dispose(); return false; }
        _extCin = c;
        _landCar = carFile; _landPos = pos; _landDir = dir; _landColor = color;
        if (Vm != null && carFile != null) c.LoadCar(Vm, Vm.FindCar(carFile));
        c.PlayMain("C_LANDED");
        _phase = Phase.Landed;
        _t.Input.CursorLocked = true;
        GameMode.Set(Gamemode.GAME_MODE_AIRPORT);
        Log.Info("Flughafen: Ankunft " + arrival.Code + " – C_LANDED");
        return true;
    }

    static void UpdateLanded(float dt)
    {
        var cin = _extCin;
        if (cin == null) { FinishLanded(); return; }
        cin.Update(dt);
        foreach (var n in cin.TakeEndEvents()) if (n == "C_LANDED") { FinishLanded(); return; }
        ApplyCinemaCamera(cin);
    }

    static void FinishLanded()
    {
        string? car = _landCar;
        EndCinematics();
        var vm = Vm;
        _v.Suspended = false;
        _t.Input.CursorLocked = false;
        _t.FlyCameraEnabled = true;
        if (vm != null && car != null && !vm.SpawnCar(car, _landPos, _landDir, _landColor)) Log.Warn("Flughafen: Auto " + car + " nach der Landung nicht spawnbar");
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Ende C_TAKE_OFF (exe 0x41300C): Inselwechsel, Ankunft am Flughafen der Zielinsel, Auto kommt mit.</summary>
    static void Fly()
    {
        var car = _lastCar;
        string dest = _destination;
        EndCinematics();
        Log.Info("Flughafen: Flug nach " + IslandTravel.DisplayName(dest));
        LeaveForTravel();   // ohne Auto am Abflugort: der Inselwechsel (Ladebildschirm) setzt es erst am Ziel
        IslandTravel.Go(dest, true, car?.FileName, "EAIR");
    }

    static void EndCinematics()
    {
        _phase = Phase.Walk; _menu = false;
        InteriorNpcs.Suppressed = null;
        _intCin?.Dispose(); _intCin = null;
        _extCin?.Dispose(); _extCin = null;
        _cinBank = null;
    }
}
