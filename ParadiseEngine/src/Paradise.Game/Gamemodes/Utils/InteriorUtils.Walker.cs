using Paradise.Core.Physics;
using Paradise.Core.Platform;
using Paradise.Game.Input;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

public abstract partial class InteriorUtils
{
    /// <summary>
    /// Laufen in Interieurs wie im Spiel (Ich-Perspektive, "FirstPerson"-Entity der Szene) – Port von Unity TDU2FirstPerson:
    /// Laufen-Tasten (KeyConfig walk_*), Shift rennen, Maus umsehen. Statt des Unity-CharacterController (Höhe 1,8, Radius 0,28,
    /// Stufe 0,35 m) Strahlen gegen die Kollision des Interieurs. Koordinaten: Spielraum.
    /// </summary>
    internal sealed class Walker
    {
        public float EyeHeight = 1.65f;
        public float WalkSpeed = 1.9f, RunSpeed = 4.2f;
        public float MouseSensitivity = 2.2f;
        public bool LookEnabled = true;
        public const float Radius = 0.28f, StepOffset = 0.35f, Height = 1.8f;
        static readonly float[] RayHeights = { StepOffset + 0.05f, 0.9f, Height - 0.1f };

        /// <summary>Füße (Spielraum).</summary>
        public Vector3 Feet;
        float _yaw, _pitch, _vy;   // Grad (Unity: Euler(pitch, yaw, 0))

        public Vector3 Eye => Feet + Vector3.UnitY * EyeHeight;
        // Unity rechnet in der gespiegelten Welt (TDU2MapStreamer.MirrorX: Wurzel Scale -1,1,1). _yaw/_pitch sind Unity-Werte,
        // Richtungen werden mit X -> -X in den Spielraum übertragen (wie VehicleManager U.Mirror).
        /// <summary>Blickrichtung (Unity Quaternion.Euler(pitch, yaw, 0) * forward, gespiegelt in den Spielraum).</summary>
        public Vector3 Forward
        {
            get
            {
                float y = _yaw * MathF.PI / 180f, p = _pitch * MathF.PI / 180f;
                return new Vector3(-MathF.Sin(y) * MathF.Cos(p), -MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
            }
        }
        public Vector3 Up
        {
            get
            {
                float y = _yaw * MathF.PI / 180f, p = _pitch * MathF.PI / 180f;
                return new Vector3(-MathF.Sin(y) * MathF.Sin(p), MathF.Cos(p), MathF.Cos(y) * MathF.Sin(p));
            }
        }

        /// <summary>Augenposition + Blickrichtung (Spielraum).</summary>
        public void Place(Vector3 eye, Vector3 look)
        {
            Feet = eye - Vector3.UnitY * EyeHeight;
            var flat = new Vector3(look.X, 0f, look.Z);
            _yaw = flat.LengthSquared() > 1e-6f ? MathF.Atan2(-flat.X, flat.Z) * 180f / MathF.PI : 0f;   // Unity-Raum (gespiegelt)
            float ly = look.LengthSquared() > 1e-9f ? Vector3.Normalize(look).Y : 0f;
            _pitch = Math.Clamp(-MathF.Asin(Math.Clamp(ly, -1f, 1f)) * 180f / MathF.PI, -60f, 60f);
            _vy = 0f;
            _settle = true;
        }

        /// <summary>Nach Place: Unity CharacterController drückt die Kapsel beim Einschalten aus dem Boden heraus. Liegt der Boden
        /// zwischen Füßen und Augen (cam_entree in ias_d: Auge 1,65 m über y 3,54, Boden 3,99), stehen die Füße danach auf ihm –
        /// sonst begann der Bodenstrahl (Füße + 0,40 m) unter dem Boden und man fiel durch.</summary>
        bool _settle;

        public void Update(float dt, InputState input, PhysicsWorld? phys)
        {
            if (dt <= 0f) return;
            if (_settle && phys != null)
            {
                _settle = false;
                if (phys.Raycast(Feet + Vector3.UnitY * EyeHeight, -Vector3.UnitY, EyeHeight + 0.05f, out var top) && top.Distance < EyeHeight)
                    Feet.Y = Feet.Y + EyeHeight - top.Distance;
            }
            if (LookEnabled)
            {
                // Unity: Input.GetAxis("Mouse X/Y") ≈ Pixel x 0,1
                float ms = MouseSensitivity * 0.1f;
                _yaw += input.MouseDeltaX * ms;   // Unity TDU2FirstPerson: _yaw += Input.GetAxis("Mouse X") * ms
                _pitch = Math.Clamp(_pitch + input.MouseDeltaY * ms, -75f, 75f);
                if (TDU2Controls.Held("walk_turnl")) _yaw -= 120f * dt;
                if (TDU2Controls.Held("walk_turnr")) _yaw += 120f * dt;
            }
            float f = 0f, s = 0f;
            if (TDU2Controls.Held("walk_forward")) f += 1f;
            if (TDU2Controls.Held("walk_back")) f -= 1f;
            if (TDU2Controls.Held("walk_right")) s += 1f;
            if (TDU2Controls.Held("walk_left")) s -= 1f;
            float speed = TDU2Controls.Held("walk_run") ? RunSpeed : WalkSpeed;
            if (!LookEnabled) { f = 0f; s = 0f; }
            float yr = _yaw * MathF.PI / 180f;
            var fwd = new Vector3(-MathF.Sin(yr), 0f, MathF.Cos(yr));     // Unity transform.forward, gespiegelt
            var right = new Vector3(-MathF.Cos(yr), 0f, -MathF.Sin(yr));  // Unity transform.right, gespiegelt
            var move = fwd * f + right * s;
            if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);

            // waagerecht: an Wänden entlang gleiten (zwei Durchgänge, Strahlen in Knie-, Hüft- und Kopfhöhe)
            var delta = move * speed * dt;
            if (phys != null)
                for (int iter = 0; iter < 2 && delta.LengthSquared() > 1e-10f; iter++)
                {
                    var dir = Vector3.Normalize(delta);
                    float len = delta.Length();
                    float best = float.MaxValue; Vector3 n = default;
                    foreach (float h in RayHeights)
                        if (phys.Raycast(Feet + Vector3.UnitY * h, dir, len + Radius, out var hit) && hit.Distance < best)
                        { best = hit.Distance; n = hit.Normal; }
                    if (best == float.MaxValue) break;
                    float free = MathF.Max(0f, best - Radius);
                    var nh = new Vector3(n.X, 0f, n.Z);
                    if (nh.LengthSquared() < 1e-6f) break;
                    nh = Vector3.Normalize(nh);
                    var go = dir * MathF.Min(free, len);
                    Feet += go;
                    var rest = delta - go;
                    delta = rest - nh * Vector3.Dot(rest, nh);   // Rest entlang der Wand
                }
            Feet += delta;

            // senkrecht: Schwerkraft, Boden (Stufen bis StepOffset)
            _vy = MathF.Max(_vy - 9.81f * dt, -30f);
            float dy = _vy * dt;
            if (phys != null && phys.Raycast(Feet + Vector3.UnitY * (StepOffset + 0.05f), -Vector3.UnitY, StepOffset + 0.05f + MathF.Max(0.05f, -dy), out var g))
            {
                float gy = Feet.Y + StepOffset + 0.05f - g.Distance;
                if (Feet.Y + dy <= gy + 0.02f) { Feet.Y = gy; _vy = 0f; return; }
            }
            Feet.Y += dy;
        }
    }
}
