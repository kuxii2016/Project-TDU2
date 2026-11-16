using Paradise.Core.Audio;

namespace Paradise.Game.Config;

/// <summary>
/// Lautstärken (0..1). Werte nur hier im Code ändern (keine Datei, kein Speichern/Laden), wie LightingConfig.
/// Gesamtlautstärke = Master x Kanal.
/// </summary>
public sealed class AudioConfig
{
    /// <summary>Gesamtlautstärke aller Sounds (AudioEngine.MasterVolume).</summary>
    public float Master { get; set; } = 0.85f;
    /// <summary>Menümusik (frontend/…/music: LOGIN, MAP, LOBBY …).</summary>
    public float Music { get; set; } = 0.35f;
    /// <summary>Oberflächen-Sounds der Flash-Menüs (fe_snd.bnk).</summary>
    public float Interface { get; set; } = 0.4f;
    /// <summary>Fahrzeug (Motor, Reifen, Hupe …) – Unity TDU2VehicleManager.Volume.</summary>
    public float Vehicle { get; set; } = 0.34f;
    /// <summary>Reifenquietschen (Faktor auf den Fahrzeugsound).</summary>
    public float TireSqueal { get; set; } = 0.25f;
    /// <summary>Schaltgeräusche (Faktor auf den Fahrzeugsound).</summary>
    public float GearShift { get; set; } = 0.25f;
    /// <summary>Umgebungsgeräusche der Insel (ambiences.xmb: Stadt, Meer, Wald …, Regen) – Core/Audio/TDU2Ambience.</summary>
    public float Ambience { get; set; } = 0.45f;

    // ---------------- Autoradio (Unity TDU2Radio: Inspektor-Werte; Sender/Lautstärke gelten nur für die Sitzung)
    /// <summary>Sender beim Start: off, hariba, kalua (eigene MP3 in SharedAssets/Radio), roadtrack.</summary>
    public string RadioStation { get; set; } = "off";
    /// <summary>Radio-Lautstärke 0..11 (Ziffernblock 8/2).</summary>
    public int RadioVolume { get; set; } = 1;
    /// <summary>Original-Sender (Hariba / RoadRock) aus Euro/Bnk/sound/radio abspielen.</summary>
    public bool PlayOriginalRadio { get; set; } = true;
    /// <summary>Wahrscheinlichkeit Jingle / Moderation / Werbung zwischen zwei Songs.</summary>
    public float RadioJingleChance { get; set; } = 0.3f;
    public float RadioCommentChance { get; set; } = 0.2f;
    public float RadioAdChance { get; set; } = 0.1f;
    /// <summary>Laufschrift: sichtbare Zeichen, Sekunden je Schritt.</summary>
    public int RadioMarqueeLength { get; set; } = 15;
    public float RadioMarqueeSpeed { get; set; } = 0.3f;

    public void Apply()
    {
        AudioEngine.MasterVolume = Clamp(Master);
        FrontendMusic.Volume = Clamp(Music);
        FrontendSounds.Volume = Clamp(Interface);
    }

    static float Clamp(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
