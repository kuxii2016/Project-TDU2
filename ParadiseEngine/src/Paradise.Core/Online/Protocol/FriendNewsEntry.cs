#nullable disable
namespace Paradise.Core.Online
{
    /// <summary>Meldung eines Freundes im Spielstand (Friends: Art, Spieler, Datum ISO UTC, gelesen).</summary>
    [Serializable]
    public sealed class FriendNewsEntry
    {
        public int kind;
        public string nick = "";
        public string date = "";
        public bool read;
    }
}
