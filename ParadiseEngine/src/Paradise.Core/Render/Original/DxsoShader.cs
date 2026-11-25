using System.Text;

namespace Paradise.Core.Render.Original;

public sealed class DxsoShader
{
    public bool IsPixel;
    public int Major, Minor;
    public uint[] Tokens = Array.Empty<uint>();      // ganzer Strom inkl. Versions- und END-Token
    public readonly List<Constant> Constants = new();
    public readonly List<Decl> Decls = new();

    /// <summary>CTAB-Eintrag (Strukturen wie Var_PS werden in ihre Member aufgelöst: Register = Start + Summe der Vorgänger).</summary>
    public sealed class Constant
    {
        public string Name = "", Struct = "";
        public int RegisterSet;   // 0 bool, 1 int4, 2 float4, 3 sampler
        public int Register, Count;
        /// <summary>D3DXPARAMETER_CLASS: 0 Skalar, 1 Vektor, 2 Matrix (Zeilen), 3 Matrix (Spalten), 5 Struktur.</summary>
        public int Class;
        public override string ToString() => (Struct.Length > 0 ? Struct + "." : "") + Name + " " + "bics"[System.Math.Clamp(RegisterSet, 0, 3)] + Register + "x" + Count;
    }

    /// <summary>dcl: Eingang/Ausgang mit D3DDECLUSAGE, Sampler mit Typ (2 = 2D, 3 = Würfel, 4 = Volumen).</summary>
    public sealed class Decl
    {
        public int RegType, Register, Usage, UsageIndex, SamplerType, WriteMask;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out DxsoShader? shader, out string error)
    {
        shader = null; error = "";
        if (data.Length < 8) { error = "zu kurz"; return false; }
        uint ver = BitConverter.ToUInt32(data);
        if ((ver & 0xFFFE0000) != 0xFFFE0000) { error = "kein D3D9-Shader"; return false; }
        int n = 0, off = 4;
        // Länge bis END-Token
        var toks = new List<uint> { ver };
        while (off + 4 <= data.Length)
        {
            uint t = BitConverter.ToUInt32(data.Slice(off));
            toks.Add(t); off += 4;
            if (t == 0x0000FFFF) break;
            if ((t & 0xFFFF) == 0xFFFE)
            {
                int len = (int)((t >> 16) & 0x7FFF);
                for (int k = 0; k < len && off + 4 <= data.Length; k++) { toks.Add(BitConverter.ToUInt32(data.Slice(off))); off += 4; }
            }
            if (++n > 200000) { error = "kein END-Token"; return false; }
        }
        if (toks[^1] != 0x0000FFFF) { error = "kein END-Token"; return false; }
        var s = new DxsoShader { IsPixel = (ver >> 16) == 0xFFFF, Major = (int)((ver >> 8) & 0xFF), Minor = (int)(ver & 0xFF), Tokens = toks.ToArray() };
        s.ParseComments();
        shader = s;
        return true;
    }

    /// <summary>Bytegröße eines Shaders ab <paramref name="data"/>[0] (bis einschließlich END), 0 = ungültig.</summary>
    public static int Size(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return 0;
        uint ver = BitConverter.ToUInt32(data);
        if ((ver & 0xFFFE0000) != 0xFFFE0000) return 0;
        int off = 4;
        while (off + 4 <= data.Length)
        {
            uint t = BitConverter.ToUInt32(data.Slice(off));
            if (t == 0x0000FFFF) return off + 4;
            off += (t & 0xFFFF) == 0xFFFE ? 4 + (int)((t >> 16) & 0x7FFF) * 4 : 4;
        }
        return 0;
    }

    void ParseComments()
    {
        int i = 1;
        while (i < Tokens.Length)
        {
            uint t = Tokens[i];
            if (t == 0x0000FFFF) break;
            if ((t & 0xFFFF) == 0xFFFE)
            {
                int len = (int)((t >> 16) & 0x7FFF);
                if (len >= 8 && Tokens[i + 1] == 0x42415443) ParseCtab(i + 2, len - 1);   // "CTAB"
                i += 1 + len;
                continue;
            }
            int op = (int)(t & 0xFFFF);
            int ilen = (int)((t >> 24) & 0xF);
            if (op == 31 && i + 2 < Tokens.Length)   // dcl
            {
                uint u = Tokens[i + 1], d = Tokens[i + 2];
                Decls.Add(new Decl
                {
                    RegType = DxsoToGlsl.RegType(d), Register = (int)(d & 0x7FF),
                    Usage = (int)(u & 0x1F), UsageIndex = (int)((u >> 16) & 0xF), SamplerType = (int)((u >> 27) & 0xF),
                    WriteMask = (int)((d >> 16) & 0xF),
                });
            }
            i += 1 + ilen;
        }
    }

    void ParseCtab(int start, int lenTokens)
    {
        var b = new byte[lenTokens * 4];
        Buffer.BlockCopy(Tokens, start * 4, b, 0, b.Length);
        if (b.Length < 28) return;
        int count = BitConverter.ToInt32(b, 12), infoOff = BitConverter.ToInt32(b, 16);
        string Str(int p) { if (p <= 0 || p >= b.Length) return ""; int e = p; while (e < b.Length && b[e] != 0) e++; return Encoding.ASCII.GetString(b, p, e - p); }
        for (int k = 0; k < count; k++)
        {
            int o = infoOff + k * 20;
            if (o + 20 > b.Length) break;
            string name = Str(BitConverter.ToInt32(b, o));
            int set = BitConverter.ToUInt16(b, o + 4), reg = BitConverter.ToUInt16(b, o + 6), cnt = BitConverter.ToUInt16(b, o + 8);
            int ti = BitConverter.ToInt32(b, o + 12);
            int members = ti + 16 <= b.Length ? BitConverter.ToUInt16(b, ti + 10) : 0;
            int memOff = ti + 16 <= b.Length ? BitConverter.ToInt32(b, ti + 12) : 0;
            int ccls = ti >= 0 && ti + 2 <= b.Length ? BitConverter.ToUInt16(b, ti) : 0;
            if (members == 0) { Constants.Add(new Constant { Name = name, RegisterSet = set, Register = reg, Count = cnt, Class = ccls }); continue; }
            int r = reg, end = reg + cnt;
            for (int m = 0; m < members && r < end; m++)
            {
                int mo = memOff + m * 8;
                if (mo + 8 > b.Length) break;
                string mn = Str(BitConverter.ToInt32(b, mo));
                int mt = BitConverter.ToInt32(b, mo + 4);
                int rows = BitConverter.ToUInt16(b, mt + 4), cols = BitConverter.ToUInt16(b, mt + 6), el = System.Math.Max(1, (int)BitConverter.ToUInt16(b, mt + 8));
                int cls = BitConverter.ToUInt16(b, mt);
                int regs = cls >= 2 ? (cls == 3 ? cols : rows) * el : el;   // Matrix (Spalten-Hauptordnung: Spaltenzahl) bzw. Array
                regs = System.Math.Max(1, System.Math.Min(regs, end - r));
                Constants.Add(new Constant { Name = mn, Struct = name, RegisterSet = set, Register = r, Count = regs, Class = cls });
                r += regs;
            }
        }
    }

    public Constant? Find(string name) => Constants.Find(c => c.Name == name);
}