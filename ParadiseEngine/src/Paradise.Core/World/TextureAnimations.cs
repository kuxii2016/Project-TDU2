using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Collections.Concurrent;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Textur-Animationen (.uva) der Welt-Banks (Sektoren, Spot-Exteriors, World Actors). Portiert aus dem Unity-Projekt
/// (InteriorFx.PrepareUva / ApplyUva, RaceUvaAnimator, DetailUva):
///   .2dm: MAT.-Ebene +28 -> MTP. +20 -> UVA.-Schlüssel -> .uva (ANM!) der Bank.
///   Animiert wird _MainTex_ST bzw. bei DETAIL-Ebenen _DetailTex_ST; Neon (TD_NEONDECAL) bekommt die echte Leuchtmaske.
/// </summary>
public static class TextureAnimations
{
    public sealed class UvaLink { public string Material = ""; public string? Texture; public bool Detail; public RaceUva Anim = null!; }

    static readonly ConcurrentDictionary<string, List<UvaLink>> _links = new(StringComparer.OrdinalIgnoreCase);
    static readonly ulong SemDetail = Bin.KeyFromString("DETAIL"), SemDetail1 = Bin.KeyFromString("DETAIL1");

    // laufende Animationen (Main-Thread)
    sealed class Item { public IGpuMaterial Mat = null!; public RaceUva? Main, Detail; public string Bank = ""; }
    static readonly Dictionary<IGpuMaterial, Item> _items = new(ReferenceEqualityComparer.Instance);

    public static int ActiveCount => _items.Count;

