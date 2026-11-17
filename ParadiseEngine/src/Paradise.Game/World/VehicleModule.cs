using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;

using Paradise.Game.Input;

namespace Paradise.Game.World;

/// <summary>
/// Gameplay-Seite der Fahrzeuge (Tasten wie TDU2VehicleManager.HandleKeys/DriveInput im Unity-Projekt). Belegung: Game/Config/KeyConfig.cs
/// über TDU2Controls (Standard wie Unity: W/S Gas/Bremse, A/D lenken, Leertaste Handbremse, E/Q schalten, G Getriebe, U Fahrhilfen,
/// C Kamera, B Rückblick, R aufrichten, H Hupe, L Licht, F Lichthupe, Y Blinker rechts, X Warnblinker; Minimap J: GAME_MODE_FREERIDE).
/// Testtasten: F2 einsteigen/aussteigen, F3/F4 nächstes/voriges Auto, F8 Lackfarbe, F9 neu laden.
/// (F5–F7 belegt der ScreenModule-Test.) Laden/Physik/Zeichnen: Paradise.Core.Vehicles.VehicleManager.
/// </summary>
public sealed class VehicleModule : GameModule
{
    private readonly IRenderer _renderer;
    private readonly TerrainModule _terrain;
    private readonly IGameWindow _window;
    private readonly DebugOverlay? _overlay;
    private VehicleManager? _vm;
    private Paradise.Game.Gameplay.Traffic? _traffic;

    /// <summary>Verkehr (Gameplay/Traffic, Werte Game/Config/TrafficConfig.cs).</summary>
    public Paradise.Game.Gameplay.Traffic? Traffic => _traffic;

    public VehicleManager? Manager => _vm;
    /// <summary>Renderer (Zeichnen in den Spielmodi, z.B. Straßennetz der großen Karte).</summary>
    public IRenderer Renderer => _renderer;
    /// <summary>Unity TDU2VehicleManager.Suspended: während Login/Start keine Fahr-/Freiflugtasten (Tippen im Profil-Popup).</summary>
    public bool Suspended;
    /// <summary>Unity Time.timeScale = 0 (Pausemenü offen): Physik und Fahrzeug stehen, Kamera bleibt.</summary>
    public bool Frozen;

    public VehicleModule(IRenderer renderer, TerrainModule terrain, IGameWindow window, DebugOverlay? overlay = null)
    {
        _renderer = renderer;
        _terrain = terrain;
        _window = window;
        _overlay = overlay;
    }

    public override void Load()
    {
        VehicleCamera.PadLook = () => TDU2InputDevices.Look;   // Unity TDU2VehicleCamera: rechter Stick = Blickrichtung
        Paradise.Game.Screens.AccessoryMenu.HudSource = () => _vm?.Minimap?.Hud;   // Unity TDU2UIManager.Hud
        if (_terrain.Physics == null) { Log.Warn("VehicleModule: keine Physikwelt (Welt nicht geladen)."); return; }
        _vm = new VehicleManager(_renderer, _terrain.Physics);
        Paradise.Game.Config.GpsConfig.Apply();
        _vm.TerrainVisualLower = () => _terrain.Streamer?.TerrainLower ?? 0f;
        _vm.DiagContext = () =>
        {
            var s = _terrain.Streamer;
            return s == null ? "" : $"Kollider zuletzt eingetragen vor {(s.Time - s.LastColliderAddTime) * 1000:0} ms, Aufträge {s.ColliderJobs}";
        };
        _vm.IslandBank = () => _terrain.Streamer?.Index?.IslandBank;
        _vm.GroundHeight = p => _terrain.Streamer != null && _terrain.Streamer.TryGetGroundHeight(p, out float y) ? y : null;
        new VehicleConfig().ApplyTo(_vm);   // Werte: Game/Config/VehicleConfig.cs
        _vm.EnsureDatabase(GameData.BnkDir);
        _traffic = new Paradise.Game.Gameplay.Traffic(_renderer, _terrain, () => _vm);
        _overlay?.AddSection("Fahrzeug (Tasten: Game/Config/KeyConfig.cs – F2 ein-/aussteigen, F3/F4 Auto, F8 Farbe, F9 neu)", () => _vm.DebugLines());
    }

