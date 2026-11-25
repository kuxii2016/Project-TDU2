using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Scheinwerfer-Projektoren des Originals: ein Spotlicht je Auto, das nur in den Lichtpuffer s15 (y/z) der Original-Shader
    /// geht (edrender_pfx_lightness PS1/PS2, Render/Original/DxsoToGlsl.LightBufGlsl). Nachbau von
    /// <list type="bullet">
    /// <item>Manager 0xd1b140 (26 Slots à 0x9a0, Licht E = Slot+0x320; Spot_Color = (1,1,1) 0xd1bf42),</item>
    /// <item>Anmeldung je Auto 0xd1c340 (aus der Autoschleife 0x764399): Art k = (Spieler ? 0 : 1) + (Autotyp vt+0x2c == 1 ? 2 : 0),
    ///   Tabellen fac8.. (unten), Lage M = RotY(Lenkung, nur Autotyp 1) · T(0, fb08[k] + A/2, fb18[k] + B/2) · Automatrix,
    ///   Pos = Zeile 3 von M, Dir = Zeile 2 und Oben = Zeile 1 von RotX(faf8[k]) · M,</item>
    /// <item>Konstanten 0xd1cc80: Att (0xf6cf30/0xf6cfb0), Near (s. <see cref="Near"/>), ProjMatrix 0xdaf220,</item>
    /// <item>je Bild 0xd1d380: vars.z = Intensität (Slot+0x50) · sat(Einschaltgrad)² · Kamera-Abblendung 400..650 m (0xd1da69: [esp+0x28] =
    ///   Slot+0x50, ohne Reichweite – s311 hatte fälschlich · Reichweite, daher ~100× zu hell).</item>
    /// </list>
    /// Intensität = car_headlights_intensity (0x70eaf0; Lichtmodus 2 erzwungen -> max(Wert, 1)).
    /// </summary>
    public static class HeadlightProjectors
    {
        /// <summary>E+0x80 Reichweite (m) je Art (exe 0xfac8).</summary>
        static readonly float[] Cut = { 102f, 22f, 107f, 127f };
        /// <summary>exe 0xfad8: E+0x84 DropOff (m) und E+0x8c Innenwinkel (rad, voller Kegel).</summary>
        static readonly float[] DropInner = { 1.4835f, 1.1345f, 1.1868f, 0.7854f };
        /// <summary>exe 0xfae8: E+0x88 Außenwinkel (rad, voller Kegel) = Sichtfeld der Cookie-Projektion.</summary>
        static readonly float[] Outer = { 1.6057f, 1.4486f, 1.3614f, 1.1519f };
        /// <summary>exe 0xfaf8: Neigung nach unten (rad, RotX), Slot+0x60.</summary>
        static readonly float[] Pitch = { 0.4712f, 0.7854f, 0.6283f, 0.5411f };
        /// <summary>exe 0xfb08: Höhe über dem Autoursprung (m, + halbe Autohöhe).</summary>
        static readonly float[] OffY = { 3.1f, 2.3f, 1.2125f, 1.5125f };
        /// <summary>exe 0xfb18: Längsversatz (m, + halbe Autolänge).</summary>
        static readonly float[] OffZ = { -1.6f, -0.6f, -0.3f, -0.8f };
        /// <summary>exe 0xfab0/0xfab4: Abblendung über den Kameraabstand.</summary>
        const float FadeStart = 400f, FadeEnd = 650f;
        /// <summary>exe 0x1a = 26 Slots.</summary>
        public const int MaxSlots = 26;

        /// <summary>
        /// Projektor eines Autos anhängen. Fahrzeugachsen im Render-Raum (rechts/oben/vorn aus der Unity-Lage über die
        /// Spiegelung, Ursprung = Autoursprung), A/B = Höhe/Länge des Autos, intensity = car_headlights_intensity (Modus 2 schon
        /// max(…, 1)), on = Einschaltgrad 0..1 (Slot+0x31c), camera = Kameraposition (Render-Raum).
        /// </summary>
        public static void Add(List<HeadlightProjector> output, bool player, bool bike, Vector3 origin, Vector3 up, Vector3 forward,
                               float height, float length, float intensity, float on, Vector3 camera)
        {
            if (output.Count >= MaxSlots) return;
            // Autotyp vt+0x2c == 1 = Bike (Hinweis User 24.11.): Art + 2; die Lenkung steckt dann schon in forward (VehicleLights)
            int k = (player ? 0 : 1) + (bike ? 2 : 0);
            up = Vector3.Normalize(up); forward = Vector3.Normalize(forward);
            float cut = Cut[k], drop = DropInner[k], outer = Outer[k], inner = DropInner[k], pitch = Pitch[k];
            var pos = origin + up * (OffY[k] + 0.5f * height) + forward * (OffZ[k] + 0.5f * length);
            // RotX(pitch) · M: Zeile 1 = cos·oben + sin·vorn, Zeile 2 = cos·vorn − sin·oben (f37e50)
            float c = MathF.Cos(pitch), s = MathF.Sin(pitch);
            var pUp = Vector3.Normalize(c * up + s * forward);
            var pDir = Vector3.Normalize(c * forward - s * up);

            // f6cf30: Att.y = 1/(cut − drop), Att.x = cut · Att.y; f6cfb0: Att.w = 1/(cos(innen/2) − cos(außen/2)), Att.z = −cos(außen/2) · Att.w
            float ay = cut - drop > 1e-6f ? 1f / (cut - drop) : 0f;
            float co = MathF.Cos(outer * 0.5f), ci = MathF.Cos(inner * 0.5f);
            float aw = ci - co > 1e-6f ? 1f / (ci - co) : 0f;

            // 0xd1d380: Abblendung über den Kameraabstand (0x42260b, hier Abstand Kamera–Projektor)
            float dc = Vector3.Distance(camera, pos);
            float fade = dc < FadeStart ? 1f : 1f - (dc - FadeStart) / (FadeEnd - FadeStart);
            float sOn = System.Math.Clamp(on, 0f, 1f);
            float strength = intensity * sOn * sOn * fade;
            if (strength <= 0f) return;

            output.Add(new HeadlightProjector
            {
                Position = pos,
                Near = Near(OffY[k], -OffZ[k], pitch, outer, 0f, cut),
                Direction = pDir,
                Strength = strength,
                Up = pUp,
                Cot = 1f / MathF.Tan(outer * 0.5f),
                Attenuation = new Vector4(cut * ay, ay, -co * aw, aw),
            });
        }

        /// <summary>
        /// Spot_PositionDropOffNear.w (0xd1cc80): n = max(√(y² + z²), y · tan(π/2 − Neigung − außen/2)) mit y = Slot+0x58, z = Slot+0x5c
        /// (= −fb18[k]); x = Reichweite · Slot+0x54 · 1,01, ist x &gt; n − 0,5, dann n = 0,5 + Reichweite · Slot+0x54.
        /// Slot+0x54 = 1/Reichweite und y + Zusatzwert nur, wenn der Typ-Aufruf 0x19ca728 zutrifft (nicht zugeordnet) -> hier 0 bzw. y = fb08[k].
        /// </summary>
        static float Near(float y, float z, float pitch, float outer, float s54, float cut)
        {
            float n = MathF.Max(MathF.Sqrt(y * y + z * z), y * MathF.Tan(MathF.PI * 0.5f - pitch - outer * 0.5f));
            float x = cut * s54 * 1.01f;
            if (x > n - 0.5f) n = 0.5f + cut * s54;
            return n;
        }
    }
}