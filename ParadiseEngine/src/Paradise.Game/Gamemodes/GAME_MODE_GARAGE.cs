using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Garage (home.bnk) – eigene Autos auf den Stellplätzen. Port von Unity TDU2CarDealer.cs, Garagen-Teil:
///  * Auto anvisieren + Bestätigen -> Fahrzeugmenü des Films (home.vmf _root.SetMenu): Losfahren, Einsteigen, Daten, Vergleichen.
///  * Fahrzeugdaten (car_datas, Vollansicht fd_*), Vergleichen (comp) / Vergleich aufheben (rsco).
///  * Einsteigen (enter_car: rqca, caca/caou), Hilfeleiste im Auto (star/honk/bcvi).
///  * Losfahren: Dialog drive_out/drive_out_list -> garage_out -> gofr -> mit dem Auto hinaus.
/// Gemeinsam mit dem Händler (wortgleich): Fahrzeugdaten schreiben (FillCarData), Einsteigen/Cockpit/Motor, Anvisieren – Utils/InteriorUtils.
///  * Fahrzeugliste (home.vmf garage/vehicule_list, native Liste ASFlashList "garage_vehicle_list"): über "list" im Menü bzw. das
///    Plakat (Auswahlpunkt Typ 12/14), Zeile wählen -> Dialog confirm_change_vehicule -> seca, garage_quit, gofr -> mit dem Auto hinaus.
///    Im Unity-Projekt nur angedeutet (IMGUI-Menü) – hier nach dem Original-Film gebaut.
/// Stellplätze tauschen (chpl:, Liste der Häuser) gibt es im Unity-Projekt nicht – noch offen.
/// </summary>
public class GAME_MODE_GARAGE : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_GARAGE;
    static readonly ScreenDef[] _screens = { ScreenDefs.Home };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Home;

    public override void Enter(IGameModeContext ctx, Gamemode from)
    {
        base.Enter(ctx, from);
        HookGarageList();
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        base.Run(ctx, time);
        UpdateGarageList();
    }

    // ================================================================== Fahrzeugliste (garage/vehicule_list)
    // Film (S501): "open" ruft ASFlashList(this, "garage_vehicle_list", 10 Zeilen, Spalten Gruppe/Fahrzeug/Leistung/Gebiet/Haus).
    // Zeilen line1..10: Texte ../:name, ../:power, ../:house; Clips content/ic (Gruppe A1..C4, mA1..), content/upg (level0..5),
    // content/area (h1..h4, i1, i2). Bestätigen: LineValidate -> list.OnValidate() -> DBXO("confirm_change_vehicule") ->
    // dbxo -> DialogBox: seca, garage_quit -> gofr.
    internal const string CarListPath = "/garage/vehicule_list";
    internal static VmfPlayer? _listHooked;
    internal static VmfPlayer.FlashList? _carList;
    internal static readonly List<CarPhysics> _listCars = new();
    internal static readonly List<HouseInfo?> _listHouses = new();
    internal static CarPhysics? _listChosen;
    internal static int _listDrawnTop = -1;
    internal static bool _listShown;
    internal static float _listOpenedAt;

    /// <summary>Ein Menü des Anschlagbretts ist offen (wie ein Menü: Maus frei, kein Laufen/Umsehen, ESC = B des Films):
    /// Mini-Menü (Liste / Platz wechseln), Fahrzeugliste, Platz wechseln – oder das Losfahren aus der Liste läuft noch.</summary>
    internal static bool GarageListOpen => _ui != null && _kind == Kind.Garage && !_dealerMenu
                                           && ((_listChosen != null && _driveOutAt > 0f) || CarListShown || ChangePlaceShown || BillboardMiniMenuShown);

    /// <summary>Fahrzeugliste: /garage auf "list" und vehicule_list auf "open"/"stop" (Film S501; "close" endet mit BackToWM bzw.
    /// bei bestätigter Zeile mit FSCommand "case").</summary>
    static bool CarListShown
    {
        get
        {
            var d = _ui!.Find(CarListPath);
            if (d == null || !d.Visible || _ui.Find("/garage") is not { Visible: true }) return false;
            if (_ui.Label("/garage") != "list") return false;
            string l = _ui.Label(CarListPath) ?? "";
            return l == "open" || l == "stop";
        }
    }

    /// <summary>Platz wechseln: /garage auf "change_place", Clip S479 auf "open"/"stop"/"block".</summary>
    static bool ChangePlaceShown
    {
        get
        {
            if (_ui!.Find("/garage") is not { Visible: true } || _ui.Label("/garage") != "change_place") return false;
            string l = _ui.Label(ChangePlacePath) ?? "";
            return l == "open" || l == "stop" || l == "block";
        }
    }

    /// <summary>Mini-Menü des Anschlagbretts (_root.SetMenu für SelItemType "billboard": Liste / Platz wechseln): Cursor auf "mini_menu".</summary>
    static bool BillboardMiniMenuShown => _ui!.Label("/cursor") == "mini_menu" && (_ui.GetScriptValue("/", "SelItemType") ?? _ui.GetVariable("/:SelItemType")) == "billboard";

    internal static void HookGarageList()
    {
        if (_ui == null || ReferenceEquals(_listHooked, _ui)) return;
        if (_listHooked != null) { _listHooked.ListCreated -= OnGarageListCreated; _listHooked.ListValidated -= OnGarageListValidated; }
        _ui.ListCreated += OnGarageListCreated;
        _ui.ListValidated += OnGarageListValidated;
        _listHooked = _ui;
    }

    /// <summary>Anschlagbrett in der Garage (Typ 12/14) bestätigt: wie im Spiel _root.cursor.ItemAction("valid") -> ItemType("valid") für
    /// "billboard". Das Original setzt beim Betreten der Garage /:EnableChangePlace = true (unpacked.exe 0xAC4667–0xAC468A), damit
    /// öffnet der Film das Mini-Menü (_root.SetMenu: "Liste" / "Platz wechseln"); ValidMenu spielt dann garage "list" bzw.
    /// "change_place".</summary>
    internal static void OpenGarageList()
    {
        if (_ui == null) return;
        if (_ui.Find("/garage") == null) { Log.Warn("Garage: home.vmf ohne /garage"); return; }
        HookGarageList();
        _listChosen = null; _carList = null;
        _ui.SetVisible("/garage", true);           // unsere Sichtbarkeitsliste (UiVisible) blendet den Clip sonst aus
        _ui.SetVariable("/:SelItemType", "billboard");
        _ui.SetValue("/:EnableChangePlace", true);
        _miniOpenedAt = _time;
        _ui.SetValue("/:Input", true);
        if (!_ui.CallClip("/cursor", "ItemAction", "valid"))
        {
            Log.Warn("Garage: cursor.ItemAction fehlt im Film");
            return;
        }
        _uiAim = null;                             // Cursor hat der Film selbst auf "default" gesetzt
        Log.Info("Garage: Fahrzeugliste geöffnet (Film /garage " + _ui.Label("/garage") + ", Liste " + (_ui.Label(CarListPath) ?? "-") + ")");
    }

    static void OnGarageListCreated(VmfPlayer.FlashList l)
    {
        if (!string.Equals(l.Name, "garage_vehicle_list", StringComparison.OrdinalIgnoreCase)) return;
        _carList = l;
        l.ColumnVars = new[] { "name", "power", "house" };
        _listCars.Clear(); _listHouses.Clear();
        var vm = Vm; var db = vm?.Database; var sv = Paradise.Core.Online.TDU2Session.Save;
        // Alle Autos aller Garagen mit ihrem Haus (Original: Spalte m_Home_ = Haus des Stellplatzes, m_Zone_ = Gebiet des Hauses);
        // Garage dieses Hauses zuerst, dann die übrigen Häuser, je Haus nach Stellplatz.
        var here = CurrentHouse();
        if (vm != null && db != null && sv != null)
        {
            Paradise.Core.Savegame.TDU2Garage.Normalize(sv, db, here?.SpotName);
            var houses = Paradise.Core.Savegame.TDU2Garage.Houses(db);
            if (here != null) houses.Sort((a, b) => (a == here ? 0 : 1).CompareTo(b == here ? 0 : 1));
            foreach (var h in houses)
                for (int k = 0; k < h.CarSlots; k++)
                {
                    string? file = Paradise.Core.Savegame.TDU2Garage.CarAt(sv, h.SpotName, k);
                    var c = file != null ? vm.FindCar(file) : null;
                    if (c != null && !_listCars.Contains(c)) { _listCars.Add(c); _listHouses.Add(h); }
                }
        }
        l.Rows.Clear();
        for (int i = 0; i < _listCars.Count; i++)
        {
            var c = _listCars[i];
            string house = HouseLabel(_listHouses[i]);
            var r = c.Raw;
            string version = r?.S("_version_name", "") ?? "";
            if (version == "##") version = "";
            string name = (string.IsNullOrEmpty(c.Brand) ? "" : c.Brand + " ") + c.ModelName + (version.Length > 0 ? " " + version : "");
            float bhp = r != null ? r.F("_power_bhp") : 0f;   // wie FillCarData; Einheit "CV" wie der Platzhalter im Film
            l.Rows.Add(new[] { name, ((int)MathF.Round(bhp)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " CV", house });
        }
        l.Selected = 0; l.Top = 0;
        _ui?.ListRefresh(l);
        _listDrawnTop = -1;
        ApplyListLineClips();
        _ui?.ListLoaded(l);
        Log.Info($"Garage: Fahrzeugliste mit {_listCars.Count} Autos");
    }

    static void OnGarageListValidated(VmfPlayer.FlashList l)
    {
        if (!ReferenceEquals(l, _carList)) return;
        _listChosen = l.Selected >= 0 && l.Selected < _listCars.Count ? _listCars[l.Selected] : null;
        Log.Info("Garage: aus der Liste gewählt " + (_listChosen?.FileName ?? "-"));
    }

    /// <summary>Haus dieser Garage (DBHouse).</summary>
    static HouseInfo? CurrentHouse()
    {
        var db = Vm?.Database; var s = _spot;
        return db != null && s != null ? db.HouseForSpot(s, _t.Streamer?.Index?.IslandName ?? "") : null;
    }

    /// <summary>Name eines Hauses (DBHouse._housename_hc, Original /:CurHouseName).</summary>
    static string HouseLabel(HouseInfo? h) => h == null ? "" : Lang(h.NameHash, h.SpotName ?? "");

    static readonly Dictionary<string, Spot?> _houseSpots = new(StringComparer.OrdinalIgnoreCase);
    static object? _houseSpotsFor;

    /// <summary>Spot eines Hauses auf der aktuellen Insel (null = andere Insel/unbekannt).</summary>
    static Spot? HouseSpot(HouseInfo h)
    {
        var spots = _t.Streamer?.Spots; var db = Vm?.Database;
        if (spots == null || db == null) return null;
        if (!ReferenceEquals(_houseSpotsFor, spots)) { _houseSpots.Clear(); _houseSpotsFor = spots; }
        if (_houseSpots.TryGetValue(h.SpotName, out var hit)) return hit;
        string island = _t.Streamer?.Index?.IslandName ?? "";
        Spot? found = null;
        foreach (var s in spots.Spots)
            if (s.Code != null && h.SpotName.StartsWith(s.Code, StringComparison.OrdinalIgnoreCase) && db.HouseForSpot(s, island) == h) { found = s; break; }
        _houseSpots[h.SpotName] = found;
        return found;
    }

    /// <summary>Gebiet eines Hauses (Flash-Label h1..h4 / i1..i2; Original /:CurHouseArea): Gebiet am Spot des Hauses, auf der anderen
    /// Insel nur die Insel ("h" bzw. "i" – beides gibt es als Label in content/area).</summary>
    static string HouseArea(HouseInfo? h)
    {
        if (h == null) return "";
        var s = HouseSpot(h);
        string? a = s != null ? ((GAME_MODE_PAUSE)GameModes.Get(Gamemode.GAME_MODE_PAUSE)).Menu?.AreaAt(s.Position.X, s.Position.Z) : null;
        return a ?? (h.SpotName.EndsWith("I", StringComparison.OrdinalIgnoreCase) ? "i" : "h");
    }

    /// <summary>Gruppe/Tuningstufe/Gebiet je sichtbarer Zeile (macht im Spiel die native Liste).</summary>
    static void ApplyListLineClips()
    {
        var l = _carList;
        if (_ui == null || l?.Owner == null) return;
        string op = VmfPlayer.PathOf(l.Owner);
        for (int i = 1; i <= l.ViewSize; i++)
        {
            int r = l.Top + i - 1;
            string lp = (op == "/" ? "" : op) + "/line" + i + "/content";
            if (r < 0 || r >= _listCars.Count) continue;
            var c = _listCars[r];
            int cat = c.Raw != null ? c.Raw.I("_vehiclecategory") : 0;
            _ui.GotoLabel(lp + "/ic", GroupLabel(cat), false);
            _ui.GotoLabel(lp + "/upg", CarUpgrades.LevelLabel(CarUpgrades.Get(c.FileName).Global), false);   // Tuningstufe (Tuner, CarUpgrades)
            string area = HouseArea(r < _listHouses.Count ? _listHouses[r] : null);
            if (area.Length > 0) _ui.GotoLabel(lp + "/area", area, false);
        }
        _listDrawnTop = l.Top;
    }

    /// <summary>Jedes Bild in der Garage: Zeilen-Clips beim Blättern nachführen. Maus/Umsehen/Laufen regelt UpdateInside über GarageListOpen.</summary>
    internal static void UpdateGarageList()
    {
        HookGarageList();
        bool open = GarageListOpen;
        if (open && _carList != null && _carList.Top != _listDrawnTop) ApplyListLineClips();
        if (open && !_listShown) _listOpenedAt = _time;
        // Eingabe an den Film (wie Tagebuch/My House): Hoch/Runter bewegt die native Liste, A = S501.A (Zeile bestätigen),
        // Y = S501.Y (Sortierung umkehren); ESC/Rücktaste = MenuBack -> GarageListBack. Fehlte – die Liste war nicht bedienbar.
        if (open && _ui != null && _driveOutAt <= 0f && _time - _listOpenedAt >= 0.25f && _time - _miniOpenedAt >= 0.25f)
        {
            bool cp = ChangePlaceShown;
            if (TDU2Controls.Pressed("menu_up")) _ui.Input("Move", "up");
            if (TDU2Controls.Pressed("menu_down")) _ui.Input("Move", "down");
            // Platz wechseln (S479): links/rechts = changePage (anderes Haus), sonst Move
            if (TDU2Controls.Pressed("menu_left")) { if (!cp || !_ui.Input("changePage", "left")) _ui.Input("Move", "left"); }
            if (TDU2Controls.Pressed("menu_right")) { if (!cp || !_ui.Input("changePage", "right")) _ui.Input("Move", "right"); }
            if (TDU2Controls.ConfirmPressed()) _ui.Input("A");
            if (TDU2Controls.Pressed("menu_opt2")) _ui.Input("Y");
        }
        if (!open && _listShown) { _uiAim = null; _carList = null; }   // Liste zu (B des Films: BackToWM) -> Laufmodus, neu anvisieren
        _listShown = open;
    }

    /// <summary>ESC/Rücktaste: B des offenen Menüs (Fahrzeugliste S501.B, Platz wechseln S479.B, Mini-Menü cursor.B).</summary>
    internal static void GarageListBack()
    {
        if (_ui == null) return;
        _ui.SetValue("/:Input", true);
        if (ChangePlaceShown) { if (!_ui.CallClip(ChangePlacePath, "B")) _ui.Input("B"); return; }
        if (BillboardMiniMenuShown) { if (!_ui.CallClip("/cursor", "B")) _ui.Input("B"); return; }
        if (!_ui.CallClip(CarListPath, "B")) { _ui.GotoLabel(CarListPath, "close", true); _ui.CallClip("/", "BackToWM"); }
    }

    /// <summary>Film reagiert nicht auf B: Liste und Garage-Clip zurücksetzen, Laufmodus.</summary>
    internal static void GarageListForceClose()
    {
        if (_ui != null)
        {
            _ui.GotoLabel(CarListPath, "default", false);
            _ui.GotoLabel(ChangePlacePath, "default", false);
            if (_ui.Label("/cursor") == "mini_menu") _ui.GotoLabel("/cursor", "default", false);
            _ui.GotoLabel("/garage", "default", false);
            _ui.SetVisible("/garage", false);
            _ui.SetValue("/:InGarage", false);
            _ui.SetValue("/:Input", true);
        }
        _listChosen = null; _carList = null; _cpSrc = -1;
        ResetUiAim();
    }

    /// <summary>Befehle des Films, solange die Liste (ohne Fahrzeugmenü) offen ist bzw. das Losfahren aus ihr läuft.</summary>
    internal static bool OnGarageListCommand(string c)
    {
        if (c.StartsWith("chpl:")) return OnChangePlaceCommand(c.Substring(5));
        switch (c)
        {
            case "dbxo" when (_ui?.GetVariable("/:DgbxCase") ?? "") == "confirm_change_vehicule" || _listChosen != null:
                AnswerGarageDialog();
                return true;
            case "case" when _listChosen != null:
                {
                    // Film S501 Bild 43: Liste nach bestätigter Zeile (valid) zu -> "case" an das Spiel. Das Losfahren läuft dann über den
                    // Dialog-Zweig "drive_out_list" von _root.DialogBox: PlayerAnswer "a" -> FSCommand seca, garage_quit -> Bild 168 gofr.
                    // Vorher wurde "case" nur geschluckt: nichts lief weiter, Eingabe blieb gesperrt (Softlock).
                    _ui!.SetVariable("/:DgbxCase", "drive_out_list");
                    _ui.SetVariable("/:PlayerAnswer", "a");
                    if (!_ui.CallClip("/", "DialogBox")) _ui.GotoLabel("/", "garage_quit", true);
                    if (_driveOutAt <= 0f) { _driveOutCar = _listChosen; _driveOutAt = _time + 0.9f; }
                    Log.Info("Garage: Losfahren aus der Liste mit " + (_listChosen.FileName ?? "?") + " (Wurzel " + _ui.Label("/") + ")");
                    return true;
                }
            case "seca": return _driveOutAt > 0f;
            case "gofr" when _driveOutAt > 0f:
                {
                    var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null; _listChosen = null;
                    ExitInterior(car);
                    return true;
                }
        }
        return false;
    }


    // ================================================================== Platz wechseln (garage/change_place, Film S479)
    // Film: initMenu sendet chpl:open; Hoch/Runter wählt den Stellplatz, links/rechts (changePage -> onChangePage) das Haus
    // (chpl:hsch, _root.CurHouse); A auf einem Platz: beginMove (chpl:slct, Quelle = _root.VehicleMovedIdx im Haus CurHouse),
    // A auf dem Ziel: validMove (_root.NewVehicleIdx, chpl:vali), B während des Verschiebens: stopMove + chpl:canc.
    // Spiel (unpacked.exe): open 0xAC3700, hsch 0xAC37F0, slct 0xAC2E50, canc 0xAC2E90, vali 0xAC3850, Füllen 0xAC3170,
    // Minikarte 0xAC3550, Bilder 0xAC3620.
    internal const string ChangePlacePath = "/garage/change_place";
    internal static float _miniOpenedAt;
    static List<HouseInfo>? _cpHouses;
    static int _cpHouse;                     // Index in _cpHouses (Original this+0x434)
    static int _cpSrcHouse = -1, _cpSrc = -1;   // gewählte Quelle (Original 0x444/0x448, gültig wenn 0x440)
    static readonly Dictionary<string, (Texture2D? gar, Texture2D? ext)> _housePics = new(StringComparer.OrdinalIgnoreCase);

    static bool OnChangePlaceCommand(string cmd)
    {
        var db = Vm?.Database; var sv = Paradise.Core.Online.TDU2Session.Save;
        if (_ui == null || db == null || sv == null) { Log.Warn("Platz wechseln: kein Spielstand/keine Datenbank"); return true; }
        switch (cmd)
        {
            case "open":
                {
                    // aus dem Fahrzeugmenü (ValidMenu "change_place"): das Menü des Films übernimmt – unser Fahrzeugmenü ist damit zu
                    if (_dealerMenu) { EndDealerCar(); _dealerMenu = false; _garageMenu = false; _dealerCar = null; }
                    _ui.SetVisible("/garage", true);
                    var here = CurrentHouse();
                    Paradise.Core.Savegame.TDU2Garage.Normalize(sv, db, here?.SpotName);
                    _cpHouses = Paradise.Core.Savegame.TDU2Garage.Houses(db);
                    _ui.SetValue("/:NbHouses", _cpHouses.Count);
                    _cpHouse = System.Math.Max(0, here != null ? _cpHouses.IndexOf(here) : 0);
                    _ui.SetValue("/:CurHouse", _cpHouse);
                    // Original: Platz des aktuellen Autos (0x1B25018+0xD28, 1-basiert) -> VehicleMovedIdx
                    var mine = _lastCar != null ? Paradise.Core.Savegame.TDU2Garage.SlotOf(sv, _lastCar.FileName) : null;
                    if (mine != null && here != null && string.Equals(mine.house, here.SpotName, StringComparison.OrdinalIgnoreCase))
                        _ui.SetValue("/:VehicleMovedIdx", mine.slot);
                    _cpSrc = -1; _cpSrcHouse = -1;
                    FillChangePlace();
                    Log.Info("Platz wechseln: geöffnet, " + _cpHouses.Count + " Häuser, Haus " + _cpHouse);
                    return true;
                }
            case "hsch":
                {
                    int n = _cpHouses?.Count ?? 0;
                    int h = (int)MathF.Round(Num("/:CurHouse"));
                    _cpHouse = h >= 0 && h < n ? h : 0;
                    FillChangePlace();
                    return true;
                }
            case "slct":
                _cpSrcHouse = _cpHouse;
                _cpSrc = (int)MathF.Round(Num("/:VehicleMovedIdx"));
                return true;
            case "canc":
                _cpSrc = -1; _cpSrcHouse = -1;
                return true;
            case "vali":
                {
                    int dst = (int)MathF.Round(Num("/:NewVehicleIdx"));
                    if (_cpHouses != null && _cpSrc >= 0 && _cpSrcHouse >= 0 && _cpSrcHouse < _cpHouses.Count && _cpHouse < _cpHouses.Count)
                    {
                        var a = _cpHouses[_cpSrcHouse]; var b = _cpHouses[_cpHouse];
                        Paradise.Core.Savegame.TDU2Garage.Swap(sv, a.SpotName, _cpSrc, b.SpotName, dst);
                        Log.Info($"Platz wechseln: {a.SpotName} Platz {_cpSrc} <-> {b.SpotName} Platz {dst}");
                        Paradise.Game.Online.SessionAutosave.Schedule(0.5f);
                        // Autos dieser Garage neu aufstellen (Original 0xAC3878: Szene neu belegen)
                        if (_data != null) { ClearCars(); QueueCars(_data, false); }
                    }
                    _cpSrc = -1; _cpSrcHouse = -1;
                    FillChangePlace();
                    return true;
                }
        }
        return false;
    }

    /// <summary>Zahl einer Wurzel-Variable, die der Film per Skript setzt (_root.CurHouse …).</summary>
    static float Num(string var)
    {
        string name = var.StartsWith("/:") ? var.Substring(2) : var;
        string? s = _ui?.GetScriptValue("/", name) ?? _ui?.GetVariable(var);
        return float.TryParse(s ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    /// <summary>Original 0xAC3170: Daten des Hauses _cpHouse und seine Stellplätze in den Film.</summary>
    static void FillChangePlace()
    {
        var db = Vm?.Database; var sv = Paradise.Core.Online.TDU2Session.Save; var vm = Vm;
        if (_ui == null || db == null || sv == null || vm == null || _cpHouses == null || _cpHouses.Count == 0) return;
        var h = _cpHouses[System.Math.Clamp(_cpHouse, 0, _cpHouses.Count - 1)];
        string name = HouseLabel(h);
        _ui.SetVariable("/:CurHouseName", name);
        _ui.SetVariable("/:/House_Name", name);
        _ui.SetValue("/:CurHouseLevel", h.Level);
        _ui.SetValue("/:IsCurHouse", h == CurrentHouse());
        _ui.SetValue("/:NbParkSlots", h.CarSlots);
        _ui.SetValue("/:NbParkSlotsUsed", Paradise.Core.Savegame.TDU2Garage.Used(sv, h.SpotName));
        _ui.SetVariable("/:CurHouseArea", HouseArea(h));
        for (int i = 0; i < h.CarSlots; i++)
        {
            string lp = ChangePlacePath + "/list/line" + i;
            string? file = Paradise.Core.Savegame.TDU2Garage.CarAt(sv, h.SpotName, i);
            var c = file != null ? vm.FindCar(file) : null;
            if (c == null) { _ui.SetValue(lp + ":is_slot_empty", true); continue; }
            _ui.SetValue(lp + ":is_slot_empty", false);
            // Original schreibt "category" zweimal (erst die Tuningstufe, dann die Gruppe) und nie "upgd_level"
            _ui.SetVariable(lp + ":category", GroupLabel(c.Raw != null ? c.Raw.I("_vehiclecategory") : 0));
            var r = c.Raw;
            string version = r?.S("_version_name", "") ?? "";
            if (version == "##") version = "";
            _ui.SetVariable(lp + ":vehicle_name", (string.IsNullOrEmpty(c.Brand) ? "" : c.Brand + " ") + c.ModelName + (version.Length > 0 ? " " + version : ""));
        }
        FillChangePlaceMap(h);
        FillChangePlacePictures(h);
    }

    /// <summary>Original 0xAC3550: Lage des Hauses auf der Minikarte (location/circle relativ zu location/lure, wie die Pausenkarte).</summary>
    static void FillChangePlaceMap(HouseInfo h)
    {
        const string loc = ChangePlacePath + "/infos/mini_map/location";
        var s = HouseSpot(h);
        var lure = _ui!.Find(loc + "/lure");
        if (s == null || lure == null) return;
        var r = _ui.LocalBounds(lure);
        if (!r.HasValue) return;
        bool hawaii = (_t.Streamer?.Index?.IslandName ?? "").StartsWith("h", StringComparison.OrdinalIgnoreCase);
        float ax = (hawaii ? 11 : 7) * 6144f, az = (hawaii ? 9 : 6) * 6144f;
        _ui.SetPosition(loc + "/circle", new Vector2(r.Value.width * s.Position.X / ax, r.Value.height * s.Position.Z / az));
    }

    /// <summary>Original 0xAC3620: Bilder Garage/Außenansicht (lure_garage / lure_exterior) aus FrontEnd/AllRes/RealtorScr/&lt;haus&gt;.bnk.</summary>
    static void FillChangePlacePictures(HouseInfo h)
    {
        if (!_housePics.TryGetValue(h.SpotName, out var p))
        {
            try
            {
                string? f = string.IsNullOrEmpty(FlashHost.BnkDir) ? null
                    : CarDatabase.FindFile(Path.Combine(FlashHost.BnkDir, "frontend", "AllRes", "RealtorScr"), h.SpotName.ToLowerInvariant() + ".bnk");
                if (f != null) p = (FlashTextures.TextureNamed(f, "IMG_GAR"), FlashTextures.TextureNamed(f, "IMG_EXT"));
                else Log.Warn("Platz wechseln: kein Bild RealtorScr/" + h.SpotName.ToLowerInvariant() + ".bnk");
            }
            catch (Exception ex) { Log.Warn("Platz wechseln: Bilder " + h.SpotName + ": " + ex.Message); }
            _housePics[h.SpotName] = p;
        }
        _ui!.SetClipTexture(ChangePlacePath + "/infos/lures/lure_garage", p.gar!);
        _ui.SetClipTexture(ChangePlacePath + "/infos/lures/lure_exterior", p.ext!);
    }

    // ================================================================== Fahrzeugmenü öffnen / schließen

    /// <summary>Fahrzeugmenü des Films (home.vmf _root.SetMenu) für ein eigenes Auto öffnen; false, wenn der Film es nicht kann.</summary>
    internal static bool OpenGarageMenu(DisplayCar dc)
    {
        if (_ui == null || dc?.Car == null) return false;
        const string fn = "SetMenu";
        if (_ui.FunctionOwners(fn).Count == 0) { Log.Warn("Garage: " + fn + " fehlt im Film"); return false; }
        _garageMenu = true;
        _dealerMenu = true; _dealerCar = dc; _dBought = false; _dChanged = false;
        _dPhase = DPhase.Opening; _dPhaseAt = _time; _dIdleSince = -1f;
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        try { FlashLocalization.Apply(_ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }
        _dRims.Clear(); _dColors.Clear(); _dInts.Clear(); _dOrig = dc.Choice;
        _ui.MouseEnabled = true;
        _ui.SetValue("/:WhatPlatform", 1);
        _ui.SetVariable("/:MenuType", "pc");
        _ui.SetValue("/:IsPC_X360", true);
        _ui.SetValue("/:fake", 0);
        _ui.SetValue("/:Input", true);
        _ui.SetVariable("/:SelItemType", "vehicle");
        bool bike = IsBike(dc.Car);
        _ui.SetVariable("/:state_Vehicle", bike ? "bike" : "car");
        _ui.SetValue("/:IsBike", bike);
        _ui.SetValue("/:IsPassenger", false);
        _ui.SetValue("/:CanEnterCar", false);
        _ui.SetValue("/:IsRewardCar", false);
        _ui.SetValue("/:DLC_Locked", false);
        _ui.SetValue("/:IsCompare", _compareCar != null);
        // Garage: eigene Autos (Losfahren, Platz wechseln – Original setzt /:EnableChangePlace beim Betreten auf true, 0xAC4685)
        _ui.SetValue("/:FromClub", false);
        _ui.SetValue("/:AmIOwner", true);
        _ui.SetValue("/:EnableChangePlace", true);
        _ui.SetValue("/:IsBache", false);
        _ui.SetValue("/:LockBorrowVehicle", false);
        _ui.SetValue("/:CanBuyVehicle", false);
        _ui.SetValue("/:IsClassicDealer", 0);
        _ui.SetValue("/:HaveBought", false);
        _ui.SetValue("/:state_Demo", 0);
        _ui.SetValue("/:state_demo", 0);
        _ui.SetValue("/:IsTutorial", false);
        _ui.SetVariable("/:AfterValid", "");
        _ui.SetVariable("/:BtValid", "");
        foreach (var n in DealerClips) _ui.SetVisible("/" + n, true);
        // Wie ItemType("valid") im Spiel für ein Fahrzeug: Fahrzeugdaten zu, Hilfeleiste zu, dann SetMenu – öffnet /cursor "mini_menu",
        // darüber schließt der Film das Menü später selbst.
        _ui.SetValue("/:ItemInfo", false);
        _ui.GotoLabel("/car_datas", "default", false);
        _ui.CallClip("/", "SetHelp", "close");
        _ui.SetValue("/:Input", true);
        if (!_ui.CallClip("/", fn)) { _dealerMenu = false; _t.Input.CursorLocked = true; return false; }
        _ui.SetVisible("/cursor/mini_menu", true);
        _ui.SetValue("/:Input", true);
        Log.Info("Garage: Menü für " + dc.Car.FileName);
        return true;
    }

    /// <summary>Zurück in den Laufmodus (Film steht wieder auf garage_stop).</summary>
    internal static void EndGarageMenu()
    {
        if (!_dealerMenu) return;
        EndDealerCar();
        _dealerMenu = false;
        _dTints.Clear();
        if (_ui != null)
        {
            _ui.GotoLabel("/cursor/mini_menu", "default", false);
            _ui.GotoLabel("/buy", "default", false);
            _ui.GotoLabel("/enter_car", "default", false);
            // Fahrzeugdaten zu (nach "Vergleichen" bliebe die Vergleichsanzeige sonst stehen) – beim nächsten Anvisieren
            // öffnet der Film sie wieder (mit IsCompare als Vergleich)
            string cdl = _ui.Label("/car_datas") ?? "";
            if (cdl != "" && cdl != "default") _ui.GotoLabel("/car_datas", "close", true);
            string prl = _ui.Label("/price") ?? "";
            if (prl != "" && prl != "default") _ui.GotoLabel("/price", "close", true);
            _ui.SetValue("/:ItemInfo", false);
            if (_ui.Label("/") != "garage_stop") _ui.GotoLabel("/", "garage_stop", false);
            foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
            _ui.GotoLabel("/cursor", "default", false);   // Laufmodus: das nächste Anvisieren spielt wieder "on"
            _ui.SetValue("/:Input", true);
            _ui.MouseEnabled = false;
        }
        _uiAim = null; _dChanged = false; _dIdleSince = -1f;
        if (Inside) { _t.Input.CursorLocked = true; if (_fp != null) _fp.LookEnabled = true; }
    }

    /// <summary>ESC/Rücktaste im Garagenmenü (Unity TDU2MenuFlow -> DealerBack).</summary>
    internal static void GarageBack()
    {
        if (_ui == null) { EndGarageMenu(); return; }
        _ui.SetValue("/:Input", true);
        switch (ProbeDealerPhase())
        {
            case DPhase.Car:
                // enter_car.B(): lädt noch -> caca, sonst ExitVehicle -> caou, mcou, BackToWM
                if (!_ui.CallClip("/enter_car", "B")) { EndDealerCar(); _ui.GotoLabel("/enter_car", "default", false); }
                return;
            case DPhase.Datas:
                // car_datas.B (fd_stop): fd_close -> zurück ins Menü
                if (!_ui.CallClip("/car_datas", "B")) _ui.GotoLabel("/car_datas", "fd_close", true);
                return;
            case DPhase.Mini:
                // wie die B-Taste: cursor.B -> wmon, camf, mini_menu close, BackFromMiniMenu
                if (!_ui.CallClip("/cursor", "B"))
                {
                    _ui.GotoLabel("/cursor/mini_menu", "close", true);
                    _ui.CallClip("/", "BackFromMiniMenu");
                }
                _ui.SetVariable("/:BtValid", "");
                return;
            case DPhase.DriveOut:
                return;
            default:
                EndGarageMenu();
                return;
        }
    }

    /// <summary>Garagenmenü ohne Mitwirkung des Films schließen.</summary>
    internal static void GarageForceClose()
    {
        EndDealerCar();
        if (_ui != null)
        {
            _ui.GotoLabel("/enter_car", "default", false);
            _ui.GotoLabel("/car_datas", "default", false);
        }
        EndGarageMenu();
    }

    // ================================================================== Befehle des Films (home.vmf)

    /// <summary>FSCommands des Garagenmenüs (Unity TDU2CarDealer.OnUiCommand, Garage).</summary>
    internal static bool OnGarageCommand(string c)
    {
        switch (c)
        {
            case "dbxo": AnswerGarageDialog(); return true;
            case "rqca": BeginDealerCar(); return true;      // Einsteigen: Auto anfordern (Lader wartet auf CanEnterCar)
            case "caca":
            case "caou":                                     // Einsteigen abgebrochen / ausgestiegen
                EndDealerCar();
                if (_ui != null && _ui.Label("/enter_car") != "default") _ui.GotoLabel("/enter_car", "default", false);
                return true;
            case "wmon": case "wmof": return true;           // Laufmodus an/aus – die Phase kommt aus dem Film-Zustand
            case "cain": case "shin": return true;           // Innenkamera / Hilfeleiste – schon gesetzt
            case "gofr":
                // Ende von garage_quit (Losfahren): jetzt mit dem Auto hinaus
                if (_driveOutAt > 0f)
                {
                    var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
                    EndGarageMenu();
                    ExitInterior(car);
                }
                else EndGarageMenu();
                return true;
            case "seca": return true;                        // Auto gewählt (das Losfahren folgt mit gofr)
            case "comp":                                     // Vergleichen: dieses Auto ist jetzt das Vergleichsauto
                _compareCar = _dealerCar?.Car;
                if (_compareCar != null) { FillCarData(_compareCar, true); _ui!.SetValue("/:IsCompare", true); }
                return true;
            case "rsco":                                     // Vergleich aufheben
                _compareCar = null; _ui!.SetValue("/:IsCompare", false);
                return true;
            // Hilfeleiste im Auto (Mausklick auf die Knöpfe): Anlasser, Hupe, Rückblick
            case "star": if (_carIn == CarIn.Inside) ToggleDealerEngine(); return true;
            case "honk": _inHornClick = true; return true;
            case "hoof": _inHornClick = false; return true;
            case "bcvi": _inBackClick = true; return true;
            case "ftvi": _inBackClick = false; return true;
            case "stao":
            case "shko":
            case "stci":
            case "sbdd":
            case "dain":
            case "camn":
            case "camf":
            case "mcin":
            case "mcou":
            case "shou":
                return true;
        }
        return false;
    }

    /// <summary>Garage (home.vmf): Dialog beantworten; "drive_out" (Losfahren) -> Film spielt garage_out, danach mit dem Auto hinaus.</summary>
    internal static void AnswerGarageDialog()
    {
        string kase = _ui!.GetVariable("/:DgbxCase") ?? "";
        if (kase == "vehicle_full") { EndDealerCar(); _ui.SetValue("/:Input", true); return; }
        // Bestätigungsbox aus general.bnk wie im Spiel; Losfahren nur bei "Ja"
        var ui = _ui!;
        FlashDialog.Open(ui, ans =>
        {
            Log.Info("Garage: Dialog '" + kase + "' -> " + ans);
            ui.SetVariable("/:PlayerAnswer", ans);
            if (!ui.CallClip("/", "DialogBox"))
            {
                if (ans == "a" && kase == "drive_out" && ui.HasLabel("/", "garage_out")) ui.GotoLabel("/", "garage_out", true);
                ui.SetValue("/:Input", true);
            }
            if (ans == "a" && (kase == "drive_out" || kase == "drive_out_list") && _dealerCar != null)
            {
                _driveOutCar = _dealerCar.Car;
                _driveOutAt = _time + 0.9f;
            }
            else if (ans == "a" && kase == "confirm_change_vehicule" && _listChosen != null)
            {
                // aus der Fahrzeugliste: mit dem gewählten Auto hinaus (Film: seca, garage_quit, dann gofr)
                _driveOutCar = _listChosen;
                _driveOutAt = _time + 0.9f;
            }
        });
    }

    // ================================================================== Jedes Bild

    /// <summary>Wartet die gewählte Aktion noch auf einen Befehl des Films?</summary>
    internal static bool GarageActionPending()
    {
        string bt = _ui?.GetVariable("/:BtValid") ?? "";
        float t = _time - _dIdleSince;
        if (bt == "enter") return t < 1f;
        if (bt == "select") return t < 2f;   // Dialog "Losfahren"
        return false;
    }

    /// <summary>Jedes Bild, solange das Garagenmenü offen ist.</summary>
    internal static void UpdateGarageMenu(float dt)
    {
        if (_ui == null) { _dealerMenu = false; return; }
        var phase = ProbeDealerPhase();
        SetDealerPhase(phase);
        if (phase == DPhase.DriveOut)
        {
            if (_time < _driveOutAt) return;
            var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
            EndGarageMenu();
            ExitInterior(car);
            return;
        }
        if (phase == DPhase.Car) { if (_carIn != CarIn.None) UpdateDealerCar(dt); return; }   // im Auto (Einsteigen)
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        if (phase == DPhase.Idle)
        {
            // nichts mehr offen: kurz abwarten (Übergänge, z.B. Daten -> Menü) und ob die Aktion noch etwas startet
            if (_dIdleSince < 0f) _dIdleSince = _time;
            if (_time - _dIdleSince >= 0.3f && !GarageActionPending()) EndGarageMenu();
            return;
        }
        _dIdleSince = -1f;
        bool ok = TDU2Controls.ConfirmPressed();
        bool up = TDU2Controls.Pressed("menu_up"), down = TDU2Controls.Pressed("menu_down");
        if (phase == DPhase.Mini) UnstickMiniMenu();
        if (phase == DPhase.Mini && _ui.Label("/cursor/mini_menu") == "stop")
        {
            // nur im stehenden Menü (beim Öffnen/Schließen würden Eingaben im Film verloren gehen oder doppelt zählen)
            if (ok || up || down) _ui.SetValue("/:Input", true);
            if (ok) _ui.CallClip("/cursor/mini_menu", "A");
            else if (up) _ui.CallClip("/cursor/mini_menu", "Move", "up");
            else if (down) _ui.CallClip("/cursor/mini_menu", "Move", "down");
        }
    }
}
