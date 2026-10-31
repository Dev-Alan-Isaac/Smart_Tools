using Microsoft.Data.SqlClient;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Database Migrator - copies a SQL Server database to another server of ANY version (up or down) through
    /// two live connections: structure as generated DDL, data streamed with SqlBulkCopy. No .bak involved,
    /// so a newer server's database can be moved to an older server too.
    ///
    /// Created on the UI thread; log lines are raised back on that thread. Parts:
    ///  .Catalog.cs  reads the source (sys.* views)       .Compat.cs  version differences
    ///  .Script.cs   schema -> CREATE statements          .Data.cs    streaming data copy
    ///  .Verify.cs   row counts / checksums / report      .Settings.cs  remembered options (never passwords)
    /// The source database is only ever read.
    /// </summary>
    public sealed partial class DbMigrateManager : IDisposable
    {
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
        private CancellationTokenSource? _cts;
        private string? _createdDatabase;      // set only when THIS run created it: the only database we will ever drop
        private DbMigrateEndpoint? _createdOn;
        private bool _disposed;

        public event Action<DbMigrateLogLine>? LogAdded;

        public bool IsBusy { get; private set; }

        /// <summary>Stops whatever is running (plan or copy). Safe to call any time. Called from the window-closing path.</summary>
        public void CancelActiveRun()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelActiveRun();
            _cts?.Dispose();
        }

        // -------------------------------------------------------------- logging / UI marshalling

        internal void Log(DbMigrateSeverity severity, string source, string text)
        {
            var line = new DbMigrateLogLine { Severity = severity, Source = source, Text = text };
            Ui(() => LogAdded?.Invoke(line));
        }

        private void Ui(Action a)
        {
            if (_ui == null) a();
            else _ui.Post(_ => a(), null);
        }

        // -------------------------------------------------------------- connections

        public static string BuildConnectionString(DbMigrateEndpoint ep, string? database, bool readOnlyIntent = false)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = ep.Server.Trim(),
                InitialCatalog = database ?? "",
                TrustServerCertificate = ep.TrustServerCertificate,
                ConnectTimeout = 15,
                ApplicationName = "Smart Tools - Database Migrator",
                Pooling = true
            };
            if (readOnlyIntent) b.ApplicationIntent = ApplicationIntent.ReadOnly;
            if (ep.Auth == DbMigrateAuth.Windows) b.IntegratedSecurity = true;
            else { b.UserID = ep.User.Trim(); b.Password = ep.Password; }
            return b.ConnectionString;
        }

        private static async Task<SqlConnection> OpenAsync(DbMigrateEndpoint ep, string? database, bool readOnly, CancellationToken ct)
        {
            var cn = new SqlConnection(BuildConnectionString(ep, database, readOnly));
            try
            {
                await cn.OpenAsync(ct).ConfigureAwait(false);
                return cn;
            }
            catch
            {
                await cn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task<DbMigrateServerInfo> ProbeAsync(SqlConnection cn, CancellationToken ct)
        {
            await using var cmd = Cmd(cn, @"
SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), CAST(SERVERPROPERTY('Edition') AS nvarchar(100)),
       CAST(SERVERPROPERTY('EngineEdition') AS int), CAST(SERVERPROPERTY('ServerName') AS nvarchar(200)),
       CAST(SERVERPROPERTY('Collation') AS nvarchar(200))");
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            var version = Str(r, 0) ?? "0";
            var info = new DbMigrateServerInfo
            {
                ProductVersion = version,
                Edition = Str(r, 1) ?? "",
                EngineEdition = Int(r, 2),
                ServerName = Str(r, 3) ?? "",
                Collation = Str(r, 4) ?? ""
            };
            _ = int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major);
            // Azure SQL (engine editions 5 and 8) reports 12.x but behaves like a current server
            info.Major = info.EngineEdition is 5 or 8 ? Math.Max(major, 16) : major;
            return info;
        }

        /// <summary>Connects, reads the version, and reports it. Used by the "Test connection" buttons.</summary>
        public async Task<DbMigrateServerInfo> TestConnectionAsync(DbMigrateEndpoint ep, CancellationToken ct = default)
        {
            await using var cn = await OpenAsync(ep, string.IsNullOrWhiteSpace(ep.Database) ? null : ep.Database.Trim(), false, ct).ConfigureAwait(false);
            return await ProbeAsync(cn, ct).ConfigureAwait(false);
        }

        /// <summary>User databases on a server, for the database picker.</summary>
        public async Task<List<string>> ListDatabasesAsync(DbMigrateEndpoint ep, CancellationToken ct = default)
        {
            var list = new List<string>();
            await using var cn = await OpenAsync(ep, null, false, ct).ConfigureAwait(false);
            await foreach (var r in Rows(cn, "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", ct))
                list.Add(r.GetString(0));
            return list;
        }

        // -------------------------------------------------------------- plan (no writes)

        /// <summary>
        /// Reads the source, checks it against the target's version, and says what would happen. Writes nothing anywhere.
        /// </summary>
        public async Task<DbMigratePlan> BuildPlanAsync(DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateOptions options, CancellationToken ct = default)
        {
            StartOperation(ct, out var token);
            try
            {
                Log(DbMigrateSeverity.Info, "SOURCE", $"Connecting to {source.Server}...");
                await using var src = await OpenAsync(source, source.Database.Trim(), true, token).ConfigureAwait(false);
                var srcInfo = await ProbeAsync(src, token).ConfigureAwait(false);
                Log(DbMigrateSeverity.Info, "SOURCE", srcInfo.Display);

                Log(DbMigrateSeverity.Info, "TARGET", $"Connecting to {target.Server}...");
                await using var dst = await OpenAsync(target, options.CreateTargetDatabase ? "master" : target.Database.Trim(), false, token).ConfigureAwait(false);
                var dstInfo = await ProbeAsync(dst, token).ConfigureAwait(false);
                Log(DbMigrateSeverity.Info, "TARGET", dstInfo.Display);

                Log(DbMigrateSeverity.Info, "SCHEMA", "Reading the source database structure...");
                var schema = await ReadSchemaAsync(src, srcInfo.Major, token).ConfigureAwait(false);
                srcInfo.CompatibilityLevel = schema.CompatibilityLevel;
                srcInfo.Collation = schema.Collation;

                var direction = srcInfo.Major == dstInfo.Major ? DbMigrateDirection.Same
                    : dstInfo.Major > srcInfo.Major ? DbMigrateDirection.Upgrade : DbMigrateDirection.Downgrade;
                var plan = new DbMigratePlan { Source = srcInfo, Target = dstInfo, Schema = schema, Direction = direction };
                plan.TargetCompatibilityLevel = PickCompatibilityLevel(schema.CompatibilityLevel, dstInfo.Major);

                plan.Issues.AddRange(Analyze(schema, srcInfo, dstInfo, options));
                await CheckTargetAsync(dst, source, target, srcInfo, dstInfo, schema, options, plan, token).ConfigureAwait(false);

                foreach (var t in schema.Tables)
                    plan.Tables.Add(new DbMigrateTableItem
                    {
                        Schema = t.Schema,
                        Name = t.Name,
                        RowCount = t.RowCount,
                        Unsupported = t.Unsupported,
                        Include = t.Unsupported == null,
                        Message = t.Unsupported ?? ""
                    });

                Log(DbMigrateSeverity.Info, "SCHEMA",
                    $"{schema.Tables.Count} table(s), {plan.ObjectCount(DbMigrateObjectKind.Index)} index(es), {schema.ForeignKeys.Count} foreign key(s), " +
                    $"{schema.Modules.Count} view/function/procedure/trigger object(s). Direction: {direction}.");
                return plan;
            }
            finally { EndOperation(); }
        }

        private async Task CheckTargetAsync(SqlConnection dst, DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateServerInfo srcInfo,
            DbMigrateServerInfo dstInfo, DbMigrateSchema schema, DbMigrateOptions options, DbMigratePlan plan, CancellationToken ct)
        {
            void Add(DbMigrateSeverity sev, string obj, string msg) => plan.Issues.Add(new DbMigrateIssue { Severity = sev, Object = obj, Message = msg });

            var dstName = target.Database.Trim();
            if (string.IsNullOrEmpty(source.Database)) Add(DbMigrateSeverity.Blocker, "source", "choose the source database");
            if (string.IsNullOrEmpty(dstName)) { Add(DbMigrateSeverity.Blocker, "target", "type the target database name"); return; }

            if (string.Equals(srcInfo.ServerName, dstInfo.ServerName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(source.Database.Trim(), dstName, StringComparison.OrdinalIgnoreCase))
            {
                Add(DbMigrateSeverity.Blocker, "target", "source and target are the same database");
                return;
            }

            if (options.CreateTargetDatabase)
            {
                await using var cmd = Cmd(dst, "SELECT COUNT(*) FROM sys.databases WHERE name = @n");
                cmd.Parameters.AddWithValue("@n", dstName);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    Add(DbMigrateSeverity.Blocker, dstName, "a database with this name already exists on the target. Pick another name, or switch to \"use an existing empty database\"");
                else
                    Add(DbMigrateSeverity.Info, dstName, $"will be created with collation {schema.Collation} and compatibility level {plan.TargetCompatibilityLevel}");
            }
            else
            {
                long objects;
                string? collation;
                await using (var cmd = Cmd(dst, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0"))
                    objects = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                await using (var cmd = Cmd(dst, "SELECT collation_name FROM sys.databases WHERE name = DB_NAME()"))
                    collation = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

                if (objects > 0) Add(DbMigrateSeverity.Blocker, dstName, $"the target database is not empty ({objects} object(s)). Use an empty database or let the tool create a new one");
                if (collation != null && !collation.Equals(schema.Collation, StringComparison.OrdinalIgnoreCase))
                    Add(DbMigrateSeverity.Warning, dstName, $"collation is {collation} but the source uses {schema.Collation}; text sorting/comparison may differ");
            }
        }

        // -------------------------------------------------------------- run

        /// <summary>The structure only, as a .sql text, for the "Save schema script" button.</summary>
        public string BuildSchemaScript(DbMigratePlan plan, string databaseName, DbMigrateOptions options) =>
            BuildScriptText(plan.Schema, databaseName, plan.Target.Major, plan.Target.EngineEdition, options,
                plan.Tables.Where(t => t.Include).Select(t => t.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase), plan.TargetCompatibilityLevel);

        public async Task<DbMigrateResult> RunAsync(DbMigratePlan plan, DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateOptions options,
            IProgress<DbMigrateProgress>? progress, CancellationToken ct = default)
        {
            var result = new DbMigrateResult();
            if (plan.HasBlockers)
            {
                Log(DbMigrateSeverity.Blocker, "RUN", "The plan has blockers. Fix them first.");
                return result;
            }

            StartOperation(ct, out var token);
            var sw = Stopwatch.StartNew();
            var dstName = target.Database.Trim();
            var included = plan.Tables.Where(t => t.Include && t.Unsupported == null).ToList();
            var includedSet = included.Select(t => t.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var state = new RunState(progress, included.Count, included.Sum(t => t.RowCount ?? 0));

            SqlConnection? ddl = null;
            try
            {
                // 1. target database
                state.Report(DbMigrateStage.Connecting, "Connecting...");
                if (options.CreateTargetDatabase)
                {
                    await using var master = await OpenAsync(target, "master", false, token).ConfigureAwait(false);
                    await ExecAsync(master, $"CREATE DATABASE {Q(dstName)} COLLATE {plan.Schema.Collation}", options, token).ConfigureAwait(false);
                    _createdDatabase = dstName; _createdOn = target;
                    result.DatabaseCreated = true;
                    Log(DbMigrateSeverity.Info, "TARGET", $"Created database {dstName}.");
                    try { await ExecAsync(master, $"ALTER DATABASE {Q(dstName)} SET COMPATIBILITY_LEVEL = {plan.TargetCompatibilityLevel}", options, token).ConfigureAwait(false); }
                    catch (SqlException ex) { Log(DbMigrateSeverity.Warning, "TARGET", $"Could not set compatibility level {plan.TargetCompatibilityLevel}: {FirstLine(ex.Message)}"); }
                }

                ddl = await OpenAsync(target, dstName, false, token).ConfigureAwait(false);
                await ExecAsync(ddl, SessionSetup, options, token).ConfigureAwait(false);

                var steps = BuildSteps(plan.Schema, plan.Target.Major, plan.Target.EngineEdition, options, includedSet);
                var failedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 2. structure up to the tables (and synonyms)
                state.Report(DbMigrateStage.Structure, "Creating structure...");
                foreach (var phase in new[] { DbMigratePhase.Schemas, DbMigratePhase.Types, DbMigratePhase.Sequences, DbMigratePhase.Tables, DbMigratePhase.Synonyms })
                    await RunPhaseAsync(ddl, steps, phase, options, result, state, token, failedTables).ConfigureAwait(false);
                foreach (var name in failedTables)
                    if (included.FirstOrDefault(t => t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } item)
                        Ui(() => { item.State = DbMigrateStepState.Failed; item.Message = "table could not be created"; });

                // 3. data (before indexes: loading into bare tables is much faster)
                if (options.IncludeData)
                {
                    state.Report(DbMigrateStage.Data, "Copying data...");
                    var toCopy = included.Where(t => !failedTables.Contains(t.FullName)).ToList();
                    await CopyDataAsync(source, target, plan.Schema, toCopy, options, state, result, token).ConfigureAwait(false);
                }
                else foreach (var t in included) Ui(() => { t.State = DbMigrateStepState.Skipped; t.Message = "data not copied (turned off)"; });

                // 4. indexes, constraints, code
                state.Report(DbMigrateStage.Indexes, "Building indexes and constraints...");
                foreach (var phase in new[] { DbMigratePhase.Indexes, DbMigratePhase.Checks, DbMigratePhase.ForeignKeys })
                    await RunPhaseAsync(ddl, steps, phase, options, result, state, token, null).ConfigureAwait(false);

                state.Report(DbMigrateStage.Code, "Creating views, functions, procedures and triggers...");
                foreach (var phase in new[] { DbMigratePhase.Code, DbMigratePhase.ViewIndexes, DbMigratePhase.Triggers })
                    await RunPhaseAsync(ddl, steps, phase, options, result, state, token, null).ConfigureAwait(false);

                state.Report(DbMigrateStage.Finishing, "Finishing...");
                foreach (var phase in new[] { DbMigratePhase.Reseed, DbMigratePhase.Properties })
                    await RunPhaseAsync(ddl, steps, phase, options, result, state, token, null).ConfigureAwait(false);

                // 5. verify
                if (options.IncludeData)
                {
                    state.Report(DbMigrateStage.Verifying, "Verifying...");
                    await VerifyAsync(source, target, plan.Schema, included.Where(t => t.State == DbMigrateStepState.Done).ToList(), options, result, token).ConfigureAwait(false);
                }

                result.TablesCopied = included.Count(t => t.State == DbMigrateStepState.Done);
                result.TablesFailed = included.Count(t => t.State == DbMigrateStepState.Failed);
                result.RowsCopied = included.Sum(t => t.RowsCopied);
                result.Success = result.TablesFailed == 0 && result.ObjectsFailed == 0 && result.Verify.All(v => v.Ok);
                state.Report(result.Success ? DbMigrateStage.Done : DbMigrateStage.Failed, result.Success ? "Done." : "Finished with problems.");
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                Log(DbMigrateSeverity.Warning, "RUN", "Cancelled.");
                state.Report(DbMigrateStage.Cancelled, "Cancelled.");
            }
            catch (Exception ex)
            {
                result.Failures.Add(FirstLine(ex.Message));
                Log(DbMigrateSeverity.Blocker, "RUN", $"Stopped: {FirstLine(ex.Message)}");
                state.Report(DbMigrateStage.Failed, "Failed.");
            }
            finally
            {
                if (ddl != null) await ddl.DisposeAsync().ConfigureAwait(false);
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                EndOperation();
            }

            if (!result.Success && result.DatabaseCreated && options.DropCreatedDatabaseOnFailure)
                result.DatabaseDropped = await DropCreatedDatabaseAsync().ConfigureAwait(false);
            return result;
        }

        /// <summary>Drops the target database, but only if this run created it. Returns false (and logs why) otherwise.</summary>
        public async Task<bool> DropCreatedDatabaseAsync()
        {
            if (_createdDatabase == null || _createdOn == null)
            {
                Log(DbMigrateSeverity.Warning, "TARGET", "Nothing to drop: this tool did not create the target database in this session.");
                return false;
            }
            try
            {
                await using var master = await OpenAsync(_createdOn, "master", false, CancellationToken.None).ConfigureAwait(false);
                await ExecAsync(master, $"ALTER DATABASE {Q(_createdDatabase)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE", new DbMigrateOptions(), CancellationToken.None).ConfigureAwait(false);
                await ExecAsync(master, $"DROP DATABASE {Q(_createdDatabase)}", new DbMigrateOptions(), CancellationToken.None).ConfigureAwait(false);
                Log(DbMigrateSeverity.Info, "TARGET", $"Dropped database {_createdDatabase}.");
                _createdDatabase = null; _createdOn = null;
                return true;
            }
            catch (Exception ex)
            {
                Log(DbMigrateSeverity.Warning, "TARGET", $"Could not drop {_createdDatabase}: {FirstLine(ex.Message)}");
                return false;
            }
        }

        // -------------------------------------------------------------- phases

        private async Task RunPhaseAsync(SqlConnection ddl, List<DbMigrateScriptStep> all, DbMigratePhase phase, DbMigrateOptions options,
            DbMigrateResult result, RunState state, CancellationToken ct, HashSet<string>? failedTables)
        {
            var steps = all.Where(s => s.Phase == phase).ToList();
            if (steps.Count == 0) return;
            await ExecAsync(ddl, SessionSetup, options, ct).ConfigureAwait(false);
            var tag = phase.ToString().ToUpperInvariant();

            var pending = new List<(DbMigrateScriptStep Step, string Error)>();
            foreach (var s in steps)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ExecStepAsync(ddl, s, options, ct).ConfigureAwait(false);
                    result.ObjectsCreated++;
                    state.ObjectDone(s.Object);
                }
                catch (SqlException ex) when (s.Retry)
                {
                    pending.Add((s, FirstLine(ex.Message)));
                }
                catch (SqlException ex)
                {
                    result.ObjectsFailed++;
                    state.ObjectFailed();
                    result.Failures.Add($"{s.Object}: {FirstLine(ex.Message)}");
                    Log(DbMigrateSeverity.Warning, tag, $"{s.Object}: {FirstLine(ex.Message)}");
                    if (s.Phase == DbMigratePhase.Tables) failedTables?.Add(s.Object);
                }
            }

            // views that need other views, etc.: try again until a whole pass makes no progress
            while (pending.Count > 0)
            {
                var again = new List<(DbMigrateScriptStep Step, string Error)>();
                foreach (var (s, previous) in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await ExecStepAsync(ddl, s, options, ct).ConfigureAwait(false);
                        result.ObjectsCreated++;
                        state.ObjectDone(s.Object);
                    }
                    catch (SqlException ex) { again.Add((s, FirstLine(ex.Message))); }
                }
                if (again.Count == pending.Count)
                {
                    foreach (var (s, error) in again)
                    {
                        result.ObjectsFailed++;
                        state.ObjectFailed();
                        result.Failures.Add($"{s.Object}: {error}");
                        Log(DbMigrateSeverity.Warning, tag, $"{s.Object}: {error}");
                    }
                    break;
                }
                pending = again;
            }
        }

        private async Task ExecStepAsync(SqlConnection cn, DbMigrateScriptStep s, DbMigrateOptions options, CancellationToken ct)
        {
            if (s.Prelude != null) await ExecAsync(cn, s.Prelude, options, ct).ConfigureAwait(false);
            await ExecAsync(cn, s.Sql, options, ct).ConfigureAwait(false);
        }

        private static async Task ExecAsync(SqlConnection cn, string sql, DbMigrateOptions options, CancellationToken ct)
        {
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = options.CommandTimeoutSeconds };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        internal static string FirstLine(string text)
        {
            var i = text.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? text : text[..i];
        }

        // -------------------------------------------------------------- operation bookkeeping

        private void StartOperation(CancellationToken external, out CancellationToken token)
        {
            if (IsBusy) throw new InvalidOperationException("Another operation is still running.");
            IsBusy = true;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            token = _cts.Token;
        }

        private void EndOperation() => IsBusy = false;

        /// <summary>Counters and throttled progress reporting (about 10 updates a second).</summary>
        private sealed class RunState
        {
            private readonly IProgress<DbMigrateProgress>? _progress;
            private readonly object _gate = new();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _lastReportMs = -1000;
            private DbMigrateStage _stage;
            private string _message = "";
            private int _tablesDone, _tablesFailed, _objectsDone, _objectsFailed;
            private long _rowsDone;

            public RunState(IProgress<DbMigrateProgress>? progress, int tablesTotal, long rowsTotal)
            {
                _progress = progress;
                TablesTotal = tablesTotal;
                RowsTotal = rowsTotal;
            }

            public int TablesTotal { get; }
            public long RowsTotal { get; }

            public void Report(DbMigrateStage stage, string message)
            {
                lock (_gate) { _stage = stage; _message = message; Emit(force: true); }
            }

            public void ObjectDone(string name) { lock (_gate) { _objectsDone++; _message = name; Emit(false); } }
            public void ObjectFailed() { lock (_gate) { _objectsFailed++; Emit(false); } }
            public void TableDone(bool ok) { lock (_gate) { if (ok) _tablesDone++; else _tablesFailed++; Emit(true); } }
            public void Rows(long delta, string table) { lock (_gate) { _rowsDone += delta; _message = table; Emit(false); } }

            private void Emit(bool force)
            {
                if (_progress == null) return;
                var now = _clock.ElapsedMilliseconds;
                if (!force && now - _lastReportMs < 100) return;
                _lastReportMs = now;
                _progress.Report(new DbMigrateProgress
                {
                    Stage = _stage,
                    Message = _message,
                    TablesDone = _tablesDone,
                    TablesFailed = _tablesFailed,
                    TablesTotal = TablesTotal,
                    RowsDone = _rowsDone,
                    RowsTotal = RowsTotal,
                    ObjectsDone = _objectsDone,
                    ObjectsFailed = _objectsFailed
                });
            }
        }
    }
}
