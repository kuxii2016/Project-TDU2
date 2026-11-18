namespace Paradise.Game.Config;

/// <summary>
/// Online-Modus (Game/Online: Multiplayer, RemoteCars, Chat, NameTags, RoomAvatars) – Standardwerte wie im Unity-Projekt
/// (TDU2GameData "Mitspieler-Synchronisation (UDP)" / "Chat", TDU2Multiplayer).
/// </summary>
public static class OnlineConfig
{
    // ---- Mitspieler-Synchronisation (Unity TDU2GameData)
    /// <summary>P2P: Mitspieler-Daten direkt austauschen (Hole-Punching), Server-Relay nur ohne direkten Weg (Unity UseP2P).</summary>
    public static bool UseP2P = true;
    /// <summary>Zustandspakete pro Sekunde (Unity SendRate).</summary>
    public static float SendRate = 20f;
    /// <summary>Darstellungsverzögerung für die Interpolation in Sekunden (Unity InterpDelay).</summary>
    public static float InterpDelay = 0.12f;
    /// <summary>Mitspieler ohne Pakete so lange behalten (Sekunden, Unity Timeout).</summary>
    public static float Timeout = 4f;
    /// <summary>Mitspieler-Autos höchstens so weit entfernt darstellen (m, Unity MaxDistance).</summary>
    public static float MaxDistance = 3000f;
    /// <summary>Mitspieler-Autos sind fest – man prallt ab statt durchzufahren (Unity PlayerCollisions).</summary>
    public static bool PlayerCollisions = true;
    /// <summary>Motor- und Hupen-Sound der Mitspieler (Unity PlayerSounds).</summary>
    public static bool PlayerSounds = true;
    /// <summary>Lackierung/Beklebung der Mitspieler (Unity TDU2OnlineSettings.SyncLiveries).</summary>
    public static bool SyncLiveries = true;
    /// <summary>Verschmutzung der Mitspieler-Autos (Unity TDU2OnlineSettings.SyncDirt).</summary>
    public static bool SyncDirt = true;
    /// <summary>Mitspieler weiter weg als … m bekommen (P2P) nur jedes 4. Zustandspaket (Unity PeerFarRange).</summary>
    public static float PeerFarRange = 1500f;
    /// <summary>Verkehrspakete (P2P) nur an Mitspieler innerhalb … m (Unity PeerTrafficRange).</summary>
    public static float PeerTrafficRange = 1200f;
    /// <summary>Hörweite der Mitspieler-Autos (m): Lautstärke linear von 4 m bis hier (Engine-Mixer ist 2D, wie Traffic).</summary>
    public static float SoundRange = 120f;

    // ---- Namen über den Autos (Original general.bnk av_name_XX, Game/Online/NameTags)
    /// <summary>Namen über den Autos der Mitspieler (Unity ShowNames).</summary>
    public static bool ShowNames = true;
    /// <summary>Höchstzahl Namensschilder: av_name_01..08 im Film, der Rest wird wie im Original dupliziert (exe bis 32).
    /// Engine-Lobbys haben höchstens 16 Spieler.</summary>
    public static int MaxNameTags = 16;

    // ---- Mitspieler als Spielfigur in Interieurs (Unity TDU2OnlineSettings, Game/Online/RoomAvatars)
    /// <summary>Hauptschalter: Mitspieler in Interieurs als Spielfigur zeigen (und die eigene Figur senden).</summary>
    public static bool ShowPlayersInInteriors = true;
    public static bool ShowInClothesShops = true;     // EAS_*  Kleidungsläden
    public static bool ShowInCarDealers = true;       // ECD_* / ECDC_*  Autohändler
    public static bool ShowInBikeDealers = true;      // EBD_*  Motorradhändler
    public static bool ShowInClubs = true;            // ECH_*  Clubs / Community Racing Center
    public static bool ShowInCasino = true;           // Casino (Halle und Spielräume)
    public static bool ShowInGamblingHall = true;     // EGAM_* Spielhalle
    public static bool ShowInAirport = true;          // EAIR_* Flughafen
    public static bool ShowInHairdresser = true;      // EHD_*  Friseur
    public static bool ShowInSurgery = true;          // ESUR_* Schönheitsklinik
    public static bool ShowInRealtor = true;          // ERE_*  Makler
    public static bool ShowInTuner = false;           // ETUN_* Tuner
    public static bool ShowInStickerShop = false;     // ESTICK_* Lackiererei/Sticker
    public static bool ShowInRacingSchool = true;     // ERS_*  Fahrschule (Gebäude)
    public static bool ShowInPhotograph = true;       // EPHOT_* Fotograf
    public static bool ShowInAuctionHall = true;      // EUC_*  Auktionshaus
    public static bool ShowInStolenCarDealer = true;  // ESTOL_* Hehler
    public static bool ShowInOtherInteriors = true;   // alles andere
    /// <summary>Häuser (EPH_*): Mitspieler überhaupt zeigen.</summary>
    public static bool ShowInHouses = true;
    /// <summary>Häuser nur für Mitspieler aus der eigenen Gruppe (Einladung/Beitreten), sonst für alle im selben Haus.</summary>
    public static bool HousesInviteOnly = true;

    /// <summary>Sieht man in diesem Interieur (Spot-Code, Casino) Mitspieler? (Unity TDU2OnlineSettings.InteriorShared)</summary>
    public static bool InteriorShared(string? spotCode, bool casino)
    {
        if (!ShowPlayersInInteriors) return false;
        if (casino) return ShowInCasino;
        string c = (spotCode ?? "").ToUpperInvariant();
        if (c.StartsWith("EPHOT")) return ShowInPhotograph;
        if (c.StartsWith("EPH")) return ShowInHouses;
        if (c.StartsWith("EAS")) return ShowInClothesShops;
        if (c.StartsWith("ECD")) return ShowInCarDealers;
        if (c.StartsWith("EBD")) return ShowInBikeDealers;
        if (c.StartsWith("ECH")) return ShowInClubs;
        if (c.StartsWith("ECAS")) return ShowInCasino;
        if (c.StartsWith("EGAM")) return ShowInGamblingHall;
        if (c.StartsWith("EAIR")) return ShowInAirport;
        if (c.StartsWith("EHD")) return ShowInHairdresser;
        if (c.StartsWith("ESUR")) return ShowInSurgery;
        if (c.StartsWith("ERE")) return ShowInRealtor;
        if (c.StartsWith("ETUN")) return ShowInTuner;
        if (c.StartsWith("ESTICK")) return ShowInStickerShop;
        if (c.StartsWith("ESTOL")) return ShowInStolenCarDealer;
        if (c.StartsWith("ERS")) return ShowInRacingSchool;
        if (c.StartsWith("EUC")) return ShowInAuctionHall;
        return ShowInOtherInteriors;
    }

    // ---- Chat (general.bnk /pop_chat, Unity TDU2GameData "Chat")
    public static bool EnableChat = true;
    /// <summary>Chatzeile so lange anzeigen (Sekunden, Unity ChatLineSeconds).</summary>
    public static float ChatLineSeconds = 20f;
    /// <summary>Maximale Länge einer Nachricht (Unity ChatMaxLength).</summary>
    public static int ChatMaxLength = 100;
}
