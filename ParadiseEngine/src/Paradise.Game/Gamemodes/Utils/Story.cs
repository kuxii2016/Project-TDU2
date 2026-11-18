// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: TDU2StoryProgress, TDU2Story, StoryMode, FadeOverlay).
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Screens;
using System.Numerics;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Fortschritt der Geschichte je Profil (wie profile+0x58 im Original, FUN_00b10da0). Schritte (Namen aus dem Spiel):
///  0 Avatar choice / First WalkMode, 1 First Garage, 2 First Freeride, 3 Meeting Tess, 4 Driving Tess,
///  5 Solar Crown exterior, 6 Solar Crown interior, 7 Used Car Dealer, 8 Tuto FROG, 9 Go Home,
///  10 Home sweet home : Caravan, 11 Hawai First Freeride, 12 Igloo First Intro.
/// Gespeichert im Spielstand des Profils (Unity: PlayerPrefs "TDU2.Story.&lt;Profil&gt;"): SaveData.story (Original-DATA
/// Driver.BeginGameState + U3D_Story, -1 = nie begonnen), storyAvatar (gewählter Intro-Avatar, U3D_StoryAvatar) und
/// storyGaveCar (Story-Auto gegeben, U3D_StoryGave) – TDU2SaveGame liest/schreibt sie mit der DATA, der Server mit dem Spielstand.
/// </summary>
internal static class StoryProgress
{
    public static readonly string[] StepNames =
    {
        "Avatar choice / First WalkMode", "First Garage", "First Freeride", "Meeting Tess", "Driving Tess",
        "Solar Crown exterior", "Solar Crown interior", "Used Car Dealer", "Tuto FROG", "Go Home",
        "Home sweet home : Caravan", "Hawai First Freeride", "Igloo First Intro",
    };

    /// <summary>Ab diesem Schritt ist die Geschichte (soweit umgesetzt) durch: normales Spiel.</summary>
    public const int Implemented = 11;

    /// <summary>Spielstand des angemeldeten Profils (null = kein Spielstand: nichts zu merken).</summary>
    static SaveData? SaveOf(string? nick) =>
        TDU2Session.Save != null && string.Equals(TDU2Session.Nick ?? "", nick ?? "", StringComparison.OrdinalIgnoreCase) ? TDU2Session.Save : null;

    /// <summary>Schritt des Profils (-1 = noch nie begonnen).</summary>
    public static int Get(string? nick) => SaveOf(nick)?.story ?? -1;

    public static void Set(string? nick, int step)
    {
        var s = SaveOf(nick);
        if (s != null)
        {
            s.story = step;
            if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            SessionAutosave.Schedule(3f);   // läuft gerade eine Speicherung, übernimmt die nächste den Schritt
        }
        Log.Info("Geschichte: Schritt " + step + " (" + StepName(step) + ")");
    }

    public static string IntroAvatar(string? nick) => SaveOf(nick)?.storyAvatar ?? "";
    public static void SetIntroAvatar(string? nick, string? cfg) { var s = SaveOf(nick); if (s != null) s.storyAvatar = cfg ?? ""; }

    /// <summary>Hat die Geschichte das Story-Auto (California) in die Garage gelegt? (Tess nimmt es an der Solar Crown zurück)</summary>
    public static bool GaveStoryCar(string? nick) => SaveOf(nick)?.storyGaveCar ?? false;
    public static void SetGaveStoryCar(string? nick, bool on) { var s = SaveOf(nick); if (s != null) s.storyGaveCar = on; }

    public static string StepName(int step) => step >= 0 && step < StepNames.Length ? StepNames[step] : "Not handled";
}

/// <summary>
/// Ein Schritt der Geschichte (Unity StoryMode). Die Schritte stehen in den Klassen ihrer Spielmodi:
/// GAME_MODE_AVATAR_CHOICE (0), GAME_MODE_FIRST_WM (1), GAME_MODE_CINE (2, 3, 5, 6), GAME_MODE_MISSION (4, 9, 10),
/// GAME_MODE_SHOP (7), GAME_MODE_FREERIDE (8).
/// </summary>
internal abstract class StoryStep
{
    protected static VehicleManager Manager => Story.Vm!;
    /// <summary>Pausemenü/Karte gesperrt (Kino, Laufmodus).</summary>
    public virtual bool BlocksInput => true;
    /// <summary>Große Karte gesperrt (Kinos/Laufmodus und Missionen mit festem Ziel, z.B. Tess zum Drehort).</summary>
    public virtual bool BlocksMap => BlocksInput;
    /// <summary>Autoradio stumm (Kinos, Laufmodus: dort laufen die Töne/Musik der Szene).</summary>
    public virtual bool MutesRadio => BlocksInput;
    public abstract void Enter();
    public abstract void Tick(float dt);
    public virtual void Exit() { }
    /// <summary>Zeichnen (Darsteller der Szenen, Tess im Auto). gameToRender = MapStreamer.RootMatrix.</summary>
    public virtual void Render(Matrix4x4 gameToRender, Vector3 cameraRender) { }

