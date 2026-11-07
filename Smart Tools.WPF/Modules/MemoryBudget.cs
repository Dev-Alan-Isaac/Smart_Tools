// Root namespace on purpose (same as Log): `MemoryBudget.Acquire(...)` works everywhere.
namespace Smart_Tools.WPF
{
    /// <summary>
    /// A byte budget for heavy work. Parallelism limits how MANY pictures are decoded at once, not how BIG
    /// they are: four 40 MP photos need gigabytes, four icons need nothing. A tool asks for the bytes it is
    /// about to use, waits (in order) until they fit, and gives them back when done, so the total in flight
    /// stays under a ceiling however many files are queued. Nothing here calls the garbage collector.
    /// A request bigger than the whole budget is admitted alone, so a huge file runs by itself instead of
    /// waiting forever.
    /// </summary>
    public static class MemoryBudget
    {
        private static readonly object Gate = new();
        private static readonly LinkedList<Waiter> Queue = new();
        private static long _used;

        /// <summary>Roughly a quarter of the machine's memory, never below 256 MB or above 2 GB.</summary>
        public static long Total { get; } = ComputeTotal();

        /// <summary>Bytes currently handed out (for tests and the log).</summary>
        public static long InUse { get { lock (Gate) return _used; } }

        private static long ComputeTotal()
        {
            long ram;
            try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
            catch (InvalidOperationException) { ram = 0; }
            if (ram <= 0) ram = 4L << 30;
            return Math.Clamp(ram / 4, 256L << 20, 2L << 30);
        }

        private sealed class Waiter
        {
            public long Bytes;
            public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public readonly struct Lease : IDisposable
        {
            private readonly long _bytes;
            internal Lease(long bytes) => _bytes = bytes;
            public void Dispose() { if (_bytes > 0) Release(_bytes); }
        }

        /// <summary>Waits for <paramref name="bytes"/> to fit. Blocks the calling thread: use from worker threads, not the UI.</summary>
        public static Lease Acquire(long bytes, CancellationToken ct = default)
            => AcquireAsync(bytes, ct).GetAwaiter().GetResult();

        public static Task<Lease> AcquireAsync(long bytes, CancellationToken ct = default)
        {
            bytes = Math.Max(0, bytes);
            if (bytes == 0) return Task.FromResult(new Lease(0));
            ct.ThrowIfCancellationRequested();

            Waiter w;
            lock (Gate)
            {
                // Strict order: a big request at the head is not starved by small ones behind it.
                if (Queue.Count == 0 && Fits(bytes))
                {
                    _used += bytes;
                    return Task.FromResult(new Lease(bytes));
                }
                w = new Waiter { Bytes = bytes };
                Queue.AddLast(w);
            }
            return Wait(w, bytes, ct);
        }

        private static async Task<Lease> Wait(Waiter w, long bytes, CancellationToken ct)
        {
            using (ct.Register(() =>
            {
                lock (Gate)
                {
                    if (!w.Done.Task.IsCompleted) { Queue.Remove(w); w.Done.TrySetCanceled(ct); }
                }
            }))
            {
                await w.Done.Task.ConfigureAwait(false);
            }
            return new Lease(bytes);
        }

        private static bool Fits(long bytes) => _used == 0 || _used + bytes <= Total;

        private static void Release(long bytes)
        {
            lock (Gate)
            {
                _used = Math.Max(0, _used - bytes);
                while (Queue.First is { } head && Fits(head.Value.Bytes))
                {
                    Queue.RemoveFirst();
                    _used += head.Value.Bytes;
                    head.Value.Done.TrySetResult(true);
                }
            }
        }
    }
}
