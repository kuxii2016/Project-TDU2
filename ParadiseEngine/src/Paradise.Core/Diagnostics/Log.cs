using Paradise.Core.Config;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Paradise.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error, NI }

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
    public static bool isFullLog { get; set; } = true;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

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
    public static void NotImplemented(string msg) => Write(LogLevel.NI, msg);
    public static void Error(string msg, Exception ex) => Write(LogLevel.Error, msg + "\n" + ex);
    public static void PrintBuffer(byte[] buf)
    {
        string text = "\n" + FormatBuffer(buf) + "\n";
        Info(text + "\n");
    }

    private static string FormatBuffer(byte[] buf)
    {
        string bufferResult = "";
        int lineOffset = 0;
        do
        {
            int remainingBytes = buf.Length - lineOffset;
            int remainingBytesInLine = ((lineOffset + 16 > buf.Length - 1) ? remainingBytes : 16);
            string lineResult = $"{lineOffset:X4}";
            for (int columnOffset = 0; columnOffset < remainingBytesInLine; columnOffset++)
            {
                lineResult += $" {buf[lineOffset + columnOffset]:X2}";
            }
            bufferResult = bufferResult + lineResult + "\n";
            lineOffset += 16;
        }
        while (lineOffset < buf.Length);
        return bufferResult.Trim();
    }

    private static string GetCallerInfo([CallerMemberName] string callerMember = "")
    {
        var stackTrace = new StackTrace(skipFrames: 1, fNeedFileInfo: false);
        foreach (var frame in stackTrace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            var declaringType = method.DeclaringType;
            if (declaringType == null) continue;

            string fullName = declaringType.FullName;
            if (fullName == typeof(Log).FullName)
                continue;

            if (fullName.Contains("+<") ||
                fullName.StartsWith("System.Runtime.CompilerServices") ||
                fullName.StartsWith("System.Threading.Tasks"))
                continue;

            if (isFullLog)
            {
                if (method.Name.Contains(".ctor"))
                    return $"{fullName}::{declaringType.Name}";
                else
                    return $"{fullName}::{method.Name}";
            }

            if (method.Name.Contains(".ctor"))
                return $"{declaringType.Name}";
            else
                return $"{method.Name}";
        }
        return callerMember;
    }


    /// <summary>Die letzten Logzeilen (für den Crash-Report).</summary>
    public static string RecentLines()
    {
        var sb = new StringBuilder();
        foreach (var l in _ring) sb.AppendLine(l);
        return sb.ToString();
    }

    private static void Write(LogLevel level, string msg, [CallerMemberName] string callerMember = "")
    {
        if (level < MinLevel) return;
        string caller = GetCallerInfo(callerMember);
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {caller}()=>{msg}";

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
