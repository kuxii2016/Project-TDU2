using System.Numerics;

namespace Paradise.Core.Avatars;

/// <summary>
/// avatar_*-Werte des aktiven Wetters (Wetter-Registry, je Eintrag 0x38 Byte: +0 Id, +8 Grundwert, +0x18 aktueller Wert).
/// Grundwerte wie die Registrierung im Original (re/avatar_lighting_spec.md §1). Gefüllt von WorldWeather jedes Bild.
/// </summary>
public struct AvatarWeatherLight
{
    public bool Set;
    /// <summary>sundirection (Lichtrichtung, von der Sonne weg), sun_ambient / sun_diffuse / sun_specular – Wetterwerte unverändert.</summary>
    public Vector3 SunDir, SunAmbient, SunDiffuse, SunSpecular;
    public float CameraLightFactor, CameraLightDesynchro;
    public Vector3 CameraLightDirection;
    /// <summary>avatar_spotlight_interior_exterior_influence.xy: Gewicht der Innen- bzw. Außenlichter.</summary>
    public Vector2 SpotInfluence;
    public float CarEnvmapFactor, SunAmbiantFactor, SunDiffuseFactor, SunSpecularFactor, CarNightReflectionFactor;
    public float LumMin, LumMax, ShadowInfluence, RainSpecularCoef;

    public static AvatarWeatherLight Defaults => new()
    {
        SunDir = new Vector3(0f, -0.856f, -0.423f), SunAmbient = new Vector3(0.1f), SunDiffuse = new Vector3(0.8f), SunSpecular = Vector3.One,
        CameraLightDirection = new Vector3(0f, -1f, 0f), CarEnvmapFactor = 0.7f,
        SunAmbiantFactor = 1f, SunDiffuseFactor = 1f, SunSpecularFactor = 1f, LumMin = 0f, LumMax = 1f, ShadowInfluence = 1f, RainSpecularCoef = 1f,
    };
}

/// <summary>
/// Entity__Light einer Szene (Interieur, Kino-Bank) wie im Licht-Container des Originals (Eintrag 0xa0 Byte, max. 64).
/// Felder des Ladens: unpacked.exe 0x952cb3 (Rot +0xe0, Pos +0xf0, Ambient +0x100, Diffuse +0x110, Specular +0x120, Intensity +0x130,
/// DistanceDropOff +0x134, DistanceCutOff +0x138, AngularDropOff +0x13c, AngularCutOff +0x140, isInterior +0x144, Grundwert 1).
/// Eintrag wie der Adder 0xaf8c50 (Aufruf 0x952ed1..0x953046): Farben x Intensity, Lage = Übersetzung der Entity-Matrix,
/// Richtung = −Y-Achse der Entity-Matrix, E+0x84 = DistanceDropOff, E+0x80 = DistanceCutOff, E+0x8c/+0x88 = 2 x Angular Drop/Cut.
/// </summary>
public sealed class SceneLight
{
    /// <summary>Lage / Richtung im Szenenraum (werden mit <see cref="AvatarLighting.LightFrame"/> in den Render-Raum gebracht).</summary>
    public Vector3 Position, Direction;
    public Vector3 Ambient, Diffuse, Specular;
    /// <summary>E+0x70 / E+0x74 (exe 0xf6cf30): E74 = 1/(cut − drop) bzw. 8388608, E70 = cut · E74 -> sat(E70 − E74·len).</summary>
    public float DistA, DistB;
    /// <summary>E+0x84 = DistanceDropOff (vom Abstand im Quadrat abgezogen, Sammeln §2.2).</summary>
    public float DropOff;
    /// <summary>E+0x7c / E+0x78 (exe 0xf6cfb0): Kegel sat((cos − cosAußen) / (cosInnen − cosAußen)); CosOuter = cos(Außenwinkel/2).</summary>
    public float ConeScale, ConeBias, CosOuter;
    public bool Interior;

