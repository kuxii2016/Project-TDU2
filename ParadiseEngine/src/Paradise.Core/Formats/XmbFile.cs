using System.Globalization;
using System.Text;

namespace Paradise.Core.Formats;

/// <summary>Knoten eines XMB-Baums. Objekte haben <see cref="Children"/>, Arrays <see cref="Items"/>, Werte <see cref="Value"/>.</summary>
public sealed class XmbNode
{
    public string Name = "";
    public XmbType Type;
    public bool IsArray;
    public object? Value;                                  // bool, sbyte, short, int, long, byte, ushort, uint, ulong, float, double, string
    // Listen erst bei Bedarf anlegen: db_data.cpr hat Hunderttausende Blattknoten – je zwei leere Listen waren dort der
    // größte Teil des Speichers. Leere Knoten liefern eine gemeinsame leere Liste (nur lesen; befüllt wird nur in XmbFile).
    static readonly List<XmbNode> s_empty = new(0);
    List<XmbNode>? _children, _items;
    public List<XmbNode> Children => _children ?? s_empty;
    public List<XmbNode> Items => _items ?? s_empty;
    internal void AddChild(XmbNode n) => (_children ??= new List<XmbNode>(4)).Add(n);
    internal void AddItem(XmbNode n, int capacity) => (_items ??= new List<XmbNode>(capacity)).Add(n);

    public XmbNode? this[string name] => Get(name);

    public XmbNode? Get(string name)
    {
        foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
        return null;
    }

    public IEnumerable<XmbNode> GetAll(string name)
    {
        foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.Ordinal)) yield return c;
    }

    public double AsDouble(double def = 0) => Value switch
    {
        null => def,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def,
        _ => Convert.ToDouble(Value, CultureInfo.InvariantCulture),
    };

    public float AsFloat(float def = 0) => Value == null ? def : (float)AsDouble(def);
    public int AsInt(int def = 0) => Value == null ? def : (int)System.Math.Round(AsDouble(def));
    public ulong AsULong(ulong def = 0) => Value is ulong u ? u : Value is long l ? (ulong)l : Value == null ? def : (ulong)AsDouble(def);
    public bool AsBool(bool def = false) => Value is bool b ? b : Value == null ? def : AsDouble() != 0;
    public string? AsString(string? def = null) => Value as string ?? (Value != null ? Convert.ToString(Value, CultureInfo.InvariantCulture) : def);

    public float F(string child, float def = 0) { var c = Get(child); return c != null ? c.AsFloat(def) : def; }
    public int I(string child, int def = 0) { var c = Get(child); return c != null ? c.AsInt(def) : def; }
    public bool B(string child, bool def = false) { var c = Get(child); return c != null ? c.AsBool(def) : def; }
    public string? S(string child, string? def = null) { var c = Get(child); return c != null ? c.AsString(def) : def; }
    public ulong U(string child, ulong def = 0) { var c = Get(child); return c != null ? c.AsULong(def) : def; }

    /// <summary>Alle Zahlen eines Arrays (oder eines Objekts mit Zahlen-Kindern) als float.</summary>
    public float[] Floats()
    {
        var src = IsArray ? Items : Children;
        var r = new float[src.Count];
        for (int i = 0; i < r.Length; i++) r[i] = src[i].AsFloat();
        return r;
    }

    public override string ToString() => IsArray ? $"{Name}[{Items.Count}]" : Type == XmbType.Object ? $"{Name}{{{Children.Count}}}" : $"{Name}={Value}";
}

public enum XmbType { Object, Bool, SInt8, SInt16, SInt32, SInt64, UInt8, UInt16, UInt32, UInt64, Float, Double, String, Array, Virtual }

