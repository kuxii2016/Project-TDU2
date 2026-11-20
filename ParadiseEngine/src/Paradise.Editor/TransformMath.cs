// Szenen-Editor: Transform-Hilfen wie im Unity-Inspector (Position / Rotation als Euler-Winkel in Grad / Skalierung).

using System.Numerics;

namespace Paradise.Editor;

public static class TransformMath
{
    const float Rad = MathF.PI / 180f;

    /// <summary>Quaternion → Euler-Winkel in Grad, Unity-Reihenfolge (Z, dann X, dann Y; q = Y·X·Z).</summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float sinX = 2f * (q.W * q.X - q.Y * q.Z);
        float x = MathF.Abs(sinX) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinX) : MathF.Asin(sinX);
        float y = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float z = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.X * q.X + q.Z * q.Z));
        return new Vector3(Wrap(x / Rad), Wrap(y / Rad), Wrap(z / Rad));
    }

    /// <summary>Euler-Winkel in Grad → Quaternion (Umkehrung von <see cref="ToEuler"/>).</summary>
    public static Quaternion FromEuler(Vector3 deg) =>
        Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg.Y * Rad)
                           * Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg.X * Rad)
                           * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg.Z * Rad));

    static float Wrap(float d)
    {
        d %= 360f;
        if (d < 0f) d += 360f;
        if (d > 359.9995f) d = 0f;
        return d;
    }

    /// <summary>
    /// Matrix zerlegen wie CarNode.ApplyMatrix (Unity ObjectBuilder.ApplyMatrix): Rotation aus Z-/Y-Achse, Spiegelung
    /// (negative Determinante) als negative X-Skalierung. false = nicht zerlegbar (Achse ~0).
    /// </summary>
    public static bool Decompose(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        pos = m.Translation; rot = Quaternion.Identity; scale = Vector3.One;
        float sx = new Vector3(m.M11, m.M12, m.M13).Length();
        float sy = new Vector3(m.M21, m.M22, m.M23).Length();
        float sz = new Vector3(m.M31, m.M32, m.M33).Length();
        if (sx < 1e-6f || sy < 1e-6f || sz < 1e-6f) return false;
        var fwd = new Vector3(m.M31, m.M32, m.M33) / sz;
        var up = new Vector3(m.M21, m.M22, m.M23) / sy;
        rot = LookRotation(fwd, up);
        scale = new Vector3(m.GetDeterminant() < 0f ? -sx : sx, sy, sz);
        return true;
    }

    public static Matrix4x4 Compose(Vector3 pos, Quaternion rot, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);

    /// <summary>Unity Quaternion.LookRotation (Zeilenvektoren: Zeile 1 = rechts, 2 = oben, 3 = vorne).</summary>
    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(up, forward);
        if (right.LengthSquared() < 1e-10f) right = Vector3.Cross(MathF.Abs(forward.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, forward);
        right = Vector3.Normalize(right);
        var u = Vector3.Cross(forward, right);
        var r = new Matrix4x4(right.X, right.Y, right.Z, 0f, u.X, u.Y, u.Z, 0f, forward.X, forward.Y, forward.Z, 0f, 0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
    }

    /// <summary>AABB (min/max) mit einer Matrix transformieren → neue AABB.</summary>
    public static void TransformAabb(Vector3 min, Vector3 max, Matrix4x4 m, out Vector3 outMin, out Vector3 outMax)
    {
        outMin = new Vector3(float.MaxValue); outMax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z), m);
            outMin = Vector3.Min(outMin, p); outMax = Vector3.Max(outMax, p);
        }
    }
}
