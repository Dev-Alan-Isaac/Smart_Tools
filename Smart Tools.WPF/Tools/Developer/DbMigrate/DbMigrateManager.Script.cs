using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Order the pieces of the structure are created in. The data copy runs between <see cref="Tables"/> and <see cref="Indexes"/>.</summary>
    internal enum DbMigratePhase { Schemas, Types, Sequences, Tables, Synonyms, Indexes, Checks, ForeignKeys, Code, ViewIndexes, Triggers, Reseed, Properties }

    /// <summary>One statement (batch) to run on the target.</summary>
    internal sealed class DbMigrateScriptStep
    {
        public DbMigratePhase Phase { get; init; }
        public DbMigrateObjectKind Kind { get; init; }
        public string Object { get; init; } = "";
        public string Sql { get; init; } = "";
        /// <summary>Run as its own command first (SET ANSI_NULLS / QUOTED_IDENTIFIER for modules).</summary>
        public string? Prelude { get; init; }
        /// <summary>Failing is expected sometimes (a view that needs another view not made yet): retry after the rest.</summary>
        public bool Retry { get; init; }
    }

    /// <summary>
    /// Database Migrator - turns the in-memory schema into CREATE statements for the target version.
    /// Pure logic, no WPF, no I/O: unit-testable in a plain net8.0 project.
    /// </summary>
    public sealed partial class DbMigrateManager
    {
        /// <summary>Settings that make indexed views / computed-column indexes valid; sent once per DDL connection.</summary>
        internal const string SessionSetup =
            "SET ARITHABORT ON; SET ANSI_WARNINGS ON; SET ANSI_PADDING ON; SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;";

        /// <summary>
        /// All structure statements in creation order. Tables flagged unsupported (and anything that depends on them)
        /// are left out. <paramref name="includedTables"/> = tables the user kept ticked, as "schema.name".
        /// </summary>
        internal static List<DbMigrateScriptStep> BuildSteps(DbMigrateSchema schema, int targetMajor, int targetEngineEdition,
            DbMigrateOptions options, ISet<string> includedTables)
        {
            var steps = new List<DbMigrateScriptStep>();
            var compression = CompressionAllowed(targetMajor, targetEngineEdition);
            var keep = schema.Tables.Where(t => t.Unsupported == null && includedTables.Contains(t.FullName)).ToList();
            var keepNames = keep.Select(t => t.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            void Add(DbMigratePhase phase, DbMigrateObjectKind kind, string obj, string sql, string? prelude = null, bool retry = false) =>
                steps.Add(new DbMigrateScriptStep { Phase = phase, Kind = kind, Object = obj, Sql = sql, Prelude = prelude, Retry = retry });

            foreach (var s in schema.Schemas)
                Add(DbMigratePhase.Schemas, DbMigrateObjectKind.Schema, s, $"CREATE SCHEMA {Q(s)}");

            foreach (var t in schema.Types.Where(t => !t.IsTableType))
                Add(DbMigratePhase.Types, DbMigrateObjectKind.Type, $"{t.Schema}.{t.Name}",
                    $"CREATE TYPE {Q(t.Schema, t.Name)} FROM {t.BaseDecl} {(t.IsNullable ? "NULL" : "NOT NULL")}");
            foreach (var t in schema.Types.Where(t => t.IsTableType))
                Add(DbMigratePhase.Types, DbMigrateObjectKind.Type, $"{t.Schema}.{t.Name}",
                    $"CREATE TYPE {Q(t.Schema, t.Name)} AS TABLE ({string.Join(", ", t.Columns.Select(c => ColumnDdl(c, withDefault: false)))})");

            if (targetMajor >= 11)
                foreach (var q in schema.Sequences)
                    Add(DbMigratePhase.Sequences, DbMigrateObjectKind.Sequence, $"{q.Schema}.{q.Name}", SequenceDdl(q));

            foreach (var t in keep)
                Add(DbMigratePhase.Tables, DbMigrateObjectKind.Table, t.FullName, TableDdl(t));

            foreach (var syn in schema.Synonyms)
                Add(DbMigratePhase.Synonyms, DbMigrateObjectKind.Synonym, $"{syn.Schema}.{syn.Name}",
                    $"CREATE SYNONYM {Q(syn.Schema, syn.Name)} FOR {syn.BaseObject}");

            if (options.IncludeIndexes)
            {
                var all = keep.SelectMany(t => t.Indexes.Select(i => (Table: t, Index: i)))
                    .OrderBy(x => x.Index.IsClustered ? 0 : 1).ThenBy(x => x.Table.FullName).ToList();
                foreach (var (t, i) in all)
                {
                    foreach (var sql in IndexDdl(t, i, targetMajor, compression))
                        Add(DbMigratePhase.Indexes, DbMigrateObjectKind.Index, $"{t.FullName}.{i.Name}", sql);
                }
            }
            else
            {
                // keys are data integrity, not speed: primary keys and unique constraints are always created
                foreach (var t in keep)
                    foreach (var i in t.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint).OrderBy(i => i.IsClustered ? 0 : 1))
                        foreach (var sql in IndexDdl(t, i, targetMajor, compression))
                            Add(DbMigratePhase.Indexes, DbMigrateObjectKind.Index, $"{t.FullName}.{i.Name}", sql);
            }

            foreach (var t in keep)
                foreach (var c in t.Checks)
                {
                    var with = c.IsNotTrusted ? "WITH NOCHECK" : "WITH CHECK";
                    Add(DbMigratePhase.Checks, DbMigrateObjectKind.Constraint, $"{t.FullName}.{c.Name}",
                        $"ALTER TABLE {Q(t.Schema, t.Name)} {with} ADD CONSTRAINT {Q(c.Name)} CHECK {c.Definition}");
                    if (c.IsDisabled)
                        Add(DbMigratePhase.Checks, DbMigrateObjectKind.Constraint, $"{t.FullName}.{c.Name}",
                            $"ALTER TABLE {Q(t.Schema, t.Name)} NOCHECK CONSTRAINT {Q(c.Name)}");
                }

            foreach (var fk in schema.ForeignKeys)
            {
                if (!keepNames.Contains($"{fk.ChildSchema}.{fk.ChildTable}") || !keepNames.Contains($"{fk.ParentSchema}.{fk.ParentTable}")) continue;
                steps.Add(new DbMigrateScriptStep
                {
                    Phase = DbMigratePhase.ForeignKeys,
                    Kind = DbMigrateObjectKind.Constraint,
                    Object = $"{fk.ChildSchema}.{fk.ChildTable}.{fk.Name}",
                    Sql = ForeignKeyDdl(fk)
                });
                if (fk.IsDisabled)
                    Add(DbMigratePhase.ForeignKeys, DbMigrateObjectKind.Constraint, $"{fk.ChildSchema}.{fk.ChildTable}.{fk.Name}",
                        $"ALTER TABLE {Q(fk.ChildSchema, fk.ChildTable)} NOCHECK CONSTRAINT {Q(fk.Name)}");
            }

            if (options.IncludeCode)
            {
                // views, functions, procedures: any order works if we retry what failed; triggers wait for the tables' data
                foreach (var kind in new[] { DbMigrateObjectKind.View, DbMigrateObjectKind.Function, DbMigrateObjectKind.Procedure })
                    foreach (var m in schema.Modules.Where(m => m.Kind == kind && m.Definition != null))
                        Add(DbMigratePhase.Code, kind, m.FullName, NormalizeModule(m.Definition!), ModulePrelude(m), retry: true);

                if (options.IncludeIndexes)
                    foreach (var v in schema.ViewIndexHosts)
                        foreach (var i in v.Indexes.OrderBy(i => i.IsClustered ? 0 : 1))
                            foreach (var sql in IndexDdl(v, i, targetMajor, compression))
                                Add(DbMigratePhase.ViewIndexes, DbMigrateObjectKind.Index, $"{v.FullName}.{i.Name}", sql, retry: true);

                foreach (var m in schema.Modules.Where(m => m.Kind == DbMigrateObjectKind.Trigger && m.Definition != null))
                    Add(DbMigratePhase.Triggers, DbMigrateObjectKind.Trigger, m.FullName, NormalizeModule(m.Definition!), ModulePrelude(m), retry: true);
            }

            foreach (var t in keep)
                foreach (var c in t.Columns.Where(c => c.IsIdentity && c.IdentityLastValue != null))
                    Add(DbMigratePhase.Reseed, DbMigrateObjectKind.Table, t.FullName,
                        $"DBCC CHECKIDENT ({Lit(Q(t.Schema, t.Name))}, RESEED, {c.IdentityLastValue}) WITH NO_INFOMSGS");

            foreach (var t in keep)
            {
                if (!string.IsNullOrEmpty(t.Description))
                    Add(DbMigratePhase.Properties, DbMigrateObjectKind.Property, t.FullName, DescriptionDdl(t, null, t.Description!));
                foreach (var c in t.Columns.Where(c => !string.IsNullOrEmpty(c.Description)))
                    Add(DbMigratePhase.Properties, DbMigrateObjectKind.Property, $"{t.FullName}.{c.Name}", DescriptionDdl(t, c.Name, c.Description!));
            }

            return steps.OrderBy(s => s.Phase).ToList();   // stable: keeps the order they were added in within a phase
        }

        /// <summary>The whole structure as a .sql text (GO between batches), for "Save schema script".</summary>
        internal static string BuildScriptText(DbMigrateSchema schema, string databaseName, int targetMajor, int targetEngineEdition,
            DbMigrateOptions options, ISet<string> includedTables, int compatibilityLevel)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"-- Smart Tools - Database Migrator: structure of {databaseName}");
            sb.AppendLine($"-- Generated {DateTime.Now:yyyy-MM-dd HH:mm}, written for SQL Server {targetMajor}. Data is not included.");
            sb.AppendLine();
            sb.AppendLine($"-- CREATE DATABASE {Q(databaseName)} COLLATE {schema.Collation};");
            sb.AppendLine($"-- ALTER DATABASE {Q(databaseName)} SET COMPATIBILITY_LEVEL = {compatibilityLevel};");
            sb.AppendLine($"USE {Q(databaseName)};");
            sb.AppendLine("GO");
            foreach (var s in BuildSteps(schema, targetMajor, targetEngineEdition, options, includedTables))
            {
                if (s.Prelude != null) { sb.AppendLine(s.Prelude); sb.AppendLine("GO"); }
                sb.AppendLine(s.Sql.TrimEnd());
                sb.AppendLine("GO");
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------- statement builders

        private static string ModulePrelude(DbMigrateModuleDef m) =>
            $"SET ANSI_NULLS {(m.AnsiNulls ? "ON" : "OFF")}; SET QUOTED_IDENTIFIER {(m.QuotedIdentifier ? "ON" : "OFF")};";

        internal static string ColumnDdl(DbMigrateColumn c, bool withDefault = true)
        {
            var sb = new StringBuilder();
            sb.Append(Q(c.Name)).Append(' ');
            if (c.IsComputed)
            {
                sb.Append("AS ").Append(c.ComputedDefinition);
                if (c.IsPersisted) sb.Append(" PERSISTED");
                return sb.ToString();
            }
            sb.Append(c.IsRowVersion ? "rowversion" : c.TypeDecl);
            if (c.Collation != null) sb.Append(" COLLATE ").Append(c.Collation);
            if (c.IsSparse) sb.Append(" SPARSE");
            if (c.IsRowGuid) sb.Append(" ROWGUIDCOL");
            if (c.IsIdentity) sb.Append(" IDENTITY(").Append(c.IdentitySeed).Append(',').Append(c.IdentityIncrement).Append(')');
            sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (withDefault && c.DefaultDefinition != null)
            {
                if (c.DefaultName != null) sb.Append(" CONSTRAINT ").Append(Q(c.DefaultName));
                sb.Append(" DEFAULT ").Append(c.DefaultDefinition);
            }
            return sb.ToString();
        }

        internal static string TableDdl(DbMigrateTableDef t) =>
            $"CREATE TABLE {Q(t.Schema, t.Name)} (\n    {string.Join(",\n    ", t.Columns.Select(c => ColumnDdl(c)))}\n)";

        internal static string SequenceDdl(DbMigrateSequenceDef q)
        {
            // next value to hand out: one step after the last one used, or the start value if it was never used
            var start = q.StartValue;
            if (q.LastUsedValue != null && BigInteger.TryParse(q.LastUsedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
                && BigInteger.TryParse(q.Increment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inc))
                start = (last + inc).ToString(CultureInfo.InvariantCulture);

            var sb = new StringBuilder($"CREATE SEQUENCE {Q(q.Schema, q.Name)} AS {q.TypeName} START WITH {start} INCREMENT BY {q.Increment}");
            if (q.MinValue != null) sb.Append(" MINVALUE ").Append(q.MinValue);
            if (q.MaxValue != null) sb.Append(" MAXVALUE ").Append(q.MaxValue);
            sb.Append(q.IsCycling ? " CYCLE" : " NO CYCLE");
            sb.Append(q.IsCached ? (q.CacheSize is > 0 ? $" CACHE {q.CacheSize}" : " CACHE") : " NO CACHE");
            return sb.ToString();
        }

        private static string IndexWith(DbMigrateIndex i, int targetMajor, bool compression)
        {
            var o = new List<string>();
            if (i.IsPadded) o.Add("PAD_INDEX = ON");
            if (i.FillFactor is > 0 and < 100) o.Add($"FILLFACTOR = {i.FillFactor}");
            if (i.IgnoreDupKey) o.Add("IGNORE_DUP_KEY = ON");
            if (!i.AllowRowLocks) o.Add("ALLOW_ROW_LOCKS = OFF");
            if (!i.AllowPageLocks) o.Add("ALLOW_PAGE_LOCKS = OFF");
            if (i.OptimizeForSequentialKey && targetMajor >= 15) o.Add("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON");
            if (compression && i.DataCompression > 0) o.Add($"DATA_COMPRESSION = {(i.DataCompression == 1 ? "ROW" : "PAGE")}");
            return o.Count == 0 ? "" : $" WITH ({string.Join(", ", o)})";
        }

        /// <summary>One statement (two when the index was disabled in the source).</summary>
        internal static IEnumerable<string> IndexDdl(DbMigrateTableDef t, DbMigrateIndex i, int targetMajor, bool compression)
        {
            var keys = string.Join(", ", i.Columns.Where(c => !c.Included).Select(c => Q(c.Name) + (c.Descending ? " DESC" : " ASC")));
            var with = IndexWith(i, targetMajor, compression);
            var table = Q(t.Schema, t.Name);

            if (i.IsPrimaryKey || i.IsUniqueConstraint)
            {
                yield return $"ALTER TABLE {table} ADD CONSTRAINT {Q(i.Name)} {(i.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {(i.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({keys}){with}";
                yield break;
            }

            var sb = new StringBuilder("CREATE ");
            if (i.IsUnique) sb.Append("UNIQUE ");
            sb.Append(i.IsClustered ? "CLUSTERED" : "NONCLUSTERED").Append(" INDEX ").Append(Q(i.Name)).Append(" ON ").Append(table).Append(" (").Append(keys).Append(')');
            var included = i.Columns.Where(c => c.Included).Select(c => Q(c.Name)).ToList();
            if (included.Count > 0) sb.Append(" INCLUDE (").Append(string.Join(", ", included)).Append(')');
            if (!string.IsNullOrEmpty(i.FilterDefinition)) sb.Append(" WHERE ").Append(i.FilterDefinition);
            sb.Append(with);
            yield return sb.ToString();
            if (i.IsDisabled) yield return $"ALTER INDEX {Q(i.Name)} ON {table} DISABLE";
        }

        internal static string ForeignKeyDdl(DbMigrateForeignKey fk)
        {
            static string Action(int a) => a switch { 1 => "CASCADE", 2 => "SET NULL", 3 => "SET DEFAULT", _ => "NO ACTION" };
            var sb = new StringBuilder();
            sb.Append($"ALTER TABLE {Q(fk.ChildSchema, fk.ChildTable)} {(fk.IsNotTrusted ? "WITH NOCHECK" : "WITH CHECK")} ADD CONSTRAINT {Q(fk.Name)} ");
            sb.Append($"FOREIGN KEY ({string.Join(", ", fk.ChildColumns.Select(Q))}) REFERENCES {Q(fk.ParentSchema, fk.ParentTable)} ({string.Join(", ", fk.ParentColumns.Select(Q))})");
            if (fk.DeleteAction != 0) sb.Append(" ON DELETE ").Append(Action(fk.DeleteAction));
            if (fk.UpdateAction != 0) sb.Append(" ON UPDATE ").Append(Action(fk.UpdateAction));
            return sb.ToString();
        }

        private static string DescriptionDdl(DbMigrateTableDef t, string? column, string text)
        {
            var sql = $"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {Lit(text)}, @level0type = N'SCHEMA', @level0name = {Lit(t.Schema)}, @level1type = N'TABLE', @level1name = {Lit(t.Name)}";
            return column == null ? sql : sql + $", @level2type = N'COLUMN', @level2name = {Lit(column)}";
        }
    }
}
