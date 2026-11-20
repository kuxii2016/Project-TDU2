using Paradise.Core.Vehicles;

namespace Paradise.Game.Config;

/// <summary>
/// Fahrzeug-Einstellungen (Standardwerte wie TDU2VehicleManager im Unity-Projekt). Werte nur hier im Code ändern
/// (keine Datei, kein Speichern/Laden), wie LightingConfig.
/// </summary>
public sealed class VehicleConfig
{
    /// <summary>Startauto (Dateiname aus der Datenbank, z.B. delta_integrale_evo_2).</summary>
    public string StartCar { get; set; } = "delta_integrale_evo_2";
    /// <summary>Motorsounds laden.</summary>
    public bool LoadSound { get; set; } = true;
    /// <summary>Motor nach dem Laden/Spawnen aus; springt beim ersten Gas/Bremse/Lenken an (wie in Unity).</summary>
    public bool EngineOffAtSpawn { get; set; } = true;
    /// <summary>Grip-Skalierung der Reifen (0,3–1,2). Unity-Standard 0,62 – damit rutscht das Auto in Kurven heraus; 0,9 war der Wert vor s80.</summary>
    public float GripScale { get; set; } = 1.2f;
    /// <summary>Fahrhilfen beim Einsteigen: Secure, Sport, HyperSport, Off (Taste T wechselt).</summary>
    public DrivingAids Aids { get; set; } = DrivingAids.Sport;
    /// <summary>Automatikgetriebe (Taste G wechselt).</summary>
    public bool AutomaticGearbox { get; set; } = true;
    /// <summary>Physiktakt während der Fahrt (s), 0,005–0,02.</summary>
    public float FixedTimeStep { get; set; } = 0.01f;
    /// <summary> Karosserie-Kollider: Simple = eine Box wie das Unity-Original, Extended = mittiger Kabinenkasten in voller Höhe plus zwei flachere Kästen für Motorhaube/Heck. Greift beim nächsten Aufbau des Autos (F9 oder Neustart). </summary>
    public BodyColliderMode BodyCollider { get; set; } = BodyColliderMode.Extended;
    /// <summary>Startkamera: 20 Verfolger nah, 21 fern, 22 Stoßstange, 23 Cockpit, 24 Motorhaube (Taste C wechselt).</summary>
    public int CameraMode { get; set; } = 20;
    /// <summary>Lautstärke des Fahrzeugs (zusätzlich zu AudioConfig.Master).</summary>
    public float Volume { get; set; } = new AudioConfig().Vehicle;
    /// <summary>Helligkeit der Lichtgläser (Scheinwerfer, Rück-, Brems-, Rückfahrlicht, Blinker); 1 = wie im Unity-Projekt.</summary>
    public float LightBrightness { get; set; } = 4f;
    /// <summary>
    /// Lichtsteuerung wie das Original (Core/Vehicles/TDU2VehicleLights.OriginalControl): Licht-Taste schaltet den Modus
    /// 0 aus / 1 automatisch nach Wetter (Start) / 2 Scheinwerfer erzwungen; Blinker 1,0 s Takt mit 0,6 s an; Lichtgläser ohne
    /// Zusatz-Helligkeit (LightBrightness gilt dann nicht für die Gläser). Aus = bisherige Steuerung aus dem Unity-Projekt.
    /// </summary>
    public bool OriginalLightControl { get; set; } = true;

    /// <summary>Dämpfer degressiv: bis zu dieser Federgeschwindigkeit (m/s) linear wie Unity.</summary>
    public float DamperKnee { get; set; } = 0.5f;
    /// <summary>Anteil der Dämpfer-Steigung oberhalb von DamperKnee (1 = linear wie Unity; kleiner = weniger Hüpfen an Kanten).</summary>
    public float DamperHighRatio { get; set; } = 0.5f;

    /// <summary>Höchste Einfeder-Geschwindigkeit des Rads in m/s (Reifen fängt Kanten ab); 0 = unbegrenzt wie Unity.</summary>
    public float WheelMaxRise { get; set; } = 2.5f;

    /// <summary>Mindest-Kippgrenze in g: Schwerpunkt wird so weit abgesenkt, dass das Auto erst ab dieser Querbeschleunigung kippen
    /// könnte (Reifen rutschen vorher). 0 = Schwerpunkt aus der Datenbank wie Unity.</summary>
    public float MinRolloverG { get; set; } = 1.5f;
    /// <summary>Reifenlast für Seiten-/Längskräfte höchstens x-fache statische Radlast (Stöße erzeugen keine Riesen-Seitenkräfte). 0 = wie Unity.</summary>
    public float TireLoadMax { get; set; } = 2.2f;
    /// <summary>Anschlagpuffer beim Ausfedern (Anteil, Gummi-Hysterese): kleiner = weniger Schaukeln. 1 = wie Unity.</summary>
    public float BumpRebound { get; set; } = 0.3f;
    /// <summary>Höchste Reifenhaftung als Anteil der Kippgrenze (Auto rutscht, bevor es kippt). 0 = wie Unity.</summary>
    public float GripVsRollover { get; set; } = 0.85f;

    public void ApplyTo(VehicleManager vm)
    {
        Vehicle.MinRolloverG = System.Math.Clamp(MinRolloverG, 0f, 3f);
        Vehicle.TireLoadMax = System.Math.Clamp(TireLoadMax, 0f, 10f);
        Vehicle.BumpRebound = System.Math.Clamp(BumpRebound, 0f, 1f);
        Vehicle.GripVsRollover = System.Math.Clamp(GripVsRollover, 0f, 2f);
        Vehicle.WheelMaxRise = System.Math.Clamp(WheelMaxRise, 0f, 20f);
        Vehicle.DamperKnee = System.Math.Clamp(DamperKnee, 0.05f, 5f);
        Vehicle.DamperHighRatio = System.Math.Clamp(DamperHighRatio, 0.05f, 1f);
        VehicleRigidbody.BodyCollider = BodyCollider;
        vm.StartCar = StartCar;
        vm.LoadSound = LoadSound;
        vm.EngineOffAtSpawn = EngineOffAtSpawn;
        vm.GripScale = System.Math.Clamp(GripScale, 0.3f, 1.2f);
        vm.Aids = Aids;
        vm.AutomaticGearbox = AutomaticGearbox;
        vm.FixedTimeStep = System.Math.Clamp(FixedTimeStep, 0.005f, 0.02f);
        vm.Camera.Mode = CameraMode;
        vm.Volume = Volume;
        var audio = new AudioConfig();
        vm.SquealVolume = audio.TireSqueal;
        vm.GearShiftVolume = audio.GearShift;
        vm.LightBrightness = LightBrightness;
        VehicleLights.OriginalControl = OriginalLightControl;
    }
}
