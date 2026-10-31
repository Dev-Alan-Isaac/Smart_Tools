using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Date &amp; Time Toolkit logic. No WPF here, so it compiles in a plain net8.0
    /// project. Three jobs: turn Unix timestamps and dates into each other,
    /// show one moment in several time zones, and do calendar maths (the gap
    /// between two dates, adding or taking away years, days, working days ...).
    ///
    /// "Now" and the culture are always passed in, never read from the clock inside
    /// the maths, so every result can be tested on fixed dates.
    /// </summary>
    public static partial class DateTimeToolkitManager
    {
        public const int MaxZones = 12;

        // =================================================================
        //  Unix timestamps and dates
        // =================================================================

        /// <summary>
        /// Understands what was typed: a number (a Unix timestamp), "now", or a date and time in
        /// any common layout ("2024-03-05 14:30", "5 Mar 2024", "2024-03-05T14:30:00Z").
        /// A date without a zone counts as UTC when <paramref name="assumeUtc"/>, otherwise as the PC's own time.
        /// </summary>
        public static DateToolConversion Convert(string? input, DateToolUnit unit, bool assumeUtc, CultureInfo culture, DateTimeOffset now)
        {
            string text = (input ?? "").Trim();
            if (text.Length == 0) return Fail("Empty");
            if (IsNowWord(text)) return new DateToolConversion(true, "", false, DateToolUnit.Auto, now);

            if (TryParseNumber(text, out decimal number))
            {
                var used = unit == DateToolUnit.Auto ? DetectUnit(number) : unit;
                decimal ticks = number / UnitDivisor(used) * TimeSpan.TicksPerSecond;
                decimal min = -DateTime.UnixEpoch.Ticks, max = DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks;
                if (ticks < min || ticks > max) return Fail("OutOfRange", true, used);
                return new DateToolConversion(true, "", true, used,
                    new DateTimeOffset(DateTime.UnixEpoch.Ticks + (long)Math.Round(ticks), TimeSpan.Zero));
            }

            var style = assumeUtc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
            if (DateTimeOffset.TryParse(text, culture, style, out var parsed)
                || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, style, out parsed))
                return new DateToolConversion(true, "", false, DateToolUnit.Auto, parsed.ToUniversalTime());

            return Fail("Unreadable");
        }

        /// <summary>"now" in the languages the app speaks.</summary>
        private static bool IsNowWord(string t) =>
            t.Equals("now", StringComparison.OrdinalIgnoreCase) || t.Equals("ahora", StringComparison.OrdinalIgnoreCase);

        private static DateToolConversion Fail(string code, bool fromNumber = false, DateToolUnit unit = DateToolUnit.Auto) =>
            new(false, code, fromNumber, unit, default);

        /// <summary>Plain digits (a minus sign, one decimal point and thousands commas are fine). Dates never match.</summary>
        private static bool TryParseNumber(string text, out decimal value)
        {
            string t = text.Replace("_", "").Replace(" ", "");
            // 1,700,000,000 -> 1700000000, but 1,5 is not a thousands number
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$")) t = t.Replace(",", "");
            return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Seconds (up to year 5138), milliseconds, microseconds or nanoseconds, by the size of the number.
        /// A very small millisecond value (before March 1973) reads as seconds; pick the unit by hand for those.
        /// </summary>
        public static DateToolUnit DetectUnit(decimal value)
        {
            decimal abs = Math.Abs(Math.Truncate(value));
            if (abs < 100_000_000_000m) return DateToolUnit.Seconds;
            if (abs < 100_000_000_000_000m) return DateToolUnit.Milliseconds;
            if (abs < 100_000_000_000_000_000m) return DateToolUnit.Microseconds;
            return DateToolUnit.Nanoseconds;
        }

        private static decimal UnitDivisor(DateToolUnit unit) => unit switch
        {
            DateToolUnit.Milliseconds => 1_000m,
            DateToolUnit.Microseconds => 1_000_000m,
            DateToolUnit.Nanoseconds => 1_000_000_000m,
            _ => 1m,
        };

        /// <summary>
        /// The moment written out in the ways people ask for. Keys match Str_DateTimeTool_Row_&lt;Key&gt; in the language files.
        /// </summary>
        public static List<DateToolRow> Describe(DateTimeOffset instant, CultureInfo culture, TimeZoneInfo? local = null)
        {
            local ??= TimeZoneInfo.Local;
            var inLocal = TimeZoneInfo.ConvertTime(instant, local);
            var inv = CultureInfo.InvariantCulture;
            double julian = instant.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay + 1721425.5;

            return new List<DateToolRow>
            {
                new("UnixSeconds", instant.ToUnixTimeSeconds().ToString(inv)),
                new("UnixMillis", instant.ToUnixTimeMilliseconds().ToString(inv)),
                new("Utc", instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", inv)),
                new("Local", inLocal.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv)),
                new("LongDate", inLocal.ToString("dddd, d MMMM yyyy  HH:mm:ss", culture)),
                new("Rfc1123", instant.UtcDateTime.ToString("R", inv)),
                new("DayOfYear", inLocal.DayOfYear.ToString(inv)),
                new("IsoWeek", $"{ISOWeek.GetYear(inLocal.DateTime)}-W{ISOWeek.GetWeekOfYear(inLocal.DateTime):00}-{IsoWeekday(inLocal.DayOfWeek)}"),
                new("JulianDay", julian.ToString("0.00000", inv)),
            };
        }

        private static int IsoWeekday(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

        /// <summary>How far the moment is from now, as a number and a unit (rounded down).</summary>
        public static DateToolRelative Relative(DateTimeOffset instant, DateTimeOffset now)
        {
            var diff = instant - now;
            bool future = diff > TimeSpan.Zero;
            double secs = Math.Abs(diff.TotalSeconds);
            if (secs < 60) return new(future, (long)secs, DateToolRelativeUnit.Seconds);
            if (secs < 3600) return new(future, (long)(secs / 60), DateToolRelativeUnit.Minutes);
            if (secs < 86_400) return new(future, (long)(secs / 3600), DateToolRelativeUnit.Hours);
            double days = secs / 86_400;
            if (days < 30) return new(future, (long)days, DateToolRelativeUnit.Days);
            if (days < 365) return new(future, (long)(days / 30), DateToolRelativeUnit.Months);
            return new(future, (long)(days / 365), DateToolRelativeUnit.Years);
        }

        // =================================================================
        //  Time zones
        // =================================================================

        public static TimeZoneInfo? FindZone(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
        }

        /// <summary>Every zone the PC knows, from west to east.</summary>
        public static List<DateToolZoneOption> GetZoneOptions() =>
            TimeZoneInfo.GetSystemTimeZones()
                .OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(z => new DateToolZoneOption(z.Id, z.DisplayName))
                .ToList();

        /// <summary>UTC, the PC's own zone and a few big ones - only those this PC has.</summary>
        public static List<string> DefaultZoneIds()
        {
            var wanted = new[] { "UTC", TimeZoneInfo.Local.Id, "Eastern Standard Time", "GMT Standard Time", "Tokyo Standard Time", "AUS Eastern Standard Time" };
            return wanted.Where(id => FindZone(id) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Eastern Time (US &amp; Canada)" from "(UTC-05:00) Eastern Time (US &amp; Canada)".</summary>
        public static string ZoneName(TimeZoneInfo zone)
        {
            if (zone.Id == "UTC") return "UTC";
            string name = zone.DisplayName;
            int close = name.IndexOf(") ", StringComparison.Ordinal);
            return name.StartsWith('(') && close > 0 ? name[(close + 2)..] : name;
        }

        public static string OffsetText(TimeSpan offset) =>
            "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString("00") + ":" + Math.Abs(offset.Minutes).ToString("00");

        /// <summary>The moment as it reads on the wall of <paramref name="zone"/>.</summary>
        public static DateToolZoneItem ZoneItem(DateTimeOffset instant, TimeZoneInfo zone, TimeZoneInfo reference, bool use24Hour, CultureInfo culture)
        {
            var at = TimeZoneInfo.ConvertTime(instant, zone);
            var refDate = TimeZoneInfo.ConvertTime(instant, reference).Date;
            string time = at.ToString(use24Hour ? "HH:mm:ss" : "h:mm:ss tt", culture);
            return new DateToolZoneItem(
                zone.Id,
                ZoneName(zone),
                time,
                at.ToString("ddd, d MMM yyyy", culture),
                OffsetText(at.Offset),
                zone.IsDaylightSavingTime(at),
                zone.Id == TimeZoneInfo.Local.Id,
                Math.Sign((at.Date - refDate).Days));
        }

        /// <summary>
        /// Reads a date and time typed for <paramref name="from"/>. Empty means "now". A text that carries its own
        /// zone or offset ("...Z", "+02:00") keeps it. <paramref name="errorCode"/>: Unreadable, InvalidTime
        /// (a clock time that doesn't exist because the clocks jumped forward). <paramref name="ambiguous"/> is true
        /// when the clock time happens twice (clocks went back); the standard-time reading is used.
        /// </summary>
        public static bool TryZoneInstant(string? text, TimeZoneInfo from, CultureInfo culture, DateTimeOffset now,
                                          out DateTimeOffset instant, out string errorCode, out bool ambiguous)
        {
            instant = now;
            errorCode = "";
            ambiguous = false;
            string t = (text ?? "").Trim();
            if (t.Length == 0 || IsNowWord(t)) return true;

            if (!DateTime.TryParse(t, culture, DateTimeStyles.RoundtripKind, out var dt)
                && !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
            {
                errorCode = "Unreadable";
                return false;
            }

            if (dt.Kind == DateTimeKind.Utc) { instant = new DateTimeOffset(dt); return true; }
            if (dt.Kind == DateTimeKind.Local) { instant = new DateTimeOffset(dt.ToUniversalTime()); return true; }

            if (from.IsInvalidTime(dt)) { errorCode = "InvalidTime"; return false; }
            ambiguous = from.IsAmbiguousTime(dt);
            instant = new DateTimeOffset(dt, from.GetUtcOffset(dt)).ToUniversalTime();
            return true;
        }

        // =================================================================
        //  Calendar maths
        // =================================================================

        /// <summary>Reads a date and time for the calendar maths. "now" and "today" work. No zone is involved.</summary>
        public static bool TryParseDateTime(string? text, CultureInfo culture, DateTime now, out DateTime value)
        {
            string t = (text ?? "").Trim();
            value = default;
            if (t.Length == 0) return false;
            if (IsNowWord(t)) { value = new DateTime(now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond); return true; }
            if (t.Equals("today", StringComparison.OrdinalIgnoreCase) || t.Equals("hoy", StringComparison.OrdinalIgnoreCase)) { value = now.Date; return true; }
            if (!DateTime.TryParse(t, culture, DateTimeStyles.AllowWhiteSpaces, out var dt)
                && !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
                return false;
            value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return true;
        }

        /// <summary>
        /// The gap between two moments as whole years, months, days, hours ... (the way a person counts it:
        /// 31 Jan to 1 Mar is 1 month 1 day), plus the same gap as totals. Order doesn't matter;
        /// <see cref="DateToolDifference.IsNegative"/> says the end came first.
        /// </summary>
        public static DateToolDifference Difference(DateTime start, DateTime end, IReadOnlyList<DateToolEvent>? events = null)
        {
            bool negative = end < start;
            var (a, b) = negative ? (end, start) : (start, end);

            // Count whole months from the start date itself (not from a year-shifted copy: 29 Feb + 23 years
            // clamps to 28 Feb and would leave 12 "extra" months), then split them into years and months.
            int totalMonths = (b.Year - a.Year) * 12 + b.Month - a.Month;
            if (a.AddMonths(totalMonths) > b) totalMonths--;
            int years = totalMonths / 12, months = totalMonths % 12;
            var rest = b - a.AddMonths(totalMonths);

            var total = b - a;
            return new DateToolDifference(
                negative, years, months, rest.Days, rest.Hours, rest.Minutes, rest.Seconds,
                total.TotalDays / 7, total.TotalDays, total.TotalHours, total.TotalMinutes, total.TotalSeconds,
                CountBusinessDays(a, b, events));
        }

        /// <summary>Monday to Friday dates from <paramref name="a"/> up to (not including) the date of <paramref name="b"/>.</summary>
        public static long CountBusinessDays(DateTime a, DateTime b)
        {
            long days = (b.Date - a.Date).Days;
            if (days <= 0) return 0;
            long weeks = days / 7, rest = days % 7;
            long count = weeks * 5;
            var d = a.Date.AddDays(weeks * 7);
            for (long i = 0; i < rest; i++, d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
            return count;
        }

        /// <summary>
        /// Adds (<paramref name="sign"/> = 1) or takes away (-1) the amounts. Months and years first (31 Jan + 1 month
        /// is the last day of February), then weeks and days, then working days (weekends and the non-working event days in
        /// <paramref name="events"/> skipped), then the time of day.
        /// </summary>
        public static DateToolAddResult Add(DateTime start, int sign, DateToolAmounts a, IReadOnlyList<DateToolEvent>? events = null)
        {
            sign = sign < 0 ? -1 : 1;
            const long Limit = 4_000_000;       // more working days than the calendar holds
            if (Math.Abs(a.BusinessDays) > Limit || Math.Abs(a.Years) > 20_000 || Math.Abs(a.Months) > 240_000)
                return new DateToolAddResult(false, "OutOfRange", default);
            try
            {
                var d = start.AddMonths(checked((int)(sign * (a.Years * 12 + a.Months))));
                d = d.AddDays(sign * (double)(a.Weeks * 7 + a.Days));

                long steps = sign * a.BusinessDays;
                int dir = steps < 0 ? -1 : 1;
                for (long i = Math.Abs(steps); i > 0; i--)
                {
                    do { d = d.AddDays(dir); } while (IsNonWorkingDay(d, events));
                }

                d = d.AddHours(sign * (double)a.Hours).AddMinutes(sign * (double)a.Minutes).AddSeconds(sign * (double)a.Seconds);
                return new DateToolAddResult(true, "", d);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                return new DateToolAddResult(false, "OutOfRange", default);
            }
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\date_time_toolkit.json - the setup only, never what was typed)
        // =================================================================

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "date_time_toolkit.json");

        public static DateToolSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return Fresh();
                var s = JsonSerializer.Deserialize<DateToolSettings>(File.ReadAllText(file), SettingsJson) ?? Fresh();
                if (!Enum.IsDefined(s.Mode)) s.Mode = DateToolMode.Unix;
                if (!Enum.IsDefined(s.Unit)) s.Unit = DateToolUnit.Auto;
                var ids = (s.ZoneIds ?? new List<string>()).Where(id => FindZone(id) != null).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxZones).ToList();
                s.ZoneIds = ids.Count > 0 ? ids : DefaultZoneIds();
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return Fresh();
            }
        }

        private static DateToolSettings Fresh() => new() { ZoneIds = DefaultZoneIds() };

        public static void SaveSettings(DateToolSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJson));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
