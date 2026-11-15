using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Screens;

namespace Paradise.Game.Online;

/// <summary>
/// Autosave des angemeldeten Profils – Unity TDU2Login.UpdatePlaying/CaptureState/OnApplicationQuit. Läuft in jedem Spielmodus
/// (ScreenModule), sobald der Login das Spiel gestartet hat. Online: Server + lokale DATA, offline: nur lokale DATA.
/// </summary>
public static class SessionAutosave
{
    static float _next = -1f;
    static float _clock;

    /// <summary>Nächstes Speichern in <paramref name="seconds"/> (Login: Startauto gewählt -> kurz danach mit Position).</summary>
    public static void Schedule(float seconds) => _next = _clock + seconds;

    public static void Update(IGameModeContext ctx, float dt)
    {
        _clock += dt;
        if (!TDU2Session.LoggedIn || _next < 0f || _clock < _next) return;
        _next = _clock + MathF.Max(5f, new LoginConfig().AutosaveSeconds);
        CaptureState(ctx);
        TDU2Session.SaveAsync();
    }

    /// <summary>Aktuellen Stand (Auto, Position, Richtung, Insel) in den Spielstand übernehmen.</summary>
    public static void CaptureState(IGameModeContext ctx)
    {
        var save = TDU2Session.Save;
        var vm = ctx.Vehicles?.Manager;
        var s = ctx.Terrain?.Streamer;
        if (save == null || vm == null || s == null) return;
        // Login/Start läuft noch: Spielstand nicht mit der Kameraposition überschreiben
        if (GameMode.Current == Gamemode.GAME_MODE_LOGIN) return;
        // Autoradio: Sender und Lautstärke (Original-DATA RadioOptions)
        var radio = Paradise.Core.Audio.TDU2Radio.Instance;
        if (radio != null)
        {
            var (hash, name) = radio.SavedStation();
            save.radioStation = hash; save.radioStationName = name; save.radioVolume = radio.CurrentVolume;
        }
        // Optionsmenü und Tastenbelegung (Unity PlayerPrefs "TDU2.Opt.*" / "TDU2.Key.*")
        if (Options.Instance != null) Options.Saved = Options.Instance.Export();
        save.options = Options.Saved ?? "";
        save.keys = Paradise.Game.Input.TDU2Controls.Export();
        var car = vm.CurrentCar;
        if (vm.Current == null || car == null) return;
        save.currentCar = car.FileName;
        var g = vm.PositionGame;
        var d = vm.ForwardGame; d.Y = 0f;
        save.px = g.X; save.py = g.Y; save.pz = g.Z;
        save.heading = d.LengthSquared() > 1e-6f ? MathF.Atan2(d.X, d.Z) * (180f / MathF.PI) : 0f;
        save.hasPosition = true;
        save.island = s.Index?.IslandName ?? save.island;
        save.interior = "";   // Interieurs folgen (TDU2Interiors)
        CollectOdometers(save, vm);
    }

    /// <summary>Kilometerstände der eigenen Autos (und des aktuellen) aus dem Tacho in den Spielstand (Unity CollectOdometers).</summary>
    static void CollectOdometers(SaveData save, VehicleManager vm)
    {
        var list = new List<OdoEntry>();
        var seen = new HashSet<int>();
        var names = new List<string>(save.ownedCars);
        if (!string.IsNullOrEmpty(save.currentCar)) names.Add(save.currentCar);
        foreach (var n in names)
        {
            var c = vm.FindCar(n);
            if (c == null || !seen.Add(c.Id)) continue;
            double m = TDU2VehicleHud.GetOdometer(c.Id);
            if (m > 0) list.Add(new OdoEntry { carId = c.Id, meters = m });
        }
        save.odometers = list;
    }

    /// <summary>Beim Beenden: Stand übernehmen, speichern (max. 3 s), abmelden.</summary>
    public static void SaveAndLogout(IGameModeContext ctx)
    {
        if (!TDU2Session.LoggedIn) return;
        try
        {
            CaptureState(ctx);
            if (TDU2Session.Offline)
                // offline ist die DATA der Spielstand: hier synchron schreiben (SaveCopied schreibt im Hintergrund)
                Paradise.Core.Savegame.TDU2SaveGame.WriteAsync(TDU2Session.Nick, TDU2Session.Save, null, "Spiel beendet").Wait(3000);
            else
                TDU2Session.SaveAsync().Wait(3000);
            TDU2Session.Logout();
        }
        catch (Exception e) { Log.Warn("Autosave beim Beenden: " + e.Message); }
    }
}
