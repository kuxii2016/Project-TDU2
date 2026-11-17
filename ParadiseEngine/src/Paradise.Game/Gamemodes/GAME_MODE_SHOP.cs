using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Autohändler (shopscar.bnk, WhatPage "car_dealer") – Port von Unity TDU2CarDealer.cs, Händler-Teil: Kauffenster (Felgen, Lack,
/// Innenraum, Seiten über neop/prop), Vorschaubild window_buy/lure, Ausstellungsauto mit der gewählten Ausstattung, Kaufen,
/// Fahrzeugmenü des Händlers (Mini-Menü SetMiniMenu, Daten, Vergleichen, Einsteigen, Testfahrt).
/// Gemeinsam mit der Garage (wortgleich): Fahrzeugdaten schreiben (FillCarData), Einsteigen/Cockpit/Motor, Anvisieren – Utils/InteriorUtils.
/// </summary>
public class GAME_MODE_SHOP : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_SHOP;
    static readonly ScreenDef[] _screens = { ScreenDefs.Dealer };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Dealer;

    // ================================================================== Fahrzeugmenü öffnen / schließen

    /// <summary>Mini-Menü des Films (shopscar.vmf _root.SetMiniMenu) für ein Ausstellungsauto öffnen; false, wenn der Film es nicht kann.</summary>
    internal static bool OpenShopMenu(DisplayCar dc)
    {
        if (_ui == null || dc?.Car == null) return false;
        const string fn = "SetMiniMenu";
        if (_ui.FunctionOwners(fn).Count == 0) { Log.Warn("Autohändler: " + fn + " fehlt im Film"); return false; }
        _garageMenu = false;
        _dealerMenu = true; _dealerCar = dc; _dBought = false; _dChanged = false;
        _dPhase = DPhase.Opening; _dPhaseAt = _time; _dIdleSince = -1f;
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        try { FlashLocalization.Apply(_ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }
        PrepareOptions(dc);
        _ui.MouseEnabled = true;
        _ui.SetValue("/:WhatPlatform", 1);
        _ui.SetVariable("/:MenuType", "pc");
        _ui.SetValue("/:IsPC_X360", true);
        _ui.SetValue("/:fake", 0);
        _ui.SetValue("/:Input", true);
        _ui.SetVariable("/:WhatPage", "car_dealer");
        _ui.SetVariable("/:SelItemType", "vehicle");
        bool bike = IsBike(dc.Car);
        _ui.SetVariable("/:state_Vehicle", bike ? "bike" : "car");
        _ui.SetValue("/:IsBike", bike);
        _ui.SetValue("/:IsPassenger", false);
        _ui.SetValue("/:CanEnterCar", false);
        _ui.SetValue("/:IsRewardCar", false);
        _ui.SetValue("/:DLC_Locked", false);
        _ui.SetValue("/:IsCompare", _compareCar != null);
        _ui.SetValue("/:IsClassicDealer", 0);
        _ui.SetValue("/:HaveBought", false);
        _ui.SetValue("/:state_Demo", 0);
        _ui.SetValue("/:state_demo", 0);
        _ui.SetValue("/:IsTutorial", false);
        _ui.SetVariable("/:AfterValid", "");
        _ui.SetVariable("/:BtValid", "");
        foreach (var n in DealerClips) _ui.SetVisible("/" + n, true);
        UpdateTotals();
        // Wie ItemType("valid") im Spiel für ein Fahrzeug: Fahrzeugdaten/Preis zu, Hilfeleiste zu, dann SetMiniMenu (spielt auch
        // Label cd_buying) – öffnet /cursor "mini_menu", darüber schließt der Film das Menü später selbst (Vergleichen: Clip.B()).
        _ui.SetValue("/:ItemInfo", false);
        _ui.GotoLabel("/car_datas", "default", false);
        _ui.GotoLabel("/price", "default", false);
        _ui.CallClip("/", "SetHelp", "close");
        _ui.SetValue("/:Input", true);
        if (!_ui.CallClip("/", fn)) { _dealerMenu = false; _t.Input.CursorLocked = true; return false; }
        _ui.SetVisible("/cursor/mini_menu", true);
        _ui.SetValue("/:Input", true);
        Log.Info("Autohändler: Menü für " + dc.Car.FileName + $" ({_dRims.Count} Felgen, {_dColors.Count} Lackfarben, {_dInts.Count} Innenräume)");
        return true;
    }

    /// <summary>Zurück in den Laufmodus (Film steht wieder auf cd_stop).</summary>
    internal static void EndShopMenu(bool restoreCar = true)
    {
        if (!_dealerMenu) return;
        EndDealerCar();
        EndLure();
        _dealerMenu = false;
        _dTints.Clear();
        // nur wenn im Kauffenster etwas geändert wurde (sonst Auto nicht neu aufbauen – es "springt" sonst beim Schließen)
        if (restoreCar && _dChanged && !_dBought && _dealerCar != null)
        {
            // Ausstellungsauto wieder wie vorher (Ausstattung beim Blättern nur vorgeführt)
            _dColor = Math.Max(0, _dOrig.Color); _dRim = Math.Max(0, _dRims.FindIndex(r => r.Id == _dOrig.Rim)); _dInt = Math.Max(0, _dInts.FindIndex(i => i.Id == _dOrig.Interior));
            RequestPreview(_dOrig, 0f);
        }
        if (_ui != null)
        {
            _ui.GotoLabel("/cursor/mini_menu", "default", false);
            _ui.GotoLabel("/buy", "default", false);
            _ui.GotoLabel("/enter_car", "default", false);
            // Fahrzeugdaten/Preis zu (nach "Vergleichen" bliebe die Vergleichsanzeige sonst stehen)
            string cdl = _ui.Label("/car_datas") ?? "";
            if (cdl != "" && cdl != "default") _ui.GotoLabel("/car_datas", "close", true);
            string prl = _ui.Label("/price") ?? "";
            if (prl != "" && prl != "default") _ui.GotoLabel("/price", "close", true);
            _ui.SetValue("/:ItemInfo", false);
            if (_ui.Label("/") != "cd_stop") _ui.GotoLabel("/", "cd_stop", false);
            foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
            _ui.GotoLabel("/cursor", "default", false);   // Laufmodus: das nächste Anvisieren spielt wieder "on"
            _ui.SetValue("/:Input", true);
            _ui.MouseEnabled = false;
        }
        _uiAim = null; _dChanged = false; _dIdleSince = -1f;
        if (Inside) { _t.Input.CursorLocked = true; if (_fp != null) _fp.LookEnabled = true; }
    }

    /// <summary>ESC/Rücktaste im Händlermenü (Unity TDU2MenuFlow -> DealerBack).</summary>
    internal static void ShopBack()
    {
        if (_ui == null) { EndShopMenu(); return; }
        _ui.SetValue("/:Input", true);
        switch (ProbeDealerPhase())
        {
            case DPhase.Car:
                // enter_car.B(): lädt noch -> caca, sonst ExitVehicle -> caou, mcou, BackToWM
                if (!_ui.CallClip("/enter_car", "B")) { EndDealerCar(); _ui.GotoLabel("/enter_car", "default", false); }
                return;
            case DPhase.Window:
                if (!_ui.CallClip("/buy", "B")) _ui.GotoLabel("/buy", "close", true);
                return;
            case DPhase.Datas:
                // car_datas.B (fd_stop): fd_close -> zurück ins Mini-Menü
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
                EndShopMenu();
                return;
        }
    }

    /// <summary>Händlermenü ohne Mitwirkung des Films schließen.</summary>
    internal static void ShopForceClose()
    {
        EndDealerCar();
        EndLure();
        if (_ui != null)
        {
            _ui.GotoLabel("/buy", "default", false);
            _ui.GotoLabel("/enter_car", "default", false);
            _ui.GotoLabel("/price", "default", false);
            _ui.GotoLabel("/car_datas", "default", false);
        }
        EndShopMenu();
    }

    // ================================================================== Befehle des Films (shopscar.vmf)

    /// <summary>FSCommands des Händler-Films (Unity TDU2CarDealer.OnUiCommand): Kauffenster, Testfahrt, Dialog, Daten, Vergleichen, Einsteigen.</summary>
    internal static bool OnShopCommand(string c)
    {
        switch (c)
        {
            case "buin":                                     // Kauffenster öffnet
                _ui!.SetVisible("/buy", true);
                _ui.SetValue("/:num_option", 1);
                SetOptionVars(1);
                return true;
            case "neop": DealerPage(+1); return true;       // A: Seite weiter (bzw. Fenster öffnet)
            case "prop": DealerPage(-1); return true;       // B: Seite zurück
            case "chop": OnChangeOption(); return true;     // Element gewechselt
            case "eval": UpdateTotals(); return true;        // TestMoney liest danach _root.EnoughMoney
            case "moup": return true;                        // Geld wird bei buyc abgebucht
            case "buyc": BuyDealerCar(); return true;
            case "buou": EndLure(); return true;             // Fenster abgebrochen -> BackToWM (cd_stop)
            case "tstk": GAME_MODE_TESTCAR.StartTestDrive(); return true;
            case "dbxo": AnswerDealerDialog(); return true;
            case "rqca": BeginDealerCar(); return true;      // Einsteigen: Auto anfordern (Lader wartet auf CanEnterCar)
            case "caca":
            case "caou":                                     // Einsteigen abgebrochen / ausgestiegen
                EndDealerCar();
                if (_ui != null && _ui.Label("/enter_car") != "default") _ui.GotoLabel("/enter_car", "default", false);
                return true;
            case "wmon": case "wmof": return true;           // Laufmodus an/aus – die Phase kommt aus dem Film-Zustand
            case "cain": case "shin": return true;           // Innenkamera / Hilfeleiste – schon gesetzt
            case "gofr":
                // nach dem Kauf: mit dem neuen Auto hinaus
                if (_driveOutAt > 0f)
                {
                    var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
                    EndShopMenu(false);
                    ExitInterior(car);
                }
                else if (!_dBought) EndShopMenu();
                return true;
            case "seca": return true;
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

    // ================================================================== Jedes Bild

    /// <summary>Wartet die gewählte Aktion noch auf einen Befehl des Films?</summary>
    internal static bool ShopActionPending()
    {
        string bt = _ui?.GetVariable("/:BtValid") ?? "";
        float t = _time - _dIdleSince;
        if (bt == "test") return t < 3f;
        if (bt == "buy") return t < 1f;
        if (bt == "enter") return t < 1f;
        return false;
    }

    /// <summary>Jedes Bild, solange das Händlermenü offen ist.</summary>
    internal static void UpdateShopMenu(float dt)
    {
        if (_ui == null) { _dealerMenu = false; return; }
        ApplyDealerTints();
        var phase = ProbeDealerPhase();
        SetDealerPhase(phase);
        if (phase == DPhase.DriveOut)
        {
            if (_time < _driveOutAt) return;
            var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
            EndShopMenu(false);
            ExitInterior(car);
            return;
        }
        if (phase == DPhase.Car) { if (_carIn != CarIn.None) UpdateDealerCar(dt); return; }   // im Ausstellungsauto (Einsteigen)
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        if (phase == DPhase.Idle)
        {
            // nichts mehr offen: kurz abwarten (Übergänge, z.B. Daten -> Mini-Menü) und ob die Aktion noch etwas startet
            if (_dIdleSince < 0f) _dIdleSince = _time;
            if (_time - _dIdleSince >= 0.3f && !ShopActionPending()) EndShopMenu();
            return;
        }
        _dIdleSince = -1f;
        UpdateLure(phase == DPhase.Window ? Math.Clamp(IntVar("/:num_option"), 1, 3) : 0, dt);
        bool ok = TDU2Controls.ConfirmPressed();
        bool up = TDU2Controls.Pressed("menu_up"), down = TDU2Controls.Pressed("menu_down");
        bool left = TDU2Controls.Pressed("menu_left"), right = TDU2Controls.Pressed("menu_right");
        if (phase == DPhase.Mini) UnstickMiniMenu();
        if (phase == DPhase.Window)
        {
            if (ok) { _ui.SetValue("/:Input", true); _ui.CallClip("/buy", "A"); }
            else if (left) StepOption(-1);
            else if (right) StepOption(1);
        }
        else if (phase == DPhase.Mini && _ui.Label("/cursor/mini_menu") == "stop")
        {
            // nur im stehenden Menü (beim Öffnen/Schließen würden Eingaben im Film verloren gehen oder doppelt zählen)
            if (ok || up || down) _ui.SetValue("/:Input", true);
            if (ok) _ui.CallClip("/cursor/mini_menu", "A");
            else if (up) _ui.CallClip("/cursor/mini_menu", "Move", "up");
            else if (down) _ui.CallClip("/cursor/mini_menu", "Move", "down");
        }
    }


    // ================================================================== Kauffenster: Optionen (Felgen, Lack, Innenraum) – Unity TDU2CarDealer
    internal const string BuyWin = "/buy/window_buy";
    internal const string BuyLure = BuyWin + "/lure";

    internal static void PrepareOptions(DisplayCar dc)
    {
        var db = Vm!.Database!; var car = dc.Car;
        int dup = car.Raw != null ? car.Raw.I("_duplicate", 0) : 0;
        _dRims.Clear(); _dColors.Clear();
        if (db.CarRims.TryGetValue(car.Id, out var rl) || (dup != 0 && db.CarRims.TryGetValue(dup, out rl)))
            foreach (int id in rl) if (db.Rims.TryGetValue(id, out var r) && !_dRims.Contains(r)) _dRims.Add(r);
        // Standardfelge zuerst, danach nach Preis
        _dRims.Sort((a, b) => a.Id == car.DefaultRims ? -1 : b.Id == car.DefaultRims ? 1 : a.Price.CompareTo(b.Price));
        if ((db.Colors.TryGetValue(car.Id, out var cols) && cols.Count > 0) || (dup != 0 && db.Colors.TryGetValue(dup, out cols)))
            _dColors.AddRange(cols!);
        if (_dColors.Count > 24) _dColors.RemoveRange(24, _dColors.Count - 24);   // der Film hat 24 Farbfelder
        _dColor = dc.Assets?.Color != null ? Math.Max(0, _dColors.IndexOf(dc.Assets.Color)) : 0;
        _dRim = dc.Assets?.Rim != null ? Math.Max(0, _dRims.IndexOf(dc.Assets.Rim)) : 0;
        // Werkszustand des Ausstellungsautos (Lack 0, Standardfelge, erster Innenraum) – dahin beim Abbrechen zurück
        int ci = _dColor, ri = _dRim;
        _dColor = 0; _dRim = 0; RefreshInteriors(0);
        _dOrig = Choice();
        _dColor = ci; _dRim = ri;
        RefreshInteriors(dc.Assets?.Interior?.Id ?? 0);
    }

    /// <summary>Innenräume der gewählten Lackfarbe (DBCarsAndColors._interior_1..15, 1 = keiner).</summary>
    internal static void RefreshInteriors(int keepId)
    {
        _dInts.Clear();
        var db = Vm!.Database!;
        if (_dColor >= 0 && _dColor < _dColors.Count)
            foreach (int id in _dColors[_dColor].Interiors)
                if (id != 1 && id != 0 && db.Interiors.TryGetValue(id, out var ii) && !_dInts.Contains(ii)) _dInts.Add(ii);
        _dInt = Math.Max(0, _dInts.FindIndex(i => i.Id == keepId));
    }

    internal static CarChoice Choice() => new(_dColor,
        _dRim >= 0 && _dRim < _dRims.Count ? _dRims[_dRim].Id : 0,
        _dInt >= 0 && _dInt < _dInts.Count ? _dInts[_dInt].Id : 0);

    internal static bool Same(CarChoice a, CarChoice b) => a.Color == b.Color && a.Rim == b.Rim && a.Interior == b.Interior;

    internal static int CarPrice => _dealerCar?.Car?.Raw != null ? Math.Max(0, _dealerCar.Car.Raw.I("_price")) : 0;
    internal static int RimPrice => _dRim >= 0 && _dRim < _dRims.Count ? Math.Max(0, _dRims[_dRim].Price) : 0;
    internal static int PaintPrice => _dColor >= 0 && _dColor < _dColors.Count ? Math.Max(0, _dColors[_dColor].Price) : 0;
    internal static int InteriorPrice => _dInt >= 0 && _dInt < _dInts.Count ? Math.Max(0, _dInts[_dInt].Price) : 0;
    internal static long Total => (long)CarPrice + RimPrice + PaintPrice + InteriorPrice;
    // Unity: Geld nur angemeldet (Server-Spielstand); die Engine führt auch offline einen Spielstand (PLAYERSAVE) mit Geld
    internal static bool HasMoney => Paradise.Core.Online.TDU2Session.Save != null;
    internal static long MyMoney => Paradise.Core.Online.TDU2Session.Save?.money ?? 0;

    internal static string Money(long v) => "$ " + v.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    internal static string Loc(string var, string fallback)
    {
        var v = _ui!.GetVariable(var);
        return string.IsNullOrEmpty(v) || v.StartsWith("_") ? fallback : v;
    }

    internal static void UpdateTotals()
    {
        long total = Total;
        _ui!.SetVariable("/:TotalPrice", Money(total));
        _ui.SetVariable("/:TotalPriceStr", Money(total));
        _ui.SetVariable("/:current_price", Money(total));
        _ui.SetVariable("/:money", Money(MyMoney));
        _ui.SetVariable("/:MyMoneyStr", Money(MyMoney));
        _ui.SetValue("/:EnoughMoney", !HasMoney || MyMoney >= total ? 1 : 3);
        for (int i = 1; i <= 3; i++)
            _ui.SetVariable(BuyWin + "/op_price_" + i + ":op_price", Money(i == 1 ? RimPrice : i == 2 ? PaintPrice : InteriorPrice));
    }

    /// <summary>Seite num_option (1 Felgen, 2 Lack, 3 Innenraum) in die Film-Variablen.</summary>
    internal static void SetOptionVars(int option)
    {
        if (_ui == null) return;
        int nb, sel, price; string title, name; bool colors;
        switch (option)
        {
            case 1:
                nb = Math.Max(1, _dRims.Count); sel = _dRim; colors = false;
                title = Loc("/:m_Wheels_", "Felgen");
                name = _dRims.Count > 0 ? _dRims[_dRim].DisplayName : "Standard";
                price = RimPrice; break;
            case 2:
                nb = Math.Max(1, _dColors.Count); sel = _dColor; colors = _dColors.Count > 0;
                title = Loc("/:m_Paint_", "Lack");
                name = _dColors.Count > 0 ? _dColors[_dColor].Name : "Standard";
                price = PaintPrice; break;
            default:
                nb = Math.Max(1, _dInts.Count); sel = _dInt; colors = _dInts.Count > 0;
                title = Loc("/:m_Interior_", "Innenraum");
                name = _dInts.Count > 0 ? _dInts[_dInt].Name : "Standard";
                price = InteriorPrice; break;
        }
        sel = Math.Clamp(sel, 0, nb - 1);
        _ui.SetVariable("/:option_title", title);
        _ui.SetVariable("/:option_name", name ?? "");
        _ui.SetValue("/:has_colors", colors ? 1 : 0);
        _ui.SetValue("/:nb_items", nb);
        _ui.SetValue("/:num_item", sel + 1);
        _ui.SetValue("/:has_text", true);
        _ui.SetVariable("/:cur_op_price", Money(price));
        _ui.SetValue("/:isRimsChoice", _dRims.Count > 1);
        _ui.SetValue("/:isRimsFirst", sel == 0);
        _ui.SetValue("/:isRimsLast", sel >= nb - 1);
        _ui.SetValue("/:isPaintChoice", _dColors.Count > 1);
        _ui.SetValue("/:isInteriorChoice", _dInts.Count > 1);
        UpdateTotals();
        // Farbfelder: Lack (RAMPCLR1 der Lackfarbe) bzw. Innenraum (erste Innenfarbe)
        _dTints.Clear();
        if (colors)
            for (int i = 0; i < nb && i < 24; i++)
                _dTints[BuyWin + "/colors/color" + (i + 1)] = option == 2 ? PaintRgb(_dColors[i]) : InteriorRgb(_dInts[i]);
        ApplyDealerTints();
    }

    internal static Color32 Rgb(float r, float g, float b) => new((byte)(Mathf.Clamp01(r) * 255f + 0.5f), (byte)(Mathf.Clamp01(g) * 255f + 0.5f), (byte)(Mathf.Clamp01(b) * 255f + 0.5f), 255);

    internal static Color32 PaintRgb(CarColor? c)
    {
        var lib = _dealerCar?.Assets?.ColorLib;
        if (lib != null && c != null && !string.IsNullOrEmpty(c.ColorId)
            && lib.Materials.TryGetValue(MaterialDef.KeyFromName(c.ColorId), out var def) && def.TryGetParam("RAMPCLR1", out var v))
            return Rgb(v.X, v.Y, v.Z);
        return GuessColor(c?.Name);
    }

    internal static Color32 InteriorRgb(InteriorInfo? ii)
    {
        var lib = _dealerCar?.Assets?.ColorLib;
        if (lib != null && ii != null)
            foreach (var n in new[] { ii.Colors[0], ii.Material })
            {
                if (string.IsNullOrEmpty(n) || !lib.Materials.TryGetValue(MaterialDef.KeyFromName(n), out var def)) continue;
                if (def.TryGetParam("RAMPCLR1", out var v) || def.Params.TryGetValue(MaterialDef.KeyFromName("baseColor"), out v))
                    if (v.X + v.Y + v.Z > 0.001f) return Rgb(v.X, v.Y, v.Z);
            }
        return GuessColor(ii?.Name);
    }

    /// <summary>Notlösung ohne Materialfarbe: Farbname (englisch) grob zuordnen.</summary>
    internal static Color32 GuessColor(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (n.Contains("black") || n.Contains("noir")) return new Color32(20, 20, 22, 255);
        if (n.Contains("white") || n.Contains("blanc")) return new Color32(235, 235, 235, 255);
        if (n.Contains("red") || n.Contains("rouge")) return new Color32(170, 20, 20, 255);
        if (n.Contains("blue") || n.Contains("bleu")) return new Color32(30, 60, 160, 255);
        if (n.Contains("green") || n.Contains("vert")) return new Color32(30, 110, 50, 255);
        if (n.Contains("yellow") || n.Contains("gold") || n.Contains("jaune")) return new Color32(220, 180, 40, 255);
        if (n.Contains("orange") || n.Contains("saddle")) return new Color32(200, 100, 30, 255);
        if (n.Contains("silver") || n.Contains("grey") || n.Contains("gray") || n.Contains("gris")) return new Color32(150, 150, 155, 255);
        if (n.Contains("brown") || n.Contains("maroon") || n.Contains("beige") || n.Contains("tan")) return new Color32(120, 80, 50, 255);
        return new Color32(128, 128, 128, 255);
    }

    /// <summary>Der Film legt die Farbfelder bei Seitenwechseln neu an -> jedes Bild neu einfärben.</summary>
    internal static void ApplyDealerTints()
    {
        if (_ui == null) return;
        foreach (var kv in _dTints)
        {
            var d = _ui.Find(kv.Key);
            if (d == null) continue;
            var c = kv.Value;
            d.Cx = new ColorTransform { Mul = new Paradise.Core.Flash.Vector4(0f, 0f, 0f, 1f), Add = new Paradise.Core.Flash.Vector4(c.r / 255f, c.g / 255f, c.b / 255f, 0f) };
            d.ScriptCx = true;
        }
    }

    /// <summary>Blättern (Film hat /:num_item schon geändert): Auswahl übernehmen, Ausstellungsauto anpassen.</summary>
    internal static void OnChangeOption()
    {
        int option = IntVar("/:num_option"), item = IntVar("/:num_item") - 1;
        switch (option)
        {
            case 1: _dRim = Math.Clamp(item, 0, Math.Max(0, _dRims.Count - 1)); break;
            case 2:
                int keep = _dInt >= 0 && _dInt < _dInts.Count ? _dInts[_dInt].Id : 0;
                _dColor = Math.Clamp(item, 0, Math.Max(0, _dColors.Count - 1));
                RefreshInteriors(keep);
                break;
            case 3: _dInt = Math.Clamp(item, 0, Math.Max(0, _dInts.Count - 1)); break;
        }
        SetOptionVars(option);
        _dChanged = true;
        RequestPreview(Choice(), 0.3f);
    }

    /// <summary>Tastatur: voriges/nächstes Element der Seite (wie die Pfeil-Buttons des Fensters).</summary>
    internal static void StepOption(int dir)
    {
        int nb = IntVar("/:nb_items"), it = IntVar("/:num_item");
        int ni = Math.Clamp(it + dir, 1, Math.Max(1, nb));
        if (ni == it) return;
        _ui!.SetValue("/:num_item", ni);
        OnChangeOption();
        _ui.SetFrame(BuyWin + "/colors/square", ni);
        FrontendSounds.Play("move");
    }

    /// <summary>Seite weiter (neop aus A) bzw. zurück (prop aus B) – das tut im Spiel der Programmcode (_root.fake = 0).</summary>
    internal static void DealerPage(int dir)
    {
        string bl = _ui!.Label("/buy") ?? "";
        if (dir > 0 && bl == "car_action") { _ui.SetValue("/:num_option", 1); SetOptionVars(1); return; }   // Fenster öffnet
        if (dir < 0 && (bl == "close" || bl == "default")) return;                                          // B auf Seite 1: Fenster schließt
        int n = Math.Clamp(IntVar("/:num_option"), 1, 3);
        if (dir > 0 && n >= 3)
        {
            // letzte Seite bestätigt: Preisprüfung/Kaufdialog (confirm -> TestMoney)
            UpdateTotals();
            _ui.GotoLabel("/buy", "confirm", true);
            return;
        }
        int m = Math.Clamp(n + dir, 1, 3);
        if (m == n) return;
        _ui.SetValue("/:num_option", m);
        _ui.SetValue("/:hasChangeOption", true);
        SetOptionVars(m);
        _ui.GotoLabel("/buy", "next", true);   // -> options_stop: Fenster neu befüllen
    }

    internal static void AnswerDealerDialog()
    {
        string kase = _ui!.GetVariable("/:DgbxCase") ?? "";
        // Kein Geld: Preisanzeige rot ("no_money") und zurück zu den Optionen (em_3 "a" = back_options, em_0/em_2 "b")
        bool money = kase == "em_0" || kase == "em_2" || kase == "em_3";
        if (money)
        {
            string? clip = null;
            foreach (var p in new[] { "/price/price/ic", "/price/price", "/price" }) if (_ui.HasLabel(p, "no_money")) { clip = p; break; }
            if (clip != null) _ui.GotoLabel(clip, "no_money", false);
            else Log.Info("Autohändler: nicht genug Geld: " + Money(Total) + " (du hast " + Money(MyMoney) + ")");
        }
        if (kase == "vehicle_full") { EndDealerCar(); _ui.SetValue("/:Input", true); return; }   // Einsteigen-Zeitüberschreitung
        // Bestätigungsbox aus general.bnk wie im Spiel (Kauf bestätigen, kein Geld …); die Antwort entscheidet der Spieler,
        // _root.DialogBox des Films (shopscar.vmf) wertet PlayerAnswer je DgbxCase aus
        var ui = _ui!;
        FlashDialog.Open(ui, ans =>
        {
            ui.SetVariable("/:PlayerAnswer", ans);
            if (!ui.CallClip("/", "DialogBox")) ui.SetValue("/:Input", true);
        });
    }

    internal static void BuyDealerCar()
    {
        var dc = _dealerCar;
        if (dc?.Car == null || _dBought) return;
        long total = Total;
        var save = Paradise.Core.Online.TDU2Session.Save;
        if (save != null)
        {
            if (save.money < total) { Log.Info("Autohändler: nicht genug Geld."); return; }
            save.money = Math.Max(0, save.money - total);
        }
        _dBought = true;
        var choice = Choice();
        CarChoices.Set(dc.Car.FileName, choice);
        RememberOwned(dc.Car);
        if (Paradise.Core.Online.TDU2Session.LoggedIn) _ = Paradise.Core.Online.TDU2Session.SaveAsync();
        Log.Info($"Autohändler: {dc.Car.FileName} gekauft ({choice}) für {Money(total)}");
        // wie im Spiel: Fenster schließt, danach fährt man mit dem neuen Auto hinaus
        EndLure();
        _driveOutCar = dc.Car;
        _driveOutAt = _time + 1.2f;
    }

    /// <summary>Unity RememberOwned: gekauftes Auto in den Spielstand (eigene Autos, aktuelles Auto). Nie den Leihwagen.</summary>
    internal static void RememberOwned(CarPhysics? car)
    {
        if (GAME_MODE_TESTCAR.TestDriveActive || car == null) return;
        Paradise.Core.Online.TDU2Session.AddOwned(car.FileName);
        Paradise.Core.Online.TDU2Session.SetCurrentCar(car.FileName);
    }

    // ================================================================== Vorschaubild im Kauffenster (window_buy/lure)
    internal static bool _lureOn, _lureSnap;
    internal static int _lurePage;
    internal static Vector3 _lurePos; internal static Quaternion _lureRot; internal static float _lureFov;
    internal static Texture? _lureWrap; internal static IGpuTexture? _lureGpu;

    /// <summary>Kamera aufs Ausstellungsauto: Seite 1 Rad, 2 ganzes Auto, 3 Cockpit; 0 = aus (Unity UpdateLure).</summary>
    internal static void UpdateLure(int page, float dt)
    {
        var dc = _dealerCar; var st = _t.Streamer;
        if (page == 0 || dc == null || _ui == null || st == null) { RenderFx.LureOn = false; return; }
        if (!_lureOn)
        {
            float aspect = 4f / 3f;
            var sr = _ui.ScreenRect(BuyLure);
            if (sr.HasValue && sr.Value.height > 8f) aspect = Mathf.Clamp(sr.Value.width / sr.Value.height, 0.5f, 3f);
            int w = 512, h = Math.Clamp((int)MathF.Round(w / aspect), 128, 1024);
            RenderFx.LureWidth = w; RenderFx.LureHeight = h;
            _lureOn = true; _lureSnap = true;
        }
        if (page != _lurePage) _lurePage = page;
        LurePose(dc, page, out var pos, out var rot, out float fov);   // Unity-Raum
        if (_lureSnap) { _lurePos = pos; _lureRot = rot; _lureFov = fov; _lureSnap = false; }
        float k = 1f - MathF.Exp(-6f * dt);
        _lurePos = Vector3.Lerp(_lurePos, pos, k); _lureRot = Quaternion.Slerp(_lureRot, rot, k); _lureFov = Mathf.Lerp(_lureFov, fov, k);
        // in den Spiel-/Render-Raum (X gespiegelt)
        var eye = st.GameToRender(U.Mirror(_lurePos));
        var fwd = U.Mirror(U.Rot(_lureRot, Paradise.Core.Math.Vec.Forward));
        var up = U.Mirror(U.Rot(_lureRot, Paradise.Core.Math.Vec.Up));
        // kurzer Tiefenbereich gegen Z-Fighting der doppelten Bodenflächen (Unity: near 0,25/far 250, Cockpit 0,05/60)
        float near = page == 3 ? 0.05f : 0.25f, far = page == 3 ? 60f : 250f;
        RenderFx.LurePos = eye;
        RenderFx.LureView = Matrix4x4.CreateLookAt(eye, eye + fwd, up);
        RenderFx.LureProj = RenderFx.PerspectiveReversedZ(_lureFov, (float)RenderFx.LureWidth / RenderFx.LureHeight, near, far);
        RenderFx.LureOn = true;
        // Bild des Renderers über den Fensterteilen des Films (Unity SetClipTexture(BuyLure, _lureShow, true))
        var gpu = RenderFx.LureTexture;
        if (gpu != null && !ReferenceEquals(gpu, _lureGpu))
        {
            _lureGpu = gpu;
            _lureWrap = Texture.Wrap(gpu, RenderFx.LureWidth, RenderFx.LureHeight);
            _ui.SetClipTexture(BuyLure, _lureWrap, true);
        }
    }

    /// <summary>Unity LurePose – im Unity-Raum (Spielraum mit X gespiegelt), damit Drehrichtungen wie dort sind.</summary>
    internal static void LurePose(DisplayCar dc, int page, out Vector3 pos, out Quaternion rot, out float fov)
    {
        Vector3 P(Vector3 local) => U.Mirror(Vector3.Transform(local, dc.Pose));       // Fahrzeugraum des Spiels -> Unity-Welt
        Vector3 D(Vector3 local) => U.Mirror(Vector3.TransformNormal(local, dc.Pose));
        var carFwd = Vector3.Normalize(D(Vector3.UnitZ)); var carUp = Vector3.Normalize(D(Vector3.UnitY)); var carRight = Vector3.Normalize(D(-Vector3.UnitX));
        var bbSize = dc.Model.BodySize; var ext = bbSize * 0.5f;
        var center = P(dc.Model.BodyCenter);
        var viewer = _fp != null ? U.Mirror(_fp.Eye) : center + carFwd * 5f;
        if (page == 1)
        {
            // Rad auf der Seite des Spielers (vorne), seitlich leicht von vorn
            Vector3? best = null; float bd = float.MaxValue;
            foreach (var n in new[] { "D_RIM_FL", "D_RIM_FR", "DISC_FL", "DISC_FR", "D_TIRE_FL", "D_TIRE_FR" })
            {
                var t = dc.Model.Find(n);
                if (t == null) continue;
                var tp = P(dc.Model.LocalToVisualParent(t).Translation);
                float d = (tp - viewer).LengthSquared();
                if (d < bd) { bd = d; best = tp; }
            }
            Vector3 wheel = best ?? center + carFwd * ext.Z * 0.65f - carUp * ext.Y * 0.5f;
            float side = MathF.Sign(Vector3.Dot(wheel - center, carRight)); if (side == 0f) side = 1f;
            pos = wheel + carRight * side * 1.25f + carFwd * 0.45f + Vector3.UnitY * 0.15f;
            rot = U.LookRotation(wheel - pos, Vector3.UnitY);
            fov = 38f;
            return;
        }
        if (page == 3)
        {
            // Cockpit: Fahrerkamera des Autos (camerasingame Modus 23), etwas zurück und zur Mitte für mehr Innenraum
            if (CockpitEye(dc, false, out var eyeG, out var lookG))
            {
                var eye = U.Mirror(eyeG); var look = U.Mirror(lookG);
                pos = eye - Vector3.Normalize(look - eye) * 0.25f + Vector3.Normalize(center - eye) * 0.15f;
                rot = U.LookRotation(look - pos, carUp);
                fov = 70f;
                return;
            }
        }
        // ganzes Auto, schräg von vorn auf der Seite des Spielers
        var flat = viewer - center; flat.Y = 0f;
        if (flat.LengthSquared() < 0.01f) flat = carFwd;
        var dir = U.Rot(U.AngleAxis(Vector3.Dot(flat, carRight) >= 0f ? -25f : 25f, Vector3.UnitY), Vector3.Normalize(flat));
        float dist = MathF.Max(bbSize.X, bbSize.Z) * 1.15f + 1f;
        pos = center + dir * dist + Vector3.UnitY * (bbSize.Y * 0.55f);
        rot = U.LookRotation(center - pos, Vector3.UnitY);
        fov = 40f;
    }

    internal static void EndLure()
    {
        if (_ui != null && _lureWrap != null) _ui.SetClipTexture(BuyLure, null!);
        RenderFx.LureOn = false;
        _lureOn = false; _lureWrap = null; _lureGpu = null; _lurePage = 0;
    }

    // ================================================================== Ausstellungsauto mit anderer Ausstattung
    internal static Task<CarAssets>? _previewTask;
    internal static DisplayCar? _previewFor;
    internal static CarChoice _previewWant, _loadingChoice;
    internal static bool _previewDirty;
    internal static float _previewAt;

    internal static void RequestPreview(CarChoice want, float delay)
    {
        _previewWant = want; _previewFor = _dealerCar;
        _previewDirty = true; _previewAt = _time + delay;
    }

    /// <summary>Jedes Bild (auch nach dem Schließen: Auto zurücksetzen).</summary>
    internal static void PollDealerPreview()
    {
        if (_previewTask != null && _previewTask.IsCompleted)
        {
            var t = _previewTask; _previewTask = null;
            var target = _previewFor;
            if (t.IsFaulted) Log.Error("Autohändler-Vorschau: " + t.Exception?.GetBaseException().Message);
            else if (target == null || !_cars.Contains(target) || _scene == null || _carIn != CarIn.None) t.Result?.DestroyTextures();
            else
            {
                try { ReplaceDisplayCar(target, t.Result, _loadingChoice); }
                catch (Exception ex) { Log.Error("Autohändler-Vorschau: " + ex.Message); t.Result?.DestroyTextures(); }
            }
        }
        if (!_previewDirty || _previewTask != null || _time < _previewAt || _carIn != CarIn.None) return;
        _previewDirty = false;
        var dc = _previewFor; var vm = Vm;
        if (dc?.Car == null || vm?.Database == null || !_cars.Contains(dc) || Same(dc.Choice, _previewWant)) return;
        string bnk = vm.BnkDir; var db = vm.Database; var car = dc.Car; var w = _previewWant;
        _loadingChoice = w;
        _previewTask = Task.Run(() => CarAssets.Load(bnk, db, car, w.Color, false, w.Rim, w.Interior));
    }

    /// <summary>Tiefster Punkt der Räder (Spielraum-Y) – zum Ausrichten eines neu aufgebauten Ausstellungsautos.</summary>
    internal static bool WheelBottom(DisplayCar dc, out float y)
    {
        y = float.MaxValue;
        foreach (var name in new[] { "DISC_FL", "DISC_FR", "DISC_RL", "DISC_RR" })
        {
            var t = dc.Model.Find(name);
            if (t == null) continue;
            foreach (var n in t.SelfAndDescendants())
            {
                if (!n.HasMesh || !n.RendererEnabled) continue;
                var m = dc.Model.LocalToVisualParent(n) * dc.Pose;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    y = MathF.Min(y, Vector3.Transform(c, m).Y);
                }
            }
        }
        return y < float.MaxValue;
    }

    internal static void ReplaceDisplayCar(DisplayCar old, CarAssets a, CarChoice choice)
    {
        int idx = _cars.IndexOf(old);
        if (idx < 0) { a.DestroyTextures(); return; }
        // altes Auto vorher aus dem Weg (Kollision), gleiche Lage wie das alte Auto (Stellplatz + Bodenhöhe)
        var phys = Phys;
        if (old.Col != null) { phys?.Remove(old.Col); _carCols.Remove(old.Col); _own.Remove(old.Col); old.Col = null; }
        BuildDisplayCar(a, old.Car, old.Slot);
        var nd = _cars[^1];
        var p = nd.Pose.Translation; var op = old.Pose.Translation;
        var np = new Vector3(op.X, p.Y, op.Z);
        // Höhe: Räder des neuen Autos auf dieselbe Bodenhöhe wie die des alten (andere Felgen = anderer Raddurchmesser)
        nd.Pose.Translation = np;
        if (WheelBottom(old, out float ob) && WheelBottom(nd, out float nb) && MathF.Abs(ob - nb) < 0.6f) np.Y += ob - nb;
        var shift = np - p;
        nd.Pose.Translation = np; nd.BoxMin += shift; nd.BoxMax += shift;
        nd.Choice = choice;
        _cars.RemoveAt(_cars.Count - 1);
        _cars[idx] = nd;
        CarAccessories.Forget(old.Model);
        old.Model.Destroy();
        old.Assets.DestroyTextures();
        old.ShadowMesh?.Dispose(); old.ShadowMat?.Dispose(); old.ShadowBlob?.Dispose();
        if (_dealerCar == old) _dealerCar = nd;
        if (_previewFor == old) _previewFor = nd;
        if (ReferenceEquals(_uiAim, old)) _uiAim = nd;
    }
}
