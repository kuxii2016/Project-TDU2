using Paradise.Core.Diagnostics;
using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>
/// Platzhalter-Renderer ohne Grafikausgabe (Start mit --nullrenderer). Hält die
/// Engine lauffähig (Loop, Timing, Physik, Netzwerk lassen sich damit testen).
/// </summary>
public sealed class NullRenderer : IRenderer
{
    public string DeviceName => "NullRenderer (keine Grafikausgabe)";

    public void Initialize(nint windowHandle, int width, int height)
        => Log.Info($"NullRenderer init: hwnd=0x{windowHandle:X}, {width}x{height}");

    public void Resize(int width, int height) => Log.Debug($"NullRenderer resize: {width}x{height}");
    public void BeginFrame() { }
    public void EndFrame() { }
    public void Dispose() { }

    public float AspectRatio => 16f / 9f;
    public void SetCamera(Matrix4x4 view, Matrix4x4 projection) { }
    public void SetFrameGlobals(in FrameGlobals globals) { }
    public IGpuTexture CreateTexture(TextureUpload texture) => new NullTexture(texture.Width, texture.Height, texture.Name, texture.Format);
    public IGpuMaterial CreateMaterial(MaterialDesc desc) => new NullMaterial(desc.Kind);
    public void UpdateMaterial(IGpuMaterial material, MaterialDesc desc) { }
    public IGpuMesh CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, TextureUpload? texture = null) => new NullMesh(indices.Length);
    public IGpuMesh CreateMesh(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices) => new NullMesh(indices.Length);
    public void DrawMesh(IGpuMesh mesh, IGpuMaterial? material, Matrix4x4 model) { }
    public void DrawMesh(IGpuMesh mesh, Matrix4x4 model) { }
    public void DrawShadowOnly(IGpuMesh mesh, IGpuMaterial material, Matrix4x4 model) { }
    public void UpdateTexture(IGpuTexture texture, ReadOnlySpan<byte> rgba) { }
    public void DrawDynamic(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material, Matrix4x4 model) { }
    public void DrawFlash(ReadOnlySpan<ObjectVertex> quadVertices, IGpuTexture texture, IGpuTexture? mask, in FlashQuadParams p) { }
    public void SetMaterialUv(IGpuMaterial material, Vector4 mainST, Vector4 detailST) { }
    public void DrawOverlay(ReadOnlySpan<ObjectVertex> vertices, ReadOnlySpan<uint> indices, IGpuMaterial material) { }
    public RenderStats Stats { get; } = new() { DeviceName = "NullRenderer", Api = "none" };
    public int Width => 1280;
    public int Height => 720;

    private sealed class NullMesh : IGpuMesh
    {
        public NullMesh(int indexCount) => IndexCount = indexCount;
        public int IndexCount { get; }
        public void Dispose() { }
    }

    private sealed class NullTexture : IGpuTexture
    {
        public NullTexture(int w, int h, string? n, GpuTextureFormat f) { Width = w; Height = h; Name = n; Format = f; }
        public int Width { get; }
        public int Height { get; }
        public string? Name { get; }
        public GpuTextureFormat Format { get; }
        public void Dispose() { }
    }

    private sealed class NullMaterial : IGpuMaterial
    {
        public NullMaterial(MaterialKind k) => Kind = k;
        public MaterialKind Kind { get; }
        public void Dispose() { }
    }
}
