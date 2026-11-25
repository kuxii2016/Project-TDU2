using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Video;
using Paradise.Game.Config;
using Paradise.Game.Screens;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Paradise.Game.Gamemodes
{
    /// <summary>
    /// On start Gamemode after Opening the game
    /// </summary>
    public class GAME_MODE_BOOTMENU : GameModeHandler
    {
        //TODO: recreate the Orginal Screens, Videos and Images, As first Image the Engine Logo -> Paradise Engine_ Neon-Insel-Logo.png <- Needs be backed into exe and show for half Minute
        // Play the Second Videos, Mouse Click Key Press can skip it, and go to the next Video, after all Videos are played, go to the Main Menu

        public override Gamemode Mode => Gamemode.GAME_MODE_BOOTMENU;

        /// <summary>Modus nach Logo und Intros (Program: Login mit Server, sonst Freifahrt).</summary>
        public static Gamemode Next = Gamemode.GAME_MODE_LOGIN;

        readonly BootConfig _cfg = new();
        IGpuTexture? _logo, _black;
        VideoPlayer? _video;
        int _step;          // 0 = Logo, 1.. = Videos
        float _t;
        bool _done;

        public override void Enter(IGameModeContext ctx, Gamemode from)
        {
            _step = 0; _t = 0f; _done = false;
            var r = FlashHost.Renderer;
            if (r == null) { Finish(); return; }
            _black ??= r.CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 0, 0, 0, 255 }, Name = "Boot-Schwarz", Linear = true });
            _logo ??= LoadLogo(r);
            if (_logo == null) NextStep();
        }

        public override void Run(IGameModeContext ctx, in GameTime time)
        {
            if (_done) return;
            float dt = time.DeltaTime;
            bool skip = _cfg.Skippable && AnyInput(ctx);
            if (_step == 0)
            {
                _t += dt;
                if (skip || _t >= _cfg.LogoSeconds) NextStep();
                return;
            }
            if (_video == null) { NextStep(); return; }
            _video.Update(dt);
            if (skip || _video.Finished) NextStep();
        }

        public override void Render(IGameModeContext ctx, in GameTime time)
        {
            if (_done || _black == null) return;
            var r = FlashHost.Renderer;
            if (r == null) return;
            float sw = Paradise.Core.Flash.Screen.width, sh = Paradise.Core.Flash.Screen.height;
            Quad(r, _black, 0, 0, sw, sh, 1f);
            if (_step == 0 && _logo != null)
            {
                float f = _cfg.LogoFadeSeconds > 0f ? System.Math.Clamp(System.Math.Min(_t, _cfg.LogoSeconds - _t) / _cfg.LogoFadeSeconds, 0f, 1f) : 1f;
                Fit(r, _logo, sw, sh, f);
            }
            else if (_video?.Texture != null) Fit(r, _video.Texture, sw, sh, 1f);
        }

        public override void Exit(IGameModeContext ctx, Gamemode to)
        {
            _video?.Dispose(); _video = null;
            _done = true;
        }

        void NextStep()
        {
            _video?.Dispose(); _video = null;
            while (true)
            {
                _step++;
                int i = _step - 1;
                if (i >= _cfg.Videos.Length) { Finish(); return; }
                var dir = GameData.BnkDir;
                var path = dir == null ? null : Path.Combine(dir, "Video", _cfg.Videos[i]);
                if (path == null || !File.Exists(path)) { Log.Warn("Intro fehlt: " + (path ?? _cfg.Videos[i])); continue; }
                var r = FlashHost.Renderer;
                if (r == null) { Finish(); return; }
                if (!OperatingSystem.IsWindows()) { Log.Warn("Intros: Videowiedergabe nur unter Windows (Media Foundation)."); Finish(); return; }
                _video = new VideoPlayer(r, path);
                _video.Start(_cfg.VideoVolume);
                return;
            }
        }

        void Finish()
        {
            if (_done) return;
            _done = true;
            GameMode.Set(Next);
        }

        /// <summary>startup.png aus der exe (EmbeddedResource, Paradise.Game.csproj).</summary>
        static IGpuTexture? LoadLogo(IRenderer r)
        {
            try
            {
                using var s = typeof(GAME_MODE_BOOTMENU).Assembly.GetManifestResourceStream("startup.png");
                if (s == null) { Log.Warn("startup.png ist nicht in die exe eingebettet."); return null; }
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var rgba = PngDecoder.Decode(ms.ToArray(), out int w, out int h);
                if (rgba == null) { Log.Warn("startup.png: Format nicht lesbar (erwartet 8 Bit, nicht verschachtelt)."); return null; }
                return r.CreateTexture(new TextureUpload { Width = w, Height = h, Format = GpuTextureFormat.Rgba8, Blocks = rgba, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Name = "startup.png" });
            }
            catch (Exception ex) { Log.Error("startup.png: " + ex.Message); return null; }
        }

        static bool AnyInput(IGameModeContext ctx)
        {
            bool any = FlashInput.GetMouseButtonDown(0) || FlashInput.GetMouseButtonDown(1);
            for (int vk = 0x08; vk < 0xFF; vk++) if (ctx.Pressed(vk)) any = true;   // alle abfragen (Flankenspeicher aktuell halten)
            return any;
        }

        /// <summary>Bild bildschirmfüllend mit Seitenverhältnis (schwarze Ränder).</summary>
        static void Fit(IRenderer r, IGpuTexture t, float sw, float sh, float alpha)
        {
            float s = System.Math.Min(sw / t.Width, sh / t.Height);
            float w = t.Width * s, h = t.Height * s;
            Quad(r, t, (sw - w) * 0.5f, (sh - h) * 0.5f, w, h, alpha);
        }

        static readonly ObjectVertex[] _q = new ObjectVertex[4];
        static void Quad(IRenderer r, IGpuTexture t, float x, float y, float w, float h, float alpha)
        {
            _q[0] = new ObjectVertex { Position = new Vector3(x, y, 0), Uv0 = new Vector2(0, 0) };
            _q[1] = new ObjectVertex { Position = new Vector3(x + w, y, 0), Uv0 = new Vector2(1, 0) };
            _q[2] = new ObjectVertex { Position = new Vector3(x + w, y + h, 0), Uv0 = new Vector2(1, 1) };
            _q[3] = new ObjectVertex { Position = new Vector3(x, y + h, 0), Uv0 = new Vector2(0, 1) };
            var p = new FlashQuadParams { Mul = new Vector4(1, 1, 1, alpha), Add = Vector4.Zero, Clip = new Vector4(-1e7f, -1e7f, 1e7f, 1e7f), MaskRect = Vector4.Zero };
            r.DrawFlash(_q, t, null, p);
        }
    }
}