using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Security.Principal;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Event Log Explorer: the half that talks to Windows (EventLogSession / EventLogReader).
    /// Everything runs off the UI thread; the view reports progress through IProgress and
    /// cancels with a token - cancelling keeps what was read so far.
    /// </summary>
    public sealed partial class EventExplorerManager
    {
        private static readonly string[] WindowsLogs = { "Application", "System", "Security", "Setup", "ForwardedEvents" };

        private const long AuditFailureKeyword = 0x10000000000000;
        private const long AuditSuccessKeyword = 0x20000000000000;

        /// <summary>The most events looked at for one query, so a text filter on a huge log still ends.</summary>
        private static int ScanLimit(int maxEvents) => Math.Max(maxEvents * 25, 50_000);

        public static bool IsAdministrator() => HardwareSensorsManager.IsAdministrator();

        // =================================================================
        //  The list of logs
        // =================================================================

        /// <summary>
        /// Every readable log with its size and last write. Debug and analytic logs are skipped
        /// (Windows can't read them while they are on). The Windows logs come first.
        /// </summary>
        public Task<List<EventExplorerLogInfo>> ListLogsAsync(CancellationToken token = default) =>
            Task.Run(() =>
            {
                var logs = new List<EventExplorerLogInfo>();
                using var session = new EventLogSession();

                foreach (string name in session.GetLogNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using (var config = new EventLogConfiguration(name))
                        {
                            if (config.LogType is EventLogType.Analytical or EventLogType.Debug) continue;
                        }

                        var status = EventExplorerLogStatus.Ok;
                        long? count = null;
                        DateTime? last = null;
                        try
                        {
                            var info = session.GetLogInformation(name, PathType.LogName);
                            count = info.RecordCount;
                            last = info.LastWriteTime;
                        }
                        catch (Exception ex) when (IsAccessDenied(ex))
                        {
                            status = EventExplorerLogStatus.NeedsAdmin;
                        }
                        catch (Exception ex) when (ex is EventLogException or InvalidOperationException)
                        {
                            // a log with no file yet: shown, with no count
                        }

                        logs.Add(new EventExplorerLogInfo
                        {
                            Name = name,
                            Group = WindowsLogs.Contains(name, StringComparer.OrdinalIgnoreCase) ? EventExplorerLogGroup.Windows : EventExplorerLogGroup.Other,
                            Status = status,
                            RecordCount = count,
                            LastWrite = last,
                        });
                    }
                    catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // one broken log never hides the others
                    }
                }

                return logs
                    .OrderBy(l => l.Group)
                    .ThenBy(l => l.Group == EventExplorerLogGroup.Windows ? Array.FindIndex(WindowsLogs, w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase)) : 0)
                    .ThenBy(l => l.Name, NaturalStringComparer.Instance)
                    .ToList();
            }, token);

        // =================================================================
        //  Reading events
        // =================================================================

        /// <summary>
        /// Reads the newest events that match, up to the limit. Errors come back in the result
        /// (access denied, log missing) instead of being thrown; cancelling returns what was read.
        /// </summary>
        public Task<EventExplorerResult> QueryAsync(EventExplorerQuery query, IProgress<EventExplorerProgress>? progress = null, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var entries = new List<EventExplorerEntry>();
                int scanned = 0;
                bool truncated = false, cancelled = false;
                var error = EventExplorerError.None;
                string errorText = "";

                var ids = ParseEventIds(query.EventIds, out string? idError);
                if (ids == null)
                {
                    return new EventExplorerResult { Error = EventExplorerError.Other, ErrorText = idError ?? "", Elapsed = watch.Elapsed };
                }

                string xpath = BuildXPath(query.Levels, query.FromLocal, query.ToLocal, ids);
                int scanLimit = ScanLimit(query.MaxEvents);
                long lastReport = 0;

                try
                {
                    // With TolerateQueryErrors on, a log the user can't open (Security) or that doesn't
                    // exist would just read as empty: ask for its details first so those errors surface.
                    using (var session = new EventLogSession())
                        session.GetLogInformation(query.LogName, PathType.LogName);

                    var eventQuery = new EventLogQuery(query.LogName, PathType.LogName, xpath)
                    {
                        ReverseDirection = true,   // newest first
                        TolerateQueryErrors = true,
                    };
                    using var reader = new EventLogReader(eventQuery);

                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        using EventRecord? record = reader.ReadEvent();
                        if (record == null) break;

                        scanned++;
                        EventExplorerEntry? entry = null;
                        try
                        {
                            entry = ToEntry(record, query.LogName);
                        }
                        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or ArgumentException)
                        {
                            // one unreadable event is skipped, not fatal
                        }

                        if (entry != null && ids.Allows(entry.EventId) && Matches(entry, query.Source, query.Text))
                        {
                            entries.Add(entry);
                            if (entries.Count >= query.MaxEvents) { truncated = true; break; }
                        }
                        if (scanned >= scanLimit) { truncated = true; break; }

                        if (progress != null && watch.ElapsedMilliseconds - lastReport >= 100)
                        {
                            lastReport = watch.ElapsedMilliseconds;
                            progress.Report(new EventExplorerProgress { Scanned = scanned, Matched = entries.Count });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex) when (IsAccessDenied(ex))
                {
                    error = EventExplorerError.AccessDenied;
                    errorText = ex.Message;
                }
                catch (EventLogNotFoundException ex)
                {
                    error = EventExplorerError.NotFound;
                    errorText = ex.Message;
                }
                catch (Exception ex) when (ex is EventLogException or InvalidOperationException or IOException)
                {
                    error = EventExplorerError.Other;
                    errorText = ex.Message;
                }

                progress?.Report(new EventExplorerProgress { Scanned = scanned, Matched = entries.Count });
                return new EventExplorerResult
                {
                    Entries = entries,
                    Scanned = scanned,
                    Truncated = truncated,
                    Cancelled = cancelled,
                    Elapsed = watch.Elapsed,
                    Error = error,
                    ErrorText = errorText,
                };
            }, CancellationToken.None);

        private static EventExplorerEntry ToEntry(EventRecord record, string logName)
        {
            long keywords = record.Keywords ?? 0;
            bool auditFailure = (keywords & AuditFailureKeyword) != 0;
            bool auditSuccess = !auditFailure && (keywords & AuditSuccessKeyword) != 0;

            var level = auditFailure ? EventExplorerLevel.Error
                : record.Level is { } l and >= 1 and <= 5 ? (EventExplorerLevel)l
                : EventExplorerLevel.Information;

            string levelText = (auditFailure || auditSuccess ? Safe(() => record.KeywordsDisplayNames?.FirstOrDefault()) : null)
                ?? Safe(() => record.LevelDisplayName)
                ?? (auditFailure ? Loc.T("Str_EventLog_MsgAuditFailure", "Audit Failure") : auditSuccess ? Loc.T("Str_EventLog_MsgAuditSuccess", "Audit Success") : level.ToString());

            string message = FormatMessage(record);
            return new EventExplorerEntry
            {
                LogName = logName,
                RecordId = record.RecordId,
                TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                Level = level,
                LevelText = levelText,
                EventId = record.Id,
                Provider = record.ProviderName ?? "",
                Computer = record.MachineName ?? "",
                Message = message,
                Summary = Summarize(message),
            };
        }

        /// <summary>The description Windows shows; when the source's message file is missing, its data items instead.</summary>
        private static string FormatMessage(EventRecord record)
        {
            try
            {
                string? text = record.FormatDescription();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch (EventLogException)
            {
                // fall through to the raw data
            }

            try
            {
                var values = record.Properties?.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (values is { Count: > 0 }) return string.Join(" | ", values);
            }
            catch (EventLogException)
            {
                // nothing more to show
            }
            return "";
        }

        // =================================================================
        //  One event in full
        // =================================================================

        /// <summary>The XML and the slow-to-read fields (account name, task ...) of one event, or null if it is gone.</summary>
        public Task<EventExplorerDetails?> GetDetailsAsync(string logName, long recordId, CancellationToken token = default) =>
            Task.Run(() =>
            {
                try
                {
                    var query = new EventLogQuery(logName, PathType.LogName, $"*[System[EventRecordID={recordId}]]");
                    using var reader = new EventLogReader(query);
                    using EventRecord? record = reader.ReadEvent();
                    if (record == null || token.IsCancellationRequested) return null;

                    string xml = Safe(() => record.ToXml()) ?? "";
                    var fields = new List<KeyValuePair<string, string>>();
                    void Add(string key, string? value)
                    {
                        if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(key, value));
                    }

                    Add("Log", record.LogName ?? logName);
                    Add("Source", record.ProviderName);
                    Add("EventId", record.Id.ToString());
                    Add("Level", Safe(() => record.LevelDisplayName));
                    Add("Logged", record.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    Add("Task", Safe(() => record.TaskDisplayName));
                    Add("Opcode", Safe(() => record.OpcodeDisplayName));
                    Add("Keywords", Safe(() => string.Join(", ", record.KeywordsDisplayNames ?? Enumerable.Empty<string>())));
                    Add("User", AccountOf(record.UserId));
                    Add("Computer", record.MachineName);
                    Add("ProcessId", record.ProcessId?.ToString());
                    Add("ThreadId", record.ThreadId?.ToString());
                    Add("ActivityId", record.ActivityId?.ToString());
                    Add("RecordId", record.RecordId?.ToString());

                    return new EventExplorerDetails
                    {
                        Xml = PrettyXml(xml),
                        Fields = fields,
                        Data = ParseEventData(xml),
                    };
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return null;
                }
            }, token);

        private static string? AccountOf(SecurityIdentifier? sid)
        {
            if (sid == null) return null;
            try
            {
                return sid.Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is SystemException or InvalidOperationException)
            {
                return sid.Value;   // unknown or unreachable account: show the SID
            }
        }

        // =================================================================
        //  Export / open
        // =================================================================

        /// <summary>Saves the events that match the filters as a real .evtx file (opens in Event Viewer).</summary>
        public Task ExportEvtxAsync(EventExplorerQuery query, string targetPath, CancellationToken token = default) =>
            Task.Run(() =>
            {
                var ids = ParseEventIds(query.EventIds, out _) ?? new EventIdFilter();
                string xpath = BuildXPath(query.Levels, query.FromLocal, query.ToLocal, ids);
                // ExportLog refuses to overwrite, so write beside the target and swap it in once it worked:
                // a failed export never costs the user the file that was already there.
                string temp = targetPath + ".part";
                if (File.Exists(temp)) File.Delete(temp);
                try
                {
                    using (var session = new EventLogSession())
                        session.ExportLog(query.LogName, PathType.LogName, xpath, temp, tolerateQueryErrors: true);
                    File.Move(temp, targetPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    throw;
                }
            }, token);

        /// <summary>Opens Windows' own Event Viewer. False when it can't be started.</summary>
        public static bool OpenEventViewer()
        {
            try
            {
                Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static bool IsAccessDenied(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is UnauthorizedAccessException) return true;
                if (e is EventLogException && (e.HResult & 0xFFFF) == 5) return true;   // ERROR_ACCESS_DENIED
            }
            return false;
        }

        /// <summary>Provider metadata is sometimes missing: a property that throws reads as null.</summary>
        private static T? Safe<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is EventLogException or InvalidOperationException or NotSupportedException)
            {
                return default;
            }
        }
    }
}
