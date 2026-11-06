using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Remembers each file's fingerprint (path + size + modified time + method) between scans, so a rescan of
    /// 10,000+ files only reads the ones that are new or changed. An entry is a handful of 64-bit numbers.
    /// Kept to <see cref="MaxEntries"/>: the entries least recently seen are dropped first.
    /// </summary>
    internal sealed class SimilarFingerprintCache
    {
        private const int Version = 1;
        private const int MaxEntries = 500_000;

        private sealed class Entry
        {
            public long Size;
            public long Ticks;
            public SimilarDetectionMethod Method;
            public required FileFingerprint Print;
            public int Seen;
        }

        private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;
        private int _generation;
        private int _dirty;

        private SimilarFingerprintCache(string file) => _file = file;

        public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "similar_cache.bin");

        public int Count => _map.Count;

        /// <summary>Starts a scan: entries touched from now on count as "seen" for pruning.</summary>
        public void BeginScan() => Interlocked.Increment(ref _generation);

        public static SimilarFingerprintCache Load(string? path = null)
        {
            var cache = new SimilarFingerprintCache(path ?? DefaultPath);
            try
            {
                if (!File.Exists(cache._file)) return cache;
                using var stream = new BufferedStream(File.OpenRead(cache._file), 1 << 16);
                using var r = new BinaryReader(stream);
                if (r.ReadInt32() != Version) return cache;
                int count = r.ReadInt32();
                cache._generation = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string key = r.ReadString();
                    var e = new Entry
                    {
                        Size = r.ReadInt64(),
                        Ticks = r.ReadInt64(),
                        Method = (SimilarDetectionMethod)r.ReadByte(),
                        Seen = r.ReadInt32(),
                        Print = ReadPrint(r),
                    };
                    cache._map[key] = e;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException)
            {
                // A damaged cache is only a cache: start empty and rebuild it.
                Log.Warn("Find Similar fingerprint cache ignored: " + ex.Message);
                cache._map.Clear();
            }
            return cache;
        }

        public bool TryGet(FileInfo file, SimilarDetectionMethod method, out FileFingerprint print)
        {
            if (_map.TryGetValue(file.FullName, out var e) && e.Method == method && e.Size == file.Length && e.Ticks == file.LastWriteTimeUtc.Ticks)
            {
                e.Seen = _generation;
                print = e.Print;
                return true;
            }
            print = null!;
            return false;
        }

        public void Put(FileInfo file, SimilarDetectionMethod method, FileFingerprint print)
        {
            _map[file.FullName] = new Entry
            {
                Size = file.Length,
                Ticks = file.LastWriteTimeUtc.Ticks,
                Method = method,
                Print = print,
                Seen = _generation,
            };
            Interlocked.Exchange(ref _dirty, 1);
            // A long scan that is stopped keeps the work done so far.
            if (Interlocked.Increment(ref _sinceSave) % 2000 == 0) Save();
        }

        private int _sinceSave;
        private readonly object _saveLock = new();

        public void Save()
        {
            lock (_saveLock)
            {
                if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
                SaveCore();
            }
        }

        private void SaveCore()
        {
            try
            {
                if (_map.Count > MaxEntries)
                {
                    foreach (var kv in _map.OrderBy(k => k.Value.Seen).Take(_map.Count - MaxEntries).ToList())
                        _map.TryRemove(kv.Key, out _);
                }

                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    var snapshot = _map.ToArray();
                    w.Write(Version);
                    w.Write(snapshot.Length);
                    w.Write(_generation);
                    foreach (var (key, e) in snapshot)
                    {
                        w.Write(key);
                        w.Write(e.Size);
                        w.Write(e.Ticks);
                        w.Write((byte)e.Method);
                        w.Write(e.Seen);
                        WritePrint(w, e.Print);
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                AtomicFile.WriteAllBytes(_file, ms.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Find Similar fingerprint cache not saved: " + ex.Message);
            }
        }

        private static void WritePrint(BinaryWriter w, FileFingerprint p)
        {
            var hashes = p.Fingerprint.Hashes;
            w.Write((byte)hashes.Count);
            foreach (ulong h in hashes) w.Write(h);
            w.Write(p.SecondaryHash.HasValue);
            if (p.SecondaryHash.HasValue) w.Write(p.SecondaryHash.Value);
            w.Write(p.Variance.HasValue);
            if (p.Variance.HasValue) w.Write(p.Variance.Value);
            w.Write(p.DurationSeconds);
            w.Write(p.Width);
            w.Write(p.Height);
        }

        private static FileFingerprint ReadPrint(BinaryReader r)
        {
            int n = r.ReadByte();
            var hashes = new ulong[n];
            for (int i = 0; i < n; i++) hashes[i] = r.ReadUInt64();
            ulong? secondary = r.ReadBoolean() ? r.ReadUInt64() : null;
            double? variance = r.ReadBoolean() ? r.ReadDouble() : null;
            return new FileFingerprint
            {
                Fingerprint = new Fingerprint(hashes),
                SecondaryHash = secondary,
                Variance = variance,
                DurationSeconds = r.ReadDouble(),
                Width = r.ReadInt32(),
                Height = r.ReadInt32(),
            };
        }
    }
}
