using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Platform;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Game.Input;

/// <summary>
/// Force Feedback und Vibration – sauberes modernes Setup, aufgebaut auf dem Port von
/// Client/Assets/TDU2Map/Runtime/Input/TDU2ForceFeedback.cs.
///  * Ein gemeinsamer Effekt-Frame (<see cref="ComputeFrame"/>) beliefert Lenkrad und Pad mit denselben Kanälen:
///    geschwindigkeitsabhängige Feder mit progressivem Lenkanschlag, lastempfindliches Lenkmoment aus den Vorderrädern,
///    Understeer-Abschwächung, Schlupf, ABS-Puls, Federungsschläge/Bumps, Offroad-Rumble, Kollisionsimpuls,
///    Luft-Effekt und Drehzahl-Vibration mit Limiter-Puls.
///  * Lenkrad: Logitech SDK (optional, LEDs) oder herstellerunabhängiges DirectInput-FFB (<see cref="DirectInputFfb"/>).
///  * Gamepad: XInput-Motoren. Links = Motor/Untergrund/Schlupf/Schläge, rechts = Drehzahl/Slip/ABS.
/// Im Menü/Pause, ohne Fokus oder ohne Auto werden alle Kräfte gestoppt.
/// </summary>
public static class TDU2ForceFeedback
{
    // ------------------------------------------------------------------ Einstellungen (TDU2Options)
    public static bool Enabled = true;             // "force_feedback"
    public static float Strength = 0.5f;           // "vibration" 0..1 (Regler 5/10 = Originalstärke)
    public static float Damping = 0.5f;            // "damping" 0..1
    public static bool InvertAligningTorque;       // Lenkmoment umkehren, falls das Rad falsch herum zieht
    public static bool InvertDirectInput;          // nur DirectInput-Konstantkraft umkehren

    public static float PavedRumble = 0f;          // Rütteln auf Asphalt im Verhältnis zum Feldweg (0 = aus)
    /// <summary>
    /// Modernes Setup: keine Vibration im Stand. Während der Fahrt beginnt sie sanft ab ca. 30 % der Drehzahlgrenze
    /// (~2000-2500 rpm), wächst progressiv und wird nahe der Rotzone deutlich; am Limiter kommt ein Puls dazu.
    /// Gilt für Pad und Lenkrad.
    /// </summary>
    public static float RpmVibrationStart = 0.30f, RpmVibrationRedline = 0.35f;
    public static bool WheelRpmVibration = true;
    public static float MinCollisionSpeed = 0.8f, ImpactDecay = 2.5f;

    // ------------------------------------------------------------------ Moderne Zusatzkanäle
    /// <summary>Schlupf/Understeer-Signalstärke (0 = aus).</summary>
    public static float SlipSensitivity = 1f;
    /// <summary>Schlag-Erkennung aus der Federung (0 = aus).</summary>
    public static float BumpSensitivity = 1f;
    /// <summary>ABS-Pulsstärke (0 = aus).</summary>
    public static float AbsStrength = 1f;
    /// <summary>Zusatzsteifigkeit am Lenkanschlag (0 = aus).</summary>
    public static float SteeringLockStrength = 1f;
    /// <summary>Ab diesem Anteil des maximalen Lenkwinkels baut sich der Anschlag auf.</summary>
    public static float LockFalloffStart = 0.72f;
    public static float AbsPulseHz = 12f;
    /// <summary>Straßenmitte-Toleranz und Suchradius für die Asphalt-Erkennung (m).</summary>
    public static float PavedHalfWidth = 6f, PavedSearchRadius = 12f;

    /// <summary>Diagnose (Debug-Overlay): zuletzt gesetzte Pad-Motoren, Stoß, Untergrund, FFB-Gerät.</summary>
    public static float LastPadLow, LastPadHigh;
    public static bool LastOffPaved;
    /// <summary>Diagnose: letzte Straßenart (-1 = Gelände/kein Netz), Feldweg-Anteil und Drehzahl-Anteil am Rad-Rütteln.</summary>
    public static int LastRoadType = -1, LastRoadRumble, LastRpmRumble;
    /// <summary>Diagnose der modernen Kanäle (Debug-Overlay).</summary>
    public static float LastSlip, LastAbs, LastBump, LastLock, LastUndersteer, LastWheelspin;
    public static int LastRoughWheels;
    /// <summary>Diagnose der tatsächlich am Lenkrad gesetzten Kräfte (unabhängig vom Pad).</summary>
    public static int LastWheelAlign, LastWheelRumble;
    public static float Impact => _impact;
    public static string WheelDevice => _di?.DeviceName ?? (_logiOk ? "Logitech SDK" : "");

