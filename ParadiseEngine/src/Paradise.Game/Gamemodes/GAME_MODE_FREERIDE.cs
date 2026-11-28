using Paradise.Core.Audio;
using Paradise.Core.Engine;
using Paradise.Game.Input;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes
{
    /// <summary>
    /// Freifahrt: Welt + Fahrzeug (TerrainModule/VehicleModule fahren/zeichnen), steuert alles der Freifahrt:
    /// HUD/Minimap (Taste "minimap"), Autoradio (Unity TDU2Radio, Anzeige in hud.bnk), große Karte (Taste "map"),
    /// Einfahrten der Spots (Interieurs, Utils/InteriorUtils), ESC = Pausemenü.
    /// </summary>
    public partial class GAME_MODE_FREERIDE : GameModeHandler
    {
        //Flash Screens:
        //hud.bnk (Minimap, Radio)
        //main.bnk (Pause Menu)
        //map.bnk (Big Map)

        public override Gamemode Mode => Gamemode.GAME_MODE_FREERIDE;

        static readonly ScreenDef[] _screens = { ScreenDefs.Hud };
        public override IReadOnlyList<ScreenDef> Screens => _screens;

        public override void Run(IGameModeContext ctx, in GameTime time)
        {
            // Unity TDU2PauseMenu prüft die Menütaste global in ScreenModule (vor jedem Modus).
            if (ctx.Paused) return;
            var veh = ctx.Vehicles; var vm = veh?.Manager;
            // Minimap ein/aus (Unity TDU2VehicleManager.HandleKeys "minimap")
            if (TDU2Controls.Pressed("minimap") && vm?.Minimap != null && vm.Current != null && !veh!.Suspended && !veh.Frozen)
                vm.Minimap.ShowMinimap = !vm.Minimap.ShowMinimap;
            // Taste "map" (M): große Karte (Unity TDU2BigMap.Update – nicht im Interieur, nur mit Welt)
            if (TDU2Controls.Pressed("map") && veh != null && !veh.Suspended && ctx.Terrain?.Streamer != null && !Story.BlocksMap)
                GameMode.Set(Gamemode.GAME_MODE_MAP);
            // Spots betreten (Unity TDU2Interiors.UpdateOutside): Einfahrt eines Händlers/Hauses/Shops + Bestätigen,
            // bzw. nach dem Teleport der Karte ("goin") hinein, sobald die Welt dort steht
            if (ctx.Terrain != null && veh != null) InteriorUtils.UpdateOutside(ctx, time.DeltaTime);
            // Meisterschaften: Treffpunkte/Startpunkte (Einfahrt, Wettbewerb der Karte, Rennen aus der Karte)
            if (GameMode.Current == Gamemode.GAME_MODE_FREERIDE && ctx.Terrain != null && veh != null) Championships.UpdateFreeride(ctx, time.DeltaTime);
            // schwebende Spot-Hologramme an den Einfahrten (pack_spots.bnk)
            if (GameMode.Current == Gamemode.GAME_MODE_FREERIDE && !InteriorUtils.Inside) SpotHolograms.Update(ctx);
            else SpotHolograms.Hide();
        }

        public override void Exit(IGameModeContext ctx, Gamemode to)
        {
            if (to != Gamemode.GAME_MODE_PAUSE) SpotHolograms.Hide();   // Interieur, Karte, Kinos: aus; Pausemenü: Welt bleibt stehen
        }

        // ------------------------------------------------------------------ Autoradio (Unity TDU2Radio, Anzeige in hud.bnk der Minimap)
        // Läuft wie in Unity in jedem Spielmodus weiter (Musik, Tasten) – jedes Bild aus ScreenModule.Update.
        static Radio? _radio;
        /// <summary>Autoradio (null bis zum ersten Bild) – Entwicklermenü F1 > Audio.</summary>
        public static Radio? Radio => _radio;
        static Paradise.Core.Online.SaveData? _radioSave;   // Spielstand, aus dem Sender/Lautstärke zuletzt übernommen wurden

        static Radio CreateRadio()
        {
            var c = new Config.AudioConfig();   // Grundwerte: Game/Config/AudioConfig.cs
            var r = new Radio(Paradise.Core.Config.GameData.BnkDir)
            {
                PlayOriginalRadio = c.PlayOriginalRadio,
                JingleChance = c.RadioJingleChance,
                CommentChance = c.RadioCommentChance,
                AdChance = c.RadioAdChance,
                MarqueeLength = System.Math.Max(1, c.RadioMarqueeLength),
                MarqueeSpeed = System.Math.Max(0.05f, c.RadioMarqueeSpeed),
                CurrentVolume = System.Math.Clamp(c.RadioVolume, 0, 11),
            };
            if (Enum.TryParse<RadioStation>(c.RadioStation, true, out var st)) r.CurrentStation = st;
            return r;
        }

        /// <summary>Jedes Bild (alle Spielmodi): Radio anlegen, HUD-Anzeige, Sender/Lautstärke aus dem Spielstand, Tasten.</summary>
        public static void UpdateRadio(IGameModeContext ctx, float dt)
        {
            _radio ??= CreateRadio();
            // hud.bnk gehört der Minimap (wird mit dem Fahrzeug geladen) – Unity TDU2Minimap.LoadHud -> TDU2Radio.SetHudPlayer
            _radio.SetHudPlayer(ctx.Vehicles?.Manager?.Minimap?.Hud);
            // nach dem Anmelden: Sender und Lautstärke aus dem Spielstand (Original-DATA RadioOptions)
            var save = Paradise.Core.Online.TDU2Session.LoggedIn ? Paradise.Core.Online.TDU2Session.Save : null;
            if (!ReferenceEquals(save, _radioSave))
            {
                _radioSave = save;
                if (save != null && save.radioVolume >= 0) _radio.ApplySaved(save.radioStation, save.radioStationName, save.radioVolume);
            }
            // Tasten wie Unity TDU2Radio (Standard Ziffernblock 5 Anzeige, 4/6 Sender, 8/2 Lautstärke; Belegung in KeyConfig)
            if (TDU2Controls.Pressed("radio_open")) _radio.Toggle();
            if (TDU2Controls.Pressed("radio_prev")) _radio.ChangeStation(-1);
            else if (TDU2Controls.Pressed("radio_next")) _radio.ChangeStation(1);
            else if (TDU2Controls.Pressed("radio_volup")) _radio.ChangeVolume(1);
            else if (TDU2Controls.Pressed("radio_voldown")) _radio.ChangeVolume(-1);
            _radio.Update(dt);
        }

        /// <summary>Beim Beenden (ScreenModule.Unload).</summary>
        public static void DisposeRadio() { _radio?.Dispose(); _radio = null; _radioSave = null; }

        // ------------------------------------------------------------------ Umgebungsgeräusche (ambiences.xmb + Sektor-Klangzonen .psa)
        static Ambience? _ambience;
        static string? _ambienceIsland;
        /// <summary>Umgebungsgeräusche der aktuellen Insel – Entwicklermenü F1 > Audio.</summary>
        public static Ambience? Ambience => _ambience;
        static int _thunderSeen = Paradise.Core.World.Lightning.Strikes;

        /// <summary>
        /// Jedes Bild (aus ScreenModule.Update): Umgebung der Insel an der Kameraposition. Hörbar in der Welt (Freifahrt,
        /// Probefahrt; Karte/Pause halten über AudioEngine.Paused an), im Interieur und ohne Welt ausgeblendet.
        /// </summary>
        public static void UpdateAmbience(IGameModeContext ctx, float dt)
        {
            var streamer = ctx.Terrain?.Streamer;
            if (streamer == null) { _ambience?.Update(-1, false, 0f, false, dt); return; }
            if (_ambience == null || _ambienceIsland != streamer.Island)
            {
                _ambience?.Dispose();
                _ambienceIsland = streamer.Island;
                _ambience = Ambience.Load(Paradise.Core.Config.GameData.BnkDir, streamer.Island);
                if (_ambience == null) return;
                _ambience.Volume = System.Math.Clamp(new Config.AudioConfig().Ambience, 0f, 1f);
            }
            if (_ambience == null) return;
            var mode = GameMode.Current;
            bool world = mode is Gamemode.GAME_MODE_FREERIDE or Gamemode.GAME_MODE_MISSION or Gamemode.GAME_MODE_TESTCAR or Gamemode.GAME_MODE_MAP or Gamemode.GAME_MODE_PAUSE;
            bool active = world && !Paradise.Core.World.WorldWeather.Inside && ctx.Vehicles?.Manager?.Current != null;
            var p = streamer.ViewerGame;
            int zone = active ? streamer.AmbienceZoneAt(p.X, p.Z) : -1;
            _ambience.Update(zone, Paradise.Core.World.WorldWeather.Night > 0.5f, Paradise.Core.World.WorldWeather.Rain, active, dt);
            // Donner: WFX_Lightning meldet den Einschlag, die Umgebung spielt ihn (Original 0x657340 auf dem Umgebungs-Manager)
            int strikes = Paradise.Core.World.Lightning.Strikes;
            if (strikes != _thunderSeen) { _thunderSeen = strikes; _ambience.PlayLightning(); }
        }

        public static void DisposeAmbience() { _ambience?.Dispose(); _ambience = null; _ambienceIsland = null; }
    }
}
