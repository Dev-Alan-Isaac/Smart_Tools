using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Remembers each image/video signature (keyed by path + size + modified time) in one binary file,
    /// so a rescan only reads what changed. Audio is not cached (its hashes belong to SoundFingerprinting).
    /// A damaged or older-format file is simply ignored and rebuilt. Written through <see cref="AtomicFile"/>.
    /// </summary>
    public sealed class SimilarFingerprintCache
    {
        /// <summary>Bump when an engine changes what it stores, so stale signatures are recomputed.</summary>
        public const int EngineVersion = 3;
        private const int Magic = 0x53464331;   // "SFC1"
        private const int MaxEntries = 200_000;

        private readonly record struct Key(string Path, long Size, long Ticks);

        private sealed class Entry
        {
            public required Key Key;
            public required MediaSignature Signature;
            public bool Touched;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;

        public SimilarFingerprintCache(string? file = null)
        {
            _file = file ?? System.IO.Path.Combine(AppPaths.DataRoot, "similar_cache.bin");
        }

        public int Count => _entries.Count;

        public static SimilarFingerprintCache Load(string? file = null)
        {
            var cache = new SimilarFingerprintCache(file);
            try
            {
                if (!File.Exists(cache._file)) return cache;
                using var stream = new BufferedStream(File.OpenRead(cache._file), 1 << 20);
                using var r = new BinaryReader(stream, Encoding.UTF8);
                if (r.ReadInt32() != Magic || r.ReadInt32() != EngineVersion) return cache;
                int count = r.ReadInt32();
                if (count < 0 || count > MaxEntries * 2) return cache;
                for (int i = 0; i < count; i++)
                {
                    string path = r.ReadString();
                    var key = new Key(path, r.ReadInt64(), r.ReadInt64());
                    cache._entries[path] = new Entry { Key = key, Signature = MediaSignature.Read(r) };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Info($"Fingerprint cache ignored ({ex.GetType().Name}: {ex.Message})");
                cache._entries.Clear();
            }
            return cache;
        }

        public MediaSignature? TryGet(FileInfo file)
        {
            if (!_entries.TryGetValue(file.FullName, out var e)) return null;
            if (e.Key.Size != file.Length || e.Key.Ticks != file.LastWriteTimeUtc.Ticks) return null;
            e.Touched = true;
            return e.Signature;
        }

        public void Put(FileInfo file, MediaSignature signature)
        {
            if (!signature.Cacheable) return;
            _entries[file.FullName] = new Entry
            {
                Key = new Key(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks),
                Signature = signature,
                Touched = true,
            };
        }

        /// <summary>Saves this scan's entries, plus older ones whose file still exists (up to a cap).</summary>
        public void Save()
        {
            try
            {
                var keep = new List<Entry>();
                foreach (var e in _entries.Values)
                {
                    if (e.Touched || File.Exists(e.Key.Path)) keep.Add(e);
                }
                if (keep.Count > MaxEntries)
                    keep = keep.OrderByDescending(e => e.Touched).Take(MaxEntries).ToList();

                using var memory = new MemoryStream();
                using (var w = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(Magic);
                    w.Write(EngineVersion);
                    w.Write(keep.Count);
                    foreach (var e in keep)
                    {
                        w.Write(e.Key.Path);
                        w.Write(e.Key.Size);
                        w.Write(e.Key.Ticks);
                        e.Signature.Write(w);
                    }
                }
                AtomicFile.WriteAllBytes(_file, memory.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Fingerprint cache could not be saved.", ex);
            }
        }
    }
}
