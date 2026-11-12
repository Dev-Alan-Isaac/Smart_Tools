using System.Globalization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    public enum TimerMode { Stopwatch, Countdown, Pomodoro }

    public enum PomodoroPhase { Work, ShortBreak, LongBreak }

    public sealed class TimerStopwatchSettings
    {
        public TimerMode Mode { get; set; } = TimerMode.Stopwatch;
        public string Countdown { get; set; } = "05:00";
        public int WorkMinutes { get; set; } = 25;
        public int ShortBreakMinutes { get; set; } = 5;
        public int LongBreakMinutes { get; set; } = 15;
        public int LongBreakEvery { get; set; } = 4;
        public bool PlaySound { get; set; } = true;
        /// <summary>Which alert sound (<see cref="TimerAlertPlayer.Choices"/> id).</summary>
        public string Sound { get; set; } = TimerAlertPlayer.DefaultId;
        /// <summary>The user's own sound file, used when <see cref="Sound"/> is "file".</summary>
        public string? SoundFile { get; set; }
    }

    /// <summary>Timer &amp; Stopwatch logic: reading a typed time, showing it, and the Pomodoro cycle. No WPF types.</summary>
    public static class TimerStopwatchManager
    {
        public const string SettingsName = "timer_stopwatch";
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(99);

        private static readonly Regex Units = new(@"^(?:(?<h>\d+)\s*h)?\s*(?:(?<m>\d+)\s*m)?\s*(?:(?<s>\d+)\s*s)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// "25" (minutes), "90s", "5m", "1h30m", "5:00" (min:sec), "1:30:00" (h:min:sec) or "1.5" (minutes).
        /// False for nothing, zero, or more than 99 hours.
        /// </summary>
        public static bool TryParseDuration(string? text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();

            if (t.Contains(':'))
            {
                var parts = t.Split(':');
                if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return false;
                var n = parts.Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : -1).ToArray();
                if (n.Any(v => v < 0)) return false;
                long seconds = parts.Length == 2 ? n[0] * 60 + n[1] : n[0] * 3600 + n[1] * 60 + n[2];
                if (parts.Length == 3 && (n[1] > 59 || n[2] > 59)) return false;
                if (parts.Length == 2 && n[1] > 59) return false;
                return Finish(seconds, out duration);
            }

            if (t.Any(char.IsAsciiLetter))
            {
                var m = Units.Match(t);
                if (!m.Success || !(m.Groups["h"].Success || m.Groups["m"].Success || m.Groups["s"].Success)) return false;
                long h = m.Groups["h"].Success ? long.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
                long mi = m.Groups["m"].Success ? long.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                long s = m.Groups["s"].Success ? long.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
                if (h > 99 || mi > 99 * 60 || s > 99 * 3600) return false;
                return Finish(h * 3600 + mi * 60 + s, out duration);
            }

            if (double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes) && minutes > 0 && minutes <= 99 * 60)
                return Finish((long)Math.Round(minutes * 60), out duration);
            return false;
        }

        private static bool Finish(long seconds, out TimeSpan duration)
        {
            duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return seconds > 0 && duration <= MaxDuration;
        }

        /// <summary>"01:05.37", or "1:02:03" past an hour (hundredths for a stopwatch).</summary>
        public static string Format(TimeSpan t, bool hundredths)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int hours = (int)t.TotalHours;
            string core = hours > 0
                ? $"{hours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
            return hundredths ? core + "." + (t.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture) : core;
        }

        /// <summary>A countdown shows whole seconds, rounded up, so it reads 00:01 until it really is over.</summary>
        public static TimeSpan RoundUpToSecond(TimeSpan t) =>
            t <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));

        /// <summary>
        /// What follows a phase: work then a short break, and a long break after every <paramref name="longEvery"/>th work
        /// session. <paramref name="completedWork"/> counts the work sessions finished so far (updated here).
        /// </summary>
        public static PomodoroPhase Next(PomodoroPhase current, ref int completedWork, int longEvery)
        {
            longEvery = Math.Max(1, longEvery);
            if (current != PomodoroPhase.Work) return PomodoroPhase.Work;
            completedWork++;
            return completedWork % longEvery == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
        }

        public static TimerStopwatchSettings Clamp(TimerStopwatchSettings s)
        {
            if (!Enum.IsDefined(s.Mode)) s.Mode = TimerMode.Stopwatch;
            s.Countdown = string.IsNullOrWhiteSpace(s.Countdown) ? "05:00" : s.Countdown.Trim();
            s.WorkMinutes = Math.Clamp(s.WorkMinutes, 1, 180);
            s.ShortBreakMinutes = Math.Clamp(s.ShortBreakMinutes, 1, 60);
            s.LongBreakMinutes = Math.Clamp(s.LongBreakMinutes, 1, 120);
            s.LongBreakEvery = Math.Clamp(s.LongBreakEvery, 1, 12);
            if (!TimerAlertPlayer.Choices.Any(c => c.Id == s.Sound)) s.Sound = TimerAlertPlayer.DefaultId;
            return s;
        }
    }
}
