using Paradise.Core.Math;
using Paradise.Core.Platform;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Fahrzeugkameras aus camerasingame.xmb (je Auto, Fehlendes aus den Standardsätzen). Portiert aus TDU2VehicleCamera (Unity):
    ///   20/21 Verfolger nah/fern: Position/Ziel-Offsets interpolieren zwischen follow_*_offset (bei follow_speed_offset km/h)
    ///          und follow_*_max_offset (bei follow_speed_max_offset), FOV zwischen fov_min und fov_max über fov_speed_min..max.
    ///   22 Stoßstange, 23 Cockpit, 24 Motorhaube: feste Punkte fixed_pos/fixed_targ im Fahrzeugraum.
    ///   4x: zugehörige Rückblick-Kamera (backviewmode).
    /// Spielkoordinaten sind rechtshändig (+X = links) -> X wird gespiegelt. Ergebnis (<see cref="position"/>, <see cref="rotation"/>)
    /// im Unity-Raum wie das Fahrzeug; die Engine-Kamera übernimmt es gespiegelt.
    /// </summary>
    public sealed class TDU2VehicleCamera
    {
        public TDU2Vehicle? Target;
        public CameraDatabase? Cameras;
        public int CarId;
        public int Mode = 20;
        public bool LookBack;
        public float FollowStiffness = 6f;

        /// <summary>Unity: transform.position / rotation der Kamera.</summary>
        public Vector3 position;
        public Quaternion rotation = Quaternion.Identity;
        /// <summary>Unity: Camera.fieldOfView / nearClipPlane.</summary>
        public float fieldOfView = 60f, nearClipPlane = 1f;
        float _origNear = 1f;

        public bool IsInterior => CurrentDef != null && CurrentDef.IsFixed && (CurrentDef.Mode % 10) == 3;
        public CarCamera? CurrentDef { get; private set; }

        Vector3 _smoothPos, _velRef;
        Quaternion _smoothYaw = Quaternion.Identity;
        bool _init;
        float _orbitYaw, _orbitPitch;
        float _dt, _time;
        InputState? _input;

        public TDU2VehicleCamera(float near = 1f) { _origNear = near; nearClipPlane = near; }

        /// <summary>Floating Origin: Weltverschiebung (Unity-Raum) nachführen.</summary>
        public void Shift(Vector3 d) { position -= d; _smoothPos -= d; }

        public void NextMode()
        {
            var order = Cameras != null ? Cameras.SwitchOrder : new[] { 21, 20, 23, 24, 22 };
            int idx = System.Array.IndexOf(order, Mode);
            Mode = order[(idx + 1 + order.Length) % order.Length];
            _init = false;
        }

        /// <summary>Blickrichtung bleibt nach dem Loslassen der Maustaste stehen (sonst langsam zurück zur Mitte)</summary>
        public bool KeepLookDirection = false;
        public float MouseSensitivity = 3f;

        /// <summary>Innenansicht: Maus bewegt den Kopf auch ohne gedrückte Taste (wie im Spiel)</summary>
        public bool FreeLookInterior = false;
        float _lastLook;
        bool _stickLook;
        /// <summary>Rechter Stick des Gamepads (Unity TDU2InputDevices.Look, x = rechts, y = oben) – gesetzt vom Spiel (Game/Input).</summary>
        public static Func<Vector2>? PadLook;

        /// <summary>Rechte Maustaste halten + Maus bewegen = umsehen (außen: um das Auto kreisen, innen: Kopf drehen).</summary>
        void UpdateOrbit(float maxYaw, float minPitch, float maxPitch, bool interior)
        {
            // Unity Input.GetAxis("Mouse X/Y"): Mausweg * 0,1, Y nach oben positiv
            float mx = (_input?.MouseDeltaX ?? 0f) * 0.1f, my = -(_input?.MouseDeltaY ?? 0f) * 0.1f;
            if (Paradise.Core.Audio.TDU2Radio.IsOpen) return;
            float kx = 0, ky = 0;
            // Gamepad: rechter Stick = Blickrichtung (Ausschlag = Winkel, wie im Spiel), loslassen -> zurück zur Mitte
            var look = PadLook?.Invoke() ?? Vector2.Zero;
            if (look != Vector2.Zero)
            {
                float yawT = look.X * maxYaw;
                float pitchT = look.Y >= 0f ? -look.Y * Mathf.Abs(minPitch) : -look.Y * maxPitch;
                _orbitYaw = Mathf.MoveTowards(_orbitYaw, yawT, 420f * _dt);
                _orbitPitch = Mathf.MoveTowards(_orbitPitch, Mathf.Clamp(pitchT, minPitch, maxPitch), 240f * _dt);
                _lastLook = _time - 1.3f;   // nach dem Loslassen gleich zurückfedern
                _stickLook = true;
                return;
            }
            if (_stickLook)
            {
                _stickLook = false;
                if (!KeepLookDirection) { _lastLook = -10f; }
            }
            bool held = _input?.RightMouseDown ?? false;
            bool free = interior && FreeLookInterior && (Mathf.Abs(mx) > 0.001f || Mathf.Abs(my) > 0.001f);
            if (held || free || kx != 0f || ky != 0f)
            {
                _orbitYaw = Mathf.Clamp(_orbitYaw + mx * MouseSensitivity + kx * 120f * _dt, -maxYaw, maxYaw);
                _orbitPitch = Mathf.Clamp(_orbitPitch - my * MouseSensitivity * 0.7f - ky * 80f * _dt, minPitch, maxPitch);
                _lastLook = _time;
                if (held) return;
            }
            if (_time - _lastLook < 1.5f) return;   // kurz halten, dann zurück zur Mitte
            if (!KeepLookDirection)
            {
                float k = 1f - Mathf.Exp(-2f * _dt);
                _orbitYaw = Mathf.Lerp(_orbitYaw, 0f, k); _orbitPitch = Mathf.Lerp(_orbitPitch, 0f, k);
            }
        }

        static Vector3 Mirror(Vector3 v) => new Vector3(-v.X, v.Y, v.Z);

        public void LateUpdate(float dt, float time, InputState? input)
        {
            _dt = dt; _time = time; _input = input;
            if (Target == null) return;
            var def = Cameras?.Get(CarId, Mode);
            if (def != null && LookBack)
            {
                var back = Cameras!.Get(CarId, def.BackMode);
                if (back != null) def = back;
            }
            CurrentDef = def;
            var car = Target.VisualPose;   // Unity: interpoliertes transform des Autos
            float speed = Target.SpeedKmh;

            if (def == null)
            {
                // Rückfall: einfache Verfolgerkamera
                var p = car.TransformPoint(new Vector3(0, 2f, -6.5f));
                position = U.Lerp(position, p, _dt * 5f);
                rotation = U.LookRotation(car.TransformPoint(new Vector3(0, 1f, 2f)) - position, Vec.Up);
                return;
            }

            float fovT = Mathf.InverseLerp(def.FovSpeedMin, Mathf.Max(def.FovSpeedMin + 1f, def.FovSpeedMax), speed);
            fieldOfView = Mathf.Lerp(def.FovMin, def.FovMax, fovT);
            nearClipPlane = def.IsFixed ? Mathf.Clamp(def.Near, 0.03f, 0.3f) : Mathf.Max(0.3f, Mathf.Min(_origNear > 0 ? _origNear : 1f, 0.5f));

            if (def.IsFixed)
            {
                Vector3 pos = car.TransformPoint(Mirror(def.FixedPos));
                Vector3 tgt = car.TransformPoint(Mirror(def.FixedTarget));
                position = pos;
                var baseRot = U.LookRotation(tgt - pos, car.up);
                // Umsehen (rechte Maustaste): Kopf drehen, Loslassen -> langsam zurück
                UpdateOrbit(170f, -60f, 70f, true);
                rotation = baseRot * U.Euler(_orbitPitch, _orbitYaw, 0f);
                _init = false;
                return;
            }

            // Verfolger
            float t = Mathf.InverseLerp(def.FollowSpeed, Mathf.Max(def.FollowSpeed + 1f, def.FollowSpeedMax), speed);
            float posZ = Mathf.Lerp(def.FollowPos, def.FollowPosMax, t);
            float posY = Mathf.Lerp(def.FollowPosH, def.FollowPosMaxH, t);
            float tgZ = Mathf.Lerp(def.FollowTarg, def.FollowTargMax, t);
            float tgY = Mathf.Lerp(def.FollowTargH, def.FollowTargMaxH, t);

            // Gierwinkel des Autos verzögert folgen (Kamera schwingt in Kurven leicht aus)
            var flatFwd = U.ProjectOnPlane(car.forward, Vec.Up);
            if (flatFwd.LengthSquared() < 1e-4f) flatFwd = U.Rot(rotation, Vec.Forward);
            var yaw = U.LookRotation(Vector3.Normalize(flatFwd), Vec.Up);
            if (!_init) { _smoothYaw = yaw; }
            _smoothYaw = U.Slerp(_smoothYaw, yaw, 1f - Mathf.Exp(-FollowStiffness * _dt));

            UpdateOrbit(180f, -20f, 60f, false);
            var rot = _smoothYaw * U.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 carPos = car.position;
            Vector3 desired = carPos + U.Rot(rot, new Vector3(0f, posY, posZ));
            Vector3 target = carPos + U.Rot(_smoothYaw, new Vector3(0f, tgY, tgZ));
            // Höhe weich nachführen (Sprünge/Kuppen), horizontal starr am verzögerten Gierwinkel
            if (!_init) { _smoothPos = desired; _init = true; }
            _smoothPos = new Vector3(desired.X, Mathf.SmoothDamp(_smoothPos.Y, desired.Y, ref _velRef.Y, 0.12f, Mathf.Max(_dt, 1e-4f)), desired.Z);
            position = _smoothPos;
            rotation = U.LookRotation(target - _smoothPos, Vec.Up);
        }
    }
}
