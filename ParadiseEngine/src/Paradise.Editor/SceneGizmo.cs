// Szenen-Editor: Markierung des ausgewählten Objekts im 3D-Bild über die vorhandenen DebugLines
// (Paradise.Core.Render.DebugLines, Spielraum, ohne Tiefentest über der Welt – wie die Unity-Auswahl im Scene-View).

using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Editor;

public sealed class SceneGizmo
{
    /// <summary>Auswahlfarbe (Unity: orange Auswahl-Umriss).</summary>
    public static Vector4 Color = new(1f, 0.55f, 0.1f, 1f);
    /// <summary>Farbe der Kinder-Boxen (Unity: hellere Umrisse der Kinder).</summary>
    public static Vector4 ChildColor = new(1f, 0.8f, 0.35f, 0.45f);

    readonly Func<Vector3, Vector3> _renderToGame;
    internal string Label = "";
    internal bool LabelDone;
    /// <summary>Kinder des ausgewählten Objekts: blasse Farbe, keine Beschriftung.</summary>
    internal bool ChildMode;

    internal SceneGizmo(Func<Vector3, Vector3> renderToGame) => _renderToGame = renderToGame;

    /// <summary>Render-Raum (CarNode.World …) → Spielraum (DebugLines).</summary>
    public Vector3 RenderToGame(Vector3 r) => _renderToGame(r);

    /// <summary>Achsparallele Box im Spielraum (RenderObject/Node/Scene-Bounds).</summary>
    public void Aabb(Vector3 min, Vector3 max, bool child = false)
    {
        if (!(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z)) return;
        if (!float.IsFinite(min.X) || !float.IsFinite(max.X)) return;
        Box(min, max, Matrix4x4.Identity, child);
    }

    /// <summary>Lokale Box (min/max) mit einer Matrix in den Spielraum gebracht.</summary>
    public void Box(Vector3 min, Vector3 max, Matrix4x4 toGame, bool child = false)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = Vector3.Transform(new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z), toGame);
        child |= ChildMode;
        var col = child ? ChildColor : Color;
        // 12 Kanten: Paare, die sich in genau einem Bit unterscheiden
        for (int a = 0; a < 8; a++)
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                int b = a | bit;
                if (b != a) DebugLines.Line(c[a], c[b], col);
            }
        if (!child) Name((c[0] + c[7]) * 0.5f + new Vector3(0f, MathF.Abs(c[7].Y - c[0].Y) * 0.5f, 0f));
    }

    /// <summary>Lokale Box mit einer Render-Raum-Matrix (CarNode.World).</summary>
    public void BoxRender(Vector3 min, Vector3 max, Matrix4x4 world, bool child = false)
    {
        var off = RenderToGame(Vector3.Zero);   // Render → Spiel ist eine reine Verschiebung (MapStreamer.RenderToGame)
        Box(min, max, world * Matrix4x4.CreateTranslation(off), child);
    }

    /// <summary>Achsenkreuz (Objekt ohne Mesh): X rot, Y grün, Z blau wie Unity.</summary>
    public void Axes(Matrix4x4 toGame, float size = 0.5f)
    {
        var o = Vector3.Transform(Vector3.Zero, toGame);
        DebugLines.Line(o, Vector3.Transform(new Vector3(size, 0, 0), toGame), new Vector4(1f, 0.2f, 0.2f, 1f));
        DebugLines.Line(o, Vector3.Transform(new Vector3(0, size, 0), toGame), new Vector4(0.3f, 1f, 0.3f, 1f));
        DebugLines.Line(o, Vector3.Transform(new Vector3(0, 0, size), toGame), new Vector4(0.3f, 0.5f, 1f, 1f));
        Name(o + new Vector3(0f, size, 0f));
    }

    public void AxesRender(Matrix4x4 world, float size = 0.5f)
    {
        var off = RenderToGame(Vector3.Zero);
        Axes(world * Matrix4x4.CreateTranslation(off), size);
    }

    /// <summary>Name des Objekts über der Box (einmal je Bild).</summary>
    public void Name(Vector3 at)
    {
        if (ChildMode || LabelDone || Label.Length == 0) return;
        LabelDone = true;
        DebugLines.Label(at, Label, Color);
    }
}

/// <summary>Stabile Kennungen echter Engine-Objekte (über die Lebenszeit des Objekts, ohne es festzuhalten).</summary>
public static class EditorIds
{
    sealed class Box { public int Id; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Box> _ids = new();
    static int _next;

    public static string Of(object o) => "o:" + _ids.GetValue(o, _ => new Box { Id = ++_next }).Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