    /// <summary>Spielerauto weg, Fahrzeugsteuerung angehalten (Kino-/Laufmodus).</summary>
    protected static void Suspend()
    {
        var vm = Story.Vm;
        if (vm != null && (vm.Current != null || vm.Model != null)) vm.Despawn();
        if (Story.Vehicles != null) Story.Vehicles.Suspended = true;
    }

    protected static string PlayerAvatarConfig()
    {
        var c = StoryProgress.IntroAvatar(Story.Nick);
        if (string.IsNullOrEmpty(c)) c = Paradise.Core.Avatars.AvatarConfigs.Local != null && Paradise.Core.Avatars.AvatarConfigs.Local.Sex == 1 ? "AVT_Female_Intro" : "AVT_Male_Intro";
        return c;
    }

    protected static bool PlayerIsWoman => PlayerAvatarConfig().IndexOf("Female", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Szene erstellen (Unity TDU2StoryScene.Create).</summary>
    protected static StoryScene CreateScene(StoryScene.Setup setup) => StoryScene.Create(Story.Ctx, setup);

    /// <summary>Kino-Kamera, Blende und Laden der Szene (Unity StoryScene.Update/LateUpdate) – jedes Bild aus dem Schritt.</summary>
    protected static void UpdateScene(StoryScene? s, float dt)
    {
        if (s == null) return;
        s.Update(dt);
        s.ApplyCamera();
        s.UpdateFade();
    }

    protected static bool ReadyAround(Vector3 game, float radius)
    {
        var st = Story.Streamer;
        return st == null || st.ReadyAround(st.GameToRender(game), radius);
    }
}

/// <summary>
/// Regie der Geschichte (Unity TDU2Story; Spielmodi AVATAR_CHOICE / FIRST_WM aus OpenTDU2 und die Story-Schritte 2–10 des Originals):
/// startet für ein neues Profil (GamePC.cpr SCENARIOTUTORIAL = ON) statt des direkten Spielstarts (GAME_MODE_LOGIN) und setzt das Auto
/// über <see cref="RequestSpawn"/> in die freie Fahrt. Läuft jedes Bild aus ScreenModule (Update/Render), unabhängig vom aktiven Modus –
/// die Schritte setzen ihre Spielmodi selbst (GameMode.Push/Pop wie Unity TDU2GameMode).
/// </summary>
internal static class Story
{
    /// <summary>Läuft gerade ein Schritt der Geschichte?</summary>
    public static bool Active => _mode != null;
    /// <summary>Läuft gerade ein Kino/Laufmodus der Geschichte (Pausemenü, Karte und Fahrtasten gesperrt)?</summary>
    public static bool BlocksPauseMenu => _mode != null && _mode.BlocksInput;
    /// <summary>Große Karte gesperrt (zusätzlich zu den Kinos auch während Missionen wie "Driving Tess").</summary>
    public static bool BlocksMap => _mode != null && (_mode.BlocksInput || _mode.BlocksMap);
    /// <summary>Laufender Story-Schritt (-1 = keine Geschichte).</summary>
    public static int CurrentStep => _mode != null ? Step : -1;
    /// <summary>Name des laufenden Story-Schritts wie im Spiel ("Driving Tess" …), null = keine Geschichte.</summary>
    public static string? CurrentMission => CurrentStep >= 0 ? StoryProgress.StepName(CurrentStep) : null;
    /// <summary>Zuletzt angezeigtes Ziel/Tipp der Geschichte (lokalisiert), null = keins.</summary>
    public static string? CurrentObjective { get; internal set; }

    public static string Nick { get; private set; } = "";
    public static int Step { get; private set; }

