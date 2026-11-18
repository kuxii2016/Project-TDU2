namespace Paradise.Game.Screens;

/// <summary>Spielmodus wie im Original (GAME_MODE_*)
/// Jeder aufgelistete Mode soll seine eigene Saubere Implementierung in Gamemodes Ordner Bekommen.
/// Hier soll UI-Sowie Funktionen Gesteuert werden -> UI Callback etc um alles sauber zu haben
/// Gamemode sollen via exe direckt aufrufbar sein args exestieren Gamemode = 0 default</summary>
public enum Gamemode
{
    GAME_MODE_BOOTMENU = 0, //Implementiert
    GAME_MODE_FREERIDE = 1, //Implementiert
    GAME_MODE_MAP = 2, //Implementiert
    GAME_MODE_SHOP = 3, //Implementiert
    GAME_MODE_MISSION = 4,
    GAME_MODE_EDITOR = 5, //Race Editor, TDU2 hat einen Race-Editor, in dem Leute Rennen erstellen und ins Comunity Center Hochladen können, andere spieler können das Rennen dann Spielen
    GAME_MODE_CARSHOWCASE = 6,
    GAME_MODE_TESTCAR = 7,  //Implementiert
    GAME_MODE_GARAGE = 8, //Ported
    GAME_MODE_LIVING = 9,  //Ported
    GAME_MODE_GOODIES = 10, //Ported
    GAME_MODE_GAMBLING_HALL = 11, //Comunity Racing Center
    GAME_MODE_DRESSING = 12, //Implementiert
    GAME_MODE_ISLAND_CHANGE = 13,
    GAME_MODE_VIDEOBUMPER = 14, //Start Videos bei Spielstart, wie im Original, mit Skip Funktion, und nach allen Videos wird der MainMenu Gamemode geladen -> Siehe GAME_MODE_BOOTMENU.cs
    GAME_MODE_AITRAINER = 15, //Empty,TDU2 hat einen AI-Trainermode, aber kein Gameplay, nur Testzwecke, daher hier nicht implementiert
    GAME_MODE_CINVIEWER = 16, //Empty,TDU2 hat einen Cinematic-Viewer, aber kein Gameplay, nur Testzwecke, daher hier nicht implementiert
    GAME_MODE_ESTATES = 17, //Imobilien Markler sollte als Nächstes Gemacht werden
    GAME_MODE_CLUB = 18,
    GAME_MODE_LOBBY = 19, //Renn Lobby in Multiplayer rennen
    GAME_MODE_LOGIN = 20, //Implementiert
    GAME_MODE_JAIL = 21, //Empty,TDU2 hat keinen Jailmode mehr
    GAME_MODE_SCHOOL = 22,
    GAME_MODE_SCREEN_SHOTS = 23, //TDUMedia Screenshot Album
    GAME_MODE_AITEST = 24, //Empty,TDU2 hat einen AI-Testmode, aber kein Gameplay, nur Testzwecke, daher hier nicht implementiert
    GAME_MODE_HAIRDRESSER = 25, //Ported
    GAME_MODE_PHOTOGRAPH = 26,
    GAME_MODE_SPOT_VIEWER = 27,
    GAME_MODE_REPLAY = 28,
    GAME_MODE_CASINO = 29,
    GAME_MODE_STICKERS = 30, //Implementiert
    GAME_MODE_CINE = 31,
    GAME_MODE_AVATAR_CHOICE = 32,
    GAME_MODE_SURGERY = 33,
    GAME_MODE_AIRPORT = 34,
    GAME_MODE_FIRST_WM = 35,
    GAME_MODE_CAR_WASH = 36,
    GAME_MODE_EXIT = 37, //Keine ahnung wozu das ist, Jedenfals -> ESC menue Quit Game hier nochmals Savegame save triggern, und dann Game beenden
    GAME_MODE_CASINOAVATARCHOICE = 38,
    GAME_MODE_WORLD_MAP = 39, //World Viewer, Like Current FlyCam need Hotkey to switch Island F6 Ibiza, F7 Hawaii, F8 Casino
    GAME_MODE_PAUSE = 40, //ESC menue in Unity3d
    GAME_MODE_TUNER = 41, //Tuner (Spots ETUN_*, Interieur itun_a, shopscar.vmf "after_mkt"); im Original ein Teil des Shop-Managers, hier eigene Klasse
    GAME_MODE_CHAMPIONSHIP = 42, //Meisterschaftsrennen (Unity TDU2Championships); im Original Teil des Wettbewerbs-Managers, hier eigene Klasse
}