    static DirectInputFfb? _di;
    /// <summary>Geräteliste, für die DirectInput-FFB schon versucht wurde (verhindert Dauer-Log bei Pad-only).</summary>
    static string _diSignature = "";
    static bool _diTried;
    static float _nextDiRetry;
    static bool _logiTried, _logiOk;
    static readonly bool[] _logiFx = new bool[5];   // Feder, Konstant, Dämpfer, Feldweg, Luft
    static int _padJoy = -1;
    static float _nextPadScan;
    static bool _padOk = true;
    static float _impact, _impactSide;
    static float _surfAt;
    static bool _offPaved = true;
    static VehicleManager? _hooked;
    static bool _brokenHooked;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static float Now => (float)_clock.Elapsed.TotalSeconds;

    // ------------------------------------------------------------------ Zentrales Effekt-Frame
    /// <summary>
    /// Einmal je Bild aus der Fahrzeugphysik berechnet und an Lenkrad UND Pad geliefert.
    /// So können sich die Kanäle nicht auseinanderlaufen (früher getrennte Kurven in UpdateWheel/UpdatePad).
    /// </summary>
    struct FfbFrame
    {
        public float Kmh, SpeedT;
        public bool Grounded, Air;
        public int SpringCoef, SpringSat, Align, Damper, Rumble, RpmRumble;
        public float PadLow, PadHigh;
        public float Slip, Abs, Bump, Lock, Understeer, Wheelspin;
    }

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // ------------------------------------------------------------------ Stoßquellen
    static void Hook(VehicleManager vm)
    {
        if (_hooked == vm) return;
        if (_hooked != null) _hooked.BodyImpact -= OnBodyImpact;
        _hooked = vm;
        if (vm != null) vm.BodyImpact += OnBodyImpact;
        if (!_brokenHooked && vm != null) { WorldActorBodies.Broken += OnBroken; _brokenHooked = true; }
    }

    static void OnBodyImpact(float speed)
    {
        if (speed < MinCollisionSpeed) return;
        // Die Engine meldet nur die Geschwindigkeit (keine Normale): der Stoß geht als Konstantkraft in die Lenkung.
        ReportImpact(0.3f + (speed - MinCollisionSpeed) / 10f, 0f);
    }

    static void OnBroken(string sector, string name, float hitSpeed)
    {
        // WorldActorBodies.Broken liefert keinen auslösenden Körper: nur bei fahrendem Spielerauto als eigenen Treffer werten.
        if (_hooked?.Current == null || hitSpeed < MinCollisionSpeed || MathF.Abs(_hooked.Current.SpeedKmh) < 3f) return;
        ReportImpact(Math.Clamp(0.35f + hitSpeed / 12f, 0f, 1f), 0f);
    }

    public static void ReportImpact(float strength, float side)
    {
        strength = System.Math.Clamp(strength, 0f, 1f);
        if (strength <= _impact) return;
        _impact = strength;
        _impactSide = System.Math.Clamp(side, -1f, 1f);
    }

    /// <summary>Einmal je Bild nach der Fahreingabe (VehicleModule). active = nicht pausiert/eingefroren.</summary>
    public static void Update(VehicleManager vm, bool active, float dt)
    {
        Hook(vm);
        float step = System.Math.Clamp(dt, 0f, 0.1f);
        _impact = Mathf.MoveTowards(_impact, 0f, step * ImpactDecay);
        var v = vm?.Current;
        // Untergrund/Diagnose auch dann aktuell halten, wenn gerade kein Gerät oder kein aktives Fahrzeug vorliegt.
        LastOffPaved = v != null ? OffPaved(v) : true;
        bool on = Enabled && active && v != null && v.Car != null && Joysticks.IsFocused && !TDU2Controls.Blocked;
        if (!on)
        {
            _impact = 0f;
            StopWheel(); StopPad();
            LastRoadType = -1; LastRoadRumble = LastRpmRumble = 0;
            LastSlip = LastAbs = LastBump = LastLock = LastUndersteer = LastWheelspin = 0f;
            return;
        }

        float s = System.Math.Clamp(Strength * 2f, 0f, 1f);
        float kmh = MathF.Abs(v!.SpeedKmh);
        FfbFrame frame = ComputeFrame(v, step, s, kmh);
        UpdateWheel(v, on, frame);
        UpdatePad(v, on, frame);
    }

