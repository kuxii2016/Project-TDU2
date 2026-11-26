using System.Numerics;
using Paradise.Core.Formats;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Material für den Original-Renderer: zugeordnetes Shader-Paar (<see cref="OriginalShaderSystem.Resolve"/>), die .2DM-Werte
/// (Parameter, D3DMATERIAL-Farben, Render-States) und die Texturen nach Slot
/// Hängt an <see cref="MaterialDesc.Original"/>; der Vulkan-Renderer zeichnet solche Materialien mit den übersetzten Shadern.
/// </summary>
public sealed class OriginalMaterial
{
    public OriginalProgram Program = null!;
    /// <summary>
    /// TD_Road: Regen-Permutation (OriginalShaderSystem.Resolve(def, true), exe 0xa630f0 WFX_WetGrd) – der Renderer zeichnet damit,
    /// solange RainSpecs.x (rain_ground_global_intensity) &gt; 0 ist. Ihre zusätzlichen Slots: s6 RIPPLE (aktuelles Bild),
    /// (exe 0xa630f0: Slot-Zeiger +0x1d0 / +0x1f0 / +0x210, Adresse 1 = WRAP, Würfel 2 = CLAMP).
    /// </summary>
    public OriginalProgram? RainProgram;
    /// <summary>Renderer: im Set gebundenes RIPPLE-Bild (ImageView-Handle), 0 = keins.</summary>
    public ulong RippleBound;
    public MaterialDef Def = null!;
    /// <summary>Parameter, die vor denen von <see cref="Def"/> gelten (Autolack aus &lt;car&gt;_color.2dm).</summary>
    public MaterialDef? Over;
    /// <summary>Einmal je Shader und Material aufgelöste, szenenunabhängige Konstanten (siehe <see cref="OriginalConstants.Fill"/>).</summary>
    internal readonly Dictionary<OriginalConstants.Entry[], OriginalConstants.CachedMaterialConstants> ConstantValues =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    /// <summary>Ein Look-up je Draw für VS+PS statt zwei (mit fertigen Material-Fallbackwerten).</summary>
    internal readonly Dictionary<OriginalProgram, OriginalConstants.CachedProgramConstants> ProgramConstants = new();
    /// <summary>Der VS liest TANGENT/BINORMAL (Renderer berechnet sie je Mesh).</summary>
    public bool NeedsTangents;
    public readonly IGpuTexture?[] Slots = new IGpuTexture?[16];

    /// <summary>
    /// Shader-Sätze, die schon über den Original-Renderer laufen (Stufe 2: nur Gebäude). Weitere Sätze kommen dazu, sobald
    /// ihre Eingänge (Lichtpuffer s15, Reflexions-Würfel, Tangenten, Skinning ...) vollständig sind.
    /// </summary>
    public static readonly HashSet<string> EnabledSets = new(StringComparer.OrdinalIgnoreCase) { "TD_Building" };
    /// <summary>Alle Sätze freigeschaltet (Platzhalter, wo Eingänge fehlen) – sonst nur <see cref="EnabledSets"/>.</summary>
    public static bool AllSets = true;

    /// <summary>Pipeline-Schlüssel aus den Render-States (MAT.+0x40): Bit 0-3 D3DBLEND Quelle, 4-7 Ziel, 8 Mischen, 9 Tiefe schreiben, 10 Tiefentest.</summary>
    public int States;
    /// <summary>D3DRS_ALPHATESTENABLE / ALPHAREF (Referenz 0..1); Vergleich D3DCMP_GREATER (exe 0xfae0d4, immer).</summary>
    public bool AlphaTest;
    public float AlphaRef;
    /// <summary>Haus-/Camper-Interieur: Platzhalter-Lichtpuffer ohne Sonne.</summary>
    public bool Interior;
    /// <summary>Licht des Avatars, zu dem das Material gehört (Instanz-Lichter des Originals); null = Licht der Szene.</summary>
    public Paradise.Core.Avatars.AvatarLightState? Avatar;

