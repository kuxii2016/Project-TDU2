using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Avatars;

/// <summary>
/// Szenen-Editor (F1 &gt; Open Editor, Projekt Paradise.Editor): Liste aller Avatare (Fahrer, Beifahrer, Interieur-Figuren,
/// Mitspieler im Raum, Vorschauen), letzte Zeichenlage, Ausblenden und Teile/Materialien. Hauptthread.
/// </summary>
public sealed partial class AvatarModel
{
    static readonly List<WeakReference<AvatarModel>> _editorAll = new();

    /// <summary>Nicht zeichnen (unabhängig von <see cref="Visible"/>, das z.B. CarAvatar jedes Bild setzt).</summary>
    public bool EditorHidden;
    /// <summary>Avatar-Raum → Render-Raum des letzten Draw-Aufrufs.</summary>
    public Matrix4x4 EditorLastModel { get; private set; } = Matrix4x4.Identity;
    /// <summary>Environment.TickCount64 des letzten Zeichnens (0 = nie).</summary>
    public long EditorDrawnTick { get; private set; }
    public bool EditorDisposed { get; private set; }

    static void EditorRegister(AvatarModel a)
    {
        lock (_editorAll) _editorAll.Add(new WeakReference<AvatarModel>(a));
    }

    /// <summary>Alle lebenden (nicht freigegebenen) Avatare in Erzeugungsreihenfolge.</summary>
    public static List<AvatarModel> EditorAll()
    {
        var l = new List<AvatarModel>();
        lock (_editorAll)
        {
            _editorAll.RemoveAll(w => !w.TryGetTarget(out var a) || a.EditorDisposed);
            foreach (var w in _editorAll) if (w.TryGetTarget(out var a)) l.Add(a);
        }
        return l;
    }

    public readonly record struct EditorPiece(string Name, MaterialDesc Desc, IGpuMaterial Material, int Vertices, int Triangles, bool Head);

    public List<EditorPiece> EditorPieces()
    {
        var l = new List<EditorPiece>(_pieces.Count);
        foreach (var p in _pieces) l.Add(new EditorPiece(p.Desc.Name ?? "", p.Desc, p.Mat, p.Prim.Pos.Length, p.Indices.Length / 3, p.Head));
        return l;
    }

    /// <summary>Teile (Körper, Kleidung) mit Namen.</summary>
    public List<string> EditorParts() { var l = new List<string>(_parts.Count); foreach (var p in _parts) l.Add(p.Name); return l; }
}