/// <summary>
/// "XMBF" – binäres XML-Format von TDU2 (Datenbank, Kameras, Sound-Setups, Entities). Portiert aus dem Unity-Projekt.
///   0x00 "XMBF", 0x08 Offset Deskriptor-Tabelle (Strings), 0x0C Offset Metadaten (Typtabelle, u32-Wörter),
///   0x10 Offset Daten, 0x14 Root-Name (relativ zur Deskriptor-Tabelle), 0x18 Root-Typindex.
///   Typwort: Bits 0..3 Typ, 4..14 Kindanzahl (Objekte), 15..31 Bytegröße.
///   Objekt-Typ t: danach je Kind 2 Wörter {Stringoffset, Typzeiger (Bit31 = Array, Bits 0..29 = Typindex)}.
///   Kinder liegen gepackt hintereinander (String = u32 Offset, Array = {u32 Anzahl, u32 Offset}, Virtual = {Typ, Offset}).
/// </summary>
public sealed class XmbFile
{
    private readonly byte[] _b;
    private readonly int _desc, _meta, _data;
    // Byte-Reihenfolge: .xmb/.cpr sind little-endian, die bigfile-Indizes (.map) und .sob meist big-endian.
    // Erkannt wird sie an der Deskriptor-Tabelle (+8), die immer 0x1C ist.
    private readonly bool _be;
    // Feldnamen je Offset nur einmal als string (sonst je Datensatz und Feld eine eigene Kopie)
    private readonly Dictionary<int, string> _names = new();
    private static readonly object s_true = true, s_false = false;

    private string Name(int o)
    {
        if (!_names.TryGetValue(o, out var s)) _names[o] = s = string.Intern(CStr(o));
        return s;
    }
    public XmbNode? Root { get; private set; }

    private XmbFile(byte[] b)
    {
        _b = b;
        _be = Bin.U32(b, 8) != 0x1C && Be.U32(b, 8) == 0x1C;
        _desc = I32(8);
        _meta = I32(12);
        _data = I32(16);
    }

    public static bool IsXmb(byte[]? b) => b != null && b.Length > 28 && b[0] == 'X' && b[1] == 'M' && b[2] == 'B' && b[3] == 'F';

    public static XmbNode Parse(byte[] b)
    {
        if (!IsXmb(b)) throw new FormatException("no valid XMBF file");
        var x = new XmbFile(b);
        int rootName = x.I32(20);
        int rootType = x.I32(24);
        x.Root = x.ReadValue(x.Name(x._desc + rootName), rootType, x._data, 0);
        return x.Root;
    }

    private uint U32(int o) => _be ? Be.U32(_b, o) : Bin.U32(_b, o);
    private int I32(int o) => _be ? Be.I32(_b, o) : Bin.I32(_b, o);
    private ushort U16(int o) => _be ? Be.U16(_b, o) : Bin.U16(_b, o);
    private short I16(int o) => _be ? Be.I16(_b, o) : Bin.I16(_b, o);
    private ulong U64(int o) => _be ? Be.U64(_b, o) : Bin.U64(_b, o);
    private long I64(int o) => _be ? Be.I64(_b, o) : System.BitConverter.ToInt64(_b, o);
    private float F32(int o) => _be ? Be.F32(_b, o) : Bin.F32(_b, o);
    private double F64(int o) => _be ? Be.F64(_b, o) : System.BitConverter.ToDouble(_b, o);

    private uint W(int i) => U32(_meta + 4 * i);

    private string CStr(int o)
    {
        if (o < 0 || o >= _b.Length) return "";
        int e = o;
        while (e < _b.Length && _b[e] != 0) e++;
        return Encoding.UTF8.GetString(_b, o, e - o);
    }

    private static int SizeOf(uint typeWord) => (int)((typeWord >> 15) & 0x1FFFF);

    private XmbNode ReadField(string name, uint typePtr, int addr, int depth)
    {
        int t = (int)(typePtr & 0x3FFFFFFF);
        if ((typePtr & 0x80000000u) == 0) return ReadValue(name, t, addr, depth);

        var arr = new XmbNode { Name = name, IsArray = true };
        if (addr + 8 > _b.Length) return arr;
        int n = I32(addr);
        int baseAddr = _data + I32(addr + 4);
        uint tw = W(t);
        int ty = (int)(tw & 0xF);
        arr.Type = (XmbType)ty;
        if (n < 0 || n > 1 << 22) return arr;
        for (int i = 0; i < n; i++)
        {
            XmbNode item;
            if (ty == 13 || ty == 14)
            {
                int p = baseAddr + 8 * i;
                item = ReadValue("", I32(p), _data + I32(p + 4), depth + 1);
            }
            else if (ty == 12)
            {
                int p = baseAddr + 4 * i;
                int so = I32(p);
                item = new XmbNode { Name = "", Type = XmbType.String, Value = so == -1 ? null : CStr(_data + so) };
            }
            else item = ReadValue("", t, baseAddr + SizeOf(tw) * i, depth + 1);
            arr.AddItem(item, n);
        }
        return arr;
    }

