using Paradise.Core.Audio;
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Wohnbereich (home.vmf) – Port von Unity TDU2Interiors (Tagebuch), TDU2MyLife, TDU2MyHouse und TDU2TDUMedia:
///  * Tagebuch (Auswahlpunkt Typ 9): Film-Label "diary_in" -> Menü mit My Life / My House.
///  * MyTDULife (/mylf): Stimmung, Stimmungstext (Bildschirmtastatur), Kästen, Profilangaben, Geburtsdatum, Geschlecht –
///    gespeichert in SaveData.myLife. Das Avatar-Foto braucht den Avatar (in der Engine noch nicht vorhanden): der Sucher
///    schließt wieder.
///  * My House (myhs_*): Möbel tauschen (DBFurniture, gleicher Typ, Gruppen gemeinsam), Böden/Tapeten (DBMaterial, Spender-Bänke
///    ground_NN / wall_NN mit Vorschaubildern), Kaufen, gespeichert je Haus-Spot (SaveData.houses, ownedFurniture/ownedMaterials).
///  * TDU Media (Fernseher, Typ 57, tdmedia.bnk): Uhr, Wetter beider Inseln (jetzt / +2 h / +4 h), Meldungen zum Spielfortschritt.
///    Server-News, Umfragen, Freundes-Meldungen und Patch-Notes kommen in Unity vom Server – den gibt es in der Engine noch nicht.
/// Interieur-Ablauf (Betreten, Szene, Laufen, Hotspots): InteriorUtils.
/// </summary>
public class GAME_MODE_LIVING : InteriorUtils
{
    public override Gamemode Mode => Gamemode.GAME_MODE_LIVING;
    static readonly ScreenDef[] _screens = { ScreenDefs.Home };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Home;



    internal static bool HomeMenuOpen => _diaryOpen || _myHouse != HouseMode.None || _mediaOpen;
    /// <summary>Maus frei für den Film (Tagebuch, Fernseher, Möbel-/Material-Editor ohne Vorschau).</summary>
    internal static bool HomeMenuFreesMouse => _mediaOpen || _diaryOpen || ((_myHouse == HouseMode.Furniture || _myHouse == HouseMode.Material) && !_housePreview);
    /// <summary>Umsehen/Laufen erlaubt (kein Tagebuch/Fernseher; im Editor nur in der Vorschau).</summary>
    internal static bool HomeAllowsLook => !_mediaOpen && !_diaryOpen && (_myHouse == HouseMode.None || _myHouse == HouseMode.Walk || _housePreview);

    internal static bool LifeShown()
    {
        var d = _ui?.Find("/mylf");
        if (d == null || !d.Visible) return false;
        string l = _ui!.Label("/mylf");
        return !string.IsNullOrEmpty(l) && l != "default" && l != "close";
    }

    /// <summary>Jedes Bild im Interieur (nicht pausiert); true = ein Wohnbereich-Menü hat das Bild (kein Anvisieren/Händler).</summary>
    internal static bool UpdateHomeMenus(float dt)
    {
        PollHouseObjects();
        PollDonors();
        if (_mediaOpen) { UpdateMedia(dt); return true; }
        if ((_myHouse != HouseMode.None || _houseRestoreAt > 0f) && UpdateMyHouse()) return true;
        if (_diaryOpen) { UpdateDiary(); return true; }
        return false;
    }

    internal static void OnHomeCommand(string c)
    {
        if ((c.StartsWith("myhs:") || _myHouse != HouseMode.None) && OnMyHouseCommand(c)) return;
        if (_myHouse != HouseMode.None && c == "dbxo") { AnswerUiDialog(); return; }
        if ((c.StartsWith("mylf:") || c == "tfoc" || c == "vtke") && _ui != null && OnMyLifeCommand(c)) return;   // MyTDULife
        if (_diaryOpen) OnDiaryCommand(c);
    }

    /// <summary>Beim Verlassen/Neuaufbau der Szene.</summary>
    internal static void EndHomeModes()
    {
        if (_mediaOpen) CloseMedia();
        if (_diaryOpen) { _diaryOpen = false; EndLifeModes(); SaveLifeIfDirty(); }
        ClearHouse();
        _backLayer = MenuLayer.None;
    }

    // ================================================================== Tagebuch (Unity TDU2Interiors.OpenDiary/UpdateDiary)
    internal static bool _diaryOpen, _diaryChecked; internal static float _diaryOpenedAt, _diaryStuckAt = -1f;
    internal static readonly string[] DiaryClips = { "diary", "mylf", "mylf_book", "myhs_editor", "help", "help_system", "title_bar", "cmon_loader", "cmon_loading", "club_money" };