    /// <summary>
    /// Moderner Effektstack: Feder mit Anschlag, lastempfindliches Lenkmoment, Understeer, ABS-Puls,
    /// Federungsschläge, Schlupf/Rad Drehzahl und Drehzahl-Vibration – für Rad und Pad identisch.
    /// </summary>
    static FfbFrame ComputeFrame(Vehicle v, float dt, float s, float kmh)
    {
        var f = new FfbFrame
        {
            Kmh = kmh,
            SpeedT = Mathf.Clamp01(kmh / Mathf.Lerp(250f, 80f, TDU2InputDevices.SpeedFactor))
        };
        f.Grounded = v.AnyWheelGrounded;
        f.Air = !f.Grounded;

        // ---- Lenkanschlag: progressiver, harter Endanschlag statt nur weicher Feder ----
        float maxAngle = 25f;
        try { if (v.Config != null) maxAngle = v.Config.GetCurve("SteeringMaxAngle", 25f).Evaluate(kmh); }
        catch (Exception) { }
        f.Lock = maxAngle > 1f ? Mathf.Clamp01(MathF.Abs(v.SteeringAngle) / maxAngle) : 0f;
        float lockT = Mathf.Clamp01((f.Lock - LockFalloffStart) / Math.Max(0.01f, 1f - LockFalloffStart));
        float lockAmount = Mathf.Clamp01(Smooth(lockT) * System.Math.Clamp(SteeringLockStrength, 0f, 2f));

        f.SpringSat = (int)MathF.Round(Mathf.Lerp(85f, 100f, lockAmount) * s);
        f.SpringCoef = (int)MathF.Round(Mathf.Lerp(Mathf.Lerp(15f, 75f, f.SpeedT), 95f, lockAmount) * s);
        if (f.Air) f.SpringCoef = 5;

        // ---- Reifenkräfte, Schlupf, Federungsschläge ----
        float fy = 0f, fz = 0f, frontSlip = 0f, latSlip = 0f, bumpV = 0f, spin = 0f;
        int frontGrounded = 0, grounded = 0;
        float invDt = 1f / Mathf.Max(0.002f, dt);
        foreach (var w in v.Wheels)
        {
            if (!w.Grounded) continue;
            grounded++;
            if (w.Front)
            {
                frontGrounded++;
                fy += w.Fy; fz += w.Fz;
                frontSlip += MathF.Abs(w.SlipAngle);
            }
            latSlip += MathF.Abs(w.LatSlipVel);
            bumpV += MathF.Max(0f, (w.Compression - w.PrevCompression) * invDt);
            if (w.Driven && v.AccelPedal > 0.1f) spin = Mathf.Max(spin, MathF.Abs(w.SlipRatio));
        }
        if (frontGrounded > 0) frontSlip /= frontGrounded;
        if (grounded > 0) latSlip /= grounded;
        // Federungsschläge müssen VOR dem ABS/Konstant-Kraft-Block berechnet werden – sonst war f.Bump dort immer 0.
        f.Bump = Mathf.Clamp01((bumpV - 0.08f) / 1.2f) * System.Math.Clamp(BumpSensitivity, 0f, 2f);

        // Reifen-Lastempfindlichkeit: mehr Radlast → weniger Lenkmoment pro Newton.
        float mu = fz > 1f ? Mathf.Clamp(fy / fz, -1.5f, 1.5f) : 0f;
        float loadT = Mathf.Clamp01(fz / Mathf.Max(1000f, (v.Car?.Mass ?? 1500f) * 9.81f));
        mu *= Mathf.Lerp(1f, 0.8f, loadT);

        // Understeer: große Vorderrad-Schlupfwinkel nehmen das Lenkmoment weg ("Lenkung wird leicht").
        f.Understeer = Mathf.Clamp01((frontSlip - 0.04f) / 0.18f);   // schon ab ~2-3° Vorderrad-Schlupf
        f.Slip = Mathf.Clamp01(Mathf.Max(latSlip / 2.5f, f.Understeer * 0.85f)) * System.Math.Clamp(SlipSensitivity, 0f, 2f);
        f.Wheelspin = kmh > 5f && v.AccelPedal > 0.1f ? Mathf.Clamp01((spin - 0.08f) / 0.35f) : 0f;

        float align = mu * 45f * Mathf.Lerp(1f, 0.45f, f.Understeer);
        f.Align = (int)MathF.Round(Mathf.Clamp(align * s * (InvertAligningTorque ? -1f : 1f), -100f, 100f));
        if (_impact > 0.05f)
            f.Align = (int)Mathf.Clamp(f.Align + MathF.Round(_impactSide * _impact * 100f * s), -100f, 100f);

        f.Damper = (int)MathF.Round(Mathf.Lerp(40f, 15f, f.SpeedT) * Damping * 2f * s * Mathf.Lerp(1f, 0.8f, f.Slip));

        // ---- ABS: wie in der Physik negativer Längsschlupf = blockierendes Rad ----
        float wheelSlip = 0f;
        foreach (var w in v.Wheels) if (w.Grounded) wheelSlip = Mathf.Min(wheelSlip, w.SlipRatio);
        float absSlip = -0.1f;
        if (v.Car != null)
        {
            switch (v.Aids)
            {
                case DrivingAids.Secure: absSlip = v.Car.AbsSlipSecure; break;
                case DrivingAids.HyperSport: absSlip = v.Car.AbsSlipHyper; break;
                case DrivingAids.Off: absSlip = -10f; break;
                default: absSlip = v.Car.AbsSlipSport; break;
            }
        }
        bool absPossible = v.Aids != DrivingAids.Off && (v.Car == null || v.Car.Abs) && kmh > 15f;
        f.Abs = absPossible && v.BrakePedal > 0.05f && wheelSlip < absSlip
            ? Mathf.Clamp01((absSlip - wheelSlip) / 0.15f) * System.Math.Clamp(AbsStrength, 0f, 2f)
            : 0f;

        // ABS und Schläge zusätzlich in die Konstantkraft – greift auch, wenn das Gerät keinen Sinus-Effekt hat.
        if (f.Abs > 0f || f.Bump > 0.15f)
        {
            float sign = Mathf.Repeat(Now * Mathf.Max(1f, AbsPulseHz), 1f) < 0.5f ? 1f : -1f;
            f.Align = (int)Mathf.Clamp(f.Align + MathF.Round((f.Abs * 5f + f.Bump * 4f) * sign * s), -100f, 100f);
        }

        // ---- Untergrund: Asphalt rüttelt nicht dauerhaft, nur echte Federungsschläge ----
        int roughWheels = RoughWheels(v);
        bool off = OffPaved(v);
        float rough = 0f;
        if (f.Grounded && kmh > 5f)
        {
            if (roughWheels > 0 && off) rough = Mathf.Lerp(10f, 40f, f.SpeedT) + f.Bump * 25f;
            else if (PavedRumble > 0f) rough = Mathf.Lerp(0f, PavedRumble * 40f, f.SpeedT);
            else rough = f.Bump * 8f;   // kein Dauer-Rütteln, nur echte Federungsschläge
        }
        float pulseHz = Mathf.Max(1f, AbsPulseHz);
        float absPulse = f.Abs > 0f && Mathf.Repeat(Now * pulseHz, 1f) < 0.5f ? f.Abs * 14f : 0f;
        f.Rumble = (int)MathF.Round(Mathf.Clamp((rough + f.Slip * 14f + absPulse) * s, 0f, 100f));

        // ---- Drehzahl: sanfter Anstieg ab ~30 %, Limiter-Puls. Im Stand = 0. ----
        int rpmRumble = 0;
        if (WheelRpmVibration && !f.Air && kmh > 3f && v.Rpm > 50f)
        {
            float rpmT = v.Car != null && v.Car.RedLine > 0f ? Mathf.Clamp01(v.Rpm / v.Car.RedLine) : 0f;
            float vibT = Mathf.Clamp01((rpmT - RpmVibrationStart) / Math.Max(0.01f, 1f - RpmVibrationStart));
            rpmRumble = (int)MathF.Round(Mathf.Lerp(0f, RpmVibrationRedline * 50f, Smooth(vibT)) * s);
            if (rpmT > 0.97f) rpmRumble += Mathf.Repeat(Now * 12f, 1f) < 0.5f ? 6 : 0;
            rpmRumble = System.Math.Clamp(rpmRumble, 0, 25);
        }
        f.RpmRumble = rpmRumble;

        // ---- Pad: dieselben Kanäle, auf zwei Motoren verteilt ----
        float low = 0f, high = 0f;
        float rpmPad = v.Car != null && v.Car.RedLine > 0f ? Mathf.Clamp01(v.Rpm / v.Car.RedLine) : 0f;
        float vibPad = Mathf.Clamp01((rpmPad - RpmVibrationStart) / Math.Max(0.01f, 1f - RpmVibrationStart));
        if (kmh > 3f && vibPad > 0f && v.Rpm > 50f) high += Mathf.Lerp(0f, RpmVibrationRedline, Smooth(vibPad));
        if (kmh > 3f && rpmPad > 0.97f) high += Mathf.Repeat(Now * 12f, 1f) < 0.5f ? 0.25f : 0f;
        if (kmh > 3f && roughWheels > 0 && off) low += Mathf.Lerp(0.12f, 0.4f, Mathf.Clamp01(kmh / 120f));
        low += f.Bump * 0.35f;
        low += f.Wheelspin * 0.25f;
        high += f.Slip * 0.28f;
        if (f.Abs > 0f && Mathf.Repeat(Now * pulseHz, 1f) < 0.5f) high += f.Abs * 0.30f;
        low += _impact; high += _impact * 0.5f;
        f.PadLow = Mathf.Clamp01(low * s);
        f.PadHigh = Mathf.Clamp01(high * s);

        LastSlip = f.Slip; LastAbs = f.Abs; LastBump = f.Bump;
        LastLock = f.Lock; LastUndersteer = f.Understeer; LastWheelspin = f.Wheelspin;
        LastRoadRumble = f.Rumble; LastRpmRumble = rpmRumble;
        return f;
    }

