#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/HouseLayout.cs).
namespace Paradise.Core.Online
{
    [Serializable]
    public sealed class HouseLayout
    {
        public string spot = "";
        public List<HouseEntry> furn = new List<HouseEntry>();   // Entity-Name -> Bank (interior/myhouse)
        public List<HouseEntry> mats = new List<HouseEntry>();   // Fläche ("Floor1", "Wall2") -> Material (ground_NN / wall_NN)
    }
}
