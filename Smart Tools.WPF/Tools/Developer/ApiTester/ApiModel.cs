using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  API Tester data model.
    //
    //  Plain data only - no WPF controls, no HttpClient, no file I/O. The
    //  view (ApiTesterView) fills these from its controls and hands them to
    //  ApiManager; ApiManager hands results back as these same types. That
    //  keeps the engine testable without a window and keeps the view free
    //  of networking code.
    // =====================================================================

    /// <summary>How the request authenticates. Order matches CmbAuthType's items.</summary>
    public enum ApiAuthType
    {
        None = 0,
        Basic = 1,
        Bearer = 2,
        ApiKey = 3,
    }

    /// <summary>Body encoding. Order matches CmbBodyFormat's items.</summary>
    public enum ApiBodyFormat
    {
        Json = 0,
        Xml = 1,
        FormData = 2,
        Raw = 3,
    }

    /// <summary>Export file format. Order matches CmbExportFormat's items.</summary>
    public enum ApiExportFormat
    {
        Csv = 0,
        Json = 1,
        Xml = 2,
    }

    /// <summary>Why a request didn't produce an HTTP response.</summary>
    public enum ApiErrorKind
    {
        None,
        InvalidUrl,
        Timeout,
        Cancelled,
        ConnectionFailed,
        Unexpected,
    }

    /// <summary>WebSocket connection lifecycle, drives the header status pill.</summary>
    public enum ApiConnectionState
    {
        Idle,
        Connecting,
        Connected,
        Reconnecting,
        Disconnected,
        Failed,
    }

    /// <summary>Direction of one line in the WebSocket traffic log.</summary>
    public enum WebSocketDirection
    {
        In,
        Out,
        System,
    }

    /// <summary>One editable row in the Headers / Params grids.</summary>
    public sealed class KeyValueRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
        }

        private string _key = "";
        public string Key
        {
            get => _key;
            set { if (_key != value) { _key = value ?? ""; OnPropertyChanged(); } }
        }

        private string _value = "";
        public string Value
        {
            get => _value;
            set { if (_value != value) { _value = value ?? ""; OnPropertyChanged(); } }
        }

        /// <summary>True when this row should actually be sent.</summary>
        public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Key);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A saved Method + URL pair. Deliberately holds NO auth/secret data,
    /// since saved endpoints are persisted to disk in plain JSON.
    /// </summary>
    public sealed class SavedEndpoint
    {
        public required string Method { get; init; }
        public required string Url { get; init; }

        public override string ToString() => $"{Method}  {Url}";
    }

    /// <summary>Credentials for the current request (never persisted).</summary>
    public sealed record ApiAuthSettings(
        ApiAuthType Type,
        string Username = "",
        string Password = "",
        string Token = "",
        string KeyName = "",
        string KeyValue = "");

    /// <summary>Everything needed to send one REST request.</summary>
    public sealed class ApiRequest
    {
        public string Method { get; init; } = "GET";

        /// <summary>URL exactly as typed - may be relative to BaseUrl.</summary>
        public string Url { get; init; } = "";
        public string BaseUrl { get; init; } = "";

        public IReadOnlyList<KeyValueRow> Headers { get; init; } = Array.Empty<KeyValueRow>();
        public IReadOnlyList<KeyValueRow> QueryParams { get; init; } = Array.Empty<KeyValueRow>();

        public string Body { get; init; } = "";
        public ApiBodyFormat BodyFormat { get; init; } = ApiBodyFormat.Json;

        public ApiAuthSettings Auth { get; init; } = new(ApiAuthType.None);
    }

    /// <summary>Result of one REST request - either an HTTP response or an error.</summary>
    public sealed class ApiResponse
    {
        /// <summary>Final URL that was requested (base URL + query applied).</summary>
        public string RequestUrl { get; init; } = "";

        /// <summary>Null when no HTTP response arrived (see ErrorKind).</summary>
        public int? StatusCode { get; init; }
        public string ReasonPhrase { get; init; } = "";

        public string Body { get; init; } = "";
        public string HeadersText { get; init; } = "";
        public string? ContentType { get; init; }

        public long ElapsedMs { get; init; }

        /// <summary>Actual bytes received on the wire (not re-encoded text length).</summary>
        public long SizeBytes { get; init; }

        public ApiErrorKind ErrorKind { get; init; } = ApiErrorKind.None;
        public string ErrorMessage { get; init; } = "";

        public bool HasResponse => StatusCode.HasValue;
        public string StatusText => HasResponse ? $"{StatusCode} {ReasonPhrase}".Trim() : ErrorMessage;
    }

    /// <summary>One completed REST call, kept for export.</summary>
    public sealed record ApiHistoryEntry(
        DateTime Timestamp,
        string Method,
        string Url,
        int? StatusCode,
        string Status,
        long ElapsedMs,
        long SizeBytes);

    /// <summary>One line in the WebSocket traffic log.</summary>
    public sealed class WebSocketLogEntry
    {
        public required DateTime Timestamp { get; init; }
        public required WebSocketDirection Direction { get; init; }
        public required string Message { get; init; }

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

        public string DirectionText => Direction switch
        {
            WebSocketDirection.In => "IN",
            WebSocketDirection.Out => "OUT",
            _ => "SYS",
        };

        public override string ToString() => $"[{TimeText}] {DirectionText}  {Message}";
    }
}
