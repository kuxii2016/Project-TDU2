using Paradise.Core.Platform;
using System.Numerics;

namespace Paradise.Game.World;

/// <summary>
/// Freie Flugkamera: WASD bewegen, Leertaste/Strg hoch/runter, Shift schneller,
/// rechte Maustaste halten + bewegen zum Umsehen.
/// </summary>
public sealed class FlyCamera
{
    public Vector3 Position;
    public float Yaw, Pitch;   // Radiant

    public float Speed = 140f;
    public float FastMultiplier = 6f;
    public float MouseSensitivity = 0.0025f;

    public FlyCamera(Vector3 position, float yaw, float pitch)
    {
        Position = position; Yaw = yaw; Pitch = pitch;
    }

    public Vector3 Forward =>
        new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>Fahrzeugkamera: Blickrichtung/Oben setzt TDU2VehicleCamera (Position bleibt in <see cref="Position"/>); null = Freiflug.</summary>
    public (Vector3 forward, Vector3 up)? Override;

    public Matrix4x4 View => Override is { } o
        ? Matrix4x4.CreateLookAt(Position, Position + o.forward, o.up)
        : Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public void Update(float dt, InputState input)
    {
        if (input.RightMouseDown)
        {
            Yaw -= input.MouseDeltaX * MouseSensitivity;
            Pitch -= input.MouseDeltaY * MouseSensitivity;
            Pitch = Math.Clamp(Pitch, -1.5f, 1.5f);
        }

        Vector3 fwd = Forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fwd));
        float step = Speed * (input.IsKeyDown(InputState.VK_SHIFT) ? FastMultiplier : 1f) * dt;

        if (input.IsKeyDown(InputState.VK_W)) Position += fwd * step;
        if (input.IsKeyDown(InputState.VK_S)) Position -= fwd * step;
        if (input.IsKeyDown(InputState.VK_D)) Position -= right * step;
        if (input.IsKeyDown(InputState.VK_A)) Position += right * step;
        if (input.IsKeyDown(InputState.VK_SPACE)) Position += Vector3.UnitY * step;
        if (input.IsKeyDown(InputState.VK_CONTROL)) Position -= Vector3.UnitY * step;
    }
}
