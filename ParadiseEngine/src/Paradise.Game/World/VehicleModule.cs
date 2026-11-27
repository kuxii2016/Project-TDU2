using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;

using Paradise.Game.Input;

namespace Paradise.Game.World;

public sealed class VehicleModule : GameModule
{
    private readonly IRenderer _renderer;
    private readonly TerrainModule _terrain;
    private readonly IGameWindow _window;
    private readonly DebugOverlay? _overlay;
    private VehicleManager? _vm;
    private Paradise.Game.Gameplay.Traffic? _traffic;
    public Paradise.Game.Gameplay.Traffic? Traffic => _traffic;
    public VehicleManager? Manager => _vm;
    public IRenderer Renderer => _renderer;
    public bool Suspended;
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
        VehicleCamera.PadLook = () => TDU2InputDevices.Look;
        Paradise.Game.Screens.AccessoryMenu.HudSource = () => _vm?.Minimap?.Hud;
        if (_terrain.Physics == null) { Log.Warn("VehicleModule: No physics world (world not loaded)."); return; }
        _vm = new VehicleManager(_renderer, _terrain.Physics);
        Paradise.Game.Config.GpsConfig.Apply();
        _vm.TerrainVisualLower = () => _terrain.Streamer?.TerrainLower ?? 0f;
        _vm.DiagContext = () =>
        {
            var s = _terrain.Streamer;
            return s == null ? "" : $"Collider last added {(s.Time - s.LastColliderAddTime) * 1000:0} ms ago, jobs {s.ColliderJobs}";
        };
        _vm.IslandBank = () => _terrain.Streamer?.Index?.IslandBank;
        _vm.GroundHeight = p => _terrain.Streamer != null && _terrain.Streamer.TryGetGroundHeight(p, out float y) ? y : null;
        new VehicleConfig().ApplyTo(_vm);   // Werte: Game/Config/VehicleConfig.cs
        _vm.EnsureDatabase(GameData.BnkDir);
        _traffic = new Paradise.Game.Gameplay.Traffic(_renderer, _terrain, () => _vm);
        Paradise.Game.Racing.RaceOpponents.Renderer = _renderer;   // Gegner im Rennen (Racing/RaceOpponents)
        Paradise.Game.Racing.RaceOpponents.World = _terrain.Physics;
        _overlay?.AddSection("Vehicle, keys F2 enter or exit, F3 F4 car, F8 color, F9 reset", () => _vm.DebugLines());
        _overlay?.AddSection("Controller / Wheel / Force Feedback", () => new[] { TDU2ForceFeedback.DebugLines() });
    }


    static readonly ProfilerMarker s_mTrafficFixed = new("Traffic.FixedUpdate", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mRemoteFixed = new("RemoteCars.FixedUpdate", ProfilerCategory.Network);
    static readonly ProfilerMarker s_mOpponentsFixed = new("RaceOpponents.FixedUpdate", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mVmFixed = new("VehicleManager.FixedUpdate", ProfilerCategory.Physics);
    static readonly ProfilerMarker s_mVmUpdate = new("VehicleManager.Update", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mTraffic = new("Traffic.Update", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mVmRender = new("VehicleManager.Render", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mTrafficRender = new("Traffic.Render", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mRemoteRender = new("RemoteCars / Opponents Render", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mDebugLines = new("DebugLines.Flush", ProfilerCategory.Rendering);
    static readonly ProfilerMarker s_mDriveInput = new("VehicleManager.DriveInput", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mFfb = new("ForceFeedback / Vibration", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mLateUpdate = new("VehicleManager.LateUpdate (wheels/camera/HUD)", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mLamps = new("CollectLamps (car/traffic/remote)", ProfilerCategory.Rendering);

    public override void FixedUpdate(in GameTime time)
    {
        if (Frozen) return;
        using (s_mTrafficFixed.Auto())
            _traffic?.FixedUpdate(time.FixedDeltaTime);
        using (s_mRemoteFixed.Auto())
            Paradise.Game.Online.RemoteCars.FixedUpdate(time.FixedDeltaTime);
        using (s_mOpponentsFixed.Auto())
            Paradise.Game.Racing.RaceOpponents.FixedUpdateActive(time.FixedDeltaTime);
        using (s_mVmFixed.Auto())
            _vm?.FixedUpdate(time.FixedDeltaTime);
    }

    public override void Update(in GameTime time)
    {
        DebugLines.Clear();
        var cam = _terrain.Camera;
        var s = _terrain.Streamer;
        if (_vm == null)
        {
            // Noch keine Physikwelt: die FlyCamera übernimmt, bis das Fahrzeugmodul aufgebaut ist.
            _terrain.FlyCameraEnabled = true;
            return;
        }
        // Unity TDU2VehicleManager.Update(): EnsureDatabase() jedes Bild – GameData.BnkDir kann beim ersten
        // Laden noch leer sein; ohne Retry bleiben Auto, Kamera und Eingaben dauerhaft tot.
        _vm.EnsureDatabase(GameData.BnkDir);
        using (s_mVmUpdate.Auto())
            _vm.Update();
        bool active = !Suspended && !Frozen;
        using (s_mFfb.Auto())
            TDU2ForceFeedback.Update(_vm, active, time.DeltaTime);

        bool trafficSpawn = active && Paradise.Game.Screens.GameMode.Current == Paradise.Game.Screens.Gamemode.GAME_MODE_FREERIDE
                            && !Paradise.Game.Gamemodes.InteriorUtils.Inside && !_terrain.MapView;
        if (!Frozen)
            using (s_mTraffic.Auto())
                _traffic?.Update(time.DeltaTime, trafficSpawn);
        if (_traffic != null) _traffic.Alpha = time.FixedAlpha;
        if (!_terrain.MapView) _traffic?.DrawDebug();

        if (s != null)
        {
            s.ExtraLamps.Clear();
            s.HeadlightProjectors.Clear();
            s.PlayerPostFxProjectors.Clear();
        }

        // Unity: TDU2VehicleCamera ist eine eigene MonoBehaviour und läuft auch im Pausemenü/auf der Karte weiter.
        // Nur ohne Ziel oder im Freiflug übernimmt die FlyCamera – nicht mehr bei Suspended/Frozen.
        var v = _vm.Current;
        bool vehicleCam = v != null && !_terrain.FreeFly;
        _terrain.FlyCameraEnabled = !vehicleCam;
        if (!vehicleCam)
        {
            if (_terrain.FreeFly && v != null)
            {
                // Freiflug: Auto bremst, Kamera gehört der FlyCamera (Verhalten wie zuvor).
                _vm.DriveInput(0f, 0f, 0f, true, time.DeltaTime, false, false);
                using (s_mLateUpdate.Auto())
                    _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
                if (s != null)
                {
                    var camR = cam != null ? cam.Position : System.Numerics.Vector3.Zero;
                    _vm.CollectLamps(s.ExtraLamps, s.RootMatrix);
                    _vm.CollectProjectors(s.HeadlightProjectors, s.RootMatrix, camR);
                    _vm.CollectProjectors(s.PlayerPostFxProjectors, s.RootMatrix, camR, 1f);
                    _traffic?.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, camR);
                    Paradise.Game.Online.RemoteCars.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, camR);
                }
            }
            _vm.UpdateGps(time.DeltaTime);
            return;
        }

        if (active && _vm.Ready && cam != null && s != null)
        {
            bool f2 = TDU2Controls.Pressed("dev_car"), f3 = TDU2Controls.Pressed("dev_carnext"), f4 = TDU2Controls.Pressed("dev_carprev"),
                 f8 = TDU2Controls.Pressed("dev_color"), f9 = TDU2Controls.Pressed("dev_reload");
            #if DEBUG
            if (f2)
            {
                if (_vm.Current != null || _vm.Model != null) _vm.Despawn();
                else _vm.SpawnAt(s.RenderToGame(cam.Position), cam.Forward);
            }
            if (f3) _vm.SwitchCar(+1);
            if (f4) _vm.SwitchCar(-1);
            if (f8) _vm.NextColor();
            if (f9) _vm.SwitchCar(0);
            #endif
        }

        var lights = _vm.Lights;
        if (active)
        {
            _vm.ApplyGearboxMode();   // Modus/Getriebe-Flags auch nach Änderungen außerhalb des Optionsmenüs synchronisieren

            float accel = TDU2Controls.Held("accel") ? 1f : 0f;
            float brake = TDU2Controls.Held("brake") ? 1f : 0f;
            float steer = 0f;
            if (TDU2Controls.Held("left")) steer -= 1f;
            if (TDU2Controls.Held("right")) steer += 1f;

            bool analog = false;
            if (!TDU2Controls.Blocked && TDU2InputDevices.HasDevice)
            {
                float devSteer = TDU2InputDevices.Steer;
                float t = TDU2InputDevices.Throttle, b = TDU2InputDevices.Brake;
                if (MathF.Abs(steer) < 0.01f && MathF.Abs(devSteer) > 0.001f) { steer = devSteer; analog = true; }
                if (accel < 0.01f && t > 0f) accel = t;
                if (brake < 0.01f && b > 0f) brake = b;
            }
            // Kupplung: Pedal des Lenkrads und/"oder" die Taste (Unity DriveInput: Max aus beidem)
            float clutch = TDU2InputDevices.Clutch;
            if (TDU2Controls.Held("clutch")) clutch = 1f;
            using (s_mDriveInput.Auto())
                _vm.DriveInput(accel, brake, steer, TDU2Controls.Held("handbrake"), time.DeltaTime, analog, analog && TDU2InputDevices.IsWheel, clutch);

            if (TDU2Controls.Pressed("camera")) _vm.Camera.NextMode();
            _vm.Camera.LookBack = TDU2Controls.Held("lookback");
            if (TDU2Controls.Pressed("aids")) { _vm.Aids = (DrivingAids)(((int)_vm.Aids + 1) % 4); v.Aids = _vm.Aids; }
            if (TDU2Controls.Pressed("gearbox")) _vm.ToggleGearbox();
            if (_vm.Gearbox == VehicleManager.GearboxMode.HShifter || _vm.Gearbox == VehicleManager.GearboxMode.HShifterClutch)
                UpdateHShifter(v);
            else
            {
                if (TDU2Controls.Pressed("gearup")) v.ShiftUp();
                if (TDU2Controls.Pressed("geardown")) v.ShiftDown();
            }
            if (TDU2Controls.Pressed("reset")) _vm.ResetUpright();
            _vm.Audio?.SetHorn(TDU2Controls.Held("horn"));
            if (lights != null)
            {
                if (TDU2Controls.Pressed("lights")) lights.Headlights = (lights.Headlights + 1) % 3;
                if (TDU2Controls.Pressed("blinkl")) { lights.IndicatorLeft = !lights.IndicatorLeft; lights.IndicatorRight = false; }
                if (TDU2Controls.Pressed("blinkr")) { lights.IndicatorRight = !lights.IndicatorRight; lights.IndicatorLeft = false; }
                if (TDU2Controls.Pressed("hazard")) lights.Hazard = !lights.Hazard;
                lights.Flash = TDU2Controls.Held("flash");
            }

            using (s_mLateUpdate.Auto())
                _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
        }
        else
        {
            // Pause/Karte: keine Fahreingaben, aber die Kamera bleibt wie in Unity beweglich (rechter Stick/Maus).
            using (s_mLateUpdate.Auto())
                _vm.UpdateCameraOnly(time.DeltaTime, (float)time.TotalTime, _window.Input);
            _vm.UpdateGps(time.DeltaTime);
        }

        // TD_DashBoard/HEADLIGHTSINTENSITY folgt dem Lichtschalter, nicht nur dem Wetter.
        // Ein: mindestens 1 (Nachthelligkeit des Wetters bleibt erhalten), aus: 0.
        int headlightMode = lights?.Headlights ?? 0;
        _terrain.DashHeadlights = headlightMode > 0
            ? MathF.Max(CarLighting.HeadlightsIntensity, 1f)
            : 0f;

        if (cam != null && s != null)
        {
            var (pos, fwd, up) = _vm.CameraRender(s.RootMatrix);
            cam.Position = pos;
            cam.Override = (fwd, up);
            _terrain.FieldOfView = _vm.Camera.fieldOfView;
            _terrain.NearClip = _vm.Camera.nearClipPlane;
            using (s_mLamps.Auto())
            {
                _vm.CollectLamps(s.ExtraLamps, s.RootMatrix);
                _traffic?.CollectLamps(_vm, s.ExtraLamps, s.RootMatrix);
                Paradise.Game.Online.RemoteCars.CollectLamps(_vm, s.ExtraLamps, s.RootMatrix);
                // Scheinwerfer-Projektoren des Originals (Lichtpuffer s15): eigenes Auto zuerst, dann Verkehr
                _vm.CollectProjectors(s.HeadlightProjectors, s.RootMatrix, pos);
                _vm.CollectProjectors(s.PlayerPostFxProjectors, s.RootMatrix, pos, 1f);
                _traffic?.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, pos);
                Paradise.Game.Online.RemoteCars.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, pos);
            }
        }
    }

    // H-Schaltung: per Taste gewählter Gang bleibt eingelegt; eine echte Schaltung (TDU2InputDevices.HGear) hat Vorrang.
    static int _hGearKey = int.MinValue;

    /// <summary>Unity TDU2VehicleManager.UpdateHShifter – Gänge 1..7/−1, ohne Kupplung fliegt der Gang heraus.</summary>
    static void UpdateHShifter(Vehicle v)
    {
        for (int g = 1; g <= 7; g++)
            if (!TDU2Controls.IsJoystickBound("gear" + g) && TDU2Controls.Pressed("gear" + g)) _hGearKey = g;
        if (!TDU2Controls.IsJoystickBound("gearR") && TDU2Controls.Pressed("gearR")) _hGearKey = -1;
        if (TDU2Controls.Pressed("gearup")) _hGearKey = System.Math.Min((_hGearKey == int.MinValue ? v.Gear : _hGearKey) + 1, v.GearCount);
        if (TDU2Controls.Pressed("geardown")) _hGearKey = System.Math.Max((_hGearKey == int.MinValue ? v.Gear : _hGearKey) - 1, -1);

        int dev = TDU2InputDevices.HGear;   // int.MinValue = keine Schaltung, 0 = Leerlauf
        if (dev != int.MinValue && dev != 0) _hGearKey = int.MinValue;
        int want = dev != int.MinValue && (dev != 0 || _hGearKey == int.MinValue) ? dev : (_hGearKey == int.MinValue ? v.Gear : _hGearKey);
        want = System.Math.Clamp(want, -1, System.Math.Max(1, v.GearCount));
        if (want == v.Gear || v.Shifting) return;
        if (v.ManualClutch && v.ClutchPedal < 0.5f)
        {
            if (v.Gear != 0) v.Shift(0, 0f);   // ohne Kupplung: Gang springt heraus
            return;
        }
        v.Shift(want, 0.05f);
    }

    public override void Render(in GameTime time)
    {
        var s = _terrain.Streamer;
        if (_vm == null || s == null) return;
        if (_terrain.MapTerrainShown) return;
        using (s_mVmRender.Auto())
            _vm.Render(s.RootMatrix);
        if (_terrain.Camera != null) Paradise.Game.Online.Party.RenderOwnPassenger(_terrain.Camera.Position);
        if (!s.HideWorld)
            using (s_mTrafficRender.Auto())
                _traffic?.Render(_vm, s.RootMatrix);
        using (s_mRemoteRender.Auto())
        {
            if (!s.HideWorld && _terrain.Camera != null) Paradise.Game.Online.RemoteCars.Render(_vm, s.RootMatrix, _terrain.Camera.Position, _renderer);
            if (!s.HideWorld) Paradise.Game.Racing.RaceOpponents.RenderActive(s.RootMatrix);
        }

        var cam = _terrain.Camera;
        if (cam != null && _renderer.Height > 0)
            using (s_mDebugLines.Auto())
                DebugLines.Flush(_renderer, s.RootMatrix, cam.Position, 2f * MathF.Tan(_terrain.FieldOfView * (MathF.PI / 360f)) / _renderer.Height);
    }

    public override void Unload()
    {
        TDU2ForceFeedback.Shutdown();
        _traffic?.Dispose();
        _traffic = null;
        _vm?.Dispose();
        _vm = null;
    }
}