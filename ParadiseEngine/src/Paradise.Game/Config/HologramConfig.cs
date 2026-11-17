namespace Paradise.Game.Config;

/// <summary>
/// Spot holograms for the free ride; change the values only here in the code.
/// </summary>
public static class HologramConfig
{
    public static bool Enabled = true;
    /// <summary>Radius around the camera (m) and maximum number at one time (Spec: 300 m, the next 12).</summary>
    public static float Radius = 300f;
    public static int MaxCount = 12;
    /// <summary>Scale of the panels (Spec: 1 = actual size, frame 10 m high).</summary>
    public static float Scale = 1f;
    /// <summary>The plate points toward the camera with a local +Z axis; true = rotated 180° (if the front side is facing the wrong way).</summary>
    public static bool FlipFacing = false;
}
