using Smart_Tools.WPF.Models;
using System.Text.RegularExpressions;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Database Migrator - what changes between SQL Server versions. Pure logic, no WPF, no I/O.
    ///
    /// Rule of thumb: this tool only writes DDL options that the target version understands (an option
    /// the target lacks is left out and reported), a feature that changes meaning is never guessed (the
    /// table is skipped and reported), and a version this table doesn't know is treated as "supports it".
    /// </summary>
    public sealed partial class DbMigrateManager
    {
        /// <summary>Oldest major version this tool will write to or read from (SQL Server 2008).</summary>
        internal const int MinSupportedMajor = 10;

        /// <summary>Highest compatibility level a server of this major version accepts (110 = 2012, 150 = 2019, 170 = 2025, ...).</summary>
        internal static int MaxCompatibilityLevel(int major) => major * 10;

        /// <summary>Lowest level the tool will ever set (older levels are out of support on current servers).</summary>
        private const int MinCompatibilityLevel = 100;

        /// <summary>Data types and the first major version that has them.</summary>
        private static readonly Dictionary<string, int> TypeMinMajor = new(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = 10, ["time"] = 10, ["datetime2"] = 10, ["datetimeoffset"] = 10,
            ["hierarchyid"] = 10, ["geography"] = 10, ["geometry"] = 10,
            ["json"] = 17, ["vector"] = 17
        };

        /// <summary>T-SQL a view, function, procedure or trigger may use that older servers lack. A hint only: the real test is the CREATE.</summary>
        private static readonly (Regex Pattern, string Feature, int MinMajor)[] CodeFeatures =
        {
            (new Regex(@"\b(STRING_SPLIT|OPENJSON|JSON_VALUE|JSON_QUERY|JSON_MODIFY|ISJSON)\s*\(|\bFOR\s+JSON\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "JSON / STRING_SPLIT", 13),
            (new Regex(@"\bDROP\s+\w+\s+IF\s+EXISTS\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "DROP ... IF EXISTS", 13),
            (new Regex(@"\b(STRING_AGG|CONCAT_WS|TRANSLATE|TRIM)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled), "STRING_AGG / CONCAT_WS / TRANSLATE / TRIM", 14),
            (new Regex(@"\b(DATETRUNC|DATE_BUCKET|GENERATE_SERIES|GREATEST|LEAST|JSON_OBJECT|JSON_ARRAY|APPROX_PERCENTILE_CONT)\s*\(|\bIS\s+(NOT\s+)?DISTINCT\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "SQL Server 2022 functions", 16),
            (new Regex(@"\b(REGEXP_\w+|VECTOR_\w+|AI_\w+)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled), "SQL Server 2025 functions", 17),
        };

        private static readonly Regex CreateOrAlter = new(@"\bCREATE\s+OR\s+ALTER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A definition saved as CREATE OR ALTER is written as CREATE (the target database is empty, so it is the same thing, and older servers accept it).</summary>
        internal static string NormalizeModule(string definition) => CreateOrAlter.Replace(definition, "CREATE", 1);

        /// <summary>Compatibility level to set on the target database.</summary>
        internal static int PickCompatibilityLevel(int sourceLevel, int targetMajor) =>
            Math.Max(MinCompatibilityLevel, Math.Min(sourceLevel <= 0 ? MaxCompatibilityLevel(targetMajor) : sourceLevel, MaxCompatibilityLevel(targetMajor)));

        /// <summary>Data compression needs Enterprise before 2016, and exists from 2008.</summary>
        internal static bool CompressionAllowed(int targetMajor, int targetEngineEdition) =>
            targetMajor >= 13 || (targetMajor >= 10 && targetEngineEdition == 3);

        /// <summary>
        /// Checks the source schema against the target version. Marks tables that cannot be copied
        /// (<see cref="DbMigrateTableDef.Unsupported"/>) and returns what the user must know before running.
        /// </summary>
        internal static List<DbMigrateIssue> Analyze(DbMigrateSchema schema, DbMigrateServerInfo source, DbMigrateServerInfo target, DbMigrateOptions options)
        {
            var issues = new List<DbMigrateIssue>();
            void Add(DbMigrateSeverity sev, string obj, string msg) => issues.Add(new DbMigrateIssue { Severity = sev, Object = obj, Message = msg });

            if (target.Major < MinSupportedMajor)
                Add(DbMigrateSeverity.Blocker, target.ServerName, $"target is SQL Server {target.Major}; this tool needs version {MinSupportedMajor} (2008) or newer");
            if (source.Major < MinSupportedMajor)
                Add(DbMigrateSeverity.Blocker, source.ServerName, $"source is SQL Server {source.Major}; this tool needs version {MinSupportedMajor} (2008) or newer");

            if (source.Major != target.Major)
                Add(DbMigrateSeverity.Info, "versions", target.Major < source.Major
                    ? $"moving DOWN from {source.Major} to {target.Major}: options and types the older server lacks are left out or the table is skipped (listed below)"
                    : $"moving UP from {source.Major} to {target.Major}");

            // tables the tool can't move
            foreach (var t in schema.Tables)
            {
                if (t.Unsupported != null) { Add(DbMigrateSeverity.Warning, t.FullName, $"skipped: {t.Unsupported}"); continue; }
                foreach (var c in t.Columns)
                {
                    if (c.IsClrType)
                    {
                        t.Unsupported = $"column {c.Name} uses a CLR user type";
                        Add(DbMigrateSeverity.Warning, t.FullName, $"skipped: {t.Unsupported}");
                        break;
                    }
                    if (TypeMinMajor.TryGetValue(c.BaseType, out var min) && target.Major < min)
                    {
                        t.Unsupported = $"column {c.Name} uses type {c.BaseType} (needs SQL Server {min}+)";
                        Add(DbMigrateSeverity.Warning, t.FullName, $"skipped: {t.Unsupported}");
                        break;
                    }
                    if (c.IsSparse && target.Major < 10) { c.IsSparse = false; Add(DbMigrateSeverity.Info, $"{t.FullName}.{c.Name}", "SPARSE not supported on target; column created as normal"); }
                }
                if (t.Unsupported == null)
                {
                    foreach (var x in t.Indexes)
                    {
                        if (x.OptimizeForSequentialKey && target.Major < 15)
                            Add(DbMigrateSeverity.Info, $"{t.FullName}.{x.Name}", "OPTIMIZE_FOR_SEQUENTIAL_KEY left out (target is older than 2019)");
                        if (x.DataCompression > 0 && !CompressionAllowed(target.Major, target.EngineEdition))
                            Add(DbMigrateSeverity.Warning, $"{t.FullName}.{x.Name}", "data compression left out (not available on the target edition/version)");
                    }
                }
            }

            // views/functions/procedures/triggers
            foreach (var m in schema.Modules)
            {
                if (m.Definition == null)
                {
                    Add(DbMigrateSeverity.Warning, m.FullName, $"{m.Kind} is WITH ENCRYPTION: its text can't be read, so it is not copied");
                    continue;
                }
                foreach (var (pattern, feature, min) in CodeFeatures)
                {
                    if (target.Major < min && pattern.IsMatch(m.Definition))
                        Add(DbMigrateSeverity.Warning, m.FullName, $"uses {feature} (needs SQL Server {min}+); creating it on the target may fail");
                }
            }

            if (schema.Sequences.Count > 0 && target.Major < 11)
                Add(DbMigrateSeverity.Warning, "sequences", "the target has no sequences (needs SQL Server 2012+); they are not copied and defaults that use them will fail");

            issues.AddRange(schema.CatalogNotes);

            if (!options.IncludeCode && schema.Modules.Count > 0)
                Add(DbMigrateSeverity.Info, "code", $"{schema.Modules.Count} view/function/procedure/trigger object(s) will NOT be copied (turned off)");
            return issues;
        }
    }
}