    public static SceneLight Create(Vector3 pos, Vector3 yAxis, Vector3 amb, Vector3 diff, Vector3 spec, float intensity,
                                    float distDrop, float distCut, float angDrop, float angCut, bool interior)
    {
        var l = new SceneLight
        {
            Position = pos,
            Direction = yAxis.LengthSquared() > 1e-12f ? -Vector3.Normalize(yAxis) : new Vector3(0, -1, 0),
            Ambient = amb * intensity, Diffuse = diff * intensity, Specular = spec * intensity,
            DropOff = distDrop, Interior = interior,
        };
        // 0xf6cf30
        float d = distCut - distDrop;
        l.DistB = d > 1.1920929e-7f ? 1f / d : 8388608f;
        l.DistA = distCut * l.DistB;
        // 0xf6cfb0: Winkel x 2 im Adder (E+0x88/+0x8c), hier halbiert -> cos(Winkel)
        float cO = MathF.Cos(angCut), cI = MathF.Cos(angDrop);
        l.CosOuter = cO;
        float c = cI - cO;
        l.ConeScale = c > 1.1920929e-7f ? 1f / c : 8388608f;
        l.ConeBias = -cO * l.ConeScale;
        return l;
    }
}

/// <summary>
/// Licht der Avatare wie im Original (re/avatar_lighting_spec.md, Abschnitte gegen unpacked.exe geprüft):
///   1. Sammeln (§2.1/§2.2): Innen-/Außenlichter der Szene am Avatar, Gewicht nach Abstand/Kegel, Rest = Sonne.
///   2. Kameralicht (§2.4): Füll-Spotlicht = gesammelte Farben x avatar_cameralight_factor, von der Kamera her.
///   3. Endlicht (§2.5, exe 0x8be370..0x8be8cb): v = clamp(1, lum_min, lum_max), Farbe x (1 + (v−1)·Faktor) x Lichtfaktor p0.
///   4. Material-Symbole (§2.6, exe 0x67f360..): CARINTEG = (p1, p2, Schatten, 1), ATTEFACT = (1−p3, 1−p4, 1−p5, Envmap).
/// Spielmodus und Szene setzt Paradise.Game (Mode, InteriorScene, Lights, LightFrame).
/// </summary>
public static class AvatarLighting
{
    public static AvatarWeatherLight Weather = AvatarWeatherLight.Defaults;
    /// <summary>Lichter der aktiven Szene (Szenenraum), höchstens 64 wie der Container des Originals.</summary>
    public static readonly List<SceneLight> Lights = new();
    /// <summary>Szenenraum -> Render-Raum (Interieur: Spot-Rahmen x Wurzel), jedes Bild vom Spiel gesetzt.</summary>
    public static Matrix4x4 LightFrame = Matrix4x4.Identity;
    /// <summary>Aktueller Spielmodus (GAME_MODE_*-Nummer wie das Original, [[0xfd50fc]+8]).</summary>
    public static int Mode = 1;
    /// <summary>Kino/Makler in einem Innenraum (Original: Bytes +0x829/+0x82a des Modus-Objekts) [vermutet: Bedeutung "Innen-Kino"].</summary>
    public static bool InteriorScene;
    /// <summary>Zähler je Bild (DevMenu/Diagnose).</summary>
    public static int Gathered;

    /// <summary>fx.ini [AVATARLIGHTFACTOR] p0..p5 (Spec §2.6; Datei des Spiels, Werte aus der Spec übernommen – Laden aus fx.ini offen).</summary>
    public static readonly float[] OutCar = { 1.0f, 0f, 0f, 0f, 0f, 0f };
    public static readonly float[] InCarFactor = { 0.5f, 0.115f, 0.45f, 0.6f, 0.95f, 1.0f };
    public static readonly float[] Convertible = { 0.8f, 0.1125f, 0.40f, 1.0f, 0.75f, 0.75f };

