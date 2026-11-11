using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>One minute of readings: for every program, a few running totals (the meaning of each slot belongs to the tool).</summary>
    public sealed class UsageBucket
    {
        /// <summary>Minutes since the Unix epoch, UTC.</summary>
        public long Minute { get; set; }

        /// <summary>How many readings were taken in this minute (to turn sums into averages).</summary>
        public int Samples { get; set; }

        public Dictionary<string, double[]> Apps { get; set; } = new();

        public DateTime StartUtc => DateTime.UnixEpoch.AddMinutes(Minute);
    }

    public sealed class AppUsageFile
    {
        public List<UsageBucket> Buckets { get; set; } = new();
    }

    /// <summary>
    /// Per-minute history of what each program used, kept for a day and saved to a small JSON file. Program names only (no paths,
    /// no window titles). Thread-safe: a recorder writes from its own thread while a page reads copies.
    /// </summary>
    public sealed class AppUsageStore
    {
        /// <summary>Seconds between two readings, for every recorder; speeds are computed from it, so there is one value.</summary>
        public const double SampleSeconds = 5;

        private readonly object _gate = new();
        private readonly object _saveGate = new();
        private readonly string _name;
        private readonly int _width;
        private readonly TimeSpan _keep;
        private List<UsageBucket> _buckets = new();
        private bool _dirty;
        private bool _cleared;   // the user deleted the history: a load that is still running must not bring it back

        public AppUsageStore(string settingsName, int width, TimeSpan keep)
        {
            _name = settingsName;
            _width = width;
            _keep = keep;
        }

        public void Load()
        {
            var file = JsonSettings.Load<AppUsageFile>(_name);
            lock (_gate)
            {
                if (_cleared) { _cleared = false; return; }
                // Keep what was recorded since the page was opened (a recorder may have added readings before the file was read).
                var loaded = (file.Buckets ?? new List<UsageBucket>()).Where(b => b?.Apps != null).ToList();
                foreach (var b in loaded) FixWidths(b);
                var fresh = _buckets.Where(b => loaded.All(l => l.Minute != b.Minute)).ToList();
                _buckets = loaded.Concat(fresh).OrderBy(b => b.Minute).ToList();
                PruneLocked(DateTime.UtcNow);
            }
        }

        /// <summary>Slots of an older file may be fewer or more than now: pad or cut, and drop empty/null rows.</summary>
        private void FixWidths(UsageBucket bucket)
        {
            foreach (string key in bucket.Apps.Keys.ToList())
            {
                double[]? slots = bucket.Apps[key];
                if (slots == null) { bucket.Apps.Remove(key); continue; }
                if (slots.Length == _width) continue;
                var fixedLength = new double[_width];
                Array.Copy(slots, fixedLength, Math.Min(_width, slots.Length));
                bucket.Apps[key] = fixedLength;
            }
        }

        public void Save()
        {
            // One save at a time: a save at exit waits for a background one instead of returning while its file is half-written.
            lock (_saveGate)
            {
                AppUsageFile file;
                lock (_gate)
                {
                    if (!_dirty) return;
                    PruneLocked(DateTime.UtcNow);
                    file = new AppUsageFile { Buckets = _buckets.Select(Rounded).ToList() };
                    _dirty = false;
                }
                // The flag only stays cleared when the file really was written, so a failed write is retried at the next save.
                if (!JsonSettings.TrySave(_name, file)) lock (_gate) _dirty = true;
            }
        }

        /// <summary>Empties the history now; the (small) empty file is written in the background.</summary>
        public void Clear()
        {
            lock (_gate) { _buckets.Clear(); _dirty = true; _cleared = true; }
            Task.Run(Save);
        }

        private static long MinuteOf(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);

        private UsageBucket CurrentLocked(DateTime utcNow)
        {
            long minute = MinuteOf(utcNow);
            if (_buckets.Count > 0 && _buckets[^1].Minute == minute) return _buckets[^1];
            var bucket = new UsageBucket { Minute = minute };
            _buckets.Add(bucket);
            PruneLocked(utcNow);
            return bucket;
        }

        private void PruneLocked(DateTime utcNow)
        {
            long oldest = MinuteOf(utcNow - _keep);
            int drop = 0;
            while (drop < _buckets.Count && _buckets[drop].Minute < oldest) drop++;
            if (drop > 0) _buckets.RemoveRange(0, drop);
        }

        /// <summary>Counts one reading in the current minute.</summary>
        public void Tick(DateTime utcNow)
        {
            lock (_gate) { CurrentLocked(utcNow).Samples++; _dirty = true; }
        }

        private double[] SlotsLocked(UsageBucket bucket, string app)
        {
            if (!bucket.Apps.TryGetValue(app, out var slots)) bucket.Apps[app] = slots = new double[_width];
            return slots;
        }

        public void Add(DateTime utcNow, string app, int slot, double value)
        {
            if (value == 0) return;
            lock (_gate) { SlotsLocked(CurrentLocked(utcNow), app)[slot] += value; _dirty = true; }
        }

        public void Peak(DateTime utcNow, string app, int slot, double value)
        {
            lock (_gate)
            {
                var slots = SlotsLocked(CurrentLocked(utcNow), app);
                if (value > slots[slot]) { slots[slot] = value; _dirty = true; }
            }
        }

        public bool IsEmpty { get { lock (_gate) return _buckets.Count == 0; } }

        /// <summary>When the first kept minute began (UTC), or null when there is nothing.</summary>
        public DateTime? FirstUtc { get { lock (_gate) return _buckets.Count == 0 ? null : _buckets[0].StartUtc; } }

        /// <summary>Copies of the minutes since <paramref name="sinceUtc"/> (safe to read while recording goes on).</summary>
        public List<UsageBucket> Since(DateTime sinceUtc)
        {
            long from = MinuteOf(sinceUtc);
            lock (_gate) return _buckets.Where(b => b.Minute >= from).Select(Clone).ToList();
        }

        private static UsageBucket Clone(UsageBucket b) => new()
        {
            Minute = b.Minute,
            Samples = b.Samples,
            Apps = b.Apps.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone()),
        };

        /// <summary>The copy that goes to the file: one decimal is plenty for percentages, megabytes and bytes, and cuts the file to under half.</summary>
        private static UsageBucket Rounded(UsageBucket b) => new()
        {
            Minute = b.Minute,
            Samples = b.Samples,
            Apps = b.Apps.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => Math.Round(v, 1)).ToArray()),
        };
    }
}
