using System.IO;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Follows a text file like "tail -f": the last lines first, then
    /// whatever is appended, checked every 300 ms (polling works where
    /// FileSystemWatcher doesn't: network shares, files held open by the
    /// writer, antivirus). Survives the file being truncated or rolled
    /// over (starts again from the top), deleted (waits for it to come
    /// back), or briefly locked (tries again next time).
    ///
    /// Fixes over the old LogTailManager:
    /// - A line still being written isn't split in two: text after the last
    ///   line break is held until its line break arrives (or the file has
    ///   been quiet for a moment - then it's shown as it is).
    /// - Each run keeps its own position; a stopped run can't raise lines
    ///   (the old one shared one position field, so a quick Stop/Start
    ///   could mix two runs and repeat or skip lines).
    /// - Characters split across two reads (UTF-8, UTF-16) decode correctly.
    /// - A sharing violation while the writer has the file locked waits for
    ///   the next check instead of ending the tab with an error.
    ///
    /// Events come from a background thread; LinesReceived delivers a batch
    /// per check, not one event per line.
    /// </summary>
    public sealed class LogViewerTail : IDisposable
    {
        public const long TailByteCap = 2 * 1024 * 1024;

        private readonly int _pollMs;
        private readonly TimeSpan _flushPartialAfter;
        private CancellationTokenSource? _cts;
        private int _run;

        public LogViewerTail(int pollMs = 300, int flushPartialAfterMs = 1500)
        {
            _pollMs = pollMs;
            _flushPartialAfter = TimeSpan.FromMilliseconds(flushPartialAfterMs);
        }

        public string? FilePath { get; private set; }
        public bool IsRunning => _cts != null;

        public event Action<IReadOnlyList<string>>? LinesReceived;
        public event Action<Exception>? Failed;
        public event Action? FileMissing;
        public event Action? FileReappeared;

        /// <summary>The file got shorter (log rotation, or cleared): reading started again from the top.</summary>
        public event Action? FileRolledOver;

        public void Start(string filePath, int initialLines)
        {
            Stop();
            FilePath = filePath;
            var cts = new CancellationTokenSource();
            _cts = cts;
            int run = Interlocked.Increment(ref _run);
            _ = Task.Run(() => RunAsync(filePath, Math.Max(0, initialLines), run, cts.Token));
        }

        /// <summary>Returns at once (never blocks the UI); the run stops at its next step and raises nothing more.</summary>
        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            Interlocked.Increment(ref _run);
            cts?.Cancel();
        }

        public void Dispose() => Stop();

        private bool Current(int run) => Volatile.Read(ref _run) == run;

        // =================================================================

        private sealed class ReadState
        {
            public long Position;
            public Encoding Encoding = new UTF8Encoding(false);
            public Decoder Decoder = new UTF8Encoding(false).GetDecoder();
            public readonly StringBuilder Pending = new();
            public DateTime LastGrowthUtc = DateTime.UtcNow;
            public bool EncodingKnown;

            public void Reset()
            {
                Position = 0;
                EncodingKnown = false;
                Pending.Clear();
            }
        }

        private async Task RunAsync(string path, int initialLines, int run, CancellationToken token)
        {
            var state = new ReadState();
            bool missing = false;
            try
            {
                if (File.Exists(path))
                {
                    var first = ReadInitial(path, initialLines, state);
                    if (first.Count > 0 && Current(run)) LinesReceived?.Invoke(first);
                }
                else
                {
                    missing = true;
                    if (Current(run)) FileMissing?.Invoke();
                }

                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(_pollMs, token).ConfigureAwait(false);

                    if (!File.Exists(path))
                    {
                        if (!missing)
                        {
                            missing = true;
                            if (Current(run)) FileMissing?.Invoke();
                        }
                        continue;
                    }
                    if (missing)
                    {
                        missing = false;
                        state.Reset();
                        if (Current(run)) FileReappeared?.Invoke();
                    }

                    List<string> lines;
                    try
                    {
                        lines = ReadNew(path, state);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        continue;   // locked by the writer for a moment - try again next check
                    }
                    if (state.Position < 0)
                    {
                        state.Reset();
                        if (Current(run)) FileRolledOver?.Invoke();
                        lines = ReadNew(path, state);
                    }

                    if (lines.Count == 0 && state.Pending.Length > 0 && DateTime.UtcNow - state.LastGrowthUtc > _flushPartialAfter)
                    {
                        lines.Add(state.Pending.ToString());   // the writer stopped mid-line: show what there is
                        state.Pending.Clear();
                    }
                    if (lines.Count > 0 && Current(run)) LinesReceived?.Invoke(lines);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (Current(run)) Failed?.Invoke(ex);
            }
        }

        private static FileStream OpenShared(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        /// <summary>The byte-order mark, if any: sets the encoding; returns how many bytes it takes.</summary>
        private static int DetectEncoding(FileStream stream, ReadState state)
        {
            var bom = new byte[3];
            stream.Seek(0, SeekOrigin.Begin);
            int n = stream.Read(bom, 0, 3);
            int skip = 0;
            if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) { state.Encoding = new UTF8Encoding(false); skip = 3; }
            else if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) { state.Encoding = new UnicodeEncoding(false, false); skip = 2; }
            else if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) { state.Encoding = new UnicodeEncoding(true, false); skip = 2; }
            else state.Encoding = new UTF8Encoding(false);
            state.Decoder = state.Encoding.GetDecoder();
            state.EncodingKnown = true;
            return skip;
        }

        /// <summary>
        /// The last <paramref name="maxLines"/> complete lines, reading at most
        /// the last 2 MB (a cut first line is dropped). Text after the last
        /// line break waits in Pending.
        /// </summary>
        private static List<string> ReadInitial(string path, int maxLines, ReadState state)
        {
            using var stream = OpenShared(path);
            int bom = DetectEncoding(stream, state);
            long length = stream.Length;
            long from = Math.Max(bom, length - TailByteCap);
            bool utf16 = state.Encoding is UnicodeEncoding;
            if (utf16 && (from - bom) % 2 != 0) from++;
            var text = Decode(stream, from, length, state);
            state.Position = length;

            var lines = SplitComplete(text, state.Pending);
            if (from > bom && lines.Count > 0) lines.RemoveAt(0);   // started mid-line
            state.LastGrowthUtc = DateTime.UtcNow;
            return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }

        /// <summary>Lines appended since the last read. Sets Position to -1 if the file got shorter (the caller restarts).</summary>
        private static List<string> ReadNew(string path, ReadState state)
        {
            using var stream = OpenShared(path);
            long length = stream.Length;
            if (length < state.Position)
            {
                state.Position = -1;
                return new List<string>();
            }
            int bom = 0;
            if (!state.EncodingKnown) bom = DetectEncoding(stream, state);
            if (state.Position < bom) state.Position = bom;
            if (length == state.Position) return new List<string>();

            var text = Decode(stream, state.Position, length, state);
            state.Position = length;
            state.LastGrowthUtc = DateTime.UtcNow;
            return SplitComplete(text, state.Pending);
        }

        private static string Decode(FileStream stream, long from, long to, ReadState state)
        {
            stream.Seek(from, SeekOrigin.Begin);
            var sb = new StringBuilder();
            var bytes = new byte[64 * 1024];
            var chars = new char[state.Encoding.GetMaxCharCount(bytes.Length)];
            long remaining = to - from;
            while (remaining > 0)
            {
                int read = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining));
                if (read <= 0) break;
                remaining -= read;
                int n = state.Decoder.GetChars(bytes, 0, read, chars, 0, flush: false);   // keeps a split character for next time
                sb.Append(chars, 0, n);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Complete lines from <paramref name="pending"/> + <paramref name="text"/>
        /// (CRLF or LF); whatever follows the last line break goes back into pending.
        /// </summary>
        public static List<string> SplitComplete(string text, StringBuilder pending)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                pending.Append(text, start, i - start);
                if (pending.Length > 0 && pending[^1] == '\r') pending.Length--;
                lines.Add(pending.ToString());
                pending.Clear();
                start = i + 1;
            }
            pending.Append(text, start, text.Length - start);
            return lines;
        }
    }
}
