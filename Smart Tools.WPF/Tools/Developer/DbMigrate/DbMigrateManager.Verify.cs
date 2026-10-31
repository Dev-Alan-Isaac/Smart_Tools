using Microsoft.Data.SqlClient;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Database Migrator - checks the copy: row counts always, checksums on request, and a text report.</summary>
    public sealed partial class DbMigrateManager
    {
        /// <summary>Types BINARY_CHECKSUM can't take (or that we don't trust to match across versions).</summary>
        private static readonly HashSet<string> NoChecksum = new(StringComparer.OrdinalIgnoreCase)
        {
            "xml", "text", "ntext", "image", "geography", "geometry", "hierarchyid", "sql_variant", "json", "vector"
        };

        private async Task VerifyAsync(DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateSchema schema, List<DbMigrateTableItem> items,
            DbMigrateOptions options, DbMigrateResult result, CancellationToken ct)
        {
            if (items.Count == 0) return;
            var defs = schema.Tables.ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
            await using var src = await OpenAsync(source, source.Database.Trim(), true, ct).ConfigureAwait(false);
            await using var dst = await OpenAsync(target, target.Database.Trim(), false, ct).ConfigureAwait(false);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var def = defs[item.FullName];
                var table = Q(def.Schema, def.Name);
                try
                {
                    var s = await ScalarAsync(src, $"SELECT COUNT_BIG(*) FROM {table}", ct).ConfigureAwait(false);
                    var d = await ScalarAsync(dst, $"SELECT COUNT_BIG(*) FROM {table}", ct).ConfigureAwait(false);

                    bool? checksum = null;
                    if (options.VerifyChecksums && s == d)
                    {
                        var cols = def.Columns.Where(c => c.IsCopied && !c.IsClrType && !NoChecksum.Contains(c.BaseType)).Select(c => Q(c.Name)).ToList();
                        if (cols.Count > 0)
                        {
                            var sql = $"SELECT CHECKSUM_AGG(BINARY_CHECKSUM({string.Join(", ", cols)})) FROM {table}";
                            var a = await ScalarAsync(src, sql, ct).ConfigureAwait(false);
                            var b = await ScalarAsync(dst, sql, ct).ConfigureAwait(false);
                            checksum = a == b;
                        }
                    }

                    var row = new DbMigrateVerifyRow { Table = item.FullName, SourceRows = s, TargetRows = d, ChecksumMatch = checksum };
                    result.Verify.Add(row);
                    if (!row.Ok)
                    {
                        result.Failures.Add($"{item.FullName}: source has {s:N0} row(s), target has {d:N0}");
                        Log(DbMigrateSeverity.Warning, "VERIFY", $"{item.FullName}: source {s:N0} vs target {d:N0} - DIFFERENT");
                    }
                    else if (checksum == false)
                        Log(DbMigrateSeverity.Warning, "VERIFY", $"{item.FullName}: row counts match but the checksum differs (float/date values can checksum differently between versions - compare a sample)");
                }
                catch (SqlException ex)
                {
                    Log(DbMigrateSeverity.Warning, "VERIFY", $"{item.FullName}: could not verify: {FirstLine(ex.Message)}");
                }
            }
            Log(DbMigrateSeverity.Info, "VERIFY", $"{result.Verify.Count(v => v.Ok)} of {result.Verify.Count} table(s) have matching row counts.");
        }

        private static async Task<long> ScalarAsync(SqlConnection cn, string sql, CancellationToken ct)
        {
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
            var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        /// <summary>Plain-text summary of the plan and the outcome, for "Copy report" / "Save report".</summary>
        public static string BuildReport(DbMigratePlan plan, DbMigrateEndpoint source, DbMigrateEndpoint target, DbMigrateResult? result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Smart Tools - Database Migrator report");
            sb.AppendLine($"Date     : {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Source   : {plan.Source.Display} / {source.Database}");
            sb.AppendLine($"Target   : {plan.Target.Display} / {target.Database}");
            sb.AppendLine($"Direction: {plan.Direction}");
            sb.AppendLine();
            if (plan.Issues.Count > 0)
            {
                sb.AppendLine("Notes and warnings");
                foreach (var i in plan.Issues.OrderByDescending(i => i.Severity))
                    sb.AppendLine($"  [{i.Severity}] {i.Object}: {i.Message}");
                sb.AppendLine();
            }
            if (result != null)
            {
                sb.AppendLine(result.Success ? "RESULT: success" : result.Cancelled ? "RESULT: cancelled" : "RESULT: finished with problems");
                sb.AppendLine($"  Time     : {result.Elapsed:hh\\:mm\\:ss}");
                sb.AppendLine($"  Tables   : {result.TablesCopied} copied, {result.TablesFailed} failed");
                sb.AppendLine($"  Rows     : {result.RowsCopied:N0}");
                sb.AppendLine($"  Objects  : {result.ObjectsCreated} created, {result.ObjectsFailed} failed");
                if (result.DatabaseCreated) sb.AppendLine($"  Database : created{(result.DatabaseDropped ? " then dropped" : "")}");
                if (result.Failures.Count > 0)
                {
                    sb.AppendLine("  Problems :");
                    foreach (var f in result.Failures) sb.AppendLine($"    - {f}");
                }
                if (result.Verify.Count > 0)
                {
                    sb.AppendLine("  Row counts (source / target):");
                    foreach (var v in result.Verify)
                        sb.AppendLine($"    {(v.Ok ? "ok  " : "DIFF")} {v.Table}: {v.SourceRows:N0} / {v.TargetRows:N0}{(v.ChecksumMatch == false ? "  (checksum differs)" : "")}");
                }
            }
            return sb.ToString();
        }
    }
}
