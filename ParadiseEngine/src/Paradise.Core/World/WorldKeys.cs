namespace Paradise.Core.World;

/// <summary>Area-Koordinate (area-X-Z). Portiert 1:1 aus dem Unity-Projekt.</summary>
public readonly struct AreaKey : IEquatable<AreaKey>
{
    public readonly int X, Z;
    public AreaKey(int x, int z) { X = x; Z = z; }
    public bool Equals(AreaKey o) => X == o.X && Z == o.Z;
    public override bool Equals(object? obj) => obj is AreaKey o && Equals(o);
    public override int GetHashCode() => X * 397 ^ Z;
    public override string ToString() => $"area-{X}-{Z}";
}

/// <summary>Sektor "sector-A-B-C-D": A/B = Area, C = Spalte (X), D = Zeile (Z) innerhalb der Area.</summary>
public readonly struct SectorKey : IEquatable<SectorKey>
{
    public readonly int A, B, C, D;
    public SectorKey(int a, int b, int c, int d) { A = a; B = b; C = c; D = d; }
    public AreaKey Area => new(A, B);
    public bool Equals(SectorKey o) => A == o.A && B == o.B && C == o.C && D == o.D;
    public override bool Equals(object? obj) => obj is SectorKey o && Equals(o);
    public override int GetHashCode() => ((A * 31 + B) * 31 + C) * 31 + D;
    public override string ToString() => $"sector-{A}-{B}-{C}-{D}";
}