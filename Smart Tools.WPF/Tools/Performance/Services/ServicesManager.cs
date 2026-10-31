using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Services - lists the Windows services and starts, stops, restarts, pauses, and changes the startup type of each one.
    /// Nothing is stored by this app. Create it on the UI thread: updates to the service objects are sent back to it.
    /// This half is plain logic; the Windows calls (WMI, sc.exe) are in <c>ServicesManager.Windows.cs</c>.
    /// </summary>
    public sealed partial class ServicesManager
    {
        /// <summary>
        /// Services Windows (or the tool itself) relies on. They are not blocked, but stopping or disabling one asks twice.
        /// Winmgmt is what this tool reads services through.
        /// </summary>
        private static readonly HashSet<string> CoreServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "Winmgmt", "EventLog", "BFE", "mpssvc", "WinDefend", "wscsvc", "Dhcp", "Dnscache",
            "Schedule", "CryptSvc", "Appinfo", "gpsvc", "LanmanWorkstation",
        };

        private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(30);

        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

        // Filled by Scan. Name -> the service object the page shows, and service -> services that depend on it.
        private Dictionary<string, WinService> _byName = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsCoreService(string name) => CoreServices.Contains(name);

        public static bool IsPending(WinServiceState state) =>
            state is WinServiceState.StartPending or WinServiceState.StopPending or WinServiceState.ContinuePending or WinServiceState.PausePending;

        public static WinServiceState ParseState(string? text) => text switch
        {
            "Running" => WinServiceState.Running,
            "Stopped" => WinServiceState.Stopped,
            "Start Pending" => WinServiceState.StartPending,
            "Stop Pending" => WinServiceState.StopPending,
            "Continue Pending" => WinServiceState.ContinuePending,
            "Pause Pending" => WinServiceState.PausePending,
            "Paused" => WinServiceState.Paused,
            _ => WinServiceState.Unknown,
        };

        /// <summary>The service name inside a WMI reference such as <c>\\PC\root\cimv2:Win32_Service.Name="RpcSs"</c>, or "".</summary>
        public static string ReferenceName(string reference)
        {
            const string marker = "Name=\"";
            int start = reference.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "";
            start += marker.Length;
            int end = reference.IndexOf('"', start);
            return end < 0 ? "" : reference[start..end].Replace("\\\\", "\\");
        }

        /// <summary>Reads every service. Safe to call off the UI thread.</summary>
        public List<WinService> Scan()
        {
            var (services, dependents) = ReadAll();
            services.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            _byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            _dependents = dependents;
            return services;
        }

        /// <summary>
        /// The services that depend on this one and are not stopped, deepest first - the order they must be stopped in.
        /// Windows refuses to stop a service while one of these is running.
        /// </summary>
        public List<WinService> RunningDependents(WinService service)
        {
            var result = new List<WinService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service.Name };
            Visit(service.Name);
            return result;

            void Visit(string name)
            {
                if (!_dependents.TryGetValue(name, out var kids)) return;
                foreach (string kid in kids)
                {
                    if (!seen.Add(kid) || !_byName.TryGetValue(kid, out var child)) continue;
                    Visit(kid);
                    if (child.State != WinServiceState.Stopped) result.Add(child);
                }
            }
        }

        // =================================================================
        //  Changes - blocking; call from a background thread
        // =================================================================

        public WinServiceChangeResult Start(WinService service)
        {
            var result = StartOne(service.Name);
            Sync(service);
            return result;
        }

        /// <summary>Stops the service. With <paramref name="withDependents"/> the running services that depend on it are stopped first.</summary>
        public WinServiceChangeResult Stop(WinService service, bool withDependents)
        {
            if (withDependents)
            {
                foreach (var dependent in RunningDependents(service))
                {
                    var stopped = StopOne(dependent.Name);
                    Sync(dependent);
                    if (stopped != WinServiceChangeResult.Done)
                    {
                        Sync(service);
                        return stopped;
                    }
                }
            }
            var result = StopOne(service.Name);
            Sync(service);
            return result;
        }

        /// <summary>Stops the service (and what depends on it), starts it again, then starts the dependents that were running.</summary>
        public WinServiceChangeResult Restart(WinService service)
        {
            var dependents = RunningDependents(service);
            var result = Stop(service, withDependents: true);
            if (result != WinServiceChangeResult.Done) return result;

            result = StartOne(service.Name);
            Sync(service);
            if (result != WinServiceChangeResult.Done) return result;

            for (int i = dependents.Count - 1; i >= 0; i--)
            {
                StartOne(dependents[i].Name);   // best effort: the service itself is back up
                Sync(dependents[i]);
            }
            return WinServiceChangeResult.Done;
        }

        public WinServiceChangeResult Pause(WinService service)
        {
            var result = PauseOne(service.Name);
            Sync(service);
            return result;
        }

        public WinServiceChangeResult Resume(WinService service)
        {
            var result = ResumeOne(service.Name);
            Sync(service);
            return result;
        }

        public WinServiceChangeResult SetStartMode(WinService service, WinServiceStartMode mode)
        {
            var result = ConfigureStartMode(service.Name, mode);
            Sync(service);
            return result;
        }

        /// <summary>Reads the service again and updates the page's copy on the UI thread.</summary>
        private void Sync(WinService service)
        {
            var fresh = ReadOne(service.Name);
            if (fresh == null) return;
            if (_ui == null) service.UpdateFrom(fresh);
            else _ui.Send(_ => service.UpdateFrom(fresh), null);
        }

        private static WinServiceChangeResult FromCode(uint code) => code switch
        {
            0 or 10 => WinServiceChangeResult.Done,   // 10 = already running
            2 => WinServiceChangeResult.NeedsAdministrator,
            3 => WinServiceChangeResult.HasRunningDependents,
            7 => WinServiceChangeResult.TimedOut,
            14 => WinServiceChangeResult.Disabled,
            _ => WinServiceChangeResult.Failed,
        };
    }
}
