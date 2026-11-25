namespace Paradise.Core.World;

/// <summary>Sichtweite der Objekte (Gebäude, Schilder, Bäume …) relativ zur Grundeinstellung.</summary>
public enum RenderDistanceLevel { VeryHigh, High, Normal, Low, VeryLow }

/// <summary>
/// Objekt-Sichtweite (Portiert aus Unity TDU2RenderDistance): Faktor auf die LOD-Wechsel (<see cref="RenderObject.LodBias"/>, wie
/// QualitySettings.lodBias) und auf die Distanzen der Bäume/Vegetation (<see cref="PlacementRenderer.DistanceMultiplier"/>).
/// Very High ×4, High ×2, Normal ×1, Low ÷2, Very Low ÷4. Die Stufe kommt aus der Config (Game/Config/GraphicsConfig.cs) bzw. den Optionen.
/// Die Grafikoption "LOD" setzt ihren Grundwert über <see cref="SetBaseLodBias"/>.
/// </summary>
public static class RenderDistance
{
    public static readonly string[] Names = { "Very High (x4)", "High (x2)", "Normal", "Low (/2)", "Very Low (/4)" };

    static RenderDistanceLevel _level = RenderDistanceLevel.Normal;
    static float _base = 1f;

    public static float MultiplierOf(RenderDistanceLevel l) => l switch
    {
        RenderDistanceLevel.VeryHigh => 4.5f,
        RenderDistanceLevel.High => 2.2f,
        RenderDistanceLevel.Low => 0.6f,
        RenderDistanceLevel.VeryLow => 0.3f,
        _ => 1f,
    };

    public static RenderDistanceLevel Level
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