    internal static void OpenDiary()
    {
        if (_ui == null || _ui.Find("/diary") == null) { Log.Warn("Tagebuch: home.vmf ohne /diary"); return; }
        _diaryOpen = true; _diaryChecked = false;
        _diaryOpenedAt = _time;
        UpdateUiAim(null);
        if (_fp != null) _fp.LookEnabled = false;
        _t.Input.CursorLocked = false;
        try { FlashLocalization.Apply(_ui, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }
        _ui.MouseEnabled = true;
        _ui.SetValue("/:WhatPlatform", 1);
        _ui.SetVariable("/:MenuType", "pc");
        _ui.SetValue("/:IsPC_X360", true);
        _ui.SetValue("/:fake", false);
        _ui.SetValue("/:Input", true);
        _ui.SetValue("/:IsMyHouse", true);
        // _root.ValidMenu() verzweigt nach SelItemType: nur bei "diary" führen die Einträge (my_house, my_life …) weiter
        _ui.SetVariable("/:SelItemType", "diary");
        _ui.SetVariable("/:WhatDiaryMenu", "diary");
        _ui.SetValue("/:LockMyHouse", false);
        _ui.SetValue("/:LockMyPhotos", true);      // Fotoalbum noch nicht umgesetzt -> gesperrt (Film spielt "wrong")
        _ui.SetVariable("/:BtValid", "");
        _diaryStuckAt = -1f;
        var sv = TDU2Session.Save;
        _ui.SetVariable("/:MyMoneyStr", sv != null ? sv.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : " ");
        foreach (var n in DiaryClips) _ui.SetVisible("/" + n, true);
        // Fadenkreuz des Laufmodus aus (der Film blendet es in seinen Dialogen ebenso aus, DBXO: cursor._visible = false);
        // sonst stand es mitten im Tagebuch/MyTDULife. Fehlt im Unity-Code. CloseDiary blendet es über UiVisible wieder ein.
        _ui.SetVisible("/cursor", false);
        _ui.GotoLabel("/", "diary_in", true);
        EnsureHouseCatalog();
        Log.Info("Tagebuch geöffnet");
    }

    internal static void CloseDiary()
    {
        if (!_diaryOpen) return;
        _diaryOpen = false;
        EndLifeModes();
        SaveLifeIfDirty();
        if (_ui != null)
        {
            _ui.GotoLabel("/", _kind == Kind.Garage ? "garage_stop" : "living_stop", false);
            foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
            // Hilfeleiste des Tagebuchs/MyTDULife ("Info" …) schließen: beim normalen Zurück macht das der Film selbst, beim
            // Schließen durch das Spiel (ESC erneut, Film reagiert nicht) blieb sie stehen. Fehlt im Unity-Code.
            // MyTDULife (S201) auf Ruhebild "default": nach einem Schließen durch das Spiel (ESC erneut, MenuBack -> ForceCloseLayer) lief die
            // Schließ-Folge des Films im ausgeblendeten Clip weiter und öffnete danach die Hilfeleiste ("A Info") wieder.
            string ml = _ui.Label("/mylf") ?? "";
            if (ml != "" && ml != "default") { Log.Info("Tagebuch: MyTDULife stand auf '" + ml + "' -> default"); _ui.GotoLabel("/mylf", "default", false); }
            _ui.CallClip("/", "SetHelp", "close", false);
            _ui.SetValue("/:Input", true);
            _ui.MouseEnabled = false;
        }
        ResetUiAim();   // Hilfeleiste/low_bar des Tagebuchs ("Info") schließen – sonst blieb sie ohne Ziel stehen
        _t.Input.CursorLocked = true;
        Log.Info("Tagebuch geschlossen");
    }

    internal static void UpdateDiary()
    {
        if (_fp != null) _fp.LookEnabled = false;
        if (_ui == null) { CloseDiary(); return; }
        _ui.MouseEnabled = true;
        // Rückfall: steht das Tagebuch nach 1,5 s noch nicht im Menü (Bild "stop"), selbst öffnen
        if (!_diaryChecked && _time - _diaryOpenedAt > 1.5f)
        {
            _diaryChecked = true;
            var di = _ui.Find("/diary")?.Sprite;
            Log.Info("Tagebuch: Wurzel " + _ui.Label("/") + ", diary " + _ui.Label("/diary") + " (Bild " + (di != null ? di.Frame + 1 : 0) + "), Input " + _ui.GetVariable("/:Input"));
            if (di == null || di.Frame < 19)
            {
                _ui.SetVisible("/diary", true);
                _ui.GotoLabel("/", "diary_stop", true);
                _ui.GotoLabel("/diary", "open", true);
                _ui.SetValue("/:Input", true);
            }
        }
        DiaryWatchdog();
        if (_time - _diaryOpenedAt < 0.2f) return;   // Taste, mit der geöffnet wurde
        if (UpdateMyLife()) return;   // MyTDULife: Texteingabe hat die Tastatur
        if (TDU2Controls.Pressed("menu_up")) _ui.Input("Move", "up");
        if (TDU2Controls.Pressed("menu_down")) _ui.Input("Move", "down");
        if (TDU2Controls.Pressed("menu_left")) _ui.Input("Move", "left");
        if (TDU2Controls.Pressed("menu_right")) _ui.Input("Move", "right");
        if (TDU2Controls.ConfirmPressed()) _ui.Input("A");
        if (TDU2Controls.Pressed("menu_opt2")) _ui.Input("Y");
        if (TDU2Controls.Pressed("menu_opt1")) _ui.Input("X");
        // ESC/Rücktaste: MenuBack (GAME_MODE_LIVING.Run)
    }

    /// <summary>
    /// Sicherung (Unity DiaryWatchdog): nach der Auswahl hat ValidMenu nicht weitergeschaltet (Menü wieder "default"/"stop",
    /// Input aus) -> die Tagebuch-Verzweigung von ValidMenu nachspielen.
    /// </summary>
    internal static void DiaryWatchdog()
    {
        string ml = _ui!.Label("/diary/menu");
        bool stuck = _ui.Label("/") == "diary_stop" && _ui.Label("/diary") == "stop" && (ml == "default" || ml == "stop") && _ui.GetVariable("/:Input") != "true"
                     && !string.IsNullOrEmpty(_ui.GetVariable("/:BtValid"));
        if (!stuck) { _diaryStuckAt = -1f; return; }
        if (_diaryStuckAt < 0f) { _diaryStuckAt = _time; return; }
        if (_time - _diaryStuckAt < (ml == "default" ? 0.5f : 2f)) return;
        _diaryStuckAt = -1f;
        string bt = _ui.GetVariable("/:BtValid") ?? "";
        Log.Info("Tagebuch: ValidMenu ohne Wirkung (BtValid '" + bt + "') – Verzweigung nachgespielt");
        switch (bt)
        {
            case "my_house":
                _ui.GotoLabel("/diary", "default", false);
                _ui.GotoLabel("/title_bar", "close", true);
                _ui.CallClip("/", "SetHelp", "close", false);
                _ui.GotoLabel("/", "myhs_open", true);     // Bild 90: myhs:open, wmon, camf -> StartMyHouse()
                break;
            case "rankings":
                _ui.CallClip("/", "SetHelp", "close", false);
                _ui.GotoLabel("/diary", "menus", true);
                break;
            case "my_life":
                _ui.GotoLabel("/title_bar", "close", true);
                OnMyLifeCommand("mylf:self");
                _ui.GotoLabel("/diary", "my_life", true);
                _ui.CallClip("/", "SetHelp", "back", true);
                _ui.GotoLabel("/mylf", "load", true);
                break;
            default:
                _ui.GotoLabel("/diary", "stop", true);
                _ui.SetValue("/:Input", true);
                break;
        }
    }

    internal static void OnDiaryCommand(string c)
    {
        switch (c)
        {
            case "wmon":                 // diary_out (Bild 89): zurück in den Laufmodus
            case "gofr":
                CloseDiary(); return;
            case "camf": case "camn": return;
            case "dbxo": AnswerUiDialog(); return;
        }
        Log.Info("Tagebuch FSCommand: " + c);
    }

    /// <summary>Bestätigungsdialog des Films (DBXO -> "dbxo"): Box aus general.bnk wie im Spiel, Antwort an _root.DialogBox.</summary>
    internal static void AnswerUiDialog()
    {
        var ui = _ui;
        if (ui == null) return;
        string kase = ui.GetVariable("/:DgbxCase") ?? "";
        FlashDialog.Open(ui, ans =>
        {
            Log.Info("Interieur-Dialog '" + kase + "' -> " + ans);
            ui.SetVariable("/:PlayerAnswer", ans);
            // home.vmf: die echte DialogBox steht in _root (Unter-Clips haben nur leere Platzhalter gleichen Namens)
            var owners = ui.FunctionOwners("DialogBox");
            string? owner = owners.Contains("/") && ui.Find("/myhs_editor") != null ? "/" : (owners.Count > 0 ? owners[^1] : null);
            if (owner == null || !ui.CallClip(owner, "DialogBox")) ui.SetValue("/:Input", true);
        });
    }

    // ================================================================== MyTDULife (Unity TDU2MyLife)
    internal sealed class LifeList { public string Id = "", Var = ""; public int[] Hc = Array.Empty<int>(); }
    internal static int[] HcRange(int a, int b) { var r = new int[b - a + 1]; for (int i = a; i <= b; i++) r[i - a] = i; return r; }

    internal static readonly LifeList[] LifeLists =
    {
        new() { Id = "attitude", Var = "EdAttitude", Hc = HcRange(3490, 3496) },
        new() { Id = "driving_camera", Var = "EdDrivingCamera", Hc = HcRange(3497, 3500) },
        new() { Id = "driving_help", Var = "EdDrivingHelp", Hc = HcRange(3503, 3505) },
        new() { Id = "gamer_type", Var = "EdGamerType", Hc = HcRange(3525, 3532) },
        new() { Id = "game_genre", Var = "EdGameGenre", Hc = HcRange(3533, 3544) },
        new() { Id = "gamer_kind", Var = "EdGamerKind", Hc = HcRange(3545, 3547) },
        new() { Id = "like_tdu", Var = "EdLikeTDU", Hc = HcRange(3506, 3509) },
        new() { Id = "fav_game_mode", Var = "EdFavGameMode", Hc = new[] { 3510, 3511, 3512, 3513, 3514, 3516 } },
        new() { Id = "fav_island", Var = "EdFavIsland", Hc = new[] { 3519, 3520 } },
        new() { Id = "fav_car_category", Var = "EdFavCarCategory", Hc = HcRange(3522, 3524) },
        new() { Id = "play_frequency", Var = "EdPlayFrequency", Hc = HcRange(3486, 3489) },
    };
    internal static readonly string[] DefaultLifeBoxes = { "driver_profile", "gamer_profile", "racing", "cruising", "collection", "social", "vehicles" };
    internal const int BirthYearMin = 1940, BirthYearMax = 2012;

    internal static MyLifeData? Life
    {
        get
        {
            var sv = TDU2Session.Save;
            if (sv == null) return null;
            sv.myLife ??= new MyLifeData();
            sv.myLife.boxes ??= new List<string>();
            sv.myLife.fields ??= new List<HouseEntry>();
            return sv.myLife;
        }
    }

    internal static bool _lifeDirty;
    internal static string? _lifeSnap;   // Stand vor der laufenden Bearbeitung (edca stellt ihn wieder her)
    internal static readonly System.Text.Json.JsonSerializerOptions LifeJson = new() { IncludeFields = true };

    internal static int LifeField(MyLifeData? l, string id)
    {
        if (l != null) foreach (var e in l.fields) if (e.k == id && int.TryParse(e.v, out int v)) return v;
        return -1;
    }

    internal static void SetLifeField(MyLifeData? l, string id, int v)
    {
        if (l == null) return;
        foreach (var e in l.fields) if (e.k == id) { e.v = v.ToString(CultureInfo.InvariantCulture); return; }
        l.fields.Add(new HouseEntry { k = id, v = v.ToString(CultureInfo.InvariantCulture) });
    }

    internal static string LifeText(LifeList l, int idx) => idx >= 0 && idx < l.Hc.Length ? Lang(l.Hc[idx], "?") : "-";
    internal static string MonthText(int m) => m >= 1 && m <= 12 ? Lang(3549 + m, m.ToString("00")) : "--";

    internal static void PushMyLifeVars()
    {
        if (_ui == null) return;
        var l = Life;
        var sv = TDU2Session.Save;
        var de = Paradise.Core.Util.GermanFormat.Number;
        _ui.SetValue("/:LockMyLifeEdition", false);
        _ui.SetValue("/:AmericanDateFormat", false);
        _ui.SetValue("/:IsCasino", false);
        _ui.SetValue("/:HasCasino", false);
        _ui.SetValue("/:isCasinoStandalone", false);
        if (!string.IsNullOrEmpty(TDU2Session.Nick)) _ui.SetVariable("/:AvatarName", TDU2Session.Nick);
        _ui.SetValue("/:NumTitle", 1);
        int cars = sv?.ownedCars?.Count ?? 0;
        _ui.SetVariable("/:Money", (sv != null ? sv.money : 0).ToString("N0", de) + " $");
        _ui.SetVariable("/:NbCars", cars.ToString());
        _ui.SetVariable("/:NbHouses", Math.Max(1, sv?.houses?.Count ?? 0).ToString());
        foreach (var g in new[] { "Racing", "Social", "Cruising", "Collection" })
        {
            _ui.SetValue("/:" + g + "Points", 0);
            _ui.SetValue("/:" + g + "LvlPoints", 0);
            _ui.SetValue("/:Is" + g + "GaugeFull", false);
        }
        _ui.SetValue("/:GlobalLevel", 1);
        _ui.SetValue("/:GlobalPoints", 0);
        _ui.SetValue("/:GlobalLvlPoints", 0);
        _ui.SetVariable("/:GlobalLevelCount", "1");
        _ui.SetValue("/:HasClub", false);
        _ui.SetValue("/:ClubLevel", 0);
        _ui.SetVariable("/:ClubName", " ");
        _ui.SetVariable("/:NbClubMembers", "--");
        _ui.SetVariable("/:NbClubCars", "--");
        double km = 0, bestKm = 0;
        if (sv?.odometers != null) foreach (var o in sv.odometers) { km += o.meters / 1000.0; if (o.meters / 1000.0 > bestKm) bestKm = o.meters / 1000.0; }
        _ui.SetVariable("/:DrivenDistance", km.ToString("N0", de) + " km");
        _ui.SetVariable("/:DrivenDistanceFR", km.ToString("N0", de) + " km");
        _ui.SetVariable("/:MostUsedCarKm", bestKm.ToString("N0", de) + " km");
        _ui.SetVariable("/:NbVehOwned", cars.ToString());
        _ui.SetVariable("/:NbVehiclesBought", cars.ToString());
        _ui.SetVariable("/:NbClothesBought", (sv?.ownedClothes?.Count ?? 0).ToString());
        _ui.SetVariable("/:NbFurnituresBought", (sv?.ownedFurniture?.Count ?? 0).ToString());
        _ui.SetVariable("/:NbStickersBought", (sv?.stickerBoards?.Count ?? 0).ToString());
        _ui.SetVariable("/:EdCarSticked", (sv?.liveries?.Count ?? 0).ToString());
        _ui.SetVariable("/:NbHousesTotal", Math.Max(1, sv?.houses?.Count ?? 0).ToString());
        foreach (var z in new[] { "NbCarUpgraded", "NbEventsWon", "NbWrecksFound", "NbPhotosIDone", "NbPhotosHDone", "NbFriends", "NbFriendsInvited",
                                  "NbInstantWon", "NbInstantTotal", "NbMultiWon", "NbMultiTotal", "NbCoopPlayed", "NbChasePlayed", "NbSurgeriesBought" })
            _ui.SetVariable("/:" + z, "0");
        foreach (var z in new[] { "PlayTime", "GameCompletion", "UnlockedRoadsPercent", "LongestCruise", "BestJump", "BestDrift", "BestDodgeChain", "BestFrogSession" })
            _ui.SetVariable("/:" + z, "--");
        if (l == null) return;
        string mood = string.IsNullOrEmpty(l.mood) ? "neutral" : l.mood;
        _ui.SetVariable("/:WhatMood", mood);
        _ui.SetVariable("/:MoodTxt", l.moodText ?? "");
        _ui.SetVariable("/:MoodTxtInput", l.moodText ?? "");
        _ui.SetVariable("/:ed0", "mood");
        for (int i = 1; i <= 7; i++)
            _ui.SetVariable("/:ed" + i, i - 1 < l.boxes.Count && !string.IsNullOrEmpty(l.boxes[i - 1]) ? l.boxes[i - 1] : DefaultLifeBoxes[i - 1]);
        _ui.SetVariable("/:ed8", "empty");
        _ui.SetValue("/:NbEdBoxes", 8);
        foreach (var li in LifeLists) _ui.SetVariable("/:" + li.Var, LifeText(li, LifeField(l, li.Id)));
        PushBirth(l);
        // Unity: sonst Geschlecht des Avatars (AvatarConfigs) – den Avatar gibt es in der Engine noch nicht
        _ui.SetVariable("/:EdPlayerGender", !string.IsNullOrEmpty(l.gender) ? l.gender : "male");
        _ui.SetVariable("/:EdMail", "");
    }

    internal static void PushBirth(MyLifeData l)
    {
        _ui!.SetVariable("/:EdBirthDay", l.birthDay > 0 ? l.birthDay.ToString("00") : "--");
        _ui.SetVariable("/:EdBirthMonth", MonthText(l.birthMonth));
        _ui.SetVariable("/:EdBirthYear", l.birthYear > 0 ? l.birthYear.ToString() : "----");
        _ui.SetVariable("/:PlayerAge", l.birthDay > 0 && l.birthMonth > 0 && l.birthYear > 0
            ? l.birthDay.ToString("00") + " " + MonthText(l.birthMonth) + " " + l.birthYear : "-");
    }

    internal static void SaveLifeIfDirty()
    {
        if (!_lifeDirty) return;
        _lifeDirty = false;
        ReadLifeBoxes();
        if (TDU2Session.Save != null) _ = TDU2Session.SaveAsync();
    }

    /// <summary>Kästen ed1..ed7 aus dem Film (der Film tauscht/verschiebt sie selbst).</summary>
    internal static void ReadLifeBoxes()
    {
        var l = Life;
        if (_ui == null || l == null) return;
        var list = new List<string>();
        for (int i = 1; i <= 7; i++) { string b = _ui.GetVariable("/:ed" + i); list.Add(string.IsNullOrEmpty(b) ? "empty" : b); }
        l.boxes = list;
    }

    internal static void MarkLifeEdited()
    {
        _lifeDirty = true;
        _ui?.SetValue("/:HasEditedMyLife", true);
    }

    internal static bool OnMyLifeCommand(string c)
    {
        var l = Life;
        switch (c)
        {
            case "mylf:self": return true;
            case "mylf:wait": PushMyLifeVars(); _ui!.SetValue("/:myLifeVarLoaded", true); return true;   // Laden: Werte setzen, dann darf "open" sie lesen
            case "mylf:open": PushMyLifeVars(); return true;
            case "mylf:bxok": return true;   // Avatar-Foto im Namenskasten: kein Foto (Avatar fehlt)
            case "mylf:clos": EndLifeModes(); SaveLifeIfDirty(); return true;
            case "mylf:sgcx": return true;
            case "mylf:edit:edin": _lifeSnap = null; return true;
            case "mylf:edit:edou": SaveLifeIfDirty(); return true;
            case "mylf:edit:scin": if (_lifeSnap == null && l != null) _lifeSnap = System.Text.Json.JsonSerializer.Serialize(l, LifeJson); return true;
            case "mylf:edit:scne": LifeScroll(+1); return true;
            case "mylf:edit:scpr": LifeScroll(-1); return true;
            case "mylf:edit:edva": _lifeSnap = null; MarkLifeEdited(); return true;
            case "mylf:edit:edca": RestoreLifeSnapshot(); return true;
            case "mylf:edit:gend":
                if (l != null) { l.gender = _ui!.GetVariable("/:EdPlayerGender") ?? ""; MarkLifeEdited(); }
                return true;
            case "mylf:edit:mdva":
            case "mylf:edit:mood":
                if (l != null) { l.mood = _ui!.GetVariable("/:WhatMood") ?? "neutral"; MarkLifeEdited(); }
                return true;
            case "mylf:edit:txin": return true;   // die Eingabe beginnt mit "tfoc"
            case "mylf:edit:txca": _textVar = null; return true;
            case "mylf:edit:txck":   // Text prüfen (im Spiel Server-Filter): immer in Ordnung
                _ui!.SetValue("/:TextAbuse", false);
                _ui.SetValue("/:TimeOut", false);
                _ui.SetValue("/:mylfTextCorrect", true);
                KickLifeLoader();
                return true;
            case "mylf:edit:txva":
                if (l != null)
                {
                    string t = _ui!.GetVariable("/:VK_Value");
                    if (string.IsNullOrEmpty(t)) t = _ui.GetVariable("/:MoodTxtInput") ?? "";
                    if (t.Length > 50) t = t.Substring(0, 50);
                    l.moodText = t;
                    _ui.SetVariable("/:MoodTxt", t);
                    _ui.SetVariable("/:MoodTxtInput", t);
                    MarkLifeEdited();
                }
                return true;
            case "mylf:edit:nlis":
            case "mylf:edit:cbou":
            case "mylf:edit:valid":
                ReadLifeBoxes(); MarkLifeEdited(); return true;
            case "mylf:edit:numb": case "mylf:edit:nlist": case "mylf:edit:cbin": case "mylf:edit:cancel": return true;
            case "mylf:edit:upld":   // Verlassen nach Änderungen: speichern, dann darf der Film schließen
                _lifeDirty = true; SaveLifeIfDirty();
                _ui!.SetValue("/:MyLifeUploaded", true);
                KickLifeLoader();
                return true;
            case "mylf:edit:phop":
            case "mylf:edit:phne":
            case "mylf:edit:phpr":
            case "mylf:edit:phva":
            case "mylf:edit:phca":
            case "mylf:edit:phot":
                return true;   // Fotoalbum-Kästen – ohne Fotoalbum nichts zu tun
            case "mylf:phot:open":
                // Unity: Avatar mit der Pose der Szene M_MYLIFE vor die Kamera, Sucher /mylf/photo_taker. Der Avatar fehlt in der
                // Engine noch -> Sucher wie bei "Abbrechen" wieder schließen.
                Log.Warn("MyTDULife: Avatar-Foto braucht den Avatar (in der Engine noch nicht vorhanden)");
                _ui!.GotoLabel("/mylf/photo_taker", "close", true);
                _ui.SetValue("/:LockMyLifeEdition", false);
                _ui.SetValue("/:Input", true);
                return true;
            case "mylf:phot:lock": case "mylf:phot:vali": case "mylf:phot:canc": return true;
            case "tfoc": BeginTextInput(); return true;
            case "vtke": return true;
        }
        return c.StartsWith("mylf:");   // mylf:scrl:* (Bildlaufleiste) u.a.
    }

    internal static void LifeScroll(int dir)
    {
        var l = Life;
        if (l == null || _ui == null) return;
        _lifeSnap ??= System.Text.Json.JsonSerializer.Serialize(l, LifeJson);
        string id = _ui.GetVariable("/:ListId") ?? "";
        switch (id)
        {
            case "day": l.birthDay = l.birthDay <= 0 ? 1 : (l.birthDay - 1 + dir + 31) % 31 + 1; PushBirth(l); return;
            case "month": l.birthMonth = l.birthMonth <= 0 ? 1 : (l.birthMonth - 1 + dir + 12) % 12 + 1; PushBirth(l); return;
            case "year":
                int n = BirthYearMax - BirthYearMin + 1;
                l.birthYear = l.birthYear <= 0 ? 1990 : (l.birthYear - BirthYearMin + dir + n) % n + BirthYearMin;
                PushBirth(l); return;
        }
        foreach (var li in LifeLists)
        {
            if (li.Id != id) continue;
            int cur = LifeField(l, li.Id), cnt = li.Hc.Length;
            int next = cur < 0 ? (dir > 0 ? 0 : cnt - 1) : (cur + dir + cnt) % cnt;
            SetLifeField(l, li.Id, next);
            _ui.SetVariable("/:" + li.Var, LifeText(li, next));
            return;
        }
    }

    internal static void RestoreLifeSnapshot()
    {
        var sv = TDU2Session.Save;
        if (_lifeSnap != null && sv != null)
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<MyLifeData>(_lifeSnap, LifeJson);
            if (old != null)
            {
                sv.myLife = old;
                var l = Life!;
                foreach (var li in LifeLists) _ui!.SetVariable("/:" + li.Var, LifeText(li, LifeField(l, li.Id)));
                PushBirth(l);
            }
        }
        _lifeSnap = null;
    }