    /// <summary>Material-Familie über die Satz-GUID wie exe 0x67f434 (Vergleich auf Material +0x300/+0x304), für die Avatar-Symbole.</summary>
    public enum AvatarFamily : byte { None, Standard, Avatar, Pedestrian, Bike, Hair }
    public AvatarFamily Family
    {
        get
        {
            ulong g = Def.ShaderSet;
            uint lo = (uint)g, hi = (uint)(g >> 32);
            if (hi == 0xd5013ed0u)
                return lo switch
                {
                    0xdbdedede => AvatarFamily.Hair,   // TD_Hair (CARINTEG.w eigen, exe 0x67f2b4)
                    0xdb79bbbb or 0xdbcdcdcd or 0xdbcddbca or 0xdb79aeae or 0xcacadaca or 0xdba9daca => AvatarFamily.Standard,   // Eye, Skin2, Skinface, Clothe2, Visor, Accessories2
                    0xdb79baed => AvatarFamily.Pedestrian,
                    0xdb79caca => AvatarFamily.Standard,   // td_clothe2bike: dieselben Werte (exe 0x67f4fc)
                    0xdb79dadb => AvatarFamily.Avatar,   // TD_Avatar: Symbole bleiben wie in der .2DM
                    _ => AvatarFamily.None,
                };
            if (g == 0x413ab646426c5358UL) return AvatarFamily.Standard;   // td_belt
            return AvatarFamily.None;
        }
    }

    public static int Created, DrawnLastFrame, Pipelines;
    public static volatile string LastError = "";

    public static readonly HashSet<string> PendingSets = new(StringComparer.OrdinalIgnoreCase)
        { "TD_Sprite", "TD_VegetationSPR", "TD_TallGrass", "TD_Impostor", "TD_HardwareInstance" };

    /// <summary>GLOSSFCT der Lichtgläser je Bild (VehicleLights: Bremse, Rückfahr, Positionslicht, Blinker – exe FUN_00c588d0);
    /// null = Wert der .2DM. Gesetzt beim Aktualisieren des Materials (MaterialDesc.Lgt*).</summary>
    public Vector4? GlossFct;
    /// <summary>COLOR1 der Auto-Shader als RGBA8 (Dreckfarbe, a = Dreckstufe), je Draw über VS ext.z (s315). 0 = sauber.</summary>
    public uint Color1;
    /// <summary>DirtColor0..3 der Auto-Shader (rgb Dreckfarbe, a Dreckstufe), gesetzt beim Schreiben des Sets aus MaterialDesc.</summary>
    public Vector4 Dirt;
    internal bool DirtDiag;
    /// <summary>Ebene DIRTCOLOR (gDirtScratchTexSampler / gDirtColorTexSampler) der Auto-Materialien.</summary>
    public static readonly ulong SemDirtColor = MaterialDef.KeyFromName("DIRTCOLOR"), SemDirtScratch = MaterialDef.KeyFromName("DIRTSCRATCH"),
                                  SemStickers = MaterialDef.KeyFromName("STIKERS"), SemDamage = MaterialDef.KeyFromName("DAMAGE");
    /// <summary>Renderer: im Set gebundener Würfel (ImageView-Handle), 0 = Material hat keinen Würfel-Sampler.</summary>
    public ulong CubeBound;
    /// <summary>
    /// Textur-Animation (.uva) wie beim eigenen Shader (TextureAnimations -> IRenderer.SetMaterialUv): xy = Skalierung, zw = Versatz,
    /// für __UVA_0 (Hauptebene) und __UVA_1 (Detail-Ebene) [vermutet: Zuordnung Ebene -> UVA-Index; die exe füllt 0xe..0x10 nicht im
    /// Objektwert-Block 0xfb3460 (Sprungtabelle -> übersprungen), sondern an anderer Stelle – noch nicht gefunden].
    /// </summary>
    /// <summary>Material eines Autos (CarModel): Licht-Instanz des Autos (OriginalScene.Car*) statt der Sonne.</summary>
    public bool Car;
    public System.Numerics.Vector4 MainST = new(1, 1, 0, 0), DetailST = new(1, 1, 0, 0);

