using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataGrid = System.Windows.Controls.DataGrid;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Adds the shared bulk check/uncheck items (check all, uncheck all, check or
    /// uncheck highlighted, invert) to a checkbox grid's right-click menu, so a list
    /// of thousands of files can be ticked in a few clicks. The rules live in
    /// <see cref="CheckSelectionHelper"/>.
    /// </summary>
    public static class BulkCheckMenu
    {
        /// <param name="items">All rows (a delegate because views replace their collection on every scan).</param>
        /// <param name="eligible">Rows that may change; null means every row.</param>
        /// <param name="canRun">False while the tool is busy; the items are then disabled.</param>
        /// <param name="applied">Called once after the change so the view can refresh counts and buttons.</param>
        public static void Append<T>(
            ContextMenu menu,
            DataGrid grid,
            Func<IEnumerable<T>> items,
            Func<T, bool> isChecked,
            Action<T, bool> setChecked,
            Action applied,
            Func<T, bool>? eligible = null,
            Func<bool>? canRun = null)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());

            var entries = new List<(MenuItem Item, CheckAction Action)>();

            void Add(CheckAction action, string key, PackIconKind icon)
            {
                var item = new MenuItem { Header = Header(key, icon) };
                item.Click += (_, _) =>
                {
                    if (canRun != null && !canRun()) return;
                    var highlighted = grid.SelectedItems.OfType<T>();
                    if (CheckSelectionHelper.Apply(items(), highlighted, action, isChecked, setChecked, eligible) > 0)
                        applied();
                };
                menu.Items.Add(item);
                entries.Add((item, action));
            }

            Add(CheckAction.CheckAll, "Str_Common_CheckAll", PackIconKind.CheckboxMultipleMarkedOutline);
            Add(CheckAction.UncheckAll, "Str_Common_UncheckAll", PackIconKind.CheckboxMultipleBlankOutline);
            Add(CheckAction.CheckHighlighted, "Str_Common_CheckHighlighted", PackIconKind.CheckboxMarkedOutline);
            Add(CheckAction.UncheckHighlighted, "Str_Common_UncheckHighlighted", PackIconKind.CheckboxBlankOutline);
            Add(CheckAction.Invert, "Str_Common_InvertChecks", PackIconKind.SwapHorizontal);

            // Highlighted actions need highlighted rows; everything waits while the tool is busy.
            menu.Opened += (_, _) =>
            {
                bool run = canRun?.Invoke() ?? true;
                bool any = items().Any();
                bool highlighted = grid.SelectedItems.Count > 0;
                foreach (var (item, action) in entries)
                    item.IsEnabled = run && any && (action is not (CheckAction.CheckHighlighted or CheckAction.UncheckHighlighted) || highlighted);
            };
        }

        private static StackPanel Header(string resourceKey, PackIconKind icon)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.TextProperty, resourceKey);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new PackIcon { Kind = icon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(text);
            return panel;
        }
    }
}
