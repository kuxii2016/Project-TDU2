using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>
/// Globale Spiegelungen (Unity Shader.SetGlobal*): Bodenspiegelung im Interieur (InteriorFx.InteriorMirror, _TDU2PlanarRefl)
/// und Echtzeit-Reflexionssonde am Fahrzeug (TDU2VehicleManager.MirrorReflections, unity_SpecCube0). Gesetzt vom Spiel, gelesen
/// vom Renderer in EndFrame. Koordinaten im Render-Raum.
/// </summary>
public static class RenderFx
{
    /// <summary>Bodenspiegelung an (Interieur mit Spiegelboden) und Höhe der Ebene (Render-Raum).</summary>
    public static bool PlanarOn;
    public static float PlanarY;
    /// <summary>Unity InteriorFx.ReflectionScale: Auflösung des Spiegelbilds relativ zum Bildschirm.</summary>
    public static float PlanarScale = 0.5f;
    /// <summary>Diagnose (F11 im Interieur): spiegelnde Flächen zeigen nur das Spiegelbild (kann wieder raus).</summary>
    public static bool DebugPlanar;

    /// <summary>
    /// Spiegelbild des Meeres (Original TD_Ocean SEAREF_N, ocean_spec §4): Szene an der Wasserebene gespiegelt, ohne Himmel,
    /// gelöscht mit ocean_reflectionColor. Nur wenn keine Bodenspiegelung (Interieur) aktiv ist. Ebene im Render-Raum.
    /// </summary>
    public static bool OceanOn;
    /// <summary>Wolkenkuppel (fx/clouds.bnk, World/TDU2Clouds): Objekt aus clouds_cloud3dname der aktuellen und der vorigen
    /// Wetterart, Überblendung 0..1 (0 = nur vorige), Drehgeschwindigkeit clouds_cloud3dSpeed (vom Wetter gesetzt).</summary>
    public static string CloudName = "", CloudNamePrev = "";
    public static float CloudBlend = 1f;
    public static Vector2 CloudSpeed;
    public static float OceanY;
    public static Vector3 OceanClear = new(0.12f, 0.23f, 0.18f);

    /// <summary>Reflexionssonde an (Unity: am eigenen Auto, 1,1 m über dem Ursprung) und ihre Position (Render-Raum).</summary>
    public static bool ProbeOn;
    public static Vector3 ProbePos;
    /// <summary>Unity probe.farClipPlane.</summary>
    public static float ProbeFar = 600f;
    /// <summary>
    /// Sonde an fester Stelle in einer unbewegten Umgebung (Interieur: Raummitte, ohne Autos): die 6 Seiten einmal rendern und
    /// dann stehen lassen, bis sich Stelle oder <see cref="ProbeVersion"/> ändert (z.B. Möbel nachgeladen). Gleiches Bild wie
    /// laufend neu rendern, aber ohne den Szenen-Durchgang je Bild.
    /// </summary>
    public static bool ProbeStatic;
    public static int ProbeVersion;
    /// <summary>Vom Renderer: statische Sonde ist fertig (Streamer muss dafür keine Objekte außerhalb der Sicht mehr behalten).</summary>
    public static bool ProbeSettled;

    /// <summary>
    /// Vorschaubild im Kauffenster des Händlers (Unity TDU2CarDealer.UpdateLure: eigene Kamera -> RenderTexture -> Flash-Clip
    /// window_buy/lure). Kamera im Render-Raum (Ansicht/Projektion wie die Hauptkamera), Größe in Pixeln. Der Renderer legt das
    /// Ziel an, rendert die gesammelten opaken Objekte und den Himmel hinein und stellt es als <see cref="LureTexture"/> bereit.
    /// </summary>
    public static bool LureOn;
    public static Matrix4x4 LureView, LureProj;
    public static Vector3 LurePos;
    public static int LureWidth = 512, LureHeight = 384;
    public static IGpuTexture? LureTexture;

    /// <summary>Vulkan-Projektion (Y nach unten) mit Reversed-Z wie TerrainModule.PerspectiveReversedZ.</summary>
    public static Matrix4x4 PerspectiveReversedZ(float fovYDeg, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovYDeg * MathF.PI / 360f);
        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, near / (far - near), -1f,
            0f, 0f, far * near / (far - near), 0f);
    }
}
