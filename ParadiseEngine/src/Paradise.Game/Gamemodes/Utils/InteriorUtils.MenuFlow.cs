using Paradise.Core.Diagnostics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Zurück/ESC aller Interieur-Menüs an einer Stelle – Port von Unity TDU2MenuFlow.cs: die oberste offene Menü-Ebene wird jedes
/// Bild aus dem Zustand bestimmt; B des Films, reagiert er nicht, schließt das Spiel die Ebene selbst.
/// </summary>
public abstract partial class InteriorUtils
{

    // ================================================================== Menü-Ebenen / Zurück (Unity TDU2MenuFlow)
    internal enum MenuLayer { None, Media, GarageList, DealerCar, DealerWindow, Dealer, LifePhoto, LifeText, MyLife, Diary, HouseEditor, House }
    /// <summary>Menü offen: ESC/Rücktaste gehören dem Film (GAME_MODE_LIVING.Run).</summary>
    internal static bool MenuOpen => _dealerMenu || GAME_MODE_LIVING.HomeMenuOpen || GAME_MODE_GARAGE.GarageListOpen;

    internal static MenuLayer TopMenuLayer()
    {
        if (GAME_MODE_LIVING._mediaOpen) return MenuLayer.Media;
        if (!_dealerMenu && GAME_MODE_GARAGE.GarageListOpen) return MenuLayer.GarageList;
        if (_dealerMenu) return _carIn != CarIn.None ? MenuLayer.DealerCar : DealerWindowOpen ? MenuLayer.DealerWindow : MenuLayer.Dealer;
        if (GAME_MODE_LIVING._diaryOpen)
        {
            if (GAME_MODE_LIVING._textVar != null) return MenuLayer.LifeText;
            if (GAME_MODE_LIVING.LifeShown()) return MenuLayer.MyLife;
            return MenuLayer.Diary;
        }
        if (GAME_MODE_LIVING._myHouse == GAME_MODE_LIVING.HouseMode.Furniture || GAME_MODE_LIVING._myHouse == GAME_MODE_LIVING.HouseMode.Material) return MenuLayer.HouseEditor;
        if (GAME_MODE_LIVING._myHouse == GAME_MODE_LIVING.HouseMode.Walk) return MenuLayer.House;
        return MenuLayer.None;
    }

    internal static MenuLayer _backLayer; internal static float _backAt, _backChangeAt; internal static bool _backChanged; internal static string? _backSig;
    internal const float BackNoReactionSeconds = 0.7f, BackMaxSeconds = 3f;
    internal static readonly string[] SigClips = { "/", "/diary", "/diary/menu", "/mylf", "/mylf/edbox/edit_popup", "/mylf/photo_taker", "/mylf/vk",
                                          "/myhs_editor", "/buy", "/cursor/mini_menu", "/enter_car", "/garage", "/garage/vehicule_list" };

    internal static string MenuSignature()
    {
        var sb = new System.Text.StringBuilder(128);
        if (_ui != null) { foreach (var p in SigClips) sb.Append(_ui.Label(p)).Append('|'); sb.Append(_ui.GetVariable("/:Input")); }
        sb.Append('|').Append(TopMenuLayer());
        return sb.ToString();
    }

    /// <summary>ESC bzw. Rücktaste (esc = false) im Menü: normaler Weg des Films; ein zweites ESC während des ersten schließt sofort.</summary>
    internal static void MenuBack(bool esc)
    {
        var top = TopMenuLayer();
        if (top == MenuLayer.None) return;
        if (!esc && top == MenuLayer.LifeText) return;   // Rücktaste löscht im Textfeld
        if (top == MenuLayer.Media) { GAME_MODE_LIVING.MediaBack(); return; }
        if (_backLayer == top && _time - _backAt < BackMaxSeconds) { ForceCloseLayer(top, "ESC erneut"); return; }
        _backLayer = top; _backAt = _time; _backChangeAt = _time; _backChanged = false;
        _backSig = MenuSignature();
        if (_ui != null) _ui.SetValue("/:Input", true);
        switch (top)
        {
            case MenuLayer.GarageList: GAME_MODE_GARAGE.GarageListBack(); break;
            case MenuLayer.DealerCar:
            case MenuLayer.DealerWindow:
            case MenuLayer.Dealer: DealerBack(); break;
            case MenuLayer.LifeText: GAME_MODE_LIVING._textVar = null; _ui?.Input("B"); break;
            case MenuLayer.MyLife:
            case MenuLayer.Diary: if (_ui == null || !_ui.Input("B")) GAME_MODE_LIVING.CloseDiary(); break;
            case MenuLayer.HouseEditor: if (_ui == null || !_ui.Input("B")) GAME_MODE_LIVING.ForceHouseWalk(); break;
            case MenuLayer.House: GAME_MODE_LIVING.QuitMyHouse(); break;
        }
    }

    /// <summary>Jedes Bild: läuft ein Zurück, ob der Film reagiert – sonst schließt das Spiel die Ebene selbst.</summary>
    internal static void UpdateBackWatch()
    {
        if (_backLayer == MenuLayer.None) return;
        var top = TopMenuLayer();
        if (top != _backLayer) { _backLayer = MenuLayer.None; return; }
        var sig = MenuSignature();
        if (sig != _backSig) { _backSig = sig; _backChangeAt = _time; _backChanged = true; }
        if (_backChanged && _time - _backChangeAt >= 0.6f) { _backLayer = MenuLayer.None; return; }
        if (!_backChanged && _time - _backAt >= BackNoReactionSeconds) ForceCloseLayer(top, "Film reagiert nicht");
        else if (_time - _backAt >= BackMaxSeconds) ForceCloseLayer(top, "Zeitüberschreitung");
    }

    /// <summary>Ebene ohne Mitwirkung des Films schließen und einen sauberen Zustand herstellen.</summary>
    internal static void ForceCloseLayer(MenuLayer l, string why)
    {
        _backLayer = MenuLayer.None;
        Log.Warn("Menü " + l + " geschlossen (" + why + ")");
        switch (l)
        {
            case MenuLayer.GarageList: GAME_MODE_GARAGE.GarageListForceClose(); break;
            case MenuLayer.DealerCar:
            case MenuLayer.DealerWindow:
            case MenuLayer.Dealer: DealerForceClose(); break;
            case MenuLayer.LifeText:
                GAME_MODE_LIVING._textVar = null;
                if (_ui != null) { _ui.GotoLabel("/mylf/vk", "default", false); _ui.SetValue("/:Input", true); }
                break;
            case MenuLayer.MyLife:
            case MenuLayer.Diary: GAME_MODE_LIVING.CloseDiary(); break;
            case MenuLayer.HouseEditor: GAME_MODE_LIVING.ForceHouseWalk(); break;
            case MenuLayer.House: GAME_MODE_LIVING.ForceEndMyHouse(); break;
        }
    }
}
