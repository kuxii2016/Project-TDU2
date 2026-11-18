using System.Numerics;

namespace Paradise.Core.Formats;

/// <summary>A settings tag in the MDSC block of an .ig2 file (e.g., “gmth” = game mode, “nblp” = rounds).</summary>
public sealed class Ige2Tag
{
    public string Name = "";
    public byte Flags;
    public byte Group;
    public byte[]? Data;

    public bool HasValue => Data != null && Data.Length >= 4;
    public uint AsUInt => HasValue ? Be.U32(Data!, 0) : 0;
    public int AsInt => HasValue ? Be.I32(Data!, 0) : 0;
    public float AsFloat => HasValue ? Be.F32(Data!, 0) : 0f;
    /// <summary>8-byte values (make/model pairs such as VBrM, CvBr); for 4 bytes, the int value.</summary>
    public long AsLong => Data != null && Data.Length >= 8 ? ((long)Be.U32(Data, 0) << 32) | Be.U32(Data, 4) : AsInt;
    /// <summary>64-byte texts (mnme, DesC).</summary>
    public string AsText
    {
        get
        {
            if (Data == null) return "";
            int n = 0; while (n < Data.Length && Data[n] != 0) n++;
            return System.Text.Encoding.ASCII.GetString(Data, 0, n);
        }
    }
}

/// <summary>Checkpoint type (CHK2 word 0). Order as specified in the Challenge Editor's type table in the EXE
/// (“Checkpoint Normal,” “Checkpoint Cone,” “Checkpoint Gate,” “Braking Zone”).</summary>
public enum Ige2GateKind : uint { Normal = 0, Cones = 1, Gate = 2, BrakingZone = 3 }

/// <summary>A checkpoint gate (CHK2) in a challenge: position, orientation, and clearance width.</summary>
public sealed class Ige2Checkpoint
{
    public int Placement;               // 0 = auf einer Straße (road/piece), 1 = frei gesetzt
    public int Road, Piece, Segment, Side;
    public float Along;                 // Lage auf dem Straßenstück
    public Vector3 Position;            // Weltposition (Inselkoordinaten)
    public Vector3 Right, Up, Forward;  // Achsen aus der Transformationsmatrix (+Z zeigt die Fahrtrichtung)
    public uint GateKind;               // 0 Linie, 1 Kegel-Tor, 2 Tor (Bogen), 3 Bremszone
    public uint GateFlag;
    public float Width;                 // Durchfahrtsbreite in m
    public readonly int[] Links = new int[4];
    public Ige2GateKind Kind => (Ige2GateKind)GateKind;
}

/// <summary>
/// Decorative object (DEC2) in a racing school test. Word 0 = Type:
///   0 single cone, 1/2 start/end of a line of cones, 3 single barrier, 4/5 start/end of a line of barriers.
/// (Verified across all 529 .ig2 files: every 1 is followed by a 2, every 4 by a 5; type names “Cone,” “ConeLine,”
/// “Separ,” “SeparLine” from the type table of the Challenge Editor in the EXE.)
/// </summary>
public sealed class Ige2Decor
{
    public int Placement;
    public uint Kind;
    public Vector3 Position;
    public Vector3 Right, Up, Forward;
}

/// <summary>Vehicle (VEHI) in a test, e.g., the cars in the passing tests. Settings as MDSC tags:
/// VSph = Car ID (DBCarPhysicsData._id_car), Spmx = Maximum speed (mm/s), Stch = Checkpoint from which it starts (presumed).</summary>
public sealed class Ige2Vehicle
{
    public int Placement;
    public int Road, Piece;
    public Vector3 Position;
    public Vector3 Right, Up, Forward;
    public readonly Dictionary<string, Ige2Tag> Settings = new();
    public Ige2Tag? Setting(string n) => Settings.TryGetValue(n, out var t) ? t : null;
    public int CarId => Setting("VSph")?.AsInt ?? 0;
    public float MaxSpeed => (Setting("Spmx")?.AsInt ?? 0) / 1000f;     // m/s
    public int StartCheckpoint => Setting("Stch")?.AsInt ?? 0;
}

