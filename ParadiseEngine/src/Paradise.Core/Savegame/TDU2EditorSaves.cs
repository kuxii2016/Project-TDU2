#nullable enable
using Paradise.Core.Diagnostics;

namespace Paradise.Core.Savegame
{
    /// <summary>
    /// Eigene Challenges des Editors im Spielstand wie im Original: eigener Speicher-Kontext "IG2" ("GAME EDITOR" /
    /// "Game Mode Editor Files", exe-Tabelle 0x166ab74 neben PLAYERSAVE, LICENCES, PHOTOS, AVATAR, STICKERS) mit den Dateien
    /// "Ige/%04d%02d%02d%02d%02d%02d.ig2" (exe 0x78a170). Abgelegt wie PLAYERSAVE/DATA unter Saves/&lt;Profil&gt;/IG2/Ige/….
    /// Gespeichert wird beim Speichern des Spielstands (exe 0xacdb40 -> Spielstand-Verwaltung 0x4e10e0).
    /// </summary>
    public static class TDU2EditorSaves
    {
        const string Container = "IG2";
        /// <summary>Höchstzahl eigener Challenges (exe 0xacd40e, /:NbChallMax).</summary>
        public const int Max = 64;

        public static string Folder(string? nick)
        {
            string n = string.IsNullOrEmpty(nick) ? "_offline" : nick!;
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return Path.Combine(TDU2SaveGame.Folder, n, Container);
        }

        /// <summary>Dateiname einer neuen Challenge wie exe 0x78a170 (Jahr vierstellig, Monat, Tag, Stunde, Minute, Sekunde).</summary>
        public static string NewName(DateTime t) =>
            "Ige/" + t.Year.ToString("0000") + t.Month.ToString("00") + t.Day.ToString("00") + t.Hour.ToString("00") + t.Minute.ToString("00") + t.Second.ToString("00") + ".ig2";

        /// <summary>Alle eigenen Challenges (Namen relativ zum Container, z.B. "Ige/20261120203000.ig2"), älteste zuerst.</summary>
        public static List<string> List(string? nick)
        {
            var res = new List<string>();
            try
            {
                string dir = Path.Combine(Folder(nick), "Ige");
                if (!Directory.Exists(dir)) return res;
                foreach (var f in Directory.GetFiles(dir, "*.ig2")) res.Add("Ige/" + Path.GetFileName(f));
                res.Sort(StringComparer.Ordinal);
            }
            catch (Exception ex) { Log.Warn("Editor-Challenges: Liste nicht lesbar: " + ex.Message); }
            return res;
        }

        public static int Count(string? nick) => List(nick).Count;

        public static byte[]? Read(string? nick, string name)
        {
            try
            {
                string path = Path.Combine(Folder(nick), name.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex) { Log.Warn("Editor-Challenge " + name + " nicht lesbar: " + ex.Message); return null; }
        }

        /// <summary>Schreiben (über eine Zwischendatei, damit ein Abbruch keine halbe Datei hinterlässt). true = gespeichert.</summary>
        public static bool Write(string? nick, string name, byte[] data)
        {
            try
            {
                string path = Path.Combine(Folder(nick), name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Log.Info("Editor-Challenge gespeichert: " + path + " (" + data.Length + " Byte)");
                return true;
            }
            catch (Exception ex) { Log.Warn("Editor-Challenge " + name + " nicht gespeichert: " + ex.Message); return false; }
        }

        public static bool Delete(string? nick, string name)
        {
            try
            {
                string path = Path.Combine(Folder(nick), name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) { Log.Warn("Editor-Challenge " + name + " nicht gelöscht: " + ex.Message); return false; }
        }
    }
}
