
using Paradise.Core.Diagnostics;
using Paradise.Core.Platform;
using Paradise.Core.Render;
using System.Diagnostics;

namespace Paradise.Core.Engine;

/// <summary>
/// Haupt-Schleife der Engine: fester Physik-Takt (Accumulator) plus variabler
/// Render-Takt. Besitzt Fenster und Renderer, ruft die registrierten Module.
/// </summary>
public sealed class GameLoop : IDisposable
{
    private readonly IGameWindow _window;
    private readonly IRenderer _renderer;
    private readonly List<IGameModule> _modules = new();
    public float FixedStep { get; set; } = 1f / 60f;
    public float MaxFrameTime { get; set; } = 0.25f;
    public bool Running { get; private set; }
    /// <summary>Debug-UI (F12). Module können über AddSection eigene Werte anzeigen.</summary>
    public DebugOverlay Overlay { get; }

    private const int VK_F12 = 0x7B;
    private bool _f12Was;

    // Profiler (F1 > Performance > Open Profiler): Messpunkte der Schleife und je Modul/Phase
    private static readonly ProfilerMarker s_mPump = new("Window.PumpEvents", ProfilerCategory.Other);
    private static readonly ProfilerMarker s_mFixed = new("GameLoop.FixedUpdate", ProfilerCategory.Scripts);
    private static readonly ProfilerMarker s_mUpdate = new("GameLoop.Update", ProfilerCategory.Scripts);
    private static readonly ProfilerMarker s_mFlashUpdate = new("Flash.Update (all movies)", ProfilerCategory.UI);
    private static readonly ProfilerMarker s_mRender = new("GameLoop.Render", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mBegin = new("Renderer.BeginFrame", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mFlashRender = new("Flash.Render (all movies)", ProfilerCategory.UI);
    private static readonly ProfilerMarker s_mOverlay = new("DebugOverlay.Draw", ProfilerCategory.Other);
    private static readonly ProfilerMarker s_mEnd = new("Renderer.EndFrame", ProfilerCategory.Rendering);
    private ProfilerMarker[] _mFixedMod = Array.Empty<ProfilerMarker>();
    private ProfilerMarker[] _mUpdateMod = Array.Empty<ProfilerMarker>();
    private ProfilerMarker[] _mRenderMod = Array.Empty<ProfilerMarker>();

    public GameLoop(IGameWindow window, IRenderer renderer)
    {
        _window = window;
        _renderer = renderer;

        Overlay = new DebugOverlay(renderer);

        _window.Resized += (w, h) => _renderer.Resize(w, h);
        _window.CloseRequested += () => Running = false;
    }

    public void Add(IGameModule module) => _modules.Add(module);

    public void Run()
    {
        _renderer.Initialize(_window.Handle, _window.Width, _window.Height);

        CrashHandler.SetContext("renderer", _renderer.DeviceName);
        Profiler.SetInfo("Renderer", _renderer.DeviceName);
        ProfilerCounters.RegisterRenderer(_renderer);

        Flash.FlashHost.Attach(_renderer);
        Audio.AudioEngine.Start();

        foreach (var module in _modules)
            module.Load();

        CreateModuleMarkers();

        Running = true;

        var stopwatch = Stopwatch.StartNew();
        double last = stopwatch.Elapsed.TotalSeconds;
        double accumulator = 0;

        var time = new GameTime
        {
            FixedDeltaTime = FixedStep
        };

        while (Running && !_window.IsClosing)
        {
            using (s_mPump.Auto())
                _window.PumpEvents();

            double now = stopwatch.Elapsed.TotalSeconds;

            float frame = (float)(now - last);
            last = now;

            if (frame > MaxFrameTime)
                frame = MaxFrameTime;

            time.DeltaTime = frame;
            time.TotalTime = now;
            accumulator += frame;

            // Fixed Update
            using (s_mFixed.Auto())
            {
                while (accumulator >= FixedStep)
                {
                    for (int i = 0; i < _modules.Count; i++)
                        using (_mFixedMod[i].Auto())
                            _modules[i].FixedUpdate(in time);
                    accumulator -= FixedStep;
                }
            }

            time.FixedAlpha = (float)(accumulator / FixedStep);

            // F12: Performance view
            bool f12 = _window.Input.IsKeyDown(VK_F12);

            if (f12 && !_f12Was)
                Overlay.Visible = !Overlay.Visible;

            _f12Was = f12;

            // Variable Update
            using (s_mUpdate.Auto())
            {
                using (s_mFlashUpdate.Auto())
                    Flash.FlashHost.Update(_window.Width, _window.Height);

                for (int i = 0; i < _modules.Count; i++)
                    using (_mUpdateMod[i].Auto())
                        _modules[i].Update(in time);
            }

            // Render
            using (s_mRender.Auto())
            {
                using (s_mBegin.Auto())
                    _renderer.BeginFrame();

                for (int i = 0; i < _modules.Count; i++)
                    using (_mRenderMod[i].Auto())
                        _modules[i].Render(in time);

                using (s_mFlashRender.Auto())
                    Flash.FlashHost.Render();
                using (s_mOverlay.Auto())
                    Overlay.Draw();
                using (s_mEnd.Auto())
                    _renderer.EndFrame();
            }
            // Erst nach dem Sichtbarmachen zählen: ein Bild, das wirklich sichtbar gerendert/presentiert wurde.
            if (_window.IsVisible) time.VisibleFrames++;
            // Erst jetzt anzeigen: Renderer, Module und das erste Bild (schwarzer Shader-Cache-/Startbildschirm) sind fertig
            if (time.FrameCount == 0) _window.Show();
            Overlay.RecordFrame(now, frame, 0, 0);
            Profiler.EndFrame();

            time.FrameCount++;
        }

        for (int i = _modules.Count - 1; i >= 0; i--)
            _modules[i].Unload();

        Flash.FlashHost.Shutdown();
        Audio.AudioEngine.Shutdown();
        Overlay.Dispose();
    }

    public void Stop() => Running = false;

    private void CreateModuleMarkers()
    {
        int n = _modules.Count;
        _mFixedMod = new ProfilerMarker[n];
        _mUpdateMod = new ProfilerMarker[n];
        _mRenderMod = new ProfilerMarker[n];
        for (int i = 0; i < n; i++)
        {
            string name = _modules[i].GetType().Name;
            _mFixedMod[i] = ProfilerMarker.Get(name + ".FixedUpdate", ProfilerCategory.Scripts);
            _mUpdateMod[i] = ProfilerMarker.Get(name + ".Update", ProfilerCategory.Scripts);
            _mRenderMod[i] = ProfilerMarker.Get(name + ".Render", ProfilerCategory.Rendering);
        }
    }

    public void Dispose()
    {
        // windows owned by caller
    }
}