    /// <summary>
    /// TD_HeightMap: der VS liest das Vertexformat des Original-Geländes – POSITION.x = Höhe, TEXCOORD0.xy = Rasterindex (x, z),
    /// Lage = (Index.x · 3, Höhe, Index.y · 3) (VS: mad r0.xzw, v2.xyyx, c2.xyxy mit c2.x = 3; mov r0.y, v0.x), Farbkarte = Index / 256.
    /// Der Renderer liefert diesen Datenstrom aus dem Gelände-Mesh der Engine (Sektor-lokal, Raster 3 m).
    /// </summary>
    public bool HeightMapInput;
    /// <summary>Rasterabstand im TD_HeightMap-VS (c2.x = 3,0).</summary>
    public const float HeightMapSpacing = 3f;
    /// <summary>Slots, die ohne Textur schwarz (0, 0, 0, 1) statt weiß gebunden werden (Nachtlicht-Karten).</summary>
    public uint BlackMask;
    /// <summary>Slots ohne Textur, die durchsichtig (0,0,0,0) gebunden werden (Beklebung STIKERS der Lacke).</summary>
    public uint ClearMask;
    /// <summary>
    /// Adressmodus je Slot aus der Ebene der .2DM (s309): der Sampler-Zustand gehört zum Material, nicht zur Textur. Die Textur-Caches
    /// (LocalTextureSet/GlobalTextureCache) legen jede Textur nur einmal an – mit dem Adressmodus des ERSTEN Materials. Beispiel
    /// arrows.2db: als MARKS2 mit 2/2 (Clamp/Clamp) angelegt, als MARKS2 2/1 bzw. 1/1 (V wiederholt, Uv3.v bis 7,5) und als MARKS 2/1
    /// trotzdem Clamp/Clamp gebunden -> Randzeilen des Atlas gestreckt („grober weißer Fleck“). Bit gesetzt = SlotWrapU/V gelten.
    /// </summary>
    public readonly TextureWrap[] SlotWrapU = new TextureWrap[16], SlotWrapV = new TextureWrap[16];
    public uint SlotWrapMask;
    static readonly HashSet<string> s_wrapDiag = new();
    /// <summary>Material eines Spot-Exteriors (MaterialFactory.SpotExterior): NIGHTLMAP je Bild aus spot_exterior_lightmap_day_night
    /// (OriginalConstants, exe 0xc60bb0/0xc60dc0).</summary>
    public bool SpotExterior;
    /// <summary>Slots „gProjectiveMapSampler“ (TD_Furnitures s6, TD_Water s4): projizierte Spiegelung (Boden-Spiegelbild) – der VS
    /// liefert Bildschirm-Projektionskoordinaten (TD_Furnitures ps: rcp v3.z, mul, mad c7 StereoUvMadd, texld s6). Der Renderer
    /// bindet hier das Bodenspiegelbild der Engine (RenderEnvironment).</summary>
    public uint ProjMask;
    public ulong ProjBound;

    // Einmal je Material aus den CTAB-Deklarationen beider Shader (inkl. Regen-Permutation) ermittelt.
    // SamplerType() lief vorher bei jedem BuildOriginalSet 16× über einen Shaders()-Iterator.
    readonly int[] _samplerTypes = new int[16];

