using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;
using Paradise.Game.Screens;
using System.Globalization;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Immobilienbüro (Spots ERE_*, Interieur interior/ire_b.bnk) – Port aus der Original-exe, GM_Estates (Code 0xAEA000–0xAF0000,
/// VTable 0x14EEE48, GAME_MODE_ESTATES = 17).
///   Auswahlpunkt "Realtor_area" Typ 7 (Walk-Modus "realtor") -> <see cref="Open"/>. Oberfläche "SHOPSAVATAR" (frontend/&lt;res&gt;/shopsavatar.bnk),
///   _root.SelItemType "realtor", cursor.ItemAction("valid") -> realtor_in -> FSCommand "rltr" -> realtor_wm (_root.SetRootMenu: Kaufen,
///   Tauschen, Verkaufen; Verkaufen gesperrt bei /:NoHouses) -> ValidMenu: "rbuy" / "rexc" / "rsel", Bild Menus_in öffnet realtor_menus.
///   Befehle (Verteiler 0xAED690):
///     rltr 0xAECE80  Angebot neu (0xAEF220), /:NoHouses (höchstens 1 Haus), /:num_house = 1, /:WhatZone; nach der virtuellen Tour zurück
///                    nach Menus_in (BtValid "buy"/"exchange", Seite wie zuvor).
///     rbuy           Tausch-Haus löschen (+0x1BC548).
///     chho 0xAEC280  Seite /:num_house: Daten der Seite (0xAEF630: CurHouse, PriceHouse, CarPlaces, state_sold, MyHouseFurniture,
///                    MiniGame, Sterne), Bilder RealtorScr/&lt;haus&gt;.bnk (IMG_EXT/IMG_GAR/IMG_LVG, /:InfosLoaded); beim Tausch
///                    /:PriceAfterRetake, /:PriceEarned, /:Gain; dann buho, /:WhatZone.
///     buho 0xAEBAB0  Geld/Stellplätze prüfen -> /:EnoughMoney (0 Geld fehlt, 1 ok, 2 Tausch: Stellplätze fehlen).
///     buyh 0xAED200  Kaufen (Haus dazu; Tausch: Erlös des alten, altes Haus weg), Sammlung COLLECTION_HOUSES_*, Kino C_BUY_HOUSE (Bank
///                    des Büros); danach (0xAEB485) das Exterior-Kino C_REMOVAL am neuen Haus (0xAEA6D0 Modus 3) und Freifahrt dort;
///                    beim nächsten Betreten des Hauses C_REMOVAL_I (0x1722952, InteriorUtils.Cinema).
///     seho 0xAEBC60  Verkauf prüfen: Stellplätze ohne das Haus reichen für alle Autos -> /:EnoughMoney 1, sonst 0.
///     selh 0xAED080  Verkaufen: Verkaufspreis gutschreiben, Haus weg (Wohnsitz -> erstes Haus), /:NoHouses, Liste neu.
///     vtin           Virtuelle Tour (Taste X auf einem fremden Haus): Exterior-Kino C_VTOUR_E am Haus (0xAEA6D0 Modus 0), danach
///                    zurück ins Menü (wie rltr nach 0x1722941).
///   Liste der eigenen Häuser (Verkaufen/Tauschen): ASFlashList "sellHouses_list" (Zeilen ../:CurHouse, ../:MyHouseFurniture,
///   ../:CarPlaces, ../:PriceHouse), Daten des gewählten Hauses in /:selected_* und realtor_menus/sold_menu/datas.
/// </summary>
public class GAME_MODE_ESTATES : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_ESTATES;
    static readonly ScreenDef[] _screens = { ScreenDefs.Avatar };
    public override IReadOnlyList<ScreenDef> Screens => _phase is Phase.Removal or Phase.Tour ? Array.Empty<ScreenDef>() : _screens;
    protected override ScreenDef Screen => ScreenDefs.Avatar;

    // ================================================================== Zustand
    enum Phase { Closed, Menu, Buying, Removal, Tour }
    static Phase _phase = Phase.Closed;
    static VmfPlayer? _ui2;
    static float _clock;
    /// <summary>Angebot des Büros, sortiert nach Stufe und Preis (exe +0x1BB950, 32 Byte je Eintrag).</summary>
    static readonly List<HouseInfo> _offer = new();
    static int _page;                                   // Seite (exe +0x1BC4F8 bzw. Liste +0xBA8)
    static HouseInfo? _exchange;                        // Tausch-Haus (exe +0x1BC548)
    static HouseInfo? _bought;                          // gekauftes Haus (C_REMOVAL)
    static readonly List<HouseInfo> _sellRows = new();  // Zeilen von sellHouses_list
    static VmfPlayer.FlashList? _sellList;
    static int _sellSel = -1, _sellShown = -2;
    static SpotCinematics? _cin, _ext;
    static bool _camRealtor;
    static float _lastEsc = -10f;

    // Virtuelle Tour (exe 0x1722941): zurück an dieselbe Stelle des Menüs
    static bool _tourBack;
    static int _tourPage;
    static bool _tourExchange;

    internal static bool Active => _phase != Phase.Closed;

    // ================================================================== Öffnen (Auswahlpunkt Typ 7)
    /// <summary>Makler angesprochen (InteriorUtils.Activate, Typ 7 im Immobilienbüro); false = kein Büro.</summary>
    internal static bool Open()
    {
        if (!(_spot?.Code ?? "").StartsWith("ERE", StringComparison.OrdinalIgnoreCase)) return false;
        if (!AvatarLibrary.Ready) { Log.Info("Immobilienbüro: Avatar-Daten werden noch geladen …"); return true; }
        if (_t == null || _scene == null) return true;
        GameMode.Set(Gamemode.GAME_MODE_ESTATES);
        return true;
    }

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        _ctx = ctx;
        if (from == Gamemode.GAME_MODE_PAUSE && _phase != Phase.Closed) return;   // Weiterspielen nach dem Pausemenü
        if (_phase is Phase.Removal or Phase.Tour or Phase.Buying) return;
        try { OpenShop(ctx); }
        catch (Exception ex) { Log.Error("Immobilienbüro: Öffnen fehlgeschlagen: " + ex); CloseState(); GameMode.Set(RoomMode); }
    }

    static void OpenShop(IGameModeContext ctx)
    {
        var ui = ctx.Open(ScreenDefs.Avatar);
        if (ui == null || _t == null) { Log.Warn("Immobilienbüro: shopsavatar.bnk nicht ladbar"); GameMode.Set(RoomMode); return; }
        Hook(ui);
        BuildOffer();
        _exchange = null; _bought = null; _sellSel = -1; _sellShown = -2;
        if (ctx.Terrain != null) ctx.Terrain.Input.CursorLocked = false;
        // Kinos der Bank des Büros: C_LIFE (Makler am Schreibtisch) im Hintergrund, C_CAM_REALTOR als Kamera im Menü, C_BUY_HOUSE
        ShopCinema.Close(ref _cin);
        _cin = ShopCinema.Open(AvatarConfigs.Local, castPlayer: true);
        if (_cin != null && _cin.Find("C_LIFE") != null) _cin.PlayBackground("C_LIFE", true);
        _camRealtor = false;
        _phase = Phase.Menu;
        try { FlashLocalization.Apply(ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }
        ui.Visible = true;
        ui.MouseEnabled = true;
        ui.SetValue("/:fake", false);
        ui.SetValue("/:IsTutorial", 0);
        ui.SetValue("/:WhatPlatform", 1);
        ui.SetValue("/:VirtualTour", 0);
        ui.SetVariable("/:ClubMoney", " ");
        ui.SetVariable("/:MyMoneyStr", HouseUtils.Money(MoneyNow));
        if (_tourBack)
        {
            // zurück von der virtuellen Tour (rltr 0xAECF46): direkt in die Seiten des Angebots
            _tourBack = false;
            Rltr(ui, _tourExchange ? _exchangeKeep : null);
            ui.SetVariable("/:SelItemType", "realtor");
            ui.SetVariable("/:BtValid", _tourExchange ? "exchange" : "buy");
            if (_tourExchange) ui.SetVariable("/:FromMenu", "exchange_change");
            ui.SetValue("/:num_house", _tourPage + 1);
            ui.GotoLabel("/", "Menus_in", true);
            Log.Info("Immobilienbüro: zurück von der virtuellen Tour, Seite " + (_tourPage + 1));
            return;
        }
        // Makler angesprochen wie im Spiel: Label "realtor" (SelItemType, Cursor), cursor.ItemAction("valid") -> _root.ItemType:
        // Leiste zu, gotoAndPlay realtor_in, Input aus, "camn"; der Film meldet dann "rltr" und öffnet in realtor_wm das Wurzelmenü.
        // Werte, die "rltr" setzt, schon jetzt (SetRootMenu liest /:NoHouses, page_house /:num_house und /:nb_houses)
        Rltr(ui);
        ui.GotoLabel("/", "realtor", true);
        ui.SetVariable("/:SelItemType", "realtor");
        if (!ui.CallClip("/cursor", "ItemAction", "valid")) Log.Warn("Immobilienbüro: cursor.ItemAction fehlt im Film");
    }

    static HouseInfo? _exchangeKeep;

    static void Hook(VmfPlayer ui)
    {
        if (_ui2 == ui) return;
        Unhook();
        _ui2 = ui;
        ui.FSCommand += OnCommand;
        ui.ListCreated += OnListCreated;
        ui.ListValidated += OnListValidated;
    }

    static void Unhook()
    {
        var ui = _ui2; if (ui == null) return;
        ui.FSCommand -= OnCommand;
        ui.ListCreated -= OnListCreated;
        ui.ListValidated -= OnListValidated;
        _ui2 = null;
    }

    public override void Exit(IGameModeContext ctx, Gamemode to)
    {
        if (to == Gamemode.GAME_MODE_PAUSE) return;   // Pausemenü: Büro bleibt offen
        if (_phase is Phase.Removal or Phase.Tour) return;   // Exterior-Kino läuft ohne Oberfläche weiter
        CloseState();
        base.Exit(ctx, to);
    }

    static void CloseState()
    {
        if (_phase == Phase.Closed) return;
        _phase = Phase.Closed;
        Unhook();
        _sellList = null;
        ShopCinema.Close(ref _cin);
        _ext?.Dispose(); _ext = null;
        _camRealtor = false;
        if (_t != null) _t.NearClip = 0.05f;
    }

    /// <summary>Zurück in den Laufmodus des Büros.</summary>
    static void Close()
    {
        CloseState();
        GameMode.Set(RoomMode);
    }

    static long MoneyNow => TDU2Session.Save?.money ?? 0;
    static CarDatabase? Db => Vm?.Database;
    static string IslandName => _t?.Streamer?.Index?.IslandName ?? "";

    // ================================================================== Angebot (0xAEF220) und Seiten (0xAEF630)
    static void BuildOffer()
    {
        _offer.Clear();
        var db = Db;
        if (db != null && _spot != null) _offer.AddRange(db.HousesForRealtor(_spot, IslandName));
        if (_offer.Count == 0) Log.Warn("Immobilienbüro: kein Haus mit _realtor dieses Büros (" + _spot?.Code + ")");
    }

    static HouseInfo? PageHouse => _page >= 0 && _page < _offer.Count ? _offer[_page] : null;

    static bool Owns(HouseInfo? h) => h != null && TDU2Garage.Owns(TDU2Session.Save, h.SpotName);

    /// <summary>"rltr" (0xAECE80): Tausch löschen, Angebot neu, /:NoHouses, /:num_house = 1, /:WhatZone, /:nb_houses.</summary>
    static void Rltr(VmfPlayer ui, HouseInfo? keepExchange = null)
    {
        _exchange = keepExchange;
        BuildOffer();
        ui.SetValue("/:nb_houses", _offer.Count);
        ui.SetValue("/:NoHouses", TDU2Garage.OwnedNames(TDU2Session.Save).Count <= 1);
        ui.SetValue("/:num_house", 1);
        _page = 0;
        ui.SetVariable("/:WhatZone", HouseUtils.HouseArea(PageHouse));
        // Kamera auf den Makler (Bank: C_CAM_REALTOR), solange das Menü offen ist
        if (_cin != null && !_camRealtor && _cin.Find("C_CAM_REALTOR") != null && _cin.PlayMain("C_CAM_REALTOR", true)) _camRealtor = true;
    }

    /// <summary>Seitenzahl/-nummer des Angebots für page_house ("1 / n").</summary>
    static void SetPages(VmfPlayer ui)
    {
        ui.SetValue("/:nb_houses", _offer.Count);
        if (Num(ui, "/:num_house") < 1f) ui.SetValue("/:num_house", 1);
        ui.SetValue("/:NoHouses", TDU2Garage.OwnedNames(TDU2Session.Save).Count <= 1);
    }

    /// <summary>"chho" (0xAEC280): Seite /:num_house füllen, Tausch-Rechnung, buho, /:WhatZone.</summary>
    static void ChangePage(VmfPlayer ui)
    {
        int n = (int)MathF.Round(Num(ui, "/:num_house"));
        _page = System.Math.Clamp(n - 1, 0, System.Math.Max(0, _offer.Count - 1));
        ui.SetValue("/:InfosLoaded", 0);
        FillPage(ui);
        var h = PageHouse;
        if (_exchange != null && h != null)
        {
            int after = h.BuyPrice - _exchange.SellPrice;
            ui.SetVariable("/:PriceAfterRetake", HouseUtils.Money(System.Math.Max(0, after)));
            int earned = _exchange.SellPrice - h.BuyPrice;
            if (earned > 0) { ui.SetVariable("/:PriceEarned", HouseUtils.Money(earned)); ui.SetValue("/:Gain", 1); }
            else ui.SetValue("/:Gain", 0);
        }
        CheckBuy(ui);
        ui.SetVariable("/:WhatZone", HouseUtils.HouseArea(h));
    }

    /// <summary>0xAEF630: Daten der Seite in den Film, dann die Bilder der Bank RealtorScr/&lt;haus&gt;.bnk.</summary>
    static void FillPage(VmfPlayer ui)
    {
        var h = PageHouse;
        if (h == null) return;
        ui.SetVariable("/:CurHouse", HouseUtils.HouseLabel(h));
        ui.SetVariable("/:PriceHouse", HouseUtils.Money(h.BuyPrice));
        ui.SetValue("/:CarPlaces", h.CarSlots);
        ui.SetValue("/:state_sold", Owns(h));
        ui.SetValue("/:MyHouseFurniture", HouseUtils.FurnitureCount(h.Level));
        ui.SetVariable("/:MiniGame", "");
        ui.GotoFrameNumber("/realtor_menus/buy_menu/stars", h.Level);   // gotoAndStop(_level + 1)
        HouseUtils.PlaceOnMiniMap(ui, "/realtor_menus/buy_menu/location", h);
        // Bilder (Rückruf 0xAEEF90: Clips outward/garage/living sind nur Rahmen – Bild über die ganze Clip-Fläche, Rahmen darüber)
        var ext = HouseUtils.Picture(h, "IMG_EXT"); var gar = HouseUtils.Picture(h, "IMG_GAR"); var lvg = HouseUtils.Picture(h, "IMG_LVG");
        bool o1 = ui.SetClipImage("/realtor_menus/buy_menu/outward", ext!);
        bool o2 = ui.SetClipImage("/realtor_menus/buy_menu/garage", gar!);
        bool o3 = ui.SetClipImage("/realtor_menus/buy_menu/living", lvg!);
        Log.Info($"Immobilienbüro: Seite {_page + 1}/{_offer.Count} {h.SpotName}, Bilder außen {(ext != null ? "ok" : "-")}/{(o1 ? "Clip" : "kein Clip")}, "
                 + $"Garage {(gar != null ? "ok" : "-")}/{(o2 ? "Clip" : "kein Clip")}, Wohnraum {(lvg != null ? "ok" : "-")}/{(o3 ? "Clip" : "kein Clip")}");
        ui.SetValue("/:InfosLoaded", 1);
    }

    /// <summary>
    /// "buho" (0xAEBAB0): Geld und Stellplätze für das Haus der Seite. Freie Plätze = alle Plätze - Autos + Plätze des neuen Hauses;
    /// beim Tausch zählt der Verkaufspreis des alten Hauses zum Geld und seine Plätze fallen weg.
    /// </summary>
    static void CheckBuy(VmfPlayer ui)
    {
        var h = PageHouse; var sv = TDU2Session.Save; var db = Db;
        if (h == null || sv == null || db == null) { ui.SetValue("/:EnoughMoney", 0); return; }
        long money = sv.money;
        int free = TDU2Garage.TotalSlots(sv, db) - TDU2Garage.CarCount(sv) + h.CarSlots;
        if (_exchange != null) { money += _exchange.SellPrice; free -= _exchange.CarSlots; }
        if (money < h.BuyPrice) { ui.SetValue("/:EnoughMoney", 0); ui.SetValue("/:Gain", 2); }
        else if (_exchange != null && free < 0) ui.SetValue("/:EnoughMoney", 2);
        else ui.SetValue("/:EnoughMoney", 1);
    }

    // ================================================================== Kaufen / Verkaufen
    /// <summary>"buyh" (0xAED200): Haus kaufen bzw. tauschen, Geld, Wohnsitz, Statistik; danach C_BUY_HOUSE.</summary>
    static void Buy(VmfPlayer ui)
    {
        var h = PageHouse; var sv = TDU2Session.Save; var db = Db;
        if (h == null || sv == null || db == null || Owns(h)) return;
        long money = sv.money + (_exchange != null ? _exchange.SellPrice : 0);
        if (money < h.BuyPrice) { Log.Warn("Immobilienbüro: Kauf ohne genug Geld abgelehnt"); return; }
        TDU2Garage.AddHouse(sv, h.SpotName);
        if (_exchange != null)
        {
            var old = _exchange;
            sv.money = money - h.BuyPrice;
            bool wasHome = string.Equals(sv.homeHouse, old.SpotName, StringComparison.OrdinalIgnoreCase);
            TDU2Garage.RemoveHouse(sv, db, old.SpotName);
            sv.housesSold++;
            if (wasHome) sv.homeHouse = h.SpotName;
        }
        else
        {
            sv.money -= h.BuyPrice;
            if (string.IsNullOrEmpty(sv.homeHouse)) sv.homeHouse = TDU2Garage.OwnedNames(sv)[0];
        }
        TDU2Garage.Normalize(sv, db, sv.homeHouse);
        // exe 0xAED241: die beiden Luxushäuser melden die Freunde ("Ihr Freund §FRIENDNAME§ hat eine Yacht gekauft!")
        if (string.Equals(h.SpotName, "EPH_L5Y_0362I", StringComparison.OrdinalIgnoreCase) || string.Equals(h.SpotName, "EPH_L5Y_6360", StringComparison.OrdinalIgnoreCase))
            Paradise.Game.Online.Friends.AnnounceHouse();
        _exchange = null;
        _bought = h;
        TDU2Session.NotifyChanged();
        Paradise.Game.Online.SessionAutosave.Schedule(0.5f);
        ui.SetVariable("/:MyMoneyStr", HouseUtils.Money(sv.money));
        // Kino C_BUY_HOUSE (Makler übergibt den Schlüssel CF_Key), ohne Kino gleich das Exterior-Kino am Haus
        _camRealtor = false;
        _phase = Phase.Buying;
        if (_cin != null && _cin.Find("C_BUY_HOUSE") != null)
        {
            _cin.SetPlayer(AvatarConfigs.Local.Clone());
            _cin.PlayMain("C_BUY_HOUSE");
            ui.Visible = false;
            return;
        }
        StartRemoval();
    }

    /// <summary>"seho" (0xAEBC60): Plätze ohne das gewählte Haus reichen für alle Autos -> /:EnoughMoney 1, sonst 0.</summary>
    static void CheckSell(VmfPlayer ui)
    {
        var h = SellHouse; var sv = TDU2Session.Save; var db = Db;
        if (h == null || sv == null || db == null) { ui.SetValue("/:EnoughMoney", 0); return; }
        int free = TDU2Garage.TotalSlots(sv, db) - TDU2Garage.CarCount(sv) - h.CarSlots;
        ui.SetValue("/:EnoughMoney", free < 0 || TDU2Garage.OwnedNames(sv).Count <= 1 ? 0 : 1);
    }

    /// <summary>"selh" (0xAED080): Verkaufspreis gutschreiben, Haus abgeben (Wohnsitz -> erstes Haus), /:NoHouses, Liste neu.</summary>
    static void Sell(VmfPlayer ui)
    {
        var h = SellHouse; var sv = TDU2Session.Save; var db = Db;
        if (h == null || sv == null || db == null) return;
        bool wasHome = string.Equals(sv.homeHouse, h.SpotName, StringComparison.OrdinalIgnoreCase);
        if (!TDU2Garage.RemoveHouse(sv, db, h.SpotName)) { Log.Warn("Immobilienbüro: " + h.SpotName + " kann nicht verkauft werden"); return; }
        sv.money += h.SellPrice;
        sv.housesSold++;
        if (wasHome) sv.homeHouse = TDU2Garage.OwnedNames(sv)[0];
        TDU2Session.NotifyChanged();
        Paradise.Game.Online.SessionAutosave.Schedule(0.5f);
        Log.Info($"Immobilienbüro: {h.SpotName} verkauft für {h.SellPrice} $");
        ui.SetVariable("/:MyMoneyStr", HouseUtils.Money(sv.money));
        ui.SetValue("/:NoHouses", TDU2Garage.OwnedNames(sv).Count <= 1);
        _sellSel = -1; _sellShown = -2;
        if (_sellList != null) { FillSellList(_sellList); ui.ListRefresh(_sellList); }
    }

    // ================================================================== Liste der eigenen Häuser (sellHouses_list)
    static HouseInfo? SellHouse => _sellSel >= 0 && _sellSel < _sellRows.Count ? _sellRows[_sellSel] : null;

    static void OnListCreated(VmfPlayer.FlashList l)
    {
        if (!string.Equals(l.Name, "sellHouses_list", StringComparison.OrdinalIgnoreCase)) return;
        _sellList = l;
        FillSellList(l);
        _ui2?.ListRefresh(l);
        _ui2?.ListLoaded(l);
        _sellShown = -2;
    }

    static void FillSellList(VmfPlayer.FlashList l)
    {
        l.ColumnVars = new[] { "CurHouse", "MyHouseFurniture", "CarPlaces", "PriceHouse" };
        _sellRows.Clear();
        l.Rows.Clear();
        var db = Db;
        if (db != null)
            foreach (var h in TDU2Garage.Houses(db, TDU2Session.Save))
            {
                _sellRows.Add(h);
                l.Rows.Add(new[] { HouseUtils.HouseLabel(h), HouseUtils.FurnitureCount(h.Level).ToString(CultureInfo.InvariantCulture),
                                   h.CarSlots.ToString(CultureInfo.InvariantCulture), HouseUtils.Money(h.SellPrice) });
            }
        if (l.Selected >= l.Rows.Count) { l.Selected = System.Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
    }

    /// <summary>Zeile bestätigt: Verkaufen bzw. Tauschen nimmt dieses Haus (Tausch: exe +0x1BC548, Bild exchange_change liest /:ExcArea).</summary>
    static void OnListValidated(VmfPlayer.FlashList l)
    {
        if (!ReferenceEquals(l, _sellList)) return;
        _sellSel = l.Selected;
        ShowSelected();
        var ui = _ui2;
        string from = ui?.GetScriptValue("/", "FromMenu") ?? ui?.GetVariable("/:FromMenu") ?? "";
        if (from.StartsWith("exchange", StringComparison.OrdinalIgnoreCase)) _exchange = SellHouse;
    }

    /// <summary>Daten des gewählten eigenen Hauses (/:selected_*, sold_menu/datas: Sterne, Gebiet, Minikarte).</summary>
    static void ShowSelected()
    {
        var ui = _ui2; var l = _sellList;
        if (ui == null || l == null) return;
        int i = l.Selected;
        _sellShown = i;
        var h = i >= 0 && i < _sellRows.Count ? _sellRows[i] : null;
        if (h == null) return;
        ui.SetVariable("/:selected_name", HouseUtils.HouseLabel(h));
        ui.SetVariable("/:selected_price", HouseUtils.Money(h.SellPrice));
        ui.SetValue("/:selected_CarPlaces", h.CarSlots);
        ui.SetVariable("/:selected_MiniGame", "");
        ui.SetVariable("/:ExcArea", HouseUtils.HouseArea(h));
        ui.GotoFrameNumber("/realtor_menus/sold_menu/datas/stars", h.Level);
        HouseUtils.PlaceOnMiniMap(ui, "/realtor_menus/sold_menu/datas/location", h);
    }

    // ================================================================== Befehle des Films
    static float Num(VmfPlayer ui, string var)
    {
        string name = var.StartsWith("/:") ? var.Substring(2) : var;
        string? s = ui.GetScriptValue("/", name) ?? ui.GetVariable(var);
        return float.TryParse(s ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    static void OnCommand(string cmd, string args)
    {
        if (_phase == Phase.Closed) return;
        var ui = _ui2; if (ui == null) return;
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (FrontendSounds.Command(c)) return;
        switch (c)
        {
            case "dbxo": AnswerDialog(); return;
            case "moup": ui.SetVariable("/:MyMoneyStr", HouseUtils.Money(MoneyNow)); return;
            case "rltr": Rltr(ui); return;
            case "rbuy": _exchange = null; SetPages(ui); return;
            case "rexc": case "rsel": SetPages(ui); _sellList = null; return;   // Original: Basisklasse (Film öffnet die Liste selbst)
            case "chho": ChangePage(ui); return;
            case "buho": CheckBuy(ui); return;
            case "buyh": Buy(ui); return;
            case "seho": CheckSell(ui); return;
            case "selh": Sell(ui); return;
            case "vtin": StartTour(); return;
            case "lohs": FillPage(ui); return;                     // Bank der Seite (Bilder) – hier sofort geladen
            case "camn": case "camf": return;                      // Kamera: C_CAM_REALTOR ab rltr
            case "gofr": case "wmon": if (_phase == Phase.Menu) Close(); return;
        }
        Log.Debug("Immobilienbüro: " + c);
    }

    /// <summary>Bestätigungsbox aus general.bnk wie im Spiel (FlashDialog), Antwort an _root.DialogBox (ValidateBuy / ValidateSell).</summary>
    static void AnswerDialog()
    {
        var ui = _ui2; if (ui == null) return;
        FlashDialog.Open(ui, ans =>
        {
            ui.SetVariable("/:PlayerAnswer", ans);
            var owners = ui.FunctionOwners("DialogBox");
            string target = owners.Contains("/") ? "/" : owners.Count > 0 ? owners[^1] : "/";
            ui.CallClip(target, "DialogBox");
        });
    }

    // ================================================================== Exterior-Kinos (0xAEA6D0): C_REMOVAL nach dem Kauf, C_VTOUR_E
    /// <summary>Nach C_BUY_HOUSE (0xAEB485): Exterior-Kino C_REMOVAL am neuen Haus, danach Freifahrt dort.</summary>
    static void StartRemoval()
    {
        var h = _bought;
        var hs = h != null ? HouseUtils.HouseSpot(h) : null;
        RemovalPending = true;                                  // 0x1722952: beim nächsten Betreten C_REMOVAL_I
        if (hs != null && PlayExterior(hs, "C_REMOVAL")) { ClearScene(); _phase = Phase.Removal; return; }
        LeaveTo(hs);
    }

    /// <summary>"vtin": virtuelle Tour – Exterior-Kino C_VTOUR_E am Haus der Seite, danach zurück ins Menü.</summary>
    static void StartTour()
    {
        var h = PageHouse;
        var hs = h != null ? HouseUtils.HouseSpot(h) : null;
        _tourPage = _page;
        _tourExchange = _exchange != null;
        _exchangeKeep = _exchange;
        if (hs == null || !PlayExterior(hs, "C_VTOUR_E"))
        {
            ReturnFromTour();
            return;
        }
        _phase = Phase.Tour;
    }

    /// <summary>Exterior-Bank des Haus-Spots öffnen und das Kino spielen (Raum aus, Welt sichtbar); false ohne Kino.</summary>
    static bool PlayExterior(Spot hs, string name)
    {
        if (_v?.Renderer == null) return false;
        _ext?.Dispose();
        _ext = SpotCinematics.OpenExterior(_v.Renderer, _t.Streamer, hs);
        if (_ext == null || _ext.Find(name) == null) { _ext?.Dispose(); _ext = null; return false; }
        _ext.SetPlayer(AvatarConfigs.Local.Clone());
        if (_ui2 != null) _ui2.Visible = false;
        ShopCinema.Close(ref _cin);
        _camRealtor = false;
        Paradise.Core.World.WorldWeather.Inside = false;
        _ext.PlayMain(name);
        return true;
    }

    /// <summary>Ende der Tour (exe: rltr mit 0x1722941): Büro-Menü an derselben Seite.</summary>
    static void ReturnFromTour()
    {
        _ext?.Dispose(); _ext = null;
        Paradise.Core.World.WorldWeather.Inside = true;
        var ctx = _ctx;
        _phase = Phase.Closed;
        Unhook();
        _tourBack = true;
        if (ctx == null) { _tourBack = false; GameMode.Set(RoomMode); return; }
        OpenShop(ctx);
    }

    /// <summary>Büro verlassen, Auto am Haus-Spot (Freifahrt), ohne Haus-Spot am Büro.</summary>
    static void LeaveTo(Spot? at)
    {
        var car = _lastCar;
        _ext?.Dispose(); _ext = null;
        CloseState();
        ExitInterior(car, at);
        GameMode.Set(Gamemode.GAME_MODE_FREERIDE);
    }

    // ================================================================== Ablauf je Bild
    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        _ctx = ctx;
        float dt = time.DeltaTime;
        try { Tick(ctx, dt); }
        catch (Exception ex) { Log.Error("Immobilienbüro: " + ex); Close(); }
    }

    static void Tick(IGameModeContext ctx, float dt)
    {
        _clock += dt;
        if (_phase == Phase.Closed || _t == null || ctx.Paused) return;

        if (_phase is Phase.Removal or Phase.Tour)
        {
            var ext = _ext;
            if (ext == null) { if (_phase == Phase.Tour) ReturnFromTour(); else LeaveTo(_bought != null ? HouseUtils.HouseSpot(_bought) : null); return; }
            ext.Update(dt);
            bool end = !ext.MainPlaying || ShopCinema.SkipPressed();
            foreach (var n in ext.TakeEndEvents()) if (n == "C_REMOVAL" || n == "C_VTOUR_E") end = true;
            if (end)
            {
                ext.StopMain();
                if (_phase == Phase.Tour) ReturnFromTour();
                else LeaveTo(_bought != null ? HouseUtils.HouseSpot(_bought) : null);
                return;
            }
            ShopCinema.Camera(ext, true);
            return;
        }

        ShopCinema.Update(_cin, dt);

        if (_phase == Phase.Buying)
        {
            var cin = _cin;
            bool end = cin == null || !cin.MainPlaying || ShopCinema.SkipPressed();
            if (cin != null) foreach (var n in cin.TakeEndEvents()) if (n == "C_BUY_HOUSE") end = true;
            if (end) { cin?.StopMain(); StartRemoval(); return; }
            if (cin != null) ShopCinema.Camera(cin, false);
            return;
        }

        var ui = _ui2;
        if (ui == null) return;
        // Liste der eigenen Häuser: hat der Film sie schon vor dem Öffnen angelegt (kein ListCreated), die aktive übernehmen
        var al = ui.ActiveList;
        if (al != null && !ReferenceEquals(al, _sellList) && string.Equals(al.Name, "sellHouses_list", StringComparison.OrdinalIgnoreCase))
            OnListCreated(al);
        if (_sellList != null && _sellList.Selected != _sellShown) ShowSelected();
        if (!FlashDialog.Active && FlashNav.Update(ui, out bool backHandled))
        {
            // Zurück, das der Film nicht verarbeitet (Wurzelmenü schon zu), bzw. zweimal schnell: Büro verlassen
            if (!backHandled || _clock - _lastEsc < 0.3f) { Close(); return; }
            _lastEsc = _clock;
        }
        if (_camRealtor && _cin != null) ShopCinema.Camera(_cin, false);
    }

    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        var st = _t?.Streamer;
        if (_phase is not (Phase.Removal or Phase.Tour)) base.Render(ctx, time);   // Interieur-Autos
        if (st == null) return;
        _cin?.Render(st.RootMatrix);
        _ext?.Render(st.RootMatrix);
    }
}