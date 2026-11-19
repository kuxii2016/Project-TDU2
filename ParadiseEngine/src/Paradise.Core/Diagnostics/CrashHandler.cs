using Paradise.Core.Config;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Global crash handler. Installs handlers for unhandled exceptions
/// and writes the following for each crash:
///   - a minidump (Crash_time.dmp) via dbghelp.dll (included with Windows)
///   - a text report (Crash_time.txt) with cause, function/file/line, context,
///     system specs, stack traces and recent logs
/// Use at the very beginning of Main():
///   CrashHandler.Install(“Game”);
/// Additional information (loaded sector, vehicle, startup parameters) via
///   CrashHandler.SetContext(“sector”, “A-12-7-3”);
///
/// Wichtig: DiePDB-Informationen müssen zur Laufzeit verfügbar sein, sonst gibt es
/// nur Funktionsnamen ohne Zeilennummern. Dafür <DebugType>embedded</DebugType>
/// in Directory.Build.props / Paradise.Game.csproj – eine externe .pdb wird beim
/// Single-File-Publish nicht mit ausgeliefert.
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

        // Wichtig: Ein UnobservedTaskException beendet die Anwendung NICHT (seit .NET 4.5).
        // Früher wurde dafür ein Crash-Report mit .dmp geschrieben – dadurch sah ein
        // harmloser, beim Beenden abgebrochener Socket-Connect wie ein Absturz aus.
        // Nur noch loggen und die Exception beobachten, damit der Finalizer sie nicht
        // erneut meldet. Echte Abstürze laufen über AppDomain.UnhandledException.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                var root = e.Exception?.GetBaseException();
                if (root != null)
                {
                    Log.Warn($"Hintergrund-Task nicht beobachtet: {root.GetType().Name}: {root.Message}\n" +
                             (root.StackTrace ?? "(keine Stacktrace)"));
                }
            }
            catch { /* niemals im Finalizer werfen */ }
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
        sb.AppendLine($"Zeit:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Quelle:  {source}");
        sb.AppendLine();

        // Die Ursache steht absichtlich ganz oben – darum geht es beim Report zuerst.
        sb.AppendLine("--- Ursache ---");
        try { sb.AppendLine(BuildCause(ex).TrimEnd()); }
        catch (Exception e) { sb.AppendLine("(Ursache konnte nicht ermittelt werden: " + e.Message + ")"); }
        sb.AppendLine();

        sb.AppendLine("--- Kontext ---");
        lock (_gate)
            foreach (var kv in _context)
                sb.AppendLine($"{kv.Key,-10}: {kv.Value}");
        sb.AppendLine();

        sb.AppendLine("--- Exception ---");
        if (ex != null)
        {
            int depth = 0;
            for (var e = ex; e != null; e = e.InnerException)
            {
                sb.AppendLine($"[{depth++}] {e.GetType().FullName}: {e.Message}");
                sb.AppendLine($"      HResult=0x{e.HResult:X8} | Quelle={e.Source ?? "-"} | Funktion={FormatMethod(e.TargetSite)}");
                try { sb.Append(FormatStackTrace(e)); }
                catch (Exception se) { sb.AppendLine("      (Stacktrace nicht lesbar: " + se.Message + ")"); }
                if (e.InnerException != null) sb.AppendLine("      --- InnerException ---");
            }
        }
        else sb.AppendLine("kein Exception-Objekt (vermutlich nativer Absturz – siehe .dmp)");
        sb.AppendLine();

        sb.AppendLine("--- System ---");
        try { sb.AppendLine(BuildSystemInfo().TrimEnd()); }
        catch (Exception se) { sb.AppendLine("(Systeminfos nicht vollständig lesbar: " + se.Message + ")"); }
        sb.AppendLine();

        sb.AppendLine("--- Letzte Logzeilen ---");
        try { sb.AppendLine(Log.RecentLines()); }
        catch { sb.AppendLine("(nicht verfügbar)"); }

        File.WriteAllText(path, sb.ToString());
    }

    // ------------------------------------------------------------------ Ursache

    private static string BuildCause(Exception? ex)
    {
        if (ex == null)
            return "Kein Exception-Objekt – wahrscheinlich ein nativer Absturz. Die .dmp-Datei enthält weitere Details.";

        var root = ex;
        int depth = 0;
        while (root.InnerException != null && depth < 20) { root = root.InnerException; depth++; }

        var sb = new StringBuilder();
        sb.AppendLine($"Typ:     {root.GetType().FullName}");
        sb.AppendLine($"Meldung: {root.Message}");
        sb.AppendLine($"Deutung: {Explain(root)}");

        try
        {
            var frame = new StackTrace(root, fNeedFileInfo: true).GetFrame(0);
            if (frame != null)
            {
                string file = frame.GetFileName() ?? "";
                int line = frame.GetFileLineNumber();
                string where = FormatMethod(frame.GetMethod());
                if (line > 0) where += $" in {file}:{line}";
                sb.AppendLine($"Auslöser: {where}");
            }
        }
        catch { /* Stacktrace ist optional */ }

        if (depth > 0) sb.AppendLine($"(tiefste InnerException, {depth} Ebene(n) unter der äußeren Exception)");
        return sb.ToString();
    }

    private static string Explain(Exception e)
    {
        string msg = e.Message ?? "";

        if (e is CultureNotFoundException || msg.Contains("culture", StringComparison.OrdinalIgnoreCase))
            return "InvariantGlobalization ist aktiv: benannte Kulturen wie \"de-DE\" können nicht geladen werden. " +
                   "Statt CultureInfo.GetCultureInfo(\"de-DE\") Paradise.Core.Util.GermanFormat.Number verwenden " +
                   "(oder InvariantGlobalization in Directory.Build.props abschalten).";

        if (e is NullReferenceException)
            return "Ein Objekt war null – meist ein noch nicht geladener Flash-Film/Node, eine fehlende Textur " +
                   "oder ein nicht initialisiertes Feld. Siehe 'Auslöser' und die erste Stacktrace-Zeile.";

        if (e is IndexOutOfRangeException || e is ArgumentOutOfRangeException)
            return "Ein Index lag außerhalb des gültigen Bereichs (leere Liste, falsche Arraygröße oder ungültiger Slot).";

        if (e is OutOfMemoryException)
            return "Speicher erschöpft – die [DIAG Mem]-Zeilen der letzten Logs zeigen Heap-, Textur- und Physik-Verbrauch.";

        if (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            return "Eine native DLL fehlt, hat den falschen Einstiegspunkt oder die falsche Architektur (x64 erwartet).";

        if (e is FileNotFoundException || e is DirectoryNotFoundException || e is UnauthorizedAccessException)
            return "Datei oder Verzeichnis fehlt bzw. kein Zugriff – Pfad in der Meldung und im Kontext prüfen " +
                   "(StreamingAssets, Saves, Config).";

        if (e is System.Net.Sockets.SocketException || e is System.Net.Http.HttpRequestException || e is TimeoutException)
            return "Netzwerk-/Serverproblem: Verbindung, Port oder Antwortzeit prüfen (Kontext 'server').";

        if (e is System.Text.Json.JsonException)
            return "Eine JSON-Datei (Savegame, Serverantwort oder Config) konnte nicht gelesen werden – " +
                   "beschädigt oder Format geändert.";

        if (e is AccessViolationException)
            return "Native Speicherzugriff verletzt – meist Treiber-/Vulkan- oder Pointer-Problem, Details in der .dmp.";

        if (e is InvalidOperationException)
            return "Operation im aktuellen Zustand nicht erlaubt (z. B. Film/Renderer noch nicht bereit oder doppelte Initialisierung).";

        if (msg.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) || msg.Contains("VK_", StringComparison.OrdinalIgnoreCase))
            return "Vulkan-Fehler: Grafiktreiber, Device oder Shader-Pipeline prüfen; zum Eingrenzen '--nullrenderer' nutzen.";

        if (e is DivideByZeroException)
            return "Division durch null – ein Zähler/Nenner wurde nicht gegen 0 geprüft.";

        return "Funktion, Datei und Zeile stehen in der Stacktrace, zusätzliche Hinweise in den letzten Logzeilen.";
    }

    // ------------------------------------------------------------------ Stacktrace

    private static string FormatMethod(MethodBase? m)
    {
        if (m == null) return "?";
        string type = m.DeclaringType?.FullName ?? "?";
        string name = m.Name.StartsWith('<') ? "Lambda/MoveNext" : m.Name;
        return type + "." + name;
    }

    /// <summary>Stacktrace mit Funktionsname, Datei, Zeile und Spalte (falls PDB verfügbar).</summary>
    private static string FormatStackTrace(Exception ex)
    {
        var frames = new StackTrace(ex, fNeedFileInfo: true).GetFrames();
        if (frames == null || frames.Length == 0)
            return string.IsNullOrEmpty(ex.StackTrace) ? "      (keine Stacktrace)\n" : ex.StackTrace + "\n";

        var sb = new StringBuilder();
        int resolved = 0;
        foreach (var frame in frames)
        {
            string file = frame.GetFileName() ?? "";
            int line = frame.GetFileLineNumber();
            int col = frame.GetFileColumnNumber();
            if (line > 0 && file.Length > 0) resolved++;

            string loc = line > 0
                ? (file.Length > 0 ? file : "(Datei unbekannt)") + ":Zeile " + line + (col > 0 ? ",Spalte " + col : "")
                : "Zeile unbekannt (keine PDB-Informationen)";
            sb.AppendLine("   bei " + FormatMethod(frame.GetMethod()) + "  ->  " + loc);
        }
        sb.AppendLine($"   [Zeileninfo: {resolved}/{frames.Length} Frames aufgelöst]");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Systeminfos

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows       : " + Safe(WindowsDescription));
        sb.AppendLine("Architektur   : " + RuntimeInformation.OSArchitecture + " (OS) / " +
                      RuntimeInformation.ProcessArchitecture + " (Prozess), 64-Bit=" + Environment.Is64BitProcess);
        sb.AppendLine("CPU           : " + Safe(GetCpu) + " / " + Environment.ProcessorCount + " logische Kerne");
        sb.AppendLine("RAM           : " + Safe(GetMemory));
        sb.AppendLine("GPU           : " + Safe(GetGpu) + " | Renderer-Kontext: " + ContextValue("renderer"));
        sb.AppendLine("Bildschirm    : " + Safe(GetDisplay));
        sb.AppendLine("Laufwerk      : " + Safe(GetDrive));
        sb.AppendLine("Prozess       : " + Safe(GetProcess));
        sb.AppendLine(".NET          : " + RuntimeInformation.FrameworkDescription + " / CLR " + Environment.Version);
        sb.AppendLine("Globalization : " + Safe(GlobalizationInfo));
        sb.AppendLine("Zeitzone      : " + Safe(() => TimeZoneInfo.Local.DisplayName));
        sb.AppendLine("Benutzer      : " + Environment.UserName + " @" + Environment.MachineName);
        return sb.ToString();
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "(nicht verfügbar: " + e.GetType().Name + ")"; }
    }

    private static string ContextValue(string key)
    {
        lock (_gate) return _context.TryGetValue(key, out var v) ? v : "-";
    }

    private static string WindowsDescription()
    {
        if (!OperatingSystem.IsWindows()) return Environment.OSVersion.VersionString;
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = k?.GetValue("ProductName") as string ?? Environment.OSVersion.VersionString;
        string edition = k?.GetValue("EditionID") as string ?? "";
        string display = k?.GetValue("DisplayVersion") as string ?? "";
        string build = k?.GetValue("CurrentBuild") as string ?? "";
        object? ubr = k?.GetValue("UBR");
        if (build.Length > 0 && ubr != null) build += "." + ubr;
        return $"{product} {edition} {display} (Build {build}) – {Environment.OSVersion.VersionString}";
    }

    private static string GetCpu()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.ProcessArchitecture.ToString();
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return k?.GetValue("ProcessorNameString") as string ?? "unbekannt";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx stat);

    private static string GetMemory()
    {
        if (!OperatingSystem.IsWindows()) return "unbekannt";
        var stat = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref stat)) return "unbekannt";
        const double gb = 1024.0 * 1024 * 1024;
        return $"{stat.ullTotalPhys / gb:F1} GB physisch, {stat.ullAvailPhys / gb:F1} GB frei, Auslastung {stat.dwMemoryLoad}%";
    }

    private static string GetGpu()
    {
        if (!OperatingSystem.IsWindows()) return "unbekannt";
        var names = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:0000}");
            string? desc = k?.GetValue("DriverDesc") as string;
            if (!string.IsNullOrWhiteSpace(desc) && !names.Contains(desc)) names.Add(desc);
        }
        return names.Count > 0 ? string.Join(" | ", names) : "unbekannt";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static string GetDisplay()
    {
        if (!OperatingSystem.IsWindows()) return "unbekannt";
        return GetSystemMetrics(0) + "x" + GetSystemMetrics(1);
    }

    private static string GetDrive()
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        string? root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root)) return "unbekannt";
        var d = new DriveInfo(root);
        if (!d.IsReady) return d.Name + " (nicht bereit)";
        const double gb = 1024.0 * 1024 * 1024;
        return $"{d.Name} {d.TotalSize / gb:F1} GB ({d.AvailableFreeSpace / gb:F1} GB frei) – exe: {dir}";
    }

    private static string GetProcess()
    {
        using var p = Process.GetCurrentProcess();
        var up = DateTime.Now - p.StartTime;
        return $"PID {p.Id}, Start {p.StartTime:yyyy-MM-dd HH:mm:ss}, Laufzeit {up:hh\\:mm\\:ss}, " +
               $"WorkingSet {p.WorkingSet64 / (1024.0 * 1024):F0} MB, Threads {p.Threads.Count}";
    }

    private static string GlobalizationInfo()
    {
        string invariant;
        try { _ = CultureInfo.GetCultureInfo("de-DE"); invariant = "nein"; }
        catch (CultureNotFoundException) { invariant = "JA"; }
        catch { invariant = "?"; }
        return $"invariant={invariant}, Culture={CultureInfo.CurrentCulture.Name}, UI={CultureInfo.CurrentUICulture.Name}";
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
