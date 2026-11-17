using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using System.Globalization;

namespace Paradise.Game.Screens;

/// <summary>
/// Menüseiten des Entwicklermenüs (F1), gleicher Aufbau wie Unity TDU2DevMenu (MainMenu, CarsMenu, WorldMenu, StreamingMenu,
/// WeatherMenu, RenderingMenu, UsageMenu, JobsMenu). Werte wirken direkt auf die Engine (MapStreamer, VehicleManager,
/// WorldWeather, GraphicsSettings, RenderDistance). Was es in der Engine noch nicht gibt, steht als grauer Platzhalter da.
/// Weitere Seiten (Insel, Netzwerk, Spielstand, Eingabe, Debug-Ansicht, Flash, Audio): DevMenu.Debug.cs.
/// </summary>
public sealed partial class DevMenu
{
    MapStreamer? Streamer => _terrain.Streamer;
    VehicleManager? Mgr => _vehicles.Manager;
    WorldWeather? Weather => _terrain.Weather;

    static string Num(float v, string fmt = "0") => v.ToString(fmt, CultureInfo.InvariantCulture);
    static string MB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    // ------------------------------------------------------------------ Hauptmenü (Unity MainMenu)
    List<Item> MainMenu() => new List<Item>
    {
        Head("RACING & TESTS"),
        PhMenu("Race Debug / Start Race (.ig2)", "Racing is not ported yet"),
        PhMenu("Cutscene Tester", "Cutscenes are not ported yet"),
        PhMenu("Animation Viewer", "Avatars/animations are not ported yet"),

        Head("VEHICLES & TRAFFIC"),
        Menu("Cars", CarsMenu),
        PhMenu("Traffic", "Traffic is not ported yet"),

        Head("WORLD"),
        Menu("World Loading / Island travel", WorldMenu),
        Menu("Streaming", StreamingMenu),
        Menu("Weather & Time of Day", WeatherMenu),
        Menu("GPS / Bookmarks", GpsMenu),

        Head("INPUT"),
        Menu("Controls / Input Devices", ControlsMenu),
        PhBool("Input overlay (axes, pedals, buttons)", false),

        Head("PLAYER & ONLINE"),
        Menu("Avatar", AvatarMenu),
        Menu("Network", NetworkMenu),
        Menu("Savegame / Profile / Money", SavegameMenu),

        Head("VISUALS & PERFORMANCE"),
        Menu("Rendering", RenderingMenu),
        Menu("Performance", UsageMenu),
        Menu("Jobs & Threads", JobsMenu),
        Menu("Audio (volumes, now playing)", AudioMenu),

        Head("DEBUG VIEW"),
        Menu("Debug View Toggles (race / traffic / camera)", DebugViewMenu),
        Bool("Free-fly camera", () => _terrain.FreeFly, SetFreeFly, false),

        Head("FLASH"),
        Menu("Flash Debug", FlashMenu),
        Menu("Flash Profiler", FlashProfilerMenu),

        Head(""),
        Info("Close F1 · Up/Down Entry · Left/Right Value (Shift x10) · Enter to open · Backspace to go back · Delete (default)"),
    };

    // ------------------------------------------------------------------ Autos (Unity CarsMenu)
    static readonly string[] AidNames = { "Secure", "Sport", "Hypersport", "Off" };