    /// <summary>Alles stoppen (Unload/Fokusverlust) und Geräte freigeben.</summary>
    public static void Shutdown()
    {
        StopWheel();
        StopPad();
        _di?.Dispose(); _di = null; _diTried = false; _diSignature = "";
        if (_logiOk)
        {
            try { Logi.Shutdown(); } catch (Exception) { }
            _logiOk = false;
        }
        _logiTried = false;
        if (_hooked != null) { _hooked.BodyImpact -= OnBodyImpact; _hooked = null; }
        if (_brokenHooked) { WorldActorBodies.Broken -= OnBroken; _brokenHooked = false; }
    }

    public static string DebugLines() =>
        $"FFB: {Enabled} strength {Strength:0.00} damp {Damping:0.00} wheel \"{WheelDevice}\" pad {_padJoy} " +
        $"low/high {LastPadLow:0.00}/{LastPadHigh:0.00} impact {_impact:0.00} offroad {LastOffPaved} " +
        $"road {LastRoadType} rumble {LastRoadRumble}+{LastRpmRumble} " +
        $"slip {LastSlip:0.00} understeer {LastUndersteer:0.00} abs {LastAbs:0.00} bump {LastBump:0.00} " +
        $"lock {LastLock:0.00} wheelspin {LastWheelspin:0.00} roughWheels {LastRoughWheels} " +
        $"wheelAlign {LastWheelAlign} wheelRumble {LastWheelRumble}";