    public static OriginalMaterial? Create(OriginalProgram? prog, MaterialDef def, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTexture, bool interior)
    {
        if (prog == null || (!AllSets && !EnabledSets.Contains(prog.Library.Name))) return null;
        if (PendingSets.Contains(prog.Library.Name)) return null;
        if (prog.Vs.Parsed == null || prog.Ps.Parsed == null) return null;
        // Konstanten-Klassifikation einmal beim Materialaufbau statt beim ersten Draw. Erst dort ist die Permutation
        // endgültig bekannt; die Shader-Parsings sind hier bereits erfolgt. Keine Bild- oder Zustandsänderung.
        prog.VsEntries ??= OriginalConstants.EntriesOf(prog.Vs.Parsed!);
        prog.PsEntries ??= OriginalConstants.EntriesOf(prog.Ps.Parsed!);
        // Volumen-Texturen (dcl_volume) hat der Renderer noch nicht
        foreach (var sh in new[] { prog.Vs.Parsed, prog.Ps.Parsed })
            foreach (var d in sh.Decls) if (d.RegType == 10 && d.SamplerType == 4) return null;
        var m = new OriginalMaterial { Program = prog, Def = def, Interior = interior };
        if (string.Equals(prog.Library.Name, "TD_Road", StringComparison.OrdinalIgnoreCase))
        {
            var rain = OriginalShaderSystem.Resolve(def, true);
            if (rain != null && rain.Vs.Parsed != null && rain.Ps.Parsed != null)
            {
                rain.VsEntries ??= OriginalConstants.EntriesOf(rain.Vs.Parsed!);
                rain.PsEntries ??= OriginalConstants.EntriesOf(rain.Ps.Parsed!);
                m.RainProgram = rain;
            }
        }
        for (int i = 0; i < 16; i++) m._samplerTypes[i] = m.SamplerTypeCore(i);
        m.HeightMapInput = string.Equals(prog.Library.Name, "TD_HeightMap", StringComparison.OrdinalIgnoreCase);
        // Nachtlicht-Karten (TD_Building s8 gNightLightMapSampler, TD_Furnitures s7) und Sektortextur (TD_Ground s2 / TD_Road s5
        // gTexSamplerShadowMap, Kanäle Schatten/Nacht) ohne Textur: schwarz statt weiß, sonst leuchten Objekte nachts voll. Alle anderen leeren Slots bleiben weiß (Cockpit/Spiegel).
        foreach (var sh in m.Shaders())
            foreach (var c in sh.Constants)
                if (c.RegisterSet == 3 && c.Register >= 0 && c.Register < 16 && (c.Name.Contains("NightLight", StringComparison.Ordinal) || c.Name == "gTexSamplerShadowMap"))
                    m.BlackMask |= 1u << c.Register;
        foreach (var sh in new[] { prog.Vs.Parsed!, prog.Ps.Parsed! })
            foreach (var c in sh.Constants)
                if (c.RegisterSet == 3 && c.Register >= 0 && c.Register < 16 && c.Name == "gProjectiveMapSampler") m.ProjMask |= 1u << c.Register;
        // Beklebung/Schaden der Lacke ohne Laufzeit-Textur: durchsichtig bzw. schwarz (keine Beklebung, kein Schaden) statt weiß
        foreach (var l in def.Layers)
        {
            if (l.Slot < 0 || l.Slot >= 16) continue;
            if (l.Semantic == SemStickers) m.ClearMask |= 1u << l.Slot;        // PS: lrp(sticker.a, sticker, Lack) -> Alpha 0
            else if (l.Semantic == SemDamage) m.BlackMask |= 1u << l.Slot;    // PS: dp4(Schaden, DmgMask)
        }
        foreach (var sh in m.Shaders())
            if (!sh.IsPixel)
                foreach (var d in sh.Decls) if (d.RegType == 1 && (d.Usage == 6 || d.Usage == 7)) m.NeedsTangents = true;
        // Render-States applyRenderStates: [0] Tiefentest, [1] Tiefe schreiben (nicht 0/2), [5] Alpha-Test (0 = an),
        // [7] Referenz, [8]/[9] Quelle/Ziel, [0xa] != 6 = Mischen
        var st = def.Flags;
        bool blend = st[0xa] != 6;
        int src = blend ? st[8] : 2, dst = blend ? st[9] : 1;
        bool mix = !(src == 2 && dst == 1);
        m.States = (src & 0xF) | ((dst & 0xF) << 4) | (mix ? 0x100 : 0) | (st[1] != 0 && st[1] != 2 ? 0x200 : 0) | (st[0] != 0 ? 0x400 : 0);
        m.AlphaTest = st[7] != 0 && st[5] == 0;
        m.AlphaRef = st[7] / 255f;
        foreach (var l in def.Layers)
        {
            if (l.Slot < 0 || l.Slot >= 16 || l.TextureKey == 0) continue;
            // Würfel-Sampler bindet der Renderer immer selbst (Umgebungswürfel, BuildOriginalSet). Die .2DM tragen dort nur Laufzeit-
            // Namen (CUBEMAP, CUBEMAP_A ...) ohne Textur in den Banken – nicht nachschlagen, sonst landen sie als "fehlend" in
            // MaterialFactory._pending (Log-Flut "TEXTUR FEHLT ... 5f50414d45425584" je Material, Test 24.11.)
            if (m.SamplerType(l.Slot) == 3) continue;
            // Adressmodus 3/3 = projizierte Schattenkarte des Sektors (kein UV-Satz) – kommt im Original aus dem Lichtpuffer
            m.Slots[l.Slot] = getTexture(l.TextureKey, TextureDef.WrapFromEden(l.AddressU), TextureDef.WrapFromEden(l.AddressV));
            m.SlotWrapU[l.Slot] = TextureDef.WrapFromEden(l.AddressU);
            m.SlotWrapV[l.Slot] = TextureDef.WrapFromEden(l.AddressV);
            m.SlotWrapMask |= 1u << l.Slot;
            // s312 Prüf-Log (Gelände-Streifen): je Shader/Slot/Adressmodus ungleich Wrap einmal
            bool newWrap = false;
            if (l.AddressU != 1 || l.AddressV != 1) lock (s_wrapDiag) newWrap = s_wrapDiag.Add($"{prog.Library.Name}/{l.Slot}/{l.SemanticName}/{l.AddressU}/{l.AddressV}");
        }
        Interlocked.Increment(ref Created);
        return m;
    }