    List<Item> CarsMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("CURRENT CAR") };
        if (m == null) { l.Add(Info("no vehicle manager")); return l; }
        l.Add(Info("Car", () => m.Current?.Car is { } c ? ((c.Brand ?? "") + " " + c.ModelName).Trim() + "  [" + c.FileName + ", Id " + c.Id + "]" : "-"));
        l.Add(Info("Speed", () => m.Current != null ? Num(m.Current.SpeedKmh) + " km/h" : "-"));
        l.Add(Table("Driving aids", AidNames, () => (int)m.Aids, i => { m.Aids = (DrivingAids)i; if (m.Current != null) m.Current.Aids = m.Aids; }, (int)DrivingAids.Sport));
        l.Add(Bool("Automatic gearbox", () => m.AutomaticGearbox, v => { m.AutomaticGearbox = v; if (m.Current != null) m.Current.AutomaticGearbox = v; }, true));
        l.Add(Action_("Wash car (remove dirt/scratches)", () =>
        {
            if (m.Dirt == null) { Toast("No dirt system on the car"); return; }
            m.Dirt.Wash(); Toast("Car washed");
        }));
        l.Add(Action_("Right / reset car", () => { if (m.Current == null) return; m.ResetUpright(); Toast("Car reset"); }));
        l.Add(Bool("Mirror reflections (probe on the car)", () => m.MirrorReflections, v => m.MirrorReflections = v, true));
        l.Add(Head("SWITCH CAR"));
        l.Add(Menu("All cars (" + m.Available.Count + ")", CarList));
        l.Add(Menu("Own cars", OwnCarsMenu));
        return l;
    }

    List<Item> CarList()
    {
        var m = Mgr;
        var l = new List<Item> { Head("ALL CARS  (Enter = drive)") };
        if (m == null) return l;
        var cars = new List<CarPhysics>(m.Available);
        cars.Sort((a, b) => string.Compare((a.Brand ?? "") + a.ModelName, (b.Brand ?? "") + b.ModelName, StringComparison.OrdinalIgnoreCase));
        if (cars.Count == 0) { l.Add(Info("no cars loaded")); return l; }
        foreach (var c in cars)
        {
            var car = c;
            l.Add(new Item
            {
                Label = ((car.Brand ?? "") + " " + car.ModelName).Trim(),
                Value = () => ReferenceEquals(m.CurrentCar, car) ? "<- driving" : car.FileName,
                Activate = () => DriveCar(car),
            });
        }
        return l;
    }

    void DriveCar(CarPhysics car)
    {
        var m = Mgr; var s = Streamer;
        if (m == null) return;
        int target = m.Available.IndexOf(car);
        int cur = m.CurrentCar != null ? m.Available.IndexOf(m.CurrentCar) : -1;
        if (target < 0) return;
        if (m.Current != null && cur >= 0) m.SwitchCar(target - cur);   // gleiche Stelle, gleiche Geschwindigkeit
        else if (s != null && _terrain.Camera != null) m.SpawnCar(car.FileName, s.ViewerGame, _terrain.Camera.Forward);
        Toast("Car: " + ((car.Brand ?? "") + " " + car.ModelName).Trim());
    }

    // ------------------------------------------------------------------ Welt laden (Unity WorldMenu)
    List<Item> WorldMenu()
    {
        var s = Streamer;
        var l = new List<Item> { Head("STATUS") };
        if (s == null || s.Index == null) { l.Add(Info("no island loaded (MapStreamer)")); l.AddRange(TravelItems()); return l; }
        l.Add(Info("Island", () => IslandTravel.DisplayName(s.Index?.IslandName) + "  (" + s.Status + ")"));
        l.Add(Info("Position (game)", () => Num(s.ViewerGame.X) + " / " + Num(s.ViewerGame.Y) + " / " + Num(s.ViewerGame.Z)));
        l.Add(Info("Sector", () => WorldLayout.SectorAt(s.ViewerGame.X, s.ViewerGame.Z).ToString()));
        l.Add(Info("Areas / sectors loaded", () => s.LoadedAreas + " / " + s.LoadedSectors));
        l.Add(Info("Objects drawn", () => s.DrawnObjects.ToString("N0", CultureInfo.InvariantCulture)));
        l.Add(Info("Instances (trees/objects)", () => s.PlacementInstances.ToString("N0", CultureInfo.InvariantCulture)));
        l.Add(Info("Object library", () => s.LibraryReady ? "ready" : "loading"));
        l.Add(Info("World-Actors", () => s.ActorsReady ? "ready" : "loading"));
        l.Add(Info("Spots", () => s.Spots != null ? s.Spots.Spots.Count.ToString(CultureInfo.InvariantCulture) : "-"));
        l.Add(Info("Pending textures", () => s.PendingTextures.ToString(CultureInfo.InvariantCulture)));
        l.Add(Head("CONTENT  (applies to newly loaded tiles)"));
        l.Add(Bool("Island model (far LOD)", () => s.ShowIslandLod, v => s.ShowIslandLod = v, true));
        l.Add(Bool("Area objects", () => s.LoadAreaObjects, v => s.LoadAreaObjects = v, true));
        l.Add(Bool("Sector objects", () => s.LoadSectorObjects, v => s.LoadSectorObjects = v, true));
        l.Add(Bool("Vegetation", () => s.LoadVegetation, v => s.LoadVegetation = v, true));
        l.Add(Bool("Other placements", () => s.LoadOtherPlacements, v => s.LoadOtherPlacements = v, true));
        l.Add(Bool("Actors", () => s.LoadActors, v => s.LoadActors = v, true));
        l.Add(Bool("World actors (traffic lights, signs)", () => s.LoadWorldActors, v => s.LoadWorldActors = v, true));
        l.Add(Bool("Grass", () => s.LoadGrass, v => s.LoadGrass = v, true));
        l.Add(Bool("Spot buildings (exterior)", () => s.LoadSpotExteriors, v => s.LoadSpotExteriors = v, true));
        l.Add(Bool("Interiors", () => s.LoadInteriors, v => s.LoadInteriors = v, true));
        l.Add(Bool("Open tunnel portals", () => s.CutTunnelPortals, v => s.CutTunnelPortals = v, true));
        l.Add(Head("DISPLAY  (immediate)"));
        l.Add(Bool("Water", () => s.ShowWater, v => s.ShowWater = v, true));
        l.Add(Bool("Clouds", () => s.ShowClouds, v => s.ShowClouds = v, true));
        l.Add(Bool("Stars / moon", () => s.ShowStars, v => s.ShowStars = v, true));
        l.Add(Bool("Power wires", () => s.ShowPowerWires, v => s.ShowPowerWires = v, true));
        l.Add(Bool("Road sign text", () => s.ShowSignText, v => s.ShowSignText = v, true));
        l.AddRange(TravelItems());
        l.Add(Head("ACTIONS"));
        l.Add(Action_("Reload world (all tiles)", () =>
        {
            if (IslandTravel.Pending) { Toast("Island travel running"); return; }
            if (WorldWeather.Inside) { Toast("Leave the interior first"); return; }
            IslandTravel.Go(s.Island, false, Mgr?.CurrentCar?.FileName, "EAIR");
            Toast("Reloading " + IslandTravel.DisplayName(s.Island));
            Close();
        }));
        l.Add(Action_("Clean memory (GC)", CleanMemory));
        return l;
    }

    // ------------------------------------------------------------------ Streaming (Unity StreamingMenu)
    List<Item> StreamingMenu()
    {
        var s = Streamer;
        var l = new List<Item> { Head("STATUS") };
        if (s == null || s.Index == null) { l.Add(Info("no island loaded (MapStreamer)")); return l; }
        l.Add(Info("Load jobs", () => s.PendingJobs + " / " + s.MaxParallelLoads));
        l.Add(Info("Collider jobs", () => s.ColliderJobs.ToString(CultureInfo.InvariantCulture)));
        l.Add(Info("Areas / sectors", () => s.LoadedAreas + "  ·  " + s.LoadedSectors));
        l.Add(Head("LOADING"));
        l.Add(Int("Parallel load jobs", () => s.MaxParallelLoads, v => s.MaxParallelLoads = v, 1, 8, 4));
        l.Add(Float("Load areas up to", () => s.AreaLoadDistance, v => s.AreaLoadDistance = v, 100f, 500f, 10000f, 900f, "0", " m"));
        l.Add(Float("Unload areas (+)", () => s.AreaUnloadMargin, v => s.AreaUnloadMargin = v, 100f, 100f, 5000f, 1000f, "0", " m"));
        l.Add(Float("Load sectors up to", () => s.SectorLoadDistance, v => s.SectorLoadDistance = v, 50f, 100f, 5000f, 950f, "0", " m"));
        l.Add(Float("Unload sectors (+)", () => s.SectorUnloadMargin, v => s.SectorUnloadMargin = v, 50f, 50f, 3000f, 300f, "0", " m"));
        l.Add(Head("RENDER DISTANCE  (immediate)"));
        l.Add(Table("Object render distance", RenderDistance.Names, () => (int)RenderDistance.Level, i => RenderDistance.Level = (RenderDistanceLevel)i, (int)RenderDistanceLevel.Normal));
        l.Add(Info("Effective", () => "x" + Num(RenderDistance.Multiplier, "0.##") + "  ·  vegetation " + Num(s.VegetationDistance * RenderDistance.Multiplier) + " m"));
        l.Add(Head("TERRAIN DISPLAY  (immediate)"));
        l.Add(Float("Lower terrain (sidewalks/roads on top)", () => s.TerrainLower, v => s.TerrainLower = v, 0.01f, 0f, 0.5f, 0.1f, "0.00", " m"));
        l.Add(Float("Terrain depth push", () => s.TerrainDepthPush * 1000f, v => s.TerrainDepthPush = v / 1000f, 0.1f, 0f, 10f, 1.5f, "0.0", " mm/m"));
        l.Add(Head("DETAIL  (newly loaded tiles)"));
        l.Add(Float("Object LOD distance", () => s.LodDistanceScale, v => s.LodDistanceScale = v, 0.25f, 0.25f, 10f, 4f, "0.00", " x"));
        l.Add(Bool("Cull last LOD", () => s.CullLastLod, v => s.CullLastLod = v, false));
        l.Add(Float("Vegetation distance", () => s.VegetationDistance, v => s.VegetationDistance = v, 50f, 100f, 5000f, 1100f, "0", " m"));
        l.Add(Float("Grass radius", () => s.GrassRadius, v => s.GrassRadius = v, 10f, 20f, 500f, 120f, "0", " m"));
        l.Add(Float("Grass density", () => s.GrassDensity, v => s.GrassDensity = v, 0.05f, 0f, 2f, 0.9f, "0.00"));
        return l;
    }

    // ------------------------------------------------------------------ Wetter (Unity WeatherMenu)
    static readonly string[] KindNames = { "Automatic", "Sun", "Clouds", "Rain", "Before rain", "After rain", "Thunderstorm" };

    static string HourText(float h)
    {
        h = (h % 24f + 24f) % 24f;
        int hh = (int)MathF.Floor(h), mm = (int)MathF.Floor((h - hh) * 60f);
        return hh.ToString("00", CultureInfo.InvariantCulture) + ":" + mm.ToString("00", CultureInfo.InvariantCulture);
    }

    List<Item> WeatherMenu()
    {
        var w = Weather;
        var l = new List<Item>
        {
            Head("CURRENT"),
            Info("Time", () => HourText(WorldWeather.Hour)),
            Info("Weather", () => KindNames[(int)WorldWeather.Kind + 1]),
            Info("Night / rain / wetness", () => Num(WorldWeather.Night * 100f) + " % / " + Num(WorldWeather.Rain * 100f) + " % / " + Num(WorldWeather.GroundWet * 100f) + " %"),
        };
        if (w == null) { l.Add(Info("Weather not active (LightingConfig.UseWeather)")); return l; }
        l.Add(Head("CONTROL  (local – otherwise synced to server time)"));
        l.Add(new Item
        {
            Label = "Fixed time",
            Value = () => w.ForceHour < 0f ? "Automatic" : HourText(w.ForceHour),
            Change = d => { float h = w.ForceHour < 0f ? MathF.Floor(WorldWeather.Hour * 2f) / 2f : w.ForceHour; h += 0.5f * MathF.Sign(d) * (Math.Abs(d) >= 10 ? 6f : 1f); w.ForceHour = (h % 24f + 24f) % 24f; },
            Reset = () => w.ForceHour = -1f,
        });
        l.Add(Table("Fixed weather (road wetness follows at once)", KindNames, () => w.ForceKind + 1, i => w.ForceKind = i - 1, 0));
        l.Add(Float("Clock offset", () => w.HourOffset, v => w.HourOffset = v, 0.5f, -12f, 12f, 0f, "+0.0;-0.0;0", " h"));
        l.Add(Float("Length of a game day", () => w.DayMinutes, v => w.DayMinutes = v, 5f, 5f, 1440f, 15f, "0", " min"));
        l.Add(Float("Weather segment length", () => w.WeatherMinutes, v => w.WeatherMinutes = v, 1f, 1f, 120f, 4f, "0", " min"));
        l.Add(Float("Weather transition", () => w.TransitionSeconds, v => w.TransitionSeconds = v, 10f, 0f, 600f, 120f, "0", " s"));
        l.Add(Head("DISPLAY"));
        l.Add(Bool("Fog", () => w.ApplyFog, v => w.ApplyFog = v, true));
        l.Add(Float("Fog strength", () => w.FogStrength, v => w.FogStrength = v, 0.05f, 0f, 1f, 0.05f, "0.00"));
        l.Add(Float("Sunlight", () => w.DiffuseScale, v => w.DiffuseScale = v, 0.05f, 0f, 2f, 0.65f, "0.00"));
        l.Add(Float("Ambient light", () => w.AmbientScale, v => w.AmbientScale = v, 0.05f, 0f, 2f, 0.75f, "0.00"));
        l.Add(PhBool("Auto headlights", true));
        l.Add(Int("Street lamps with light", () => w.LampLights, v => w.LampLights = v, 0, 32, 32));
        l.Add(Float("Lamp brightness", () => w.LampIntensity, v => w.LampIntensity = v, 0.1f, 0f, 2f, 1f, "0.0"));
        l.Add(Float("Street light on terrain", () => w.StreetLightStrength, v => w.StreetLightStrength = v, 0.05f, 0f, 1f, 0.5f, "0.00"));
        l.Add(Head("PRESETS"));
        l.Add(Action_("Sunrise (6:30)", () => { w.ForceHour = 6.5f; Toast("Time 6:30"); }));
        l.Add(Action_("Noon (12:00)", () => { w.ForceHour = 12f; Toast("Time 12:00"); }));
        l.Add(Action_("Dusk (19:30)", () => { w.ForceHour = 19.5f; Toast("Time 19:30"); }));
        l.Add(Action_("Night (23:00)", () => { w.ForceHour = 23f; Toast("Time 23:00"); }));
        l.Add(Action_("Midnight (00:00, moon)", () => { w.ForceHour = 0f; Toast("Time 0:00"); }));
        l.Add(Action_("Thunderstorm", () => { w.ForceKind = (int)WeatherKind.Tempest; Toast("Weather: thunderstorm"); }));
        l.Add(Action_("All automatic (synced)", () => { w.ForceHour = -1f; w.ForceKind = -1; w.HourOffset = 0f; Toast("Weather/time synced again"); }));
        return l;
    }

    // ------------------------------------------------------------------ Grafik (Unity RenderingMenu)
    static readonly string[] MsaaNames = { "Off", "2x", "4x", "8x" };
    static readonly int[] MsaaValues = { 1, 2, 4, 8 };
    static readonly string[] TexNames = { "Full", "1/2", "1/4", "1/8" };
    static readonly string[] ShadowResNames = { "1024", "2048", "4096" };
    static readonly int[] ShadowResValues = { 1024, 2048, 4096 };

    List<Item> RenderingMenu()
    {
        var s = Streamer;
        var l = new List<Item> { Head("DISPLAY") };
        l.Add(Info("Resolution", () => _r.Width + " x " + _r.Height));
        l.Add(Info("Graphics", () => _r.DeviceName + "  (" + _r.Stats.Api + ", " + _r.Stats.Driver + ")"));
        l.Add(Bool("VSync (restart)", () => GraphicsSettings.VSync, v => GraphicsSettings.VSync = v, true));
        l.Add(Table("Anti-aliasing (restart)", MsaaNames, () => Math.Max(0, Array.IndexOf(MsaaValues, GraphicsSettings.Msaa)), i => GraphicsSettings.Msaa = MsaaValues[i], 0));
        l.Add(Float("View distance (camera)", () => _terrain.FarClip, v => _terrain.FarClip = v, 500f, 1000f, 60000f, 60000f, "0", " m"));
        l.Add(Head("DETAIL"));
        l.Add(Table("Texture resolution (new textures)", TexNames, () => Math.Clamp(GraphicsSettings.TextureMipLimit, 0, 3), i => GraphicsSettings.TextureMipLimit = i, 0));
        if (s != null) l.Add(Float("Object LOD distance (new tiles)", () => s.LodDistanceScale, v => s.LodDistanceScale = v, 0.25f, 0.25f, 10f, 4f, "0.00", " x"));
        l.Add(Head("SHADOWS"));
        l.Add(Table("Shadow resolution (restart)", ShadowResNames, () => Math.Max(0, Array.IndexOf(ShadowResValues, GraphicsSettings.ShadowResolution)), i => GraphicsSettings.ShadowResolution = ShadowResValues[i], 1));
        l.Add(Float("Shadow distance (0 = config)", () => GraphicsSettings.ShadowDistance, v => GraphicsSettings.ShadowDistance = v, 25f, 0f, 3000f, 0f, "0", " m"));
        l.Add(Head("WORLD"));
        if (s != null)
        {
            l.Add(Bool("Glow materials (new tiles)", () => s.ShowGlowMaterials, v => { s.ShowGlowMaterials = v; MaterialFactory.ShowGlowMaterials = v; }, true));
            l.Add(Float("Vegetation distance (new tiles)", () => s.VegetationDistance, v => s.VegetationDistance = v, 50f, 100f, 5000f, 1100f, "0", " m"));
            l.Add(Float("Wind strength", () => s.WindStrength, v => s.WindStrength = v, 0.1f, 0f, 3f, 1f, "0.0"));
        }
        return l;
    }

    // ------------------------------------------------------------------ Leistung (Unity UsageMenu)
    void CleanMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Toast("Cleaning memory");
    }

    List<Item> UsageMenu()
    {
        var st = _r.Stats;
        return new List<Item>
        {
            Head("FRAME RATE"),
            Info("FPS", () => Num(_fps, "0.0")),
            Info("Frame time avg / min / max", () => Num(_ftAvg, "0.0") + " / " + Num(_ftMin, "0.0") + " / " + Num(_ftMax, "0.0") + " ms"),
            Head("RENDERING (last frame)"),
            Info("Draw calls", () => st.DrawCalls.ToString("N0", CultureInfo.InvariantCulture)),
            Info("Pipeline binds", () => st.PipelineBinds.ToString("N0", CultureInfo.InvariantCulture)),
            Info("Triangles", () => st.Triangles.ToString("N0", CultureInfo.InvariantCulture)),
            Info("Transparent / shadow / overlay draws", () => st.TransparentDraws + " / " + st.ShadowDraws + " / " + st.OverlayDraws),
            Info("Textures / materials / meshes", () => st.Textures + " / " + st.Materials + " / " + st.Meshes),
            Head("MEMORY"),
            Info("GPU allocated", () => MB(st.GpuMemoryAllocated)),
            Info("VRAM usage / budget / total", () => MB(st.VramUsage) + " / " + MB(st.VramBudget) + " / " + MB(st.VramTotal)),
            Info(".NET heap", () => MB(GC.GetTotalMemory(false))),
            Info("Process working set", () => MB(Environment.WorkingSet)),
            Info("GC collections", () => GC.CollectionCount(0) + " / " + GC.CollectionCount(1) + " / " + GC.CollectionCount(2)),
            Head("SYSTEM"),
            Info("Graphics", () => st.DeviceName + "  (" + st.DeviceType + ")"),
            Info("CPU threads", () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
            Info("Uptime", () => TimeSpan.FromSeconds(_time).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)),
            Head("ACTIONS"),
            Action_("Clean memory (GC)", CleanMemory),
        };
    }

    // ------------------------------------------------------------------ Jobs & Threads (Unity JobsMenu)
    List<Item> JobsMenu()
    {
        var s = Streamer;
        var l = new List<Item> { Head("STREAMING") };
        if (s != null && s.Index != null)
        {
            l.Add(Info("Load jobs (area/sector)", () => s.PendingJobs + " / " + s.MaxParallelLoads));
            l.Add(Int("Parallel load jobs", () => s.MaxParallelLoads, v => s.MaxParallelLoads = v, 1, 8, 4));
            l.Add(Info("Collider jobs", () => s.ColliderJobs.ToString(CultureInfo.InvariantCulture)));
            l.Add(Float("Vegetation budget per frame", () => (float)s.VegetationBudgetMs, v => s.VegetationBudgetMs = v, 0.5f, 0.5f, 20f, 3f, "0.0", " ms"));
            l.Add(Float("Grass budget per frame", () => (float)s.GrassBudgetMs, v => s.GrassBudgetMs = v, 0.5f, 0.5f, 20f, 2f, "0.0", " ms"));
        }
        else l.Add(Info("no island loaded"));
        l.Add(Head(".NET THREADPOOL  (Task.Run: banks, textures, cars, music)"));
        l.Add(Info("Workers free / max", () => { ThreadPool.GetAvailableThreads(out int w, out _); ThreadPool.GetMaxThreads(out int mw, out _); return w + " / " + mw + "  (busy " + (mw - w) + ")"; }));
        l.Add(Info("I/O free / max", () => { ThreadPool.GetAvailableThreads(out _, out int io); ThreadPool.GetMaxThreads(out _, out int mio); return io + " / " + mio; }));
        l.Add(Info("Threads", () => ThreadPool.ThreadCount + "  (queued " + ThreadPool.PendingWorkItemCount + ")"));
        l.Add(Head("SYSTEM"));
        l.Add(Info("CPU threads", () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)));
        return l;
    }
}
