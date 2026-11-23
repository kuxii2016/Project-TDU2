using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Liest die GC-Ereignisse der .NET-Runtime im eigenen Prozess (EventListener, keine Zusatz-DLL):
/// Pausen (SuspendEE → RestartEE), Generation und Grund. Die Ereignisse kommen etwas verzögert auf einem
/// Runtime-Thread an; die Zeitstempel werden auf Stopwatch-Ticks umgerechnet, damit sie in der Timeline passen.
/// </summary>
internal sealed class ProfilerGcListener : EventListener
{
    const int GCStart = 1, GCRestartEEEnd = 3, GCSuspendEEBegin = 9;
    const int Capacity = 4096;

    readonly object _lock = new();
    readonly Queue<ProfilerGcEvent> _events = new();
    // Bezugspunkt DateTime(UTC) <-> Stopwatch
    readonly long _refTicksUtc = DateTime.UtcNow.Ticks;
    readonly long _refStopwatch = Stopwatch.GetTimestamp();
    long _suspend;
    int _gen = -1, _gcIndex;
    string _reason = "";

    protected override void OnEventSourceCreated(EventSource source)
    {
        // Achtung: kann schon aus dem Basis-Konstruktor kommen (Felder noch nicht gesetzt) – hier nur einschalten.
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        try
        {
            switch (e.EventId)
            {
                case GCStart:
                    _gen = PayloadInt(e, "Depth");
                    _gcIndex = PayloadInt(e, "Count");
                    _reason = ReasonName(PayloadInt(e, "Reason"));
                    break;
                case GCSuspendEEBegin:
                    _suspend = ToStopwatch(e.TimeStamp);
                    break;
                case GCRestartEEEnd:
                    if (_suspend == 0) break;
                    var ev = new ProfilerGcEvent(_suspend, ToStopwatch(e.TimeStamp), _gen, _reason, _gcIndex);
                    _suspend = 0;
                    lock (_lock)
                    {
                        _events.Enqueue(ev);
                        while (_events.Count > Capacity) _events.Dequeue();
                    }
                    break;
            }
        }
        catch (Exception) { }
    }

    long ToStopwatch(DateTime ts)
    {
        long dt = ts.ToUniversalTime().Ticks - _refTicksUtc;   // 100-ns-Einheiten
        return _refStopwatch + (long)(dt * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }

    static int PayloadInt(EventWrittenEventArgs e, string name)
    {
        if (e.PayloadNames == null || e.Payload == null) return -1;
        int i = e.PayloadNames.IndexOf(name);
        if (i < 0 || i >= e.Payload.Count || e.Payload[i] == null) return -1;
        try { return Convert.ToInt32(e.Payload[i]); } catch (Exception) { return -1; }
    }

    static string ReasonName(int r) => r switch
    {
        0 => "AllocSmall",
        1 => "Induced",
        2 => "LowMemory",
        3 => "Empty",
        4 => "AllocLarge (LOH)",
        5 => "OutOfSpaceSOH",
        6 => "OutOfSpaceLOH",
        7 => "InducedNotForced",
        8 => "Internal",
        9 => "InducedLowMemory",
        10 => "InducedCompacting",
        11 => "LowMemoryHost",
        12 => "PMFullGC",
        13 => "LowMemoryHostBlocking",
        _ => "Reason " + r,
    };

    public void CopyTo(List<ProfilerGcEvent> dst) { lock (_lock) dst.AddRange(_events); }

    public void Clear() { lock (_lock) _events.Clear(); }
}