    // ------------------------------------------------------------------ Untergrund: Asphalt oder Feldweg?
    // Die Räder melden "Erde", sobald sie den Gelände-Kollider treffen – auf dem liegen auch die meisten asphaltierten
    // Straßen. Deshalb zusätzlich das Straßennetz fragen: nahe einer Straße, die kein Feldweg ist (Art 1), gilt Asphalt.
    static bool OffPaved(Vehicle v)
    {
        if (Now < _surfAt) return _offPaved;
        _surfAt = Now + 0.25f;
        if (v == null) { _offPaved = true; LastRoadType = -1; LastRoughWheels = 0; return true; }
        var mm = _hooked?.Minimap;

        // Räder zuerst: Surface 0 = Asphalt-Collider, 1 = Gras, 2 = Erde. Wenn alle Räder auf Asphalt
        // stehen, darf ein entfernter Feldweg-Punkt nicht "Dirt" melden (das war der Fehler auf der Straße).
        int rough = 0;
        foreach (var w in v.Wheels) if (w.Grounded && w.Surface != 0) rough++;
        LastRoughWheels = rough;

        _offPaved = true;
        LastRoadType = -1;
        if (mm != null && _hooked != null)
        {
            var pos = _hooked.CurrentPositionGame;
            int t = mm.NearestRoadType(pos, Mathf.Max(1f, PavedHalfWidth));
            if (t < 0) t = mm.NearestRoadType(pos, Mathf.Max(2f, PavedSearchRadius));
            LastRoadType = t;                        // 1 = Feldweg, 3 = Stadt, 0/2/4.. = Asphalt
            _offPaved = rough > 0 && (t < 0 || t == 1);
        }
        else _offPaved = rough > 0;
        return _offPaved;
    }

