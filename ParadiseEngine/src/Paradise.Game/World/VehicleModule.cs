using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
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
        _overlay?.AddSection("Vehicle (Keys: F2 to get in/out, F3/F4 for car, F8 for color, F9 to reset)", () => _vm.DebugLines());
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
    static readonly ProfilerMarker s_mControls = new("TDU2Controls.Update", ProfilerCategory.Scripts);
    static readonly ProfilerMarker s_mDriveInput = new("VehicleManager.DriveInput", ProfilerCategory.Scripts);
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
        if (_vm == null) return;
        using (s_mVmUpdate.Auto())
            _vm.Update();

        bool trafficSpawn = !Suspended && !Frozen && Paradise.Game.Screens.GameMode.Current == Paradise.Game.Screens.Gamemode.GAME_MODE_FREERIDE
                            && !Paradise.Game.Gamemodes.InteriorUtils.Inside && !_terrain.MapView;
        if (!Frozen)
            using (s_mTraffic.Auto())
                _traffic?.Update(time.DeltaTime, trafficSpawn);
        if (_traffic != null) _traffic.Alpha = time.FixedAlpha;
        if (!_terrain.MapView) _traffic?.DrawDebug();
        using (s_mControls.Auto())
            TDU2Controls.Update(_window.Input, time.FrameCount);
        if (Suspended || Frozen)
        {
            _terrain.FlyCameraEnabled = false;
            _vm?.UpdateGps(time.DeltaTime); 
            return;
        }
        bool f2 = TDU2Controls.Pressed("dev_car"), f3 = TDU2Controls.Pressed("dev_carnext"), f4 = TDU2Controls.Pressed("dev_carprev"),
             f8 = TDU2Controls.Pressed("dev_color"), f9 = TDU2Controls.Pressed("dev_reload");
        var cam = _terrain.Camera; var s = _terrain.Streamer;
        if (_vm.Ready && cam != null && s != null)
        {
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

        var v = _vm.Current;
        _terrain.FlyCameraEnabled = v == null || _terrain.FreeFly;
        s?.ExtraLamps.Clear();
        s?.HeadlightProjectors.Clear();
        if (v == null) return;
        if (_terrain.FreeFly)
        {
            _vm.DriveInput(0f, 0f, 0f, true, time.DeltaTime, false, false);
            _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
            if (s != null)
            {
                _vm.CollectLamps(s.ExtraLamps, s.RootMatrix);
                var camR = cam != null ? cam.Position : System.Numerics.Vector3.Zero;
                _vm.CollectProjectors(s.HeadlightProjectors, s.RootMatrix, camR);
                _traffic?.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, camR);
                Paradise.Game.Online.RemoteCars.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, camR);
            }
            return;
        }

        float accel = TDU2Controls.Held("accel") ? 1f : 0f;
        float brake = TDU2Controls.Held("brake") ? 1f : 0f;
        float steer = 0f;
        if (TDU2Controls.Held("left")) steer -= 1f;
        if (TDU2Controls.Held("right")) steer += 1f;

        bool analog = false; float devSteer = 0f;
        if (!TDU2Controls.Blocked && TDU2InputDevices.HasDevice)
        {
            devSteer = TDU2InputDevices.Steer;
            float t = TDU2InputDevices.Throttle, b = TDU2InputDevices.Brake;
            if (MathF.Abs(steer) < 0.01f && MathF.Abs(devSteer) > 0.001f) { steer = devSteer; analog = true; }
            if (accel < 0.01f && t > 0f) accel = t;
            if (brake < 0.01f && b > 0f) brake = b;
        }
        using (s_mDriveInput.Auto())
            _vm.DriveInput(accel, brake, steer, TDU2Controls.Held("handbrake"), time.DeltaTime, analog, analog && TDU2InputDevices.IsWheel);

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

        using (s_mLateUpdate.Auto())
            _vm.LateUpdate(time.DeltaTime, (float)time.TotalTime, _window.Input, time.FixedAlpha);
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
                _traffic?.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, pos);
                Paradise.Game.Online.RemoteCars.CollectProjectors(_vm, s.HeadlightProjectors, s.RootMatrix, pos);
            }
        }
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
        _traffic?.Dispose();
        _traffic = null;
        _vm?.Dispose();
        _vm = null;
    }
}