using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using Paradise.Game.World;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Inselwechsel (Port Unity TDU2IslandTravel): Flug am Flughafen / Landeplatz, Entwicklermenü (F1 > World Loading).
///  - Spielstand bekommt die Zielinsel (Flug: Position verworfen -> Ankunft am Spot EAIR* bzw. ELAND* der Zielinsel), wird gespeichert.
///  - Unity lädt die Szene mit TDU2MapStreamer.IslandOverride neu; hier baut <see cref="TerrainModule.ChangeIsland"/> den
///    MapStreamer der Zielinsel neu auf (Physikwelt, Kamera und Fahrzeugmodul bleiben).
///  - Original-Ladebildschirm (TDU2LoadingScreen) bis die Welt an der Ankunftsstelle steht und das Auto gesetzt ist.
/// Jedes Bild aus ScreenModule.Update (<see cref="Update"/>).
/// </summary>
public static class IslandTravel
{
    /// <summary>Wechsel läuft.</summary>
    public static bool Pending { get; private set; }
    /// <summary>Flug: Ankunft am Flughafen-Spot (EAIR*) der Zielinsel statt an der gespeicherten Position.</summary>
    public static bool Arriving { get; private set; }
    public static string? Target { get; private set; }
    /// <summary>Spot-Präfix des Ankunftsorts beim Flug (EAIR = Flughafen, ELAND = Landeplatz Casino).</summary>
    public static string ArrivalPrefix { get; private set; } = "EAIR";

    enum St { Fade, Load, Spawn }
    static St _st;
    static float _t;
    static string? _car;
    static Vector3 _game, _dir;
    static Spot? _arrival;   // Ankunfts-Spot (Flug)
    static bool _keep;   // ohne Flug: Auto an der bisherigen Stelle (gleiche Insel neu laden / Spielstand mit Position)
    const float SpawnTimeout = 30f;

    /// <summary>Inselordner (Bnk/islands/&lt;name&gt;) vorhanden?</summary>
    public static bool CanTravelTo(string? island, string? bnkDir)
    {
        if (string.IsNullOrEmpty(island) || string.IsNullOrEmpty(bnkDir)) return false;
        try { return Paradise.Core.Files.GameFiles.DirectoryExists(Path.Combine(bnkDir, "islands", island.ToLowerInvariant())); } catch (Exception) { return false; }
    }

    public static string DisplayName(string? island) =>
        string.IsNullOrEmpty(island) ? "?" : island.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? "Hawaii" : island.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? "Ibiza"
        : island.StartsWith("cas", StringComparison.OrdinalIgnoreCase) ? "Casino" : island;

    /// <summary>Andere Insel zum Flug (Ibiza ↔ Hawaii).</summary>
    public static string OtherIsland(string? current) =>
        !string.IsNullOrEmpty(current) && current.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? "ibiza" : "hawai";

    /// <summary>Wechsel starten. flight = true: Ankunft am Flughafen (Position im Spielstand wird verworfen).</summary>
    public static void Go(string island, bool flight, string? carFile = null, string arrivalPrefix = "EAIR")
    {
        if (Pending || string.IsNullOrEmpty(island)) return;
        Target = island.ToLowerInvariant();
        ArrivalPrefix = string.IsNullOrEmpty(arrivalPrefix) ? "EAIR" : arrivalPrefix;
        Pending = true;
        Arriving = flight;
        _car = string.IsNullOrEmpty(carFile) ? null : carFile;
        var save = TDU2Session.Save;
        if (save != null)
        {
            save.island = Target;
            if (flight) save.hasPosition = false;
            if (_car != null) save.currentCar = _car;
            if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
        }
        Log.Info("Inselwechsel nach " + DisplayName(Target) + (flight ? " (Flug)" : " (Spielstand)") + ", Auto " + (_car ?? save?.currentCar ?? "-"));
        // Ladebildschirm bis zur fertigen Welt auf der Zielinsel (gespeichert wird oben schon)
        LoadingScreen.Show("Inselwechsel nach " + DisplayName(Target), () => !Pending && LoadingScreen.WorldReady(), 2f, false);
        _st = St.Fade; _t = 0f;
    }