    // ------------------------------------------------------------------ Texteingabe (tfoc)
    internal static string? _textVar; internal static int _textMax = 50; internal static float _textAt;

    internal static void BeginTextInput()
    {
        string v = _ui!.GetVariable("/:TextToFocus");
        if (string.IsNullOrEmpty(v)) return;
        if (!v.StartsWith("/:") && !v.StartsWith("_root.")) v = "/:" + v.TrimStart('/', ':');
        if (v.StartsWith("_root.")) v = "/:" + v.Substring(6);
        _textVar = v;
        _textMax = ReadIntVar("/:VK_MaxLength", ReadIntVar("/:MaxLenghtInput", 50));
        if (_textMax <= 0 || _textMax > 200) _textMax = 50;
        _textAt = _time;
    }

    internal static int ReadIntVar(string v, int def)
    {
        string s = _ui!.GetVariable(v);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : def;
    }

    internal static bool TextInputVisible()
    {
        // Tastatur /mylf/vk: Bild 0 = zu; die Öffnen-Animation (Bild 1..33) hat kein eigenes Label
        var vk = _ui!.Find("/mylf/vk")?.Sprite;
        if (vk != null && _ui.IsShown(vk) && _ui.Frame("/mylf/vk") > 1 && _ui.Label("/mylf/vk") != "close") return true;
        string pl = _ui.Label("/mylf/edbox/edit_popup") ?? "";
        return pl.StartsWith("open_txt");
    }

    /// <summary>Tastatur in das fokussierte Textfeld; true = Eingabe verbraucht.</summary>
    internal static bool UpdateTextInput()
    {
        if (_textVar == null) return false;
        if (_time - _textAt > 0.5f && !TextInputVisible()) { _textVar = null; return false; }
        string cur = _ui!.GetVariable(_textVar) ?? "";
        bool changed = false;
        foreach (char ch in FlashInput.inputString)
        {
            if (ch == '\b') { if (cur.Length > 0) { cur = cur.Substring(0, cur.Length - 1); changed = true; } }
            else if (ch == '\n' || ch == '\r') { }
            else if (!char.IsControl(ch) && cur.Length < _textMax) { cur += ch; changed = true; }
        }
        if (changed) _ui.SetVariable(_textVar, cur);
        if (TDU2Controls.KeyPressed(0x0D)) { _textVar = null; _ui.Input("A"); }
        return true;
    }

