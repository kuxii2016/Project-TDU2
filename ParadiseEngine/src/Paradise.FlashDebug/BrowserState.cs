namespace Paradise.FlashDebug;

/// <summary>Zustand des Browsers auf Spiel-Seite (einziger Ort, der verändert werden darf).</summary>
internal sealed class BrowserState
{
    public bool Live = true;
    public string Filter = "", Status = "";
    // Standardmäßig ALLE Objekte zeigen: viele TDU2-Clips sind initial hidden/alpha=0
    // und würden sonst den kompletten Baum leer erscheinen lassen.
    public bool OnlyVisible = false, OnlyClips, ShowPaths, ExpandAll;
    public HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase) { "/" };
    public int SelPlayer = -1;
    public string SelPath = "/";
    public float NextSnapshot;

    // "Welche Funktionen lesen diese Variable?" (Unity UsagePanel)
    public string UsageVar = "";
    public List<string> Usage = new();
    public bool AutoCall;
}
