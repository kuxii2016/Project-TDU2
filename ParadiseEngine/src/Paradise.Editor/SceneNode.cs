// Szenen-Editor (F1 > Open Editor): Datenmodell auf dem SPIEL-Thread.
// Ein SceneNode beschreibt ein Objekt der Hierarchie (Unity: GameObject) über Delegates auf das echte Engine-Objekt.
// Nur Pump() (Spiel-Thread) ruft diese Delegates auf – die UI bekommt ausschließlich kopierte Snapshots.

using System.Globalization;
using System.Numerics;

namespace Paradise.Editor;

/// <summary>Ein Eintrag der Hierarchie (Spiel-Thread). Wird je Snapshot neu erzeugt; die Id bleibt für dasselbe Objekt gleich.</summary>
public sealed class SceneNode
{
    /// <summary>Stabile Kennung (echte Objekte: <see cref="EditorIds.Of"/>, Gruppen: "g:…").</summary>
    public string Id = "";
    public string Name = "";
    /// <summary>Art des Objekts (Tooltip in der Hierarchie, Kopf des Inspectors).</summary>
    public string Type = "";
    /// <summary>Das echte Engine-Objekt (nur Spiel-Thread).</summary>
    public object? Target;

    /// <summary>Aktiv-Zustand (Unity: activeSelf); null = Objekt kennt keinen.</summary>
    public Func<bool>? GetActive;
    public Action<bool>? SetActive;
    /// <summary>false = Objekt wurde entladen (Sektor weg …) – Auswahl wird aufgehoben.</summary>
    public Func<bool>? Alive;

    /// <summary>Anzahl der Kinder (billig, ohne sie zu erzeugen).</summary>
    public Func<int>? CountChildren;
    /// <summary>Kinder – wird nur für aufgeklappte Einträge (und die Suche) aufgerufen.</summary>
    public Func<IEnumerable<SceneNode>>? Children;

    /// <summary>Inspector-Inhalt (Transform, Bounds, Materialien …).</summary>
    public Action<InspectorBuilder>? Inspect;
    /// <summary>Änderung aus dem Inspector: (Schlüssel, Wert) → behandelt.</summary>
    public Func<string, string, bool>? Edit;
    /// <summary>Markierung des ausgewählten Objekts im 3D-Bild (DebugLines, Spielraum).</summary>
    public Action<SceneGizmo>? Gizmo;
    /// <summary>Klick-Auswahl (nur Wurzeln/Gruppen): Strahl im Render-Raum → nächster Treffer darunter.</summary>
    public Func<Vector3, Vector3, PickHit?>? Pick;

    public bool IsAlive { get { try { return Alive?.Invoke() ?? true; } catch (Exception) { return false; } } }
}

/// <summary>Treffer der Klick-Auswahl: Eintrag + Ids der Vorfahren (werden in der Hierarchy aufgeklappt).</summary>
public sealed class PickHit
{
    public SceneNode Node = null!;
    public string[] Path = Array.Empty<string>();
    /// <summary>Abstand entlang des Strahls (0 = Kamera steckt in der Box).</summary>
    public float T;
    /// <summary>Volumen der getroffenen Box (bei T = 0 gewinnt die kleinste).</summary>
    public float Volume;

    /// <summary>Besserer Treffer: zuerst Boxen vor der Kamera nach Abstand, dann umschließende nach Größe (wie MapStreamer.Pick).</summary>
    public static bool Better(PickHit a, PickHit? b)
    {
        if (b == null) return true;
        bool ai = a.T <= 0f, bi = b.T <= 0f;
        if (ai != bi) return !ai;
        return ai ? a.Volume < b.Volume : a.T < b.T;
    }
}

/// <summary>Zeilenarten des Inspectors.</summary>
public enum RowKind { Info, Bool, Vec3, Text }

public sealed class InspectorRow
{
    public RowKind Kind;
    public string Label = "";
    public string Value = "";
    /// <summary>Schlüssel für Änderungen (null = nur lesen).</summary>
    public string? Key;
}

public sealed class InspectorSection
{
    public string Title = "";
    /// <summary>Standard: eingeklappt (die UI merkt sich danach die Wahl des Nutzers je Titel).</summary>
    public bool Collapsed;
    public readonly List<InspectorRow> Rows = new();
}

/// <summary>Baut den Inspector-Inhalt (Spiel-Thread). Werte werden als Text (InvariantCulture) übergeben.</summary>
public sealed class InspectorBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    internal readonly List<InspectorSection> Sections = new();
    InspectorSection? _cur;

    public void Section(string title, bool collapsed = false)
    {
        _cur = new InspectorSection { Title = title, Collapsed = collapsed };
        Sections.Add(_cur);
    }

    InspectorSection Cur { get { if (_cur == null) Section("Allgemein"); return _cur!; } }

    public void Info(string label, string? value) => Cur.Rows.Add(new InspectorRow { Kind = RowKind.Info, Label = label, Value = value ?? "-" });
    public void Info(string label, float value, string fmt = "0.###") => Info(label, value.ToString(fmt, Inv));
    public void Info(string label, Vector3 v, string fmt = "0.###") => Info(label, V3(v, fmt));
    public void Bool(string label, bool value, string? key = null) => Cur.Rows.Add(new InspectorRow { Kind = RowKind.Bool, Label = label, Value = value ? "1" : "0", Key = key });
    public void Vec3(string label, Vector3 v, string? key = null) => Cur.Rows.Add(new InspectorRow { Kind = RowKind.Vec3, Label = label, Value = V3Edit(v), Key = key });
    public void Text(string label, string value, string? key = null) => Cur.Rows.Add(new InspectorRow { Kind = RowKind.Text, Label = label, Value = value ?? "", Key = key });

    public static string V3(Vector3 v, string fmt = "0.###") => v.X.ToString(fmt, Inv) + "  " + v.Y.ToString(fmt, Inv) + "  " + v.Z.ToString(fmt, Inv);
    /// <summary>Vektor für die X/Y/Z-Felder: "x;y;z".</summary>
    public static string V3Edit(Vector3 v) => v.X.ToString("0.####", Inv) + ";" + v.Y.ToString("0.####", Inv) + ";" + v.Z.ToString("0.####", Inv);

    public static bool TryParseV3(string s, out Vector3 v)
    {
        v = default;
        var p = (s ?? "").Split(';');
        if (p.Length != 3) return false;
        if (!float.TryParse(p[0], NumberStyles.Float, Inv, out float x)) return false;
        if (!float.TryParse(p[1], NumberStyles.Float, Inv, out float y)) return false;
        if (!float.TryParse(p[2], NumberStyles.Float, Inv, out float z)) return false;
        v = new Vector3(x, y, z);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }
}
