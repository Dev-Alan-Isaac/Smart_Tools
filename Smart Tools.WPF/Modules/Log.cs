using log4net;
using log4net.Config;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;

// Root namespace on purpose: every view/controller/model namespace sits below it, so `Log.Info(...)`
// works everywhere without an extra using.
namespace Smart_Tools.WPF
{
    /// <summary>
    /// App-wide log (log4net underneath). The logger name is the calling file and the member name is
    /// added to the message, so there is nothing to set up per class:
    /// <c>Log.Info("scan started");</c>. Warnings and errors are also written straight to disk in
    /// heartbeat.log, so they survive a hard reset of the machine.
    /// </summary>
    public static class Log
    {
        public static string Folder { get; } = Path.Combine(
            AppPaths.DataRoot, "logs");

        private static readonly ConcurrentDictionary<string, ILog> Loggers = new();
        private static readonly object InitGate = new();
        private static bool _ready;

        /// <summary>The latest Info line, shown in each heartbeat so a freeze shows what was last started.</summary>
        public static string LastActivity { get; private set; } = "";

        // Same defaults as log4net.config, for when that file isn't next to the exe.
        private const string DefaultConfig = """
            <log4net>
              <appender name="File" type="log4net.Appender.RollingFileAppender">
                <file type="log4net.Util.PatternString" value="%envFolderPath{ApplicationData}\Smart Tools\logs\smarttools.log" />
                <appendToFile value="true" />
                <rollingStyle value="Size" />
                <maxSizeRollBackups value="10" />
                <maximumFileSize value="10MB" />
                <staticLogFileName value="true" />
                <lockingModel type="log4net.Appender.FileAppender+MinimalLock" />
                <layout type="log4net.Layout.PatternLayout">
                  <conversionPattern value="%date{yyyy-MM-dd HH:mm:ss.fff} [%thread] %-5level %logger - %message%newline" />
                </layout>
              </appender>
              <root>
                <level value="INFO" />
                <appender-ref ref="File" />
              </root>
            </log4net>
            """;

        public static void Init()
        {
            lock (InitGate)
            {
                if (_ready) return;
                _ready = true;   // set first: a failure below must not make every call retry
                try
                {
                    Directory.CreateDirectory(Folder);
                    var repository = LogManager.GetRepository(Assembly.GetExecutingAssembly());
                    string file = Path.Combine(AppContext.BaseDirectory, "log4net.config");
                    if (File.Exists(file))
                    {
                        XmlConfigurator.Configure(repository, new FileInfo(file));
                    }
                    else
                    {
                        var doc = new XmlDocument();
                        doc.LoadXml(DefaultConfig);
                        XmlConfigurator.Configure(repository, doc.DocumentElement!);
                    }
                    PointFileAppenderAtLogFolder(repository);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Logging couldn't start: {ex.Message}");
                }
            }
        }

        /// <summary>log4net.config names %AppData%; this moves the file appender to <see cref="Folder"/> (beside the exe when portable).</summary>
        private static void PointFileAppenderAtLogFolder(log4net.Repository.ILoggerRepository repository)
        {
            foreach (var appender in repository.GetAppenders())
            {
                if (appender is not log4net.Appender.FileAppender file) continue;
                file.File = Path.Combine(Folder, Path.GetFileName(file.File));
                file.ActivateOptions();
            }
        }

        private static ILog Get(string file)
        {
            if (!_ready) Init();
            return Loggers.GetOrAdd(file, f => LogManager.GetLogger(Assembly.GetExecutingAssembly(), Path.GetFileNameWithoutExtension(f)));
        }

        public static void Debug(string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            var log = Get(file);
            if (log.IsDebugEnabled) log.Debug($"{member}: {message}");
        }

        public static void Info(string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            LastActivity = message.Length > 160 ? message[..160] : message;
            var log = Get(file);
            if (log.IsInfoEnabled) log.Info($"{member}: {message}");
        }