    /// <summary>Modi mit Sammeln der Szenenlichter (Liste 1, Spec §2.1): SHOP, CARSHOWCASE, GARAGE, LIVING, GOODIES, GAMBLING_HALL,
    /// DRESSING, ESTATES, CLUB, LOBBY, DRIVING_SCHOOL, HAIRDRESSER, PHOTOGRAPH, CASINO, STICKERS, CINE, AVATAR_CHOICE, SURGERY, AIRPORT, FIRST_WM.</summary>
    static readonly HashSet<int> s_gather = new() { 3, 6, 8, 9, 0xa, 0xb, 0xc, 0x11, 0x12, 0x13, 0x16, 0x19, 0x1a, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23 };

    public static bool GatherMode => s_gather.Contains(Mode);

    /// <summary>Sonne statt gesammelter Werte (Spec §2.5): Modus nicht in Liste 2 (= Liste 1 ohne LOBBY 0x13 und PHOTOGRAPH 0x1a);
    /// ESTATES/CINE nur außerhalb eines Innenraums; AVATAR_CHOICE immer gesammelt.</summary>
    public static bool UseSun
    {
        get
        {
            int m = Mode;
            if (m == 0x20) return false;
            if (m == 0x11 || m == 0x1f) return !InteriorScene;
            if (m == 0x13 || m == 0x1a) return true;
            return !s_gather.Contains(m);
        }
    }

    public static void ClearLights() { Lights.Clear(); LightFrame = Matrix4x4.Identity; }
    public static void AddLight(SceneLight l) { if (Lights.Count < 64) Lights.Add(l); }
}

/// <summary>
/// Licht eines Avatars (Charakter +0x178-Block, Instanz-Lichter +0x2a00 / +0x2a50 / +0x2ab0, Schatten +0xe0, Envmap +0x19c).
/// Hängt an allen Original-Materialien des Avatars (<see cref="Render.Original.OriginalMaterial.Avatar"/>); der Original-Renderer
/// schreibt daraus __AmbLight_*_0, __DirLight_*_0, __SpotLight_*_0 und die Symbole CARINTEG / ATTEFACT / SHADOWATTENUATION.
/// Alle Lagen im Render-Raum.
/// </summary>
public sealed class AvatarLightState
{
    public Vector3 Ambient, Diffuse, Specular, Direction = new(0, -1, 0);
    /// <summary>char+0xe0: Schattenanteil (CARINTEG.z).</summary>
    public float Shadow = 1f;
    /// <summary>char+0x19c: avatar_car_envmap_factor x (1 + (v−1)·night) (ATTEFACT.w).</summary>
    public float Envmap;
    /// <summary>Kameralicht (Spot): Farben, Ankerpunkt; Lage wird beim Zeichnen aus der Kamerarichtung gebildet (<see cref="SpotPosition"/>).</summary>
    public Vector3 SpotAmbient, SpotDiffuse, SpotSpecular, SpotAnchor, SpotDirection;
    public bool SpotFollowsCamera;
    /// <summary>Lichtfaktor-Satz (fx.ini AVATAR*LIGHTFACTOR): p0 Multiplikator, p1/p2 Höhenbereich, p3..p5 Abschwächung.</summary>
    public float[] Factors = AvatarLighting.OutCar;
    public bool InCar;
    public bool Valid;

    /// <summary>__SpotLight_Pos_0 (Render-Raum). Spec §2.4: anchor − 10000·dir; dir = Kameraachse (desynchro &lt; 0,5) bzw.
    /// avatar_cameralight_direction. [vermutet, Spec: Kameraachse so gewählt, dass die Kamera-Seite beleuchtet ist].</summary>
    public Vector3 SpotPosition(Vector3 cameraForward) =>
        SpotFollowsCamera ? SpotAnchor + 10000f * cameraForward : SpotAnchor - 10000f * SpotDirection;

