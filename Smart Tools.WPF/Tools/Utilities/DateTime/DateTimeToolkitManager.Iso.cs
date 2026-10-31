using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    // Date & Time Toolkit - ISO 8601 / RFC 3339 reader and validator. Pure: no WPF, no clock.
    //
    // ISO 8601 here: calendar dates (2024-03-05 / 20240305), week dates (2024-W10-2), ordinal dates (2024-065), reduced
    // dates (2024-03, 2024), a time after "T" (14:30[:05[.25]] / 143005) with "Z" or an offset, durations (P1Y2M3DT4H5M6S, P2W)
    // and intervals (start/end, start/duration, duration/end). Basic (no dashes or colons) and extended forms must not be mixed.
    // RFC 3339 is the strict internet profile of it: YYYY-MM-DD, "T" (or t / space), HH:MM:SS[.fraction] and a mandatory offset.
    public static partial class DateTimeToolkitManager
    {
        private static readonly Regex[] DatePatterns =
        {
            new(@"^(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})$"),               // 0 calendar, extended
            new(@"^(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})$"),                 // 1 calendar, basic
            new(@"^(?<y>\d{4})-W(?<w>\d{2})-(?<wd>\d)$"),                // 2 week date, extended
            new(@"^(?<y>\d{4})-W(?<w>\d{2})$"),                          // 3 week, extended (Monday)
            new(@"^(?<y>\d{4})W(?<w>\d{2})(?<wd>\d)$"),                  // 4 week date, basic
            new(@"^(?<y>\d{4})W(?<w>\d{2})$"),                           // 5 week, basic (Monday)
            new(@"^(?<y>\d{4})-(?<o>\d{3})$"),                           // 6 ordinal, extended
            new(@"^(?<y>\d{4})(?<o>\d{3})$"),                            // 7 ordinal, basic
            new(@"^(?<y>\d{4})-(?<m>\d{2})$"),                           // 8 year and month (reduced)
            new(@"^(?<y>\d{4})$"),                                       // 9 year (reduced)
        };

        private static readonly Regex TimeExtended = new(@"^(?<h>\d{2}):(?<mi>\d{2})(?::(?<s>\d{2})(?:(?<sep>[.,])(?<f>\d+))?)?$");
        private static readonly Regex TimeBasic = new(@"^(?<h>\d{2})(?<mi>\d{2})?(?:(?<s>\d{2})(?:(?<sep>[.,])(?<f>\d+))?)?$");
        private static readonly Regex ZoneAtEnd = new(@"^(?<t>.+?)(?<z>[Zz]|[+-]\d{2}(?::?\d{2})?)$");
        private static readonly Regex DurationPattern = new(
            @"^(?<neg>-)?P(?:(?<Y>\d+(?:[.,]\d+)?)Y)?(?:(?<Mo>\d+(?:[.,]\d+)?)M)?(?:(?<W>\d+(?:[.,]\d+)?)W)?(?:(?<D>\d+(?:[.,]\d+)?)D)?(?<T>T(?:(?<H>\d+(?:[.,]\d+)?)H)?(?:(?<Mi>\d+(?:[.,]\d+)?)M)?(?:(?<S>\d+(?:[.,]\d+)?)S)?)?$");

        private static int Num(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

        // One piece of text split into its parts. Problem is "" or a code the view explains.
        private sealed class IsoParts
        {
            public DateToolIsoKind Kind = DateToolIsoKind.None;
            public string Problem = "";           // range / structure problem that makes the text meaningless (both standards)
            public DateTime Date;                  // the calendar date it names
            public bool DateBasic, DateReduced, DateIsCalendarExtended;
            public bool HasTime, TimeBasic, TimeHasSeconds, TimeCommaFraction, TwentyFour, LeapSecond;
            public int Hour, Minute, Second;
            public long FractionTicks;
            public char Separator = 'T';
            public bool HasZone, ZoneUtcLetter, ZoneLower, ZoneHasColon, ZoneHasMinutes;
            public TimeSpan Offset;
        }

        // =================================================================
        //  Entry point
        // =================================================================

        /// <summary>
        /// Reads a date, date and time, duration or interval and says whether it is valid ISO 8601 and whether it is a valid
        /// RFC 3339 timestamp, with the same moment written the other ways (RFC 3339, basic, week date, ordinal date, Unix).
        /// </summary>
        public static DateToolIsoResult AnalyzeIso(string? input, CultureInfo culture)
        {
            string t = (input ?? "").Trim();
            if (t.Length == 0) return IsoFail("Empty");

            if (t.Contains('/')) return AnalyzeInterval(t, culture);
            if (t.StartsWith('P') || t.StartsWith("-P", StringComparison.Ordinal)) return AnalyzeDuration(t);

            var p = ParseDateTime(t);
            if (p.Problem.Length > 0) return IsoFail(p.Problem, p.Kind == DateToolIsoKind.None ? DateToolIsoKind.None : p.Kind);

            // ---- ISO 8601 ----
            string iso = "";
            if (p.HasTime && p.DateReduced) iso = "ReducedWithTime";
            else if (p.HasTime && p.Separator != 'T') iso = p.Separator == ' ' ? "SpaceSeparator" : "LowercaseSeparator";
            else if (p.HasTime && p.HasZone && p.ZoneLower) iso = "LowercaseZone";
            else if (p.HasTime && p.DateBasic != p.TimeBasic) iso = "MixedBasicExtended";
            else if (p.HasTime && p.HasZone && !p.ZoneUtcLetter && p.DateBasic != !p.ZoneHasColon && p.ZoneHasMinutes) iso = "MixedBasicExtended";
            else if (!p.HasTime && p.HasZone) iso = "Unreadable";

            // ---- RFC 3339 ----
            string rfc;
            if (p.Kind != DateToolIsoKind.DateTime) rfc = p.Kind == DateToolIsoKind.Date && !p.DateReduced && !p.DateBasic ? "NeedsTime" : "NotTimestamp";
            else if (p.DateBasic || !p.DateIsCalendarExtended || p.DateReduced) rfc = "NotCalendarDate";
            else if (p.TimeBasic) rfc = "ExtendedOnly";
            else if (!p.TimeHasSeconds) rfc = "NeedsSeconds";
            else if (p.TimeCommaFraction) rfc = "CommaFraction";
            else if (p.TwentyFour) rfc = "HourTwentyFour";
            else if (!p.HasZone) rfc = "NeedsOffset";
            else if (!p.ZoneUtcLetter && !p.ZoneHasColon) rfc = "OffsetNeedsColon";
            else if (!p.ZoneUtcLetter && !p.ZoneHasMinutes) rfc = "OffsetNeedsColon";
            else rfc = "";

            // ---- Same moment, other ways ----
            var rows = new List<DateToolRow>();
            var inv = CultureInfo.InvariantCulture;
            DateTimeOffset? instant = null;
            bool assumed = false;

            if (p.Kind == DateToolIsoKind.DateTime)
            {
                var wall = p.Date.AddHours(p.Hour).AddMinutes(p.Minute).AddSeconds(p.Second).AddTicks(p.FractionTicks);
                if (p.LeapSecond) wall = wall.AddSeconds(0);           // :60 already rolled into the next minute by AddSeconds
                var offset = p.HasZone ? p.Offset : TimeSpan.Zero;
                assumed = !p.HasZone;
                try
                {
                    instant = new DateTimeOffset(wall, offset);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return IsoFail("OutOfRange", p.Kind);
                }
                var at = instant.Value;
                var local = at.DateTime;                                  // wall clock of the original offset
                rows.Add(new("Rfc3339Utc", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", inv)));
                rows.Add(new("Rfc3339Offset", at.Offset == TimeSpan.Zero ? at.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", inv) + "Z" : at.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv)));
                rows.Add(new("IsoBasic", at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss.FFFFFFF'Z'", inv)));
                rows.Add(new("IsoWeek", $"{ISOWeek.GetYear(local)}-W{ISOWeek.GetWeekOfYear(local):00}-{IsoWeekday(local.DayOfWeek)}"));
                rows.Add(new("IsoOrdinal", $"{local.Year:0000}-{local.DayOfYear:000}"));
                rows.Add(new("UnixSeconds", at.ToUnixTimeSeconds().ToString(inv)));
            }
            else
            {
                var d = p.Date;
                rows.Add(new("IsoDate", d.ToString("yyyy-MM-dd", inv)));
                rows.Add(new("IsoWeek", $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}-{IsoWeekday(d.DayOfWeek)}"));
                rows.Add(new("IsoOrdinal", $"{d.Year:0000}-{d.DayOfYear:000}"));
                rows.Add(new("Weekday", d.ToString("dddd", culture)));
                rows.Add(new("DayOfYear", d.DayOfYear.ToString(inv)));
            }

            return new DateToolIsoResult(p.Kind, iso.Length == 0, rfc.Length == 0, iso, rfc, p.DateBasic, assumed, p.LeapSecond, instant, rows);
        }

        private static DateToolIsoResult IsoFail(string code, DateToolIsoKind kind = DateToolIsoKind.None) =>
            new(kind, false, false, code, code, false, false, false, null, Array.Empty<DateToolRow>());

        // =================================================================
        //  Date and time
        // =================================================================

        private static IsoParts ParseDateTime(string text)
        {
            var p = new IsoParts();
            int sep = text.IndexOfAny(new[] { 'T', 't', ' ' });
            string datePart = sep < 0 ? text : text[..sep];
            string? timePart = sep < 0 ? null : text[(sep + 1)..];
            if (sep >= 0)
            {
                p.Separator = text[sep];
                if (timePart!.Length == 0) { p.Problem = "Unreadable"; return p; }
            }

            ParseDatePart(datePart, p);
            if (p.Problem.Length > 0) return p;
            if (timePart == null) return p;

            // zone: Z, +hh, +hhmm, +hh:mm at the very end
            string time = timePart;
            var z = ZoneAtEnd.Match(timePart);
            if (z.Success)
            {
                time = z.Groups["t"].Value;
                string zone = z.Groups["z"].Value;
                p.HasZone = true;
                if (zone is "Z" or "z")
                {
                    p.ZoneUtcLetter = true;
                    p.ZoneLower = zone == "z";
                }
                else
                {
                    string digits = zone[1..].Replace(":", "");
                    int hh = int.Parse(digits[..2], CultureInfo.InvariantCulture);
                    int mm = digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0;
                    if (hh > 23 || mm > 59) { p.Problem = "BadOffset"; return p; }
                    p.ZoneHasColon = zone.Contains(':');
                    p.ZoneHasMinutes = digits.Length >= 4;
                    p.Offset = (zone[0] == '-' ? -1 : 1) * new TimeSpan(hh, mm, 0);
                }
            }

            var m = TimeExtended.Match(time);
            p.TimeBasic = false;
            if (!m.Success)
            {
                m = TimeBasic.Match(time);
                p.TimeBasic = true;
            }
            if (!m.Success) { p.Problem = "Unreadable"; return p; }
            if (m.Groups["f"].Success && !m.Groups["s"].Success) { p.Problem = "Unreadable"; return p; }

            p.HasTime = true;
            p.Kind = DateToolIsoKind.DateTime;
            p.Hour = Num(m.Groups["h"]);
            p.Minute = m.Groups["mi"].Success ? Num(m.Groups["mi"]) : 0;
            p.TimeHasSeconds = m.Groups["s"].Success;
            p.Second = p.TimeHasSeconds ? Num(m.Groups["s"]) : 0;
            if (m.Groups["f"].Success)
            {
                p.TimeCommaFraction = m.Groups["sep"].Value == ",";
                string frac = m.Groups["f"].Value;
                p.FractionTicks = long.Parse((frac.Length > 7 ? frac[..7] : frac.PadRight(7, '0')), CultureInfo.InvariantCulture);
            }

            if (p.Minute > 59) { p.Problem = "BadMinute"; return p; }
            if (p.Second > 60) { p.Problem = "BadSecond"; return p; }
            if (p.Hour > 24 || (p.Hour == 24 && (p.Minute != 0 || p.Second != 0 || p.FractionTicks != 0))) { p.Problem = "BadHour"; return p; }
            if (p.Hour == 24)
            {
                p.TwentyFour = true;
                p.Hour = 0;
                p.Date = p.Date.AddDays(1);                  // 24:00 is the end of the day = midnight of the next
            }
            if (p.Second == 60)
            {
                p.LeapSecond = true;
                p.Second = 59;
                p.FractionTicks += TimeSpan.TicksPerSecond;  // 59 s + 1 s = the minute after: the leap second's instant
            }
            return p;
        }

        private static void ParseDatePart(string s, IsoParts p)
        {
            for (int i = 0; i < DatePatterns.Length; i++)
            {
                var m = DatePatterns[i].Match(s);
                if (!m.Success) continue;

                int year = Num(m.Groups["y"]);
                if (year < 1) { p.Problem = "BadYear"; return; }
                p.DateBasic = i is 1 or 4 or 5 or 7;
                p.DateReduced = i is 3 or 5 or 8 or 9;
                p.DateIsCalendarExtended = i == 0;

                switch (i)
                {
                    case 0 or 1 or 8:
                    {
                        int month = Num(m.Groups["m"]);
                        int day = m.Groups["d"].Success ? Num(m.Groups["d"]) : 1;
                        if (month is < 1 or > 12) { p.Problem = "BadMonth"; return; }
                        if (day < 1 || day > DateTime.DaysInMonth(year, month)) { p.Problem = "BadDay"; return; }
                        p.Date = new DateTime(year, month, day);
                        p.Kind = DateToolIsoKind.Date;
                        return;
                    }
                    case 2 or 3 or 4 or 5:
                    {
                        int week = Num(m.Groups["w"]);
                        int wd = m.Groups["wd"].Success ? Num(m.Groups["wd"]) : 1;
                        if (week < 1 || week > ISOWeek.GetWeeksInYear(year)) { p.Problem = "BadWeek"; return; }
                        if (wd is < 1 or > 7) { p.Problem = "BadWeekday"; return; }
                        p.Date = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays(wd - 1);
                        p.Kind = DateToolIsoKind.WeekDate;
                        return;
                    }
                    case 6 or 7:
                    {
                        int ordinal = Num(m.Groups["o"]);
                        if (!TryOrdinalDate(year, ordinal, out var date)) { p.Problem = "BadOrdinal"; return; }
                        p.Date = date;
                        p.Kind = DateToolIsoKind.OrdinalDate;
                        return;
                    }
                    default:
                        p.Date = new DateTime(year, 1, 1);
                        p.Kind = DateToolIsoKind.Date;
                        return;
                }
            }
            p.Problem = "Unreadable";
        }

        // =================================================================
        //  Durations and intervals
        // =================================================================

        private readonly record struct IsoDuration(decimal Years, decimal Months, decimal Weeks, decimal Days, decimal Hours, decimal Minutes, decimal Seconds, bool Negative)
        {
            public bool HasCalendarUnits => Years != 0 || Months != 0;
            public decimal ExactSeconds => (Weeks * 7 + Days) * 86_400m + Hours * 3_600m + Minutes * 60m + Seconds;
        }

        private static bool TryParseDuration(string text, out IsoDuration d, out string problem)
        {
            d = default;
            problem = "";
            var m = DurationPattern.Match(text);
            if (!m.Success) { problem = "Unreadable"; return false; }

            decimal Get(string name)
            {
                var g = m.Groups[name];
                return g.Success ? decimal.Parse(g.Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0m;
            }

            string[] names = { "Y", "Mo", "W", "D", "H", "Mi", "S" };
            var present = names.Where(n => m.Groups[n].Success).ToList();
            if (present.Count == 0) { problem = "EmptyDuration"; return false; }
            if (m.Groups["T"].Success && !present.Any(n => n is "H" or "Mi" or "S")) { problem = "EmptyDuration"; return false; }
            // Only the last (smallest) part may carry a fraction.
            for (int i = 0; i < present.Count - 1; i++)
                if (m.Groups[present[i]].Value.Contains('.') || m.Groups[present[i]].Value.Contains(',')) { problem = "FractionNotLast"; return false; }

            d = new IsoDuration(Get("Y"), Get("Mo"), Get("W"), Get("D"), Get("H"), Get("Mi"), Get("S"), m.Groups["neg"].Success);
            return true;
        }

        private static string DurationText(IsoDuration d)
        {
            string N(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var date = (d.Years != 0 ? N(d.Years) + "Y" : "") + (d.Months != 0 ? N(d.Months) + "M" : "") + (d.Weeks != 0 ? N(d.Weeks) + "W" : "") + (d.Days != 0 ? N(d.Days) + "D" : "");
            var time = (d.Hours != 0 ? N(d.Hours) + "H" : "") + (d.Minutes != 0 ? N(d.Minutes) + "M" : "") + (d.Seconds != 0 ? N(d.Seconds) + "S" : "");
            if (date.Length == 0 && time.Length == 0) time = "0S";
            return (d.Negative ? "-" : "") + "P" + date + (time.Length > 0 ? "T" + time : "");
        }

        private static DateToolIsoResult AnalyzeDuration(string text)
        {
            if (!TryParseDuration(text, out var d, out string problem)) return IsoFail(problem, DateToolIsoKind.Duration);

            var inv = CultureInfo.InvariantCulture;
            var rows = new List<DateToolRow> { new("IsoDuration", DurationText(d)) };
            if (!d.HasCalendarUnits)
            {
                rows.Add(new("DurTotalSeconds", (d.Negative ? -d.ExactSeconds : d.ExactSeconds).ToString("0.###", inv)));
                rows.Add(new("DurTotalDays", (d.ExactSeconds / 86_400m * (d.Negative ? -1 : 1)).ToString("0.######", inv)));
            }
            else
            {
                // Years and months differ in length: show an average, marked as one.
                decimal days = d.Years * 365.2425m + d.Months * 30.436875m + d.ExactSeconds / 86_400m;
                rows.Add(new("DurTotalDays", "≈ " + (days * (d.Negative ? -1 : 1)).ToString("0.##", inv)));
            }
            // RFC 3339 only defines timestamps (its appendix lists the duration grammar, but a duration is not a date-time).
            return new DateToolIsoResult(DateToolIsoKind.Duration, true, false, "", "NotTimestamp", false, false, false, null, rows);
        }

        private static DateToolIsoResult AnalyzeInterval(string text, CultureInfo culture)
        {
            var parts = text.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return IsoFail("Unreadable", DateToolIsoKind.Interval);

            bool aDur = parts[0].StartsWith('P') || parts[0].StartsWith("-P", StringComparison.Ordinal);
            bool bDur = parts[1].StartsWith('P') || parts[1].StartsWith("-P", StringComparison.Ordinal);
            if (aDur && bDur) return IsoFail("Unreadable", DateToolIsoKind.Interval);

            IsoDuration dur = default;
            if ((aDur && !TryParseDuration(parts[0], out dur, out string pa)) ) return IsoFail("Unreadable", DateToolIsoKind.Interval);
            if ((bDur && !TryParseDuration(parts[1], out dur, out string pb))) return IsoFail("Unreadable", DateToolIsoKind.Interval);

            DateTimeOffset? Moment(string s, out string code)
            {
                var r = AnalyzeIso(s, culture);
                code = r.IsoProblem;
                return r.Kind == DateToolIsoKind.DateTime && r.IsIso8601 ? r.Instant : null;
            }

            DateTimeOffset start, end;
            if (!aDur && !bDur)
            {
                if (Moment(parts[0], out _) is not { } s0 || Moment(parts[1], out _) is not { } e0) return IsoFail("Unreadable", DateToolIsoKind.Interval);
                (start, end) = (s0, e0);
            }
            else if (bDur)
            {
                if (Moment(parts[0], out _) is not { } s1) return IsoFail("Unreadable", DateToolIsoKind.Interval);
                if (dur.Years != Math.Truncate(dur.Years) || dur.Months != Math.Truncate(dur.Months)) return IsoFail("FractionalCalendarUnits", DateToolIsoKind.Interval);
                start = s1;
                end = AddDuration(s1, dur, 1);
            }
            else
            {
                if (Moment(parts[1], out _) is not { } e1) return IsoFail("Unreadable", DateToolIsoKind.Interval);
                if (dur.Years != Math.Truncate(dur.Years) || dur.Months != Math.Truncate(dur.Months)) return IsoFail("FractionalCalendarUnits", DateToolIsoKind.Interval);
                end = e1;
                start = AddDuration(e1, dur, -1);
            }

            var inv = CultureInfo.InvariantCulture;
            var length = end - start;
            var rows = new List<DateToolRow>
            {
                new("IntervalStart", start.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv)),
                new("IntervalEnd", end.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv)),
                new("IntervalLength", length.TotalDays.ToString("0.######", inv) + " d"),
            };
            return new DateToolIsoResult(DateToolIsoKind.Interval, true, false, "", "NotTimestamp", false, false, false, null, rows);
        }

        private static DateTimeOffset AddDuration(DateTimeOffset from, IsoDuration d, int direction)
        {
            direction *= d.Negative ? -1 : 1;
            try
            {
                var wall = from.DateTime.AddMonths(direction * (int)(d.Years * 12 + d.Months))
                                        .AddSeconds(direction * (double)d.ExactSeconds);
                return new DateTimeOffset(wall, from.Offset);
            }
            catch (ArgumentOutOfRangeException)
            {
                return from;
            }
        }
    }
}