    public static void Update(IGameModeContext ctx, float dt)
    {
        if (!Pending) return;
        _t += dt;
        var terrain = ctx.Terrain; var vehicles = ctx.Vehicles; var vm = vehicles?.Manager;
        if (terrain == null) { Done(); return; }
        switch (_st)
        {
            case St.Fade:
                {
                    if (_t < 1.2f) return;   // Unity Reload: 1,2 s Überblendung, Speichern anstoßen
                    _car ??= vm?.CurrentCar?.FileName ?? TDU2Session.Save?.currentCar;
                    _keep = false;
                    var save0 = TDU2Session.Save;
                    if (!Arriving && vm?.Current != null && string.Equals(terrain.Streamer?.Island, Target, StringComparison.OrdinalIgnoreCase))
                    {
                        _game = vm.PositionGame; _dir = vm.ForwardGame; _dir.Y = 0f; _keep = true;
                    }
                    else if (!Arriving && save0 != null && save0.hasPosition)
                    {
                        _game = new Vector3(save0.px, save0.py, save0.pz);
                        float h = save0.heading * (MathF.PI / 180f);
                        _dir = new Vector3(MathF.Sin(h), 0f, MathF.Cos(h)); _keep = true;
                    }
                    if (_keep) _dir = _dir.LengthSquared() > 1e-4f ? Vector3.Normalize(_dir) : Vector3.UnitZ;
                    if (vm != null && (vm.Current != null || vm.Model != null)) vm.Despawn();
                    if (vehicles != null) vehicles.Suspended = true;
                    if (!terrain.ChangeIsland(Target!))
                    {
                        Log.Warn("Inselwechsel: Insel " + Target + " nicht ladbar");
                        if (vehicles != null) vehicles.Suspended = false;
                        Done();
                        return;
                    }
                    _st = St.Load; _t = 0f;
                    return;
                }
            case St.Load:
                {
                    var s = terrain.Streamer;
                    if (s == null) { Done(); return; }
                    if (_keep) { _st = St.Spawn; _t = 0f; PlaceCamera(terrain); return; }
                    if (s.Spots == null && _t < SpawnTimeout) return;
                    Spot? arrival = null;
                    if (s.Spots != null)
                        foreach (var sp in s.Spots.Spots)
                            if (sp.Code.StartsWith(ArrivalPrefix, StringComparison.OrdinalIgnoreCase)) { arrival = sp; break; }
                    _arrival = arrival;
                    if (arrival != null) { _game = SpotPos(arrival); _dir = SpotDir(arrival); }
                    else
                    {
                        _game = s.RenderToGame(s.StartPosition); _dir = Vector3.UnitZ;
                        Log.Warn("Inselwechsel: kein Ankunfts-Spot " + ArrivalPrefix + "* auf " + Target + " – Start an der Inselmitte");
                    }
                    _st = St.Spawn; _t = 0f;
                    PlaceCamera(terrain);
                    return;
                }
            case St.Spawn:
                {
                    PlaceCamera(terrain);
                    var s = terrain.Streamer;
                    if (s == null) { Done(); return; }
                    bool ground = s.RaycastDown(_game + Vector3.UnitY * 60f, 0f, 200f, out _);
                    if ((!ground || _t < 0.5f) && _t < SpawnTimeout) return;
                    if (!ground) Log.Warn($"Inselwechsel: Boden an der Ankunft nach {SpawnTimeout:0} s nicht geladen – setze trotzdem");
                    var save = TDU2Session.Save;
                    int color = -1;
                    if (save?.carConfigs != null && _car != null)
                        foreach (var e in save.carConfigs) if (e != null && string.Equals(e.car, _car, StringComparison.OrdinalIgnoreCase)) color = Math.Max(0, e.color);
                    // Flug zum Flughafen: Landung C_LANDED des Ankunfts-Flughafens, das Auto setzt GAME_MODE_AIRPORT danach
                    if (Arriving && _arrival != null && ArrivalPrefix == "EAIR" && GAME_MODE_AIRPORT.BeginLanded(ctx, _arrival, _car, _game, _dir, color))
                    {
                        if (save != null && _car != null) TDU2Session.SetCurrentCar(_car);
                        SessionAutosave.Schedule(5f);
                        Done();
                        return;
                    }
                    if (vm != null && _car != null && vm.SpawnCar(_car, _game, _dir, color)) { if (save != null) TDU2Session.SetCurrentCar(_car); }
                    else Log.Warn("Inselwechsel: Auto " + (_car ?? "-") + " nicht spawnbar");
                    if (vehicles != null) vehicles.Suspended = false;
                    SessionAutosave.Schedule(5f);
                    GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
                    Done();
                    return;
                }
        }
    }

    static void Done()
    {
        Pending = false; Arriving = false; ArrivalPrefix = "EAIR"; _car = null; _keep = false; _arrival = null;
    }

    static Vector3 SpotPos(Spot s) => s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
    static Vector3 SpotDir(Spot s) => new(MathF.Sin(s.Heading2), 0f, MathF.Cos(s.Heading2));

    /// <summary>Freiflug-Kamera hinter die Ankunftsstelle – das Streaming lädt um die Kamera herum (wie GAME_MODE_LOGIN.PlaceCamera).</summary>
    static void PlaceCamera(TerrainModule terrain)
    {
        var cam = terrain.Camera; var s = terrain.Streamer;
        if (cam == null || s == null) return;
        cam.Override = null;
        cam.Position = s.GameToRender(_game - _dir * 7f + Vector3.UnitY * 3f);
        cam.Yaw = MathF.Atan2(_dir.X, _dir.Z);
        cam.Pitch = -0.15f;
    }
}