/// <summary>
/// .ig2 - Challenge/race track definition (big-endian).
///   “IGE2” u32 version, @0x1C ‘MDSC’ { u32 size, u32 count, tags }, followed by “ENTI” { u32 size, u32 count, records }.
///   Tag:    char[4] Name, u8 Flags, u8 Group, u16 Length, Data.
///   Record: char[4] Type, u32 Size, u32 Placement; if Placement=0, u32 road/piece/segment/side + f32 along;
///           followed by f32[16] transformation matrix (rows: axes | translation), then type-dependent fields.
///   CHK2:   u32 GateKind, u32 Flag, f32 Width, i32[4] Successors.
///   DEC2:   u32 Type, u32 0.
///   VEHI:   u32 50, u32 ?, u32 n, n MDSC tags, i32 -1 x 17.
/// The tag named “\0\0\0#” (here “#”) is 1 for circuit races (confirmed in all racing school exams
/// whose descriptions mention “lap” or “circuit”).
/// </summary>
public sealed class Ige2File
{
    public int Version;
    public readonly Dictionary<string, Ige2Tag> Settings = new();
    public readonly List<Ige2Checkpoint> Checkpoints = new();
    public readonly List<Ige2Decor> Decors = new();
    public readonly List<Ige2Vehicle> Vehicles = new();
    public readonly List<string> RecordKinds = new();

    public int ConeCount => Decors.Count;
    public int VehicleCount => Vehicles.Count;

    public Ige2Tag? Setting(string name) => Settings.TryGetValue(name, out var t) ? t : null;
    public int Int(string name, int def = 0) { var t = Setting(name); return t != null && t.HasValue ? t.AsInt : def; }
    public uint Mode => Setting("gmth")?.AsUInt ?? 0;
    public int Island => Setting("isla")?.AsInt ?? -1;
    public int Laps => Setting("nblp")?.AsInt ?? 0;
    public uint Length => Setting("leme")?.AsUInt ?? 0;
    /// <summary>Circuit (Day “#” = 1): The finish line is back at the starting line, <see cref="Laps"/> laps.</summary>
    public bool IsCircuit => Int("#") == 1;

    public static bool LooksLikeIge2(byte[]? b) => b != null && b.Length >= 0x20 && Be.TagIs(b, 0, "IGE2");

    public static Ige2File Parse(byte[] b)
    {
        var f = new Ige2File();
        if (!LooksLikeIge2(b)) return f;
        f.Version = Be.I32(b, 8);
        int mdsc = Be.I32(b, 4);
        if (mdsc < 0 || mdsc + 12 > b.Length || !Be.TagIs(b, mdsc, "MDSC")) return f;
        int msize = Be.I32(b, mdsc + 4);
        int mcount = Be.I32(b, mdsc + 8);
        int mEnd = System.Math.Min(mdsc + msize, b.Length);
        ReadTags(b, mdsc + 12, mEnd, mcount, f.Settings);

        int enti = mEnd;
        if (enti + 12 > b.Length || !Be.TagIs(b, enti, "ENTI")) return f;
        int esize = Be.I32(b, enti + 4);
        int ecount = Be.I32(b, enti + 8);
        int q = enti + 12, eEnd = System.Math.Min(enti + esize, b.Length);
        for (int i = 0; i < ecount && q + 12 <= eEnd; i++)
        {
            string kind = Be.Tag(b, q);
            int rsize = Be.I32(b, q + 4);
            int place = Be.I32(b, q + 8);
            f.RecordKinds.Add(kind);
            int baseP = q + 12 + (place == 0 ? 20 : 0);
            if (rsize <= 0 || q + rsize > b.Length) break;
            if (baseP + 64 <= q + rsize)
            {
                if (kind == "CHK2") f.ReadCheckpoint(b, q, baseP, place, q + rsize);
                else if (kind == "DEC2") f.ReadDecor(b, baseP, place, q + rsize);
                else if (kind == "VEHI") f.ReadVehicle(b, q, baseP, place, q + rsize);
            }
            q += rsize;
        }
        return f;
    }

