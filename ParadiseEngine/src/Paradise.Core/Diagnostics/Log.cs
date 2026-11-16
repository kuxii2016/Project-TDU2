using Paradise.Core.Config;
using System.Collections.Concurrent;
using System.Text;

namespace Paradise.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Schlanker Logger: schreibt in Konsole und in eine Logdatei unter
/// Documents\KuxiiSoft\TDU2toUnity3D\Logs. Hält zusätzlich die letzten
/// Zeilen im Ring vor, damit der CrashHandler sie in den Report legen kann.
/// Thread-sicher.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();
    private static StreamWriter? _file;
    private static readonly ConcurrentQueue<string> _ring = new();
    private const int RingSize = 200;

    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    public static void Init(string? tag = null)
    {
        lock (_gate)
        {
            if (_file != null) return;
            string name = $"paradise_{DateTime.Now:yyyyMMdd_HHmmss}{(tag != null ? "_" + tag : "")}.log";
            _file = new StreamWriter(Path.Combine(UserPaths.Logs, name), append: false)
            {
                AutoFlush = true
            };
        }
        Info($"Paradise Engine Log gestartet ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void Error(string msg, Exception ex) => Write(LogLevel.Error, msg + "\n" + ex);

    /// <summary>Die letzten Logzeilen (für den Crash-Report).</summary>
    public static string RecentLines()
    {
        var sb = new StringBuilder();
        foreach (var l in _ring) sb.AppendLine(l);
        return sb.ToString();
    }

    private static void Write(LogLevel level, string msg)
    {
        if (level < MinLevel) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {msg}";

        _ring.Enqueue(line);
        while (_ring.Count > RingSize) _ring.TryDequeue(out _);

        lock (_gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
            System.Diagnostics.Debug.WriteLine(line);
        }
    }
}
