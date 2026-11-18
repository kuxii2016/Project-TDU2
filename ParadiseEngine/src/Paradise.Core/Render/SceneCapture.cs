namespace Paradise.Core.Render;

/// <summary>
/// Weltbild eines Bildes auf der CPU (<see cref="IRenderer.RequestSceneCapture"/>): fertig gerendert, vor Flash/HUD und vor der
/// Photoshop-Nachbearbeitung. Rgba8, Zeile 0 = oben (wie das Bild auf dem Schirm), Breite × Höhe × 4 Bytes.
/// </summary>
public sealed class SceneCapture
{
    public int Width, Height;
    public byte[] Rgba = System.Array.Empty<byte>();
}