namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// "About how long is left" for a scan stage, from how fast the percentage has been moving lately.
    /// The speed is measured over the last 30 seconds and over the last 2 minutes, and the SLOWER of the two
    /// wins. So a burst of already-cached files at the start (which finishes instantly) does not make the
    /// estimate wildly optimistic for the next two minutes, and a stretch of long videos is followed within
    /// half a minute. Nothing is shown in the first 40 seconds of a stage, while the rate is still noise.
    /// No WPF types, so it is tested directly.
    /// </summary>
    public sealed class SimilarEta
    {
        public const long ShortWindowMs = 30_000;
        public const long LongWindowMs = 120_000;
        /// <summary>Nothing is shown until a stage has run this long (longer than the short window, so a start-up burst has left it).</summary>
        public const long MinStageMs = 40_000;
        /// <summary>A window must span at least this much time to give a rate.</summary>
        public const long MinSpanMs = 4_000;
        /// <summary>Anything longer is shown as this: a number of days is not an estimate worth trusting.</summary>
        public static readonly TimeSpan Cap = TimeSpan.FromDays(10);

        private readonly Queue<(long Ms, double Percent)> _samples = new();
        private string _stage = "";
        private long _stageStartMs;

        /// <summary>Forget everything (a new scan).</summary>
        public void Reset()
        {
            _samples.Clear();
            _stage = "";
        }

        /// <summary>
        /// Feed one progress report. <paramref name="stage"/> names what is running (it restarts the measurement when
        /// it changes), <paramref name="percent"/> is 0-100, <paramref name="nowMs"/> a steady clock in milliseconds.
        /// Returns the time left, or null while there is no believable estimate.
        /// </summary>
        public TimeSpan? Update(string stage, double percent, long nowMs)
        {
            if (!double.IsFinite(percent)) return null;
            if (stage != _stage)
            {
                _stage = stage;
                _stageStartMs = nowMs;
                _samples.Clear();
            }

            _samples.Enqueue((nowMs, percent));
            while (_samples.Count > 1 && nowMs - _samples.Peek().Ms > LongWindowMs) _samples.Dequeue();

            if (nowMs - _stageStartMs < MinStageMs || percent >= 100) return null;

            double? slowest = null;
            foreach (long window in new[] { ShortWindowMs, LongWindowMs })
            {
                double? left = SecondsLeft(percent, nowMs, window);
                if (left is double l && (slowest == null || l > slowest)) slowest = l;
            }
            if (slowest is not double seconds) return null;
            return seconds >= Cap.TotalSeconds ? Cap : TimeSpan.FromSeconds(seconds);
        }

        private double? SecondsLeft(double percent, long nowMs, long windowMs)
        {
            (long Ms, double Percent)? oldest = null;
            foreach (var s in _samples)
            {
                if (nowMs - s.Ms <= windowMs) { oldest = s; break; }   // the queue is in time order: first one inside the window
            }
            if (oldest is not { } o) return null;

            long span = nowMs - o.Ms;
            double gained = percent - o.Percent;
            if (span < MinSpanMs || gained <= 0) return null;
            double left = (100 - percent) / (gained / (span / 1000.0));
            return double.IsFinite(left) && left >= 0 ? left : null;
        }

        /// <summary>"45 s", "12 min", "3 h 05 min", "2 d 4 h": rounded so it doesn't flicker.</summary>
        public static string Format(TimeSpan left)
        {
            if (left.TotalSeconds < 58) return Loc.F("Str_FindSimilar_EtaSeconds", "{0} s", Math.Max(1, (int)Math.Round(left.TotalSeconds / 5) * 5));
            if (left.TotalMinutes < 60) return Loc.F("Str_FindSimilar_EtaMinutes", "{0} min", Math.Max(1, (int)Math.Round(left.TotalMinutes)));
            if (left.TotalHours < 48)
            {
                int totalMinutes = (int)Math.Round(left.TotalMinutes / 5) * 5;   // to the nearest 5 minutes
                return Loc.F("Str_FindSimilar_EtaHours", "{0} h {1:00} min", totalMinutes / 60, totalMinutes % 60);
            }
            int days = (int)left.TotalDays;
            return Loc.F("Str_FindSimilar_EtaDays", "{0} d {1} h", days, (int)Math.Round(left.TotalHours - days * 24));
        }
    }
}
