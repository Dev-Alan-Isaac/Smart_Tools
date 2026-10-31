using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // Database Migrator - plain data only (no WPF types, no I/O).
    // Backend: Controllers/DbMigrateManager*.cs. UI: Views/DbMigrateView.

    public enum DbMigrateAuth { Windows, Sql }

    public enum DbMigrateDirection { Same, Upgrade, Downgrade }

    public enum DbMigrateSeverity { Info, Warning, Blocker }

    public enum DbMigrateObjectKind { Schema, Type, Sequence, Table, Index, Constraint, View, Function, Procedure, Trigger, Synonym, Property }

    public enum DbMigrateStepState { Pending, Running, Done, Skipped, Failed }

    public enum DbMigrateStage { Idle, Connecting, Planning, Structure, Data, Indexes, Code, Finishing, Verifying, Done, Cancelled, Failed }

    // ------------------------------------------------------------ connection

    /// <summary>One side of the move. The password lives in memory only and is never saved.</summary>
    public sealed class DbMigrateEndpoint
    {
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public DbMigrateAuth Auth { get; set; } = DbMigrateAuth.Windows;
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public bool TrustServerCertificate { get; set; } = true;
    }

    public sealed class DbMigrateServerInfo
    {
        public string ProductVersion { get; set; } = "";
        public int Major { get; set; }
        public string Edition { get; set; } = "";
        public int EngineEdition { get; set; }
        public string Collation { get; set; } = "";
        public int CompatibilityLevel { get; set; }
        public string ServerName { get; set; } = "";

        /// <summary>"SQL Server 2022 (16.0.4135)" style text for the UI and log.</summary>
        public string Display => $"{ServerName} - v{ProductVersion} ({Edition})";
    }

    // ------------------------------------------------------------ options

    public sealed class DbMigrateOptions
    {
        /// <summary>true: CREATE DATABASE on the target. false: use the existing (empty) database named in the target endpoint.</summary>
        public bool CreateTargetDatabase { get; set; } = true;
        public bool IncludeData { get; set; } = true;
        public bool IncludeCode { get; set; } = true;          // views, functions, procedures, triggers
        public bool IncludeIndexes { get; set; } = true;
        public bool VerifyChecksums { get; set; }
        public bool DropCreatedDatabaseOnFailure { get; set; }
        public int BatchSize { get; set; } = 5000;
        public int Parallelism { get; set; } = 2;
        public int CommandTimeoutSeconds { get; set; } = 0;    // 0 = no limit (big tables)
    }

    /// <summary>Remembered between sessions: server, database, sign-in mode and user. Never passwords.</summary>
    public sealed class DbMigrateSettings
    {
        public string SourceServer { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public DbMigrateAuth SourceAuth { get; set; } = DbMigrateAuth.Windows;
        public string SourceUser { get; set; } = "";
        public bool SourceTrustCert { get; set; } = true;
        public string TargetServer { get; set; } = "";
        public string TargetDatabase { get; set; } = "";
        public DbMigrateAuth TargetAuth { get; set; } = DbMigrateAuth.Windows;
        public string TargetUser { get; set; } = "";
        public bool TargetTrustCert { get; set; } = true;
        public bool CreateTargetDatabase { get; set; } = true;
        public bool IncludeData { get; set; } = true;
        public bool IncludeCode { get; set; } = true;
        public bool IncludeIndexes { get; set; } = true;
        public bool VerifyChecksums { get; set; }
        public int BatchSize { get; set; } = 5000;
        public int Parallelism { get; set; } = 2;
        public bool OptionsCollapsed { get; set; }
    }

    // ------------------------------------------------------------ catalog (what the source database contains)

    public sealed class DbMigrateColumn
    {
        public string Name { get; set; } = "";
        /// <summary>Full type text ready for DDL: nvarchar(50), decimal(18,2), [dbo].[MyAlias] ...</summary>
        public string TypeDecl { get; set; } = "";
        /// <summary>Plain type name (nvarchar, xml, geography, rowversion, ...) used for version checks and copy rules.</summary>
        public string BaseType { get; set; } = "";
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public string IdentitySeed { get; set; } = "1";
        public string IdentityIncrement { get; set; } = "1";
        /// <summary>Last identity value handed out in the source, or null when never used (used to reseed the target).</summary>
        public string? IdentityLastValue { get; set; }
        public bool IsComputed { get; set; }
        public string ComputedDefinition { get; set; } = "";
        public bool IsPersisted { get; set; }
        public string? Collation { get; set; }
        public bool IsRowGuid { get; set; }
        public bool IsSparse { get; set; }
        public bool IsRowVersion { get; set; }
        public bool IsClrType { get; set; }
        public string? DefaultName { get; set; }
        public string? DefaultDefinition { get; set; }
        public string? Description { get; set; }

        /// <summary>Copied by the data pass (not computed, not rowversion).</summary>
        public bool IsCopied => !IsComputed && !IsRowVersion;
    }

    public sealed class DbMigrateIndexColumn
    {
        public string Name { get; set; } = "";
        public bool Descending { get; set; }
        public bool Included { get; set; }
    }

    public sealed class DbMigrateIndex
    {
        public string Name { get; set; } = "";
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsUniqueConstraint { get; set; }
        public bool IsClustered { get; set; }
        public bool IsDisabled { get; set; }
        public int FillFactor { get; set; }
        public bool IsPadded { get; set; }
        public bool IgnoreDupKey { get; set; }
        public bool AllowRowLocks { get; set; } = true;
        public bool AllowPageLocks { get; set; } = true;
        public bool OptimizeForSequentialKey { get; set; }
        public string? FilterDefinition { get; set; }
        /// <summary>0 none, 1 row, 2 page (partition 1).</summary>
        public int DataCompression { get; set; }
        public List<DbMigrateIndexColumn> Columns { get; } = new();
    }

    public sealed class DbMigrateCheck
    {
        public string Name { get; set; } = "";
        public string Definition { get; set; } = "";
        public bool IsDisabled { get; set; }
        public bool IsNotTrusted { get; set; }
    }

    public sealed class DbMigrateForeignKey
    {
        public string Name { get; set; } = "";
        public string ChildSchema { get; set; } = "";
        public string ChildTable { get; set; } = "";
        public string ParentSchema { get; set; } = "";
        public string ParentTable { get; set; } = "";
        public List<string> ChildColumns { get; } = new();
        public List<string> ParentColumns { get; } = new();
        /// <summary>0 NO ACTION, 1 CASCADE, 2 SET NULL, 3 SET DEFAULT.</summary>
        public int DeleteAction { get; set; }
        public int UpdateAction { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsNotTrusted { get; set; }
    }

    public sealed class DbMigrateTableDef
    {
        public int ObjectId { get; set; }
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        /// <summary>Why this table can't be copied (memory-optimized, temporal, ...). Null = supported.</summary>
        public string? Unsupported { get; set; }
        public bool IsView { get; set; }
        public long? RowCount { get; set; }
        public List<DbMigrateColumn> Columns { get; } = new();
        public List<DbMigrateIndex> Indexes { get; } = new();
        public List<DbMigrateCheck> Checks { get; } = new();
        public string FullName => $"{Schema}.{Name}";
    }

    public sealed class DbMigrateModuleDef
    {
        public DbMigrateObjectKind Kind { get; set; }
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Definition { get; set; }          // null: WITH ENCRYPTION
        public bool AnsiNulls { get; set; } = true;
        public bool QuotedIdentifier { get; set; } = true;
        public string FullName => $"{Schema}.{Name}";
    }

    public sealed class DbMigrateTypeDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsTableType { get; set; }
        /// <summary>Alias type: the base type text, e.g. varchar(20).</summary>
        public string BaseDecl { get; set; } = "";
        public bool IsNullable { get; set; } = true;
        public List<DbMigrateColumn> Columns { get; } = new();
    }

    public sealed class DbMigrateSequenceDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "bigint";
        public string StartValue { get; set; } = "1";
        public string Increment { get; set; } = "1";
        public string? MinValue { get; set; }
        public string? MaxValue { get; set; }
        public bool IsCycling { get; set; }
        public bool IsCached { get; set; }
        public int? CacheSize { get; set; }
        /// <summary>Last value handed out, or null when the sequence was never used.</summary>
        public string? LastUsedValue { get; set; }
    }

    public sealed class DbMigrateSynonymDef
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string BaseObject { get; set; } = "";
    }

    /// <summary>Everything read from the source database, in memory.</summary>
    public sealed class DbMigrateSchema
    {
        public string Collation { get; set; } = "";
        public int CompatibilityLevel { get; set; }
        public List<string> Schemas { get; } = new();
        public List<DbMigrateTypeDef> Types { get; } = new();
        public List<DbMigrateSequenceDef> Sequences { get; } = new();
        public List<DbMigrateTableDef> Tables { get; } = new();        // tables only
        public List<DbMigrateTableDef> ViewIndexHosts { get; } = new(); // views that carry indexes (indexed views)
        public List<DbMigrateForeignKey> ForeignKeys { get; } = new();
        public List<DbMigrateModuleDef> Modules { get; } = new();
        public List<DbMigrateSynonymDef> Synonyms { get; } = new();
        /// <summary>Things that exist in the source but this tool does not move (CLR, columnstore, partitioning, ...).</summary>
        public List<DbMigrateIssue> CatalogNotes { get; } = new();
    }

    // ------------------------------------------------------------ plan and run

    public sealed class DbMigrateIssue
    {
        public DbMigrateSeverity Severity { get; set; }
        public string Object { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>One table row in the plan list; updated live while the data is copied.</summary>
    public sealed class DbMigrateTableItem : INotifyPropertyChanged
    {
        private bool _include = true;
        private DbMigrateStepState _state = DbMigrateStepState.Pending;
        private string _message = "";
        private long _rowsCopied;

        public required string Schema { get; init; }
        public required string Name { get; init; }
        public long? RowCount { get; init; }
        public string? Unsupported { get; init; }
        public string FullName => $"{Schema}.{Name}";

        public bool Include { get => _include; set => Set(ref _include, value); }
        public DbMigrateStepState State { get => _state; set => Set(ref _state, value); }
        public string Message { get => _message; set => Set(ref _message, value); }
        public long RowsCopied { get => _rowsCopied; set { if (Set(ref _rowsCopied, value)) Raise(nameof(Progress)); } }

        /// <summary>0..1 for the row bar.</summary>
        public double Progress => RowCount is > 0 ? Math.Min(1.0, (double)_rowsCopied / RowCount.Value) : (State == DbMigrateStepState.Done ? 1.0 : 0.0);

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class DbMigratePlan
    {
        public required DbMigrateServerInfo Source { get; init; }
        public required DbMigrateServerInfo Target { get; init; }
        public required DbMigrateSchema Schema { get; init; }
        public required DbMigrateDirection Direction { get; init; }
        public List<DbMigrateIssue> Issues { get; } = new();
        public List<DbMigrateTableItem> Tables { get; } = new();
        /// <summary>Compatibility level to set on the target: the lower of the source's and the target server's maximum.</summary>
        public int TargetCompatibilityLevel { get; set; }
        public bool HasBlockers => Issues.Any(i => i.Severity == DbMigrateSeverity.Blocker);

        public int ObjectCount(DbMigrateObjectKind kind) => kind switch
        {
            DbMigrateObjectKind.Schema => Schema.Schemas.Count,
            DbMigrateObjectKind.Type => Schema.Types.Count,
            DbMigrateObjectKind.Sequence => Schema.Sequences.Count,
            DbMigrateObjectKind.Table => Schema.Tables.Count,
            DbMigrateObjectKind.Synonym => Schema.Synonyms.Count,
            DbMigrateObjectKind.Index => Schema.Tables.Sum(t => t.Indexes.Count) + Schema.ViewIndexHosts.Sum(t => t.Indexes.Count),
            DbMigrateObjectKind.Constraint => Schema.ForeignKeys.Count + Schema.Tables.Sum(t => t.Checks.Count),
            _ => Schema.Modules.Count(m => m.Kind == kind)
        };
    }

    public sealed class DbMigrateProgress
    {
        public DbMigrateStage Stage { get; init; }
        public string Message { get; init; } = "";
        public int TablesDone { get; init; }
        public int TablesFailed { get; init; }
        public int TablesTotal { get; init; }
        public long RowsDone { get; init; }
        public long RowsTotal { get; init; }
        public int ObjectsDone { get; init; }
        public int ObjectsFailed { get; init; }
    }

    public sealed class DbMigrateLogLine
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public DbMigrateSeverity Severity { get; init; }
        /// <summary>Short source chip: SOURCE, TARGET, SCHEMA, DATA, CODE, VERIFY, ...</summary>
        public string Source { get; init; } = "";
        public string Text { get; init; } = "";
        public string TimeText => Time.ToString("HH:mm:ss");
    }

    public sealed class DbMigrateVerifyRow
    {
        public string Table { get; init; } = "";
        public long SourceRows { get; init; }
        public long TargetRows { get; init; }
        public bool? ChecksumMatch { get; init; }
        public bool Ok => SourceRows == TargetRows;
    }

    public sealed class DbMigrateResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public bool DatabaseCreated { get; set; }
        public bool DatabaseDropped { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int TablesCopied { get; set; }
        public int TablesFailed { get; set; }
        public long RowsCopied { get; set; }
        public int ObjectsCreated { get; set; }
        public int ObjectsFailed { get; set; }
        public List<string> Failures { get; } = new();
        public List<DbMigrateVerifyRow> Verify { get; } = new();
    }
}
