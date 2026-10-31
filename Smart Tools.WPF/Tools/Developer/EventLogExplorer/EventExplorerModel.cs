namespace Smart_Tools.WPF.Models
{
    // Event Log Explorer - plain data only (no WPF types, no I/O).

    /// <summary>Severity as Windows Event Viewer shows it. "Always log" (0) counts as Information.</summary>
    public enum EventExplorerLevel
    {
        Critical = 1,
        Error = 2,
        Warning = 3,
        Information = 4,
        Verbose = 5,
    }

    /// <summary>How far back to read.</summary>
    public enum EventExplorerTimeRange
    {
        LastHour,
        Last24Hours,
        Last7Days,
        Last30Days,
        All,
    }

    public enum EventExplorerLogGroup
    {
        /// <summary>Application, System, Security, Setup, Forwarded Events.</summary>
        Windows,

        /// <summary>Everything under "Applications and Services Logs".</summary>
        Other,
    }

    public enum EventExplorerLogStatus
    {
        Ok,

        /// <summary>Windows refused to open it for this user (the Security log, for one).</summary>
        NeedsAdmin,
    }

    public enum EventExplorerError
    {
        None,
        AccessDenied,
        NotFound,
        Other,
    }

    public enum EventExplorerExportFormat
    {
        Csv,
        Json,
        Evtx,
    }

    /// <summary>One log in the picker.</summary>
    public sealed class EventExplorerLogInfo
    {
        public required string Name { get; init; }
        public EventExplorerLogGroup Group { get; init; }
        public EventExplorerLogStatus Status { get; init; }

        /// <summary>Events stored in it right now (null when it could not be read).</summary>
        public long? RecordCount { get; init; }

        public DateTime? LastWrite { get; init; }

        public bool NeedsAdmin => Status == EventExplorerLogStatus.NeedsAdmin;

        public bool IsEmpty => Status == EventExplorerLogStatus.Ok && RecordCount is 0;

        /// <summary>
        /// "Microsoft-Windows-Kernel-Power/Thermal-Operational" as "Kernel-Power › Thermal-Operational":
        /// many logs end in "Operational", so the last part alone would not tell them apart.
        /// </summary>
        public string DisplayName
        {
            get
            {
                string text = Name;
                foreach (string prefix in new[] { "Microsoft-Windows-", "Microsoft-" })
                {
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[prefix.Length..];
                        break;
                    }
                }
                return text.Replace("/", " › ");
            }
        }

        /// <summary>"24,422 events", or the admin note; Windows logs are labelled as such.</summary>
        public string Hint
        {
            get
            {
                string count = RecordCount is { } n ? $"{n:N0} events" : NeedsAdmin ? "Needs administrator rights" : "";
                if (Group != EventExplorerLogGroup.Windows) return count;
                return count.Length == 0 ? "Windows log" : "Windows log · " + count;
            }
        }
    }

    /// <summary>One event, already formatted for the list.</summary>
    public sealed class EventExplorerEntry
    {
        public required string LogName { get; init; }
        public long? RecordId { get; init; }
        public DateTime TimeCreated { get; init; }
        public EventExplorerLevel Level { get; init; }

        /// <summary>"Error", "Warning", "Audit Failure" ...</summary>
        public string LevelText { get; init; } = "";

        public int EventId { get; init; }
        public string Provider { get; init; } = "";
        public string Computer { get; init; } = "";
        public string Message { get; init; } = "";

        public string TimeText => TimeCreated.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>First line of the message, for the list row.</summary>
        public string Summary { get; init; } = "";
    }

    /// <summary>The pieces of one event that are too slow to read for every row.</summary>
    public sealed class EventExplorerDetails
    {
        /// <summary>Indented XML exactly as Windows stores the event.</summary>
        public string Xml { get; init; } = "";

        /// <summary>Label / value pairs (user, task, process ...).</summary>
        public List<KeyValuePair<string, string>> Fields { get; init; } = new();

        /// <summary>The event's own data items (EventData / UserData).</summary>
        public List<KeyValuePair<string, string>> Data { get; init; } = new();
    }

    /// <summary>What to read: one log, filtered.</summary>
    public sealed class EventExplorerQuery
    {
        public required string LogName { get; init; }
        public HashSet<EventExplorerLevel> Levels { get; init; } = new();
        public DateTime? FromLocal { get; init; }
        public DateTime? ToLocal { get; init; }

        /// <summary>"41, 1000-1005, -7" (Event Viewer style; a leading minus excludes).</summary>
        public string EventIds { get; init; } = "";

        /// <summary>Case-insensitive "contains" on the source name.</summary>
        public string Source { get; init; } = "";

        /// <summary>Case-insensitive "contains" on the message, source and Event ID.</summary>
        public string Text { get; init; } = "";

        public int MaxEvents { get; init; } = 1000;
    }

    public sealed class EventExplorerProgress
    {
        public int Scanned { get; init; }
        public int Matched { get; init; }
    }

    public sealed class EventExplorerResult
    {
        public List<EventExplorerEntry> Entries { get; init; } = new();

        /// <summary>Events looked at (more than <see cref="Entries"/> when a text filter dropped some).</summary>
        public int Scanned { get; init; }

        /// <summary>Stopped at the limit - there are more.</summary>
        public bool Truncated { get; init; }

        public bool Cancelled { get; init; }
        public TimeSpan Elapsed { get; init; }
        public EventExplorerError Error { get; init; }
        public string ErrorText { get; init; } = "";
    }

    /// <summary>Parsed Event ID filter: ranges to include and to exclude.</summary>
    public sealed class EventIdFilter
    {
        public List<(int From, int To)> Include { get; } = new();
        public List<(int From, int To)> Exclude { get; } = new();

        public bool IsEmpty => Include.Count == 0 && Exclude.Count == 0;

        public bool Allows(int id)
        {
            foreach (var (from, to) in Exclude)
                if (id >= from && id <= to) return false;
            if (Include.Count == 0) return true;
            foreach (var (from, to) in Include)
                if (id >= from && id <= to) return true;
            return false;
        }
    }

    /// <summary>Saved between sessions (%AppData%\Smart Tools\event_log_explorer.json).</summary>
    public sealed class EventExplorerSettings
    {
        public string LastLog { get; set; } = "Application";
        public EventExplorerTimeRange TimeRange { get; set; } = EventExplorerTimeRange.Last24Hours;
        public List<EventExplorerLevel> Levels { get; set; } = new()
        {
            EventExplorerLevel.Critical, EventExplorerLevel.Error, EventExplorerLevel.Warning,
            EventExplorerLevel.Information, EventExplorerLevel.Verbose,
        };
        public int MaxEvents { get; set; } = 1000;
        public bool HideEmptyLogs { get; set; } = true;

        /// <summary>0 = off.</summary>
        public int LiveSeconds { get; set; }

        public bool ShowDetails { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }
}
