#nullable disable
namespace Paradise.Core.Online
{
    /// <summary>
    /// Challenge des Community Racing Centers (op "challlist"/"challget"; Server: TDU2Server Data/ChallengeStore.cs, gleiche Namen).
    /// Karte wie das Original (ChallengeIDCard): Gebühr, Basisbelohnung, Laufzeit (Tage), Zugänglichkeit (0 Alle, 1 Freunde, 2 Klub,
    /// 3 Freunde &amp; Klub); official = CRC-Challenge aus dem Admin-Panel.
    /// </summary>
    [Serializable]
    public sealed class ChallengeEntry
    {
        public int id;
        public string author = "";
        public string name = "";
        public string description = "";
        public int mode;
        public int island;
        public int length;
        public int checkpoints;
        public int fee;
        public int reward;
        public int duration;
        public int postTo;
        public bool official;
        public string created = "";
        public string expires = "";
        /// <summary>Beste Wertung (Zeitarten ms, Tempo/Radar km/h x 100), ihr Halter und Auto, gewertete Fahrten.</summary>
        public int bestScore;
        public string bestNick = "";
        public string bestCar = "";
        public int tries;
    }

    /// <summary>Hochladen aus dem Race-Editor (op "challup"): Karte + .ig2 (Base64).</summary>
    [Serializable]
    public sealed class ChallengeUpload
    {
        public string name = "";
        public string description = "";
        public int fee;
        public int reward;
        public int duration = 1;
        public int postTo;
        public string data = "";
    }
}
