using Smart_Tools.WPF.Controls;
using MaterialDesignThemes.Wpf;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What the whole period amounts to: how many readings were taken and how many seconds they cover.</summary>
    public readonly record struct UsageTotals(double Samples, double Seconds);

    /// <summary>One program in a ranking: its headline value and its second figure (a peak or a rate).</summary>
    public sealed record UsageRank(string Name, double Value, double Peak);

    /// <summary>
    /// One thing a history tool can measure (CPU, memory, download...): how to read it from a minute of history, how to rank and write it.
    /// <see cref="UsageBoardBuilder"/> turns it into the page's figures, chart and ranking.
    /// </summary>
    public sealed class UsageMetric
    {
        /// <summary>Saved in <see cref="UsageSettings.Metric"/>.</summary>
        public required string Name { get; init; }

        /// <summary>Language key of the button.</summary>
        public required string LabelKey { get; init; }

        /// <summary>What a program did in one minute, from its slots.</summary>
        public required Func<double[], double> Sum { get; init; }

        /// <summary>How much a minute counts in the chart (readings, or seconds when the figure is a total).</summary>
        public required Func<UsageBucket, double> Den { get; init; }

        /// <summary>A program's highest figure in one minute.</summary>
        public required Func<double[], UsageBucket, double> PeakOf { get; init; }

        /// <summary>The ranking value from a program's sum over the period.</summary>
        public required Func<double, UsageTotals, double> Rank { get; init; }

        /// <summary>The value as written in the ranking, and as written on the chart axis and tooltip.</summary>
        public required Func<double, string> ValueText { get; init; }

        public required Func<double, string> ChartFormat { get; init; }

        /// <summary>The second column: (peak, value, totals) to text.</summary>
        public required Func<double, double, UsageTotals, string> SecondText { get; init; }

        public required string ValueHeaderKey { get; init; }
        public required string PeakHeaderKey { get; init; }

        /// <summary>The middle figure above the chart, from the minutes and the whole ranking.</summary>
        public required Func<IReadOnlyList<UsageBucket>, IReadOnlyList<UsageRank>, UsageHighlight> Middle { get; init; }
    }

    /// <summary>Builds a history page's data (figures, stacked chart, ranking) for one metric and period. Keys use the tool's prefix: Str_&lt;prefix&gt;_StatHeaviest, _StatBusiest, _StatBusiestSub.</summary>
    public static class UsageBoardBuilder
    {
        private const int RankedPrograms = 40;

        public static UsageBoardData Build(AppUsageStore store, UsageMetric metric, TimeSpan period, string prefix)
        {
            var since = DateTime.UtcNow - period;
            var buckets = store.Since(since);
            double samples = buckets.Sum(b => (double)b.Samples);
            var totals = new UsageTotals(samples, Math.Max(1, samples * AppUsageStore.SampleSeconds));

            var perApp = new Dictionary<string, (double Sum, double Peak)>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in buckets)
                foreach (var (app, slots) in b.Apps)
                {
                    perApp.TryGetValue(app, out var t);
                    perApp[app] = (t.Sum + metric.Sum(slots), Math.Max(t.Peak, metric.PeakOf(slots, b)));
                }

            var all = perApp.Select(kv => new UsageRank(kv.Key, metric.Rank(kv.Value.Sum, totals), kv.Value.Peak))
                .Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
            var ranked = all.Take(RankedPrograms).ToList();
            double top = ranked.Count > 0 ? ranked[0].Value : 1;

            var rows = ranked.Select((r, i) => new UsageRow
            {
                Rank = i + 1,
                Name = r.Name,
                ValueText = metric.ValueText(r.Value),
                PeakText = metric.SecondText(r.Peak, r.Value, totals),
                Share = top > 0 ? r.Value / top * 100.0 : 0,
                ColorIndex = ChartPalette.IndexFor(i),
            }).ToList();

            var slices = UsageMath.Cut(buckets, since, period, metric.Sum, metric.Den);
            var stack = UsageMath.Stack(slices, ranked.Select(r => r.Name).ToList(), metric.ChartFormat, "HH:mm");

            string Key(string name) => "Str_" + prefix + "_" + name;
            var highlights = new List<UsageHighlight>();
            if (ranked.Count > 0)
                highlights.Add(new UsageHighlight(PackIconKind.TrendingUp, Loc.T(Key("StatHeaviest"), "Heaviest"), ranked[0].Name, metric.ValueText(ranked[0].Value)));
            highlights.Add(metric.Middle(buckets, all));
            var busiest = UsageMath.Busiest(slices);
            string busiestLabel = Loc.T(Key("StatBusiest"), "Busiest moment");
            highlights.Add(busiest == null
                ? new UsageHighlight(PackIconKind.ClockOutline, busiestLabel, "-", "")
                : new UsageHighlight(PackIconKind.ClockOutline, busiestLabel, busiest.Value.When.ToString("HH:mm"), metric.ChartFormat(busiest.Value.Value) + " " + Loc.T(Key("StatBusiestSub"), "all programs together")));

            return new UsageBoardData
            {
                Rows = rows,
                Highlights = highlights,
                Stack = stack,
                ValueHeaderKey = metric.ValueHeaderKey,
                PeakHeaderKey = metric.PeakHeaderKey,
            };
        }
    }
}
