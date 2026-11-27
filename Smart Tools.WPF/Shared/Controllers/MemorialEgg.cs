namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The memorial easter egg: typing Toxy's name in a tool search (Home or Ctrl+K) opens the memorial page.
    /// Not listed anywhere and not a catalog tool, so it never shows up as a search result.
    /// </summary>
    public static class MemorialEgg
    {
        /// <summary>The page id MainWindow opens for it.</summary>
        public const string PageId = "memorial";

        private const string Word = "toxy";

        /// <summary>True when the search text is exactly the name (any case, spaces around ignored).</summary>
        public static bool Matches(string? text) => string.Equals(text?.Trim(), Word, StringComparison.OrdinalIgnoreCase);
    }
}
