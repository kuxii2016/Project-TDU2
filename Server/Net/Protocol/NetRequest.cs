using TDU2Server.Data;

namespace TDU2Server.Net.Protocol;

/// <summary>Anfrage des Clients (eine JSON-Zeile). Nicht jede Anfrage nutzt alle Felder.</summary>
public sealed class NetRequest
{
    public string op = "";
    public string nick = "";
    public string email = "";
    public string password = "";
    public string token = "";
    public string session = "";
    public int version;
    public int nationality;
    public SaveData save;
    public int target;          // invite/invreply/profile: Spielernummer
    public bool accept;         // invreply
    public string kind = "";    // invite: "codriving", "game|…", "room|…"
    public int since;           // news: höchste schon bekannte News-Id (0 = alle holen)
    public int choice;          // vote: gewählte Antwort (0-basiert), target = Umfrage-Id
    public bool p2p;            // status: Client kann P2P (Hole-Punching)
    public string localEp = ""; // status: lokaler UDP-Endpunkt "ip:port" (Mitspieler im selben LAN)
    public HouseLayout house;   // house: geänderte Einrichtung eines Hauses (Besitzer)
    public string spot = "";    // houseget: Haus-Spot (leer = alle Häuser des Spielers "target")
    public int score;           // challres: Wertung der Fahrt (Zeit ms bzw. km/h x 100); kind = Auto
    public ChallengeUpload chall;  // challup: Challenge aus dem Race-Editor (Karte + .ig2); challlist: kind = "crc"/"community", target = Insel (-1 alle); challget: target = Id
}