    /// <summary>Im Lade-Thread: .uva der Bank lesen und den Materialien zuordnen.</summary>
    public static void PrepareUva(BnkArchive? bank)
    {
        if (bank == null || string.IsNullOrEmpty(bank.FilePath)) return;
        var list = new List<UvaLink>();
        try
        {
            var byKey = new Dictionary<ulong, RaceUva>();
            foreach (var e in bank.Entries)
            {
                if (e.Extension != ".uva") continue;
                try { var u = RaceUva.Parse(bank.Read(e), Path.GetFileNameWithoutExtension(e.Name)); if (u != null) byKey[u.Key] = u; } catch (Exception) { }
            }
            if (byKey.Count > 0)
            {
                var texName = new Dictionary<ulong, string>();
                foreach (var e in bank.Entries)
                    if (e.Extension == ".2db")
                    {
                        var hd = bank.ReadPartial(e, 0, 0x28);
                        ulong k = hd != null ? TextureDef.PeekKey(hd) : 0;
                        if (k != 0) texName[k] = Path.GetFileNameWithoutExtension(e.Name);
                    }
                foreach (var e in bank.Entries)
                {
                    if (e.Extension != ".2dm") continue;
                    var d = bank.Read(e);
                    for (int m = 0; m + 0x110 <= d.Length; m++)
                    {
                        if (d[m] != 'M' || d[m + 1] != 'A' || d[m + 2] != 'T' || d[m + 3] != '.') continue;
                        string name = Bin.CString(d, m + 0x20, 32);
                        int n = System.Math.Min(16, (int)Bin.U16(d, m + 0x10C));
                        for (int i = 0; i < n; i++)
                        {
                            int p = m + 0x110 + i * 32;
                            if (p + 32 > d.Length) break;
                            ulong sem = Bin.U64(d, p), tk = Bin.U64(d, p + 8);
                            int mtp = (int)Bin.U32(d, p + 28);
                            if (mtp <= 0 || mtp + 24 > d.Length) continue;
                            int up = (int)Bin.U32(d, mtp + 20);
                            if (up <= 0 || up + 8 > d.Length) continue;
                            if (!byKey.TryGetValue(Bin.U64(d, up), out var anim)) continue;
                            texName.TryGetValue(tk, out var tn);
                            list.Add(new UvaLink { Material = name, Texture = tn, Detail = sem == SemDetail || sem == SemDetail1, Anim = anim });
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("UVA: " + ex.Message); }
        if (list.Count > 0) _links[bank.FilePath] = list;
        else _links.TryRemove(bank.FilePath, out _);
    }

    static string Stem(string? n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int dot = n.LastIndexOf('.');
        return dot > 0 ? n[..dot] : n;
    }

    /// <summary>
    /// Main-Thread: Materialien der gebauten Objekte einer Bank mit ihren Animationen verbinden (Unity: ApplyUva keep=true).
    /// Gibt die Zahl animierter Materialien zurück.
    /// </summary>
    public static int Apply(string? bankPath, IEnumerable<RenderObject> objects)
    {
        if (string.IsNullOrEmpty(bankPath) || !_links.TryGetValue(bankPath, out var links) || links.Count == 0) return 0;
        var done = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
        int n = 0;
        foreach (var o in objects)
            foreach (var lod in o.Lods)
                foreach (var part in lod.Parts)
                {
                    var m = part.Material;
                    if (m == null || !done.Add(m)) continue;
                    if (Bind(m, links, bankPath)) n++;
                }
        return n;
    }

    /// <summary>Animation eines Materials passend zu Materialname/Haupttextur wählen und Haupt- oder Detailebene zuordnen.</summary>
    static bool Bind(IGpuMaterial m, List<UvaLink> links, string bankPath)
    {
        if (m.Kind is not (MaterialKind.Object or MaterialKind.Transparent)) return false;   // z.B. TDU2/Water
        if (!MaterialFactory.TryGetInfo(m, out var info)) return false;
        string mn = info.Def.Name ?? "";
        string tex = Stem(info.MainTexture);
        UvaLink? best = null;
        foreach (var l in links)
        {
            if (string.IsNullOrEmpty(l.Material) || !mn.StartsWith(l.Material, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || (tex.Length > 0 && string.Equals(l.Texture, tex, StringComparison.OrdinalIgnoreCase))) best = l;
        }
        if (best == null) return false;
        if (!_items.TryGetValue(m, out var it)) _items[m] = it = new Item { Mat = m };
        it.Bank = bankPath;
        it.Main = null; it.Detail = null;
        if (best.Detail && info.Desc.DetailMode) it.Detail = best.Anim;
        else { MaterialFactory.UseNeonMask(m); it.Main = best.Anim; }
        return true;
    }

    /// <summary>
    /// Animation von einem Material auf ein ersetztes übertragen (z.B. Unity ConvertMaterials der Renn-Tore:
    /// BuildScene bindet die .uva an das 2dm-Material, danach tauscht RaceProps es gegen das Checkpoint-Overlay).
    /// </summary>
    public static void Transfer(IGpuMaterial from, IGpuMaterial to)
    {
        if (ReferenceEquals(from, to) || !_items.TryGetValue(from, out var it)) return;
        _items[to] = new Item { Mat = to, Bank = it.Bank, Main = it.Main, Detail = it.Detail };
    }

    /// <summary>
    /// Material wurde mit nachgereichten Texturen neu aufgebaut (MaterialFactory.RetryPendingTextures): Zuordnung neu treffen –
    /// fehlte die Detail- oder Haupttextur beim ersten Bau, lief die Animation sonst auf der falschen Ebene.
    /// </summary>
    public static void Rebind(IGpuMaterial m, MaterialDesc d)
    {
        if (!_items.TryGetValue(m, out var it) || string.IsNullOrEmpty(it.Bank)) return;
        if (_links.TryGetValue(it.Bank, out var links) && links.Count > 0) Bind(m, links, it.Bank);
    }

    /// <summary>Pro Frame: _MainTex_ST / _DetailTex_ST aller animierten Materialien setzen.</summary>
    public static void Update(IRenderer r, float time)
    {
        if (_items.Count == 0) return;
        var one = new Vector4(1, 1, 0, 0);
        foreach (var it in _items.Values)
            r.SetMaterialUv(it.Mat, it.Main?.ST(time) ?? one, it.Detail?.ST(time) ?? one);
    }

    /// <summary>Freigegebene Materialien nicht mehr animieren.</summary>
    public static void Forget(IGpuMaterial m) => _items.Remove(m);

    public static void Clear() { _items.Clear(); _links.Clear(); }
}