    /// <summary>Read MDSC tags. Leading null bytes are removed from names (“\0\0\0#” -> “#”); the unnamed header tag is omitted.</summary>
    static void ReadTags(byte[] b, int p, int end, int count, Dictionary<string, Ige2Tag> into)
    {
        for (int i = 0; i < count && p + 8 <= end; i++)
        {
            int len = Be.U16(b, p + 6);
            string raw = Be.Tag(b, p);
            var tag = new Ige2Tag { Name = raw.Trim('\0'), Flags = b[p + 4], Group = b[p + 5] };
            if (len > 0 && p + 8 + len <= b.Length)
            {
                tag.Data = new byte[len];
                Array.Copy(b, p + 8, tag.Data, 0, len);
            }
            if (tag.Name.Length > 0 && !into.ContainsKey(tag.Name)) into[tag.Name] = tag;
            p += 8 + len;
        }
    }

    static void ReadAxes(byte[] b, int baseP, out Vector3 pos, out Vector3 right, out Vector3 up, out Vector3 fwd)
    {
        pos = new Vector3(Be.F32(b, baseP + 12), Be.F32(b, baseP + 28), Be.F32(b, baseP + 44));
        right = new Vector3(Be.F32(b, baseP + 0), Be.F32(b, baseP + 16), Be.F32(b, baseP + 32));
        up = new Vector3(Be.F32(b, baseP + 4), Be.F32(b, baseP + 20), Be.F32(b, baseP + 36));
        fwd = new Vector3(Be.F32(b, baseP + 8), Be.F32(b, baseP + 24), Be.F32(b, baseP + 40));
    }

    void ReadCheckpoint(byte[] b, int q, int baseP, int place, int recEnd)
    {
        var cp = new Ige2Checkpoint { Placement = place };
        if (place == 0)
        {
            cp.Road = Be.I32(b, q + 12);
            cp.Piece = Be.I32(b, q + 16);
            cp.Segment = Be.I32(b, q + 20);
            cp.Side = Be.I32(b, q + 24);
            cp.Along = Be.F32(b, q + 28);
        }
        ReadAxes(b, baseP, out cp.Position, out cp.Right, out cp.Up, out cp.Forward);
        if (baseP + 92 <= recEnd)
        {
            cp.GateKind = Be.U32(b, baseP + 64);
            cp.GateFlag = Be.U32(b, baseP + 68);
            cp.Width = Be.F32(b, baseP + 72);
            for (int i = 0; i < 4; i++) cp.Links[i] = Be.I32(b, baseP + 76 + i * 4);
        }
        Checkpoints.Add(cp);
    }

    void ReadDecor(byte[] b, int baseP, int place, int recEnd)
    {
        var d = new Ige2Decor { Placement = place };
        ReadAxes(b, baseP, out d.Position, out d.Right, out d.Up, out d.Forward);
        if (baseP + 68 <= recEnd) d.Kind = Be.U32(b, baseP + 64);
        Decors.Add(d);
    }

    void ReadVehicle(byte[] b, int q, int baseP, int place, int recEnd)
    {
        var v = new Ige2Vehicle { Placement = place };
        if (place == 0) { v.Road = Be.I32(b, q + 12); v.Piece = Be.I32(b, q + 16); }
        ReadAxes(b, baseP, out v.Position, out v.Right, out v.Up, out v.Forward);
        int p = baseP + 64;
        if (p + 12 <= recEnd)
        {
            int n = Be.I32(b, p + 8);
            ReadTags(b, p + 12, recEnd, n, v.Settings);
        }
        Vehicles.Add(v);
    }
}