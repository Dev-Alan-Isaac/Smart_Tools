using Smart_Tools.WPF.Models;
using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    // Date & Time Toolkit - calendar weeks, the day of the year and the event days / holidays kept in
    // Settings. Pure: "today" and the culture are passed in, nothing is read from the clock here.
    public static partial class DateTimeToolkitManager
    {
        // =================================================================
        //  Week and day-of-year numbers
        // =================================================================

        /// <summary>
        /// Where a date falls: ISO week (Monday first, week 1 holds the first Thursday), the same week by the
        /// US rule (Sunday first, week 1 holds 1 January) and by the culture's own rule, the day of the year and the quarter.
        /// </summary>
        public static DateToolCalendarInfo CalendarInfo(DateTime date, CultureInfo culture)
        {
            date = date.Date;
            int isoYear = ISOWeek.GetYear(date);
            var monday = date.AddDays(-(IsoWeekday(date.DayOfWeek) - 1));
            int daysInYear = DateTime.IsLeapYear(date.Year) ? 366 : 365;
            var fmt = culture.DateTimeFormat;
            return new DateToolCalendarInfo(
                date,
                ISOWeek.GetWeekOfYear(date), isoYear, IsoWeekday(date.DayOfWeek), ISOWeek.GetWeeksInYear(isoYear),
                monday, monday.AddDays(6),
                date.DayOfYear, daysInYear, daysInYear - date.DayOfYear,
                (date.Month - 1) / 3 + 1,
                CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Sunday),
                culture.Calendar.GetWeekOfYear(date, fmt.CalendarWeekRule, fmt.FirstDayOfWeek),
                DateTime.IsLeapYear(date.Year));
        }

        /// <summary>The Monday of ISO week <paramref name="week"/> of <paramref name="isoYear"/>; false when that week doesn't exist (53 in a 52-week year).</summary>
        public static bool TryWeekStart(int isoYear, int week, out DateTime monday)
        {
            monday = default;
            if (isoYear is < 1 or > 9998 || week < 1 || week > ISOWeek.GetWeeksInYear(isoYear)) return false;
            monday = ISOWeek.ToDateTime(isoYear, week, DayOfWeek.Monday);
            return true;
        }

        /// <summary>Day number <paramref name="day"/> of <paramref name="year"/> (1 to 365 or 366) as a date.</summary>
        public static bool TryOrdinalDate(int year, int day, out DateTime date)
        {
            date = default;
            if (year is < 1 or > 9999 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) return false;
            date = new DateTime(year, 1, 1).AddDays(day - 1);
            return true;
        }

        // =================================================================
        //  Event days and holidays
        // =================================================================

        /// <summary>A repeating 29 February falls on the 28th in years that have no 29th.</summary>
        private static DateTime InYear(DateToolEvent e, int year)
        {
            int day = e.Date.Month == 2 && e.Date.Day == 29 && !DateTime.IsLeapYear(year) ? 28 : e.Date.Day;
            return new DateTime(year, e.Date.Month, day);
        }

        public static bool OccursOn(DateToolEvent e, DateTime day) =>
            e.Repeats ? InYear(e, day.Year).Date == day.Date : e.Date.Date == day.Date;

        /// <summary>The events that fall on this day, in the order given.</summary>
        public static List<DateToolEvent> EventsOn(DateTime day, IReadOnlyList<DateToolEvent>? events) =>
            events == null ? new List<DateToolEvent>() : events.Where(e => OccursOn(e, day)).ToList();

        /// <summary>Saturday, Sunday, or a day marked non-working in the event list.</summary>
        public static bool IsNonWorkingDay(DateTime day, IReadOnlyList<DateToolEvent>? events) =>
            day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            || (events != null && events.Any(e => e.NonWorking && OccursOn(e, day)));

        /// <summary>The next date on or after <paramref name="from"/> that the event falls on (null for a one-off in the past).</summary>
        public static DateTime? NextOccurrence(DateToolEvent e, DateTime from)
        {
            from = from.Date;
            if (!e.Repeats) return e.Date.Date >= from ? e.Date.Date : null;
            for (int year = from.Year; year <= from.Year + 1 && year <= 9999; year++)
            {
                var d = InYear(e, year);
                if (d >= from) return d;
            }
            return null;
        }

        /// <summary>The next <paramref name="count"/> event days from <paramref name="from"/> (today included), soonest first.</summary>
        public static List<DateToolUpcoming> Upcoming(DateTime from, IReadOnlyList<DateToolEvent>? events, int count)
        {
            var list = new List<DateToolUpcoming>();
            if (events == null) return list;
            foreach (var e in events)
                if (NextOccurrence(e, from) is { } d)
                    list.Add(new DateToolUpcoming(e, d, (d - from.Date).Days));
            return list.OrderBy(u => u.Date).ThenBy(u => u.Event.Name, StringComparer.CurrentCultureIgnoreCase).Take(Math.Max(0, count)).ToList();
        }

        /// <summary>
        /// Monday-to-Friday dates from <paramref name="a"/> up to (not including) the date of <paramref name="b"/>, minus the
        /// non-working event days that fall on one of those weekdays (each date counted once, however many events share it).
        /// </summary>
        public static long CountBusinessDays(DateTime a, DateTime b, IReadOnlyList<DateToolEvent>? events)
        {
            long count = CountBusinessDays(a, b);
            if (count == 0 || events == null) return count;

            var holidays = new HashSet<DateTime>();
            var first = a.Date;
            var last = b.Date.AddDays(-1);
            foreach (var e in events.Where(e => e.NonWorking))
            {
                if (!e.Repeats)
                {
                    if (e.Date.Date >= first && e.Date.Date <= last) holidays.Add(e.Date.Date);
                    continue;
                }
                for (int year = first.Year; year <= last.Year; year++)
                {
                    var d = InYear(e, year);
                    if (d >= first && d <= last) holidays.Add(d);
                }
            }
            return count - holidays.Count(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        }
    }
}
