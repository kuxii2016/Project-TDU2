#if WINDOWS
using Paradise.Core.Diagnostics;
using Paradise.Game.Config;

namespace Paradise.Game.Screens;

/// <summary>
/// F1 &gt; Performance &gt; Open Profiler: Profiler-Fenster (Projekt Paradise.ProfilerUI, Nachbau Unity-Profiler).
/// Läuft wie der Flash-Browser im Spielprozess auf einem eigenen UI-Thread und bleibt offen, wenn das F1-Menü schließt.
/// </summary>
public sealed partial class DevMenu
{
    void OpenProfiler()
    {
        if (!Profiler.Available)
        {
            Toast("Profiler only in Debug builds (or Release with -p:Profiler=true)");
            return;
        }
        Profiler.SetInfo("Engine", AppConfig.EngineName + " " + AppConfig.EngineVersion);
        Paradise.ProfilerUI.ProfilerWindow.Open();
        Toast("Profiler opened");
    }
}
#endif
