using System.Numerics;
using System.Text;

namespace Paradise.Core.Formats;

/// <summary>
/// Schreibt eine Challenge im .ig2-Format (IGE2, big-endian) – Gegenstück zu <see cref="Ige2File.Parse"/>.
/// Die exe serialisiert eine Challenge als feste Folge von MDSC-Tags (Felder des Challenge-Objekts, registriert in exe 0xb380f0 ff.:
/// gmth +0x98, isla +0x238, leme +0x2e8, forc +0x10e0, ChTR +0x428, trat +0xbc0, nblp +0xb18, offR +0x1b68, NbCp +0x3a00,
/// mnme +0x7a8, DesC +0x878 …) und den Toren als ENTI/CHK2-Datensätze. Damit Reihenfolge, Flags, Gruppen und Längen aller Tags
/// genau wie im Spiel sind, dient eine Original-ig2 derselben Spielart als Vorlage; nur die Werte der Challenge werden ersetzt.
/// </summary>
public static class Ige2Writer
{
    /// <summary>
    /// Ein Tor: Position (Inselkoordinaten), Fahrtrichtung, Breite. <see cref="OnRoad"/> = Placement 0 wie die Original-Tore:
    /// Straße/Stück/Bézier-Abschnitt des IA-Netzes (ROAD_DETAIL), Seite 1 = in Straßenrichtung (Lage t), 0 = gegen sie (Lage 1 - t);
    /// Position = Kurvenpunkt + 0,10 m, Vorwärtsachse = Kurventangente in Fahrtrichtung, Hochachse um die Querneigung der Straße
    /// gerollt (geprüft an allen Toren der Original-ig2). Ohne Straßenbezug Placement 1 (frei).
    /// </summary>
    public readonly record struct Gate(Vector3 Position, Vector3 Forward, float Width, uint Kind)
    {
        public bool OnRoad { get; init; }
        public int Road { get; init; }
        public int Piece { get; init; }
        public int Segment { get; init; }
        public int Side { get; init; }
        public float Along { get; init; }
        /// <summary>Hochachse (leer = senkrecht).</summary>
        public Vector3 Up { get; init; }
    }

    /// <summary>
    /// Vorlage + neue Werte -> Datei. <paramref name="ints"/>: 4-Byte-Tags (Name ohne führende Nullbytes, "#" = Rundkurs),
    /// <paramref name="texts"/>: 64-Byte-Texte (mnme, DesC). Tags, die die Vorlage nicht hat, werden ignoriert.
    /// </summary>
    public static byte[] Build(byte[] template, IReadOnlyDictionary<string, int> ints, IReadOnlyDictionary<string, string> texts, IReadOnlyList<Gate> gates,
        IReadOnlyCollection<string>? clear = null)
    {
        if (!Ige2File.LooksLikeIge2(template)) throw new InvalidDataException("Vorlage ist keine IGE2-Datei");
        int mdsc = Be.I32(template, 4);
        int msize = Be.I32(template, mdsc + 4);
        int mcount = Be.I32(template, mdsc + 8);
        int mEnd = System.Math.Min(mdsc + msize, template.Length);

        using var ms = new MemoryStream();
        ms.Write(template, 0, mdsc);   // Kopf (IGE2, Offset, Version …) wie die Vorlage

        // MDSC: Tags der Vorlage in derselben Reihenfolge, Werte ersetzt
        var tags = new MemoryStream();
        int p = mdsc + 12, n = 0;
        for (int i = 0; i < mcount && p + 8 <= mEnd; i++, n++)
        {
            int len = Be.U16(template, p + 6);
            string name = Encoding.ASCII.GetString(template, p, 4).Trim('\0');
            var data = new byte[len];
            Array.Copy(template, p + 8, data, 0, System.Math.Min(len, template.Length - (p + 8)));
            // gesetzte Werte zuerst, danach die zu leerenden Werte der Vorlage (die zu deren Strecke/Prüfung gehören)
            if (len == 4 && ints.TryGetValue(name, out int v)) PutI32(data, 0, v);
            else if (len == 8 && ints.TryGetValue(name, out int v8)) { PutI32(data, 0, 0); PutI32(data, 4, v8); }
            else if (texts.TryGetValue(name, out var txt))
            {
                Array.Clear(data);
                var tb = Encoding.ASCII.GetBytes(txt);
                Array.Copy(tb, data, System.Math.Min(tb.Length, len - 1));
            }
            else if (clear != null && clear.Contains(name)) Array.Clear(data);
            tags.Write(template, p, 8);
            tags.Write(data, 0, len);
            p += 8 + len;
        }
        WriteTag(ms, "MDSC"); WriteI32(ms, 12 + (int)tags.Length); WriteI32(ms, n);
        tags.Position = 0; tags.CopyTo(ms);

        // ENTI: CHK2 je Tor – Kopf 12 Byte (+20 Byte Straßenbezug bei Placement 0), Matrix 64 Byte, Art/Flag/Breite/4 Nachfolger 28 Byte
        int entiSize = 12;
        foreach (var g in gates) entiSize += g.OnRoad ? 124 : 104;
        WriteTag(ms, "ENTI"); WriteI32(ms, entiSize); WriteI32(ms, gates.Count);
        foreach (var g in gates)
        {
            var f = g.Forward;
            var up = g.Up;
            if (up.LengthSquared() < 1e-6f) { up = Vector3.UnitY; f.Y = 0f; }
            f = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitZ;
            up = Vector3.Normalize(up);
            var right = Vector3.Normalize(Vector3.Cross(up, f));
            WriteTag(ms, "CHK2");
            if (g.OnRoad)
            {
                WriteI32(ms, 124); WriteI32(ms, 0);
                WriteI32(ms, g.Road); WriteI32(ms, g.Piece); WriteI32(ms, g.Segment); WriteI32(ms, g.Side); WriteF(ms, g.Along);
            }
            else { WriteI32(ms, 104); WriteI32(ms, 1); }
            // Matrix zeilenweise: (rechts.x oben.x vorn.x pos.x / … y … / … z … / -0 -0 -0 1) wie die Original-Tore
            WriteF(ms, right.X); WriteF(ms, up.X); WriteF(ms, f.X); WriteF(ms, g.Position.X);
            WriteF(ms, right.Y); WriteF(ms, up.Y); WriteF(ms, f.Y); WriteF(ms, g.Position.Y);
            WriteF(ms, right.Z); WriteF(ms, up.Z); WriteF(ms, f.Z); WriteF(ms, g.Position.Z);
            WriteF(ms, -0f); WriteF(ms, -0f); WriteF(ms, -0f); WriteF(ms, 1f);
            WriteI32(ms, (int)g.Kind); WriteI32(ms, 0); WriteF(ms, g.Width);
            for (int k = 0; k < 4; k++) WriteI32(ms, -1);
        }
        return ms.ToArray();
    }

    static void PutI32(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void WriteI32(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void WriteF(Stream s, float f) => WriteI32(s, BitConverter.SingleToInt32Bits(f));
    static void WriteTag(Stream s, string t) { var b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, 4); }
}
