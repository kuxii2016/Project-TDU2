namespace Paradise.Core.Config;

/// <summary>
/// Darstellung der Avatare (Körper, Kopf, Kleidung).
/// </summary>
public static class AvatarRenderConfig
{
    /// <summary>
    /// Stufen gekrümmter Unterteilung (PN-Dreiecke, SkinPrim.Subdivided) beim Laden: 0 = Original-Meshes, 1 = jedes Dreieck x4,
    /// 2 = x16. Haare/Bärte werden nie unterteilt. Kosten: CPU-Häutung je Bild wächst mit der Vertex-Zahl.
    /// </summary>
    public static int Subdivisions = 1;
}
