// Profiler-Fenster der Paradise Engine (Nachbau Unity-Profiler).
// Läuft wie der Flash-Browser auf einem eigenen STA-Thread im Spielprozess. Es liest nur fertige,
// unveränderliche Bilder aus Paradise.Core.Diagnostics.Profiler – kein Zugriff auf Spielobjekte.

using Paradise.Core.Diagnostics;

namespace Paradise.ProfilerUI;

public static class ProfilerWindow
{
    static readonly object _lock = new();
    static ProfilerForm? _form;
    static Thread? _thread;

    public static bool IsOpen { get { lock (_lock) return _form != null && _thread != null && _thread.IsAlive; } }

    /// <summary>F1 &gt; Performance &gt; Open Profiler. Öffnet (oder fokussiert) das Fenster und startet die Aufnahme.</summary>
    public static void Open()
    {
        TaskCompletionSource<bool> ready;
        Thread t;
        lock (_lock)
        {
            if (IsOpen)
            {
                var f = _form;
                if (f != null)
                    try { f.BeginInvoke(new Action(f.FocusMe)); } catch (Exception) { }
                return;
            }
            ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            t = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    var form = new ProfilerForm();
                    lock (_lock) _form = form;
                    ready.TrySetResult(true);
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    Log.Error("Profiler window: " + ex);
                    ready.TrySetResult(false);
                }
                finally
                {
                    lock (_lock) _form = null;
                    Profiler.Enabled = false;
                }
            })
            { IsBackground = true, Name = "Paradise Profiler UI" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
        }
        // Nicht im lock warten – der UI-Thread setzt _form ebenfalls unter _lock.
        t.Start();
        try { ready.Task.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }

    public static void Close()
    {
        var f = _form;
        if (f != null && !f.IsDisposed)
            try { f.BeginInvoke(new Action(f.Close)); } catch (Exception) { }
    }
}
