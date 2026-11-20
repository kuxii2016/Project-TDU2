// Szenen-Editor: kopierte Daten für den UI-Thread (keine Engine-Referenzen).

namespace Paradise.Editor;

internal sealed class TreeItem
{
    public string Id = "", Name = "", Type = "";
    public bool? Active;
    public int ChildCount;
    /// <summary>null = zugeklappt (oder keine Kinder); sonst die Kinder.</summary>
    public List<TreeItem>? Children;
    /// <summary>Platzhalter "… n weitere" (nicht auswählbar).</summary>
    public bool More;
}

internal sealed class InspectorData
{
    public string Id = "", Name = "", Type = "";
    public bool? Active;
    public bool CanRename;
    public List<InspectorSection> Sections = new();
    /// <summary>Aufbau (Abschnitte/Zeilen/Arten): gleich = Werte an Ort und Stelle aktualisieren, sonst neu aufbauen.</summary>
    public string Signature = "";
}

internal sealed class SceneSnapshot
{
    public List<TreeItem> Roots = new();
    /// <summary>Suchergebnis (flach, Name = Pfad) – null ohne Filter.</summary>
    public List<TreeItem>? Search;
    public int SearchVisited;
    public bool SearchTruncated;
    public int Visible;
    public InspectorData? Inspector;
    public string? SelectedId;
    public string Status = "";
    public bool Live, Gizmo, GizmoChildren;
}
