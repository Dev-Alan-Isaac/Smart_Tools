namespace Smart_Tools.WPF.Models
{
    // Date & Time Toolkit - plain data only. The maths, parsing and zone handling
    // live in Controllers/DateTimeToolkitManager.cs; the view only shows the result.
    // Types start with "DateTool" because every tool shares this namespace.

    public enum DateToolMode { Unix, Zones, Difference, Iso, Calendar }

    /// <summary>What a typed number counts: seconds, milliseconds ... since 1 Jan 1970 UTC. Auto picks by size.</summary>
    public enum DateToolUnit { Auto, Seconds, Milliseconds, Microseconds, Nanoseconds }

    public enum DateToolRelativeUnit { Seconds, Minutes, Hours, Days, Months, Years }

    /// <summary>"3 days ago" / "in 2 hours" as numbers; the view turns it into words in the current language.</summary>
    public readonly record struct DateToolRelative(bool IsFuture, long Value, DateToolRelativeUnit Unit);

    /// <summary>One line of a result. <see cref="Key"/> names the label in the language files; the view fills <see cref="Label"/>.</summary>
    public sealed record DateToolRow(string Key, string Value, string Label = "");

    /// <summary>
    /// What the Unix / date box was understood as. <see cref="ErrorCode"/>: Empty, Unreadable, OutOfRange
    /// (empty when <see cref="Ok"/>).
    /// </summary>
    public sealed record DateToolConversion(
        bool Ok,
        string ErrorCode,
        bool FromNumber,
        DateToolUnit UnitUsed,
        DateTimeOffset Instant);

    /// <summary>One zone card in the Time Zones view.</summary>
    public sealed record DateToolZoneItem(
        string Id,
        string Name,
        string Time,
        string Date,
        string Offset,
        bool IsDaylightTime,
        bool IsLocal,
        int DayDelta);          // -1 yesterday, 0 same day, +1 tomorrow, compared with the zone the time was typed in

    /// <summary>An entry of the "add a zone" list.</summary>
    public sealed record DateToolZoneOption(string Id, string Label);

    /// <summary>
    /// The gap between two moments. The years / months / days ... are calendar parts (always positive);
    /// <see cref="IsNegative"/> says the end is before the start.
    /// </summary>
    public sealed record DateToolDifference(
        bool IsNegative,
        int Years, int Months, int Days, int Hours, int Minutes, int Seconds,
        double TotalWeeks, double TotalDays, double TotalHours, double TotalMinutes, double TotalSeconds,
        long BusinessDays);

    /// <summary>The "add / subtract" amounts. All are counts; the sign is applied separately.</summary>
    public sealed record DateToolAmounts(
        long Years, long Months, long Weeks, long Days, long BusinessDays,
        long Hours, long Minutes, long Seconds);

    /// <summary><see cref="ErrorCode"/>: OutOfRange (the result would be before year 1 or after year 9999).</summary>
    public sealed record DateToolAddResult(bool Ok, string ErrorCode, DateTime Result);

    /// <summary>
    /// An event day or holiday the user keeps in Settings. <see cref="Repeats"/> = every year (the year is ignored);
    /// <see cref="NonWorking"/> = business-day counts skip it.
    /// </summary>
    public sealed record DateToolEvent(string Name, DateTime Date, bool Repeats, bool NonWorking);

    /// <summary>The next time an event day comes round: <see cref="DaysAway"/> is 0 for today.</summary>
    public sealed record DateToolUpcoming(DateToolEvent Event, DateTime Date, int DaysAway);

    /// <summary>Where a date falls in its week, month, quarter and year.</summary>
    public sealed record DateToolCalendarInfo(
        DateTime Date,
        int IsoWeek, int IsoWeekYear, int IsoWeekday, int WeeksInIsoYear,
        DateTime WeekStart, DateTime WeekEnd,
        int DayOfYear, int DaysInYear, int DaysLeft,
        int Quarter, int UsWeek, int CultureWeek,
        bool IsLeapYear);

    /// <summary>What kind of ISO 8601 text was typed.</summary>
    public enum DateToolIsoKind { None, Date, WeekDate, OrdinalDate, DateTime, Duration, Interval }

    /// <summary>
    /// What an ISO 8601 / RFC 3339 text was understood as. <see cref="IsoProblem"/> / <see cref="RfcProblem"/> are codes
    /// ("" when valid): the view turns them into sentences. <see cref="Instant"/> is set when the text names one moment
    /// (a time without an offset is read as UTC and <see cref="OffsetAssumed"/> says so).
    /// </summary>
    public sealed record DateToolIsoResult(
        DateToolIsoKind Kind,
        bool IsIso8601,
        bool IsRfc3339,
        string IsoProblem,
        string RfcProblem,
        bool Basic,
        bool OffsetAssumed,
        bool LeapSecond,
        DateTimeOffset? Instant,
        IReadOnlyList<DateToolRow> Rows);

    /// <summary>Remembered between sessions: how the tool is set up, never what was typed.</summary>
    public sealed class DateToolSettings
    {
        public DateToolMode Mode { get; set; } = DateToolMode.Unix;
        public DateToolUnit Unit { get; set; } = DateToolUnit.Auto;
        public bool TreatDatesAsUtc { get; set; }
        public bool Use24HourClock { get; set; } = true;
        public bool OptionsCollapsed { get; set; }
        public List<string>? ZoneIds { get; set; }

        /// <summary>Business-day counts and "add working days" skip the non-working event days from Settings.</summary>
        public bool SkipEventDays { get; set; } = true;
    }
}
