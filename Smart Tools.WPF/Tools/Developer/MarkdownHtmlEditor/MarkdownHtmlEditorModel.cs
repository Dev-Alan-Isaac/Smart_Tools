namespace Smart_Tools.WPF.Models
{
    public enum MdHtmlFormat { Markdown, Html }

    public enum MdHtmlLayout { Editor, Split, Preview }

    public enum MdHtmlPreviewTheme { App, Light, Dark }

    public enum MdHtmlCommand
    {
        Heading1,
        Heading2,
        Bold,
        Italic,
        Strikethrough,
        BulletList,
        NumberedList,
        Quote,
        Code,
        Link,
        Image,
        Table,
    }

    /// <summary>How the tool is set up. Never holds file paths.</summary>
    public sealed class MdHtmlEditorSettings
    {
        public MdHtmlLayout Layout { get; set; } = MdHtmlLayout.Split;
        public MdHtmlPreviewTheme PreviewTheme { get; set; } = MdHtmlPreviewTheme.App;
        public bool SyncScroll { get; set; } = true;
        public bool WordWrap { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
    }

    /// <summary>CSS colours (#RRGGBB) for the rendered page.</summary>
    public sealed record MdHtmlPreviewColors(
        string Background,
        string Text,
        string Muted,
        string Border,
        string Accent,
        string CodeBackground)
    {
        public static MdHtmlPreviewColors Light { get; } = new("#FFFFFF", "#1F2328", "#59636E", "#D1D9E0", "#0969DA", "#F6F8FA");
        public static MdHtmlPreviewColors Dark { get; } = new("#0D1117", "#E6EDF3", "#9198A1", "#3D444D", "#4493F8", "#151B23");
    }

    /// <summary>
    /// A toolbar edit, expressed so the view can apply it through the TextBox
    /// selection (keeping Ctrl+Z working): replace [ReplaceStart, +ReplaceLength)
    /// with Replacement, then select [SelectStart, +SelectLength).
    /// </summary>
    public sealed record MdHtmlEdit(int ReplaceStart, int ReplaceLength, string Replacement, int SelectStart, int SelectLength);

    public readonly record struct MdHtmlCaret(int Line, int Column);

    public readonly record struct MdHtmlStats(int Words, int Characters, int Lines);
}
