using Paradise.Core.Config;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Global crash handler. Installs handlers for unhandled exceptions
/// and writes the following for each crash:
///   - a minidump (Crash_time.dmp) via dbghelp.dll (included with Windows)
///   - a text report (Crash_time.txt) containing context, stack trace, and recent logs
/// Use at the very beginning of Main():
///   CrashHandler.Install(“Game”);
/// Additional information (loaded sector, vehicle, startup parameters) via
///   CrashHandler.SetContext(“sector”, “A-12-7-3”);
/// </summary>
public static class CrashHandler
{
    private static readonly Dictionary<string, string> _context = new();
    private static readonly object _gate = new();
    private static string _tag = "App";
    private static bool _installed;

    public static void Install(string tag = "App")
    {
        if (_installed) return;
        _installed = true;
        _tag = tag;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(e.ExceptionObject as Exception, terminating: e.IsTerminating, source: "AppDomain");

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, terminating: false, source: "UnobservedTask");
            e.SetObserved();
        };

        SetContext("app", tag);
        SetContext("version", typeof(CrashHandler).Assembly.GetName().Version?.ToString() ?? "?");
        SetContext("cmdline", Environment.CommandLine);
        SetContext("os", Environment.OSVersion.VersionString);
        SetContext("clr", Environment.Version.ToString());
        SetContext("machine", $"{Environment.MachineName} / {Environment.ProcessorCount} Kerne / x64={Environment.Is64BitProcess}");
    }

    public static void SetContext(string key, string value)
    {
        lock (_gate) _context[key] = value;
    }

    /// <summary>Manuell einen Report erzeugen, ohne dass die App abstürzt (z. B. aus einem catch).</summary>
    public static string Report(Exception ex) => Handle(ex, terminating: false, source: "Manual");

    private static string Handle(Exception? ex, bool terminating, string source)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string baseName = $"Crash_{stamp}";
        string dir = UserPaths.Crashes;
        string txtPath = Path.Combine(dir, baseName + ".txt");
        string dmpPath = Path.Combine(dir, baseName + ".dmp");

        try { WriteTextReport(txtPath, ex, source); } catch { /* niemals im Crash-Pfad werfen */ }
        try { WriteMiniDump(dmpPath); } catch { /* dbghelp evtl. nicht verfügbar */ }

        try { Log.Error($"CRASH ({source}) -> {txtPath}"); } catch { }
        return txtPath;
    }

    private static void WriteTextReport(string path, Exception? ex, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Paradise Engine – Crash Report ===");
        sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Source:  {source}");
        sb.AppendLine();

        sb.AppendLine("--- Context ---");
        lock (_gate)
            foreach (var kv in _context)
                sb.AppendLine($"{kv.Key,-10}: {kv.Value}");
        sb.AppendLine();

        sb.AppendLine("--- Exception ---");
        if (ex != null)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
                sb.AppendLine(e.StackTrace);
                if (e.InnerException != null) sb.AppendLine("-- InnerException --");
            }
        }
        else sb.AppendLine("no Exception");
        sb.AppendLine();

        sb.AppendLine("--- Last Logs ---");
        sb.AppendLine(Log.RecentLines());

        File.WriteAllText(path, sb.ToString());
    }

    // ---- Minidump via dbghelp.dll (Teil von Windows, keine Zusatz-DLL) ----

    [Flags]
    private enum MiniDumpType : uint
    {
        Normal = 0x00000000,
        WithDataSegs = 0x00000001,
        WithThreadInfo = 0x00001000,
        WithIndirectlyReferencedMemory = 0x00000040,
        WithFullMemoryInfo = 0x00000800,
        WithHandleData = 0x00000004,
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, SafeHandle hFile,
        MiniDumpType dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private static void WriteMiniDump(string path)
    {
        using var proc = Process.GetCurrentProcess();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Write);
        const MiniDumpType type = MiniDumpType.WithThreadInfo | MiniDumpType.WithDataSegs
                                | MiniDumpType.WithHandleData | MiniDumpType.WithFullMemoryInfo;
        MiniDumpWriteDump(proc.Handle, (uint)proc.Id, fs.SafeFileHandle, type,
                          IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }
}