    private XmbNode ReadValue(string name, int t, int addr, int depth)
    {
        uint tw = W(t);
        int ty = (int)(tw & 0xF);
        var node = new XmbNode { Name = name, Type = (XmbType)ty };
        if (depth > 64 || addr < 0 || addr >= _b.Length) return node;
        switch (ty)
        {
            case 0:
                {
                    int n = (int)((tw >> 4) & 0x7FF);
                    int off = 0;
                    for (int i = 0; i < n; i++)
                    {
                        // Feld-Einträge: little-endian (Name, Typ), big-endian (Typ, Name).
                        uint fa = W(t + 2 + 2 * i);
                        uint fb = W(t + 3 + 2 * i);
                        int so = (int)(_be ? fb : fa);
                        uint tp = _be ? fa : fb;
                        string cn = Name(_desc + so);
                        try { node.AddChild(ReadField(cn, tp, addr + off, depth + 1)); }
                        catch (Exception) { /* abgeschnittene Daten am Dateiende ignorieren */ }
                        if ((tp & 0x80000000u) != 0) off += 8;
                        else
                        {
                            uint ctw = W((int)(tp & 0x3FFFFFFF));
                            off += (ctw & 0xF) == 12 ? 4 : SizeOf(ctw);
                        }
                    }
                    break;
                }
            case 1: node.Value = _b[addr] != 0 ? s_true : s_false; break;
            case 2: node.Value = (sbyte)_b[addr]; break;
            case 3: node.Value = I16(addr); break;
            case 4: node.Value = I32(addr); break;
            case 5: node.Value = I64(addr); break;
            case 6: node.Value = _b[addr]; break;
            case 7: node.Value = U16(addr); break;
            case 8: node.Value = U32(addr); break;
            case 9: node.Value = U64(addr); break;
            case 10: node.Value = F32(addr); break;
            case 11: node.Value = F64(addr); break;
            case 12:
                {
                    int so = I32(addr);
                    node.Value = so == -1 ? null : CStr(_data + so);
                    break;
                }
            case 13:
            case 14:
                {
                    // Virtual: {Typindex, Datenoffset}
                    int ti = I32(addr);
                    return ReadValue(name, ti, _data + I32(addr + 4), depth + 1);
                }
        }
        return node;
    }
}

/// <summary>
/// .cpr = mit XTEA verschlüsselte Datei (db_data.cpr, Physics.cpr, GamePC.cpr ...).
/// Variante aus TDU2: 8 Byte Seed vorne, danach Blöcke; Klartext[i] = Dec(Cipher[i+1]) XOR Cipher[i].
/// Schlüssel "Other" (alle .cpr außer Savegames).
/// </summary>
public static class CprCrypto
{
    private static readonly uint[] KeyOther = { 0x4FE23C4Au, 0x80BAC211u, 0x6917BD3Au, 0xF0528EBDu };

    public static byte[] Decrypt(byte[]? buf)
    {
        if (buf == null || buf.Length < 16) return Array.Empty<byte>();
        int blocks = (buf.Length - 8) >> 3;
        var outp = new byte[blocks * 8];
        for (int b = 0; b < blocks; b++)
        {
            int o = b * 8;
            uint y = Bin.U32(buf, o + 8), z = Bin.U32(buf, o + 12);
            uint sum = 3337565984u;
            for (int i = 0; i < 32; i++)
            {
                z -= (y + ((y >> 5) ^ (y << 4))) ^ (sum + KeyOther[(sum >> 11) & 3]);
                sum += 1640531527u;
                y -= (z + ((z >> 5) ^ (z << 4))) ^ (sum + KeyOther[sum & 3]);
            }
            y ^= Bin.U32(buf, o);
            z ^= Bin.U32(buf, o + 4);
            outp[o] = (byte)y; outp[o + 1] = (byte)(y >> 8); outp[o + 2] = (byte)(y >> 16); outp[o + 3] = (byte)(y >> 24);
            outp[o + 4] = (byte)z; outp[o + 5] = (byte)(z >> 8); outp[o + 6] = (byte)(z >> 16); outp[o + 7] = (byte)(z >> 24);
        }
        return outp;
    }
}
