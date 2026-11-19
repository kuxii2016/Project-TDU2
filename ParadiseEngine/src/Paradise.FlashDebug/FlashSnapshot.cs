// Port der Unity Flash-Debugger-Daten ("TDU2/Flash Debugger") für die eigene Engine.
// Snapshots sind einfache DTOs: die VmfPlayer-Objekte bleiben immer auf dem Spiel-Thread,
// der WinForms-UI-Thread bekommt nur kopierte Werte (kein Cross-Thread-Zugriff auf Flash).

namespace Paradise.FlashDebug;

public sealed class FlashPlayerInfo
{
    public int Index;
    public string Label = "";
    public string Bank = "";
    public string Film = "";
    public bool Visible;
    public bool MouseEnabled;
    public int GuiDepth;
}

public sealed class FlashNodeInfo
{
    public string Path = "/";
    public string Name = "";
    public string Kind = "";
    public int Depth;
    public bool Visible;
    public bool IsClip;
    public bool HasChildren;
    public bool Expanded;
    public string Alpha = "";
}

public sealed class FlashLabelInfo
{
    public string Name = "";
    public int Frame;   // 1-basiert (wie im Unity-Debugger angezeigt)
}

public sealed class FlashVarInfo
{
    public string Key = "";
    public string Value = "";
}

public sealed class FlashFnInfo
{
    public string Key = "";
    public string Params = "";
    /// <summary>Vollständiger "/pfad:name"-Schlüssel, wie ihn VmfPlayer.DebugCall braucht.</summary>
    public string CallKey = "";
}

public sealed class FlashLogInfo
{
    public string Time = "";
    public string Command = "";
    public string Args = "";
    public override string ToString() => string.IsNullOrEmpty(Args) ? Time + "  " + Command : Time + "  " + Command + "  " + Args;
}

public sealed class FlashDetailInfo
{
    public string Path = "/";
    public string Film = "";
    public string Type = "";
    public int Depth, CharId, ClipDepth;
    public bool Visible, IsClip, Playing;
    public float X, Y, Alpha, ScaleX, ScaleY, OffsetX, OffsetY;
    public string Screen = "";
    public string ScaleInfo = "";
    public string Text = "", TextVariable = "", TextKey = "";

    // Zeitleiste (auch für "/" = Wurzel)
    public int Frame, FrameCount;
    public string CurrentLabel = "";
    public FlashLabelInfo[] Labels = Array.Empty<FlashLabelInfo>();

    // Variablen/Funktionen dieses Clips (bereits gefiltert nach Pfad-Präfix)
    public List<FlashVarInfo> ClipVars = new();
    public List<FlashFnInfo> ClipFns = new();

    // Fertige Code-/Patch-Zeilen zum Kopieren
    public string[] CodeLines = Array.Empty<string>();
    public List<FlashPatchInfo> Patches = new();
}

public sealed class FlashPatchInfo
{
    public string Film = "", Path = "", Command = "", Value = "";
    public string Line => Film + " | " + Path + " | " + Command + " | " + Value;
}

public sealed class FlashSnapshot
{
    public List<FlashPlayerInfo> Players = new();
    public int SelectedPlayer = -1;
    public string SelectedPath = "/";
    public bool ShowPaths;

    public List<FlashNodeInfo> Nodes = new();
    public int RootObjects;          // echte Anzahl Objekte in Root.List (ohne jeden Filter)
    public string FilmName = "";
    public bool FilterIgnored;       // Baum wurde ungefiltert erneut aufgebaut
    public bool TreeTruncated;       // Knotenlimit/Tiefe erreicht – es gibt noch mehr
    public bool ExpandAll;
    public List<FlashVarInfo> Vars = new();
    public List<FlashFnInfo> Fns = new();
    public List<FlashLogInfo> Logs = new();
    public FlashDetailInfo? Detail;

    public string[] Banks = Array.Empty<string>();
    public string BnkDir = "";
    public string BankStartDir = "";
    public string PatchFile = "";
    public string Focus = "";
    public string ActiveList = "";
    public string UsageVar = "";
    public List<string> Usage = new();
    public bool AutoCall;
    public string Status = "";
    public int VarsTotal, FnsTotal;
}