    // Ladeanzeige /mylf/cmon_loader: schließt sie nicht selbst, hier nachhelfen – sonst hängt "Annehmen"/Verlassen
    internal static float _loaderKick1 = -1f, _loaderKick2 = -1f;
    internal static void KickLifeLoader() { _loaderKick1 = _time + 0.8f; _loaderKick2 = _time + 2.2f; }

    internal static void UpdateLifeLoader()
    {
        const string L = "/mylf/cmon_loader";
        if (_loaderKick1 > 0f && _time >= _loaderKick1)
        {
            _loaderKick1 = -1f;
            if (_ui!.GetVariable(L + ":isLoading") == "true") { Log.Debug("MyTDULife: Ladeanzeige geschlossen"); _ui.CallClip(L, "closeLoading"); }
        }
        if (_loaderKick2 > 0f && _time >= _loaderKick2)
        {
            _loaderKick2 = -1f;
            if (_ui!.GetVariable(L + ":isLoading") == "true") { Log.Debug("MyTDULife: Ladeanzeige beendet"); _ui.CallClip(L, "endLoading"); _ui.SetValue("/:Input", true); }
        }
    }

    /// <summary>Tagebuch: Texteingabe vor allem anderen; true = Eingabe verbraucht.</summary>
    internal static bool UpdateMyLife()
    {
        if (_ui == null) return false;
        UpdateLifeLoader();
        return UpdateTextInput();
    }

    internal static void EndLifeModes() { _textVar = null; }

    // ================================================================== My House (Unity TDU2MyHouse)
    internal sealed class FurnItem { public int Id; public string Bank = ""; public int Type; public int Price; public int Level; public int NameHc; public string? Path; }
    internal sealed class MatItem { public int Id; public string Name = ""; public int Type; public int Price; public int NameHc; public string? Path; }
    internal static List<FurnItem>? _furnCat;
    internal static List<MatItem>? _matCat;
    internal static Task<(List<FurnItem>, List<MatItem>)>? _catTask;

