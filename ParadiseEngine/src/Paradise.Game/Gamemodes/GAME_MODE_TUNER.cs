using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Game.Screens;
using System.Globalization;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Tuner (Port aus der Original-exe, GMCarShop "after market"): Spots ETUN_* (Interieur itun_a, eins für alle Tuner).
/// Zugang nur mit einem Auto einer Marke des Tuners: DBCarShop des Spots listet je Marke ein Auto (_slot_1..9) – passt die Marke
/// des eigenen Autos zu keinem, ist die Einfahrt gesperrt (HUD /HUD/enter "locked", Text m_SpotLockedTuner_ "Falsches Fahrzeug/Marke").
/// Menü im Film frontend/&lt;res&gt;/shopscar.bnk ab Label "after_mkt" (WhatPage car_tuner, Laufmodus tuner_stop):
///   _root.OpenTuner() -> menu_sq (acceleration, top_speed, braking, rims) -> ValidMenu: amtv (Stufenliste menu_upl) bzw. menu_rims.
///   FSCommands wie die exe (Verteiler 0xb88fa0): afmk Sperren/Gebiet, amtv Stufenliste (UpgName/UpgPrice/LockUpgLevel 0..3),
///   evmo EnoughMoney, kida Vorschau, kiva/kich Stufe kaufen, chop Felge wechseln, tune:rims:open|clos|vali, tuci Abschluss,
///   dbxo Bestätigungsbox (tun_valid, rims_valid, tun_not_enough_money, tun_already_bought, tun_out), gofr Tuner verlassen.
/// Stufen und Preise: <see cref="CarUpgrades"/> (lokal im Spielstand). Freischaltung einer Stufe: Entdeckungslevel des Gebiets.
/// </summary>
public class GAME_MODE_TUNER : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_TUNER;
    static readonly ScreenDef[] _screens = { ScreenDefs.Dealer };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Dealer;

    static bool _open, _hooked, _upgraded, _rimsValidated;
    static DisplayCar? _dc;
    static int _type = -1;              // exe [this+0x104]: 0..2 Stufen, 4 Felgen
    static readonly List<int> _rims = new();
    static int _rimSel, _rimOwned;      // exe [this+0x110], [this+0x114]
    static CarChoice _choice;

    /// <summary>Tuner-Menü offen: kein Laufen/Umsehen, Maus frei, Tasten gehören dem Film (InteriorUtils.UpdateInside).</summary>
    internal static bool MenuOpen => _open;

    // ================================================================== Raum-Modus
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (!_open) { base.Run(ctx, time); return; }
        // Menü offen: wie InteriorUtils.Run, aber ESC/Rücktaste gehören dem Film (kein Pausemenü)
        _ctx = ctx;
        if (_t == null || !UpdateInside(time.DeltaTime, ctx.Paused))
        {
            Close();
            GameMode.Set(GAME_MODE_TESTCAR.TestDriveActive ? Gamemode.GAME_MODE_TESTCAR : Gamemode.GAME_MODE_FREERIDE);
        }
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to != Gamemode.GAME_MODE_PAUSE && _open) Close();
        base.Exit(ctx, to);
    }

    // ================================================================== Zugang (Marke)
    /// <summary>Nimmt der Tuner dieses Auto an? Marke (_car_brand) wie eines der Autos in DBCarShop._slot_1..9 des Spots.</summary>
    internal static bool Accepts(Spot spot, CarPhysics? car) => Accepts(spot, car, Vm?.Database, _t?.Streamer?.Index?.IslandName ?? "");

    /// <summary>Wie oben mit Datenbank und Insel des Aufrufers (große Karte).</summary>
    internal static bool Accepts(Spot spot, CarPhysics? car, CarDatabase? db, string island)
    {
        if (db == null || car?.Raw == null) return true;
        var shop = db.ShopForSpot(spot, island);
        if (shop == null) return true;
        int brand = car.Raw.I("_car_brand");
        foreach (int id in shop.CarIds)
            if (id != 0 && db.ById.TryGetValue(id, out var c) && c.Raw != null && c.Raw.I("_car_brand") == brand) return true;
        return false;
    }

    /// <summary>Text der gesperrten Einfahrt (m_SpotLockedTuner_ = 5757).</summary>
    internal static string LockedText()
    {
        var texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language);
        return texts != null && texts.TryGetValue("m_SpotLockedTuner_", out var t) && !string.IsNullOrEmpty(t) ? t : Lang(5757, "Falsches Fahrzeug/Marke");
    }

    // ================================================================== Laufmodus-Oberfläche
    /// <summary>Tuner betreten: shopscar.vmf ab "after_mkt" (WhatPage car_tuner, afmk) bis "tuner_stop" (Laufmodus).</summary>
    internal static void LoadTunerUi()
    {
        var ui = _ui; if (ui == null) return;
        if (!_hooked) { ui.FSCommand += OnCommand; _hooked = true; }
        var texts = FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language);
        FlashLocalization.Apply(ui, texts);
        ui.MouseEnabled = false;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetValue("/:FirstTime", false);   // Hilfesystem (openHelpSystem) nur beim ersten Besuch: nicht portiert
        ui.GotoLabel("/", "after_mkt", true);
        foreach (var d in ui.Root.List.Values) d.Visible = d.Name != null && (d.Name == "cursor" || d.Name == "title_bar" || d.Name == "help");
        ui.SetVariable("/:Title", TitleFor(Kind.Tuner));
        UpdateMoney();
        ResetUiAim();
        Log.Info("Tuner: Oberfläche after_mkt (" + TitleFor(Kind.Tuner) + ")");
    }

    // ================================================================== Öffnen / Schließen
    /// <summary>Tuner-Menü öffnen (Tuner angesprochen oder Auto angeklickt): _root.OpenTuner().</summary>
    internal static bool Open(DisplayCar? dc = null)
    {
        if (_open) return true;
        var ui = _ui;
        dc ??= _cars.Count > 0 ? _cars[0] : null;
        if (ui == null || dc?.Car == null) { Log.Warn("Tuner: kein Auto im Tuner"); return false; }
        if (!_hooked) { ui.FSCommand += OnCommand; _hooked = true; }
        _dc = dc; _upgraded = false; _type = -1;
        CarChoices.TryGet(dc.Car.FileName, out _choice);
        UpdateUiAim(null);
        foreach (var n in new[] { "car_datas", "menu_sq", "menu_upl", "menu_rims", "onlymoney", "help", "popup_lock" }) ui.Show("/" + n);
        FillCarData(dc.Car);
        OnAfterMarket();
        UpdateMoney();
        ui.MouseEnabled = true;
        ui.SetValue("/:Input", true);
        _open = true;
        // Der Film öffnet das Menü bei "valid" auf dem Tuner/Auto (ItemType npc_tuner/vehicle) evtl. schon selbst: nicht doppelt öffnen
        string root = ui.Label("/") ?? "";
        if (root != "tuner_block" && root != "tuner_edit" && !ui.CallClip("/", "OpenTuner")) { Log.Warn("Tuner: OpenTuner fehlt im Film"); _open = false; return false; }
        Log.Info("Tuner geöffnet: " + dc.Car.FileName + " – " + CarUpgrades.Get(dc.Car.FileName));
        return true;
    }

    static void Close()
    {
        if (!_open) return;
        _open = false;
        FlashDialog.Cancel();
        var ui = _ui;
        if (ui != null)
        {
            ui.MouseEnabled = false;
            ui.SetValue("/:Input", true);
            ui.SetVariable("/:CurrentAction", "walk_mode");
        }
        Log.Info("Tuner-Menü geschlossen");
    }

    static void Leave()
    {
        var car = _dc?.Car;
        Close();
        ExitInterior(_lastCar ?? car);
    }

    // ================================================================== Befehle des Films (exe 0xb88fa0)
    static void OnCommand(string cmd, string args)
    {
        if (_kind != Kind.Tuner || _ui == null) return;
        var ui = _ui;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        switch (c)
        {
            case "dbxo": FlashDialog.Open(ui); return;                    // Bestätigungsbox aus general.bnk, Antwort an _root.DialogBox
            case "gofr": Leave(); return;                                 // tun_out "Ja" -> tuner_close -> Tuner verlassen
            case "wmon": case "liin": case "shko": Close(); return;       // BackToWM: zurück in den Laufmodus (tuner_stop)
        }
        if (!_open) { if (c == "afmk") OnAfterMarket(); return; }
        switch (c)
        {
            case "afmk": OnAfterMarket(); return;
            case "amtv": FillUpgradeList(); return;
            case "evmo": EvalMoney(); return;
            case "kida": Preview(); return;
            case "kiva": case "kich": BuyUpgrade(); FillUpgradeList(); _upgraded = true; return;
            case "chop": if (_rims.Count > 1) RimOption(); return;
            case "tune:rims:open": RimsOpen(); return;
            case "tune:rims:clos": RimsClose(); return;
            case "tune:rims:vali": BuyRims(); _upgraded = true; _rimsValidated = true; return;
            case "tuci":
                // exe: nach einem Kauf Tuning-Film (Kinematik) der Werkstatt, danach zurück in den Laufmodus.
                // Die Kinematik ist noch nicht portiert: direkt wie nach ihrem Ende weiter (BackToWM -> tuner_stop, wmon).
                if (_upgraded) Log.Info("Tuner: Tuning-Kinematik (noch nicht portiert) übersprungen");
                _upgraded = false;
                if (!ui.CallClip("/", "BackToWM")) ui.GotoLabel("/", "tuner_stop", false);
                return;
        }
    }

    /// <summary>afmk (exe 0xb886c0): Sperren der Kategorien, Felgen-Anzahl, Gebiet und dessen Entdeckungslevel.</summary>
    static void OnAfterMarket()
    {
        var ui = _ui; if (ui == null) return;
        foreach (var n in new[] { "/:LockUpgAcceleration", "/:LockUpgTopSpeed", "/:LockUpgBraking", "/:LockUpgHandling", "/:LockUpgRims" }) ui.SetValue(n, false);
        LoadRims();
        ui.SetValue("/:isNoRims", _rims.Count <= 1);
        ui.SetValue("/:nb_items", _rims.Count);
        ui.SetValue("/:AreaIncreased", false);
        ui.SetValue("/:CruisingAreaLevel", AreaLevel());
        ui.SetVariable("/:CruisingArea", AreaName());
        ui.SetValue("/:group_change", false);
    }

    // ================================================================== Stufen
    /// <summary>amtv (exe 0xb88340): Liste der Stufen 1..4 der Kategorie /:UpgType – Name, Preis bzw. "Schon bezahlt", Sperre.</summary>
    static void FillUpgradeList()
    {
        var ui = _ui; var car = _dc?.Car;
        if (ui == null || car == null) return;
        _type = CarUpgrades.TypeOf(ui.GetVariable("/:UpgType"));
        if (_type < 0 || _type > 2) return;   // handling: kein Kit (exe: Index -1 -> keine Liste)
        var lv = CarUpgrades.Get(car.FileName);
        int cur = lv.Get(_type), carPrice = CarUpgrades.CarPrice(car);
        int area = AreaLevel();
        string name = Lang(4055, "Level §TUNLEVEL§");
        for (int i = 1; i <= CarUpgrades.MaxLevel; i++)
        {
            ui.SetVariable("/:UpgName" + (i - 1), name.Replace("§TUNLEVEL§", " " + i));
            bool bought = i <= cur;
            ui.SetVariable("/:UpgPrice" + (i - 1), bought ? Lang(7441, "Schon bezahlt") : Money(CarUpgrades.BuyPrice(_type, i, lv, carPrice)));
            // 2 = gekauft, 0 = frei, 1 = gesperrt (Entdeckungslevel des Gebiets zu niedrig)
            ui.SetValue("/:LockUpgLevel" + (i - 1), bought ? 2 : (area >= i ? 0 : 1));
        }
        ui.SetValue("/:CurrentUpgrade", cur);
    }

    /// <summary>evmo (exe 0xb88bd0): genug Geld für die gewählte Stufe bzw. Felge.</summary>
    static void EvalMoney()
    {
        var ui = _ui; var car = _dc?.Car;
        if (ui == null || car == null) return;
        long money = TDU2Session.Save?.money ?? long.MaxValue;
        bool enough;
        if (_type > 3)
        {
            var rim = RimInfoAt(_rimSel);
            enough = rim == null || _rimSel == _rimOwned || money >= rim.Price;
        }
        else
        {
            int sel = ParseInt(ui.GetVariable("/:UpgradeSelected")) + 1;
            enough = money >= CarUpgrades.BuyPrice(_type, sel, CarUpgrades.Get(car.FileName), CarUpgrades.CarPrice(car));
        }
        ui.SetValue("/:EnoughMoney", enough);
    }

    /// <summary>kida (exe 0xb88a30): Vorschau der Fahrzeugdaten mit der markierten Stufe (CurrentUpgrade + 1).</summary>
    static void Preview()
    {
        var ui = _ui; var car = _dc?.Car;
        if (ui == null || car == null || _type < 0 || _type > 2) return;
        ui.SetValue("/:group_change", false);
        var lv = CarUpgrades.Get(car.FileName);
        lv.Set(_type, ParseInt(ui.GetVariable("/:CurrentUpgrade")) + 1);
        // Balken der Fahrzeugdaten (car_datas.setUpgradeStatus: new_accel/new_speed/new_braking, *_upg-Texte)
        CarUpgrades.Multipliers(lv, out float tq, out float top, out float brk);
        ui.SetValue("/:new_accel", System.Math.Clamp((int)MathF.Round(ParseInt(ui.GetVariable("/:cur_accel")) * tq), 1, 101));
        ui.SetValue("/:new_speed", System.Math.Clamp((int)MathF.Round(ParseInt(ui.GetVariable("/:cur_speed")) * top), 1, 101));
        ui.SetValue("/:new_braking", System.Math.Clamp((int)MathF.Round(ParseInt(ui.GetVariable("/:cur_braking")) * brk), 1, 101));
        var ci = CultureInfo.InvariantCulture;
        float bhp = car.PowerBhp * tq, vmax = car.MaxTheoricalSpeed * top;
        ui.SetVariable("/:cur_power_m_upg", bhp > 0 ? (bhp * 1.01387f).ToString("0", ci) + " PS" : "-");
        ui.SetVariable("/:cur_power_i_upg", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
        ui.SetVariable("/:cur_max_speed_m_upg", vmax > 0 ? vmax.ToString("0", ci) + " Km/h" : "-");
        ui.SetVariable("/:cur_max_speed_i_upg", vmax > 0 ? (vmax / 1.609344f).ToString("0", ci) + " mph" : "-");
    }

    /// <summary>kiva (exe 0xb88c90): Stufe /:UpgradeSelected + 1 kaufen, bezahlen, speichern.</summary>
    static void BuyUpgrade()
    {
        var ui = _ui; var car = _dc?.Car;
        if (ui == null || car == null || _type < 0 || _type > 2) return;
        int level = ParseInt(ui.GetVariable("/:UpgradeSelected")) + 1;
        var lv = CarUpgrades.Get(car.FileName);
        int price = CarUpgrades.BuyPrice(_type, level, lv, CarUpgrades.CarPrice(car));
        var sv = TDU2Session.Save;
        if (sv != null) sv.money = System.Math.Max(0, sv.money - price);
        lv.Set(_type, level);
        CarUpgrades.Set(car.FileName, lv);
        UpdateMoney();
        FillCarData(car);
        ui.CallClip("/", "CinematicFinished");
        Log.Info("Tuner: " + car.FileName + " Kategorie " + _type + " Stufe " + level + " für $" + price);
    }

    // ================================================================== Felgen (tune:rims:*)
    static void LoadRims()
    {
        _rims.Clear();
        var db = Vm?.Database; var car = _dc?.Car;
        if (db == null || car == null) return;
        if (db.CarRims.TryGetValue(car.Id, out var l)) _rims.AddRange(l);
        int cur = _choice.Rim;
        _rimOwned = System.Math.Max(0, _rims.IndexOf(cur));
    }

    static RimInfo? RimInfoAt(int i)
    {
        var db = Vm?.Database;
        return db != null && i >= 0 && i < _rims.Count && db.Rims.TryGetValue(_rims[i], out var r) ? r : null;
    }

    /// <summary>tune:rims:open: aktuelle Felge merken, Auswahl darauf.</summary>
    static void RimsOpen()
    {
        _type = CarUpgrades.Rims; _rimsValidated = false;
        LoadRims();
        _ui?.SetValue("/:num_item", _rimOwned);
        RimOption();
    }

    /// <summary>chop (exe 0xb88e20): Felge /:num_item zeigen – Name, Preis bzw. "Schon bezahlt", Vorschau am Auto.</summary>
    static void RimOption()
    {
        var ui = _ui; if (ui == null || _rims.Count == 0) return;
        int n = ParseInt(ui.GetVariable("/:num_item"));
        if (n >= _rims.Count) n = 0; else if (n < 0) n = _rims.Count - 1;
        _rimSel = n;
        ui.SetValue("/:already_buyed", _rimSel == _rimOwned);
        var rim = RimInfoAt(_rimSel);
        ui.SetVariable("/:option_name", rim?.DisplayName ?? "");
        ui.SetVariable("/:cur_op_price", _rimSel == _rimOwned ? Lang(7441, "Schon bezahlt") : Money(rim?.Price ?? 0));
        PreviewRim(_rims[_rimSel]);
    }

    /// <summary>tune:rims:clos: ohne Kauf die eigene Felge wieder zeigen.</summary>
    static void RimsClose()
    {
        if (!_rimsValidated && _rims.Count > 0) PreviewRim(_rims[_rimOwned]);
        _rimsValidated = false;
        _type = -1;
    }

    /// <summary>tune:rims:vali (exe 0xb88920): Felge kaufen und behalten.</summary>
    static void BuyRims()
    {
        var car = _dc?.Car; var rim = RimInfoAt(_rimSel);
        if (car == null || rim == null || _rimSel == _rimOwned) return;
        var sv = TDU2Session.Save;
        if (sv != null) sv.money = System.Math.Max(0, sv.money - rim.Price);
        _choice.Rim = rim.Id;
        CarChoices.Set(car.FileName, _choice);
        _rimOwned = _rimSel;
        UpdateMoney();
        _ui?.CallClip("/", "CinematicFinished");
        Log.Info("Tuner: Felge " + rim.DisplayName + " für $" + rim.Price + " an " + car.FileName);
    }

    /// <summary>Felge am Auto im Tuner zeigen (Neuaufbau wie die Händler-Vorschau).</summary>
    static void PreviewRim(int rimId)
    {
        if (_dc == null) return;
        var want = _choice; want.Rim = rimId;
        GAME_MODE_SHOP._previewFor = _dc;
        GAME_MODE_SHOP._previewWant = want;
        GAME_MODE_SHOP._previewDirty = true;
        GAME_MODE_SHOP._previewAt = _time + 0.15f;
    }

    // ================================================================== Jedes Bild (aus InteriorUtils.UpdateInside)
    /// <summary>Menü offen: Tasten an den Film (Rücktaste gehört dem Film: B -> Ebene zurück bzw. "Tuner verlassen?").</summary>
    internal static void UpdateMenu(float dt)
    {
        var ui = _ui;
        if (!_open || ui == null || FlashDialog.Active) return;
        GAME_MODE_SHOP.PollDealerPreview();
        FlashNav.Directions(ui);
        if (FlashNav.Pressed(FlashKey.Confirm)) FlashNav.Send(ui, FlashKey.Confirm);
        if (FlashNav.Pressed(FlashKey.Back)) FlashNav.Send(ui, FlashKey.Back);
        if (FlashNav.Pressed(FlashKey.X)) FlashNav.Send(ui, FlashKey.X);
        if (FlashNav.Pressed(FlashKey.Y)) FlashNav.Send(ui, FlashKey.Y);
    }

    // ================================================================== Hilfen
    /// <summary>Gebietslevel (DBAreaLevel 1..6) des Gebiets, in dem der Spieler steht (= der Tuner).</summary>
    internal static int AreaLevel()
    {
        // exe 0xa2f720: Gebiet der Spielerposition -> 0xa05b40 Gebietslevel aus den Entdeckungspunkten des Gebiets;
        // Stufe n frei, wenn Level >= n.
        var d = Paradise.Game.Gameplay.Discovery.Current;
        return d != null ? d.AreaLevel(d.Area) : 1;
    }

    /// <summary>Gebietslevel am Ort eines Spots (Karten-Tooltip des Tuners: /:MaxUpgLevel).</summary>
    internal static int AreaLevelAt(System.Numerics.Vector3 game)
    {
        var d = Paradise.Game.Gameplay.Discovery.Current;
        if (d == null) return 1;
        int area = Paradise.Game.Gameplay.Wrecks.Current?.AreaAt(game) ?? d.Area;
        return d.AreaLevel(area < 1 ? d.Area : area);
    }

    /// <summary>Name des Entdeckungsgebiets der Spielerposition (m_HawaiArea1..4 / m_IbizaArea1..2).</summary>
    static string AreaName() => Paradise.Game.Gameplay.Discovery.Current?.AreaName ?? "";

    static void UpdateMoney()
    {
        var ui = _ui; if (ui == null) return;
        var sv = TDU2Session.Save;
        ui.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : " ");
        ui.SetVariable("/:ClubMoney", " ");
    }

    static int ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)d : 0;

    static string Money(long v) => "$ " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number);
}
