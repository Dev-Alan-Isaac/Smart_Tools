namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Cuts a period of history into chart slices and builds the stacked columns. Numbers only: colours are theme brushes chosen by the UI.</summary>
    public static class UsageMath
    {
        public const int ChartPoints = 60;

        /// <summary>The period cut into slices: for each, how long it covered (<see cref="Den"/>) and what each program did (<see cref="Num"/>).</summary>
        public sealed class Slices
        {
            public DateTime[] Times { get; init; } = Array.Empty<DateTime>();
            public double[] Den { get; init; } = Array.Empty<double>();
            public Dictionary<string, double[]> Num { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public static Slices Cut(IReadOnlyList<UsageBucket> buckets, DateTime sinceUtc, TimeSpan period, Func<double[], double> numerator, Func<UsageBucket, double> denominator)
        {
            var den = new double[ChartPoints];
            var num = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            double sliceMinutes = period.TotalMinutes / ChartPoints;
            foreach (var b in buckets)
            {
                int i = (int)Math.Clamp(Math.Floor((b.StartUtc - sinceUtc).TotalMinutes / sliceMinutes), 0, ChartPoints - 1);
                den[i] += denominator(b);
                foreach (var (app, slots) in b.Apps)
                {
                    if (!num.TryGetValue(app, out var arr)) num[app] = arr = new double[ChartPoints];
                    arr[i] += numerator(slots);
                }
            }
            var times = Enumerable.Range(0, ChartPoints).Select(i => sinceUtc.AddMinutes(sliceMinutes * i).ToLocalTime()).ToArray();
            return new Slices { Times = times, Den = den, Num = num };
        }

        /// <summary>The stacked columns: the top programs (first = bottom) and everything else as "Other".</summary>
        public static Controls.StackedData Stack(Slices s, IReadOnlyList<string> topNames, Func<double, string> format, string timeFormat)
        {
            var series = new List<Controls.StackSeries>();
            double[] Per(double[] num) => num.Select((v, i) => s.Den[i] > 0 ? v / s.Den[i] : 0).ToArray();
            int shown = Math.Min(Controls.ChartPalette.Colors, topNames.Count);
            for (int k = 0; k < shown; k++)
                if (s.Num.TryGetValue(topNames[k], out var arr))
                    series.Add(new Controls.StackSeries { Name = topNames[k], ColorIndex = k, Values = Per(arr) });
            var others = s.Num.Where(kv => !topNames.Take(Controls.ChartPalette.Colors).Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0)
            {
                var sum = new double[ChartPoints];
                foreach (var kv in others) for (int i = 0; i < ChartPoints; i++) sum[i] += kv.Value[i];
                series.Add(new Controls.StackSeries { Name = Loc.T("Str_AppUsage_Other", "Other programs"), ColorIndex = Controls.ChartPalette.OtherIndex, Values = Per(sum) });
            }
            return new Controls.StackedData { Times = s.Times, HasData = s.Den, Series = series, Format = format, TimeFormat = timeFormat };
        }

        /// <summary>The slice where all programs together were highest: its time and value.</summary>
        public static (DateTime When, double Value)? Busiest(Slices s)
        {
            (DateTime, double)? best = null;
            for (int i = 0; i < s.Den.Length; i++)
            {
                if (s.Den[i] <= 0) continue;
                double total = s.Num.Values.Sum(a => a[i]) / s.Den[i];
                if (best == null || total > best.Value.Item2) best = (s.Times[i], total);
            }
            return best;
        }
    }

    /// <summary>How the history pages write sizes and percentages.</summary>
    public static class UsageFormat
    {
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public static string Bytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i == 0 ? "0" : bytes >= 100 ? "0" : "0.0", Inv) + " " + units[i];
        }

        public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

        public static string Megabytes(double mb) => mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + " GB" : mb.ToString("0", Inv) + " MB";

        public static string Percent(double v) => v.ToString(v >= 10 ? "0" : "0.0", Inv) + " %";
    }
}
