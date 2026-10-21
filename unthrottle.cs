// unthrottle -- lifts Windows' background power throttling from busy processes.
//
// On Windows 10 with a hybrid (P/E-core) CPU, a process whose console/window is in the
// background can be throttled onto the E-cores. Clearing its per-process EXECUTION_SPEED
// throttling flag lets it use all cores again; README.md has the measurements.
//
// Every probe_ms it samples CPU time of processes named in unthrottle.ini. A process
// that averages >= unthrottle_cores for unthrottle_probes consecutive probes is opted out of
// throttling; once it stays below reset_cores for reset_seconds it is handed back to
// Windows. Processes of other users are skipped (no access without admin).
//
// unthrottle.ini (next to the exe) is required and must set every key; there are no
// built-in defaults. Startup problems are shown in a message box and logged.
//
// Build: build.bat (C# 5 / .NET Framework 4 csc that ships with Windows).
// Run:   unthrottle.exe        (no window; one instance per user)
// Stop:  unthrottle.exe stop   (resets every unthrottled process first)
// Exit codes: 0 ok, 1 stop: not running, 2 already running, 3 bad config (including
// other_users=true without elevation), 4 crash.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Config
{
    public HashSet<string> Processes;
    public int ProbeMs;
    public double UnthrottleCores;
    public int UnthrottleProbes;
    public double ResetCores;
    public double ResetSeconds;
    public long LogMaxBytes;
    public bool OtherUsers;

    static readonly string[] Keys = { "processes", "probe_ms", "unthrottle_cores", "unthrottle_probes", "reset_cores", "reset_seconds", "log_max_kb", "other_users" };

    // Parses the whole file or throws InvalidDataException naming the problem; never returns a partial config.
    public static Config Load(string path)
    {
        var values = new Dictionary<string, string>();
        var errors = new List<string>();
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) { errors.Add(string.Format("line {0}: expected key=value, got '{1}'", i + 1, line)); continue; }
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            if (!Keys.Contains(key)) errors.Add(string.Format("line {0}: unknown key '{1}'", i + 1, key));
            else if (values.ContainsKey(key)) errors.Add(string.Format("line {0}: '{1}' is set twice", i + 1, key));
            else values[key] = line.Substring(eq + 1).Trim();
        }
        foreach (string k in Keys) if (!values.ContainsKey(k)) errors.Add("missing key '" + k + "'");

        // keep going after structural errors so a single message lists every problem
        var c = new Config();
        c.Processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.ContainsKey("processes"))
        {
            foreach (string n in values["processes"].Split(',')) if (n.Trim().Length > 0) c.Processes.Add(n.Trim());
            if (c.Processes.Count == 0) errors.Add("processes: no exe names given");
        }
        c.ProbeMs = (int)Number(values, "probe_ms", 200, int.MaxValue, true, errors);
        c.UnthrottleCores = Number(values, "unthrottle_cores", 0, 1024, false, errors);
        c.UnthrottleProbes = (int)Number(values, "unthrottle_probes", 1, int.MaxValue, true, errors);
        c.ResetCores = Number(values, "reset_cores", 0, 1024, false, errors);
        c.ResetSeconds = Number(values, "reset_seconds", 0, int.MaxValue, false, errors);
        c.LogMaxBytes = (long)Number(values, "log_max_kb", 1, int.MaxValue, true, errors) * 1024;
        c.OtherUsers = Flag(values, "other_users", errors);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        return c;
    }

    static bool Flag(Dictionary<string, string> values, string key, List<string> errors)
    {
        string s;
        if (!values.TryGetValue(key, out s)) return false;   // already reported as missing
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (!s.Equals("false", StringComparison.OrdinalIgnoreCase)) errors.Add(string.Format("{0}: '{1}' is not true or false", key, s));
        return false;
    }

    static double Number(Dictionary<string, string> values, string key, double min, double max, bool integer, List<string> errors)
    {
        string s;
        if (!values.TryGetValue(key, out s)) return 0;   // already reported as missing
        double v;
        long whole;
        bool ok;
        if (integer) { ok = long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole); v = whole; }
        else ok = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v);
        if (!ok) { errors.Add(string.Format("{0}: '{1}' is not {2}", key, s, integer ? "a whole number" : "a number")); return 0; }
        if (v < min || v > max)
        {
            string range = max >= int.MaxValue ? "at least " + min : min + " to " + max;
            errors.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1} is out of range (must be {2})", key, s, range));
            return 0;
        }
        return v;
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "processes={0} probe_ms={1} unthrottle: {2} probes >= {3} cores, reset: {4}s < {5} cores, other_users={6}",
            string.Join(",", Processes), ProbeMs, UnthrottleProbes, UnthrottleCores, ResetSeconds, ResetCores, OtherUsers ? "on" : "off");
    }
}

