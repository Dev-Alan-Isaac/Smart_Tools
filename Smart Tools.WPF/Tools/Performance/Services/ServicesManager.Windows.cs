using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows half of <see cref="ServicesManager"/>: WMI for reading and controlling services, sc.exe for the startup type.</summary>
    public sealed partial class ServicesManager
    {
        private const string Scope = @"\\.\root\cimv2";
        private const string Columns = "Name, DisplayName, Description, State, StartMode, DelayedAutoStart, PathName, StartName, AcceptStop, AcceptPause";
        private const uint CodeAccessDenied = 2;
        private const uint CodeFailed = 0xFFFF;

        private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

        private (List<WinService> Services, Dictionary<string, List<string>> Dependents) ReadAll()
        {
            var services = new List<WinService>();
            using (var searcher = new ManagementObjectSearcher(Scope, $"SELECT {Columns} FROM Win32_Service"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item) services.Add(FromWmi(item));
                }
            }

            var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(Scope, "SELECT Antecedent, Dependent FROM Win32_DependentService");
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        string needed = ReferenceName(item["Antecedent"] as string ?? "");
                        string dependent = ReferenceName(item["Dependent"] as string ?? "");
                        if (needed.Length == 0 || dependent.Length == 0) continue;
                        if (!dependents.TryGetValue(needed, out var list)) dependents[needed] = list = new List<string>();
                        list.Add(dependent);
                    }
                }
            }
            catch (ManagementException)
            {
                // Without the links every service still lists and works; only the "stop dependents too" help is lost.
            }
            return (services, dependents);
        }

        /// <summary>One fresh reading of a service, or null when it no longer exists.</summary>
        private WinService? ReadOne(string name)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(Scope, $"SELECT {Columns} FROM Win32_Service WHERE Name = '{Escape(name)}'");
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    using (item) return FromWmi(item);
                }
            }
            catch (ManagementException)
            {
            }
            return null;
        }

        private static WinService FromWmi(ManagementBaseObject item)
        {
            string name = item["Name"] as string ?? "";
            bool delayed = item["DelayedAutoStart"] is true;
            var mode = (item["StartMode"] as string) switch
            {
                "Manual" => WinServiceStartMode.Manual,
                "Disabled" => WinServiceStartMode.Disabled,
                "Auto" when delayed => WinServiceStartMode.AutomaticDelayed,
                _ => WinServiceStartMode.Automatic,
            };
            return new WinService
            {
                Name = name,
                DisplayName = item["DisplayName"] as string ?? name,
                Description = (item["Description"] as string)?.Trim() ?? "",
                BinaryPath = item["PathName"] as string ?? "",
                Account = item["StartName"] as string ?? "",
                IsCore = IsCoreService(name),
                State = ParseState(item["State"] as string),
                StartMode = mode,
                AcceptsStop = item["AcceptStop"] is true,
                AcceptsPause = item["AcceptPause"] is true,
            };
        }

        // =================================================================
        //  Control requests
        // =================================================================

        private WinServiceChangeResult StartOne(string name)
        {
            if (ReadOne(name)?.State == WinServiceState.Running) return WinServiceChangeResult.Done;
            uint code = InvokeCode(name, "StartService");
            if (FromCode(code) != WinServiceChangeResult.Done) return FromCode(code);
            return WaitWhilePending(name) == WinServiceState.Running ? WinServiceChangeResult.Done : WinServiceChangeResult.Failed;
        }

        private WinServiceChangeResult StopOne(string name)
        {
            var current = ReadOne(name);
            if (current == null || current.State == WinServiceState.Stopped) return WinServiceChangeResult.Done;
            uint code = InvokeCode(name, "StopService");
            if (code != 5 && FromCode(code) != WinServiceChangeResult.Done) return FromCode(code);   // 5 = it was already stopping
            if (WaitWhilePending(name) == WinServiceState.Stopped) return WinServiceChangeResult.Done;
            return code == 5 ? WinServiceChangeResult.Failed : WinServiceChangeResult.TimedOut;
        }

        private WinServiceChangeResult PauseOne(string name)
        {
            uint code = InvokeCode(name, "PauseService");
            if (FromCode(code) != WinServiceChangeResult.Done) return FromCode(code);
            return WaitWhilePending(name) == WinServiceState.Paused ? WinServiceChangeResult.Done : WinServiceChangeResult.Failed;
        }

        private WinServiceChangeResult ResumeOne(string name)
        {
            uint code = InvokeCode(name, "ResumeService");
            if (FromCode(code) != WinServiceChangeResult.Done) return FromCode(code);
            return WaitWhilePending(name) == WinServiceState.Running ? WinServiceChangeResult.Done : WinServiceChangeResult.Failed;
        }

        /// <summary>Calls a Win32_Service method and returns its WMI return code (0 = accepted).</summary>
        private static uint InvokeCode(string name, string method)
        {
            try
            {
                using var service = new ManagementObject(Scope, $"Win32_Service.Name='{Escape(name)}'", null);
                return Convert.ToUInt32(service.InvokeMethod(method, null), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                return CodeAccessDenied;
            }
            catch (UnauthorizedAccessException)
            {
                return CodeAccessDenied;
            }
            catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException or FormatException)
            {
                return CodeFailed;
            }
        }

        /// <summary>Polls while the service is still starting, stopping, pausing, or resuming; returns the state it ended in.</summary>
        private WinServiceState WaitWhilePending(string name)
        {
            var end = DateTime.UtcNow + PendingTimeout;
            while (true)
            {
                var now = ReadOne(name);
                if (now == null) return WinServiceState.Stopped;   // removed meanwhile
                if (!IsPending(now.State) || DateTime.UtcNow >= end) return now.State;
                Thread.Sleep(250);
            }
        }

        /// <summary>
        /// Changes the startup type through sc.exe: WMI's ChangeStartMode cannot set "Automatic (delayed)".
        /// Exit code 5 is "access denied".
        /// </summary>
        private static WinServiceChangeResult ConfigureStartMode(string name, WinServiceStartMode mode)
        {
            string value = mode switch
            {
                WinServiceStartMode.Automatic => "auto",
                WinServiceStartMode.AutomaticDelayed => "delayed-auto",
                WinServiceStartMode.Manual => "demand",
                _ => "disabled",
            };
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            info.Arguments = string.Join(" ", new[] { "config", name, "start=", value }.Select(a => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a));

            try
            {
                using var process = Process.Start(info);
                if (process == null) return WinServiceChangeResult.Failed;
                process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(15000)) return WinServiceChangeResult.TimedOut;
                return process.ExitCode switch
                {
                    0 => WinServiceChangeResult.Done,
                    5 => WinServiceChangeResult.NeedsAdministrator,
                    _ => WinServiceChangeResult.Failed,
                };
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return WinServiceChangeResult.Failed;
            }
        }
    }
}
