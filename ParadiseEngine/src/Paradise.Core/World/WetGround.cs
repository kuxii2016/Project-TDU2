using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// WFX_WetGrd des Originals (rain_shadow_spec A.2): Texturen PUDDLES und RIPPLE_01..16 aus fx/weatherfx.bnk,
/// Ripple-Verschiebung und -Animation. Die Formel selbst steht im Objekt-Shader (TD_Road-Regenvariante).
/// <list type="bullet">
/// <item>update FUN_00aa0b60: s = dt · rain_ground_ripple_scroll_speed · 0,15; scroll_u += s; scroll_v += s.</item>
/// <item>FUN_00aa0440: Zeitgeber &lt; 0 → Textur RIPPLE_(frame+1), frame = (frame+1) &amp; 15, Zeitgeber = 0,025 s;
///   sonst Zeitgeber −= dt.</item>
/// </list>
/// Die Texturen gehen über RenderFx an die Frame-Bindings 5/6.
/// </summary>
public sealed class WetGround : IDisposable
{
    readonly IGpuTexture?[] _ripples = new IGpuTexture?[16];
    IGpuTexture? _puddles;
    float _timer;
    int _frame;
    Vector2 _scroll;

    public static WetGround? Create(string? bnkDir, GlobalTextureCache textures)
    {
        string? path = bnkDir != null ? Path.Combine(bnkDir, "fx", "weatherfx.bnk") : null;
        if (path == null || !GameFiles.Exists(path)) { Log.Warn("Regen: fx/weatherfx.bnk fehlt"); return null; }
        var w = new WetGround();
        try
        {
            textures.IndexBank(BnkArchive.Open(path));
            w._puddles = textures.Get(textures.KeyByName("puddles.2db"), TextureWrap.Repeat, TextureWrap.Repeat);
            int found = 0;
            for (int i = 0; i < 16; i++)
            {
                w._ripples[i] = textures.Get(textures.KeyByName($"ripple_{i + 1:00}.2db"), TextureWrap.Repeat, TextureWrap.Repeat);
                if (w._ripples[i] != null) found++;
            }
            Log.Info($"Regen: PUDDLES {(w._puddles != null ? "ok" : "fehlt")}, RIPPLE {found}/16");
            RenderFx.RainPuddles = w._puddles;
            RenderFx.RainRipple = w._ripples[0];
            return w;
        }
        catch (Exception ex) { Log.Warn("Regen: " + ex.Message); return null; }
    }

    public void Update(float dt)
    {
        float s = dt * RenderFx.RainScrollSpeed * 0.15f;
        _scroll += new Vector2(s, s);
        RenderFx.RainScroll = _scroll;
        if (_timer < 0f)
        {
            var tex = _ripples[_frame];          // RIPPLE_(frame+1)
            _frame = (_frame + 1) & 15;
            _timer = 0.025f;
            if (tex != null) RenderFx.RainRipple = tex;
        }
        else _timer -= dt;
    }

    public void Dispose()
    {
        RenderFx.RainPuddles = null;
        RenderFx.RainRipple = null;
    }
}
