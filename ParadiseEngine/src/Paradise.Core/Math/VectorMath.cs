using System.Numerics;

namespace Paradise.Core.Math;

/// <summary>
/// Unity-ähnliche Helfer rund um System.Numerics.Vector3 / Quaternion.
/// Die Engine nutzt System.Numerics als Basistyp (SIMD-beschleunigt);
/// diese Helfer liefern die aus Unity gewohnten Methoden/Konstanten nach,
/// damit die Portierung nah am Original bleibt.
/// Achtung: System.Numerics nutzt Großschreibung (.X/.Y/.Z), Unity .x/.y/.z.
/// </summary>
public static class Vec
{
    public static readonly Vector3 Zero = new(0, 0, 0);
    public static readonly Vector3 One = new(1, 1, 1);
    public static readonly Vector3 Up = new(0, 1, 0);
    public static readonly Vector3 Down = new(0, -1, 0);
    public static readonly Vector3 Left = new(-1, 0, 0);
    public static readonly Vector3 Right = new(1, 0, 0);
    public static readonly Vector3 Forward = new(0, 0, 1);
    public static readonly Vector3 Back = new(0, 0, -1);

    public static float Dot(Vector3 a, Vector3 b) => Vector3.Dot(a, b);
    public static Vector3 Cross(Vector3 a, Vector3 b) => Vector3.Cross(a, b);
    public static float Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, Mathf.Clamp01(t));
    public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, t);
    public static Vector3 Min(Vector3 a, Vector3 b) => Vector3.Min(a, b);
    public static Vector3 Max(Vector3 a, Vector3 b) => Vector3.Max(a, b);
    public static Vector3 Scale(Vector3 a, Vector3 b) => a * b;
    public static Vector3 Project(Vector3 v, Vector3 onto)
    {
        float d = Vector3.Dot(onto, onto);
        return d < Mathf.Epsilon ? Vector3.Zero : onto * (Vector3.Dot(v, onto) / d);
    }

    /// <summary>Winkel zwischen zwei Vektoren in Grad.</summary>
    public static float Angle(Vector3 from, Vector3 to)
    {
        float denom = Mathf.Sqrt(from.LengthSquared() * to.LengthSquared());
        if (denom < 1e-15f) return 0f;
        float cos = Mathf.Clamp(Vector3.Dot(from, to) / denom, -1f, 1f);
        return Mathf.Acos(cos) * Mathf.Rad2Deg;
    }

    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
    {
        Vector3 d = target - current;
        float dist = d.Length();
        if (dist <= maxDistanceDelta || dist < 1e-15f) return target;
        return current + d / dist * maxDistanceDelta;
    }
}

public static class VectorExtensions
{
    public static float Magnitude(this Vector3 v) => v.Length();
    public static float SqrMagnitude(this Vector3 v) => v.LengthSquared();

    /// <summary>Wie Unity: normalized gibt bei Länge ~0 den Nullvektor zurück (kein NaN).</summary>
    public static Vector3 Normalized(this Vector3 v)
    {
        float m = v.Length();
        return m > 1e-05f ? v / m : Vector3.Zero;
    }

    public static Vector3 WithY(this Vector3 v, float y) => new(v.X, y, v.Z);
    public static Vector3 Flat(this Vector3 v) => new(v.X, 0f, v.Z);
}

public static class Quat
{
    public static readonly Quaternion Identity = Quaternion.Identity;

    public static Quaternion Euler(float xDeg, float yDeg, float zDeg)
        => Quaternion.CreateFromYawPitchRoll(yDeg * Mathf.Deg2Rad, xDeg * Mathf.Deg2Rad, zDeg * Mathf.Deg2Rad);

    public static Quaternion Euler(Vector3 degrees) => Euler(degrees.X, degrees.Y, degrees.Z);
    public static Quaternion AngleAxis(float angleDeg, Vector3 axis)
        => Quaternion.CreateFromAxisAngle(axis.Normalized(), angleDeg * Mathf.Deg2Rad);

    public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => Quaternion.Slerp(a, b, Mathf.Clamp01(t));
    public static Vector3 Mul(Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        forward = forward.Normalized();
        if (forward.LengthSquared() < 1e-10f) return Quaternion.Identity;
        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 realUp = Vector3.Cross(forward, right);
        var m = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            realUp.X, realUp.Y, realUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(m);
    }
}