    public override void FixedUpdate(in GameTime time)
    {
        if (Frozen) return;
        _traffic?.FixedUpdate(time.FixedDeltaTime);   // KI + kinematische Körper vor dem Physikschritt
        _vm?.FixedUpdate(time.FixedDeltaTime);
    }

    public override void Update(in GameTime time)
    {
        DebugLines.Clear();   // Debug-Linien/Beschriftungen dieses Bildes (Verkehr, Entwicklermenü) neu sammeln
        if (_vm == null) return;
        _vm.Update();
        // Verkehr: neue Autos nur in der Freifahrt, wenn gefahren wird (nicht im Interieur, auf der Karte, in Kinos)
        bool trafficSpawn = !Suspended && !Frozen && Paradise.Game.Screens.GameMode.Current == Paradise.Game.Screens.Gamemode.GAME_MODE_FREERIDE
                            && !Paradise.Game.Gamemodes.InteriorUtils.Inside && !_terrain.MapView;
        if (!Frozen) _traffic?.Update(time.DeltaTime, trafficSpawn);
        if (_traffic != null) _traffic.Alpha = time.FixedAlpha;   // Darstellung zwischen den Physikschritten
        if (!_terrain.MapView) _traffic?.DrawDebug();   // TrafficConfig.ShowDebug (F1 > Traffic)
        TDU2Controls.Update(_window.Input, time.FrameCount);   // Tastenbelegung (Game/Config/KeyConfig.cs, Unity TDU2Controls)
        if (Suspended || Frozen)
        {
            _terrain.FlyCameraEnabled = false;
            _vm?.UpdateGps(time.DeltaTime);   // Unity: TDU2Gps läuft auch bei angehaltenem Spiel (Route für die große Karte)
            return;
        }
        bool f2 = TDU2Controls.Pressed("dev_car"), f3 = TDU2Controls.Pressed("dev_carnext"), f4 = TDU2Controls.Pressed("dev_carprev"),
             f8 = TDU2Controls.Pressed("dev_color"), f9 = TDU2Controls.Pressed("dev_reload");
        var cam = _terrain.Camera; var s = _terrain.Streamer;
        if (_vm.Ready && cam != null && s != null)
        {
            if (f2)
            {
                if (_vm.Current != null || _vm.Model != null) _vm.Despawn();
                else _vm.SpawnAt(s.RenderToGame(cam.Position), cam.Forward);
            }
            if (f3) _vm.SwitchCar(+1);
            if (f4) _vm.SwitchCar(-1);
            if (f8) _vm.NextColor();
            if (f9) _vm.SwitchCar(0);
        }

        var v = _vm.Current;
        _terrain.FlyCameraEnabled = v == null || _terrain.FreeFly;
        s?.ExtraLamps.Clear();
        if (v == null) return;
        if (_terrain.FreeFly)
        {
            // Freiflug-Kamera (Entwicklermenü F1, Unity TDU2FlyCameraTool): Auto ohne Eingabe mit Handbremse, Kamera bleibt frei
            _vm.DriveInput(0f, 0f, 0f, true, time.DeltaTime, false, false);
            _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
            if (s != null) _vm.CollectLamps(s.ExtraLamps, s.RootMatrix);
            return;
        }

        // DriveInput (Unity TDU2VehicleManager: TDU2Controls "accel"/"brake"/"left"/"right"/"handbrake")
        float accel = TDU2Controls.Held("accel") ? 1f : 0f;
        float brake = TDU2Controls.Held("brake") ? 1f : 0f;
        float steer = 0f;
        if (TDU2Controls.Held("left")) steer -= 1f;
        if (TDU2Controls.Held("right")) steer += 1f;
        // Gamepad/Lenkrad (TDU2InputDevices: Geräteprofil aus StreamingAssets, Pedale kalibriert, Optionen Totzone/Linearität)
        bool analog = false; float devSteer = 0f;
        if (!TDU2Controls.Blocked && TDU2InputDevices.HasDevice)
        {
            devSteer = TDU2InputDevices.Steer;
            float t = TDU2InputDevices.Throttle, b = TDU2InputDevices.Brake;
            if (MathF.Abs(steer) < 0.01f && MathF.Abs(devSteer) > 0.001f) { steer = devSteer; analog = true; }
            if (accel < 0.01f && t > 0f) accel = t;
            if (brake < 0.01f && b > 0f) brake = b;
        }
        _vm.DriveInput(accel, brake, steer, TDU2Controls.Held("handbrake"), time.DeltaTime, analog, analog && TDU2InputDevices.IsWheel);

        // HandleKeys (Unity TDU2VehicleManager.HandleKeys)
        if (TDU2Controls.Pressed("camera")) _vm.Camera.NextMode();
        _vm.Camera.LookBack = TDU2Controls.Held("lookback");
        if (TDU2Controls.Pressed("aids")) { _vm.Aids = (DrivingAids)(((int)_vm.Aids + 1) % 4); v.Aids = _vm.Aids; }
        if (TDU2Controls.Pressed("gearbox")) { _vm.AutomaticGearbox = !_vm.AutomaticGearbox; v.AutomaticGearbox = _vm.AutomaticGearbox; }
        if (TDU2Controls.Pressed("gearup")) v.ShiftUp();
        if (TDU2Controls.Pressed("geardown")) v.ShiftDown();
        if (TDU2Controls.Pressed("reset")) _vm.ResetUpright();
        _vm.Audio?.SetHorn(TDU2Controls.Held("horn"));
        var lights = _vm.Lights;
        if (lights != null)
        {
            if (TDU2Controls.Pressed("lights")) lights.Headlights = (lights.Headlights + 1) % 3;
            if (TDU2Controls.Pressed("blinkl")) { lights.IndicatorLeft = !lights.IndicatorLeft; lights.IndicatorRight = false; }
            if (TDU2Controls.Pressed("blinkr")) { lights.IndicatorRight = !lights.IndicatorRight; lights.IndicatorLeft = false; }
            if (TDU2Controls.Pressed("hazard")) lights.Hazard = !lights.Hazard;
            lights.Flash = TDU2Controls.Held("flash");
        }

        // LateUpdate: Räder/Lenkrad + Fahrzeugkamera -> Engine-Kamera (Render-Raum)
        _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
        if (cam != null && s != null)
        {
            var (pos, fwd, up) = _vm.CameraRender(s.RootMatrix);   // ohne Umweg über große Spielkoordinaten (Genauigkeit)
            cam.Position = pos;
            cam.Override = (fwd, up);
            _terrain.FieldOfView = _vm.Camera.fieldOfView;
            _terrain.NearClip = _vm.Camera.nearClipPlane;
            _vm.CollectLamps(s.ExtraLamps, s.RootMatrix);   // Scheinwerferkegel, Brems-/Rückfahrlicht
            _traffic?.CollectLamps(_vm, s.ExtraLamps, s.RootMatrix);
        }
    }

    public override void Render(in GameTime time)
    {
        var s = _terrain.Streamer;
        if (_vm == null || s == null) return;
        if (_terrain.MapTerrainShown) return;   // Unity: Kartenkamera zeichnet dann nur die Kartenebene
        _vm.Render(s.RootMatrix);
        if (!s.HideWorld) _traffic?.Render(_vm, s.RootMatrix);
        // Debug-Linien (Core/Render/DebugLines, Unity TDU2DebugLines): Verkehr, Radstrahlen des Autos (F1)
        var cam = _terrain.Camera;
        if (cam != null && _renderer.Height > 0)
            DebugLines.Flush(_renderer, s.RootMatrix, cam.Position, 2f * MathF.Tan(_terrain.FieldOfView * (MathF.PI / 360f)) / _renderer.Height);
    }

    public override void Unload()
    {
        _traffic?.Dispose();
        _traffic = null;
        _vm?.Dispose();
        _vm = null;
    }
}
