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
    }
}
