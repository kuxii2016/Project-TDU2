using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using System.Diagnostics;
using System.Numerics;

namespace Paradise.Core.Avatars;

/// <summary>
/// Driver or passenger in the car, as in the game (ported from Unity TDU2CarAvatar; anims/car, driveiklayer): seat pose “stance_turn”,
/// with “turn” based on the steering angle (0 s for a quarter turn to the right, 0.52 s straight ahead, 1.04 s for a quarter turn to the left), then IK of the hands to
/// the steering wheel grips (HAND_HR_L/_R, children of STEER_HR) and the feet to the pedals (FOOT_HR_L/_R). The clip root (ROOT) is located at
/// the hip point DVRSEAT_HR (passenger: PSGSEAT_HR, PSGFOOT_HR_*; pose hit_stand_car or sho_stand_car). Motorcycle (D_BIKE):
/// drive_front_speed / drive_back_turn in the model space; pelvis at DVRSEAT_HR; hands/feet at HAND_L/R, FOOT_L/R.
/// The avatar is attached to the CarModel.Visual node (play space). Called per frame after CarModel.UpdateTransforms: <see cref="Render"/>.
/// </summary>
public sealed class CarAvatar : IDisposable
{
    public readonly CarModel Model;
    public readonly bool Passenger;
    public Func<float>? Steer;
    /// <summary>Reverse the direction of the body animation.</summary>
    public bool InvertSteer;
    /// <summary>Hands on the steering wheel grips (IK).</summary>
    public bool HandIK = true;
    /// <summary>Feet on the pedals (IK).</summary>
    public bool FootIK = true;
    /// <summary>Hide the head when the camera is in the cockpit.</summary>
    public bool HideHeadInCockpit = true;
    /// <summary>Check the appearance continuously (every 2 seconds) and rebuild it if it changes.</summary>
    public Func<string?>? ConfigCode;
    public AvatarModel? Avatar { get; private set; }

    public static bool ShowDrivers = true, ShowPassengers = true;
    /// <summary>Developer menu: Play clip instead of sitting pose (null = normal).</summary>
    public static string? DebugClip;
    public static bool DebugIK = true;
    public static float DebugSpeed = 1f;
    public static readonly List<CarAvatar> All = new();

    readonly IRenderer _r;
    readonly CarNode _seat;
    readonly bool _bike;
    CarNode? _handL, _handR, _footL, _footR;
    AnmClip? _base, _turn, _debug;
    string? _debugName, _code;
    float _baseT, _debugT, _steerSmooth;
    double _nextCheck;
    int _armL, _foreL, _ikHandL, _armR, _foreR, _ikHandR, _upLegL, _legL, _ikFootL, _upLegR, _legR, _ikFootR, _head;
    int _pelvis = -1;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;

    CarAvatar(IRenderer r, CarModel model, CarNode seat, bool bike, bool passenger)
    {
        _r = r; Model = model; _seat = seat; _bike = bike; Passenger = passenger;
        All.Add(this);
    }

    public static CarAvatar? Attach(IRenderer r, CarModel? model, AvatarConfig? cfg, bool passenger, Func<float>? steer)
    {
        if (model == null || !AvatarLibrary.Ready) return null;
        bool bike = model.Find("D_BIKE") != null;
        if (bike && passenger) return null;
        var seat = bike ? model.Find("DVRSEAT_HR")
            : model.Find(passenger ? "PSGSEAT_HR" : "DVRSEAT_HR") ?? model.Find(passenger ? "PSGSEAT_LR" : "DVRSEAT_LR");
        if (seat == null) return null;   // Autos ohne Sitz-Dummy
        var ca = new CarAvatar(r, model, seat, bike, passenger) { Steer = steer };
        ca.Rebuild(cfg);
        return ca;
    }
    /// <summary>Rebuild the avatar with a new look.</summary>
    public void Rebuild(AvatarConfig? cfg)
    {
        Avatar?.Dispose();
        cfg ??= AvatarConfig.Default();
        _code = cfg.Encode();
        Avatar = AvatarModel.Create(_r, cfg);
        Avatar.InCar = true;
        Avatar.Built += OnBuilt;
    }

    void OnBuilt(AvatarModel a)
    {
        var rig = a.Rig!;
        bool woman = a.Config.Sex == 1;
        if (_bike)
        {
            // Motorrad: Normalfahrt mit drive_front_speed, Lenkung über drive_back_turn
            _base = AvatarLibrary.Clip("drive_front_speed") ?? AvatarLibrary.Clip("drive_back");
            _turn = AvatarLibrary.Clip("drive_back_turn");
            if (_base == null) { _base = _turn; _baseT = _turn != null ? _turn.Length * 0.5f : 0f; }
            _handL = Model.Find("HAND_L");
            _handR = Model.Find("HAND_R");
            _footL = Model.Find("FOOT_L");
            _footR = Model.Find("FOOT_R");
        }
        else if (Passenger)
        {
            _base = AvatarLibrary.Clip(woman ? "sho_stand_car" : "hit_stand_car") ?? AvatarLibrary.Clip("stance_turn");
            _handL = _handR = null;
            _footL = Model.Find("PSGFOOT_HR_L") ?? Model.Find("PSGFOOT_LR_L");
            _footR = Model.Find("PSGFOOT_HR_R") ?? Model.Find("PSGFOOT_LR_R");
        }
        else
        {
            _base = AvatarLibrary.Clip("stance_turn");
            _turn = AvatarLibrary.Clip("turn");
            _handL = Model.Find("HAND_HR_L") ?? Model.Find("HAND_LR_L");
            _handR = Model.Find("HAND_HR_R") ?? Model.Find("HAND_LR_R");
            _footL = Model.Find("FOOT_HR_L") ?? Model.Find("FOOT_LR_L");
            _footR = Model.Find("FOOT_HR_R") ?? Model.Find("FOOT_LR_R");
        }
        _armL = rig.Bone("ARM_L"); _foreL = rig.Bone("FOREARM_L"); _ikHandL = rig.Bone("IK_LEFT_HAND");
        _armR = rig.Bone("ARM_R"); _foreR = rig.Bone("FOREARM_R"); _ikHandR = rig.Bone("IK_RIGHT_HAND");
        _upLegL = rig.Bone("UPLEG_L"); _legL = rig.Bone("LEG_L"); _ikFootL = rig.Bone("IK_LEFT_FOOT");
        _upLegR = rig.Bone("UPLEG_R"); _legR = rig.Bone("LEG_R"); _ikFootR = rig.Bone("IK_RIGHT_FOOT");
        _head = rig.Bone("HEAD");
        _pelvis = rig.Bone("PELVIS");
    }

