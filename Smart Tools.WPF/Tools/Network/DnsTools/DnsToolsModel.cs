using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  DNS lookup, WHOIS and traceroute - plain data only (no WPF, no I/O).
    //  Every type carries a "Dns", "Whois" or "Trace" prefix because all tools share this namespace.
    // =====================================================================

    public enum DnsToolMode
    {
        Dns,
        Whois,
        Trace,
    }

    /// <summary>The record types that can be asked for. The numbers are the ones DNS uses on the wire.</summary>
    public enum DnsRecordType : ushort
    {
        A = 1,
        NS = 2,
        CNAME = 5,
        SOA = 6,
        PTR = 12,
        MX = 15,
        TXT = 16,
        AAAA = 28,
        SRV = 33,
        CAA = 257,
    }

    /// <summary>How a lookup ended. The view turns each into a plain sentence.</summary>
    public enum DnsLookupStatus
    {
        Ok,
        NoRecords,        // the name exists, but has no record of that type
        NameNotFound,     // NXDOMAIN
        ServerFailure,    // SERVFAIL: the server couldn't answer
        Refused,          // the server won't answer this client
        Timeout,
        Unreachable,      // no DNS server to ask, or it can't be reached
        BadName,          // not a valid host name or address
        BadReply,
    }

    /// <summary>One answer line.</summary>
    public sealed record DnsRecord(DnsRecordType Type, string Name, int Ttl, string Value)
    {
        public string TypeText => Type.ToString();
        public string TtlText => Ttl.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    public sealed class DnsLookupResult
    {
        public DnsLookupStatus Status { get; init; }
        public string Server { get; init; } = "";
        public List<DnsRecord> Records { get; init; } = new();
        public TimeSpan Elapsed { get; init; }

        /// <summary>The name that was really asked: the reverse name when an address was typed.</summary>
        public string QueryName { get; init; } = "";

        public bool IsReverse { get; init; }
        public string Detail { get; init; } = "";
    }

    /// <summary>A DNS server the person can ask. Id "system" means the ones Windows is using.</summary>
    public sealed record DnsServerChoice(string Id, string Name, string Address);

    // ---------------------------------------------------------------------
    //  WHOIS
    // ---------------------------------------------------------------------

    public enum WhoisStatus
    {
        Ok,
        BadQuery,
        NoServer,      // the registry for this ending doesn't run a WHOIS server
        NotFound,      // the server answered that it has no such name
        Failed,        // network trouble
    }

    public enum WhoisFieldKind
    {
        Registrar,
        Created,
        Updated,
        Expires,
        NameServer,
        Status,
        Organization,
        Country,
        NetName,
        Range,
    }

    public sealed record WhoisField(WhoisFieldKind Kind, string Value);

    public sealed class WhoisResult
    {
        public WhoisStatus Status { get; init; }
        public string Query { get; init; } = "";

        /// <summary>The server whose answer is shown (after following a referral).</summary>
        public string Server { get; init; } = "";

        public string Raw { get; init; } = "";
        public List<WhoisField> Fields { get; init; } = new();

        /// <summary>When the name stops being registered, when the text says so in a date we can read.</summary>
        public DateTimeOffset? ExpiryDate { get; init; }

        public string Detail { get; init; } = "";
    }

    // ---------------------------------------------------------------------
    //  Traceroute
    // ---------------------------------------------------------------------

    /// <summary>One hop of a trace; the three probes' times are shown in separate columns.</summary>
    public sealed class TraceHop : INotifyPropertyChanged
    {
        private string _hostName = "";

        public int Number { get; init; }

        /// <summary>The router's address, or empty when none of the probes were answered.</summary>
        public string Address { get; init; } = "";

        /// <summary>Round-trip times in milliseconds, one per probe; null = no answer.</summary>
        public long?[] Times { get; init; } = Array.Empty<long?>();

        public bool IsDestination { get; init; }

        public string HostName
        {
            get => _hostName;
            set
            {
                if (_hostName == value) return;
                _hostName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
            }
        }

        /// <summary>"router.example.net" over its address, or just the address, or a star when silent.</summary>
        public string Display => Address.Length == 0 ? "*" : HostName.Length > 0 && HostName != Address ? $"{HostName} ({Address})" : Address;

        public string Time1 => Show(0);
        public string Time2 => Show(1);
        public string Time3 => Show(2);

        public int Lost => Times.Count(t => t == null);

        /// <summary>The average of the probes that were answered, or empty.</summary>
        public string Average
        {
            get
            {
                var answered = Times.Where(t => t != null).Select(t => t!.Value).ToList();
                if (answered.Count == 0) return "";
                double average = answered.Average();
                return average < 1 ? "<1 ms" : $"{average:0} ms";
            }
        }

        public bool Silent => Address.Length == 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string Show(int i) => i < Times.Length && Times[i] is { } ms ? (ms <= 0 ? "<1 ms" : $"{ms} ms") : "*";
    }

    public enum TraceStatus
    {
        Reached,
        NotReached,     // ran out of hops, or was stopped, before the destination answered
        BadHost,
    }

    public sealed class TraceSummary
    {
        public TraceStatus Status { get; init; }
        public string Target { get; init; } = "";
        public int HopCount { get; init; }
    }

    /// <summary>A hop that is new, or (with a name) a hop whose router name has just been found.</summary>
    public sealed record TraceUpdate(TraceHop Hop, string? ResolvedName);

    /// <summary>What the tool remembers between sessions: the setup, never a host name.</summary>
    public sealed class DnsToolsSettings
    {
        public DnsToolMode Mode { get; set; } = DnsToolMode.Dns;

        /// <summary>The record type asked for; null = "all common records".</summary>
        public DnsRecordType? RecordType { get; set; }

        public string ServerId { get; set; } = "system";
        public string CustomServer { get; set; } = "";
        public int MaxHops { get; set; } = 30;
        public bool ResolveNames { get; set; } = true;
    }
}
