using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Platform;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.World;

namespace Paradise.Game.Screens;

/// <summary>
/// Führt den aktiven Spielmodus aus: je GAME_MODE_* eine Klasse in Game/Gamemodes (<see cref="GameModes.Get"/>).
/// Bei einem Wechsel: alter Modus Exit, dessen Oberflächen aus, Oberflächen des neuen Modus ein (+ Musik), neuer Modus Enter;
/// danach jedes Bild Run/Render des aktiven Modus. Laden/Abspielen/Zeichnen der Flash-Filme und Sounds liegt in Paradise.Core.
/// </summary>
public sealed class ScreenModule : GameModule, IGameModeContext
{
    readonly IGameWindow _window;
    readonly LaunchOptions _opts;
    readonly TerrainModule? _terrain;
    readonly VehicleModule? _vehicles;
    readonly Action? _quit;
    readonly Dictionary<string, VmfPlayer?> _films = new(StringComparer.OrdinalIgnoreCase);
    VmfPlayer? _general;
    Paradise.Core.Online.SaveData? _profileSave;   // Spielstand, aus dem Optionen/Tasten zuletzt übernommen wurden
    GameModeHandler? _active;

    /// <summary>Startmodus (Program: --gamemode, --login, --freeride).</summary>
    public Gamemode StartMode = Gamemode.GAME_MODE_LOGIN;

    // Test-Umschalter F5/F6/F7 (KeyConfig "dev_*"), bis Login/Pausemenü/Karte als Abläufe portiert sind
    readonly bool[] _was = new bool[256];

    public ScreenModule(IGameWindow window, LaunchOptions opts, TerrainModule? terrain, VehicleModule? vehicles, Action? quit)
    {
        _window = window; _opts = opts; _terrain = terrain; _vehicles = vehicles; _quit = quit;
    }

    /// <summary>Aktiver Spielmodus (Klasse aus Game/Gamemodes).</summary>
    public GameModeHandler? Active => _active;

    public override void Load()
    {
        GameMode.Changed += OnModeChanged;
        // general.bnk: Popups/Einladungen – immer geladen (TDU2GameData)
        _general = Open(ScreenDefs.General);
        GeneralOverlay.Attach(_general);   // saving_logo / online_evt / deco_pop (Unity TDU2GameData)
        // Ladebildschirm (Interieur, Inselwechsel, Teleport): Speicher-Symbol darüber, Welt fertig = Streamer um die Kamera + kein Auto im Laden
        TDU2LoadingScreen.General = _general;
        TDU2LoadingScreen.WorldReadyCheck = radius =>
        {
            var s = _terrain?.Streamer; var cam = _terrain?.Camera;
            if (s == null || cam == null) return true;
            if (_vehicles?.Manager != null && _vehicles.Manager.IsLoadingCar) return false;
            return s.ReadyAround(cam.Position, radius);
        };
        bool same = GameMode.Current == StartMode;
        GameMode.Set(StartMode);
        if (same) OnModeChanged(StartMode, StartMode);   // kein Wechsel-Ereignis: Modus trotzdem starten
    }

    public override void Update(in GameTime time)
    {
        TDU2Controls.Update(_window.Input, time.FrameCount);   // Tastenbelegung (Game/Config/KeyConfig.cs)
        if (TDU2Controls.Pressed("dev_login")) GameMode.Set(Gamemode.GAME_MODE_LOGIN);
        if (TDU2Controls.Pressed("dev_freeride")) GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
        if (TDU2Controls.Pressed("dev_map")) GameMode.Set(GameMode.Current == Gamemode.GAME_MODE_MAP ? Gamemode.GAME_MODE_FREERIDE : Gamemode.GAME_MODE_MAP);
        _active?.Run(this, in time);
        ImportProfile();
        GAME_MODE_FREERIDE.UpdateRadio(this, time.DeltaTime);   // Autoradio läuft in jedem Modus (Unity TDU2Radio)
        GAME_MODE_FREERIDE.UpdateAmbience(this, time.DeltaTime);   // Umgebungsgeräusche der Insel (ambiences.xmb, Klangzonen .psa)
        SessionAutosave.Update(this, time.DeltaTime);   // Unity TDU2Login.UpdatePlaying (läuft in jedem Modus)
        TDU2GameData.Update(this, time.DeltaTime);       // Online-Status, Spielerzahl, Flash-Variablen (Unity TDU2GameData)
        GeneralOverlay.Update(time.DeltaTime);
        TDU2LoadingScreen.Update();
        if (GeneralOverlay.DisconnectedOpen && (Pressed(0x0D) || TDU2GameData.Online)) GeneralOverlay.CloseDisconnected();
    }

    public override void Render(in GameTime time) => _active?.Render(this, in time);

