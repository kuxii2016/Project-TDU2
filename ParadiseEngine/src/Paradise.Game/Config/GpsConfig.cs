using Paradise.Core.Vehicles;

namespace Paradise.Game.Config;

/// <summary>
/// GPS (Unity TDU2GpsSettings). Werte nur hier im Code ändern; angewendet beim Laden der Fahrzeuge (VehicleModule).
/// Ansagen: Nummer der Datei VFGPSVoiceNNN_.wav in Sound/Voices/&lt;Sprache&gt;/GPS.bnk, 0 = keine (Unity-Grundwerte: alle 0).
/// </summary>
public static class GpsConfig
{
    public static bool Voice = true;
    public static float VoiceVolume = 1f;
    /// <summary>Sprache der Ansagen ("ge", "us", "fr" …); leer = Spielsprache.</summary>
    public static string VoiceLanguage = "ge";
    public static int VoiceTargetSet = 0;
    // Ziel erreicht: VFGPSVoice016 – im Original (0xCA3C0C -> 0xAEE6F0) unter 50 m Abstand zum Ziel
    public static int VoiceArrived = 16;
    // Abbiegen aus unpacked.exe (0xCFB43D -> 0xCFAFB0): Ansage erst ab 200 m vor der Kreuzung ([+0x1D00] = 200) beim
    // Unterschreiten von 200 m -> 002/004 ("gleich"), beim Unterschreiten von 50 m ([+0x1D04] = 50) -> 003/005 ("jetzt").
    // Seite nach Kreuzungswinkel (< 121° bzw. > 241°); welche Seite welche Datei ist, steht nicht im Code -> im Log
    // ("GPS-Hinweis") prüfen und bei Bedarf Links/Rechts tauschen.
    public static int VoiceTurnLeftSoon = 4;
    public static int VoiceTurnRightSoon = 2;
    public static int VoiceTurnLeftNow = 5;
    public static int VoiceTurnRightNow = 3;
    // 001: im Original nach 10 s Abweichung (0xCFC066), einmalig bis zur Rückkehr auf die Route
    public static int VoiceUTurn = 1;
    /// <summary>Ziel erreicht unter diesem Abstand (m).</summary>
    public static float ArriveDistance = 50f;
    /// <summary>Breite des Pfads relativ zum Sichtradius der Minimap.</summary>
    public static float PathWidth = 0.020f;
    /// <summary>Farbe des Pfads auf Minimap und großer Karte (R, G, B). Unity 255/150/20 ist kaum von den Feldwegen (181/120/2) zu
    /// unterscheiden -> rot.</summary>
    public static byte PathR = 230, PathG = 30, PathB = 30;
    /// <summary>Öffnungswinkel des Richtungs-Kranzes (Grad bis ganz dunkel).</summary>
    public static float RingSpread = 30f;
    public static bool Miles = false;
    /// <summary>Entfernungsansagen wie im Original-Spiel (VFGPSVoice006–010 metrisch, 011–015 Meilen, aus unpacked.exe).</summary>
    public static bool DistanceVoices = true;
    /// <summary>Höchstabstand des Restwegs zur Schwelle für eine Ansage (m).</summary>
    public static float DistanceVoiceTolerance = 150f;

    public static void Apply()
    {
        TDU2GpsSettings.Voice = Voice;
        TDU2GpsSettings.VoiceVolume = VoiceVolume;
        TDU2GpsSettings.VoiceLanguage = VoiceLanguage;
        TDU2GpsSettings.VoiceTargetSet = VoiceTargetSet;
        TDU2GpsSettings.VoiceArrived = VoiceArrived;
        TDU2GpsSettings.VoiceTurnLeftSoon = VoiceTurnLeftSoon;
        TDU2GpsSettings.VoiceTurnRightSoon = VoiceTurnRightSoon;
        TDU2GpsSettings.VoiceTurnLeftNow = VoiceTurnLeftNow;
        TDU2GpsSettings.VoiceTurnRightNow = VoiceTurnRightNow;
        TDU2GpsSettings.VoiceUTurn = VoiceUTurn;
        TDU2GpsSettings.ArriveDistance = ArriveDistance;
        TDU2GpsSettings.PathWidth = PathWidth;
        TDU2GpsSettings.PathColor = new Paradise.Core.Flash.Color32(PathR, PathG, PathB, 255);
        TDU2GpsSettings.RingSpread = RingSpread;
        TDU2GpsSettings.Miles = Miles;
        TDU2GpsSettings.DistanceVoices = DistanceVoices;
        TDU2GpsSettings.DistanceVoiceTolerance = DistanceVoiceTolerance;
    }
}
