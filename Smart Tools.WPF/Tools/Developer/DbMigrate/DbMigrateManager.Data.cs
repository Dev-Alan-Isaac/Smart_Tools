using Microsoft.Data.SqlClient;
using Smart_Tools.WPF.Models;
using System.Data;
using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Database Migrator - the data pass. Each table is streamed from a read-only source connection into the target
    /// with SqlBulkCopy (never held in memory), so a large table costs the same memory as a small one.
    /// One table failing is logged and the others carry on; cancelling stops at once and keeps nothing half-reported.
    /// </summary>
    public sealed partial class DbMigrateManager
    {
        private async Task CopyDataAsync(DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateSchema schema, List<DbMigrateTableItem> items,
            DbMigrateOptions options, RunState state, DbMigrateResult result, CancellationToken ct)
        {
            var defs = schema.Tables.ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(Math.Clamp(options.Parallelism, 1, 8));

            // biggest tables first so the long ones don't start last
            var ordered = items.OrderByDescending(i => i.RowCount ?? 0).ToList();
            var tasks = ordered.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { await CopyTableAsync(source, target, defs[item.FullName], item, options, state, result, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task CopyTableAsync(DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateTableDef def, DbMigrateTableItem item,
            DbMigrateOptions options, RunState state, DbMigrateResult result, CancellationToken ct)
        {
            var columns = def.Columns.Where(c => c.IsCopied).ToList();
            Ui(() => { item.State = DbMigrateStepState.Running; item.RowsCopied = 0; item.Message = ""; });

            if (columns.Count == 0)
            {
                Ui(() => { item.State = DbMigrateStepState.Done; item.Message = "no copyable columns"; });
                state.TableDone(true);
                return;
            }

            try
            {
                await using var src = await OpenAsync(source, source.Database.Trim(), true, ct).ConfigureAwait(false);
                await using var dst = await OpenAsync(target, target.Database.Trim(), false, ct).ConfigureAwait(false);

                var select = $"SELECT {string.Join(", ", columns.Select(c => Q(c.Name)))} FROM {Q(def.Schema, def.Name)}";
                await using var cmd = new SqlCommand(select, src) { CommandTimeout = options.CommandTimeoutSeconds };
                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

                var batch = Math.Max(100, options.BatchSize);
                using var bulk = new SqlBulkCopy(dst, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock, null)
                {
                    DestinationTableName = Q(def.Schema, def.Name),
                    BatchSize = batch,
                    BulkCopyTimeout = options.CommandTimeoutSeconds,
                    EnableStreaming = true,
                    NotifyAfter = batch
                };
                foreach (var c in columns) bulk.ColumnMappings.Add(c.Name, c.Name);

                long reported = 0;
                bulk.SqlRowsCopied += (_, e) =>
                {
                    var delta = e.RowsCopied - reported;
                    reported = e.RowsCopied;
                    state.Rows(delta, item.FullName);
                    var total = e.RowsCopied;
                    Ui(() => item.RowsCopied = total);
                };

                await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);

                // the exact count (the last partial batch isn't reported by the event)
                await using var count = Cmd(dst, $"SELECT COUNT_BIG(*) FROM {Q(def.Schema, def.Name)}");
                var copied = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                state.Rows(copied - reported, item.FullName);
                Ui(() => { item.RowsCopied = copied; item.State = DbMigrateStepState.Done; item.Message = ""; });
                state.TableDone(true);
                Log(DbMigrateSeverity.Info, "DATA", $"{item.FullName}: {copied:N0} row(s)");
            }
            catch (OperationCanceledException)
            {
                Ui(() => { item.State = DbMigrateStepState.Skipped; item.Message = "cancelled"; });
                throw;
            }
            catch (Exception ex)
            {
                var message = FirstLine(ex.Message);
                lock (result.Failures) result.Failures.Add($"{item.FullName}: {message}");
                Ui(() => { item.State = DbMigrateStepState.Failed; item.Message = message; });
                state.TableDone(false);
                Log(DbMigrateSeverity.Warning, "DATA", $"{item.FullName}: {message}");
            }
        }
    }
}
