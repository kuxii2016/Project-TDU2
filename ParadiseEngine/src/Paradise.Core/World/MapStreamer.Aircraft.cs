using Paradise.Core.Diagnostics;
using Paradise.Core.Render;
using System.Globalization;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Integration der Flugzeuge in den MapStreamer. Alles läuft in Spielkoordinaten; das Zeichnen benutzt
/// dieselbe RootMatrix wie die übrige Welt und ist damit world-shift-sicher.
/// </summary>
public sealed partial class MapStreamer
{
    public bool ShowAircraft = true;
    public bool LogAircraft = false;
    public bool ParkAircraft = false;
    /// <summary>-aircraftspeed: Skalierung der Flugzeit (nur für Tests/Headless).</summary>
    public float AircraftSpeedScale = 1f;

    AircraftSystem? _aircraft;
    int _drawnAircraft;

    public int AircraftCount => _aircraft?.PlaneCount ?? 0;
    public int AircraftRouteCount => _aircraft?.RouteCount ?? 0;
    public int AircraftGroundCount => _aircraft?.GroundCount ?? 0;
    public int AircraftDrawn => _drawnAircraft;
    public int TakeAircraftEvents() => _aircraft?.TakeEvents() ?? 0;

    void LoadAircraft()
    {
        _aircraft = null;
        _drawnAircraft = 0;
        if (_index == null || string.IsNullOrEmpty(_index.IslandDir)) return;

        // Die Schalter der C++-Version, ohne Eingriff in die LaunchOptions (unbekannte args werden ignoriert).
        string[] args = Environment.GetCommandLineArgs();
        bool disabled = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "-noaircraft", StringComparison.OrdinalIgnoreCase)) disabled = true;
            else if (string.Equals(a, "-logaircraft", StringComparison.OrdinalIgnoreCase)) LogAircraft = true;
            else if (string.Equals(a, "-aircraftpark", StringComparison.OrdinalIgnoreCase)) ParkAircraft = true;
            else if (string.Equals(a, "-aircraftspeed", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
                AircraftSpeedScale = MathF.Max(1f, scale);
        }
        if (disabled) return;

        try
        {
            var system = new AircraftSystem();
            if (!system.Load(_index.IslandDir, (float x, float z, out float y) => TryGetMapGroundHeight(x, z, out y),
                             LogAircraft, ParkAircraft)) return;
            _aircraft = system;
        }
        catch (Exception ex) { Log.Warn("Flugzeuge konnten nicht geladen werden: " + ex.Message); }
    }

    /// <summary>Nach PollLibrary: Library-Nodes binden und die Flugzeuge einen Schritt weiterbewegen.</summary>
    void PollAircraft(float dt)
    {
        if (_aircraft == null) return;
        if (!_aircraft.IsBound)
        {
            if (_library == null) return;   // commonworld.bnk noch nicht fertig
            _aircraft.Bind(_library, _textures, _opt, _renderer);
            Log.Info($"Flugzeuge: {_aircraft.ModelCount} Modelle aus der commonworld-Library gebunden");
        }
        // Positionen bleiben in Spielkoordinaten; RootMatrix/RootOffset werden erst beim Zeichnen angewandt.
        _aircraft.Tick(dt * AircraftSpeedScale);
    }

    void DrawAircraft(IRenderer renderer, Vector3 cameraGame, Matrix4x4 root, Vector4[] frustum)
    {
        _drawnAircraft = 0;
        if (!ShowAircraft || _aircraft == null) return;
        _drawnAircraft = _aircraft.Draw(renderer, cameraGame, root, frustum);
    }

    void DisposeAircraft()
    {
        _aircraft?.Unload();
        _aircraft = null;
        _drawnAircraft = 0;
    }
}
