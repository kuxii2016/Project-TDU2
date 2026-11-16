namespace Paradise.Game.Config;

/// <summary>Login/Profile (GAME_MODE_LOGIN) – Werte wie im Unity-Projekt (TDU2Login/TDU2GameData).</summary>
public sealed class LoginConfig
{
    /// <summary>Autosave-Abstand in Sekunden (Unity TDU2GameData.AutosaveSeconds).</summary>
    public float AutosaveSeconds { get; set; } = 240f;
    /// <summary>Neue Profile wie im Spiel nach GamePC.cpr starten (Auto, Garage, Geld, Standard-Haus). Aus = Startauto des Servers.</summary>
    public bool GamePcStart { get; set; } = true;
    /// <summary>Server-Port, wenn --server keinen angibt (Unity TDU2GameData.TcpPort).</summary>
    public int DefaultTcpPort { get; set; } = 7777;
    /// <summary>Herkunft für neue Konten (Unity TDU2GameData.NationalityId).</summary>
    public int NationalityId { get; set; } = 0;
    /// <summary>Neues Profil: Popup startet als Online-Profil, wenn ein Server übergeben wurde (sonst Offline).</summary>
    public bool NewProfileOnlineWithServer { get; set; } = true;
    /// <summary>Höchstzeit, bis der Boden am Startpunkt geladen ist (Sekunden, Unity 15).</summary>
    public float SpawnTimeout { get; set; } = 15f;
    /// <summary>FSCommands des Login/Profilmenüs ins Log schreiben.</summary>
    public bool LogCommands { get; set; } = false;
}
