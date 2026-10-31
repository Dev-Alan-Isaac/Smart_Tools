namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What a bulk check/uncheck menu item does to a list's checkboxes.</summary>
    public enum CheckAction
    {
        CheckAll,
        UncheckAll,
        CheckHighlighted,
        UncheckHighlighted,
        Invert,
    }

    /// <summary>
    /// Bulk check/uncheck over any list of rows - logic only, no WPF. The
    /// "highlighted" rows are the ones the user selected in the grid (Shift or
    /// Ctrl-click, drag), which is not the same as the ticked ones.
    /// </summary>
    public static class CheckSelectionHelper
    {
        /// <summary>
        /// Applies <paramref name="action"/> and returns how many rows changed.
        /// Rows that fail <paramref name="eligible"/> (already deleted, shredded...) are left as they are.
        /// </summary>
        public static int Apply<T>(
            IEnumerable<T> all,
            IEnumerable<T> highlighted,
            CheckAction action,
            Func<T, bool> isChecked,
            Action<T, bool> setChecked,
            Func<T, bool>? eligible = null)
        {
            var targets = action is CheckAction.CheckHighlighted or CheckAction.UncheckHighlighted ? highlighted : all;
            int changed = 0;
            foreach (var item in targets.ToList())
            {
                if (eligible != null && !eligible(item)) continue;

                bool current = isChecked(item);
                bool wanted = action switch
                {
                    CheckAction.CheckAll or CheckAction.CheckHighlighted => true,
                    CheckAction.UncheckAll or CheckAction.UncheckHighlighted => false,
                    _ => !current,
                };
                if (wanted == current) continue;

                setChecked(item, wanted);
                changed++;
            }
            return changed;
        }
    }
}
