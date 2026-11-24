namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Which file the Metadata details window shows next. Kept apart from the window so the rule can be tested:
    /// the buttons wrap round (after the last file comes the first), as in Find Similar.
    /// </summary>
    public static class MetadataDetailsNavigation
    {
        /// <summary>The index <paramref name="delta"/> steps from <paramref name="index"/>, wrapping. -1 when the list is empty.</summary>
        public static int Step(int count, int index, int delta)
        {
            if (count <= 0) return -1;
            return ((index + delta) % count + count) % count;
        }

        /// <summary>Previous / next only mean something when there is another file to go to.</summary>
        public static bool CanStep(int count) => count > 1;

        /// <summary>"3 / 12" (one-based), or nothing when no file is shown.</summary>
        public static string Position(int count, int index) =>
            count <= 0 || index < 0 || index >= count ? "" : $"{index + 1} / {count}";
    }
}
