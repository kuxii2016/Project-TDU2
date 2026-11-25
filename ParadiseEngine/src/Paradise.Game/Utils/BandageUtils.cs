using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Online;
using Paradise.Game.Screens;

namespace Paradise.Game.Utils;

public static class BandageUtils
{
    /// <summary>Tragedauer des Verbands in Sekunden Echtzeit (exe 0xBF0A2B: 0xE10).</summary>
    public const long DurationSeconds = 3600;

    public const int TextSurgeryLocked = 4405, TextHairdresserLocked = 7169, TextRemoved = 7472, TextStillBandaged = 5756;

    static SurgeryData? Data => TDU2Session.Save?.surgery;

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Verband sitzt (und ist nicht abgelaufen).</summary>
    public static bool Active { get { var d = Data; return d != null && d.bandage && !Expired(d); } }

    /// <summary>exe 0x672C90.</summary>
    public static bool Expired(SurgeryData d)
    {
        if (!d.bandage) return false;
        long now = Now;
        return d.bandageTime <= now || d.bandageTime > now + DurationSeconds;
    }

    /// <summary>Restzeit in Sekunden (0 ohne Verband) – Karte "surgery_locked" zeigt sie im Original als Minuten (0xBD73AC).</summary>
    public static long Remaining { get { var d = Data; return d != null && d.bandage ? System.Math.Max(0L, d.bandageTime - Now) : 0L; } }

    static string Text(int id, string fallback) => FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, id) ?? fallback;

    /// <summary>Hinweis im Spiel (general.bnk pop_news).</summary>
    public static void Notify(int textId, string fallback)
    {
        string t = Text(textId, fallback);
        GeneralOverlay.SendNews(t);
    }

    /// <summary>
    /// "surg:cine:intr" (0xBF1568): die Teile sichern, die der Verband ersetzt – Helm (0xBF157E: Platz 6 Satz 0, 0xBF15BA: Satz 1),
    /// Brille (Platz 5), Frisur (Platz 0), Bart (Platz 8), jeweils mit Farbe.
    /// </summary>
    public static void SaveWorn(AvatarConfig cfg)
    {
        var d = Data; if (d == null) return;
        static (int hash, int color) W(AvatarConfig c, ClothePart p) { var w = c.Worn(p); return w != null && !w.IsNothing ? (w.Hash, c.ColorOf(w.Hash)) : (0, 0); }
        (d.helmetBandage, d.helmetColorBandage) = W(cfg, ClothePart.Helmet);
        (d.bikeHelmetBandage, d.bikeHelmetColorBandage) = W(cfg, ClothePart.Helmet);   // zweiter Kleidungssatz (fahren) – in der Engine derselbe
        (d.glassesBandage, d.glassesColorBandage) = W(cfg, ClothePart.Glasses);
        (d.hairBandage, d.hairColorBandage) = W(cfg, ClothePart.Hair);
        (d.beardBandage, d.beardColorBandage) = W(cfg, ClothePart.Beard);
    }

    /// <summary>Helm und Brille abnehmen, damit das Gesicht frei ist (0xBF0AE0: M_/W_NO_HELMET, M_/W_NO_GLASSES).</summary>
    public static void TakeOffHeadwear(AvatarConfig cfg)
    {
        cfg.Remove(ClothePart.Helmet);
        cfg.Remove(ClothePart.Glasses);
    }

    /// <summary>
    /// Verband anlegen (0xBF07C0): Helm-Platz = Verband, Brille/Frisur/Bart = nichts; Ablaufzeit setzen. <paramref name="cfg"/> wird geändert.
    /// </summary>
    public static void PutOn(AvatarConfig cfg)
    {
        var d = Data;
        var bandage = AvatarLibrary.ClotheByFile(cfg.Sex == 1 ? "W_HE_BANDAGE" : "M_HE_BANDAGE");
        cfg.Remove(ClothePart.Helmet);
        cfg.Remove(ClothePart.Glasses);
        cfg.Remove(ClothePart.Hair);
        cfg.Remove(ClothePart.Beard);
        if (bandage != null) cfg.Set(bandage, 0);
        else Log.Warn("DBClothe " + (cfg.Sex == 1 ? "W_HE_BANDAGE" : "M_HE_BANDAGE") + " missing");
        if (d == null) return;
        d.bandage = true;
        d.bandageTime = Now + DurationSeconds;
    }

    /// <summary>
    /// Abnehmen, wenn abgelaufen (0x672D30): Bandage = 0, NeedBandageNews = 1, gesicherte Teile zurück auf den Avatar, speichern.
    /// true = abgenommen.
    /// </summary>
    public static bool RemoveIfExpired()
    {
        var d = Data;
        if (d == null || !Expired(d)) return false;
        d.bandage = false;
        d.needBandageNews = true;
        var cfg = AvatarConfigs.Local;
        cfg.Remove(ClothePart.Helmet);
        Restore(cfg, d.helmetBandage, d.helmetColorBandage);
        cfg.Remove(ClothePart.Glasses);
        Restore(cfg, d.glassesBandage, d.glassesColorBandage);
        cfg.Remove(ClothePart.Hair);
        Restore(cfg, d.hairBandage, d.hairColorBandage);
        cfg.Remove(ClothePart.Beard);
        Restore(cfg, d.beardBandage, d.beardColorBandage);
        AvatarConfigs.SetLocal(cfg);
        return true;
    }

    static void Restore(AvatarConfig cfg, int hash, int color)
    {
        var c = hash != 0 ? AvatarLibrary.Clothe(hash) : null;
        if (c != null && !c.IsNothing) cfg.Set(c, color);
    }

    /// <summary>Beim Start eines Spielmodus (Original 0xA7EA2A im Basis-Start aller Modi): abgelaufenen Verband abnehmen, Meldung zeigen.</summary>
    public static void OnModeStart(Gamemode mode)
    {
        if (mode is Gamemode.GAME_MODE_PAUSE or Gamemode.GAME_MODE_LOGIN or Gamemode.GAME_MODE_BOOTMENU) return;
        try
        {
            RemoveIfExpired();
            var d = Data;
            if (d != null && d.needBandageNews && mode is Gamemode.GAME_MODE_FREERIDE or Gamemode.GAME_MODE_LIVING)
            {
                d.needBandageNews = false;
                var msg = Paradise.Game.Gamemodes.InteriorUtils.Lang(7472, "Your bandages have been removed!");
                Notify(TextRemoved, msg);
                if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            }
        }
        catch (Exception ex) { Log.Error("Bandage: " + ex.Message); }
    }
}
