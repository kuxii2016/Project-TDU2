namespace Paradise.Core.Math;

/// <summary>
/// Unity-kompatible Mathf-Helfer. Signaturen bewusst gleich wie in Unity,
/// damit die ~1900 Mathf-Aufrufe aus dem Unity-Projekt unverändert portiert
/// werden können. Intern auf System.MathF (float) aufgebaut.
/// </summary>
public static class Mathf
{
    public const float PI = 3.14159265358979f;
    public const float Deg2Rad = PI / 180f;
    public const float Rad2Deg = 180f / PI;
    public const float Epsilon = 1.401298E-45f;
    public const float Infinity = float.PositiveInfinity;
    public const float NegativeInfinity = float.NegativeInfinity;

    public static float Abs(float v) => System.MathF.Abs(v);
    public static float Sign(float v) => v >= 0f ? 1f : -1f;
    public static float Min(float a, float b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static float Min(params float[] v) { var m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] < m) m = v[i]; return m; }
    public static float Max(params float[] v) { var m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] > m) m = v[i]; return m; }

    public static float Sqrt(float v) => System.MathF.Sqrt(v);
    public static float Pow(float a, float b) => System.MathF.Pow(a, b);
    public static float Exp(float v) => System.MathF.Exp(v);
    public static float Log(float v) => System.MathF.Log(v);
    public static float Log(float v, float b) => System.MathF.Log(v) / System.MathF.Log(b);
    public static int Abs(int v) => v < 0 ? -v : v;
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;
    public static float Log10(float v) => System.MathF.Log10(v);

    public static float Sin(float v) => System.MathF.Sin(v);
    public static float Cos(float v) => System.MathF.Cos(v);
    public static float Tan(float v) => System.MathF.Tan(v);
    public static float Asin(float v) => System.MathF.Asin(v);
    public static float Acos(float v) => System.MathF.Acos(v);
    public static float Atan(float v) => System.MathF.Atan(v);
    public static float Atan2(float y, float x) => System.MathF.Atan2(y, x);

    public static float Floor(float v) => System.MathF.Floor(v);
    public static float Ceil(float v) => System.MathF.Ceiling(v);
    public static float Round(float v) => System.MathF.Round(v);
    public static int FloorToInt(float v) => (int)System.MathF.Floor(v);
    public static int CeilToInt(float v) => (int)System.MathF.Ceiling(v);
    public static int RoundToInt(float v) => (int)System.MathF.Round(v);

    public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    public static float InverseLerp(float a, float b, float v)
        => System.MathF.Abs(b - a) < Epsilon ? 0f : Clamp01((v - a) / (b - a));

    public static float LerpAngle(float a, float b, float t)
    {
        float delta = Repeat(b - a, 360f);
        if (delta > 180f) delta -= 360f;
        return a + delta * Clamp01(t);
    }

    public static float MoveTowards(float current, float target, float maxDelta)
        => System.MathF.Abs(target - current) <= maxDelta ? target : current + Sign(target - current) * maxDelta;

    public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);
    public static float PingPong(float t, float length)
    {
        t = Repeat(t, length * 2f);
        return length - System.MathF.Abs(t - length);
    }

    public static float DeltaAngle(float current, float target)
    {
        float delta = Repeat(target - current, 360f);
        if (delta > 180f) delta -= 360f;
        return delta;
    }

    public static bool Approximately(float a, float b)
        => System.MathF.Abs(b - a) < Max(1E-06f * Max(System.MathF.Abs(a), System.MathF.Abs(b)), Epsilon * 8f);

    public static float SmoothStep(float from, float to, float t)
    {
        t = Clamp01(t);
        t = -2f * t * t * t + 3f * t * t;
        return to * t + from * (1f - t);
    }

    /// <summary>Unity Mathf.SmoothDamp (kritisch gedämpfte Feder, Game Programming Gems 4).</summary>
    public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
    {
        smoothTime = Max(0.0001f, smoothTime);
        float omega = 2f / smoothTime;
        float x = omega * deltaTime;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float originalTo = target;
        float maxChange = maxSpeed * smoothTime;
        change = Clamp(change, -maxChange, maxChange);
        target = current - change;
        float temp = (currentVelocity + omega * change) * deltaTime;
        currentVelocity = (currentVelocity - omega * temp) * exp;
        float output = target + (change + temp) * exp;
        if (originalTo - current > 0.0f == output > originalTo)
        {
            output = originalTo;
            currentVelocity = (output - originalTo) / deltaTime;
        }
        return output;
    }

    public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float deltaTime)
        => SmoothDamp(current, target, ref currentVelocity, smoothTime, float.PositiveInfinity, deltaTime);
}