static class Unthrottle
{
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string IniPath = Path.Combine(Dir, "unthrottle.ini");
    static readonly string LogPath = Path.Combine(Dir, "unthrottle.log");
    // Local\ objects are per logon session, not per user, so the user name keeps two accounts apart
    static readonly string User = Environment.UserName;
    static readonly string StopEventName = @"Local\unthrottle-stop-" + User;
    static readonly string MutexName = @"Local\unthrottle-" + User;

    static Config cfg;
    static DateTime iniStamp;
    static bool iniMissingLogged;
    static long logMaxBytes = 1024 * 1024;   // only until the ini has been read, so startup errors can be logged

    class Tracked
    {
        public int Pid;
        public string Name;
        public DateTime Start;      // guards against PID reuse
        public double LastCpuSec;
        public DateTime LastAt;
        public int HighProbes;
        public bool Unthrottled;
        public DateTime UnthrottledAt;
        public DateTime BusySince;  // last probe at or above reset_cores while unthrottled
        public double PeakCores;
        public bool GaveUp;         // a failed unthrottle is not retried for this process
    }

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            Log("CRASH", e.ToString());
            Box("unthrottle crashed:\n\n" + e.Message + "\n\nDetails: " + LogPath, true);
            return 4;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(StopEventName, out ev))
            {
                Box("unthrottle is not running for user " + User + ".", false);
                return 1;
            }
            ev.Set();
            return 0;
        }

        // Config is checked before taking the single-instance mutex, so an error box never holds it.
        if (!File.Exists(IniPath)) return ConfigFailure("unthrottle.ini not found.\n\nExpected at: " + IniPath);
        try { Apply(Config.Load(IniPath)); }
        catch (InvalidDataException e) { return ConfigFailure("unthrottle.ini has errors:\n\n" + e.Message + "\n\nFile: " + IniPath); }
        if (cfg.OtherUsers)
        {
            string error = EnableDebugPrivilege();
            if (error != null)
                return ConfigFailure("other_users=true needs unthrottle to run elevated (as administrator).\n\n" + error + "\n\nFile: " + IniPath);
        }

        bool firstInstance;
        var mutex = new Mutex(true, MutexName, out firstInstance);
        if (!firstInstance)
        {
            mutex.Dispose();
            string pids = OtherInstancePids();
            Box("unthrottle is already running for user " + User + (pids.Length > 0 ? " (pid " + pids + ")" : "") +
                ".\n\nTo stop it: unthrottle.exe stop", false);
            return 2;
        }
        try { return Watch(); }
        finally { mutex.ReleaseMutex(); mutex.Dispose(); }   // released before Main's crash box can show
    }

    static int Watch()
    {
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        Log("START", "pid=" + Process.GetCurrentProcess().Id + " user=" + User + " " + cfg);
        var tracked = new Dictionary<int, Tracked>();
        while (!stop.WaitOne(cfg.ProbeMs))
        {
            try
            {
                ReloadIfChanged();
                Probe(tracked);
            }
            catch (Exception e) { Log("ERROR", e.GetType().Name + ": " + e.Message); }
        }

        foreach (var t in tracked.Values)
            if (t.Unthrottled)
                LogResult("RESET", SetThrottling(t.Pid, false), t, string.Format(CultureInfo.InvariantCulture,
                    "unthrottle stopping, back to Windows' default throttling (unthrottled {0:F0}s)", (DateTime.UtcNow - t.UnthrottledAt).TotalSeconds));
        Log("STOP", "");
        return 0;
    }

    static int ConfigFailure(string message)
    {
        Log("ERROR", message.Replace("\n\n", " ").Replace("\n", "; "));
        Box(message, true);
        return 3;
    }

    static void Apply(Config c)
    {
        cfg = c;
        logMaxBytes = c.LogMaxBytes;
        iniStamp = File.GetLastWriteTimeUtc(IniPath);
    }

    // A bad edit keeps the previous settings; a deleted ini is reported once and also keeps them.
    // other_users is read at startup only: the privilege it needs is acquired once, before the mutex.
    static void ReloadIfChanged()
    {
        if (!File.Exists(IniPath))
        {
            if (!iniMissingLogged) Log("WARN", "unthrottle.ini is missing; keeping current settings");
            iniMissingLogged = true;
            return;
        }
        iniMissingLogged = false;
        DateTime stamp = File.GetLastWriteTimeUtc(IniPath);
        if (stamp == iniStamp) return;
        try
        {
            Config c = Config.Load(IniPath);
            if (c.OtherUsers != cfg.OtherUsers)
            {
                Log("WARN", "other_users change takes effect after a restart; keeping other_users=" + (cfg.OtherUsers ? "true" : "false"));
                c.OtherUsers = cfg.OtherUsers;
            }
            Apply(c);
            Log("CONFIG", "reloaded " + cfg);
        }
        catch (Exception e)   // InvalidDataException, or the editor still holding the file
        {
            iniStamp = stamp;   // report each bad save once, not on every probe
            Log("ERROR", "config not reloaded, keeping previous settings: " + e.Message.Replace("\n", "; "));
        }
    }

    static void Probe(Dictionary<int, Tracked> tracked)
    {
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName + ".exe";
                if (!cfg.Processes.Contains(name)) continue;
                double cpuSec; DateTime start;
                try { cpuSec = p.TotalProcessorTime.TotalSeconds; start = p.StartTime; }
                catch { continue; }   // no query access (other users' processes usually still allow it), or it just exited
                DateTime now = DateTime.UtcNow;
                seen.Add(p.Id);

                Tracked t;
                if (!tracked.TryGetValue(p.Id, out t) || t.Start != start)
                {
                    tracked[p.Id] = new Tracked { Pid = p.Id, Name = name, Start = start, LastCpuSec = cpuSec, LastAt = now };
                    continue;
                }
                double dt = (now - t.LastAt).TotalSeconds;
                if (dt <= 0) continue;
                double cores = (cpuSec - t.LastCpuSec) / dt;
                t.LastCpuSec = cpuSec;
                t.LastAt = now;

                if (!t.Unthrottled)
                {
                    if (t.GaveUp) continue;
                    t.HighProbes = cores >= cfg.UnthrottleCores ? t.HighProbes + 1 : 0;
                    if (t.HighProbes < cfg.UnthrottleProbes) continue;
                    t.HighProbes = 0;
                    string result = SetThrottling(p.Id, true);
                    if (result != "ok")
                    {
                        // Reading another user's (or an elevated) process's CPU time is allowed, changing it is not.
                        t.GaveUp = true;
                        if (result == AccessDenied)
                            Log("SKIP", name + " pid=" + p.Id + ": no access (another user's or an elevated process), leaving it alone");
                        else
                            LogResult("UNTHROTTLE", result + "; not retrying", t, null);
                        continue;
                    }
                    t.Unthrottled = true;
                    t.UnthrottledAt = t.BusySince = now;
                    t.PeakCores = cores;
                    LogResult("UNTHROTTLE", result, t, string.Format(CultureInfo.InvariantCulture,
                        "busy ({0:F1} cores), exempt from background throttling. cmd: {1}", cores, CommandLine(p.Id)));
                }
                else
                {
                    if (cores > t.PeakCores) t.PeakCores = cores;
                    if (cores >= cfg.ResetCores) { t.BusySince = now; continue; }
                    if ((now - t.BusySince).TotalSeconds < cfg.ResetSeconds) continue;
                    string result = SetThrottling(p.Id, false);
                    LogResult("RESET", result, t, string.Format(CultureInfo.InvariantCulture,
                        "idle for {0:F0}s, back to Windows' default throttling (unthrottled {1:F0}s, peak {2:F1} cores)",
                        (now - t.BusySince).TotalSeconds, (now - t.UnthrottledAt).TotalSeconds, t.PeakCores));
                    t.Unthrottled = false;
                }
            }
            finally { p.Dispose(); }
        }

        var gone = new List<int>();
        foreach (var t in tracked.Values)
        {
            if (seen.Contains(t.Pid)) continue;
            gone.Add(t.Pid);
            if (t.Unthrottled)
                LogResult("EXIT", "ok", t, string.Format(CultureInfo.InvariantCulture,
                    "exited while unthrottled (unthrottled {0:F0}s, peak {1:F1} cores)", (DateTime.UtcNow - t.UnthrottledAt).TotalSeconds, t.PeakCores));
        }
        foreach (int pid in gone) tracked.Remove(pid);
    }

    // --- Win32: per-process power throttling (EXECUTION_SPEED). Windows 10 accepts the set
    //     but cannot read the state back, so the result here only says whether the call succeeded.
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    const int ProcessPowerThrottling = 4;
    const uint THROTTLING_EXECUTION_SPEED = 1;
    const uint PROCESS_SET_INFORMATION = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    // --- Win32: SeDebugPrivilege, which lets an elevated administrator open other users' processes
    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }   // exactly one privilege

    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;
    const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    // Returns null once the privilege is enabled, else why not. AdjustTokenPrivileges "succeeds" even when
    // the token lacks the privilege (non-elevated), so ERROR_NOT_ALL_ASSIGNED has to be checked separately.
    static string EnableDebugPrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
            return "OpenProcessToken error " + Marshal.GetLastWin32Error();
        try
        {
            LUID luid;
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                return "LookupPrivilegeValue error " + Marshal.GetLastWin32Error();
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return "AdjustTokenPrivileges error " + Marshal.GetLastWin32Error();
            return Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED
                ? "Windows did not grant SeDebugPrivilege: this process is not running elevated." : null;
        }
        finally { CloseHandle(token); }
    }

    const string AccessDenied = "access denied";
    const int ERROR_ACCESS_DENIED = 5;

    // optOut=true: explicitly not throttled; optOut=false: drop the override, Windows decides again.
    // Returns "ok", AccessDenied, or a description of the failure.
    static string SetThrottling(int pid, bool optOut)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            return error == ERROR_ACCESS_DENIED ? AccessDenied : "OpenProcess error " + error;
        }
        try
        {
            var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = optOut ? THROTTLING_EXECUTION_SPEED : 0, StateMask = 0 };
            return SetProcessInformation(h, ProcessPowerThrottling, ref s, (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)))
                ? "ok" : "SetProcessInformation error " + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    static void Box(string text, bool error)
    {
        const uint MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        MessageBox(IntPtr.Zero, text, "unthrottle", (error ? MB_ICONERROR : MB_ICONINFORMATION) | MB_SETFOREGROUND | MB_TOPMOST);
    }

    // only called on UNTHROTTLE, so the WMI cost does not matter
    static string CommandLine(int pid)
    {
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                foreach (ManagementObject o in q.Get())
                {
                    string cmd = (o["CommandLine"] as string) ?? "";
                    return cmd.Length <= 220 ? cmd : cmd.Substring(0, 60) + " ... " + cmd.Substring(cmd.Length - 155);
                }
        }
        catch (Exception e) { return "<" + e.Message + ">"; }
        return "<gone>";
    }

    // PIDs of this user's other unthrottle processes, for the "already running" message; "" if unknown
    static string OtherInstancePids()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            var pids = new List<string>();
            using (var q = new ManagementObjectSearcher("SELECT Handle, ProcessId FROM Win32_Process WHERE Name='" + me.ProcessName + ".exe'"))
                foreach (ManagementObject o in q.Get())
                {
                    int pid = Convert.ToInt32(o["ProcessId"]);
                    if (pid == me.Id) continue;
                    var owner = new object[2];
                    if (Convert.ToInt32(o.InvokeMethod("GetOwner", owner)) == 0 && User.Equals(owner[0] as string, StringComparison.OrdinalIgnoreCase))
                        pids.Add(pid.ToString());
                }
            return string.Join(", ", pids);
        }
        catch { return ""; }
    }

    // Per-process event line; a failed Win32 call turns the tag into "<TAG> FAILED" and says why.
    static void LogResult(string tag, string result, Tracked t, string text)
    {
        if (result == "ok") Log(tag, t.Name + " pid=" + t.Pid + ": " + text);
        else Log(tag + " FAILED", t.Name + " pid=" + t.Pid + ": " + result);
    }

    // Tags are padded so the messages line up when skimming the log.
    static void Log(string tag, string text)
    {
        string msg = (tag.PadRight(10) + " " + text).TrimEnd();
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > logMaxBytes)
            {
                string old = LogPath + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(LogPath, old);
            }
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg + Environment.NewLine);
        }
        catch { }   // logging must never take the watcher down
    }
}