    /// <summary>
    /// For each frame, after CarModel.UpdateTransforms (steering wheel is already in place): Set the pose as in Unity's LateUpdate, then render.
    /// <paramref name="cameraRender"/> = Camera position in render space (hide head in cockpit).
    /// </summary>
    public void Render(Vector3 cameraRender, bool indoor = false)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)System.Math.Clamp(now - _last, 0.0, 0.1);
        _last = now;
        if (ConfigCode != null && now >= _nextCheck)
        {
            _nextCheck = now + 2.0;
            string? c = ConfigCode();
            if (!string.IsNullOrEmpty(c) && c != _code) { var cfg = AvatarConfig.Decode(c); if (cfg != null) Rebuild(cfg); }
        }
        var a = Avatar;
        if (a == null) return;
        a.Update();
        if (!a.Ready || a.Rig == null) return;
        a.Visible = Passenger ? ShowPassengers : ShowDrivers;
        a.Indoor = indoor;
        var rig = a.Rig;
        var vis = Model.Visual.World;
        if (!Matrix4x4.Invert(vis, out var visInv)) return;
        var seatLocal = Vector3.Transform(_seat.World.Translation, visInv);
        // Avatar-Raum -> Visual: Motorrad am Modellursprung (Clips mit absoluten Positionen), sonst am Sitz
        Vector3 offset = _bike ? Vector3.Zero : seatLocal;

        rig.BeginPose();
        if (!string.IsNullOrEmpty(DebugClip))
        {
            if (_debugName != DebugClip) { _debugName = DebugClip; _debug = AvatarLibrary.Clip(DebugClip); _debugT = 0f; }
            _debugT += dt * DebugSpeed;
            if (_debug != null) rig.ApplyClip(_debug, Repeat(_debugT, MathF.Max(0.05f, _debug.Length)), 1f, true);
        }
        else rig.ApplyClip(_base, _baseT);
        if (_bike && _pelvis >= 0 && string.IsNullOrEmpty(DebugClip))
            offset = seatLocal - rig.BonePosition(_pelvis);
        if (_turn != null && string.IsNullOrEmpty(DebugClip))
        {
            float s = Steer != null ? System.Math.Clamp(Steer(), -1f, 1f) : 0f;
            if (InvertSteer) s = -s;
            _steerSmooth = MoveTowards(_steerSmooth, s, dt * 4f);
            float half = _turn.Length > 0f ? _turn.Length * 0.5f : 0.52f;
            float t = System.Math.Clamp(half - _steerSmooth * half, 0f, _turn.Length > 0f ? _turn.Length : 1.04f);
            rig.ApplyClip(_turn, t, 1f, false);
        }
        var model = Matrix4x4.CreateTranslation(offset) * vis;   // Avatar-Raum -> Render-Raum
        if (!Matrix4x4.Invert(model, out var toAvatar)) return;
        Vector3 Av(CarNode n) => Vector3.Transform(n.World.Translation, toAvatar);
        if (HandIK && DebugIK)
        {
            if (_handL != null) rig.SolveTwoBone(_armL, _foreL, _ikHandL, Av(_handL));
            if (_handR != null) rig.SolveTwoBone(_armR, _foreR, _ikHandR, Av(_handR));
        }
        if (FootIK && DebugIK)
        {
            if (_footL != null) rig.SolveTwoBone(_upLegL, _legL, _ikFootL, Av(_footL));
            if (_footR != null) rig.SolveTwoBone(_upLegR, _legR, _ikFootR, Av(_footR));
        }
        if (HideHeadInCockpit && _head >= 0)
        {
            var headRender = Vector3.Transform(rig.BonePosition(_head), model);
            a.HideHead(Vector3.DistanceSquared(cameraRender, headRender) < 0.45f * 0.45f);
        }
        else a.HideHead(false);
        a.Draw(model);
    }

    static float Repeat(float t, float length) => t - MathF.Floor(t / length) * length;
    static float MoveTowards(float cur, float target, float maxDelta) =>
        MathF.Abs(target - cur) <= maxDelta ? target : cur + MathF.Sign(target - cur) * maxDelta;

    public void Dispose()
    {
        All.Remove(this);
        Avatar?.Dispose();
        Avatar = null;
    }
}