    internal static void EnsureHouseCatalog()
    {
        if (_furnCat != null || _catTask != null || string.IsNullOrEmpty(BnkDir)) return;
        string? db = CarDatabase.FindDatabase(BnkDir!);
        string dir = Path.Combine(InteriorDir, "myhouse");
        if (db == null) { _furnCat = new(); _matCat = new(); return; }
        _catTask = Task.Run(() =>
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(GameFiles.ReadAllBytes(db)));
            var fl = new List<FurnItem>(); var ml = new List<MatItem>();
            var f = root.Get("DBFurniture");
            if (f != null)
                foreach (var r in f.Items)
                {
                    if (!r.B("_isvalid", true) || r.B("_iscasino") || r.B("_isdlc")) continue;
                    string bank = (r.S("_bank", "") ?? "").ToLowerInvariant();
                    if (bank.Length == 0) continue;
                    string? p = CarDatabase.FindFile(dir, bank + ".bnk");
                    if (p == null) continue;
                    fl.Add(new FurnItem { Id = r.I("_hashcode"), Bank = bank, Type = r.I("_type"), Price = r.I("_price"), Level = r.I("_level"), NameHc = (int)r.U("_name_hc"), Path = p });
                }
            var m = root.Get("DBMaterial");
            var seen = new HashSet<string>();
            if (m != null)
                foreach (var r in m.Items)
                {
                    string n = (r.S("_materialname", "") ?? "").ToLowerInvariant();
                    if (n.Length == 0 || !seen.Add(n)) continue;
                    string? p = CarDatabase.FindFile(dir, n + ".bnk");
                    if (p == null) continue;
                    ml.Add(new MatItem { Id = r.I("_hashcode"), Name = n, Type = r.I("_type"), Price = r.I("_price"), NameHc = (int)r.U("_name_hc"), Path = p });
                }
            ml.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return (fl, ml);
        });
    }

    internal static bool HouseCatalogReady
    {
        get
        {
            if (_furnCat != null) return true;
            if (_catTask == null || !_catTask.IsCompleted) return false;
            if (_catTask.IsFaulted) { Log.Error("My House: Katalog: " + _catTask.Exception?.GetBaseException().Message); _furnCat = new(); _matCat = new(); }
            else { _furnCat = _catTask.Result.Item1; _matCat = _catTask.Result.Item2; Log.Info("My House: " + _furnCat.Count + " Möbel, " + _matCat.Count + " Böden/Tapeten"); }
            _catTask = null;
            return true;
        }
    }

    internal static string ItemName(int hc, string fallback)
    {
        string t = hc != 0 ? Lang(hc, "") : "";
        return string.IsNullOrEmpty(t) ? fallback : t;
    }

    /// <summary>Modell-Gruppe einer Bank: nur die Serien-Möbel (T1…T8, S1…S6) sind Farbvarianten eines Modells (letzter Buchstabe = Farbe).</summary>
    internal static string ShapeOf(string bank)
    {
        if (string.IsNullOrEmpty(bank) || bank.Length < 4) return bank ?? "";
        string b = bank.ToLowerInvariant();
        bool series = (b[0] == 't' || b[0] == 's') && char.IsDigit(b[1]);
        return series ? b.Substring(0, b.Length - 1) : b;
    }

    // ------------------------------------------------------------------ Einrichtung (gespeichert)
    internal static HouseLayout? Layout(bool create)
    {
        if (_spot == null) return null;
        string key = _spot.Code ?? "";
        var sv = TDU2Session.Save;
        if (sv == null) return null;
        sv.houses ??= new List<HouseLayout>();
        foreach (var h in sv.houses) if (h != null && string.Equals(h.spot, key, StringComparison.OrdinalIgnoreCase)) return h;
        if (!create) return null;
        var nh = new HouseLayout { spot = key };
        sv.houses.Add(nh);
        return nh;
    }

    internal static string? Lookup(List<HouseEntry>? l, string k)
    {
        if (l == null) return null;
        foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.OrdinalIgnoreCase)) return e.v;
        return null;
    }

    internal static void Store(List<HouseEntry> l, string k, string v)
    {
        foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.OrdinalIgnoreCase)) { e.v = v; return; }
        l.Add(new HouseEntry { k = k, v = v });
    }

    /// <summary>Bank eines Möbelstücks (Einrichtung, sonst Standard der Szene).</summary>
    internal static string HouseObjectBank(InteriorScene.Ent e, string defaultBank)
    {
        var l = Layout(false);
        string? b = l != null ? Lookup(l.furn, e.Name ?? "") : null;
        return string.IsNullOrEmpty(b) ? defaultBank : b;
    }

    // ------------------------------------------------------------------ Möbel der Szene
    internal sealed class HouseObj
    {
        public InteriorScene.Ent Ent = null!; public int Type, Group;
        public string DefaultBank = "", Bank = "";
        public MapStreamer.SceneHandle? Handle; public StaticCollider? Col;
        public Task<BankModel>? Loading; public string? LoadingBank;
    }
    internal static readonly List<HouseObj> _houseObjs = new();
    internal static readonly Dictionary<InteriorScene.Ent, string> _houseBankOf = new();

    internal static void RegisterHouseObject(InteriorScene.Ent e, MapStreamer.SceneHandle h, StaticCollider? col)
    {
        string def = Regex.Replace(e.Name ?? "", @"_\d+$", "").ToLowerInvariant();
        _houseObjs.Add(new HouseObj
        {
            Ent = e,
            Type = e.Node != null ? e.Node.I("Type") : 0,
            Group = e.Node != null ? e.Node.I("Group") : 0,
            DefaultBank = def,
            Bank = _houseBankOf.TryGetValue(e, out var b) ? b : def,
            Handle = h,
            Col = col,
        });
    }

    internal static void ClearHouse()
    {
        foreach (var o in _houseObjs) o.Loading = null;
        _houseObjs.Clear();
        _houseBankOf.Clear();
        var st = _t.Streamer;
        foreach (var d in _matDonors.Values) if (d != null) st?.DestroyScene(d.Handle);
        _matDonors.Clear(); _donorTasks.Clear(); _zoneWanted.Clear();
        _zones = null; _zoneParts = null;
        _myHouse = HouseMode.None; _edit = null; _editZone = null; _housePreview = false; _houseRestoreAt = -1f;
    }

    /// <summary>Möbelstück (und seine Gruppe) gegen eine andere Bank tauschen (Vorschau oder endgültig).</summary>
    internal static void SetFurnitureBank(HouseObj target, string bank)
    {
        var st = _t.Streamer;
        if (st == null) return;
        foreach (var o in _houseObjs)
        {
            bool same = o == target || (target.Group != 0 && o.Group == target.Group && o.Type == target.Type);
            if (!same || string.Equals(o.LoadingBank ?? o.Bank, bank, StringComparison.OrdinalIgnoreCase)) continue;
            string? path = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), bank + ".bnk");
            if (path == null) { Log.Error("My House: Bank fehlt: " + bank); continue; }
            o.LoadingBank = bank;
            o.Loading = Task.Run(() => st.LoadSceneModel(path));
        }
    }

    internal static bool AnyFurnitureLoading { get { foreach (var o in _houseObjs) if (o.Loading != null) return true; return false; } }

    internal static void PollHouseObjects()
    {
        var st = _t.Streamer; var phys = Phys;
        foreach (var o in _houseObjs)
        {
            if (o.Loading == null || !o.Loading.IsCompleted) continue;
            var t = o.Loading; o.Loading = null;
            if (t.IsFaulted || t.Result == null) { Log.Warn("My House: " + o.LoadingBank + ": " + t.Exception?.GetBaseException().Message); o.LoadingBank = null; continue; }
            if (st == null || _scene == null) { t.Result.Release(); continue; }
            if (o.Handle != null) { _furniture.Remove(o.Handle); st.DestroyScene(o.Handle); }
            if (o.Col != null) { phys?.Remove(o.Col); _colliders.Remove(o.Col); _own.Remove(o.Col); o.Col = null; }
            o.Handle = st.BuildScene(t.Result, o.Ent.M * _holder, false, glassOnly: true);
            st.Scenes.Add(o.Handle);
            _furniture.Add(o.Handle);
            o.Col = PrepareStatic(o.Handle, false);
            o.Bank = o.LoadingBank ?? o.Bank; o.LoadingBank = null;
            RenderFx.ProbeVersion++;
        }
    }

    // ------------------------------------------------------------------ Böden / Wände
    internal sealed class Zone { public string Key = ""; public int Type; public string Default = ""; public readonly List<IGpuMaterial> Mats = new(); }
    internal static Dictionary<string, Zone>? _zones;
    internal static readonly Regex ZoneRx = new(@"^MAT_(Floor|Wall)(\d+)_(\d+)", RegexOptions.IgnoreCase);

    internal static Dictionary<string, Zone>? Zones
    {
        get
        {
            if (_zones != null || _scene == null) return _zones;
            _zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in _scene.Objects)
                foreach (var lod in o.Lods)
                    foreach (var pt in lod.Parts)
                    {
                        if (!MaterialFactory.TryGetInfo(pt.Material, out var mi)) continue;
                        var mt = ZoneRx.Match(mi.Def.Name ?? "");
                        if (!mt.Success) continue;
                        bool floor = mt.Groups[1].Value.Equals("Floor", StringComparison.OrdinalIgnoreCase);
                        string key = (floor ? "Floor" : "Wall") + mt.Groups[2].Value;
                        if (!_zones.TryGetValue(key, out var z))
                            _zones[key] = z = new Zone { Key = key, Type = floor ? 1 : 2, Default = (floor ? "ground_" : "wall_") + int.Parse(mt.Groups[3].Value).ToString("00") };
                        if (!z.Mats.Contains(pt.Material)) z.Mats.Add(pt.Material);
                    }
            return _zones;
        }
    }

    internal static string ZoneMaterial(Zone z)
    {
        var l = Layout(false);
        string? v = l != null ? Lookup(l.mats, z.Key) : null;
        return string.IsNullOrEmpty(v) ? z.Default : v;
    }

    // Spender-Materialien (interior/myhouse/ground_NN / wall_NN.bnk: Vorschau-Fläche)
    internal sealed class Donor { public MapStreamer.SceneHandle Handle = null!; public MaterialDesc? Desc; public IGpuTexture? Thumb; public Texture? ThumbWrap; }
    internal static readonly Dictionary<string, Donor?> _matDonors = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, Task<BankModel>> _donorTasks = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, string> _zoneWanted = new(StringComparer.OrdinalIgnoreCase);

    internal static Donor? GetDonor(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_matDonors.TryGetValue(name, out var d)) return d;
        if (!_donorTasks.ContainsKey(name))
        {
            string? path = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), name + ".bnk");
            var st = _t.Streamer;
            if (path == null || st == null) { _matDonors[name] = null; return null; }
            _donorTasks[name] = Task.Run(() => st.LoadSceneModel(path));
        }
        return null;
    }

    internal static void PollDonors()
    {
        if (_donorTasks.Count == 0) return;
        var st = _t.Streamer;
        List<string>? done = null;
        foreach (var kv in _donorTasks)
        {
            if (!kv.Value.IsCompleted) continue;
            (done ??= new()).Add(kv.Key);
            if (kv.Value.IsFaulted || kv.Value.Result == null || st == null) { _matDonors[kv.Key] = null; continue; }
            // aufgebaut, aber nicht gezeichnet (nicht in Streamer.Scenes): nur Materialien/Texturen
            var h = st.BuildScene(kv.Value.Result, Matrix4x4.Identity, true);
            MaterialDesc? desc = null;
            foreach (var o in h.Objects) { foreach (var lod in o.Lods) foreach (var pt in lod.Parts) if (desc == null && MaterialFactory.TryGetInfo(pt.Material, out var mi)) desc = mi.Desc; if (desc != null) break; }
            var thumb = desc?.Detail ?? desc?.Main;
            _matDonors[kv.Key] = new Donor { Handle = h, Desc = desc, Thumb = thumb, ThumbWrap = thumb != null ? Texture.Wrap(thumb, thumb.Width, thumb.Height) : null };
        }
        if (done == null) return;
        foreach (var k in done) _donorTasks.Remove(k);
        foreach (var kv in new List<KeyValuePair<string, string>>(_zoneWanted))
            if (_matDonors.ContainsKey(kv.Value)) { _zoneWanted.Remove(kv.Key); ApplyZone(kv.Key, kv.Value); }
        if (_myHouse == HouseMode.Material) RefreshMaterialThumbs();
    }

    /// <summary>Fläche (Zone) mit dem Material eines Spenders belegen (Vorschau oder endgültig): _DetailTex/_GlossTex wie Unity.</summary>
    internal static void ApplyZone(string zoneKey, string matName)
    {
        var zones = Zones;
        if (zones == null || !zones.TryGetValue(zoneKey, out var z)) return;
        var d = GetDonor(matName);
        if (d == null) { if (!_matDonors.ContainsKey(matName)) _zoneWanted[zoneKey] = matName; return; }
        if (d.Desc == null) return;
        var r = _v.Renderer;
        foreach (var m in z.Mats)
        {
            if (!MaterialFactory.TryGetInfo(m, out var mi)) continue;
            var desc = mi.Desc;
            bool copied = false;
            if (d.Desc.Detail != null) { desc.Detail = d.Desc.Detail; copied = true; }
            if (d.Desc.GlossTex != null) { desc.GlossTex = d.Desc.GlossTex; copied = true; }
            if (!copied && d.Thumb != null)
            {
                // Spender ohne Detail-Ebene: Textur dorthin, wo die Fläche ihre Oberfläche hat
                if (desc.Detail != null) desc.Detail = d.Thumb; else desc.Main = d.Thumb;
            }
            r.UpdateMaterial(m, desc);
        }
    }

    /// <summary>Gespeicherte Böden/Wände anwenden (nach dem Aufbau der Szene).</summary>
    internal static void ApplyHouseMaterials()
    {
        var zones = Zones;
        if (zones == null || zones.Count == 0) return;
        var l = Layout(false);
        if (l?.mats == null) return;
        foreach (var e in l.mats) if (e != null && zones.ContainsKey(e.k) && !string.IsNullOrEmpty(e.v)) ApplyZone(e.k, e.v);
    }

    // ------------------------------------------------------------------ Ablauf
    internal enum HouseMode { None, Walk, Furniture, Material }
    internal static HouseMode _myHouse;
    internal static bool _housePreview;
    internal static float _houseModeAt, _houseRestoreAt = -1f;
    internal static HouseObj? _aimObj, _edit;
    internal static string? _aimZone, _editZone;
    internal static List<List<FurnItem>>? _shapes; internal static int _shape, _color; internal static string? _editOrigBank;
    internal static List<MatItem>? _mats; internal static int _matIdx; internal static string? _editOrigMat;

    internal static void StartMyHouse()
    {
        _diaryOpen = false;
        _myHouse = HouseMode.Walk;
        _houseModeAt = _time; _houseRestoreAt = -1f;
        _aimObj = null; _aimZone = null; _edit = null; _editZone = null;
        EnsureHouseCatalog();
        if (_ui != null)
        {
            foreach (var n in new[] { "myhs_editor", "cursor", "help", "help_system", "title_bar", "cmon_loader", "cmon_loading" }) _ui.SetVisible("/" + n, true);
            foreach (var n in new[] { "diary", "mylf", "mylf_book", "club_money" }) _ui.SetVisible("/" + n, false);
            _ui.SetValue("/:Input", true);
            _ui.MouseEnabled = false;
            SetMoneyVars();
        }
        _t.Input.CursorLocked = true;
        Log.Info("My House: Einrichten (Zonen " + (Zones?.Count ?? 0) + ", Möbel " + _houseObjs.Count + ")");
    }

    internal static void EndMyHouse()
    {
        ClearMaterialThumbs();
        if (_myHouse == HouseMode.None) return;
        RevertEdit();
        _myHouse = HouseMode.None; _housePreview = false;
        _houseRestoreAt = _time + 1.2f;   // Film spielt living_in -> danach die Laufmodus-Oberfläche wiederherstellen
        _t.Input.CursorLocked = true;
    }

    internal static void SetMoneyVars()
    {
        var sv = TDU2Session.Save;
        string money = sv != null ? sv.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : " ";
        _ui!.SetVariable("/:CurMoney", money);
        _ui.SetVariable("/:MyMoneyStr", money);
    }

    internal static string PriceText(int price) => price > 0 ? price.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "0 $";

    /// <summary>Jedes Bild im Haus; true = Eingabe verbraucht.</summary>
    internal static bool UpdateMyHouse()
    {
        if (_ui == null) { _myHouse = HouseMode.None; _houseRestoreAt = -1f; return false; }
        if (_houseRestoreAt > 0f && _time >= _houseRestoreAt)
        {
            _houseRestoreAt = -1f;
            if (_myHouse == HouseMode.None && !_diaryOpen)
            {
                // Wohnbereich wie nach dem Tagebuch: Film auf living_stop, Eingabe frei, Laufmodus mit Maus-Umsicht
                _ui.GotoLabel("/", _kind == Kind.Garage ? "garage_stop" : "living_stop", false);
                foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                _ui.GotoLabel("/myhs_editor", "default", false);
                _ui.SetVisible("/cursor/low_bar", true);
                _ui.SetVisible("/title_bar/ic", true);
                _ui.SetValue("/:Input", true);
                _ui.MouseEnabled = false;
                ResetUiAim();
            }
            _t.Input.CursorLocked = true;
        }
        if (_myHouse == HouseMode.None) return false;
        if (_myHouse == HouseMode.Furniture && !AnyFurnitureLoading && _ui.GetVariable("/:furnitureLoaded") != "true") _ui.SetValue("/:furnitureLoaded", true);
        if (_myHouse == HouseMode.Material && _time - _houseModeAt > 0.1f && _ui.GetVariable("/:MaterialLoaded") != "true") _ui.SetValue("/:MaterialLoaded", true);
        bool walk = _myHouse == HouseMode.Walk;
        _ui.SetVisible("/help", false);
        _ui.SetVisible("/cursor/low_bar", walk);
        _ui.SetVisible("/title_bar/ic", walk);
        if (walk) UpdateHouseWalk();
        else
        {
            _ui.MouseEnabled = !_housePreview;
            if (_time - _houseModeAt < 0.25f) return true;
            if (TDU2Controls.Pressed("menu_left")) _ui.Input("Move", "left");
            if (TDU2Controls.Pressed("menu_right")) _ui.Input("Move", "right");
            if (TDU2Controls.Pressed("menu_up")) _ui.Input("Move", "up");
            if (TDU2Controls.Pressed("menu_down")) _ui.Input("Move", "down");
            if (TDU2Controls.ConfirmPressed()) _ui.Input("A");
            if (TDU2Controls.Pressed("menu_opt1") || TDU2Controls.KeyPressed(0x50)) _ui.Input("myhsPreview");   // Vorschau (X bzw. P)
        }
        return true;
    }

    /// <summary>My House verlassen (= Ja im Dialog "My House verlassen?").</summary>
    internal static void QuitMyHouse()
    {
        if (_ui == null) { ForceEndMyHouse(); return; }
        _ui.SetVariable("/:DgbxCase", "myhs_quit");
        _ui.SetVariable("/:PlayerAnswer", "a");
        if (!_ui.CallClip("/", "DialogBox") || _myHouse != HouseMode.None)
        {
            if (_myHouse != HouseMode.None) OnMyHouseCommand("myhs:clos");
            _ui.GotoLabel("/myhs_editor", "default", false);
            _ui.GotoLabel("/", "living_in", true);
        }
    }

    /// <summary>Editor ohne Film-Rückmeldung verlassen: Vorschau verwerfen, zurück in den Laufmodus.</summary>
    internal static void ForceHouseWalk()
    {
        ClearMaterialThumbs();
        RevertEdit();
        _myHouse = HouseMode.Walk; _houseModeAt = _time; _housePreview = false;
        if (_ui != null)
        {
            foreach (var n in new[] { "furniture", "material", "simple_object", "frames" }) _ui.GotoLabel("/myhs_editor/" + n, "default", false);
            _ui.GotoLabel("/myhs_editor", "myhs_wm", false);
            _ui.SetValue("/:Input", true);
            _ui.MouseEnabled = false;
        }
        _t.Input.CursorLocked = true;
    }

    /// <summary>My House ohne Film-Rückmeldung verlassen (Editor verwerfen, Wohnbereich wiederherstellen).</summary>
    internal static void ForceEndMyHouse()
    {
        if (_myHouse == HouseMode.None) return;
        RevertEdit();
        EndMyHouse();
        if (_ui != null) { _ui.GotoLabel("/myhs_editor", "default", false); _ui.SetValue("/:Input", true); }
        _houseRestoreAt = _time;
    }

    internal static void UpdateHouseWalk()
    {
        if (_fp == null || _ui == null) return;
        // Ziel unter dem Fadenkreuz: Möbel oder Boden/Wand (MAT_Floor*/MAT_Wall*), Wände/Säulen davor zählen
        var eye = _fp.Eye; var dir = _fp.Forward;
        float maxD = InteractDistance + 4f, hitD = maxD;
        if (Phys != null) { _aimQuery = true; try { if (Phys.Raycast(eye, dir, maxD, out var hit)) hitD = hit.Distance; } finally { _aimQuery = false; } }
        string? zone = ZoneOnRay(eye, dir, MathF.Min(maxD, hitD + 0.3f));
        // Möbel genau über ihre Dreiecke (Glasplatten haben keine Kollision); unsichtbare Kollisionsvolumen dürfen davor liegen
        float tol = zone == null ? 0.6f : 0.02f;
        HouseObj? obj = HouseObjOnRay(eye, dir, MathF.Min(maxD, hitD + tol));
        if (obj != null) zone = null;
        if (obj != _aimObj || zone != _aimZone)
        {
            _aimObj = obj; _aimZone = zone;
            if (obj != null || zone != null)
            {
                _ui.SetVariable("/:SelItemType", "myhs_item");
                _ui.SetVariable("/:myhsChangeType", obj != null ? "furniture" : "material");
                _ui.SetVariable("/:t_wm_destination", obj != null ? "Möbel ändern" : (Zones![zone!].Type == 1 ? "Boden ändern" : "Tapete ändern"));
                _ui.GotoLabel("/cursor", "on", true);
                _ui.GotoLabel("/cursor/low_bar", "open", true);
            }
            else
            {
                _ui.GotoLabel("/cursor/low_bar", "close", true);
                _ui.GotoLabel("/cursor", "off", true);
            }
        }
        if (_time - _houseModeAt < 0.3f) return;
        bool use = TDU2Controls.Pressed("walk_use") || TDU2Controls.ConfirmPressed() || (_t.Input.CursorLocked && _t.Input.LeftMousePressed);
        if (use && (_aimObj != null || _aimZone != null))
        {
            if (!HouseCatalogReady) { Log.Info("My House: Katalog wird geladen …"); return; }
            _edit = _aimObj; _editZone = _aimObj == null ? _aimZone : null;
            _ui.SetVariable("/:myhsChangeType", _edit != null ? "furniture" : "material");
            _ui.CallClip("/", "myhsEditorOpen");   // Film: myhs:furn:open bzw. myhs:mate:open
        }
    }

    internal static float RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det > -1e-9f && det < 1e-9f) return float.MaxValue;   // beidseitig (Möbel sind nicht immer geschlossen)
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return float.MaxValue;
        var q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(d, q) * inv;
        if (w < 0f || u + w > 1f) return float.MaxValue;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 0f ? t : float.MaxValue;
    }

    /// <summary>Erster Dreiecks-Treffer eines Objekts (Spielraum), Hülle als Vorfilter.</summary>
    internal static float RayObject(RenderObject ro, Vector3 eye, Vector3 dir, float maxDist, Func<RenderPart, bool>? partFilter = null)
    {
        if (ro.Lods.Length == 0 || !RayBox(eye, dir, ro.BoundsMin - new Vector3(0.05f), ro.BoundsMax + new Vector3(0.05f), out float bd) || bd > maxDist) return float.MaxValue;
        float best = float.MaxValue;
        foreach (var pt in ro.Lods[0].Parts)
        {
            var c = pt.Collision;
            if (c == null || (partFilter != null && !partFilter(pt))) continue;
            for (int i = 0; i + 2 < c.Length; i += 3)
            {
                float t = RayTri(eye, dir, Vector3.Transform(c[i], ro.Model), Vector3.Transform(c[i + 1], ro.Model), Vector3.Transform(c[i + 2], ro.Model));
                if (t < best) best = t;
            }
        }
        return best <= maxDist ? best : float.MaxValue;
    }

    internal static HouseObj? HouseObjOnRay(Vector3 eye, Vector3 dir, float maxDist)
    {
        HouseObj? best = null; float bestD = maxDist;
        foreach (var o in _houseObjs)
        {
            if (o.Handle == null) continue;
            foreach (var ro in o.Handle.Objects)
            {
                float d = RayObject(ro, eye, dir, bestD);
                if (d < bestD) { bestD = d; best = o; }
            }
        }
        return best;
    }

    internal static List<(RenderObject ro, RenderPart pt, string zone)>? _zoneParts;

    internal static string? ZoneOnRay(Vector3 eye, Vector3 dir, float maxDist)
    {
        var zones = Zones;
        if (zones == null || zones.Count == 0 || _scene == null) return null;
        if (_zoneParts == null)
        {
            _zoneParts = new();
            foreach (var ro in _scene.Objects)
            {
                if (ro.Lods.Length == 0) continue;
                foreach (var pt in ro.Lods[0].Parts)
                    foreach (var z in zones.Values)
                        if (z.Mats.Contains(pt.Material)) { _zoneParts.Add((ro, pt, z.Key)); break; }
            }
        }
        string? best = null; float bestD = maxDist;
        foreach (var (ro, pt, zone) in _zoneParts)
        {
            float d = RayObject(ro, eye, dir, bestD, p => p == pt);
            if (d < bestD) { bestD = d; best = zone; }
        }
        return best;
    }

    // ------------------------------------------------------------------ Befehle des Films (myhs:*)
    internal static bool OnMyHouseCommand(string c)
    {
        switch (c)
        {
            case "myhs:open": StartMyHouse(); return true;
            case "myhs:clos": EndMyHouse(); return true;
            case "myhs:prin": _housePreview = true; _t.Input.CursorLocked = true; return true;    // Vorschau: frei umsehen
            case "myhs:prou": _housePreview = false; _t.Input.CursorLocked = false; return true;
            case "myhs:bdlc": return true;
            case "myhs:furn:open": FurnitureOpen(); return true;
            case "myhs:furn:chsh": FurnitureShape(); return true;
            case "myhs:furn:chco": FurnitureColor(); return true;
            case "myhs:furn:scco": return true;
            case "myhs:furn:puok": Purchase(); return true;
            case "myhs:furn:clos": RevertEdit(); _myHouse = HouseMode.Walk; _houseModeAt = _time; _t.Input.CursorLocked = true; _ui?.SetValue("/:Input", true); return true;
            case "myhs:mate:open": MaterialOpen(); return true;
            case "myhs:mate:chma": MaterialPreview(); return true;
            case "myhs:mate:scma": return true;
            case "myhs:mate:puok": Purchase(); return true;
            case "myhs:mate:clos": ClearMaterialThumbs(); RevertEdit(); _myHouse = HouseMode.Walk; _houseModeAt = _time; _t.Input.CursorLocked = true; _ui?.SetValue("/:Input", true); return true;
            case "myhs:sobj:open":
            case "myhs:fram:open":
                Log.Info("My House: dieses Objekt kann nicht geändert werden");
                _ui?.CallClip("/myhs_editor", "BackToWM");
                return true;
            case "wmon": case "wmof": case "camf": case "camn": case "dlci": return _myHouse != HouseMode.None;
        }
        if (c.StartsWith("myhs:mate:dima:")) { RefreshMaterialThumbs(); return true; }
        if (c.StartsWith("myhs:furn:dico:")) return true;
        return false;
    }

    internal static void FurnitureOpen()
    {
        if (_edit == null || _furnCat == null) { _ui?.CallClip("/myhs_editor", "BackToWM"); return; }
        _myHouse = HouseMode.Furniture; _houseModeAt = _time; _housePreview = false;
        _t.Input.CursorLocked = false;
        _editOrigBank = _edit.Bank;
        int dbType = _edit.Type + 1;   // DBFurniture._type = Entity.Type + 1
        var byShape = new Dictionary<string, List<FurnItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var f in _furnCat)
        {
            if (f.Type != dbType) continue;
            string s = ShapeOf(f.Bank);
            if (!byShape.TryGetValue(s, out var l)) { byShape[s] = l = new(); order.Add(s); }
            l.Add(f);
        }
        string curShape = ShapeOf(_edit.Bank);
        if (!byShape.ContainsKey(curShape))
        {
            byShape[curShape] = new List<FurnItem> { new FurnItem { Id = 0, Bank = _edit.Bank, Type = dbType, Path = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), _edit.Bank + ".bnk") } };
            order.Insert(0, curShape);
        }
        _shapes = new();
        foreach (var s in order) { var l = byShape[s]; l.Sort((a, b) => string.CompareOrdinal(a.Bank, b.Bank)); _shapes.Add(l); }
        _shape = Math.Max(0, order.IndexOf(curShape));
        _color = Math.Max(0, _shapes[_shape].FindIndex(f => string.Equals(f.Bank, _edit.Bank, StringComparison.OrdinalIgnoreCase)));
        _ui!.SetValue("/:nb_o", Math.Max(0, _shapes.Count - 1));
        _ui.SetValue("/:num_o", _shape);
        _ui.SetValue("/:view_sq", 4);
        _ui.SetValue("/:furnitureLocked", false);
        _ui.SetValue("/:DLC_Locked", false);
        _ui.SetValue("/:IsDLC", false);
        _ui.MouseEnabled = true;
        UpdateFurnitureVars(false);
    }

    internal static FurnItem? CurFurn => _shapes != null && _shape >= 0 && _shape < _shapes.Count && _color >= 0 && _color < _shapes[_shape].Count ? _shapes[_shape][_color] : null;

    internal static bool Owns(FurnItem? f) => f != null && (f.Id == 0 || string.Equals(f.Bank, _editOrigBank, StringComparison.OrdinalIgnoreCase)
                                           || (TDU2Session.Save?.ownedFurniture?.Contains(f.Id) ?? false));

    internal static void UpdateFurnitureVars(bool preview)
    {
        var f = CurFurn;
        if (f == null || _ui == null || _edit == null) return;
        var sv = TDU2Session.Save;
        bool owned = Owns(f);
        _ui.SetValue("/:nb_colors", Math.Max(0, _shapes![_shape].Count - 1));
        _ui.SetValue("/:color_id", _color);
        _ui.SetVariable("/:name_o", ItemName(f.NameHc, f.Bank.ToUpperInvariant()));
        _ui.SetVariable("/:CurPrice", owned ? " " : PriceText(f.Price));
        _ui.SetValue("/:IsItemBought", owned);
        _ui.SetValue("/:IsCurItem", string.Equals(f.Bank, _editOrigBank, StringComparison.OrdinalIgnoreCase));
        _ui.SetValue("/:EnoughMoney", owned || sv == null || sv.money >= f.Price);
        SetMoneyVars();
        if (preview) { _ui.SetValue("/:furnitureLoaded", false); SetFurnitureBank(_edit, f.Bank); }
        else if (!AnyFurnitureLoading) _ui.SetValue("/:furnitureLoaded", true);
    }

    internal static void FurnitureShape()
    {
        if (_shapes == null) return;
        int n = Math.Clamp(ReadIntVar("/:num_o", _shape), 0, _shapes.Count - 1);
        if (n != _shape) { _shape = n; _color = 0; }
        UpdateFurnitureVars(true);
    }

    internal static void FurnitureColor()
    {
        if (_shapes == null) return;
        int c = Math.Clamp(ReadIntVar("/:color_id", _color), 0, _shapes[_shape].Count - 1);
        if (c == _color) return;
        _color = c;
        UpdateFurnitureVars(true);
    }

    internal static void MaterialOpen()
    {
        var zones = Zones;
        if (_editZone == null || zones == null || _matCat == null || !zones.TryGetValue(_editZone, out var z)) { _ui?.CallClip("/myhs_editor", "BackToWM"); return; }
        _myHouse = HouseMode.Material; _houseModeAt = _time; _housePreview = false;
        _t.Input.CursorLocked = false;
        _editOrigMat = ZoneMaterial(z);
        _mats = new();
        foreach (var m in _matCat) if (m.Type == z.Type) _mats.Add(m);
        if (!_mats.Exists(m => string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)))
            _mats.Insert(0, new MatItem { Id = 0, Name = _editOrigMat, Type = z.Type });
        _matIdx = Math.Max(0, _mats.FindIndex(m => string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)));
        _ui!.SetValue("/:nb_m", Math.Max(0, _mats.Count - 1));
        _ui.SetValue("/:id_m", _matIdx);
        _ui.SetValue("/:view_sq", 4);
        _ui.SetValue("/:DLC_Locked", false);
        _ui.MouseEnabled = true;
        UpdateMaterialVars();
        foreach (var m in _mats) GetDonor(m.Name);   // Vorschaubilder laden
        RefreshMaterialThumbs();
        _ui.SetValue("/:MaterialLoaded", true);
    }

    internal static bool OwnsMat(MatItem? m) => m != null && (m.Id == 0 || string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)
                                              || (TDU2Session.Save?.ownedMaterials?.Contains(m.Id) ?? false));

    internal static void UpdateMaterialVars()
    {
        if (_mats == null || _ui == null || _matIdx < 0 || _matIdx >= _mats.Count) return;
        var m = _mats[_matIdx];
        var sv = TDU2Session.Save;
        bool owned = OwnsMat(m);
        string name = ItemName(m.NameHc, m.Name.Replace('_', ' ').ToUpperInvariant());
        string price = owned ? " " : PriceText(m.Price);
        _ui.SetVariable("/:material_name", name); _ui.SetVariable("/myhs_editor/material:material_name", name);
        _ui.SetVariable("/:material_price", price); _ui.SetVariable("/myhs_editor/material:material_price", price);
        _ui.SetVariable("/:CurPrice", price);
        _ui.SetValue("/:IsItemBought", owned);
        _ui.SetValue("/:IsCurItem", string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase));
        _ui.SetValue("/:EnoughMoney", owned || sv == null || sv.money >= m.Price);
        SetMoneyVars();
    }

    internal static void MaterialPreview()
    {
        if (_mats == null || _editZone == null) return;
        _matIdx = Math.Clamp(ReadIntVar("/:id_m", _matIdx), 0, _mats.Count - 1);
        UpdateMaterialVars();
        ApplyZone(_editZone, _mats[_matIdx].Name);
    }

    /// <summary>
    /// Vorschaubilder der Felder wieder entfernen. Der Film blendet die Felder im Label "default" über die Alpha der Clips
    /// sqN/lure aus (Alpha 0); ein Clip mit Spieltextur übergeht aber seine eigene Alpha (Platzhalter in shopscar), darum blieben
    /// die Kacheln nach dem Schließen stehen. Abweichung vom Unity-Code (dort fehlt das Entfernen), damit das Ergebnis dem Spiel entspricht.
    /// </summary>
    internal static void ClearMaterialThumbs()
    {
        if (_ui == null) return;
        for (int i = 0; i < 12; i++) _ui.SetClipTexture("/myhs_editor/material/sq" + i + "/lure", null);
    }

    /// <summary>Vorschaubilder der sichtbaren Felder (sqN/lure, Feld-Variable id = Index in der Liste).</summary>
    internal static void RefreshMaterialThumbs()
    {
        if (_ui == null || _mats == null) return;
        for (int i = 0; i < 12; i++)
        {
            string sq = "/myhs_editor/material/sq" + i;
            if (_ui.Find(sq) == null) continue;
            int id = ReadIntVar(sq + ":id", -1);
            if (id < 0 || id >= _mats.Count) continue;
            var d = GetDonor(_mats[id].Name);
            if (d?.ThumbWrap != null) _ui.SetClipTexture(sq + "/lure", d.ThumbWrap);
        }
    }

    internal static void Purchase()
    {
        var sv = TDU2Session.Save;
        if (_myHouse == HouseMode.Furniture && _edit != null)
        {
            var f = CurFurn;
            if (f != null)
            {
                if (!Owns(f) && sv != null)
                {
                    if (sv.money < f.Price) { Log.Info("My House: nicht genug Geld"); RevertEdit(); _myHouse = HouseMode.Walk; return; }
                    sv.money -= f.Price;
                    sv.ownedFurniture ??= new List<int>();
                    if (f.Id != 0 && !sv.ownedFurniture.Contains(f.Id)) sv.ownedFurniture.Add(f.Id);
                }
                var l = Layout(true);
                if (l != null)
                {
                    l.furn ??= new List<HouseEntry>();
                    foreach (var o in _houseObjs)
                        if (o == _edit || (_edit.Group != 0 && o.Group == _edit.Group && o.Type == _edit.Type))
                            Store(l.furn, o.Ent.Name ?? "", f.Bank);
                }
                SetFurnitureBank(_edit, f.Bank);
                Log.Info("My House: " + _edit.Ent.Name + " -> " + f.Bank + " (" + f.Price + " $)");
            }
        }
        else if (_myHouse == HouseMode.Material && _editZone != null && _mats != null)
        {
            var m = _mats[_matIdx];
            if (!OwnsMat(m) && sv != null)
            {
                if (sv.money < m.Price) { Log.Info("My House: nicht genug Geld"); RevertEdit(); _myHouse = HouseMode.Walk; return; }
                sv.money -= m.Price;
                sv.ownedMaterials ??= new List<int>();
                if (m.Id != 0 && !sv.ownedMaterials.Contains(m.Id)) sv.ownedMaterials.Add(m.Id);
            }
            var l = Layout(true);
            if (l != null) { l.mats ??= new List<HouseEntry>(); Store(l.mats, _editZone, m.Name); }
            ApplyZone(_editZone, m.Name);
            Log.Info("My House: " + _editZone + " -> " + m.Name + " (" + m.Price + " $)");
        }
        _editOrigBank = null; _editOrigMat = null;
        _edit = null; _editZone = null;
        _myHouse = HouseMode.Walk; _houseModeAt = _time;
        _t.Input.CursorLocked = true;
        if (_ui != null) { SetMoneyVars(); _ui.SetValue("/:Input", true); _ui.MouseEnabled = false; }
        if (sv != null) _ = TDU2Session.SaveAsync();
    }

    /// <summary>Vorschau verwerfen: gespeicherten Stand wiederherstellen.</summary>
    internal static void RevertEdit()
    {
        if (_edit != null && !string.IsNullOrEmpty(_editOrigBank)) SetFurnitureBank(_edit, _editOrigBank);
        if (_editZone != null && !string.IsNullOrEmpty(_editOrigMat)) ApplyZone(_editZone, _editOrigMat);
        _edit = null; _editZone = null; _editOrigBank = null; _editOrigMat = null;
        _shapes = null; _mats = null;
    }

    // ================================================================== TDU Media (Unity TDU2TDUMedia, tdmedia.bnk)
    internal static bool _mediaOpen;
    internal static VmfPlayer? _media;
    internal static float _mediaOpenedAt, _mediaNextWeather, _mediaForceCloseAt = -1f;
    internal static VmfPlayer.FlashList? _newsList;
    internal static int _shownNews = -1, _clockShown = -1;
    internal static bool _german, _mediaHooked;
    internal static string _newsTab = "game_progress";
    internal sealed class News { public string Title = "", Text = "", Version = ""; public DateTime Date; }
    internal static readonly List<News> _news = new();
    internal const string DetailPath = "/lists/details/content";

    internal static void OpenMedia()
    {
        _media = _ctx?.Open(ScreenDefs.TdMedia);
        if (_media == null) { Log.Warn("TDU Media: tdmedia.bnk nicht gefunden"); return; }
        if (!_mediaHooked)
        {
            _media.FSCommand += OnMediaCommand;
            _media.ListCreated += OnMediaListCreated;
            _media.ListValidated += l => { if (l == _newsList) ShowNews(l.Selected); };
            _mediaHooked = true;
        }
        _german = (FlashHost.Language ?? "").StartsWith("ge", StringComparison.OrdinalIgnoreCase);
        _mediaOpen = true; _mediaOpenedAt = _time; _mediaForceCloseAt = -1f; _clockShown = -1;
        UpdateUiAim(null);
        _t.Input.CursorLocked = false;
        try { FlashLocalization.Apply(_media, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { }
        _media.MouseEnabled = true;
        _media.SetValue("/:WhatPlatform", 1);
        _media.SetVariable("/:MenuType", "pc");
        _media.SetValue("/:IsPC_X360", true);
        _media.SetValue("/:fake", false);
        _media.SetValue("/:IsMyHouse", true);
        // Updates-/Umfrage-Kanal kommen in Unity vom Server (TDU2News/TDU2Surveys) – ohne Server keine Kacheln dafür
        _media.SetValue("/:IsDLCChannel", false);
        _media.SetValue("/:IsDLCHsys", false);
        _media.SetValue("/:DLC_Bought", false);
        _media.SetValue("/:Input", true);
        _newsTab = "game_progress";
        BuildNews();
        SetWeather();
        _media.Visible = true;
        _media.GotoLabel("/", "media_in", true);
        try { FrontendMusic.Play("TDMEDIA"); } catch (Exception) { }
        Log.Info($"TDU Media geöffnet: {_news.Count} Meldungen");
    }

    internal static void UpdateMedia(float dt)
    {
        if (_fp != null) _fp.LookEnabled = false;
        if (_media == null) { _mediaOpen = false; return; }
        if (_time >= _mediaNextWeather) { _mediaNextWeather = _time + 5f; SetWeather(); }
        UpdateNewsDetails();
        UpdateClock();
        if (_mediaForceCloseAt > 0f && _time >= _mediaForceCloseAt)
        {
            // B hat den Film zugeklappt, aber das abschließende "tdmd:clos" kam nicht: selbst schließen
            _mediaForceCloseAt = -1f;
            string l = _media.Label("/") ?? "";
            if (l == "close" || l == "default") CloseMedia();
        }
        if (_time - _mediaOpenedAt < 0.2f) return;   // Taste, mit der geöffnet wurde
        if (TDU2Controls.Pressed("menu_up")) _media.Input("Move", "up");
        if (TDU2Controls.Pressed("menu_down")) _media.Input("Move", "down");
        if (TDU2Controls.Pressed("menu_left")) _media.Input("Move", "left");
        if (TDU2Controls.Pressed("menu_right")) _media.Input("Move", "right");
        if (TDU2Controls.ConfirmPressed()) _media.Input("A");
        _media.ForceVisible("/dlc_popup", false);
    }

    /// <summary>ESC/Rücktaste: B des Films (Kanal zurück bzw. Fernseher aus).</summary>
    internal static void MediaBack()
    {
        if (_media == null) { CloseMedia(); return; }
        if (!_media.Input("B")) CloseMedia();
        else _mediaForceCloseAt = _time + 2.5f;
    }

    internal static void CloseMedia()
    {
        if (!_mediaOpen) return;
        _mediaOpen = false; _mediaForceCloseAt = -1f;
        if (_media != null) { _media.Visible = false; _media.GotoLabel("/", "default", false); }
        _newsList = null; _shownNews = -1;
        try { FrontendMusic.Stop(); } catch (Exception) { }
        _t.Input.CursorLocked = true;
        ResetUiAim();
        Log.Info("TDU Media geschlossen");
    }

    /// <summary>Uhr oben im Film (/clock): Ortszeit der Insel (Spielzeit) – _root.HDecade/HUnit/MDecade/MUnit, setHour().</summary>
    internal static void UpdateClock()
    {
        float h = WorldWeather.Hour;
        int hh = (int)MathF.Floor(h) % 24, mm = (int)MathF.Floor((h - MathF.Floor(h)) * 60f) % 60;
        int code = hh * 100 + mm;
        if (code == _clockShown) return;
        _clockShown = code;
        _media!.SetValue("/:HDecade", hh / 10); _media.SetValue("/:HUnit", hh % 10);
        _media.SetValue("/:MDecade", mm / 10); _media.SetValue("/:MUnit", mm % 10);
        _media.SetValue("/:SDecade", 0); _media.SetValue("/:SUnit", 0);
        _media.CallClip("/clock", "setHour");
    }

    internal static void OnMediaCommand(string cmd, string args)
    {
        if (!_mediaOpen || _media == null) return;
        string c = (cmd ?? "").Trim();
        if (!string.IsNullOrEmpty(args)) c += ":" + args.Trim();
        string lc = c.ToLowerInvariant();
        if (lc.StartsWith("sndp:")) return;
        Log.Info("TDU Media FSCommand " + c);
        if (lc == "sbdd") { try { FlashLocalization.Apply(_media, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language)); } catch (Exception) { } return; }
        if (lc == "tdmd:clos") { FrontendSounds.Play("mdstop"); CloseMedia(); return; }   // Ende der Schließ-Animation: Fernseher aus
        if (lc.StartsWith("tdmd:") && lc.EndsWith(":open")) FrontendSounds.Play("mdswitch");
        if (lc.StartsWith("tdmd:weat:")) { SetWeather(); return; }
        if (lc == "dlci" || lc.StartsWith("tdmd:virt:") || lc.StartsWith("tdmd:cdlc:") || lc.StartsWith("tdmd:surv:")) return;
        if (lc.StartsWith("tdmd:news:"))
        {
            if (lc.EndsWith(":open")) { SetNewsTabFlags(); BuildNews(); return; }
            if (lc.EndsWith(":clos")) { BuildNews(); return; }
            int sel = _newsList != null ? _newsList.Selected : 0;
            if (lc.Contains("del") && sel >= 0 && sel < _news.Count)
            {
                _news.RemoveAt(sel);
                if (_newsList != null) { FillNews(_newsList); _media.ListRefresh(_newsList); }
            }
            ShowNews(sel);
        }
    }

    internal static string WeatherFrame(float hoursAhead, string island)
    {
        WorldWeather.Forecast(hoursAhead, island, out var kind, out bool night);
        string t = kind switch
        {
            WeatherKind.Rainy => "rainy",
            WeatherKind.Tempest => "stormy",
            WeatherKind.Cloudy or WeatherKind.BeforeRain => "cloudy",
            _ => "clear",
        };
        return (night ? "n_" : "d_") + t;
    }

    internal static void SetWeather()
    {
        if (_media == null) return;
        for (int i = 0; i < 3; i++)
        {
            // jetzt, in 2 h, in 4 h (Spielzeit) – je Insel eigenes Wetter und Ortszeit
            _media.SetVariable("/:IbizaWeather" + i, WeatherFrame(i * 2f, "ibiza"));
            _media.SetVariable("/:HawaiiWeather" + i, WeatherFrame(i * 2f, "hawai"));
        }
    }

    internal static void BuildNews()
    {
        _news.Clear();
        // Unity: Server-News (Community) und Freundes-Meldungen vom Server – gibt es in der Engine noch nicht
        if (_newsTab == "game_progress")
        {
            var now = DateTime.Now;
            var save = TDU2Session.Save;
            if (save != null)
                _news.Add(new News
                {
                    Date = now,
                    Title = _german ? "Dein Profil" : "Your profile",
                    Text = _german
                        ? $"{TDU2Session.Nick}: {save.ownedCars?.Count ?? 0} Autos in der Garage, Kontostand {save.money:N0} $."
                        : $"{TDU2Session.Nick}: {save.ownedCars?.Count ?? 0} cars in the garage, balance ${save.money:N0}.",
                });
        }
        if (_media != null)
        {
            _media.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
            if (_newsList != null) { FillNews(_newsList); _media.ListRefresh(_newsList); }
        }
    }

    internal static void SetNewsTabFlags()
    {
        _media!.SetValue("/:IsNewsGameProgress", false);
        _media.SetValue("/:IsNewsCommunity", false);
        _media.SetValue("/:IsNewsClub", false);
        _media.SetValue("/:IsNewsDLC", false);
    }

    internal static void OnMediaListCreated(VmfPlayer.FlashList l)
    {
        if (_media == null) return;
        string lname = l.Name ?? "";
        if (lname.StartsWith("news_", StringComparison.OrdinalIgnoreCase))
        {
            _newsTab = lname.Substring(5).ToLowerInvariant();
            if (_newsTab != "community" && _newsTab != "club") _newsTab = "game_progress";
            _newsList = l; _shownNews = -1;
            BuildNews();
            _media.ListRefresh(l);
            _media.ListLoaded(l);
            ShowNews(0);
            return;
        }
        string path = VmfPlayer.PathOf(l.Owner) ?? "";
        bool news = path.IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0 || lname.IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(_media.GetVariable("/:CurrentChannel"), "news", StringComparison.OrdinalIgnoreCase);
        if (news) { _newsList = l; FillNews(l); }
        else l.Rows.Clear();   // DLC-/Updates-Listen bleiben leer (kein Server)
        _media.ListRefresh(l);
        _media.ListLoaded(l);
        if (news) ShowNews(0);
    }

    internal static void FillNews(VmfPlayer.FlashList l)
    {
        l.ColumnVars = new[] { "news_title", "news_date" };
        l.Rows.Clear();
        foreach (var n in _news)
            l.Rows.Add(new[] { n.Title, !string.IsNullOrEmpty(n.Version) ? "v" + n.Version.TrimStart('v', 'V') : n.Date.ToString(_german ? "dd.MM.yyyy" : "MM/dd/yyyy", CultureInfo.InvariantCulture) });
        if (l.Selected >= l.Rows.Count) { l.Selected = Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
        _media!.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
    }

    internal static void UpdateNewsDetails()
    {
        if (_newsList == null || _media == null) return;
        if (_newsList.Selected != _shownNews) ShowNews(_newsList.Selected);
        // Textlage halten (ohne Bild: "pictured" = Text über die ganze Breite)
        string l = _media.Label(DetailPath);
        if (l != null && l != "pictured") _media.GotoLabel(DetailPath, "pictured", false);
    }

    internal static void ShowNews(int i)
    {
        if (_media == null) return;
        _shownNews = i;
        var n = i >= 0 && i < _news.Count ? _news[i] : null;
        if (_newsList != null) _media.GotoLabel(DetailPath, "pictured", false);
        _media.SetVariable("/:NewsTitle", n?.Title ?? "");
        _media.SetVariable("/:NewsDescription", n?.Text ?? "");
        _media.SetVariable("/:CurrentNewsIndex", (_news.Count == 0 ? 0 : Math.Max(0, i) + 1).ToString(CultureInfo.InvariantCulture));
        _media.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
    }
}
