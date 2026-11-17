using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.Input;
using Paradise.Game.Online;
using System.Globalization;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1), zweiter Teil: Inselwechsel, Player &amp; Online (Netzwerk, Spieler, Spielstand/Geld), Eingabegeräte,
/// Debug-Ansicht/Freiflug-Kamera, Flash Debug/Profiler (Unity TDU2DevMenu: NetworkMenu, SavegameMenu, ControlsMenu,
/// DebugViewMenu, FlashMenu, FlashProfilerMenu) und Audio (Lautstärken, was gerade spielt).
/// </summary>
public sealed partial class DevMenu
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ Inselwechsel (Unity TDU2IslandTravel)
    static readonly (string island, string name, string arrival)[] Islands =
    {
        ("ibiza", "Ibiza (airport)", "EAIR"),
        ("hawai", "Hawaii / Oahu (airport)", "EAIR"),
        ("casino", "Casino (landing pad)", "ELAND"),
    };

    List<Item> TravelItems()
    {
        var l = new List<Item> { Head("ISLAND TRAVEL  (flight: arrival at the airport / landing pad, car comes along)") };
        l.Add(Info("Travel", () => IslandTravel.Pending ? "to " + IslandTravel.DisplayName(IslandTravel.Target) + " …" : "-"));
        foreach (var (island, name, arrival) in Islands)
        {
            string isl = island, arr = arrival;
            l.Add(new Item
            {
                Label = "Teleport -> " + name,
                Value = () => string.Equals(Streamer?.Island, isl, StringComparison.OrdinalIgnoreCase) ? "<- here"
                    : IslandTravel.CanTravelTo(isl, Paradise.Core.Config.GameData.BnkDir) ? "[Enter]" : "no Bnk/islands/" + isl,
                Activate = () => Travel(isl, arr),
            });
        }
        return l;
    }

    void Travel(string island, string arrival)
    {
        if (IslandTravel.Pending) { Toast("Island travel already running"); return; }
        if (WorldWeather.Inside) { Toast("Leave the interior first"); return; }
        if (string.Equals(Streamer?.Island, island, StringComparison.OrdinalIgnoreCase)) { Toast("Already on " + IslandTravel.DisplayName(island)); return; }
        if (!IslandTravel.CanTravelTo(island, Paradise.Core.Config.GameData.BnkDir)) { Toast("Island folder missing: Bnk/islands/" + island); return; }
        IslandTravel.Go(island, true, Mgr?.CurrentCar?.FileName, arrival);
        Toast("Flight to " + IslandTravel.DisplayName(island));
        Close();
    }

    // ------------------------------------------------------------------ Avatare (Unity AvatarMenu / ClothesMenu)
    static readonly string[] SexNames = { "Male", "Female" };
    static readonly string[] EthNames = { "European", "Asian", "African" };
    static readonly (ClothePart part, string name)[] AvatarParts =
    {
        (ClothePart.Hair, "Hair"), (ClothePart.Top, "Top"), (ClothePart.Bottom, "Trousers / skirt"),
        (ClothePart.Dress, "Dress"), (ClothePart.Shoes, "Shoes"), (ClothePart.Helmet, "Hat / helmet"),
        (ClothePart.Glasses, "Glasses"), (ClothePart.Gloves, "Gloves"), (ClothePart.Beard, "Beard"),
        (ClothePart.Accessories, "Accessories"), (ClothePart.Belt, "Belt"),
    };
    List<string>? _clipNames;

    static AvatarConfig Cfg => AvatarConfigs.Local;
    static void SetCfg(AvatarConfig c) => AvatarConfigs.SetLocal(c);

    static Item LightFloat(string label, Func<float> get, Action<float> set, float step, float min, float max, float def) =>
        Float(label, get, v => { set(v); AvatarLight.Changed(); }, step, min, max, def, "0.00");

    static float Gray(System.Numerics.Vector4 c) => c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;

    List<Item> AvatarMenu()
    {
        var l = new List<Item>();
        if (!AvatarLibrary.Ready) { l.Add(Info("Avatar data not loaded yet (database)")); return l; }
        l.Add(Head("OWN AVATAR"));
        l.Add(Table("Model (sex)", SexNames, () => Cfg.Sex, v => { var c = AvatarConfig.Default(v); c.Ethnic = Cfg.Ethnic; SetCfg(c); Toast("Model: " + SexNames[v]); }, 0));
        l.Add(Table("Ethnicity (head, skin)", EthNames, () => Cfg.Ethnic, v => { var c = Cfg; c.Ethnic = v; SetCfg(c); }, 0));
        l.Add(Menu("Clothes", ClothesMenu));
        l.Add(Action_("Default look", () => { var c = AvatarConfig.Default(Cfg.Sex); c.Ethnic = Cfg.Ethnic; SetCfg(c); Toast("Default look"); }));
        l.Add(Info("Encoded", () => { var s = AvatarConfigs.LocalCode ?? "-"; return s.Length > 60 ? s.Substring(0, 60) + "…" : s; }));
        l.Add(Head("DISPLAY"));
        l.Add(Bool("Show drivers", () => CarAvatar.ShowDrivers, v => CarAvatar.ShowDrivers = v, true));
        l.Add(Bool("Show passengers", () => CarAvatar.ShowPassengers, v => CarAvatar.ShowPassengers = v, true));
        l.Add(Bool("NPCs in interiors", () => InteriorNpcs.Show, v => InteriorNpcs.Show = v, true));
        l.Add(Bool("Hide head in cockpit view", () => CarAvatar.All.Count == 0 || CarAvatar.All[0].HideHeadInCockpit,
            v => { foreach (var a in CarAvatar.All) a.HideHeadInCockpit = v; }, true));
        l.Add(Head("SHADER (fine tuning, 0 = original; defaults: Game/Config/LightingConfig.cs)"));
        l.Add(LightFloat("Skin ramp + boost", () => AvatarLight.RampAdd, v => AvatarLight.RampAdd = v, 0.05f, -1.5f, 2f, 0f));
        l.Add(LightFloat("Skin ramp row (0 = COEFFS.z)", () => AvatarLight.RampRow, v => AvatarLight.RampRow = v, 0.05f, 0f, 1f, 0f));
        l.Add(LightFloat("Ambient on lit side (0 = original)", () => AvatarLight.AddMix, v => AvatarLight.AddMix = v, 0.05f, 0f, 1f, 0f));
        l.Add(LightFloat("Extra ambient light", () => AvatarLight.AmbAdd, v => AvatarLight.AmbAdd = v, 0.02f, -0.5f, 1f, 0f));
        l.Add(LightFloat("Extra hair sheen", () => AvatarLight.HairSheen, v => AvatarLight.HairSheen = v, 0.05f, -0.33f, 1f, 0f));
        l.Add(LightFloat("Indoor: ceiling light", () => Gray(AvatarLight.IndoorLight), v => AvatarLight.IndoorLight = new(v, v * 0.94f, v * 0.86f, 1f), 0.02f, 0f, 2f, 0.47f));
        l.Add(LightFloat("Indoor: ambient top", () => Gray(AvatarLight.IndoorAmbTop), v => AvatarLight.IndoorAmbTop = new(v, v * 0.96f, v * 0.9f, 1f), 0.02f, 0f, 2f, 0.44f));
        l.Add(LightFloat("Indoor: ambient bottom", () => Gray(AvatarLight.IndoorAmbBottom), v => AvatarLight.IndoorAmbBottom = new(v, v * 0.95f, v * 0.9f, 1f), 0.02f, 0f, 2f, 0.19f));
        l.Add(Head("ANIMATION"));
        _clipNames ??= AvatarLibrary.ClipNames();
        var clips = _clipNames;
        l.Add(new Item
        {
            Label = "Clip (avatarsdriver.bnk)",
            Value = () => string.IsNullOrEmpty(CarAvatar.DebugClip) ? "Seated pose (normal)" : CarAvatar.DebugClip!,
            Change = d =>
            {
                int i = clips.IndexOf(CarAvatar.DebugClip ?? "");
                int n = clips.Count + 1;   // -1 = normal
                i = ((i + 1 + d) % n + n) % n - 1;
                CarAvatar.DebugClip = i < 0 ? null : clips[i];
            },
            Reset = () => CarAvatar.DebugClip = null,
        });
        l.Add(Float("Playback speed", () => CarAvatar.DebugSpeed, v => CarAvatar.DebugSpeed = v, 0.1f, 0f, 4f, 1f, "0.0", "x"));
        l.Add(Bool("IK hands/feet (wheel, pedals)", () => CarAvatar.DebugIK, v => CarAvatar.DebugIK = v, true));
        l.Add(Head("INFO / ACTIONS"));
        l.Add(Info("Avatars in cars / NPCs", () => CarAvatar.All.Count + " / " + InteriorNpcs.Npcs.Count));
        l.Add(Info("Clips in avatarsdriver.bnk", () => clips.Count.ToString(Inv)));
        l.Add(Action_("Rebuild all avatars", () =>
        {
            foreach (var a in CarAvatar.All.ToArray())
            {
                var code = a.ConfigCode?.Invoke();
                a.Rebuild(AvatarConfig.Decode(code) ?? a.Avatar?.Config ?? Cfg);
            }
            Toast("Avatars rebuilt");
        }));
        return l;
    }

    static List<ClotheInfo> PartItems(ClothePart part, int sex)
    {
        var res = new List<ClotheInfo>();
        foreach (var c in AvatarLibrary.AllClothes)
            if (c.Part == part && c.Valid && !c.IsNothing && (sex == 1 ? c.ForWoman : c.ForMan) && AvatarLibrary.HasBank(c)) res.Add(c);
        res.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        return res;
    }

    List<Item> ClothesMenu()
    {
        var l = new List<Item> { Head("CLOTHES  (Left/Right item · Shift x10 · Delete = remove)") };
        int sex = Cfg.Sex;
        foreach (var (part, name) in AvatarParts)
        {
            var p = part;
            if (p == ClothePart.Dress && sex != 1) continue;
            if (p == ClothePart.Beard && sex == 1) continue;
            var list = PartItems(p, sex);
            if (list.Count == 0) continue;
            l.Add(new Item
            {
                Label = name + "  (" + list.Count + ")",
                Value = () => Cfg.Worn(p)?.File ?? "-",
                Change = d =>
                {
                    var c = Cfg; var w = c.Worn(p);
                    int i = w != null ? list.IndexOf(w) : -1;
                    int n = list.Count + 1;   // -1 = nichts
                    i = ((i + 1 + d) % n + n) % n - 1;
                    c.Remove(p);
                    if (i >= 0) c.Set(list[i], 0);
                    SetCfg(c);
                },
                Reset = () => { var c = Cfg; c.Remove(p); SetCfg(c); },
            });
            l.Add(new Item
            {
                Label = "    Colour",
                Value = () => { var w = Cfg.Worn(p); return w != null ? (Cfg.ColorOf(w.Hash) + 1) + " / " + Math.Max(1, w.NbColors) : "-"; },
                Change = d =>
                {
                    var c = Cfg; var w = c.Worn(p);
                    if (w == null) return;
                    int n = Math.Max(1, w.NbColors);
                    int col = ((c.ColorOf(w.Hash) + Math.Sign(d)) % n + n) % n;
                    c.Set(w, col); SetCfg(c);
                },
                Reset = () => { var c = Cfg; var w = c.Worn(p); if (w != null) { c.Set(w, 0); SetCfg(c); } },
            });
        }
        return l;
    }

    // ------------------------------------------------------------------ GPS / Lesezeichen (Unity GpsMenu)
    List<Item> GpsMenu()
    {
        var gps = Gps.Instance;
        var l = new List<Item> { Head("GPS  (defaults: Game/Config/GpsConfig.cs)") };
        if (gps == null) l.Add(Info("GPS not active yet (minimap/HUD not loaded)"));
        else
        {
            l.Add(Info("Target", () => gps.HasTarget ? Num(gps.TargetGame.X) + " / " + Num(gps.TargetGame.Y) + " / " + Num(gps.TargetGame.Z) : "none (big map: right click or mini menu)"));
            l.Add(Info("Remaining", () => gps.HasTarget && gps.Remaining >= 0f ? Num(gps.Remaining) + " m (" + gps.Route.Count + " points)" : "-"));
            l.Add(Action_("Clear target", () => { gps.ClearTarget(); Toast("GPS target cleared"); }));
        }
        l.Add(Float("Arrived below", () => TDU2GpsSettings.ArriveDistance, v => TDU2GpsSettings.ArriveDistance = v, 5f, 5f, 200f, 50f, "0", " m"));
        l.Add(Float("Path width", () => TDU2GpsSettings.PathWidth, v => TDU2GpsSettings.PathWidth = v, 0.002f, 0.004f, 0.08f, 0.020f, "0.000"));
        l.Add(Float("Ring spread", () => TDU2GpsSettings.RingSpread, v => TDU2GpsSettings.RingSpread = v, 5f, 5f, 180f, 30f, "0", "°"));
        l.Add(Bool("Miles", () => TDU2GpsSettings.Miles, v => TDU2GpsSettings.Miles = v, false));
        l.Add(Bool("Distance voices", () => TDU2GpsSettings.DistanceVoices, v => TDU2GpsSettings.DistanceVoices = v, true));
        l.Add(Head("VOICE (Sound/Voices/<language>/GPS.bnk)"));
        l.Add(Bool("Voice", () => TDU2GpsSettings.Voice, v => TDU2GpsSettings.Voice = v, true));
        l.Add(Float("Volume", () => TDU2GpsSettings.VoiceVolume, v => TDU2GpsSettings.VoiceVolume = v, 0.05f, 0f, 1f, 0.85f, "0.00"));
        var langs = new[] { "", "ge", "us", "fr", "it", "sp", "ja" };
        var langNames = new[] { "System language", "German (ge)", "English (us)", "French (fr)", "Italian (it)", "Spanish (sp)", "Japanese (ja)" };
        l.Add(Table("Language", langNames, () => Math.Max(0, Array.IndexOf(langs, TDU2GpsSettings.VoiceLanguage ?? "")), v => TDU2GpsSettings.VoiceLanguage = langs[v], 0));
        int count = Math.Max(17, gps?.VoiceCount ?? 0);
        l.Add(Int("Target set", () => TDU2GpsSettings.VoiceTargetSet, v => TDU2GpsSettings.VoiceTargetSet = v, 0, count, 0));
        l.Add(Int("Arrived", () => TDU2GpsSettings.VoiceArrived, v => TDU2GpsSettings.VoiceArrived = v, 0, count, 16));
        l.Add(Int("Left soon", () => TDU2GpsSettings.VoiceTurnLeftSoon, v => TDU2GpsSettings.VoiceTurnLeftSoon = v, 0, count, 4));
        l.Add(Int("Right soon", () => TDU2GpsSettings.VoiceTurnRightSoon, v => TDU2GpsSettings.VoiceTurnRightSoon = v, 0, count, 2));
        l.Add(Int("Left now", () => TDU2GpsSettings.VoiceTurnLeftNow, v => TDU2GpsSettings.VoiceTurnLeftNow = v, 0, count, 5));
        l.Add(Int("Right now", () => TDU2GpsSettings.VoiceTurnRightNow, v => TDU2GpsSettings.VoiceTurnRightNow = v, 0, count, 3));
        l.Add(Int("U-turn", () => TDU2GpsSettings.VoiceUTurn, v => TDU2GpsSettings.VoiceUTurn = v, 0, count, 1));
        if (gps != null)
        {
            l.Add(Head("LISTEN TO VOICES"));
            for (int i = 1; i <= count; i++)
            {
                int k = i;
                l.Add(Action_("VFGPSVoice" + k.ToString("000", Inv) + " play", () => { bool on = TDU2GpsSettings.Voice; TDU2GpsSettings.Voice = true; gps.Voice(k); TDU2GpsSettings.Voice = on; Toast("GPS voice " + k); }));
            }
        }
        l.Add(Head("BOOKMARKS (big map: mini menu > bookmark · filter 'my spots')"));
        l.Add(Info("Saved", () => TDU2Bookmarks.All.Count + " / " + TDU2Bookmarks.Max + (TDU2Session.LoggedIn ? "  (savegame)" : "  (local)")));
        foreach (var b in new List<TDU2Bookmarks.Mark>(TDU2Bookmarks.All))
        {
            var bm = b;
            l.Add(new Item
            {
                Label = "Bookmark " + bm.Id + "  (" + IslandTravel.DisplayName(bm.Island) + " " + Num(bm.Pos.X) + " / " + Num(bm.Pos.Z) + ")",
                Value = () => "Enter = GPS target · Delete = remove",
                Activate = () =>
                {
                    if (!string.Equals(bm.Island, Streamer?.Island, StringComparison.OrdinalIgnoreCase)) { Toast("Bookmark is on " + IslandTravel.DisplayName(bm.Island)); return; }
                    if (Gps.Instance == null) { Toast("GPS not active"); return; }
                    Gps.Instance.SetTarget(bm.Pos); Toast("GPS target: bookmark " + bm.Id);
                },
                Reset = () => { TDU2Bookmarks.Remove(bm.Id); Toast("Bookmark deleted"); Rebuild(); },
            });
        }
        l.Add(Info("Favourite spots", () => TDU2Session.Save?.favoriteSpots?.Count.ToString(Inv) ?? "local"));
        return l;
    }

    // ------------------------------------------------------------------ Netzwerk (Unity NetworkMenu)
    List<Item> NetworkMenu()
    {
        var l = new List<Item>
        {
            Head("STATUS"),
            Info("Logged in", () => TDU2Session.LoggedIn ? "YES (" + TDU2Session.Nick + ", Id " + TDU2Session.PlayerId + ")" : "NO"),
            Info("Session", () => TDU2Session.Offline ? "offline profile" : "server"),
            Info("Online", () => TDU2GameData.Online ? "YES" : "NO"),
            Info("Players online", () => TDU2GameData.PlayerCount.ToString(Inv)),
            Info("Lobby", () => TDU2GameData.Lobby + "  (" + TDU2GameData.LobbyCount + " / " + TDU2GameData.LobbyMax + ")"),
            Info("Own island", () => IslandTravel.DisplayName(Streamer?.Island) + "  (code " + TDU2GameData.LocalIsland + ")"),
            Info("Server time", () => DateTimeOffset.FromUnixTimeMilliseconds((long)(TDU2Session.ServerSeconds * 1000.0)).ToLocalTime().ToString("HH:mm:ss", Inv)),
            Info("Saving", () => TDU2Session.Saving ? "running" : "-"),
            Head("SETTINGS"),
            Bool("Player join/leave messages", () => TDU2GameData.ShowPlayerEvents, v => TDU2GameData.ShowPlayerEvents = v, true),
            Float("Status message duration", () => TDU2GameData.StatusSeconds, v => TDU2GameData.StatusSeconds = v, 0.5f, 0.5f, 15f, 3f, "0.0", " s"),
            PhBool("Hide other players", false),
            PhBool("Names above players", true),
            PhBool("Collision with players", true),
            PhBool("Other players' engine sounds", true),
            PhFloat("Send rate", 1f, 2f, 60f, 20f, "0", " /s"),
            PhFloat("Interpolation", 0.01f, 0f, 1f, 0.12f, "0.00", " s"),
            PhMenu("Network Debug (detailed)", "Multiplayer (UDP positions) is not ported yet"),
            Head("ACTIONS"),
            Action_("Save game now", () =>
            {
                if (!TDU2Session.LoggedIn) { Toast("Not logged in"); return; }
                TDU2Session.SaveAsync(); Toast("Saving game");
            }),
            Menu("Players (" + TDU2GameData.Players.Count + ")", PlayersMenu),
        };
        return l;
    }

    List<Item> PlayersMenu()
    {
        var l = new List<Item> { Head("PLAYERS") };
        var list = new List<PlayerInfo>(TDU2GameData.Players.Values);
        list.Sort((a, b) => string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));
        foreach (var p in list)
        {
            var pl = p;
            l.Add(Info(pl.Nick.Length > 0 ? pl.Nick : "#" + pl.Id, () =>
                (pl.Online ? "online" : "offline") + "  ·  " + (pl.Island == 2 ? "Hawaii" : pl.Island == 1 ? "Ibiza" : "?") +
                "  ·  $ " + pl.Money.ToString("N0", Inv) + "  ·  cars " + pl.NbCars + (pl.CarName.Length > 0 ? "  ·  " + pl.CarName : "")));
        }
        if (list.Count == 0) l.Add(Info("no other players"));
        return l;
    }

    // ------------------------------------------------------------------ Spielstand / Geld (Unity SavegameMenu)
    List<Item> SavegameMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("PROFILE") };
        var save = TDU2Session.Save;
        l.Add(Info("Nick", () => TDU2Session.Nick ?? "-"));
        l.Add(Info("Logged in", () => TDU2Session.LoggedIn ? "YES" : "NO"));
        if (save == null) { l.Add(Info("no savegame loaded (log in first)")); return l; }
        l.Add(Info("Island / position", () => (string.IsNullOrEmpty(save.island) ? "-" : save.island) + (save.hasPosition ? "  (" + Num(save.px) + ", " + Num(save.py) + ", " + Num(save.pz) + ")" : "")));
        l.Add(Info("Interior", () => string.IsNullOrEmpty(save.interior) ? "outside" : save.interior));
        l.Add(Info("Saved at", () => string.IsNullOrEmpty(save.savedAt) ? "-" : save.savedAt));
        l.Add(Head("MONEY"));
        l.Add(new Item
        {
            Label = "Money  (Left/Right ±10 000 · Shift ±100 000)",
            Value = () => "$ " + save.money.ToString("N0", Inv),
            Change = d => { save.money = Math.Max(0, save.money + d * 10000L); },
            Reset = () => save.money = 0,
        });
        l.Add(Action_("+ $ 1 000 000", () => { save.money += 1000000; Toast("Money: $ " + save.money.ToString("N0", Inv)); }));
        l.Add(Head("CARS"));
        l.Add(Info("Own cars", () => save.ownedCars.Count + "  (current: " + (string.IsNullOrEmpty(save.currentCar) ? "-" : save.currentCar) + ")"));
        l.Add(Menu("Own cars (Delete = remove)", OwnCarsMenu));
        l.Add(Bool("Starter car chosen", () => save.starterChosen, v => save.starterChosen = v, true));
        l.Add(Head("STORY"));
        l.Add(PhInfo("Story step", save.story >= 0 ? save.story.ToString(Inv) : "never started"));
        l.Add(PhAction("Start the selected story step now", "Story is not ported yet"));
        l.Add(Head("OTHER"));
        l.Add(Info("Houses with layout / furniture / materials", () => save.houses.Count + " / " + save.ownedFurniture.Count + " / " + save.ownedMaterials.Count));
        l.Add(Info("Clothes / sticker boards / liveries", () => save.ownedClothes.Count + " / " + save.stickerBoards.Count + " / " + save.liveries.Count));
        l.Add(Info("Bookmarks / favourite spots", () => save.bookmarks.Count + " / " + save.favoriteSpots.Count));
        l.Add(Action_("Clear last interior (start outside next login)", () => { save.interior = ""; Toast("Interior cleared"); }));
        l.Add(Head("SAVE / LOAD"));
        l.Add(Action_("Save to server now", () => { if (!TDU2Session.LoggedIn) { Toast("Not logged in"); return; } TDU2Session.SaveAsync(); Toast("Saving to server"); }));
        l.Add(Action_("Write local DATA (original savegame) now", () =>
        {
            if (m?.Database == null) { Toast("Car database not loaded"); return; }
            TDU2SaveGame.WriteAsync(TDU2Session.Nick, save, m.Database, "dev menu");
            Toast("Writing " + TDU2SaveGame.PathFor(TDU2Session.Nick));
        }));
        l.Add(Info("Local DATA", () => TDU2SaveGame.Exists(TDU2Session.Nick) ? TDU2SaveGame.PathFor(TDU2Session.Nick) : "none"));
        l.Add(Action_("Read local DATA (log only, no import)", () =>
        {
            if (m?.Database == null) { Toast("Car database not loaded"); return; }
            if (TDU2SaveGame.TryRead(TDU2Session.Nick, m.Database, out var s2, out var why))
            {
                string info = "DATA: money " + s2.money + ", cars " + s2.ownedCars.Count + ", island " + s2.island + ", story " + s2.story + ", interior '" + s2.interior + "'";
                Paradise.Core.Diagnostics.Log.Info(info); Toast(info);
            }
            else Toast("DATA not readable: " + why);
        }));
        return l;
    }

    List<Item> OwnCarsMenu()
    {
        var m = Mgr; var save = TDU2Session.Save;
        var cl = new List<Item> { Head("OWN CARS  (Enter = drive · Delete = remove from profile)") };
        if (save == null) { cl.Add(Info("no savegame loaded")); return cl; }
        foreach (var f in new List<string>(save.ownedCars))
        {
            string f2 = f; var car = m?.FindCar(f2);
            cl.Add(new Item
            {
                Label = car != null ? ((car.Brand ?? "") + " " + car.ModelName).Trim() : f2,
                Value = () => f2 + (string.Equals(save.currentCar, f2, StringComparison.OrdinalIgnoreCase) ? "  (current)" : ""),
                Activate = () => { if (car != null) DriveCar(car); },
                Reset = () => { save.ownedCars.Remove(f2); Toast(f2 + " removed from profile"); Rebuild(); },
            });
        }
        if (cl.Count == 1) cl.Add(Info("no own cars"));
        return cl;
    }

    // ------------------------------------------------------------------ Eingabegeräte (Unity ControlsMenu)
    List<Item> ControlsMenu()
    {
        var l = new List<Item> { Head("DEVICE") };
        l.Add(Info("Main device", () => TDU2InputDevices.HasDevice ? TDU2InputDevices.DeviceName + "  (Joystick " + TDU2InputDevices.DeviceJoy + ")" : "none"));
        l.Add(Info("Detected as", () => TDU2InputDevices.HasDevice ? (TDU2InputDevices.IsWheel ? "Wheel" : "Gamepad") : "-"));
        l.Add(Info("Profile", () => string.IsNullOrEmpty(TDU2InputDevices.ProfileName) ? "-" : TDU2InputDevices.ProfileName));
        l.Add(Info("Option 'Device'", () => TDU2InputDevices.Mode.ToString()));
        l.Add(Info("Separate shifter", () => TDU2InputDevices.ShifterJoy > 0 ? TDU2InputDevices.ShifterName + "  (Joystick " + TDU2InputDevices.ShifterJoy + ")" : "none"));
        l.Add(Head("RAW AXES (main device)"));
        for (int a = 1; a <= 8; a++)
        {
            int ax = a;
            l.Add(Info("Axis " + ax + (TDU2InputDevices.IsHatAxis(ax) ? " (hat)" : ""), () => TDU2InputDevices.RawAxis(ax).ToString("+0.000;-0.000", Inv)));
        }
        l.Add(Head("RESULT (after dead zone / curve / gain)"));
        l.Add(Info("Steer", () => TDU2InputDevices.Steer.ToString("+0.00;-0.00", Inv) + (TDU2InputDevices.IsWheel ? "   gain " + TDU2InputDevices.WheelSteerGain.ToString("0.00", Inv) : "")));
        l.Add(Info("Throttle / Brake / Clutch", () => TDU2InputDevices.Throttle.ToString("0.00", Inv) + " / " + TDU2InputDevices.Brake.ToString("0.00", Inv) + " / " + TDU2InputDevices.Clutch.ToString("0.00", Inv)));
        l.Add(Info("H-shifter gear", () => { int g = TDU2InputDevices.HGear; return g < 0 ? "R" : g == 0 ? "N / none" : g.ToString(Inv); }));
        l.Add(Info("Look / Move", () => TDU2InputDevices.Look.X.ToString("+0.00;-0.00", Inv) + " " + TDU2InputDevices.Look.Y.ToString("+0.00;-0.00", Inv) + "  /  " + TDU2InputDevices.Move.X.ToString("+0.00;-0.00", Inv) + " " + TDU2InputDevices.Move.Y.ToString("+0.00;-0.00", Inv)));
        l.Add(Head("BINDINGS"));
        foreach (var id in new[] { "accel", "brake", "left", "right", "clutch" })
        {
            string a = id;
            l.Add(Info(a, () => TDU2InputDevices.BindingText(a)));
        }
        l.Add(Head("SETTINGS (Options -> Controls)"));
        l.Add(Info("Steering sensitivity / dead zone", () => TDU2InputDevices.SteerLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.DeadZone.ToString("0.00", Inv)));
        l.Add(Info("Throttle / brake / clutch linearity", () => TDU2InputDevices.ThrottleLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.BrakeLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.ClutchLinearity.ToString("0.00", Inv)));
        l.Add(Info("Mouse sensitivity / invert", () => TDU2InputDevices.MouseSensitivity.ToString("0.00", Inv) + " / " + (TDU2InputDevices.MouseInvert ? "yes" : "no")));
        return l;
    }

    // ------------------------------------------------------------------ Debug-Ansicht (Unity DebugViewMenu)
    List<Item> DebugViewMenu()
    {
        var l = new List<Item>
        {
            Head("RACE DEBUG VIEW"),
            PhMenu("Race route / gates / opponents", "Racing is not ported yet"),
            Head("TRAFFIC DEBUG VIEW"),
            PhMenu("Traffic routes / arrows / labels", "Traffic is not ported yet"),
            Head("CAMERA"),
            Bool("Free-fly camera", () => _terrain.FreeFly, v => SetFreeFly(v), false),
        };
        var cam = _terrain.Camera;
        if (cam != null)
        {
            l.Add(Float("Fly speed", () => cam.Speed, v => cam.Speed = v, 10f, 10f, 2000f, 140f, "0", " m/s"));
            l.Add(Float("Fast multiplier (Shift)", () => cam.FastMultiplier, v => cam.FastMultiplier = v, 0.5f, 1f, 20f, 6f, "0.0", " x"));
            l.Add(Info("Camera (game)", () => Streamer is { } s ? Num(s.ViewerGame.X) + " / " + Num(s.ViewerGame.Y) + " / " + Num(s.ViewerGame.Z) : "-"));
        }
        l.Add(Action_("Put car at the camera", () =>
        {
            var m = Mgr; var s = Streamer; var c = _terrain.Camera;
            if (m?.Current == null || s == null || c == null) { Toast("No car"); return; }
            var f = c.Forward; f.Y = 0f;
            m.TeleportSafe(s.ViewerGame, f.LengthSquared() > 1e-4f ? System.Numerics.Vector3.Normalize(f) : System.Numerics.Vector3.UnitZ);
            SetFreeFly(false);
            Toast("Car moved to the camera");
        }));
        l.Add(Head(""));
        l.Add(Info("Debug overlay: F12 · object under the crosshair: F10"));
        l.Add(Info("Free camera: RMB look · WASD move · Space/Ctrl up/down · Shift fast"));
        return l;
    }

    void SetFreeFly(bool on)
    {
        _terrain.FreeFly = on;
        Toast(on ? "Free-fly camera on" : "Free-fly camera off");
    }

    // ------------------------------------------------------------------ Flash (Unity FlashMenu / FlashPlayerMenu / FlashProfilerMenu)
    readonly List<VmfPlayer> _flashViewers = new();
    bool _flashLogAuto;
    double _flashLogNext;

    List<Item> FlashMenu()
    {
        _flashViewers.RemoveAll(v => !FlashHost.Players.Contains(v));
        var l = new List<Item> { Head("PLAYING FLASH") };
        int n = 0;
        foreach (var p in FlashHost.Players)
        {
            if (p?.Movie == null) continue;
            var pp = p; n++;
            string file = Path.GetFileName(p.BankPath ?? "");
            l.Add(Menu(file + (_flashViewers.Contains(p) ? "  (viewer)" : "") + (p.Visible ? "" : "  [hidden]"), () => FlashPlayerMenu(pp)));
        }
        if (n == 0) l.Add(Info("no Flash movie active"));
        l.Add(Head("LOAD MOVIE (frontend)"));
        l.Add(Menu("Open frontend bank …", FlashLoadMenu));
        if (_flashViewers.Count > 0)
            l.Add(Action_("Close loaded viewers (" + _flashViewers.Count + ")", () =>
            {
                foreach (var v in _flashViewers) FlashHost.Remove(v);
                _flashViewers.Clear(); Toast("Viewers closed"); Rebuild();
            }));
        l.Add(Head("PROFILER"));
        l.Add(Menu("Flash Profiler / Graph / Log", FlashProfilerMenu));
        return l;
    }

    List<Item> FlashProfilerMenu()
    {
        var l = new List<Item> { Head("FLASH PROFILER") };
        l.Add(Info("Frame ms", () => VmfPlayer.LastFrameTimeMs.ToString("0.000", Inv)));
        l.Add(Info("Update/Frame parts", () => "Tick " + VmfPlayer.LastTickTimeMs.ToString("0.000", Inv) + " / Mouse " + VmfPlayer.LastMouseTimeMs.ToString("0.000", Inv) + " / Apply " + VmfPlayer.LastApplyTimeMs.ToString("0.000", Inv)));
        l.Add(Info("Draw ms", () => VmfPlayer.LastDrawTimeMs.ToString("0.000", Inv)));
        l.Add(Info("Raster integrate ms", () => VmfPlayer.LastRasterIntegrateTimeMs.ToString("0.000", Inv)));
        l.Add(Info("Draws / Shapes / Texts", () => VmfPlayer.LastDrawCalls + " / " + VmfPlayer.LastShapeDraws + " / " + VmfPlayer.LastTextDraws));
        l.Add(Info("Quad batches", () => VmfPlayer.LastQuadBatches.ToString(Inv)));
        l.Add(Info("Raster pending bytes", () => (VmfPlayer.LastRasterPendingBytes / 1024f).ToString("0", Inv) + " KB"));
        l.Add(Info("Raster queue / done", () => VmfPlayer.LastRasterQueueCount + " / " + VmfPlayer.LastRasterDoneCount));
        l.Add(Bool("Auto-log to CSV (every 0.5s)", () => _flashLogAuto, v => { _flashLogAuto = v; _flashLogNext = _time; }, false));
        l.Add(Action_("Log now to CSV", () => { VmfPlayer.AppendFlashLog(); Toast("Logged to " + Path.GetFileName(VmfPlayer.GetFlashLogPath())); }));
        l.Add(Action_("Open log path", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + VmfPlayer.GetFlashLogPath() + "\""); } catch (Exception) { } }));
        l.Add(Action_("Clear graph history", () => { VmfPlayer.ClearFlashMetrics(); Toast("Cleared"); }));
        l.Add(Head("GRAPH (Frame ms)"));
        var hist = VmfPlayer.GetFrameHistory();
        if (hist.Length == 0) l.Add(Info("no samples yet"));
        else
        {
            float max = 0f, sum = 0f;
            foreach (float h in hist) { if (h > max) max = h; sum += h; }
            if (max < 0.01f) max = 0.01f;
            float avg = sum / hist.Length;
            l.Add(Info("Samples", () => hist.Length.ToString(Inv)));
            l.Add(Info("Max / Avg", () => max.ToString("0.000", Inv) + " / " + avg.ToString("0.000", Inv) + " ms"));
            l.Add(Info("Graph", () => GraphStr(hist, 40, max)));
        }
        // Unity: beim Aufbau der Seite (jedes Bild, solange sie offen ist)
        if (_flashLogAuto && _time >= _flashLogNext)
        {
            _flashLogNext = _time + 0.5;
            VmfPlayer.AppendFlashLog();
        }
        return l;
    }

    static string GraphStr(float[] arr, int width, float max)
    {
        if (arr.Length == 0) return "-";
        var sb = new System.Text.StringBuilder(width + 2);
        sb.Append('[');
        for (int i = 0; i < width; i++)
        {
            int idx = arr.Length - width + i;
            if (idx < 0) { sb.Append(' '); continue; }
            float v = arr[idx] / max;
            sb.Append(v >= 0.9f ? '#' : v >= 0.7f ? 'X' : v >= 0.5f ? '=' : v >= 0.3f ? '-' : v >= 0.1f ? '.' : ' ');
        }
        sb.Append(']');
        return sb.ToString();
    }

    List<Item> FlashLoadMenu()
    {
        var l = new List<Item>();
        string bnk = FlashHost.BnkDir;
        if (string.IsNullOrEmpty(bnk)) { l.Add(Info("Bnk folder unknown")); return l; }
        foreach (var res in new[] { "hires", "medres", "lowres" })
        {
            string dir = Path.Combine(bnk, "frontend", res);
            if (!Directory.Exists(dir)) continue;
            l.Add(Head(res.ToUpperInvariant()));
            var files = Directory.GetFiles(dir, "*.bnk");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string path = f;
                l.Add(Action_(Path.GetFileNameWithoutExtension(f), () => LoadFlashViewer(path)));
            }
            break;
        }
        if (l.Count == 0) l.Add(Info("no frontend folder (hires/medres/lowres)"));
        return l;
    }

    void LoadFlashViewer(string path)
    {
        var p = FlashHost.Load(path, -20);
        if (p == null) { Toast("Error: " + Path.GetFileName(path) + " not loadable (log)"); return; }
        p.MouseEnabled = true;
        _flashViewers.Add(p);
        Toast("Loaded: " + Path.GetFileName(path) + " – labels under Flash Debug > movie > root");
    }

    List<Item> FlashPlayerMenu(VmfPlayer p)
    {
        var l = new List<Item>();
        if (p == null || p.Root == null || !FlashHost.Players.Contains(p)) { l.Add(Info("Movie no longer exists")); return l; }
        l.Add(Head("CLIP " + Path.GetFileName(p.BankPath ?? "")));
        l.Add(Bool("Visible", () => p.Visible, v => p.Visible = v, true));
        l.Add(Bool("Mouse", () => p.MouseEnabled, v => p.MouseEnabled = v, false));
        l.Add(Int("GUI Depth", () => p.GuiDepth, v => p.GuiDepth = v, -100, 100, 0));
        l.Add(Info("Focus (_root.Clip)", () => p.GetVariable("/:Clip") ?? "-"));
        l.Add(Action_("Reset Movie (Reload + Set Up)", () => ResetFlash(p)));
        l.Add(Info("Note: labels/images from the root folder alter the menus/HUD – reset them here"));
        l.Add(Head("INPUT (like the game)"));
        foreach (var (lab, fn, arg) in new (string, string, string?)[] { ("UP", "Move", "up"), ("DOWN", "Move", "down"), ("LEFT", "Move", "left"), ("RIGHT", "Move", "right"), ("A", "A", null), ("B", "B", null), ("X", "X", null), ("Y", "Y", null) })
        {
            string f2 = fn; string? a2 = arg;
            l.Add(Action_(lab, () => { bool ok = a2 != null ? p.Input(f2, a2) : p.Input(f2); Toast(f2 + (a2 != null ? "(" + a2 + ")" : "") + (ok ? " processed" : ": no function")); }));
        }
        l.AddRange(FlashClipItems(p, "/", p.Root));
        return l;
    }

    /// <summary>Film neu laden (Ausgangszustand). Der HUD-Film der Minimap wird vom Fahrzeug eingerichtet und bleibt.</summary>
    void ResetFlash(VmfPlayer p)
    {
        if (p == null || string.IsNullOrEmpty(p.BankPath)) return;
        if (ReferenceEquals(Mgr?.Minimap?.Hud, p)) { Toast("HUD is set up by the car – respawn the car to reload it"); return; }
        try
        {
            bool vis = p.Visible;
            p.Load(p.BankPath);
            p.Visible = vis;
            if (!string.IsNullOrEmpty(FlashHost.BnkDir)) FlashLocalization.Apply(p, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language));
            Toast("Reloaded: " + Path.GetFileName(p.BankPath) + " (initial state – reopen the menu if necessary)");
        }
        catch (Exception ex) { Toast("Error: " + ex.Message); }
        Rebuild();
    }

    static string? FlashLabel(VmfPlayer.Instance inst)
    {
        string? lab = null; int best = -1;
        foreach (var kv in inst.Timeline.Labels) if (kv.Value <= inst.Frame && kv.Value > best) { best = kv.Value; lab = kv.Key; }
        return lab;
    }

    /// <summary>Zeitleiste eines Clips: Abspielen/Stoppen, Bild, Labels (gotoAndPlay / gotoAndStop), Unter-Clips.</summary>
    List<Item> FlashClipItems(VmfPlayer p, string path, VmfPlayer.Instance? inst)
    {
        var l = new List<Item>();
        if (inst == null) { l.Add(Info("Clip no longer exists")); return l; }
        l.Add(Head("CLIP " + path));
        l.Add(Info("Frame / label", () => (inst.Frame + 1) + " / " + inst.Timeline.FrameCount + "   [" + (FlashLabel(inst) ?? "-") + "]" + (inst.Playing ? "  playing" : "  stopped")));
        l.Add(Bool("Play", () => inst.Playing, v => inst.Playing = v, true));
        l.Add(Int("Frame", () => inst.Frame + 1, v => p.GotoFrame(inst, Math.Clamp(v - 1, 0, Math.Max(0, inst.Timeline.FrameCount - 1)), true), 1, Math.Max(1, inst.Timeline.FrameCount), 1));
        var owner = inst.Owner;
        if (owner != null) l.Add(Bool("Visible (_visible)", () => owner.Visible, v => owner.Visible = v, true));
        if (inst.Timeline.Labels.Count > 0)
        {
            l.Add(Head("LABELS (Enter = gotoAndPlay, Left/Right = gotoAndStop)"));
            var labs = new List<KeyValuePair<string, int>>(inst.Timeline.Labels);
            labs.Sort((a, b) => a.Value.CompareTo(b.Value));
            foreach (var kv in labs)
            {
                string lab = kv.Key;
                l.Add(new Item
                {
                    Label = lab + "  (frame " + (kv.Value + 1) + ")",
                    Value = () => FlashLabel(inst) == lab ? "<- active" : "",
                    Activate = () => { p.GotoLabel(path, lab, true); Toast(path + " -> " + lab + " (Play)"); },
                    Change = _ => { p.GotoLabel(path, lab, false); Toast(path + " -> " + lab + " (Stop)"); },
                });
            }
        }
        var kids = new List<Item>();
        foreach (var d in inst.List.Values)
        {
            if (d.Sprite == null) continue;
            string name = d.Name ?? ("#" + d.Depth);
            string cp = (path == "/" ? "/" : path + "/") + name;
            var sub = d.Sprite;
            bool shown = d.Visible && !(d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f);
            kids.Add(Menu(name + (shown ? "" : "  [hidden]") + "  (" + sub.Timeline.FrameCount + " fr., " + sub.Timeline.Labels.Count + " labels)", () => FlashClipItems(p, cp, sub)));
        }
        if (kids.Count > 0) { l.Add(Head("SUB-CLIPS")); l.AddRange(kids); }
        return l;
    }

    // ------------------------------------------------------------------ Audio (Lautstärken, laufende Wiedergabe)
    List<Item> AudioMenu()
    {
        var m = Mgr;
        var l = new List<Item> { Head("NOW PLAYING") };
        l.Add(Info("Audio output", () => (AudioEngine.Paused ? "paused" : "running") + "  ·  " + AudioEngine.VoiceCount + " voices  ·  " + AudioEngine.Rate + " Hz"));
        l.Add(Info("Menu music", () => FrontendMusic.Current ?? "-"));
        l.Add(Info("Spot music (interior)", () => SpotMusic.Current ?? "-"));
        l.Add(Info("Radio", () =>
        {
            var r = GAME_MODE_FREERIDE.Radio;
            if (r == null) return "-";
            string t = r.CurrentArtist.Length > 0 ? r.CurrentArtist + " - " + r.CurrentTitle : r.CurrentTitle;
            return r.CurrentStation + "  vol " + r.CurrentVolume + (r.Muted ? "  (muted)" : "") + (t.Length > 0 ? "  ·  " + t : "");
        }));
        l.Add(Info("Ambience", () =>
        {
            var a = GAME_MODE_FREERIDE.Ambience;
            return a == null ? "-" : a.Island + "  ·  zone type " + (a.CurrentType >= 0 ? a.CurrentType.ToString(Inv) : "-") + "  ·  vol " + Num(a.Volume, "0.00");
        }));
        l.Add(Info("Car engine", () => m?.Audio is { } va ? (va.EngineOn ? "on" : "off") + (va.InteriorView ? "  ·  interior view" : "") : "-"));
        l.Add(Head("LISTENER VOLUMES  (session only – defaults: Game/Config/AudioConfig.cs)"));
        l.Add(Float("Master", () => AudioEngine.MasterVolume, v => AudioEngine.MasterVolume = v, 0.05f, 0f, 1f, 0.85f, "0.00"));
        l.Add(Float("Menu music", () => FrontendMusic.Volume, v => FrontendMusic.Volume = v, 0.05f, 0f, 1f, 0.35f, "0.00"));
        l.Add(Float("Spot music", () => SpotMusic.Volume, v => SpotMusic.Volume = v, 0.05f, 0f, 1f, 0.6f, "0.00"));
        l.Add(Float("Interface sounds", () => FrontendSounds.Volume, v => FrontendSounds.Volume = v, 0.05f, 0f, 1f, 0.4f, "0.00"));
        l.Add(Bool("Interface sounds on", () => FrontendSounds.Enabled, v => FrontendSounds.Enabled = v, true));
        if (m != null)
        {
            l.Add(Float("Car", () => m.Volume, v => { m.Volume = v; if (m.Audio != null) m.Audio.MasterVolume = v; }, 0.02f, 0f, 1f, 0.34f, "0.00"));
            l.Add(Float("Tyre squeal (factor)", () => m.SquealVolume, v => { m.SquealVolume = v; if (m.Audio != null) m.Audio.SquealFactor = v; }, 0.05f, 0f, 2f, 0.3f, "0.00"));
            l.Add(Float("Gear shift (factor)", () => m.GearShiftVolume, v => { m.GearShiftVolume = v; if (m.Audio != null) m.Audio.GearShiftFactor = v; }, 0.05f, 0f, 2f, 0.3f, "0.00"));
        }
        l.Add(new Item
        {
            Label = "Ambience",
            Value = () => GAME_MODE_FREERIDE.Ambience is { } a ? Num(a.Volume, "0.00") : "-",
            Change = d => { if (GAME_MODE_FREERIDE.Ambience is { } a) a.Volume = Math.Clamp(a.Volume + 0.05f * d, 0f, 1f); },
            Reset = () => { if (GAME_MODE_FREERIDE.Ambience is { } a) a.Volume = 0.1f; },
        });
        l.Add(new Item
        {
            Label = "Radio volume (0..11)",
            Value = () => GAME_MODE_FREERIDE.Radio is { } r ? r.CurrentVolume.ToString(Inv) : "-",
            Change = d => GAME_MODE_FREERIDE.Radio?.ChangeVolume(Math.Sign(d)),
        });
        l.Add(Head("RADIO"));
        l.Add(Action_("Radio on/off", () => GAME_MODE_FREERIDE.Radio?.Toggle()));
        l.Add(Action_("Next station", () => GAME_MODE_FREERIDE.Radio?.ChangeStation(1)));
        l.Add(Action_("Previous station", () => GAME_MODE_FREERIDE.Radio?.ChangeStation(-1)));
        return l;
    }
}
