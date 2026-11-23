using Paradise.Core.Diagnostics;

namespace Paradise.ProfilerUI;

/// <summary>Farben und Schriften wie der dunkle Unity-Profiler.</summary>
internal static class ProfilerTheme
{
    public static readonly Color Back = Color.FromArgb(56, 56, 56);
    public static readonly Color Panel = Color.FromArgb(45, 45, 45);
    public static readonly Color PanelDark = Color.FromArgb(35, 35, 35);
    public static readonly Color Graph = Color.FromArgb(40, 40, 40);
    public static readonly Color Grid = Color.FromArgb(70, 70, 70);
    public static readonly Color Border = Color.FromArgb(25, 25, 25);
    public static readonly Color Text = Color.FromArgb(210, 210, 210);
    public static readonly Color TextDim = Color.FromArgb(140, 140, 140);
    public static readonly Color Selection = Color.FromArgb(62, 95, 150);
    public static readonly Color RowAlt = Color.FromArgb(50, 50, 50);
    public static readonly Color Cursor = Color.FromArgb(235, 235, 235);
    public static readonly Color Gc = Color.FromArgb(220, 60, 60);

    public static readonly Font UiFont = new("Segoe UI", 8.25f);
    public static readonly Font UiBold = new("Segoe UI", 8.25f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 7.5f);
    public static readonly Font Mono = new("Consolas", 8.25f);

    /// <summary>Kategorie-Farben (Unity: Rendering grün, Scripts blau, Physics orange …).</summary>
    public static Color Category(ProfilerCategory c) => c switch
    {
        ProfilerCategory.Rendering => Color.FromArgb(120, 190, 35),
        ProfilerCategory.Scripts => Color.FromArgb(60, 145, 210),
        ProfilerCategory.Physics => Color.FromArgb(235, 140, 20),
        ProfilerCategory.Animation => Color.FromArgb(70, 200, 200),
        ProfilerCategory.UI => Color.FromArgb(200, 110, 210),
        ProfilerCategory.Streaming => Color.FromArgb(230, 200, 60),
        ProfilerCategory.Audio => Color.FromArgb(150, 120, 230),
        ProfilerCategory.Network => Color.FromArgb(50, 170, 130),
        ProfilerCategory.GarbageCollector => Color.FromArgb(220, 60, 60),
        ProfilerCategory.Wait => Color.FromArgb(200, 200, 75),
        ProfilerCategory.Profiler => Color.FromArgb(100, 100, 100),
        _ => Color.FromArgb(130, 130, 130),
    };

    static readonly Color[] s_series =
    {
        Color.FromArgb(120, 190, 35), Color.FromArgb(60, 145, 210), Color.FromArgb(235, 140, 20), Color.FromArgb(200, 110, 210),
        Color.FromArgb(70, 200, 200), Color.FromArgb(230, 200, 60), Color.FromArgb(220, 80, 80), Color.FromArgb(150, 120, 230),
        Color.FromArgb(50, 170, 130), Color.FromArgb(190, 190, 190),
    };

    public static Color Series(int i) => s_series[((i % s_series.Length) + s_series.Length) % s_series.Length];

    public static Color Darker(Color c, float f = 0.65f) => Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
}
