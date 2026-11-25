// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceCheckpoint.cs).
using Paradise.Core.Formats;
using Paradise.Core.Math;
using System.Numerics;

namespace Paradise.Core.Racing;

/// <summary>Ein Checkpoint / Tor eines Rennens (aus <see cref="Ige2Checkpoint"/>). Alle Koordinaten in Inselkoordinaten.</summary>
public sealed class RaceCheckpoint
{
    public int Index;
    public Vector3 Position;    // Inselkoordinaten
    public Vector3 Forward;     // Durchfahrtsrichtung (+Z der Tor-Matrix)
    public Vector3 Right;       // quer zur Fahrtrichtung (+X der Tor-Matrix)
    public Vector3 Up;
    public float Width;         // Durchfahrtsbreite in m
    public int Kind;            // CHK2-Art: 0 normal, 1 Einzelfass (exe Typ 5 SF_Barel_D_act), 2 Fass-Tor (Typ 6 Gate_Main), 3 Bremszone (Typ 7)
    /// <summary>Länge der Matrix-Achsen (Right, Up, Forward) wie in der Datei. Die exe setzt das Tormodell mit genau dieser Matrix
    /// (0x980ee0 / 0x97e330) – ohne Anpassung an die Torbreite.</summary>
    public Vector3 AxisScale = Vector3.One;
    public bool OnRoad;         // Placement 0: an eine Straße gebunden (Spiel gibt dann seitlich Toleranz dazu)
    public int Road = -1, Piece = -1;   // Straßenverweis der CHK2 (nur OnRoad): IA-Straße und Abschnitt
    public bool IsFinish;       // letztes Tor der Route (wird beim Aufbau gesetzt)

    public Ige2GateKind GateKind => (Ige2GateKind)Kind;
    public bool IsStart => Index == 0;
    public bool IsBrakingZone => Kind == (int)Ige2GateKind.BrakingZone;

    /// <summary>
    /// Halbe Breite, innerhalb der die Durchfahrt zählt. Das Spiel erweitert Straßen-Checkpoints um
    /// DBEnvironnement.CheckpointAdditionalWidthAfterRoad (7 m) bzw. ...Offroad (11 m).
    /// </summary>
    public float HalfAcceptWidth(bool offroad)
    {
        float extra = OnRoad ? (offroad ? 11f : 7f) * 0.5f : 1.5f;
        return Mathf.Max(2.5f, Width * 0.5f) + extra;
    }

    /// <summary>Länge der Bremszone in Fahrtrichtung (im Spiel nicht gespeichert; vermutet: so tief wie breit).</summary>
    public float ZoneDepth => Mathf.Max(8f, Width);

    static float Len(Vector3 v) { float l = v.Length(); return l > 1e-4f ? l : 1f; }

    public static RaceCheckpoint From(Ige2Checkpoint cp, int index)
    {
        var f = cp.Forward; f.Y = 0f;
        if (f.LengthSquared() < 1e-6f) f = Vector3.UnitZ;
        f = Vector3.Normalize(f);
        var r = cp.Right; r.Y = 0f;
        if (r.LengthSquared() < 1e-6f) r = new Vector3(f.Z, 0f, -f.X);
        r = Vector3.Normalize(r);
        return new RaceCheckpoint
        {
            Index = index,
            Position = cp.Position,
            Forward = f,
            Right = r,
            Up = cp.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(cp.Up) : Vector3.UnitY,
            Width = cp.Width > 0.5f ? cp.Width : 10f,
            Kind = (int)cp.GateKind,
            OnRoad = cp.Placement == 0,
            Road = cp.Placement == 0 ? cp.Road : -1,
            Piece = cp.Placement == 0 ? cp.Piece : -1,
            AxisScale = new Vector3(Len(cp.Right), Len(cp.Up), Len(cp.Forward)),
        };
    }
}
