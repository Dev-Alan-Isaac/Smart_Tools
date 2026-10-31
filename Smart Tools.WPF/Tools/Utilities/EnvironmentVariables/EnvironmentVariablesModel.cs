namespace Smart_Tools.WPF.Models
{
    /// <summary>User variables belong to the signed-in account; system variables to every account (changing them needs administrator rights).</summary>
    public enum EnvScope { User, System }

    public enum EnvChangeResult { Done, NeedsAdministrator, Failed }

    public enum EnvNameProblem { None, Empty, Illegal, TooLong }

    /// <summary>What is wrong with one folder in a PATH-style list. Listed by priority: only the first applicable one is reported.</summary>
    public enum EnvPathIssue { None, Empty, Duplicate, Relative, Missing }

    /// <summary>One variable as stored in the registry.</summary>
    public sealed class EnvVariable
    {
        public EnvScope Scope { get; init; }

        public string Name { get; init; } = "";

        /// <summary>The value as stored: %OTHER% references are not expanded.</summary>
        public string Value { get; init; } = "";

        /// <summary>REG_EXPAND_SZ: Windows replaces %OTHER% references when a program starts. Plain REG_SZ values are used as they are.</summary>
        public bool IsExpandable { get; init; }

        /// <summary>The value on one line, shortened, for the list.</summary>
        public string Preview => Value.Length <= 160 ? Value.ReplaceLineEndings(" ") : Value[..160].ReplaceLineEndings(" ") + "…";
    }

    /// <summary>One folder of a PATH-style value.</summary>
    public sealed class EnvPathEntry
    {
        public string Path { get; init; } = "";

        public EnvPathIssue Issue { get; init; }

        public bool HasIssue => Issue != EnvPathIssue.None;

        /// <summary>Display text for <see cref="Issue"/>; filled in by the view (it knows the language).</summary>
        public string IssueText { get; set; } = "";
    }

    /// <summary>What a variable looked like before the last change, so the change can be undone. A null <see cref="OldValue"/> means it didn't exist.</summary>
    public sealed record EnvChange(EnvScope Scope, string Name, string? OldValue, bool OldExpandable);
}
