using Microsoft.Data.SqlClient;
using Smart_Tools.WPF.Models;
using System.Data;
using System.Globalization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Database Migrator - reads what is inside the source database (sys.* catalog views only, so it works on
    /// every SQL Server version). Pure ADO.NET, no WPF: compiles in a plain net8.0 test project.
    /// </summary>
    public sealed partial class DbMigrateManager
    {
        // -------------------------------------------------------------- small helpers (shared by the other partials)

        internal static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
        internal static string Q(string schema, string name) => Q(schema) + "." + Q(name);
        internal static string Lit(string value) => "N'" + value.Replace("'", "''") + "'";

        private static string? Str(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        private static int Int(IDataRecord r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
        private static bool Bool(IDataRecord r, int i) => !r.IsDBNull(i) && Convert.ToBoolean(r.GetValue(i), CultureInfo.InvariantCulture);

        /// <summary>Type text for DDL, from the catalog's max_length / precision / scale.</summary>
        internal static string TypeDecl(string type, int maxLength, int precision, int scale)
        {
            switch (type.ToLowerInvariant())
            {
                case "char": case "varchar": case "binary": case "varbinary":
                    return $"{type}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})";
                case "nchar": case "nvarchar":
                    return $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})";
                case "decimal": case "numeric":
                    return $"{type}({precision},{scale})";
                case "datetime2": case "time": case "datetimeoffset":
                    return $"{type}({scale})";
                case "timestamp":
                    return "rowversion";
                default:
                    return type;
            }
        }

        // -------------------------------------------------------------- the read

        /// <summary>Reads the whole source schema. <paramref name="major"/> is the source server's major version (it gates columns that older servers don't have).</summary>
        internal async Task<DbMigrateSchema> ReadSchemaAsync(SqlConnection cn, int major, CancellationToken ct)
        {
            var schema = new DbMigrateSchema();
            string dbName;

            await using (var cmd = Cmd(cn, "SELECT DB_NAME(), collation_name, compatibility_level FROM sys.databases WHERE name = DB_NAME()"))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await r.ReadAsync(ct).ConfigureAwait(false);
                dbName = r.GetString(0);
                schema.Collation = Str(r, 1) ?? "";
                schema.CompatibilityLevel = Int(r, 2);
            }

            // user schemas
            await foreach (var r in Rows(cn, "SELECT name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383 ORDER BY name", ct))
                schema.Schemas.Add(r.GetString(0));

            // descriptions (extended property MS_Description) on tables and columns
            var descriptions = new Dictionary<(int obj, int col), string>();
            await foreach (var r in Rows(cn, "SELECT major_id, minor_id, CAST(value AS nvarchar(max)) FROM sys.extended_properties WHERE class = 1 AND name = N'MS_Description'", ct))
                if (!r.IsDBNull(2)) descriptions[(Int(r, 0), Int(r, 1))] = Str(r, 2)!;

            // alias types
            await foreach (var r in Rows(cn, @"
SELECT SCHEMA_NAME(t.schema_id), t.name, bt.name, t.max_length, t.precision, t.scale, t.is_nullable
FROM sys.types t JOIN sys.types bt ON bt.user_type_id = t.system_type_id
WHERE t.is_user_defined = 1 AND t.is_assembly_type = 0 AND t.is_table_type = 0
ORDER BY 1, 2", ct))
            {
                schema.Types.Add(new DbMigrateTypeDef
                {
                    Schema = r.GetString(0),
                    Name = r.GetString(1),
                    BaseDecl = TypeDecl(r.GetString(2), Int(r, 3), Int(r, 4), Int(r, 5)),
                    IsNullable = Bool(r, 6)
                });
            }

            // table types (columns come from the type's internal table)
            var tableTypes = new Dictionary<int, DbMigrateTypeDef>();
            await foreach (var r in Rows(cn, "SELECT type_table_object_id, SCHEMA_NAME(schema_id), name FROM sys.table_types WHERE is_user_defined = 1 ORDER BY 2, 3", ct))
            {
                var tt = new DbMigrateTypeDef { Schema = r.GetString(1), Name = r.GetString(2), IsTableType = true };
                tableTypes[Int(r, 0)] = tt;
                schema.Types.Add(tt);
            }

            // sequences (SQL Server 2012+)
            if (major >= 11)
            {
                var lastUsed = major >= 14 ? "CONVERT(nvarchar(60), s.last_used_value)" : "CAST(NULL AS nvarchar(60))";
                await foreach (var r in Rows(cn, $@"
SELECT SCHEMA_NAME(s.schema_id), s.name, TYPE_NAME(s.user_type_id), CONVERT(nvarchar(60), s.start_value), CONVERT(nvarchar(60), s.increment),
       CONVERT(nvarchar(60), s.minimum_value), CONVERT(nvarchar(60), s.maximum_value), s.is_cycling, s.is_cached, s.cache_size,
       CONVERT(nvarchar(60), s.current_value), {lastUsed}
FROM sys.sequences s ORDER BY 1, 2", ct))
                {
                    var seq = new DbMigrateSequenceDef
                    {
                        Schema = r.GetString(0),
                        Name = r.GetString(1),
                        TypeName = Str(r, 2) ?? "bigint",
                        StartValue = Str(r, 3) ?? "1",
                        Increment = Str(r, 4) ?? "1",
                        MinValue = Str(r, 5),
                        MaxValue = Str(r, 6),
                        IsCycling = Bool(r, 7),
                        IsCached = Bool(r, 8),
                        CacheSize = r.IsDBNull(9) ? null : Int(r, 9),
                        LastUsedValue = Str(r, 11)
                    };
                    // before 2017 there is no last_used_value: a current value that differs from the start means it was used
                    if (major < 14 && Str(r, 10) is { } cur && cur != seq.StartValue) seq.LastUsedValue = cur;
                    schema.Sequences.Add(seq);
                }
            }

            // tables
            var tables = new Dictionary<int, DbMigrateTableDef>();
            var memOpt = major >= 12 ? "t.is_memory_optimized" : "CAST(0 AS bit)";
            var temporal = major >= 13 ? "t.temporal_type" : "CAST(0 AS tinyint)";
            var external = major >= 13 ? "t.is_external" : "CAST(0 AS bit)";
            var ledger = major >= 16 ? "t.ledger_type" : "CAST(0 AS tinyint)";
            await foreach (var r in Rows(cn, $@"
SELECT t.object_id, SCHEMA_NAME(t.schema_id), t.name, {memOpt}, {temporal}, {external}, {ledger}
FROM sys.tables t WHERE t.is_ms_shipped = 0 ORDER BY 2, 3", ct))
            {
                var t = new DbMigrateTableDef { ObjectId = Int(r, 0), Schema = r.GetString(1), Name = r.GetString(2) };
                if (Bool(r, 3)) t.Unsupported = "memory-optimized table";
                else if (Int(r, 4) != 0) t.Unsupported = "system-versioned (temporal) table";
                else if (Bool(r, 5)) t.Unsupported = "external table";
                else if (Int(r, 6) != 0) t.Unsupported = "ledger table";
                if (descriptions.TryGetValue((t.ObjectId, 0), out var d)) t.Description = d;
                tables[t.ObjectId] = t;
                schema.Tables.Add(t);
            }

            // row counts (needs VIEW DATABASE STATE; if denied, the exact count is taken at verify time)
            try
            {
                await foreach (var r in Rows(cn, "SELECT object_id, SUM(row_count) FROM sys.dm_db_partition_stats WHERE index_id IN (0, 1) GROUP BY object_id", ct))
                    if (tables.TryGetValue(Int(r, 0), out var t) && !r.IsDBNull(1)) t.RowCount = r.GetInt64(1);
            }
            catch (SqlException) { /* no permission: counts stay null */ }

            // columns of tables and of table types
            var generated = new HashSet<int>();
            await ReadColumnsAsync(cn, major, schema.Collation, "JOIN sys.tables t ON t.object_id = c.object_id AND t.is_ms_shipped = 0", descriptions,
                (obj, col) => { if (tables.TryGetValue(obj, out var t)) t.Columns.Add(col); }, generated, ct);
            await ReadColumnsAsync(cn, major, schema.Collation, "JOIN sys.table_types tt ON tt.type_table_object_id = c.object_id AND tt.is_user_defined = 1", null,
                (obj, col) => { if (tableTypes.TryGetValue(obj, out var tt)) tt.Columns.Add(col); }, new HashSet<int>(), ct);
            foreach (var id in generated)
                if (tables.TryGetValue(id, out var t) && t.Unsupported == null) t.Unsupported = "table with generated-always columns (temporal/ledger)";

            // indexes and keys (tables and indexed views)
            var views = new Dictionary<int, DbMigrateTableDef>();
            await ReadIndexesAsync(cn, major, tables, views, schema, ct);
            schema.ViewIndexHosts.AddRange(views.Values);

            // check constraints
            await foreach (var r in Rows(cn, @"
SELECT cc.parent_object_id, cc.name, cc.definition, cc.is_disabled, cc.is_not_trusted
FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id = cc.parent_object_id AND t.is_ms_shipped = 0
ORDER BY cc.parent_object_id, cc.object_id", ct))
            {
                if (tables.TryGetValue(Int(r, 0), out var t))
                    t.Checks.Add(new DbMigrateCheck { Name = r.GetString(1), Definition = Str(r, 2) ?? "", IsDisabled = Bool(r, 3), IsNotTrusted = Bool(r, 4) });
            }

            // foreign keys
            DbMigrateForeignKey? current = null;
            int currentId = -1;
            await foreach (var r in Rows(cn, @"
SELECT fk.object_id, fk.name, SCHEMA_NAME(pt.schema_id), pt.name, SCHEMA_NAME(rt.schema_id), rt.name,
       fk.delete_referential_action, fk.update_referential_action, fk.is_disabled, fk.is_not_trusted, pc.name, rc.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables pt ON pt.object_id = fk.parent_object_id AND pt.is_ms_shipped = 0
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
ORDER BY fk.object_id, fkc.constraint_column_id", ct))
            {
                if (Int(r, 0) != currentId)
                {
                    currentId = Int(r, 0);
                    current = new DbMigrateForeignKey
                    {
                        Name = r.GetString(1),
                        ChildSchema = r.GetString(2),
                        ChildTable = r.GetString(3),
                        ParentSchema = r.GetString(4),
                        ParentTable = r.GetString(5),
                        DeleteAction = Int(r, 6),
                        UpdateAction = Int(r, 7),
                        IsDisabled = Bool(r, 8),
                        IsNotTrusted = Bool(r, 9)
                    };
                    schema.ForeignKeys.Add(current);
                }
                current!.ChildColumns.Add(r.GetString(10));
                current.ParentColumns.Add(r.GetString(11));
            }

            // views, functions, procedures, triggers (definition is null for WITH ENCRYPTION)
            await foreach (var r in Rows(cn, @"
SELECT o.type, SCHEMA_NAME(o.schema_id), o.name, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
FROM sys.objects o JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR')
ORDER BY o.create_date, o.object_id", ct))
            {
                var kind = r.GetString(0).Trim() switch
                {
                    "V" => DbMigrateObjectKind.View,
                    "P" => DbMigrateObjectKind.Procedure,
                    "TR" => DbMigrateObjectKind.Trigger,
                    _ => DbMigrateObjectKind.Function
                };
                schema.Modules.Add(new DbMigrateModuleDef
                {
                    Kind = kind,
                    Schema = r.GetString(1),
                    Name = r.GetString(2),
                    Definition = Str(r, 3),
                    AnsiNulls = Bool(r, 4),
                    QuotedIdentifier = Bool(r, 5)
                });
            }

            // synonyms
            await foreach (var r in Rows(cn, "SELECT SCHEMA_NAME(schema_id), name, base_object_name FROM sys.synonyms ORDER BY 1, 2", ct))
                schema.Synonyms.Add(new DbMigrateSynonymDef { Schema = r.GetString(0), Name = r.GetString(1), BaseObject = Str(r, 2) ?? "" });

            await ReadNotesAsync(cn, schema, ct);
            return schema;
        }

        private async Task ReadColumnsAsync(SqlConnection cn, int major, string dbCollation, string joinClause,
            Dictionary<(int obj, int col), string>? descriptions, Action<int, DbMigrateColumn> add, HashSet<int> generatedTables, CancellationToken ct)
        {
            var gen = major >= 13 ? "c.generated_always_type" : "CAST(0 AS tinyint)";
            await foreach (var r in Rows(cn, $@"
SELECT c.object_id, c.column_id, c.name, ty.name, ty.is_user_defined, ty.is_assembly_type, SCHEMA_NAME(ty.schema_id),
       c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.is_rowguidcol, c.is_sparse,
       c.collation_name, c.xml_collection_id, ic.seed_value, ic.increment_value, cc.definition, cc.is_persisted,
       dc.name, dc.definition, {gen}, ic.last_value
FROM sys.columns c
{joinClause}
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
ORDER BY c.object_id, c.column_id", ct))
            {
                var obj = Int(r, 0);
                var typeName = r.GetString(3);
                var userDefined = Bool(r, 4);
                var assembly = Bool(r, 5);
                var col = new DbMigrateColumn
                {
                    Name = r.GetString(2),
                    BaseType = typeName,
                    IsNullable = Bool(r, 10),
                    IsIdentity = Bool(r, 11),
                    IsComputed = Bool(r, 12),
                    IsRowGuid = Bool(r, 13),
                    IsSparse = Bool(r, 14),
                    IsRowVersion = typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase),
                    IsClrType = assembly && userDefined
                };
                col.TypeDecl = userDefined && !assembly
                    ? Q(r.GetString(6), typeName)
                    : TypeDecl(typeName, Int(r, 7), Int(r, 8), Int(r, 9));

                var collation = Str(r, 15);
                if (collation != null && !collation.Equals(dbCollation, StringComparison.OrdinalIgnoreCase)) col.Collation = collation;
                if (col.IsIdentity)
                {
                    col.IdentitySeed = Str(r, 17) ?? "1";
                    col.IdentityIncrement = Str(r, 18) ?? "1";
                    col.IdentityLastValue = Str(r, 24);
                }
                if (col.IsComputed)
                {
                    col.ComputedDefinition = Str(r, 19) ?? "";
                    col.IsPersisted = Bool(r, 20);
                }
                col.DefaultName = Str(r, 21);
                col.DefaultDefinition = Str(r, 22);
                if (Int(r, 23) != 0) generatedTables.Add(obj);
                if (descriptions != null && descriptions.TryGetValue((obj, Int(r, 1)), out var desc)) col.Description = desc;
                add(obj, col);
            }
        }

        private async Task ReadIndexesAsync(SqlConnection cn, int major, Dictionary<int, DbMigrateTableDef> tables, Dictionary<int, DbMigrateTableDef> views, DbMigrateSchema schema, CancellationToken ct)
        {
            var seq = major >= 15 ? "i.optimize_for_sequential_key" : "CAST(0 AS bit)";
            DbMigrateIndex? idx = null;
            int curObj = -1, curIdx = -1;
            DbMigrateTableDef? host = null;
            var viewNames = new Dictionary<int, (string schema, string name)>();
            await foreach (var r in Rows(cn, "SELECT object_id, SCHEMA_NAME(schema_id), name FROM sys.views WHERE is_ms_shipped = 0", ct))
                viewNames[Int(r, 0)] = (r.GetString(1), r.GetString(2));

            await foreach (var r in Rows(cn, $@"
SELECT i.object_id, i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled, i.fill_factor, i.is_padded,
       i.ignore_dup_key, i.allow_row_locks, i.allow_page_locks, i.filter_definition, {seq}, ds.type,
       ic.is_descending_key, ic.is_included_column, c.name, o.type,
       ISNULL((SELECT TOP 1 p.data_compression FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id ORDER BY p.partition_number), 0)
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U', 'V')
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE i.is_hypothetical = 0 AND i.type IN (1, 2)
ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id", ct))
            {
                var obj = Int(r, 0);
                var id = Int(r, 1);
                if (obj != curObj || id != curIdx)
                {
                    curObj = obj; curIdx = id; idx = null; host = null;
                    var isView = r.GetString(19).Trim() == "V";
                    if (isView)
                    {
                        if (!viewNames.TryGetValue(obj, out var vn)) continue;
                        if (!views.TryGetValue(obj, out host))
                            views[obj] = host = new DbMigrateTableDef { ObjectId = obj, Schema = vn.schema, Name = vn.name, IsView = true };
                    }
                    else if (!tables.TryGetValue(obj, out host)) continue;

                    idx = new DbMigrateIndex
                    {
                        Name = r.GetString(2),
                        IsClustered = Int(r, 3) == 1,
                        IsUnique = Bool(r, 4),
                        IsPrimaryKey = Bool(r, 5),
                        IsUniqueConstraint = Bool(r, 6),
                        IsDisabled = Bool(r, 7),
                        FillFactor = Int(r, 8),
                        IsPadded = Bool(r, 9),
                        IgnoreDupKey = Bool(r, 10),
                        AllowRowLocks = Bool(r, 11),
                        AllowPageLocks = Bool(r, 12),
                        FilterDefinition = Str(r, 13),
                        OptimizeForSequentialKey = Bool(r, 14),
                        DataCompression = Math.Min(Int(r, 20), 2)
                    };
                    host.Indexes.Add(idx);
                    if (Str(r, 15) == "PS")
                        schema.CatalogNotes.Add(new DbMigrateIssue { Severity = DbMigrateSeverity.Warning, Object = $"{host.FullName}.{idx.Name}", Message = "partitioned in the source; it is created unpartitioned on the target" });
                }
                if (idx == null) continue;
                idx.Columns.Add(new DbMigrateIndexColumn { Name = r.GetString(18), Descending = Bool(r, 16), Included = Bool(r, 17) });
            }
        }

        /// <summary>Things this tool does not move: reported in the plan so nothing is lost silently.</summary>
        private async Task ReadNotesAsync(SqlConnection cn, DbMigrateSchema schema, CancellationToken ct)
        {
            void Note(DbMigrateSeverity sev, string obj, string msg) => schema.CatalogNotes.Add(new DbMigrateIssue { Severity = sev, Object = obj, Message = msg });

            await foreach (var r in Rows(cn, "SELECT SCHEMA_NAME(schema_id), name, type_desc FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PC', 'FS', 'FT', 'AF', 'TA')", ct))
                Note(DbMigrateSeverity.Warning, $"{r.GetString(0)}.{r.GetString(1)}", $"{Str(r, 2)} (SQL CLR) is not copied");
            await foreach (var r in Rows(cn, "SELECT name FROM sys.triggers WHERE parent_class = 0", ct))
                Note(DbMigrateSeverity.Warning, r.GetString(0), "database-level (DDL) trigger is not copied");
            await foreach (var r in Rows(cn, "SELECT i.name, OBJECT_NAME(i.object_id), i.type_desc FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V') WHERE i.is_hypothetical = 0 AND i.type IN (3, 4, 5, 6, 7)", ct))
                Note(DbMigrateSeverity.Warning, $"{Str(r, 1)}.{Str(r, 0)}", $"{Str(r, 2)} index is not copied");
            try
            {
                await foreach (var r in Rows(cn, "SELECT name FROM sys.fulltext_catalogs", ct))
                    Note(DbMigrateSeverity.Warning, r.GetString(0), "full-text catalog is not copied");
            }
            catch (SqlException) { /* full-text not installed */ }
            await foreach (var r in Rows(cn, "SELECT name FROM sys.xml_schema_collections WHERE xml_collection_id > 65535 AND schema_id <> 4", ct))
                Note(DbMigrateSeverity.Warning, r.GetString(0), "XML schema collection is not copied (xml columns become untyped)");
            await foreach (var r in Rows(cn, "SELECT name FROM sys.partition_functions", ct))
                Note(DbMigrateSeverity.Info, r.GetString(0), "partition function/scheme is not copied");
        }

        // -------------------------------------------------------------- tiny query helpers

        private SqlCommand Cmd(SqlConnection cn, string sql) => new(sql, cn) { CommandTimeout = 120 };

        private async IAsyncEnumerable<SqlDataReader> Rows(SqlConnection cn, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await using var cmd = Cmd(cn, sql);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) yield return r;
        }
    }
}
