using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Ampeln (World Actor SF_Xin_*, Entity__Actor__TrafficLights): das Modell hat die Leuchten HALO_R, HALO_O, HALO_G
/// als eigene Instanzen – ohne Steuerung leuchten alle drei. Hier läuft ein fester Zyklus:
///   Grün -> Gelb -> Rot (die querende Richtung jeweils um eine halbe Periode versetzt).
/// Zeitbasis ist die Serveruhr: alle Clients sehen dieselbe Phase ohne eigene Netzwerkpakete.
/// Der Verkehr fragt <see cref="MustStop"/>. Portiert aus dem Unity-Projekt (TDU2TrafficLights).
/// </summary>
public static class TrafficLights
{
    public sealed class Signal
    {
        public Func<bool> Alive = () => true;
        public Vector3 Pos;       // Spielkoordinaten
        public Vector3 Axis;      // Blickachse der Ampel (waagerecht, normiert)
        public int Group;         // 0 / 1: die beiden Fahrtrichtungen einer Kreuzung
        public float Offset;      // Versatz je Kreuzung
        public RenderObject[] R = Array.Empty<RenderObject>(), O = Array.Empty<RenderObject>(), G = Array.Empty<RenderObject>();
        public int Shown = -1;
    }

    public static float GreenTime = 14f, AmberTime = 3f, AllRedTime = 2f;
    /// <summary>Raster, in dem Ampeln zur selben Kreuzung (gleicher Versatz) gezählt werden (m).</summary>
    public static float JunctionCell = 70f;
    public static bool SwapAxis = false;
    static float Half => GreenTime + AmberTime + AllRedTime;
    static float Cycle => 2f * Half;

    static readonly List<Signal> _all = new();
    public static int Count => _all.Count;

    /// <summary>Serverzeit in Sekunden (bis zum Online-Teil: UTC-Uhr, für alle Clients gleich).</summary>
    public static Func<double> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>0 = Grün, 1 = Gelb, 2 = Rot.</summary>
    public static int StateOf(Signal s)
    {
        double t = (Clock() + s.Offset) % Cycle;
        if (t < 0) t += Cycle;
        if (s.Group == 1) t = (t + Half) % Cycle;
        if (t < GreenTime) return 0;
        if (t < GreenTime + AmberTime) return 1;
        return 2;
    }

    /// <summary>Eine gebaute Ampel anmelden (objects = Objekte der Instanz, world = Instanzmatrix in Spielkoordinaten).</summary>
    public static bool Register(IReadOnlyList<RenderObject> objects, Matrix4x4 world, Func<bool> alive)
    {
        var r = new List<RenderObject>(); var o = new List<RenderObject>(); var g = new List<RenderObject>();
        foreach (var ro in objects)
        {
            string n = (ro.Name ?? "").ToUpperInvariant();
            // HALO_x = Lichtschein (Sprite), R/O/G = leuchtende Linsenscheibe (SF_TLigh_HaloA) – beides nur bei aktiver Farbe
            var dst = n.Contains("HALO_R") || n == "R" ? r : n.Contains("HALO_O") || n == "O" ? o : n.Contains("HALO_G") || n == "G" ? g : null;
            if (dst != null && !dst.Contains(ro)) dst.Add(ro);
        }
        if (r.Count == 0 && o.Count == 0 && g.Count == 0) return false;
        var pos = world.Translation;
        var ax = Vector3.TransformNormal(Vector3.UnitZ, world); ax.Y = 0f;   // holder.forward
        ax = ax.LengthSquared() > 1e-6f ? Vector3.Normalize(ax) : Vector3.UnitZ;
        if (SwapAxis) ax = new Vector3(ax.Z, 0f, -ax.X);
        int cx = (int)MathF.Floor(pos.X / JunctionCell), cz = (int)MathF.Floor(pos.Z / JunctionCell);
        uint h;
        unchecked
        {
            h = (uint)(cx * 73856093) ^ (uint)(cz * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        }
        var s = new Signal
        {
            Alive = alive,
            Pos = pos,
            Axis = ax,
            Group = MathF.Abs(ax.X) > MathF.Abs(ax.Z) ? 1 : 0,
            Offset = (h % 10000) / 10000f * Cycle,
            R = r.ToArray(),
            O = o.ToArray(),
            G = g.ToArray(),
        };
        _all.Add(s);
        Show(s, StateOf(s));
        return true;
    }

    static void Show(Signal s, int state)
    {
        s.Shown = state;
        foreach (var x in s.G) x.Active = state == 0;
        foreach (var x in s.O) x.Active = state == 1;
        foreach (var x in s.R) x.Active = state == 2;
    }

    /// <summary>Jedes Bild: Leuchten umschalten, abgebaute Sektoren entfernen.</summary>
    public static void Tick()
    {
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var s = _all[i];
            if (!s.Alive()) { _all.RemoveAt(i); continue; }
            int st = StateOf(s);
            if (st != s.Shown) Show(s, st);
        }
    }

    public static void Clear() => _all.Clear();

    /// <summary>
    /// Muss ein Fahrzeug an pos (Spielkoordinaten) mit Fahrtrichtung fwd vor einer Ampel halten? dist = Abstand zur Ampel.
    /// Gelb: nur halten, wenn noch genug Weg zum Bremsen bleibt.
    /// </summary>
    public static bool MustStop(Vector3 pos, Vector3 fwd, float look, float speed, out float dist)
    {
        dist = float.MaxValue;
        fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-6f || _all.Count == 0) return false;
        fwd = Vector3.Normalize(fwd);
        bool stop = false;
        foreach (var s in _all)
        {
            if (!s.Alive()) continue;
            var d = s.Pos - pos; d.Y = 0f;
            float f = Vector3.Dot(d, fwd);
            if (f < 1.5f || f > look) continue;
            float lat = MathF.Abs(d.X * fwd.Z - d.Z * fwd.X);
            if (lat > 10f) continue;
            if (MathF.Abs(Vector3.Dot(s.Axis, fwd)) < 0.7f) continue;   // Ampel der querenden Richtung
            int st = StateOf(s);
            if (st == 0) continue;
            if (st == 1 && speed * speed / (2f * 4f) > f - 3f) continue;   // Gelb, zu nah zum Halten -> fahren
            if (f < dist) { dist = f; stop = true; }
        }
        return stop;
    }
}