    public override void Unload()
    {
        SessionAutosave.SaveAndLogout(this);   // Unity TDU2Login.OnApplicationQuit
        GameMode.Changed -= OnModeChanged;
        _active?.Exit(this, GameMode.Current);
        _active = null;
        FrontendMusic.Stop();
        GAME_MODE_FREERIDE.DisposeRadio();
        GAME_MODE_FREERIDE.DisposeAmbience();
        foreach (var f in _films.Values) { if (f != null) { FrontendSounds.Detach(f); FlashHost.Remove(f); } }
        _films.Clear();
    }

    void OnModeChanged(Gamemode old, Gamemode now)
    {
        var next = GameModes.Get(now);
        if (_active != null && !ReferenceEquals(_active, next)) _active.Exit(this, now);
        var want = next.Screens;
        // nicht mehr benötigte Bildschirme ausblenden (geladen bleiben – schneller Wechsel)
        foreach (var kv in _films)
        {
            if (kv.Value == null || ReferenceEquals(kv.Value, _general)) continue;
            bool keep = false;
            foreach (var s in want) if (string.Equals(s.Bank, kv.Key, StringComparison.OrdinalIgnoreCase)) keep = true;
            if (!keep) kv.Value.Visible = false;
        }

        string? music = null;
        foreach (var s in want)
        {
            var p = Open(s);
            if (p != null) p.Visible = true;
            music ??= s.Music;
        }
        if (music != null) FrontendMusic.Play(music);
        else if (FrontendMusic.Current != null) FrontendMusic.Stop();

        _active = next;
        _active.Enter(this, old);
    }

    // ---- IGameModeContext ----

    /// <summary>Pausemenü (main.bnk) offen? Eigener Spielmodus GAME_MODE_PAUSE (Gamemodes/GAME_MODE_PAUSE.cs).</summary>
    public bool Paused
    {
        get => GameMode.Current == Gamemode.GAME_MODE_PAUSE;
        set
        {
            if (value) GAME_MODE_PAUSE.Pause();
            else GAME_MODE_PAUSE.Resume();
        }
    }

    /// <summary>Film laden (einmal) – Bank wie im Unity-Projekt unter frontend/hires|medres|lowres gesucht.</summary>
    public VmfPlayer? Open(ScreenDef s)
    {
        if (_films.TryGetValue(s.Bank, out var have)) return have;
        VmfPlayer? p = null;
        var path = FlashHost.FindFrontendBank(s.Bank);
        if (path == null) Log.Warn("Oberfläche: frontend/<res>/" + s.Bank + " nicht gefunden");
        else
        {
            p = FlashHost.Load(path, s.Depth, true, s.Before);
            if (p != null)
            {
                p.MouseEnabled = s.Mouse;
                FrontendSounds.Attach(p);   // FSCommand "sndp:<id>" -> Oberflächen-Sound
                p.FSCommand += (c, a) => Log.Debug("[Flash] " + s.Bank + " FSCommand " + c + " " + a);
            }
        }
        _films[s.Bank] = p;
        return p;
    }

    public LaunchOptions Options => _opts;
    public TerrainModule? Terrain => _terrain;
    public VehicleModule? Vehicles => _vehicles;
    public void Quit() => _quit?.Invoke();

    // ------------------------------------------------------------------ Profil (Optionsmenü, Tastenbelegung)
    /// <summary>Nach dem Anmelden: Optionsmenü und Tastenbelegung aus dem Spielstand (Unity PlayerPrefs). Radio: GAME_MODE_FREERIDE.</summary>
    void ImportProfile()
    {
        var save = Paradise.Core.Online.TDU2Session.LoggedIn ? Paradise.Core.Online.TDU2Session.Save : null;
        if (ReferenceEquals(save, _profileSave)) return;
        _profileSave = save;
        if (save == null) return;
        Paradise.Game.Screens.Options.Saved = save.options ?? "";
        // Unity: Sprache/Optionen gelten ab Spielstart (TDU2PauseMenu.SavedLanguage, TDU2Options.ApplyAll) – nicht erst beim ersten ESC
        if (Paradise.Game.Screens.Options.Instance == null) ((GAME_MODE_PAUSE)GameModes.Get(Gamemode.GAME_MODE_PAUSE)).EnsureMenu(this);
        else
        {
            Paradise.Game.Screens.Options.SetLanguage(Paradise.Game.Screens.Options.SavedLanguage());
            Paradise.Game.Screens.Options.Instance.Import(Paradise.Game.Screens.Options.Saved);
        }
        TDU2Controls.Import(save.keys);
    }

    public bool Pressed(int vk)
    {
        bool down = _window.Input.IsKeyDown(vk);
        bool r = down && !_was[vk];
        _was[vk] = down;
        return r;
    }
}
