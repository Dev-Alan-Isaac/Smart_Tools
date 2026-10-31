using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Process Killer backend. Reads every process on a background loop and raises one
    /// <see cref="ProcessKillerSnapshot"/> per refresh on the UI thread; ends processes
    /// (one, many or a tree) with per-item results; finds who uses a port or locks a file.
    /// This file is plain logic; Windows calls live in ProcessKillerManager.Windows.cs.
    /// </summary>
    public sealed partial class ProcessKillerManager : IDisposable
    {
        private readonly SynchronizationContext? _ui;
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _loopCts;
        private volatile int _refreshSeconds = 2;
        private volatile bool _disposed;

        public ProcessKillerManager()
        {
            _ui = SynchronizationContext.Current;
        }

        /// <summary>A new reading (UI thread).</summary>
        public event Action<ProcessKillerSnapshot>? SnapshotReady;

        public bool IsRunning => _loopCts != null;

        public static int SelfPid { get; } = Environment.ProcessId;

        /// <summary>Seconds between readings; 0 pauses (RefreshNow still reads once).</summary>
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set
            {
                _refreshSeconds = Math.Clamp(value, 0, 60);
                RefreshNow();
            }
        }

        public void Start()
        {
            if (_disposed || _loopCts != null) return;
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _ = Task.Factory.StartNew(() => LoopAsync(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        public void Stop()
        {
            var cts = _loopCts;
            _loopCts = null;
            cts?.Cancel();
        }

        /// <summary>Reads again as soon as possible.</summary>
        public void RefreshNow() => Volatile.Read(ref _wake).TrySetResult();

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        private void Post(Action action)
        {
            if (_ui == null) action();
            else _ui.Post(_ => { if (!_disposed) action(); }, null);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            var state = new ReadState();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // A fresh signal per round: RefreshNow during the read below ends the wait at once.
                    var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref _wake, wake);

                    ProcessKillerSnapshot? snapshot = null;
                    try
                    {
                        snapshot = ReadSnapshot(state);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Info($"Process Killer: read failed: {ex.Message}");
                    }
                    if (snapshot != null && !token.IsCancellationRequested) Post(() => SnapshotReady?.Invoke(snapshot));

                    int seconds = _refreshSeconds;
                    var delay = Task.Delay(seconds > 0 ? seconds * 1000 : Timeout.Infinite, token);
                    await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }

        // =================================================================
        //  Rows (pure: merge, filter, tree)
        // =================================================================

        /// <summary>
        /// Syncs the visible list with a reading in place - existing rows are updated,
        /// gone ones removed, new ones added - so the selection and scroll position stay.
        /// </summary>
        public static void Merge(ObservableCollection<ProcessKillerRow> rows, IReadOnlyList<ProcessKillerSample> samples)
        {
            var incoming = new Dictionary<(int, long), ProcessKillerSample>(samples.Count);
            foreach (var s in samples) incoming[(s.Pid, s.CreationTicks)] = s;

            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (!incoming.ContainsKey((rows[i].Pid, rows[i].CreationTicks))) rows.RemoveAt(i);
            }

            var existing = new Dictionary<(int, long), ProcessKillerRow>(rows.Count);
            foreach (var r in rows) existing[(r.Pid, r.CreationTicks)] = r;

            foreach (var s in samples)
            {
                if (!existing.TryGetValue((s.Pid, s.CreationTicks), out var row))
                {
                    row = new ProcessKillerRow(s.Pid, s.CreationTicks, s.Name) { IsSelf = s.Pid == SelfPid };
                    Update(row, s);
                    rows.Add(row);
                    existing[(s.Pid, s.CreationTicks)] = row;
                }
                else Update(row, s);
            }

            var byPid = new Dictionary<int, ProcessKillerRow>(rows.Count);
            foreach (var r in rows) byPid[r.Pid] = r;
            foreach (var r in rows)
            {
                // A parent that started after its "child" is a reused PID, not the real parent.
                r.ParentName = byPid.TryGetValue(r.ParentPid, out var p) && p.Pid != r.Pid
                               && (p.CreationTicks == 0 || r.CreationTicks == 0 || p.CreationTicks <= r.CreationTicks)
                    ? p.Name
                    : null;
            }
        }

        private static void Update(ProcessKillerRow row, ProcessKillerSample s)
        {
            row.ParentPid = s.ParentPid;
            row.Path = s.Path;
            row.Owner = s.Owner;
            row.WindowTitle = s.WindowTitle;
            row.Threads = s.Threads;
            row.CpuPercent = s.CpuPercent;
            row.MemoryBytes = s.MemoryBytes;
            row.IsCritical = s.IsCritical;
            row.Kind = s.Kind;
        }

        /// <summary>Search matches name, PID, path, owner, window title (any case) and, when it is a port number, the processes using that port (<paramref name="portPids"/>).</summary>
        public static bool Matches(ProcessKillerRow row, string? search, ProcessKillerFilter filter, IReadOnlySet<int>? onlyPids, IReadOnlySet<int>? portPids = null)
        {
            if (onlyPids != null && !onlyPids.Contains(row.Pid)) return false;

            switch (filter)
            {
                case ProcessKillerFilter.Apps when row.Kind != ProcessKillerKind.App:
                case ProcessKillerFilter.Background when row.Kind != ProcessKillerKind.Background:
                case ProcessKillerFilter.Windows when row.Kind != ProcessKillerKind.Windows:
                    return false;
            }

            if (string.IsNullOrWhiteSpace(search)) return true;
            string q = search.Trim();
            return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.Pid.ToString() == q
                || (portPids?.Contains(row.Pid) ?? false)
                || (row.Path?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (row.Owner?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (row.WindowTitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        /// <summary>
        /// The process and everything it started, children first, so nothing is re-parented
        /// halfway. A "child" that started before its parent is a reused PID and is skipped.
        /// </summary>
        public static List<ProcessKillerRow> TreeChildrenFirst(IEnumerable<ProcessKillerRow> all, ProcessKillerRow root)
        {
            var children = all
                .Where(r => r.Pid != r.ParentPid)
                .GroupBy(r => r.ParentPid)
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<ProcessKillerRow>();
            var seen = new HashSet<int>();

            void Visit(ProcessKillerRow node)
            {
                if (!seen.Add(node.Pid)) return;
                if (children.TryGetValue(node.Pid, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        if (node.CreationTicks != 0 && kid.CreationTicks != 0 && kid.CreationTicks < node.CreationTicks) continue;
                        Visit(kid);
                    }
                }
                result.Add(node);
            }

            Visit(root);
            return result;
        }

        public static int? ParsePort(string? text) =>
            int.TryParse(text?.Trim().TrimStart(':'), out int port) && port is > 0 and <= 65535 ? port : null;

        // =================================================================
        //  Ending processes
        // =================================================================

        /// <summary>Ends each process on a worker thread; one failure never stops the rest.</summary>
        public static Task<List<ProcessKillerEndResult>> EndAsync(IReadOnlyList<ProcessKillerRow> targets) =>
            Task.Run(() =>
            {
                var results = new List<ProcessKillerEndResult>(targets.Count);
                foreach (var t in targets)
                {
                    if (!t.CanEnd)
                    {
                        results.Add(new ProcessKillerEndResult(t.Pid, t.Name, ProcessKillerEndOutcome.Blocked, t.Block.ToString()));
                        continue;
                    }
                    results.Add(EndOne(t.Pid, t.CreationTicks, t.Name));
                }
                return results;
            });

        // =================================================================
        //  Settings (how the tool is set up - nothing about the processes)
        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static readonly string[] SortableColumns =
        [
            nameof(ProcessKillerRow.Name), nameof(ProcessKillerRow.Pid), nameof(ProcessKillerRow.CpuPercent),
            nameof(ProcessKillerRow.MemoryBytes), nameof(ProcessKillerRow.Threads), nameof(ProcessKillerRow.OwnerText),
            nameof(ProcessKillerRow.Path), nameof(ProcessKillerRow.Kind),
        ];

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "process_killer.json");

        public static ProcessKillerSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ProcessKillerSettings();
                var s = JsonSerializer.Deserialize<ProcessKillerSettings>(File.ReadAllText(file), JsonOptions) ?? new ProcessKillerSettings();
                if (!Enum.IsDefined(s.Filter)) s.Filter = ProcessKillerFilter.All;
                if (s.RefreshSeconds is not (0 or 1 or 2 or 5)) s.RefreshSeconds = 2;
                if (!SortableColumns.Contains(s.SortColumn)) s.SortColumn = nameof(ProcessKillerRow.Name);
                return s;
            }
            catch (Exception)
            {
                return new ProcessKillerSettings();
            }
        }

        public static void SaveSettings(ProcessKillerSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
    }
}
