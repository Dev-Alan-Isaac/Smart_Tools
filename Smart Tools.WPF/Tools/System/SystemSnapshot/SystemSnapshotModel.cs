namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// One section of the snapshot as a plain table, so one grid and one exporter serve every section.
    /// <see cref="Id"/> and <see cref="Title"/> are English and stable: files from two PCs line up even if the apps run in different languages.
    /// </summary>
    public sealed record SnapshotTable(string Id, string Title, string Icon, string[] Headers, List<string[]> Rows, string? Note = null);

    /// <summary>Everything one scan found. <see cref="Complete"/> is false when the scan was cancelled part-way.</summary>
    public sealed record SystemSnapshot(DateTime CapturedAt, string MachineName, string UserName, bool IsAdministrator, bool Complete, List<SnapshotTable> Tables)
    {
        /// <summary>Bumped if the layout of the JSON ever changes.</summary>
        public int Format { get; init; } = 1;
    }

    /// <summary>Sent while scanning: how far along it is and, when a group just finished, its tables.</summary>
    public sealed record SnapshotProgress(int Done, int Total, string Title, IReadOnlyList<SnapshotTable>? Finished);

    /// <summary>A group of sections that is scanned (and ticked in the sidebar) together.</summary>
    public sealed record SnapshotGroup(string Id, string Title, string Icon, bool OnByDefault, string[] SectionIds);

    /// <summary>What to scan. Group ids come from <c>SystemSnapshotManager.Groups</c>.</summary>
    public sealed class SnapshotOptions
    {
        public HashSet<string> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Scheduled tasks under \Microsoft\ (hundreds of them, almost all Windows' own).</summary>
        public bool IncludeMicrosoftTasks { get; init; }

        /// <summary>Firewall rules Windows ships with (rules whose name is a resource string like "@FirewallAPI.dll,-25001").</summary>
        public bool IncludeBuiltInFirewallRules { get; init; }
    }

    /// <summary>How the tool was set up last time. Only choices - never paths or scan results.</summary>
    public sealed class SystemSnapshotSettings
    {
        public List<string>? Groups { get; set; }
        public bool IncludeMicrosoftTasks { get; set; }
        public bool IncludeBuiltInFirewallRules { get; set; }
        public bool SidebarCollapsed { get; set; }
    }
}
