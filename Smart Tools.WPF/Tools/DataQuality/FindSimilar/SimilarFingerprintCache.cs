using Smart_Tools.WPF.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Remembers each image/video signature (keyed by path + size + modified time) so a rescan only reads what
    /// changed. Audio is not cached (its hashes belong to SoundFingerprinting).
    /// <para>
    /// Two files: <c>similar_cache.bin</c> (everything, written when a scan finishes) and
    /// <c>similar_cache.bin.journal</c> (each signature appended the moment it is computed and flushed every few
    /// seconds). A scan that is cancelled, crashes or loses power therefore keeps almost all its work: the next
    /// <see cref="Load"/> folds the journal back in, ignoring a half-written last record. A damaged or older-format
    /// file is simply ignored and rebuilt.
    /// </para>
    /// </summary>
    public sealed class SimilarFingerprintCache
    {
        /// <summary>Bump when an engine changes what it stores, so stale signatures are recomputed.</summary>
        public const int EngineVersion = 3;
        private const int Magic = 0x53464331;          // "SFC1"
        private const int JournalMagic = 0x53464A31;   // "SFJ1"
        private const int MaxEntries = 200_000;
        private const int MaxRecordBytes = 64 * 1024 * 1024;
        private const long JournalFlushMs = 3_000;

        private readonly record struct Key(string Path, long Size, long Ticks);

        private sealed class Entry
        {
            public required Key Key;
            public required MediaSignature Signature;
            public bool Touched;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;
        private string JournalFile => _file + ".journal";

        private readonly object _journalLock = new();
        private FileStream? _journal;
        private BinaryWriter? _journalWriter;
        private bool _journalBroken;
        private bool _finished;   // Close() is final: a worker still finishing its file must not reopen (and truncate) the journal
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastFlushMs;

        public SimilarFingerprintCache(string? file = null)
        {
            _file = file ?? System.IO.Path.Combine(AppPaths.DataRoot, "similar_cache.bin");
        }

        public int Count => _entries.Count;

        /// <summary>How many signatures were rescued from the journal of an interrupted scan when this cache was loaded.</summary>
        public int RecoveredFromJournal { get; private set; }

        public static SimilarFingerprintCache Load(string? file = null)
        {
            var cache = new SimilarFingerprintCache(file);
            try
            {
                if (File.Exists(cache._file))
                {
                    using var stream = new BufferedStream(File.OpenRead(cache._file), 1 << 20);
                    using var r = new BinaryReader(stream, Encoding.UTF8);
                    if (r.ReadInt32() == Magic && r.ReadInt32() == EngineVersion)
                    {
                        int count = r.ReadInt32();
                        if (count >= 0 && count <= MaxEntries * 2)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                string path = r.ReadString();
                                var key = new Key(path, r.ReadInt64(), r.ReadInt64());
                                cache._entries[path] = new Entry { Key = key, Signature = MediaSignature.Read(r) };
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Info($"Fingerprint cache ignored ({ex.GetType().Name}: {ex.Message})");
                cache._entries.Clear();
            }

            cache.RecoverJournal();
            return cache;
        }

        // =================================================================
        //  Journal
        // =================================================================

        /// <summary>Folds the journal of an interrupted scan into memory, then compacts it into the main file.</summary>
        private void RecoverJournal()
        {
            string journal = JournalFile;
            try
            {
                if (!File.Exists(journal)) return;

                int recovered = 0;
                using (var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                using (var r = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (stream.Length >= 8 && r.ReadInt32() == JournalMagic && r.ReadInt32() == EngineVersion)
                    {
                        while (stream.Length - stream.Position >= 4)
                        {
                            int length = r.ReadInt32();
                            if (length <= 0 || length > MaxRecordBytes || stream.Length - stream.Position < length) break;   // a half-written last record
                            byte[] payload = r.ReadBytes(length);
                            try
                            {
                                using var ms = new MemoryStream(payload);
                                using var pr = new BinaryReader(ms, Encoding.UTF8);
                                string path = pr.ReadString();
                                var key = new Key(path, pr.ReadInt64(), pr.ReadInt64());
                                _entries[path] = new Entry { Key = key, Signature = MediaSignature.Read(pr) };
                                recovered++;
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
                            {
                                break;   // damaged record: keep what was read before it
                            }
                        }
                    }
                }

                RecoveredFromJournal = recovered;
                if (recovered > 0)
                {
                    Log.Info($"Fingerprint cache: recovered {recovered} signatures from an interrupted scan");
                    Save();   // compacts into the main file and removes the journal
                }
                else
                {
                    SafeFileOps.DeleteQuietly(journal);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Fingerprint cache journal could not be read.", ex);
            }
        }

        /// <summary>Appends one finished signature to the journal (cheap: one small write, flushed every few seconds).</summary>
        private void AppendJournal(Entry entry)
        {
            lock (_journalLock)
            {
                if (_journalBroken || _finished) return;
                try
                {
                    if (_journalWriter == null)
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(JournalFile))!);
                        _journal = new FileStream(JournalFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                        _journalWriter = new BinaryWriter(_journal, Encoding.UTF8);
                        _journalWriter.Write(JournalMagic);
                        _journalWriter.Write(EngineVersion);
                    }

                    using var ms = new MemoryStream(4096);
                    using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                    {
                        w.Write(entry.Key.Path);
                        w.Write(entry.Key.Size);
                        w.Write(entry.Key.Ticks);
                        entry.Signature.Write(w);
                    }
                    _journalWriter.Write((int)ms.Length);
                    _journalWriter.Write(ms.GetBuffer(), 0, (int)ms.Length);

                    long now = _clock.ElapsedMilliseconds;
                    if (now - _lastFlushMs >= JournalFlushMs)
                    {
                        _lastFlushMs = now;
                        _journalWriter.Flush();
                        _journal!.Flush(flushToDisk: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not worth failing a scan over: it just won't be resumable.
                    _journalBroken = true;
                    Log.Warn("Fingerprint journal could not be written; this scan can't be resumed if interrupted.", ex);
                    CloseJournalCore();
                }
            }
        }

        private void CloseJournalCore()
        {
            try
            {
                _journalWriter?.Flush();
                _journal?.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // closing anyway
            }
            try { _journalWriter?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _journalWriter = null;
            _journal = null;
        }

        /// <summary>
        /// For a scan that was cancelled or failed: makes sure everything computed so far is on disk in the journal
        /// and releases the file. The next <see cref="Load"/> picks it up. Quick, whatever the cache size.
        /// </summary>
        public void Close()
        {
            lock (_journalLock)
            {
                CloseJournalCore();
                _finished = true;
            }
        }

        // =================================================================
        //  Lookup and storage
        // =================================================================

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
            var entry = new Entry
            {
                Key = new Key(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks),
                Signature = signature,
                Touched = true,
            };
            _entries[file.FullName] = entry;
            AppendJournal(entry);
        }

        /// <summary>
        /// Writes this scan's entries, plus older ones whose file still exists (up to a cap), to the main file and
        /// removes the journal. Streamed to a temporary file and moved into place, so a crash leaves the old file
        /// (and the journal) intact, and a very large cache is never held in memory twice.
        /// </summary>
        public void Save()
        {
            Close();
            string temp = _file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                var keep = new List<Entry>();
                foreach (var e in _entries.Values)
                {
                    if (e.Touched || File.Exists(e.Key.Path)) keep.Add(e);
                }
                if (keep.Count > MaxEntries)
                    keep = keep.OrderByDescending(e => e.Touched).Take(MaxEntries).ToList();

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_file))!);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var w = new BinaryWriter(stream, Encoding.UTF8))
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
                    w.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, _file, overwrite: true);
                SafeFileOps.DeleteQuietly(JournalFile);   // everything in it is in the main file now
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SafeFileOps.DeleteQuietly(temp);
                Log.Warn("Fingerprint cache could not be saved.", ex);
            }
        }
    }
}