    /// <summary>Räder mit nicht-asphaltiertem Untergrund – jeden Frame neu (Unity TDU2ForceFeedback roughWheels).</summary>
    static int RoughWheels(Vehicle v)
    {
        int n = 0;
        foreach (var w in v.Wheels) if (w.Grounded && w.Surface != 0) n++;
        return n;
    }

    // ------------------------------------------------------------------ Lenkrad
    static bool IsLogitechName(string n)
    {
        n = (n ?? "").ToLowerInvariant();
        return n.Contains("logitech") || n.Contains("g25") || n.Contains("g27") || n.Contains("g29") || n.Contains("g920") || n.Contains("g923")
            || n.Contains("driving force") || n.Contains("momo");
    }

    static void UpdateWheel(Vehicle v, bool on, FfbFrame f)
    {
        // Rad und Pad laufen IMMER: die Geräteerkennung (TDU2InputDevices.IsWheel) darf kein FFB abschalten.
        // Erkanntes Lenkrad -> diesen Namen bevorzugen. Unbekanntes/als Pad erkanntes Rad -> ersten DirectInput-FFB nehmen.
        bool knownWheel = TDU2InputDevices.IsWheel;
        string dev = knownWheel ? (TDU2InputDevices.DeviceName ?? "") : "";
        if (!_logiTried && knownWheel && IsLogitechName(dev)) TryLogitech();
        bool useLogi = _logiOk && knownWheel && IsLogitechName(dev);

        // Nur bei geänderter Geräteliste erneut öffnen – sonst würde ein fehlendes Rad alle 2 s ins Log schreiben.
        string signature = dev + "|" + string.Join("|", Joysticks.Names);
        bool signatureChanged = !string.Equals(signature, _diSignature, StringComparison.Ordinal);
        if (!useLogi && signature.Length > 0 && signatureChanged && Now >= _nextDiRetry)
        {
            _nextDiRetry = Now + 2f;
            _diTried = true; _diSignature = signature;
            _di?.Dispose(); _di = null;
            var tryDi = new DirectInputFfb();
            // Leerer Name = beliebiges angeschlossenes Force-Feedback-Gerät (auch ohne Geräteprofil).
            if (tryDi.Open(dev)) _di = tryDi;
            else tryDi.Dispose();
        }
        if (!on) { StopWheel(); return; }

        int rumble = System.Math.Clamp(f.Rumble + f.RpmRumble, 0, 100);
        LastWheelAlign = f.Align; LastWheelRumble = rumble;
        if (useLogi) ApplyLogitech(v, f.SpringCoef, f.SpringSat, f.Align, f.Damper, rumble, f.Air);
        else if (_di != null && _di.Ok)
        {
            try
            {
                _di.Spring(f.SpringCoef, f.SpringSat);
                _di.Constant(InvertDirectInput ? -f.Align : f.Align);
                _di.Damper(f.Damper);
                _di.Rumble(f.Air ? 0 : rumble);
            }
            catch (Exception ex)
            {
                Log.Warn("DirectInput Force Feedback abgeschaltet: " + ex.Message);
                _di.Dispose(); _di = null; _diSignature = ""; _nextDiRetry = Now + 3f;
            }
        }
    }

