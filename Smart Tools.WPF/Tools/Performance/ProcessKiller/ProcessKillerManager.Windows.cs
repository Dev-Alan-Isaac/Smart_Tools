using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Windows half of the Process Killer: kernel32/psapi/user32 for the process list,
    /// the shared TcpUdpTable for ports, Restart Manager for locked files, WMI for one command line.
    /// Every process is opened with the lowest right (query limited), which works for
    /// almost everything without administrator rights.
    /// </summary>
    public sealed partial class ProcessKillerManager
    {
        /// <summary>What a reading keeps for the next one (CPU is a difference) plus per-process facts that never change.</summary>
        private sealed class ReadState
        {
            public Dictionary<(int, long), long> CpuTime = new();
            public long WallTimestamp;
            public (ulong Idle, ulong Kernel, ulong User)? SystemTimes;
            public Dictionary<(int, long), StaticInfo> Static = new();
            public readonly Dictionary<string, string> AccountNames = new(StringComparer.Ordinal);
        }

        private sealed record StaticInfo(string? Path, string? OwnerSid, string? Owner, bool IsCritical, int SessionId);

        private ProcessKillerSnapshot ReadSnapshot(ReadState state)
        {
            var entries = ReadToolhelp();
            var windows = ReadWindowTitles();

            long now = Stopwatch.GetTimestamp();
            double wall100ns = state.WallTimestamp == 0 ? 0 : (now - state.WallTimestamp) * 10_000_000.0 / Stopwatch.Frequency;
            int cores = Math.Max(1, Environment.ProcessorCount);

            var cpuNow = new Dictionary<(int, long), long>(entries.Count);
            var staticNow = new Dictionary<(int, long), StaticInfo>(entries.Count);
            var samples = new List<ProcessKillerSample>(entries.Count);

            foreach (var e in entries)
            {
                if (e.Pid == 0) continue;   // System Idle Process: not a real process

                long creation = 0, cpu = -1, memory = -1;
                StaticInfo? info = null;

                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
                try
                {
                    if (h != IntPtr.Zero)
                    {
                        if (GetProcessTimes(h, out long c, out _, out long k, out long u))
                        {
                            creation = c;
                            cpu = k + u;
                        }

                        var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
                        if (K32GetProcessMemoryInfo(h, ref counters, counters.cb)) memory = (long)(ulong)counters.WorkingSetSize;

                        if (!state.Static.TryGetValue((e.Pid, creation), out info))
                            info = ReadStaticInfo(h, e.Pid, state);
                    }
                }
                finally
                {
                    if (h != IntPtr.Zero) CloseHandle(h);
                }

                info ??= state.Static.TryGetValue((e.Pid, creation), out var cached)
                    ? cached
                    : new StaticInfo(null, null, null, false, SessionOf(e.Pid));
                staticNow[(e.Pid, creation)] = info;

                double cpuPercent = 0;
                if (cpu >= 0)
                {
                    cpuNow[(e.Pid, creation)] = cpu;
                    if (wall100ns > 0 && state.CpuTime.TryGetValue((e.Pid, creation), out long before))
                        cpuPercent = Math.Clamp((cpu - before) / (wall100ns * cores) * 100.0, 0, 100);
                }

                windows.TryGetValue(e.Pid, out string? title);
                bool critical = info.IsCritical || IsCoreWindowsProcess(e.Name, info.Path);
                samples.Add(new ProcessKillerSample(
                    e.Pid, e.ParentPid, creation, e.Name, info.Path, info.Owner, title, e.Threads,
                    info.SessionId, cpuPercent, memory, critical, Classify(info, title)));
            }

            state.CpuTime = cpuNow;
            state.Static = staticNow;   // drops processes that have gone
            state.WallTimestamp = now;

            double totalCpu = 0;
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                var t = ((ulong)idle, (ulong)kernel, (ulong)user);
                if (state.SystemTimes is { } p)
                {
                    double total = (t.Item2 - p.Kernel) + (t.Item3 - p.User);   // kernel time includes idle
                    if (total > 0) totalCpu = Math.Clamp((total - (t.Item1 - p.Idle)) / total * 100.0, 0, 100);
                }
                state.SystemTimes = t;
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            long memTotal = 0, memUsed = 0;
            if (GlobalMemoryStatusEx(ref mem))
            {
                memTotal = (long)mem.ullTotalPhys;
                memUsed = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
            }

            return new ProcessKillerSnapshot(samples, totalCpu, memUsed, memTotal);
        }

        private static StaticInfo ReadStaticInfo(IntPtr h, int pid, ReadState state)
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString(0, size);

            bool critical = IsProcessCritical(h, out bool isCritical) && isCritical;

            string? sid = null, owner = null;
            if (OpenProcessToken(h, TOKEN_QUERY, out IntPtr token))
            {
                try
                {
                    GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
                    if (needed > 0)
                    {
                        IntPtr buffer = Marshal.AllocHGlobal(needed);
                        try
                        {
                            if (GetTokenInformation(token, TokenUser, buffer, needed, out _))
                            {
                                var identifier = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));   // TOKEN_USER.User.Sid
                                sid = identifier.Value;
                                owner = AccountName(identifier, state);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }

            return new StaticInfo(path, sid, owner, critical, SessionOf(pid));
        }

        /// <summary>"DOMAIN\user" → "user" (cached: the lookup can be slow on a domain).</summary>
        private static string AccountName(SecurityIdentifier sid, ReadState state)
        {
            if (state.AccountNames.TryGetValue(sid.Value, out var name)) return name;
            try
            {
                string full = sid.Translate(typeof(NTAccount)).Value;
                int slash = full.LastIndexOf('\\');
                name = slash >= 0 ? full[(slash + 1)..] : full;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                name = sid.Value;
            }
            state.AccountNames[sid.Value] = name;
            return name;
        }

        private static ProcessKillerKind Classify(StaticInfo info, string? windowTitle)
        {
            string? sid = info.OwnerSid;
            bool serviceAccount = sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
                || (sid != null && (sid.StartsWith("S-1-5-90-", StringComparison.Ordinal) || sid.StartsWith("S-1-5-96-", StringComparison.Ordinal)));

            // Our own processes can always be opened, so an owner we can't read means Windows (or another user).
            if (serviceAccount || sid == null) return ProcessKillerKind.Windows;
            return string.IsNullOrEmpty(windowTitle) ? ProcessKillerKind.Background : ProcessKillerKind.App;
        }

        private static readonly HashSet<string> CoreWindowsProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "csrss.exe", "smss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
            "System", "Registry", "Secure System", "Memory Compression", "LsaIso.exe",
        };

        private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        /// <summary>
        /// Without administrator rights Windows won't let us open SYSTEM processes to ask
        /// IsProcessCritical, so their core processes are also recognised by name - but only
        /// from System32 (or with no readable path), so a look-alike elsewhere isn't protected.
        /// </summary>
        private static bool IsCoreWindowsProcess(string name, string? path) =>
            CoreWindowsProcesses.Contains(name)
            && (path == null || path.StartsWith(System32 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        private static int SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out uint session) ? (int)session : -1;

        private readonly record struct ToolhelpEntry(int Pid, int ParentPid, int Threads, string Name);

        private static List<ToolhelpEntry> ReadToolhelp()
        {
            var list = new List<ToolhelpEntry>(400);
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return list;
                do
                {
                    list.Add(new ToolhelpEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, (int)entry.cntThreads, entry.szExeFile));
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                }
                while (Process32Next(snap, ref entry));
            }
            finally
            {
                CloseHandle(snap);
            }
            return list;
        }

        /// <summary>PID → title of its first visible, unowned, not-cloaked top-level window (what the taskbar shows).</summary>
        private static Dictionary<int, string> ReadWindowTitles()
        {
            var titles = new Dictionary<int, string>();
            var sb = new StringBuilder(256);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                int length = GetWindowTextLength(hwnd);
                if (length == 0) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (titles.ContainsKey((int)pid)) return true;

                sb.Clear();
                sb.EnsureCapacity(length + 1);
                if (GetWindowText(hwnd, sb, sb.Capacity) > 0) titles[(int)pid] = sb.ToString();
                return true;
            }, IntPtr.Zero);
            return titles;
        }

        // =================================================================
        //  Ending one process
        // =================================================================

        /// <summary>
        /// Ends one process after checking it's still the same one (a PID can be reused the moment
        /// a process exits) and not critical to Windows (ending one of those crashes the PC).
        /// </summary>
        private static ProcessKillerEndResult EndOne(int pid, long creationTicks, string name)
        {
            // A row whose start time could not be read has no identity to check: the number may belong to a different program by now.
            // Such a process could not be opened for ending anyway (that is why its time is missing), so it is refused rather than guessed at.
            if (creationTicks == 0) return new(pid, name, ProcessKillerEndOutcome.AccessDenied, null);

            IntPtr h = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
            if (h == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return error switch
                {
                    ERROR_ACCESS_DENIED => new(pid, name, ProcessKillerEndOutcome.AccessDenied, null),
                    ERROR_INVALID_PARAMETER => new(pid, name, ProcessKillerEndOutcome.AlreadyExited, null),
                    _ => new(pid, name, ProcessKillerEndOutcome.Failed, new Win32Exception(error).Message),
                };
            }

            try
            {
                if (GetProcessTimes(h, out long created, out _, out _, out _) && creationTicks != 0 && created != creationTicks)
                    return new(pid, name, ProcessKillerEndOutcome.AlreadyExited, null);

                if (IsProcessCritical(h, out bool critical) && critical)
                    return new(pid, name, ProcessKillerEndOutcome.Blocked, nameof(ProcessKillerBlock.Critical));

                if (!TerminateProcess(h, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ERROR_ACCESS_DENIED)
                    {
                        // Already on its way out also reports access denied.
                        return WaitForSingleObject(h, 0) == WAIT_OBJECT_0
                            ? new(pid, name, ProcessKillerEndOutcome.AlreadyExited, null)
                            : new(pid, name, ProcessKillerEndOutcome.AccessDenied, null);
                    }
                    return new(pid, name, ProcessKillerEndOutcome.Failed, new Win32Exception(error).Message);
                }

                WaitForSingleObject(h, 3000);
                return new(pid, name, ProcessKillerEndOutcome.Ended, null);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        // =================================================================
        //  Ports, locked files, elevation, command line
        // =================================================================

        /// <summary>Every TCP and UDP endpoint bound to <paramref name="port"/> locally, with its process.</summary>
        public static Task<List<EndpointUse>> FindPortUsesAsync(int port) =>
            Task.Run(() => TcpUdpTable.ReadAll()
                .Where(u => u.LocalPort == port)
                .OrderBy(u => u.State == "Listen" ? 0 : 1)
                .ThenBy(u => u.Protocol, StringComparer.Ordinal)
                .ToList());

        /// <summary>Processes holding <paramref name="path"/> open, from Windows' Restart Manager.</summary>
        public static Task<List<ProcessKillerLocker>> FindLockersAsync(string path) =>
            Task.Run(() =>
            {
                var result = new List<ProcessKillerLocker>();
                var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
                int rc = RmStartSession(out uint session, 0, key);
                if (rc != 0) throw new Win32Exception(rc);
                try
                {
                    rc = RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null);
                    if (rc != 0) throw new Win32Exception(rc);

                    uint needed = 0, count = 0, reasons = 0;
                    RM_PROCESS_INFO[]? infos = null;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                        if (rc == 0) break;
                        if (rc != ERROR_MORE_DATA) throw new Win32Exception(rc);
                        infos = new RM_PROCESS_INFO[needed];
                        count = needed;
                    }

                    if (infos != null)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var p = infos[i].Process;
                            long created = ((long)(uint)p.ProcessStartTime.dwHighDateTime << 32) | (uint)p.ProcessStartTime.dwLowDateTime;
                            result.Add(new ProcessKillerLocker(p.dwProcessId, created, infos[i].strAppName));
                        }
                    }
                }
                finally
                {
                    RmEndSession(session);
                }
                return result;
            });

        public static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (SystemException)
            {
                return false;
            }
        }

        /// <summary>The command line (WMI, on demand for one process); null when it can't be read.</summary>
        public static Task<string?> GetCommandLineAsync(int pid) =>
            Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                    using var results = searcher.Get();
                    foreach (ManagementObject mo in results)
                    {
                        using (mo) return mo["CommandLine"] as string;
                    }
                }
                catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
                {
                }
                return (string?)null;
            });

        // =================================================================
        //  P/Invoke
        // =================================================================

        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenUser = 1;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
        private const uint GW_OWNER = 4;
        private const int DWMWA_CLOAKED = 14;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int ERROR_MORE_DATA = 234;
        private const uint WAIT_OBJECT_0 = 0;
        private const int CCH_RM_SESSION_KEY = 32;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
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

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS counters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessCritical(IntPtr process, out bool critical);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint pid, out uint session);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, IntPtr apps, uint serviceCount, string[]? services);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? infos, ref uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);
    }
}