        public static void Warn(string message, Exception? ex = null, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            Get(file).Warn($"{member}: {message}", ex);
            DurableLog.Write($"WARN  {Path.GetFileNameWithoutExtension(file)}.{member}: {message}{(ex == null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
        }

        public static void Error(string message, Exception? ex = null, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            Get(file).Error($"{member}: {message}", ex);
            DurableLog.Write($"ERROR {Path.GetFileNameWithoutExtension(file)}.{member}: {message}{(ex == null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
        }

        public static void Fatal(string message, Exception? ex = null, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            Get(file).Fatal($"{member}: {message}", ex);
            DurableLog.Write($"FATAL {Path.GetFileNameWithoutExtension(file)}.{member}: {message}{(ex == null ? "" : " | " + ex)}");
        }

        /// <summary>
        /// Logs BEGIN now and END (with elapsed time) on dispose. Use with <c>using var _ = Log.Scope("...")</c>
        /// around anything that can block: if a freeze happens inside it, the log shows a BEGIN with no END.
        /// </summary>
        public static Scoped Scope(string name, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
            => new(name, file, member);

        private static readonly ConcurrentDictionary<long, (string Tool, string Detail, long Start)> Running = new();
        private static long _activityId;

        /// <summary>What is running right now, in every tool (not just the one on screen). Shown in each heartbeat.</summary>
        public static string ActiveSummary()
        {
            if (Running.IsEmpty) return "none";
            var parts = Running.OrderBy(kv => kv.Key).Take(8)
                .Select(kv => $"{kv.Value.Tool}: {(kv.Value.Detail.Length > 100 ? kv.Value.Detail[..100] : kv.Value.Detail)} ({Stopwatch.GetElapsedTime(kv.Value.Start).TotalSeconds:0}s)");
            string text = string.Join("; ", parts);
            return Running.Count > 8 ? text + $"; +{Running.Count - 8} more" : text;
        }

        /// <summary>
        /// Marks a run of a tool (scan, hash, convert...) as active: START now, END with the elapsed time on
        /// dispose, and listed in the heartbeat while it runs. One call per run, never per file.
        /// </summary>
        public static ActivityScope Activity(string tool, string detail = "", [CallerFilePath] string file = "", [CallerMemberName] string member = "")
            => new(tool, detail, file, member);

        public sealed class ActivityScope : IDisposable
        {
            private readonly long _id = Interlocked.Increment(ref _activityId);
            private readonly string _tool, _file, _member;
            private readonly long _start = Stopwatch.GetTimestamp();
            private int _ended;

            internal ActivityScope(string tool, string detail, string file, string member)
            {
                _tool = tool; _file = file; _member = member;
                Running[_id] = (tool, detail, _start);
                Info($"START {tool}: {detail}", file, member);
            }

            /// <summary>Updates what the heartbeat shows for this run (e.g. "sha256 big.iso").</summary>
            public void Update(string detail)
            {
                if (Running.TryGetValue(_id, out var current)) Running[_id] = (current.Tool, detail, current.Start);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _ended, 1) != 0) return;
                Running.TryRemove(_id, out _);
                Info($"END {_tool} ({Stopwatch.GetElapsedTime(_start).TotalSeconds:0.0}s)", _file, _member);
            }
        }

        /// <summary>Notes a file being read, moved, deleted or written by a tool, so two tools on one path show up together.</summary>
        public static void FileOp(string op, string path, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
            => Info($"{op} {path}", file, member);

        public readonly struct Scoped : IDisposable
        {
            private readonly string _name, _file, _member;
            private readonly long _start;

            internal Scoped(string name, string file, string member)
            {
                _name = name; _file = file; _member = member;
                _start = Stopwatch.GetTimestamp();
                Info($"BEGIN {name}", file, member);
            }

            public void Dispose()
            {
                double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
                if (ms >= 500) Warn($"END {_name} took {ms:0} ms (slow)", null, _file, _member);
                else Info($"END {_name} ({ms:0} ms)", _file, _member);
            }
        }
    }

    /// <summary>
    /// Append-only file written with WriteThrough and no buffering, so every line is on disk when the
    /// call returns. Used for the heartbeat and for warnings/errors: those are the lines still needed
    /// after the machine had to be hard-reset.
    /// </summary>
    public static class DurableLog
    {
        private const long MaxBytes = 5 * 1024 * 1024;
        private static readonly object Gate = new();
        private static FileStream? _stream;

        public static string FilePath => Path.Combine(Log.Folder, "heartbeat.log");

        public static void Write(string line)
        {
            lock (Gate)
            {
                try
                {
                    _stream ??= Open();
                    if (_stream.Length > MaxBytes)
                    {
                        _stream.Dispose();
                        _stream = null;
                        File.Move(FilePath, FilePath + ".1", overwrite: true);
                        _stream = Open();
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
                    _stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Durable log failed: {ex.Message}");
                    _stream?.Dispose();
                    _stream = null;
                }
            }
        }

        private static FileStream Open()
        {
            Directory.CreateDirectory(Log.Folder);
            return new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.WriteThrough);
        }

        public static void Close()
        {
            lock (Gate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
