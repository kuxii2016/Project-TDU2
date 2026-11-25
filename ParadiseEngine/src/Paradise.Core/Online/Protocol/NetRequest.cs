#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/NetRequest.cs).
namespace Paradise.Core.Online
{
    [Serializable]
    public sealed class NetRequest
    {
        public string op = "";
        public string nick = "";
        public string email = "";
        public string password = "";
        public string token = "";
        public string session = "";
        public int version = 1;
        public int nationality;
        public SaveData save;
        public int target;          // invite/invreply: Spielernummer
        public bool accept;         // invreply
        public string kind = "";    // invite: "codriving"
        public int since;           // news: höchste schon bekannte News-Id (0 = alle)
        public int choice;          // vote: gewählte Antwort (0-basiert), target = Umfrage-Id
        public bool p2p;            // status: dieser Client kann P2P (Hole-Punching)
        public string localEp = ""; // status: lokaler UDP-Endpunkt "ip:port" (Mitspieler im selben LAN)
        public HouseLayout house;   // house: geänderte Einrichtung eines eigenen Hauses (My House, Server erweitert)
        public string spot = "";    // houseget: Haus-Spot (leer = alle Häuser des Spielers "target")
        public int score;           // challres: Wertung der Fahrt (Zeit ms bzw. km/h x 100); kind = Auto
        public PhotoUpload photo;   // photoup: Foto eines Bilderrahmens (My House) für die Gäste
        public string file = "";    // photoget: Dateiname des Fotos (target = Spielernummer des Besitzers)
        public ChallengeUpload chall;  // challup: Challenge aus dem Race-Editor; challlist: kind "crc"/"community", target = Insel (-1 alle); challget: target = Id
    }
    /// <summary>photoup: ein Foto des Albums (JPG Base64, Dateiname "%07X%c.JPG", Name).</summary>
    [Serializable]
    public sealed class PhotoUpload
    {
        public string file = "";
        public string name = "";
        public string data = "";
    }
}
