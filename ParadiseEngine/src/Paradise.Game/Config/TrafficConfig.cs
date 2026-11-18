namespace Paradise.Game.Config;

/// <summary>
/// Traffic (Gameplay/Traffic, Port Unity TDU2Traffic). Change values only here in the code (the developer menu can set them at runtime).
/// Original GamePC.cpr: TrafficMaxCount 8, VehicleMaxCount 16, SpawnDistances 200 400, CullingConfig 500/100, PhysicsMaxCount 4.
/// </summary>
public static class TrafficConfig
{
    public static bool Enabled = true;
    /// <summary>Cars around the player.</summary>
    public static int MaxCars = 12;
    /// <summary>Traffic density by time of day (aiconfig.cpr NB_TRAFFIC, system clock).</summary>
    public static bool DensityByTime = false;
    public static float SpawnMin = 180f, SpawnMax = 400f;
    /// <summary>Cull objects beyond this distance (CullingConfig 500).</summary>
    public static float DespawnDistance = 520f;
    /// <summary>Speed relative to the road's speed class.</summary>
    public static float SpeedFactor = 0.92f;

    /// <summary>Cars within this radius are physics bodies (CullingConfig 100) – up to a maximum of <see cref="PhysicsMax"/>.</summary>
    public static float PhysicsRadius = 100f;
    public static int PhysicsMax = 6;
    /// <summary>Impact (m/s relative speed) at which a car is removed from the AI.</summary>
    public static float CrashSpeed = 3.5f;

    // Multiplayer (Unity TDU2Traffic "Multiplayer"): every car belongs to one player who simulates it; the others see it interpolated
    /// <summary>Sync traffic between players (Online/Multiplayer.SendTraffic).</summary>
    public static bool Sync = true;
    /// <summary>Traffic packets per second.</summary>
    public static float SendRate = 8f;
    /// <summary>Only send own cars when another player is this close (m).</summary>
    public static float RelevantDistance = 900f;

    public static bool Sounds = true;
    public static int SoundMax = 3;
    public static float SoundRadius = 70f;

    // Driving Behavior (IDM + Gentle Braking)
    /// <summary>Maximum acceleration, comfortable deceleration, minimum distance, time gap (s).</summary>
    public static float AccelMax = 1.6f, ComfortBrake = 2.4f, MinGap = 3.5f, TimeGap = 1.4f;
    /// <summary>Emergency braking (m/s²) – only if the distance would otherwise be insufficient.</summary>
    public static float EmergencyBrake = 8f;
    /// <summary>Jerk (m/s³): how fast the acceleration is allowed to change (normal / emergency).</summary>
    public static float Jerk = 5f, EmergencyJerk = 30f;
    /// <summary>Only yield the right of way if you can still stop with this deceleration (m/s²)—otherwise, keep going.</summary>
    public static float YieldBrake = 3.5f;
    /// <summary>Player/Teammate: pre-calculated position(s) for crossing cars.</summary>
    public static float PlayerPredict = 1.5f;

    // Debug view (Unity TDU2Traffic ShowDebug/DebugRoutes/DebugArrows/DebugLabels/DebugRange), toggleable in the developer menu (F1)
    /// <summary>Lines/labels in the world + panel at the bottom left.</summary>
    public static bool ShowDebug = false;
    public static bool DebugRoutes = true, DebugArrows = true, DebugLabels = true;
    /// <summary>Only cars at this distance from the camera (m).</summary>
    public static float DebugRange = 140f;
}