    /// <summary>
    /// Jedes Bild vor dem Zeichnen. <paramref name="point"/> = Sammelpunkt (Original FUN_00b87100: Skelettpunkt, hier Hüfte + 1 m
    /// [vermutet]), <paramref name="anchor"/> = Lage + (0, 1, 0) (Spec §2.4), beide im Render-Raum.
    /// </summary>
    public void Update(Vector3 point, Vector3 anchor)
    {
        var w = AvatarLighting.Weather;
        var sunDir = w.SunDir.LengthSquared() > 1e-12f ? Vector3.Normalize(w.SunDir) : new Vector3(0, -1, 0);
        Factors = InCar ? AvatarLighting.InCarFactor : AvatarLighting.OutCar;

        // ---- §2.1 Sammeln (nur Modi aus Liste 1; sonst Einfluss (0, 0))
        var sio = AvatarLighting.GatherMode ? w.SpotInfluence : Vector2.Zero;
        float cf = w.CameraLightFactor;
        Vector3 gDir = sunDir, gA = w.SunAmbient, gD = w.SunDiffuse, gS = w.SunSpecular;
        float shadowGather;
        if (sio.X > 0f || sio.Y > 0f || cf > 0f)
        {
            float best = Gather(point, sio, sunDir, w, out gDir, out gA, out gD, out gS);
            shadowGather = w.ShadowInfluence * (1f - best) + best * 0f;   // + w·0.0 (double 0.0 im Original)
            // §2.4 Kameralicht: gesammelte Farben x Faktor
            SpotAmbient = gA * cf; SpotDiffuse = gD * cf; SpotSpecular = gS * cf;
            SpotAnchor = anchor;
            SpotFollowsCamera = w.CameraLightDesynchro < 0.5f;
            SpotDirection = w.CameraLightDirection.LengthSquared() > 1e-12f ? Vector3.Normalize(w.CameraLightDirection) : new Vector3(0, -1, 0);
        }
        else
        {
            shadowGather = w.ShadowInfluence;
            SpotAmbient = SpotDiffuse = SpotSpecular = Vector3.Zero;
        }

        // ---- §2.5 Endlicht
        Vector3 dir, amb, diff, spec;
        if (AvatarLighting.UseSun)
        {
            Shadow = w.ShadowInfluence;
            // Im Auto nimmt das Original das Cockpit-Licht des Autos (car+0x5d0/+0x540/+0x5a0/+0x5b0) – hier noch die Sonne [Abweichung]
            dir = sunDir; amb = w.SunAmbient; diff = w.SunDiffuse; spec = w.SunSpecular;
        }
        else
        {
            Shadow = shadowGather;
            dir = gDir; amb = gA; diff = gD; spec = gS;
        }
        // v = clamp(1.0, lum_min, lum_max) (fldz + 1.0 @0x14cff08, exe 0x8be3f4)
        float v = 1f;
        if (v < w.LumMin) v = w.LumMin;
        else if (v > w.LumMax) v = w.LumMax;
        float m = Factors[0];
        Ambient = amb * (1f + (v - 1f) * w.SunAmbiantFactor) * m;
        Diffuse = diff * (1f + (v - 1f) * w.SunDiffuseFactor) * m;
        Specular = spec * (1f + (v - 1f) * w.SunSpecularFactor) * m;
        Direction = dir.LengthSquared() > 1e-12f ? Vector3.Normalize(dir) : new Vector3(0, -1, 0);
        // char+0x19c (exe 0x8be895): env · (1 + (v − 1)·night), char+0x1a0 = 1
        Envmap = w.CarEnvmapFactor * (1f + (v - 1f) * w.CarNightReflectionFactor);
        Valid = true;
    }