    static void TryLogitech()
    {
        _logiTried = true;
        try
        {
            _logiOk = Logi.SteeringInitialize(true);
            if (_logiOk)
            {
                Logi.Update();
                var sb = new StringBuilder(256);
                Logi.GetFriendlyProductName(0, sb, 256);
                _logiOk = Logi.IsConnected(0);
                Log.Info("[TDU2] Logitech-Lenkrad-SDK: " + (_logiOk ? sb.ToString() : "kein Logitech-Lenkrad – nehme DirectInput"));
            }
            else Log.Info("[TDU2] Logitech-Lenkrad-SDK nicht verfügbar (G HUB?) – nehme DirectInput");
        }
        catch (Exception ex)
        {
            _logiOk = false;
            Log.Warn("[TDU2] Logitech-SDK nicht ladbar (" + ex.Message + ") – nehme DirectInput");
        }
    }

    static void ApplyLogitech(Vehicle v, int springCoef, int springSat, int constMag, int damp, int rumble, bool air)
    {
        try
        {
            if (!Logi.Update() || !Logi.IsConnected(0)) { StopLogi(); return; }
            if (!Logi.HasForceFeedback(0)) { StopLogi(); return; }
            Logi.PlaySpringForce(0, 0, springSat, springCoef); _logiFx[0] = true;
            if (!air && (constMag != 0 || _logiFx[1])) { Logi.PlayConstantForce(0, constMag); _logiFx[1] = true; }
            if (damp > 0) { Logi.PlayDamperForce(0, damp); _logiFx[2] = true; }
            else if (_logiFx[2]) { Logi.StopDamperForce(0); _logiFx[2] = false; }
            // Ride-Effekt trägt Feldweg UND Motor-Vibration (Drehzahl) – Logitech hat keinen eigenen Motor-Kanal.
            if (rumble > 0) { Logi.PlayDirtRoadEffect(0, rumble); _logiFx[3] = true; }
            else if (_logiFx[3]) { Logi.StopDirtRoadEffect(0); _logiFx[3] = false; }
            if (air) { if (!_logiFx[4]) { Logi.PlayCarAirborne(0); _logiFx[4] = true; } }
            else if (_logiFx[4]) { Logi.StopCarAirborne(0); _logiFx[4] = false; }
            if (v.Car != null) Logi.PlayLeds(0, v.Rpm, v.Car.RedLine * 0.75f, v.Car.RedLine);
        }
        catch (Exception ex)
        {
            _logiOk = false;
            Log.Warn("Logitech Force Feedback abgeschaltet: " + ex.Message);
        }
    }

    static void StopLogi()
    {
        if (!_logiOk) return;
        try
        {
            if (_logiFx[0]) { Logi.StopSpringForce(0); _logiFx[0] = false; }
            if (_logiFx[1]) { Logi.StopConstantForce(0); _logiFx[1] = false; }
            if (_logiFx[2]) { Logi.StopDamperForce(0); _logiFx[2] = false; }
            if (_logiFx[3]) { Logi.StopDirtRoadEffect(0); _logiFx[3] = false; }
            if (_logiFx[4]) { Logi.StopCarAirborne(0); _logiFx[4] = false; }
        }
        catch (Exception) { }
    }

    static void StopWheel()
    {
        try { _di?.Silence(); } catch (Exception) { }
        StopLogi();
        // Nur gerätespezifische Werte hier zurücksetzen: StopWheel() läuft auf dem Pad jeden Frame
        // und darf die gemeinsamen Frame-Diagnosewerte (Slip/ABS/Bump/...) nicht überschreiben.
        LastWheelAlign = LastWheelRumble = 0;
    }