    /// <summary>Für Materialien außerhalb von MaterialFactory (Autos, Avatare, Gelände): zuordnen + anlegen, null ohne Original-Renderer.</summary>
    public static OriginalMaterial? For(MaterialDef? def, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTexture, MaterialDef? over = null, bool interior = false)
    {
        if (def == null || !GraphicsSettings.RenderUseOriginal) return null;
        var m = Create(OriginalShaderSystem.Resolve(def), def, getTexture, interior);
        if (m != null) m.Over = over != null && !ReferenceEquals(over, def) ? over : null;
        return m;
    }

    /// <summary>Textur eines Slots nach der Semantik der Ebene setzen (z. B. Gelände: COLOR = Sektor-Farbkarte).</summary>
    public void SetSlot(ulong semantic, IGpuTexture? tex)
    {
        if (tex == null) return;
        // Laufzeit-Textur (z. B. Sektor-Farbkarte des Geländes): bisheriges Verhalten – Adressmodus der Textur
        foreach (var l in Def.Layers) if (l.Semantic == semantic && l.Slot >= 0 && l.Slot < 16) { Slots[l.Slot] = tex; SlotWrapMask &= ~(1u << l.Slot); }
    }

    /// <summary>Shader beider Permutationen (trocken und – falls vorhanden – Regen).</summary>
    public IEnumerable<DxsoShader> Shaders()
    {
        yield return Program.Vs.Parsed!; yield return Program.Ps.Parsed!;
        if (RainProgram != null) { yield return RainProgram.Vs.Parsed!; yield return RainProgram.Ps.Parsed!; }
    }

    /// <summary>Slot eines Samplers nach seinem Namen in der CTAB der Shader (z. B. gDetailTexSampler), −1 = nicht benutzt.</summary>
    public int SamplerSlot(string name)
    {
        foreach (var sh in Shaders())
            foreach (var c in sh.Constants)
                if (c.RegisterSet == 3 && c.Register >= 0 && c.Register < 16 && c.Name == name) return c.Register;
        return -1;
    }

    /// <summary>Sampler-Typ eines Slots laut dcl der beiden Shader: 2 = 2D, 3 = Würfel, 4 = Volumen, 0 = unbenutzt.</summary>
    public int SamplerType(int slot) => (uint)slot < 16u ? _samplerTypes[slot] : 0;

    int SamplerTypeCore(int slot)
    {
        foreach (var sh in Shaders())
            foreach (var d in sh.Decls)
                if (d.RegType == 10 && d.Register == slot) return d.SamplerType == 0 ? 2 : d.SamplerType;
        return 0;
    }
}