    /// <summary>
    /// Licht der Szenenlichter an einem Punkt (Render-Raum) für Autos im Interieur: exe 0x744990 nimmt in den Modi
    /// 3/0x1d/0x1e/8/0x16/0x23 Richtung/Ambient/Diffus/Glanz aus dem Lichtblock des Autos (car+0x20/+0x30/+0x40/+0x50, 0x744d5f)
    /// statt der Sonne. [vermutet] Der Block wird wie beim Avatar aus dem Licht-Container gesammelt (§2.2) – Erzeuger in der exe
    /// noch nicht gefunden. Ohne Sammelmodus/Einfluss: Sonnenwerte.
    /// </summary>
    public static void GatherAt(Vector3 p, out Vector3 dir, out Vector3 a, out Vector3 d, out Vector3 s)
    {
        var w = AvatarLighting.Weather;
        var sunDir = w.SunDir.LengthSquared() > 1e-12f ? Vector3.Normalize(w.SunDir) : new Vector3(0, -1, 0);
        var sio = AvatarLighting.GatherMode ? w.SpotInfluence : Vector2.Zero;
        if (sio.X > 0f || sio.Y > 0f) { Gather(p, sio, sunDir, w, out dir, out a, out d, out s); return; }
        dir = sunDir; a = w.SunAmbient; d = w.SunDiffuse; s = w.SunSpecular;
    }

    /// <summary>§2.2 Sammeln über die Szenenlichter (Render-Raum über <see cref="AvatarLighting.LightFrame"/>); Rückgabe = bestes Gewicht.</summary>
    static float Gather(Vector3 p, Vector2 sio, Vector3 sunDir, in AvatarWeatherLight w, out Vector3 dir, out Vector3 a, out Vector3 d, out Vector3 s)
    {
        const float Eps = 1.1920929e-7f;
        float best = BitConverter.Int32BitsToSingle(0x00800000);   // FLT_MIN
        int bestIdx = -1; float qBest = 1f;
        Vector3 accDir = Vector3.Zero; a = d = s = Vector3.Zero;
        var frame = AvatarLighting.LightFrame;
        var list = AvatarLighting.Lights;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            float infl = e.Interior ? sio.X : sio.Y;
            if (infl <= 0f) continue;
            var ePos = Vector3.Transform(e.Position, frame);
            var eDir = Vector3.TransformNormal(e.Direction, frame);
            if (eDir.LengthSquared() > 1e-12f) eDir = Vector3.Normalize(eDir);
            var dv = p - ePos;
            float sq = System.Math.Clamp(dv.LengthSquared() - e.DropOff * e.DropOff, Eps, 100000f);
            // Kegeltest: dot² >= c²·s und dot > 0
            float dot = Vector3.Dot(eDir, dv);
            if (!(dot > 0f && dot * dot >= e.CosOuter * e.CosOuter * sq)) continue;
            float len = MathF.Sqrt(sq);
            float dl = dv.Length();
            var dn = dl > 1e-12f ? dv / dl : Vector3.Zero;
            float att = Sat(e.DistA - e.DistB * len) * Sat(e.ConeScale * Vector3.Dot(eDir, dn) + e.ConeBias);
            float wgt = att * infl;
            if (wgt <= 0f) continue;
            float q = sq * sq;
            accDir += dn * wgt / q;
            a += e.Ambient * wgt; d += e.Diffuse * wgt; s += e.Specular * wgt;
            if (wgt > best) { best = wgt; qBest = q; bestIdx = i; }
        }
        if (bestIdx != -1)
        {
            float t = 1f - (best - Eps);
            accDir += sunDir * t / qBest;
            a += w.SunAmbient * t; d += w.SunDiffuse * t; s += w.SunSpecular * t;
            AvatarLighting.Gathered++;
        }
        else
        {
            accDir = sunDir; a = w.SunAmbient; d = w.SunDiffuse; s = w.SunSpecular;
        }
        dir = accDir.LengthSquared() > 1e-20f ? Vector3.Normalize(accDir) : sunDir;
        return bestIdx != -1 ? best : 0f;
    }

    static float Sat(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