    // ------------------------------------------------------------------ Gamepad (XInput-Motoren)
    static void UpdatePad(Vehicle v, bool on, FfbFrame f)
    {
        // Pad-Vibration ist unabhängig von der Geräteerkennung: auch im Lenkrad-Modus dürfen die Motoren laufen.
        if (!_padOk) { StopPad(); return; }
        try
        {
            if (_padJoy < 0 || Now >= _nextPadScan)
            {
                _nextPadScan = Now + 2f;
                int found = -1;
                for (int j = 1; j <= Joysticks.MaxJoysticks; j++)
                    if (Joysticks.IsGamepad(j)) { found = j; break; }
                if (found != _padJoy && _padJoy >= 0) Joysticks.SetVibration(_padJoy, 0f, 0f);
                _padJoy = found;
            }
            if (_padJoy < 0) return;
            if (!on || Strength <= 0.001f) { StopPad(); return; }

            // Alle Kanäle kommen aus dem gemeinsamen Frame – kein zweites, abweichendes Profil mehr.
            LastPadLow = f.PadLow;
            LastPadHigh = f.PadHigh;
            LastOffPaved = _offPaved;
            if (!Joysticks.SetVibration(_padJoy, LastPadLow, LastPadHigh))
            {
                _padJoy = -1; _nextPadScan = Now + 3f;   // Pad kurz weg: später neu suchen
            }
        }
        catch (Exception ex)
        {
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException) _padOk = false;
            else { _padJoy = -1; _nextPadScan = Now + 3f; }
            Log.Warn("XInput-Vibration: " + ex.Message);
        }
    }

    static void StopPad()
    {
        LastPadLow = LastPadHigh = 0f;
        if (_padJoy < 0) return;
        try { Joysticks.SetVibration(_padJoy, 0f, 0f); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ Logitech Steering Wheel SDK (nur das Nötige)
    // Die DLL liegt im Unity-Projekt unter Assets/Plugins/LogitechSDK. Ohne G HUB / Logitech Gaming Software ist der
    // DllImport-Aufruf einfach nicht verfügbar; dann übernimmt DirectInput-FFB.
    static class Logi
    {
        const string Dll = "LogitechSteeringWheelEnginesWrapper";
        // EntryPoints wie im Unity-Wrapper (dort heißen die Methoden genauso wie die exportierten C-Funktionen).
        [DllImport(Dll, EntryPoint = "LogiSteeringInitialize", CallingConvention = CallingConvention.Cdecl)] public static extern bool SteeringInitialize(bool ignoreXInputControllers);
        [DllImport(Dll, EntryPoint = "LogiSteeringShutdown", CallingConvention = CallingConvention.Cdecl)] public static extern bool Shutdown();
        [DllImport(Dll, EntryPoint = "LogiUpdate", CallingConvention = CallingConvention.Cdecl)] public static extern bool Update();
        [DllImport(Dll, EntryPoint = "LogiGetFriendlyProductName", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetFriendlyProductName(int index, StringBuilder buffer, int bufferSize);
        [DllImport(Dll, EntryPoint = "LogiIsConnected", CallingConvention = CallingConvention.Cdecl)] public static extern bool IsConnected(int index);
        [DllImport(Dll, EntryPoint = "LogiHasForceFeedback", CallingConvention = CallingConvention.Cdecl)] public static extern bool HasForceFeedback(int index);
        [DllImport(Dll, EntryPoint = "LogiPlaySpringForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlaySpringForce(int index, int offsetPercentage, int saturationPercentage, int coefficientPercentage);
        [DllImport(Dll, EntryPoint = "LogiStopSpringForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool StopSpringForce(int index);
        [DllImport(Dll, EntryPoint = "LogiPlayConstantForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlayConstantForce(int index, int magnitudePercentage);
        [DllImport(Dll, EntryPoint = "LogiStopConstantForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool StopConstantForce(int index);
        [DllImport(Dll, EntryPoint = "LogiPlayDamperForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlayDamperForce(int index, int coefficientPercentage);
        [DllImport(Dll, EntryPoint = "LogiStopDamperForce", CallingConvention = CallingConvention.Cdecl)] public static extern bool StopDamperForce(int index);
        [DllImport(Dll, EntryPoint = "LogiPlayDirtRoadEffect", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlayDirtRoadEffect(int index, int magnitudePercentage);
        [DllImport(Dll, EntryPoint = "LogiStopDirtRoadEffect", CallingConvention = CallingConvention.Cdecl)] public static extern bool StopDirtRoadEffect(int index);
        [DllImport(Dll, EntryPoint = "LogiPlayCarAirborne", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlayCarAirborne(int index);
        [DllImport(Dll, EntryPoint = "LogiStopCarAirborne", CallingConvention = CallingConvention.Cdecl)] public static extern bool StopCarAirborne(int index);
        [DllImport(Dll, EntryPoint = "LogiPlayLeds", CallingConvention = CallingConvention.Cdecl)] public static extern bool PlayLeds(int index, float currentRPM, float rpmFirstLedTurnsOn, float rpmRedLine);
    }
}