    internal static IGameModeContext Ctx = null!;
    internal static VehicleManager? Vm => Ctx?.Vehicles?.Manager;
    internal static Paradise.Game.World.VehicleModule? Vehicles => Ctx?.Vehicles;
    internal static Paradise.Game.World.TerrainModule? Terrain => Ctx?.Terrain;
    internal static MapStreamer? Streamer => Ctx?.Terrain?.Streamer;

    static StoryStep? _mode;
    static NetResponse? _login;
    static bool _pendingBegin;

    /// <summary>California (DBCarPhysicsData _id_car 387): das Geschenk von Tess, Auto der ersten Schritte.</summary>
    public const int StoryCarId = 387;
    public static CarPhysics? StoryCar => Vm?.Database?.Cars.Find(c => c.Id == StoryCarId) ?? Vm?.FindCar("California");

    /// <summary>Eigenes Auto des Spielers (zuletzt gekauftes aus dem Spielstand), sonst das Start- bzw. Story-Auto.</summary>
    public static CarPhysics? PlayerCar()
    {
        var vm = Vm;
        if (vm == null) return null;
        var owned = TDU2Session.Save?.ownedCars;
        if (owned != null)
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                var c = vm.FindCar(owned[i]);
                if (c != null && c.Id != StoryCarId) return c;
            }
        return vm.FindCar(vm.StartCar) ?? StoryCar;
    }

    // ------------------------------------------------------------------ Start (aus GAME_MODE_LOGIN)
    /// <summary>Geschichte statt des normalen Starts? (SCENARIOTUTORIAL = ON und Schritt noch nicht durch)</summary>
    public static bool ShouldRun(GamePC? gp, string nick, bool starterChosen)
    {
        ApplyStepOverride(nick);
        if (gp == null || !string.Equals(gp.Get("SCENARIOTUTORIAL") ?? "", "ON", StringComparison.OrdinalIgnoreCase)) return false;
        // noch nicht gespielt (auch bei Spielständen mit Auto) oder mittendrin: Geschichte (ab dem gespeicherten Schritt)
        int step = StoryProgress.Get(nick);
        return step < StoryProgress.Implemented;
    }

    static bool _overrideDone;

