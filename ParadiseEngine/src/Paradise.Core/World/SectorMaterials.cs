using System.Numerics;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;

namespace Paradise.Core.World;

/// <summary>
/// Sektor-Materialien nach dem Laden eines Sektors wie das Original (unpacked.exe 0xc50d10..0xc50f36): über die Materialliste des
/// Sektors (Sektor +0x1a7c) bekommen
///   TD_Road   (Satz 0xc8ad93c95455291b): jede Ebene "SHADOW" = Sektortextur (Sektor-Bank +0x674), Adressmodus 3/3 (0xc50dc3),
///   TD_Ground (0xb0f1234b0f123473) und TD_Ground_HM/BLOCK (0xa0a0a1a1a2a2a3a3): Ebene 2 (Material +0x150) = Sektortextur,
///             Adressmodus 3/3 (+0x158/+0x159), Symbol SECTROFF = (Sektor +0x30, 0, Sektor +0x34, 0) (0xc50e16/0xc50e83);
///             höchstens 0x60 solcher Materialien je Sektor (Zähler Sektor +0x219bc).
/// Sektortextur = &lt;sektor&gt;-s.2db (BankModel.SplatMapKey; die Shader lesen sie als gTexSamplerShadowMap: TD_Ground-VS
/// UV = (Welt.xz − SECTROFF.xz) · 0,0013 = 1/768 m Sektorgröße, PS: .y × UserVector1.y = Nachtlicht der Straße).
/// Sektor +0x30/+0x34 = Ursprung x/z (Konstruktor 0xc522b4: Ganzzahl-Lage − Weltverschiebung); hier in Spielkoordinaten,
/// die Verschiebung in den Render-Raum macht <see cref="Paradise.Core.Render.Original.OriginalConstants"/>.
/// Die Materialien gehören dem Sektor (eigene .2DM je Bank), deshalb wird die .2DM selbst geändert wie im Original.
/// Nur mit dem Original-Renderer: der eigene Shader-Pfad liest SHADOW 3/3 nicht (MaterialFactory) und bleibt unverändert.
/// Die Satz-Prüfung 0x0123210123210123 (Render-State +0x20 = 0x17) im selben Durchlauf ist noch nicht zugeordnet [offen].
/// </summary>
public static class SectorMaterials
{
    public const ulong ShaderRoad = 0xc8ad93c95455291bUL, ShaderGround = 0xb0f1234b0f123473UL, ShaderGroundBlock = 0xa0a0a1a1a2a2a3a3UL;
    public const int MaxSectorMaterials = 0x60;
    /// <summary>Adressmodus, den das Original für die Sektortextur einträgt.</summary>
    public const byte SectorAddress = 3;
    static readonly ulong s_kSectrOff = Bin.KeyFromString("SECTROFF");
    static int s_roadDumps;

    public static void Apply(Material2DM? lib, ulong sectorTexture, Vector3 origin, string? sectorName = null)
    {
        // ohne Sektortextur bliebe der Platzhalter (weiß) – das Original bindet dann keine Textur (D3D liest 0) [offen: Leer-Textur]
        if (lib == null || !GraphicsSettings.RenderUseOriginal) return;
        if (sectorTexture == 0) { Log.Warn($"Sector Materials {sectorName}: No sector texture (-s.2db)"); return; }
        int count = 0, roads = 0;
        foreach (var def in lib.Materials.Values)
        {
            if (def.ShaderSet == ShaderRoad)
            {
                for (int i = 0; i < def.Layers.Count; i++)
                {
                    var l = def.Layers[i];
                    if (l.Semantic != MaterialDef.SemShadow) continue;
                    l.TextureKey = sectorTexture; l.AddressU = SectorAddress; l.AddressV = SectorAddress;
                    def.Layers[i] = l;
                }
                roads++;
            }
            else if (def.ShaderSet == ShaderGround || def.ShaderSet == ShaderGroundBlock)
            {
                if (count >= MaxSectorMaterials) continue;
                int idx = def.Layers.FindIndex(l => l.Slot == 2);
                var layer = idx >= 0 ? def.Layers[idx] : new MaterialLayer { Slot = 2 };
                layer.TextureKey = sectorTexture; layer.AddressU = SectorAddress; layer.AddressV = SectorAddress;
                if (idx >= 0) def.Layers[idx] = layer; else def.Layers.Add(layer);
                def.Params[s_kSectrOff] = new Vector4(origin.X, 0f, origin.Z, 0f);
                count++;
            }
        }
    }
}
