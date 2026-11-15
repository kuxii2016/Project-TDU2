#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/BookmarkEntry.cs).
namespace Paradise.Core.Online
{
    [Serializable]
    public sealed class BookmarkEntry
    {
        public int id;
        public string island = "";
        public float x, y, z;
    }
}
