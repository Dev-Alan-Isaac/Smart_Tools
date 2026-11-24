namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// What the Media Viewer's Play button does when files are listed but none has been chosen yet.
    /// Kept apart from the view so the rule can be tested; the view only asks.
    /// </summary>
    public static class PlayStartRules
    {
        /// <summary>The button works for the current item, and also whenever there is a list to start from.</summary>
        public static bool CanPress(bool hasCurrent, int itemCount) => hasCurrent || itemCount > 0;

        /// <summary>The row to start: the highlighted one, else the top. -1 when the list is empty.</summary>
        public static int StartIndex(int itemCount, int selectedIndex)
        {
            if (itemCount <= 0) return -1;
            return selectedIndex >= 0 && selectedIndex < itemCount ? selectedIndex : 0;
        }
    }
}