    /// <summary>
    /// Story neu beginnen: Startparameter -STORYSTEP n (bzw. Entwicklermenü) setzt den gespeicherten Schritt einmalig
    /// (z.B. 0 = Intro von vorn, wenn ein Durchlauf abgebrochen wurde). Danach gilt wieder der gespeicherte Fortschritt.
    /// </summary>
    static void ApplyStepOverride(string nick)
    {
        if (_overrideDone) return;
        int step = StoryStartStep;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], "-STORYSTEP", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int n)) step = n;
        if (step < 0) return;
        _overrideDone = true;
        step = System.Math.Clamp(step, 0, StoryProgress.Implemented);
        Log.Info("Geschichte: Neustart ab Schritt " + step + " (" + StoryProgress.StepName(step) + ")");
        StoryProgress.Set(nick, step);
        if (step <= 4) StoryProgress.SetGaveStoryCar(nick, false);
    }

    /// <summary>Unity DebugManager.StoryStartStep (Entwicklermenü): &gt;= 0 = beim nächsten Start ab diesem Schritt.</summary>
    public static int StoryStartStep = -1;

    /// <summary>Geschichte beginnen (ab dem gespeicherten Schritt, mindestens 0).</summary>
    public static void Begin(IGameModeContext ctx, string nick, NetResponse? login)
    {
        End();
        Ctx = ctx; Nick = nick ?? ""; _login = login;
        _pendingBegin = false;
        int step = System.Math.Max(0, StoryProgress.Get(Nick));
        Log.Info("Geschichte: Schritt " + StoryProgress.Get(Nick) + " – " + StoryProgress.StepName(step));
        Go(step);
        // automatischer Modus hinter den Schritten (Unity: Login -> Spiel): danach freie Fahrt
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    /// <summary>Nach dem Inselwechsel nach Ibiza beginnen (Unity: Login läuft nach dem Neuladen der Szene erneut).</summary>
    public static void BeginAfterTravel(IGameModeContext ctx, string nick, NetResponse? login)
    {
        Ctx = ctx; Nick = nick ?? ""; _login = login;
        _pendingBegin = true;
    }

    static bool _ticking;
    static int _pendingStep = -1;
    static bool _pendingAbort;

    /// <summary>Zum Schritt wechseln (speichert den Fortschritt). Aus einem Schritt heraus erst nach dessen Tick.</summary>
    public static void Go(int step)
    {
        if (_ticking) { _pendingStep = step; return; }
        Switch(step);
    }

    static void Switch(int step)
    {
        try { _mode?.Exit(); } catch (Exception ex) { Log.Error("Geschichte Exit: " + ex.Message); }
        _mode = null;
        Log.Debug($"STORY Schritt {Step} -> {step}");
        Step = step;
        CurrentObjective = null;
        StoryProgress.Set(Nick, step);
        _mode = Create(step);
        if (_mode == null)
        {
            Log.Info("Geschichte: Schritt " + step + " (" + StoryProgress.StepName(step) + ") nicht umgesetzt – freie Fahrt");
            GameMode.Pop();
            End();
            return;
        }
        try { _mode.Enter(); }
        catch (Exception ex) { Log.Error("Geschichte Schritt " + step + ": " + ex); Abort(); }
    }

    static StoryStep? Create(int step) => step switch
    {
        0 => new GAME_MODE_AVATAR_CHOICE.AvatarChoiceStep(),
        1 => new GAME_MODE_FIRST_WM.FirstGarageStep(),
        2 => new GAME_MODE_CINE.FirstFreerideStep(),
        3 => new GAME_MODE_CINE.MeetingTessStep(),
        4 => new GAME_MODE_MISSION.DrivingTessStep(),
        5 => new GAME_MODE_CINE.SolarCrownExteriorStep(),
        6 => new GAME_MODE_CINE.SolarCrownInteriorStep(),
        7 => new GAME_MODE_SHOP.UsedCarDealerStep(),
        8 => new GAME_MODE_FREERIDE.TutoFrogStep(),
        9 => new GAME_MODE_MISSION.GoHomeStep(),
        10 => new GAME_MODE_MISSION.CaravanStep(),
        _ => null,
    };

    /// <summary>Fehler: Geschichte abbrechen und mit dem Story-Auto in die freie Fahrt (am Haus EPH_L3DI_5067I).</summary>
    public static void Abort()
    {
        if (_ticking) { _pendingAbort = true; return; }
        Log.Warn("Geschichte abgebrochen – freie Fahrt");
        try { _mode?.Exit(); } catch { }
        _mode = null;
        GameMode.Pop();
        if (NeedsCar) SpawnAtSpot(StoryCar, "EPH_L3DI_5067I");
        StoryFade.Set(0f, default);
        End();
    }

    /// <summary>Unity OnDestroy: Geschichte beendet.</summary>
    static void End()
    {
        bool had = _mode != null;
        try { _mode?.Exit(); } catch { }
        _mode = null;
        if (had || Multiplayer.HideRemotes) Multiplayer.HideRemotes = false;
        CurrentObjective = null;
        WorldWeather.StoryLock = false;
        UpdateRadio(false);
    }

    /// <summary>Jedes Bild (ScreenModule.Update): Start nach dem Inselwechsel, Auto setzen, laufender Schritt.</summary>
    public static void Update(IGameModeContext ctx, float dt)
    {
        Ctx = ctx;
        if (_pendingBegin && !IslandTravel.Pending) { _pendingBegin = false; Begin(ctx, Nick, _login); }
        UpdateSpawn();
        if (SpawnPending && Vm?.Current != null) SpawnPending = false;
        // Tageszeit/Wetter der Geschichte fest (sonnig, trocken, Nachmittag) solange ein Schritt läuft
        WorldWeather.StoryLock = _mode != null;
        if (_mode == null || ctx.Paused) return;
        // Geschichte ist keine freie Fahrt: Mitspieler nicht anzeigen (jedes Bild, Interieurs setzen den Schalter beim Verlassen zurück)
        Multiplayer.HideRemotes = true;
        UpdateRadio(_mode.MutesRadio);
        _ticking = true;
        try { _mode.Tick(dt); }
        catch (Exception ex) { Log.Error("Geschichte Schritt " + Step + ": " + ex); _pendingAbort = true; }
        finally { _ticking = false; }
        if (_pendingAbort) { _pendingAbort = false; _pendingStep = -1; Abort(); return; }
        if (_pendingStep >= 0) { int st = _pendingStep; _pendingStep = -1; Switch(st); }
    }

    /// <summary>Jedes Bild (ScreenModule.Render): Darsteller der Szenen des laufenden Schritts.</summary>
    public static void Render(IGameModeContext ctx)
    {
        var st = ctx.Terrain?.Streamer; var cam = ctx.Terrain?.Camera;
        if (_mode == null || st == null || cam == null) return;
        _mode.Render(st.RootMatrix, cam.Position);
    }

    static bool _radioMuted;

    /// <summary>Autoradio während Kinos/Laufmodus stumm (nur selbst gesetzte Stummschaltung wieder aufheben).</summary>
    static void UpdateRadio(bool mute)
    {
        var r = GAME_MODE_FREERIDE.Radio;
        if (r == null) { _radioMuted = false; return; }
        if (mute && !_radioMuted) { if (!r.Muted) { r.Muted = true; _radioMuted = true; } }
        else if (!mute && _radioMuted) { r.Muted = false; _radioMuted = false; }
    }

    // ------------------------------------------------------------------ Auto setzen (Unity RequestSpawn -> TDU2Login.StorySpawn)
    /// <summary>Wurde ein Auto angefordert, das noch nicht steht?</summary>
    public static bool SpawnPending { get; private set; }

    /// <summary>Spieler ist in einem Interieur (Haus, Händler …): dort gibt es kein Auto – kein Ersatz-Auto auf die Straße setzen.</summary>
    public static bool InInterior => InteriorUtils.Inside;

    /// <summary>Kein Auto, keins angefordert und nicht in einem Interieur: Ersatz-Auto (Fortsetzen nach dem Anmelden) setzen.</summary>
    public static bool NeedsCar => Vm != null && Vm.Current == null && !SpawnPending && !InInterior;

    /// <summary>Auto an Spielposition/-richtung setzen und freie Fahrt beginnen (Unity RequestSpawn + TDU2Login.StorySpawn).</summary>
    public static void RequestSpawn(CarPhysics? car, Vector3 game, Vector3 forwardGame)
    {
        car ??= StoryCar;
        var save = TDU2Session.Save;
        if (car != null && car.Id == StoryCarId && save != null && !save.ownedCars.Exists(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase)))
            StoryProgress.SetGaveStoryCar(Nick, true);
        SpawnPending = true;
        if (car == null) return;
        BeginSpawn(game, forwardGame, (p, d) =>
        {
            var sv = TDU2Session.Save;
            bool owned = sv != null && sv.ownedCars.Exists(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase));
            if (!owned || sv == null || !sv.starterChosen) GAME_MODE_LOGIN.OnStarterChosen(car);
            SpawnCar(car, p, d);
        });
    }

    /// <summary>Auto am Spot (Ausfahrt Position2/Heading2) in die freie Fahrt setzen.</summary>
    public static void SpawnAtSpot(CarPhysics? car, string spotName)
    {
        var s = FindSpot(spotName);
        if (s == null || car == null) { RequestSpawn(car ?? StoryCar, Streamer?.ViewerGame ?? Vector3.Zero, Vector3.UnitZ); return; }
        var at = s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;
        var dir = new Vector3(MathF.Sin(s.Heading2), 0f, MathF.Cos(s.Heading2));
        RequestSpawn(car, at, dir.LengthSquared() > 1e-4f ? Vector3.Normalize(dir) : Vector3.UnitZ);
    }

    /// <summary>Auto an den Start einer Mission (.ig2: erster Checkpoint, Lage und Fahrtrichtung). false = keine Datei/kein Checkpoint.</summary>
    public static bool SpawnAtMissionStart(string ig2)
    {
        var vm = Vm;
        if (vm == null || Streamer == null) return false;
        try
        {
            var path = CarDatabase.FindFile(Path.Combine(vm.BnkDir, "ig2"), ig2);
            if (path == null) return false;
            var ig = Paradise.Core.Formats.Ige2File.Parse(Paradise.Core.Files.GameFiles.ReadAllBytes(path));
            if (ig == null || ig.Checkpoints.Count == 0) return false;
            var cp = ig.Checkpoints[0];
            var dir = cp.Forward; dir.Y = 0f;
            RequestSpawn(StoryCar, cp.Position, dir.LengthSquared() > 1e-4f ? Vector3.Normalize(dir) : Vector3.UnitZ);
            Log.Info("Geschichte: Auto am Start von " + ig2 + " " + cp.Position);
            return true;
        }
        catch (Exception ex) { Log.Warn(ig2 + ": " + ex.Message); return false; }
    }

    static Vector3 _spawnGame, _spawnDir;
    static Action<Vector3, Vector3>? _spawnAction;
    static float _spawnT;
    static float? _fovBefore;

    /// <summary>Unity TDU2Login.BeginSpawn: Kamera hinter die Stelle (Streaming lädt dort), auf den Boden warten, dann die Aktion.</summary>
    static void BeginSpawn(Vector3 game, Vector3 dirGame, Action<Vector3, Vector3> action)
    {
        _spawnGame = game;
        dirGame.Y = 0f;
        _spawnDir = dirGame.LengthSquared() > 1e-4f ? Vector3.Normalize(dirGame) : Vector3.UnitZ;
        _spawnAction = action;
        _spawnT = 0f;
        PlaceSpawnCamera();
    }

    static void PlaceSpawnCamera()
    {
        var t = Terrain; var cam = t?.Camera; var s = Streamer;
        if (t == null || cam == null || s == null) return;
        t.FlyCameraEnabled = false;
        cam.Override = null;
        cam.Position = s.GameToRender(_spawnGame - _spawnDir * 7f + Vector3.UnitY * 3f);
        cam.Yaw = MathF.Atan2(_spawnDir.X, _spawnDir.Z);
        cam.Pitch = -0.15f;
    }

    static void UpdateSpawn()
    {
        if (_spawnAction == null) return;
        _spawnT += Ctx.Paused ? 0f : 1f / 60f;
        var s = Streamer;
        if (s == null) return;
        PlaceSpawnCamera();
        float timeout = new LoginConfig().SpawnTimeout;
        bool ground = s.RaycastDown(_spawnGame + Vector3.UnitY * 60f, 0f, 200f, out _);
        if ((!ground || _spawnT < 0.5f) && _spawnT < timeout) return;
        if (!ground) Log.Warn($"Geschichte: Boden am Startpunkt nach {timeout:0} s nicht geladen – setze trotzdem");
        var a = _spawnAction; _spawnAction = null;
        a(_spawnGame, _spawnDir);
    }

    /// <summary>Unity TDU2Login.SpawnCar: Auto setzen (Lack aus dem Spielstand), Fahrzeuge an, Kamera zurück an das Auto.</summary>
    static void SpawnCar(CarPhysics car, Vector3 game, Vector3 dir)
    {
        var vm = Vm;
        if (vm == null) return;
        var save = TDU2Session.Save;
        int color = -1;
        if (save?.carConfigs != null)
            foreach (var e in save.carConfigs) if (e != null && string.Equals(e.car, car.FileName, StringComparison.OrdinalIgnoreCase)) color = System.Math.Max(0, e.color);
        bool ok = vm.Available.Contains(car) ? vm.SpawnCar(car.FileName, game, dir, color) : vm.SpawnCarExact(car, game, dir);
        if (!ok) { Log.Warn("Geschichte: Auto " + car.FileName + " nicht spawnbar"); SpawnPending = false; }
        else TDU2Session.SetCurrentCar(car.FileName);
        if (Vehicles != null) Vehicles.Suspended = false;
        RestoreCamera();
        SessionAutosave.Schedule(5f);
    }

    /// <summary>Kamera wieder der Engine (Auto-/Freiflug-Kamera) überlassen.</summary>
    internal static void RestoreCamera()
    {
        var t = Terrain;
        if (t == null) return;
        t.FlyCameraEnabled = true;
        if (t.Camera != null) t.Camera.Override = null;
        t.NearClip = 1f;
        if (_fovBefore.HasValue) { t.FieldOfView = _fovBefore.Value; _fovBefore = null; }
        t.Input.CursorLocked = false;
    }

    // ------------------------------------------------------------------ Gebrauchtwagenhändler (Unity TDU2Login.StoryDealer)
    /// <summary>Story-Schritt 7: Gebrauchtwagenhändler (ESTOL_B bzw. Start-Spot des Servers) mit den Startautos des Servers.</summary>
    public static void StarterDealer(Action<CarPhysics> chosen)
    {
        var vm = Vm;
        if (vm == null) return;
        var r = _login ?? TDU2Session.Config;
        var spot = FindSpotCode(r != null && !string.IsNullOrEmpty(r.startSpot) ? r.startSpot : "ESTOL_B") ?? FindSpotCode("ESTOL_B");
        var starters = new List<CarPhysics>();
        if (r != null) foreach (var n in r.starterCars ?? new List<string>()) { var c = vm.FindCar(n); if (c != null && !starters.Contains(c)) starters.Add(c); }
        if (starters.Count == 0) { var c = vm.FindCar(vm.StartCar) ?? vm.Available[0]; starters.Add(c); }
        Action<CarPhysics> done = car => { GAME_MODE_LOGIN.OnStarterChosen(car); chosen?.Invoke(car); };
        if (spot == null || !InteriorUtils.Init(Ctx))
        {
            done(starters[0]);
            RequestSpawn(starters[0], Streamer?.ViewerGame ?? Vector3.Zero, Vector3.UnitZ);
            return;
        }
        BeginSpawn(spot.Position, Vector3.UnitZ, (p, q) =>
        {
            if (Vehicles != null) Vehicles.Suspended = false;
            RestoreCamera();
            if (!InteriorUtils.EnterStarterDealer(spot, starters, done))
            {
                done(starters[0]);
                RequestSpawn(starters[0], spot.Position2.LengthSquared() > 1f ? spot.Position2 : spot.Position, new Vector3(MathF.Sin(spot.Heading2), 0f, MathF.Cos(spot.Heading2)));
            }
        });
    }

    // ------------------------------------------------------------------ Hilfen für die Schritte
    public static Spot? FindSpot(string name) => GamePC.FindSpot(Streamer?.Spots?.Spots, name);

    static Spot? FindSpotCode(string code)
    {
        var spots = Streamer?.Spots?.Spots;
        if (spots == null || string.IsNullOrEmpty(code)) return null;
        foreach (var s in spots) if (string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>Kamera an eine Spielposition (Streaming lädt dort), z.B. vor dem Laden einer Szene.</summary>
    public static void HoldCameraAt(Vector3 game, Vector3 forward)
    {
        var t = Terrain; var cam = t?.Camera; var s = Streamer;
        if (t == null || cam == null || s == null) return;
        if (forward.LengthSquared() < 1e-4f) forward = Vector3.UnitZ;
        _fovBefore ??= t.FieldOfView;
        t.FlyCameraEnabled = false;
        cam.Position = s.GameToRender(game);
        cam.Override = (Vector3.Normalize(forward), Vector3.UnitY);
    }

    /// <summary>Taste einer Aktion gehalten (auch wenn die Fahrtasten gesperrt sind).</summary>
    public static bool ActionHeld(string id)
    {
        var a = TDU2Controls.Get(id);
        if (a == null) return false;
        return (a.Key1 != 0 && TDU2Controls.RawKeyDown(a.Key1)) || (a.Key2 != 0 && TDU2Controls.RawKeyDown(a.Key2));
    }

    /// <summary>Taste einer Aktion gedrückt (auch wenn die Fahrtasten gesperrt sind).</summary>
    public static bool ActionPressed(string id)
    {
        var a = TDU2Controls.Get(id);
        if (a == null) return false;
        return (a.Key1 != 0 && TDU2Controls.RawKeyPressed(a.Key1)) || (a.Key2 != 0 && TDU2Controls.RawKeyPressed(a.Key2));
    }

    /// <summary>Laufmodus (Auswahl im Raum): wie in den Interieurs – Taste "Benutzen" (Laufen-Belegung), Bestätigen, Maus.</summary>
    public static bool ValidatePressed() => TDU2Controls.Pressed("walk_use") || TDU2Controls.ConfirmPressed() || FlashInput.GetMouseButtonDown(0);

    const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
    public static bool SkipPressed() =>
        TDU2Controls.ConfirmPressed() || TDU2Controls.RawKeyPressed(VK_ESCAPE) || TDU2Controls.RawKeyPressed(VK_SPACE) || TDU2Nav.PadBack() || TDU2Nav.StartPressed();

    /// <summary>Kino-Überspringen erlaubt (GamePC.cpr ALLOWSKIPCINE, Standard 1).</summary>
    public static bool AllowSkip
    {
        get
        {
            if (_allowSkip < 0)
            {
                var gp = GamePC.Load(Vm?.BnkDir);
                var v = gp?.Get("ALLOWSKIPCINE");
                _allowSkip = v == null || v.Trim() != "0" ? 1 : 0;
            }
            return _allowSkip == 1;
        }
    }
    static int _allowSkip = -1;
}
