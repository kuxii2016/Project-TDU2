using Paradise.Core.Diagnostics;
using Paradise.Core.Math;
using Paradise.Core.Platform;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Game.Input;

/// <summary>
/// Force Feedback und Vibration – Port von Client/Assets/TDU2Map/Runtime/Input/TDU2ForceFeedback.cs.
///  * Lenkrad: Logitech Steering Wheel SDK (Schaltblitz-LEDs, wenn die DLL installiert ist), sonst herstellerunabhängiges
///    DirectInput-FFB (<see cref="DirectInputFfb"/>): Rückstellkraft nach Tempo, Lenkmoment aus der Seitenkraft der Vorderräder,
///    Dämpfung, Rütteln auf Feldwegen, Stöße bei Kollisionen, "in der Luft".
///  * Gamepad: XInput-Motoren. Links = Motor/Untergrund/Stöße, rechts = Radschlupf/Drehzahlbegrenzer.
/// Einstellungen aus dem Optionsmenü (Steuerung → Lenkrad). Im Menü/Pause, ohne Fokus oder ohne Auto werden alle Kräfte gestoppt.
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
    public static float RpmVibrationIdle = 0.04f, RpmVibrationRedline = 0.2f;
    public static float MinCollisionSpeed = 0.8f, ImpactDecay = 2.5f;

    /// <summary>Diagnose (Debug-Overlay): zuletzt gesetzte Pad-Motoren, Stoß, Untergrund, FFB-Gerät.</summary>
    public static float LastPadLow, LastPadHigh;
    public static bool LastOffPaved;
    public static float Impact => _impact;
    public static string WheelDevice => _di?.DeviceName ?? (_logiOk ? "Logitech SDK" : "");

    static DirectInputFfb? _di;
    static string _diFor = "";
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
        if (!on) { _impact = 0f; StopWheel(); StopPad(); return; }
        UpdateWheel(v!, on);
        UpdatePad(v!, on);
    }

    /// <summary>Alles stoppen (Unload/Fokusverlust) und Geräte freigeben.</summary>
    public static void Shutdown()
    {
        StopWheel();
        StopPad();
        _di?.Dispose(); _di = null; _diTried = false; _diFor = "";
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
        $"low/high {LastPadLow:0.00}/{LastPadHigh:0.00} impact {_impact:0.00} offroad {LastOffPaved}";

    // ------------------------------------------------------------------ Untergrund: Asphalt oder Feldweg?
    // Die Räder melden "Erde", sobald sie den Gelände-Kollider treffen – auf dem liegen auch die meisten asphaltierten
    // Straßen. Deshalb zusätzlich das Straßennetz fragen: nahe einer Straße, die kein Feldweg ist (Art 1), gilt Asphalt.
    static bool OffPaved(Vehicle v)
    {
        if (Now < _surfAt) return _offPaved;
        _surfAt = Now + 0.25f;
        if (v == null) { _offPaved = true; return true; }   // ohne Straßennetz/Fahrzeug wie Unity: Feldweg
        var mm = _hooked?.Minimap;
        // Wie Unity: nur die Straßenart wird 0,25 s gecacht. Die Räder (Surface != 0) werden dagegen
        // jeden Frame neu gezählt – sonst bleibt Rütteln nach dem Verlassen eines Feldwegs "hängen".
        _offPaved = true;
        if (mm != null && _hooked != null)
        {
            int t = mm.NearestRoadType(_hooked.CurrentPositionGame, 9f);
            _offPaved = t < 0 || t == 1;   // -1 = Gelände, 1 = Feldweg; alles andere ist Asphalt
        }
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

    static void UpdateWheel(Vehicle v, bool on)
    {
        if (!TDU2InputDevices.IsWheel) { StopWheel(); return; }
        string dev = TDU2InputDevices.DeviceName;
        if (!_logiTried && IsLogitechName(dev)) TryLogitech();
        bool useLogi = _logiOk && IsLogitechName(dev);

        if (!useLogi && (_di == null || !string.Equals(_diFor, dev, StringComparison.OrdinalIgnoreCase)) && Now >= _nextDiRetry)
        {
            _nextDiRetry = Now + 2f;
            _diTried = true; _diFor = dev;
            _di?.Dispose(); _di = null;
            var tryDi = new DirectInputFfb();
            if (tryDi.Open(dev)) _di = tryDi;
            else tryDi.Dispose();
        }
        if (!on)
        {
            StopWheel();
            return;
        }

        // ---- gemeinsame Werte (Prozent wie im Logitech-SDK)
        float s = System.Math.Clamp(Strength * 2f, 0f, 1f);
        float kmh = MathF.Abs(v.SpeedKmh);
        float speedT = System.Math.Clamp(kmh / Mathf.Lerp(250f, 80f, TDU2InputDevices.SpeedFactor), 0f, 1f);
        bool grounded = v.AnyWheelGrounded;
        bool air = !grounded;
        // Rückstellkraft: im Stand weich, mit Tempo fester
        int springSat = (int)MathF.Round(85f * s);
        int springCoef = grounded ? (int)MathF.Round(Mathf.Lerp(15f, 75f, speedT) * s) : 5;
        // Lenkmoment aus der Seitenkraft der Vorderräder, Stöße obendrauf
        float fy = 0f, fz = 0f;
        foreach (var w in v.Wheels) if (w.Front && w.Grounded) { fy += w.Fy; fz += w.Fz; }
        float mu = fz > 1f ? System.Math.Clamp(fy / fz, -1.5f, 1.5f) : 0f;
        int constMag = grounded ? (int)MathF.Round(System.Math.Clamp(mu * 45f * s * (InvertAligningTorque ? -1f : 1f), -100f, 100f)) : 0;
        if (_impact > 0.05f) constMag = (int)System.Math.Clamp(constMag + MathF.Round(_impactSide * _impact * 100f * s), -100f, 100f);
        // Dämpfung (Regler), im Stand etwas stärker
        int damp = (int)MathF.Round(Mathf.Lerp(40f, 15f, speedT) * Damping * 2f * s);
        // Feldweg/Gelände rüttelt; auf Asphalt nur ein kaum spürbares Grundrauschen
        int rough = 0;
        int roughWheels = RoughWheels(v);
        if (kmh > 5f)
        {
            if (roughWheels > 0 && OffPaved(v)) rough = (int)MathF.Round(Mathf.Lerp(10f, 40f, speedT) * s);
            else if (grounded) rough = (int)MathF.Round(Mathf.Lerp(0f, PavedRumble * 40f, speedT) * s);
        }

        if (useLogi) ApplyLogitech(v, springCoef, springSat, constMag, damp, rough, air);
        else if (_di != null && _di.Ok)
        {
            try
            {
                _di.Spring(springCoef, springSat);
                _di.Constant(InvertDirectInput ? -constMag : constMag);
                _di.Damper(damp);
                _di.Rumble(air ? 0 : rough);
            }
            catch (Exception ex)
            {
                Log.Warn("DirectInput Force Feedback abgeschaltet: " + ex.Message);
                _di.Dispose(); _di = null; _nextDiRetry = Now + 3f;
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

    static void ApplyLogitech(Vehicle v, int springCoef, int springSat, int constMag, int damp, int rough, bool air)
    {
        try
        {
            if (!Logi.Update() || !Logi.IsConnected(0)) { StopLogi(); return; }
            if (!Logi.HasForceFeedback(0)) { StopLogi(); return; }
            Logi.PlaySpringForce(0, 0, springSat, springCoef); _logiFx[0] = true;
            if (!air && (constMag != 0 || _logiFx[1])) { Logi.PlayConstantForce(0, constMag); _logiFx[1] = true; }
            if (damp > 0) { Logi.PlayDamperForce(0, damp); _logiFx[2] = true; }
            else if (_logiFx[2]) { Logi.StopDamperForce(0); _logiFx[2] = false; }
            if (rough > 0) { Logi.PlayDirtRoadEffect(0, rough); _logiFx[3] = true; }
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
    }

    // ------------------------------------------------------------------ Gamepad (XInput-Motoren)
    static void UpdatePad(Vehicle v, bool on)
    {
        if (!_padOk || TDU2InputDevices.IsWheel) { StopPad(); return; }
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

            float s = System.Math.Clamp(Strength * 2f, 0f, 1f);
            float kmh = MathF.Abs(v.SpeedKmh);
            float low = 0f, high = 0f;
            // Drehzahl: feines Brummen im kleinen Motor (auch im Stand), am Begrenzer pulsierend.
            float rpmT = v.Car != null && v.Car.RedLine > 0f ? System.Math.Clamp(v.Rpm / v.Car.RedLine, 0f, 1f) : 0f;
            if (v.Rpm > 50f) high += Mathf.Lerp(RpmVibrationIdle, RpmVibrationRedline, rpmT * rpmT);
            if (rpmT > 0.97f) high += (Mathf.Repeat(Now * 12f, 1f) < 0.5f ? 0.25f : 0f);
            // Feldweg/Gelände (Straßennetz, nicht der Gelände-Kollider): Rütteln im großen Motor. Asphalt rüttelt nicht.
            int roughWheels = RoughWheels(v);
            if (kmh > 3f && roughWheels > 0 && OffPaved(v)) low += Mathf.Lerp(0.12f, 0.4f, System.Math.Clamp(kmh / 120f, 0f, 1f));
            low += _impact; high += _impact * 0.5f;
            LastPadLow = System.Math.Clamp(low * s, 0f, 1f);
            LastPadHigh = System.Math.Clamp(high * s, 0f, 1f);
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
