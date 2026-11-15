#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/InviteEntry.cs).
namespace Paradise.Core.Online
{
    /// <summary>Offene Einladung (op "status": invites).</summary>
    [Serializable]
    public sealed class InviteEntry
    {
        public int from;
        public string nick = "";
        public string kind = "";
    }
}
