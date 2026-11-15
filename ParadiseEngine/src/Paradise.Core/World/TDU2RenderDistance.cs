namespace Paradise.Core.World;

/// <summary>Sichtweite der Objekte (Gebäude, Schilder, Bäume …) relativ zur Grundeinstellung.</summary>
public enum RenderDistance { VeryHigh, High, Normal, Low, VeryLow }

/// <summary>
/// Objekt-Sichtweite (Portiert aus Unity TDU2RenderDistance): Faktor auf die LOD-Wechsel (<see cref="RenderObject.LodBias"/>, wie
/// QualitySettings.lodBias) und auf die Distanzen der Bäume/Vegetation (<see cref="PlacementRenderer.DistanceMultiplier"/>).
/// Very High ×4, High ×2, Normal ×1, Low ÷2, Very Low ÷4. Die Stufe kommt aus der Config (Game/Config/GraphicsConfig.cs) bzw. den Optionen.
/// Die Grafikoption "LOD" setzt ihren Grundwert über <see cref="SetBaseLodBias"/>.
/// </summary>
public static class TDU2RenderDistance
{
    public static readonly string[] Names = { "Very High (x4)", "High (x2)", "Normal", "Low (/2)", "Very Low (/4)" };

    static RenderDistance _level = RenderDistance.Normal;
    static float _base = 1f;

    public static float MultiplierOf(RenderDistance l) => l switch
    {
        RenderDistance.VeryHigh => 4f,
        RenderDistance.High => 2f,
        RenderDistance.Low => 0.5f,
        RenderDistance.VeryLow => 0.25f,
        _ => 1f,
    };

    public static RenderDistance Level
    {
        get => _level;
        set { _level = value; Apply(); }
    }

    public static float Multiplier => MultiplierOf(_level);

    /// <summary>Grundwert des LOD-Bias (ohne Sichtweiten-Faktor), z.B. aus den Grafikoptionen.</summary>
    public static void SetBaseLodBias(float bias) { _base = bias; Apply(); }

    static void Apply()
    {
        float m = Multiplier;
        RenderObject.LodBias = _base * m;
        PlacementRenderer.DistanceMultiplier = m;
    }
}
