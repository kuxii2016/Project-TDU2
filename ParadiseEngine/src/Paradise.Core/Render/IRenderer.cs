using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>
/// Abstraktion über das Grafik-Backend. Entspricht dem, was Unity bisher übernommen hat:
/// Texturen (Texture2D), Materialien (Material + Shader), Meshes und Zeichnen.
/// </summary>
public interface IRenderer : IDisposable
{
    /// <summary>Initialisiert Device/Swapchain für das gegebene Fenster-Handle (HWND) und die Größe.</summary>
    void Initialize(nint windowHandle, int width, int height);

    /// <summary>Reagiert auf Fenstergrößenänderung (Swapchain neu bauen).</summary>
    void Resize(int width, int height);

    /// <summary>Beginnt einen Frame (Command-Buffer, Render-Pass).</summary>
    void BeginFrame();

    /// <summary>Schließt den Frame ab und präsentiert (Swapchain present).</summary>
    void EndFrame();

    /// <summary>Name des aktiven Grafikgeräts (für Crash-Report/Diagnose).</summary>
    string DeviceName { get; }

    /// <summary>Seitenverhältnis der Swapchain (Breite/Höhe) für die Projektionsmatrix.</summary>
    float AspectRatio { get; }

    /// <summary>Kamera für den aktuellen Frame setzen (vor den Draw-Aufrufen der Module).</summary>
    void SetCamera(Matrix4x4 view, Matrix4x4 projection);

    /// <summary>Globale Shader-Werte des Frames (Kamera-, Sonnen-, Terrain-Parameter).</summary>
    void SetFrameGlobals(in FrameGlobals globals);

    /// <summary>Textur hochladen (komplette Mip-Kette). Per Dispose freigeben.</summary>
    IGpuTexture CreateTexture(TextureUpload texture);

    /// <summary>Inhalt einer Rgba8-Textur (Mip 0, gleiche Größe) ersetzen, ohne auf die GPU zu warten (z.B. Minimap).</summary>
    void UpdateTexture(IGpuTexture texture, ReadOnlySpan<byte> rgba);

    /// <summary>Material erzeugen. Referenzierte Texturen gehören dem Aufrufer.</summary>
    IGpuMaterial CreateMaterial(MaterialDesc desc);

    /// <summary>Texturen/Parameter eines Materials ersetzen (z. B. nachgereichte Texturen, Unity: Material.SetTexture).</summary>
    void UpdateMaterial(IGpuMaterial material, MaterialDesc desc);

    /// <summary>Ein Mesh (optional mit eigener Textur – Altpfad) zur GPU hochladen. Per Dispose freigeben.</summary>
    IGpuMesh CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, TextureUpload? texture = null);

    /// <summary>Objekt-Mesh (Vertexfarbe + 4 UV-Sätze) für TDU2/Object und TDU2/ObjectTransparent.</summary>
    IGpuMesh CreateMesh(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices);

    /// <summary>
    /// Mesh mit Material und Modellmatrix zeichnen (zwischen BeginFrame und EndFrame).
    /// Transparente Materialien werden gesammelt und am Frameende hinten→vorne gezeichnet (Unity: Queue Transparent).
    /// </summary>
    void DrawMesh(IGpuMesh mesh, IGpuMaterial? material, Matrix4x4 model);

    /// <summary>Mesh nur in die Schattenkarten zeichnen (nicht ins Bild), z. B. Stromleitungen.</summary>
    void DrawShadowOnly(IGpuMesh mesh, IGpuMaterial material, Matrix4x4 model);

    /// <summary>
    /// Dynamische Geometrie nur für dieses Bild zeichnen (Unity: Mesh.MarkDynamic + jedes Bild neue Vertices), z.B. Reifenspuren,
    /// Partikel. Die Daten werden kopiert; nach EndFrame freigegeben.
    /// </summary>
    void DrawDynamic(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material, Matrix4x4 model);

    /// <summary>Altpfad: Mesh mit seiner eigenen Textur (oder weiß) zeichnen.</summary>
    void DrawMesh(IGpuMesh mesh, Matrix4x4 model);

    /// <summary>
    /// 2D-Overlay (Debug-UI) zeichnen: Positionen in Pixeln (0,0 = links oben), Material vom Typ <see cref="MaterialKind.Overlay"/>.
    /// Wird nach allen 3D-Objekten ohne Tiefentest gezeichnet; die Daten werden kopiert.
    /// </summary>
    void DrawOverlay(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material);

    /// <summary>
    /// Wie <see cref="DrawOverlay"/>, aber über allem – auch über der Flash-Oberfläche (HUD, Minimap, Menüs): Entwicklermenü (F1).
    /// Ohne eigenen Durchgang des Backends wie DrawOverlay.
    /// </summary>
    void DrawOverlayTop(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material) => DrawOverlay(vertices, indices, material);

    /// <summary>
    /// Flash-Quads (VMF-Player, Shader TDU2/FlashQuad): je 4 Vertices ein Viereck (Position.xy in Pixeln, 0,0 = links oben;
    /// Uv0). Gezeichnet nach der Welt und vor dem Debug-Overlay, in Aufrufreihenfolge. mask = Maskenform (optional).
    /// </summary>
    void DrawFlash(ReadOnlySpan<ObjectVertex> quadVertices, IGpuTexture texture, IGpuTexture? mask, in FlashQuadParams p);

    /// <summary>Textur-Animation (.uva): _MainTex_ST / _DetailTex_ST eines Materials setzen (xy = Skalierung, zw = Versatz).</summary>
    void SetMaterialUv(IGpuMaterial material, Vector4 mainST, Vector4 detailST);

    /// <summary>
    /// Weltbild des nächsten gezeichneten Bildes (vor Flash/HUD) auf die CPU lesen, z.B. für ein Foto (MyTDULife, Unity: eigene Kamera
    /// mit RenderTexture + ReadPixels). <paramref name="centerSquare"/> = nur das mittige Quadrat (Höhe × Höhe). Die Antwort kommt
    /// einige Bilder später im Hauptthread (BeginFrame); null = Backend kann das nicht.
    /// </summary>
    void RequestSceneCapture(Action<SceneCapture?> done, bool centerSquare = false) => done(null);

    /// <summary>Zähler des zuletzt abgeschlossenen Frames und Gerätedaten.</summary>
    RenderStats Stats { get; }

    /// <summary>Fenstergröße in Pixeln (Swapchain).</summary>
    int Width { get; }
    int Height { get; }
}