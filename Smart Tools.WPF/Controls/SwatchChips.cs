using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Builds the small clickable colour chips (favorites, recent colours) shared by the
    /// Colour tools, with the right-click menu items they have in common. The tools only
    /// say what a click does and which extra menu items they want.
    /// </summary>
    internal static class SwatchChips
    {
        /// <summary>Replaces the chips in <paramref name="panel"/>; shows <paramref name="emptyHint"/> when there are none.</summary>
        public static void Rebuild(
            Panel panel,
            UIElement emptyHint,
            IReadOnlyList<string> hexes,
            Action<RgbColor, string> onClick,
            Func<RgbColor, string, IEnumerable<MenuItem>> menuItems,
            string? tipKey = null)
        {
            panel.Children.Clear();
            emptyHint.Visibility = hexes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (string hex in hexes)
            {
                if (!ColorManager.TryHexToRgb(hex, out var rgb)) continue;

                string tip = tipKey is null ? hex : hex + "\n" + Text(tipKey);
                var button = new Button { Background = Fill(rgb), Tag = hex, ToolTip = tip };
                button.SetResourceReference(FrameworkElement.StyleProperty, "SwatchButtonStyle");
                button.Click += (_, _) => onClick(rgb, hex);
                button.ContextMenu = new ContextMenu { ItemsSource = menuItems(rgb, hex).ToArray() };
                panel.Children.Add(button);
            }
        }

        public static MenuItem Item(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            return item;
        }

        /// <summary>"Add to favorites" / "Remove from favorites" depending on the colour's current state.</summary>
        public static string FavoriteHeader(ColorHistoryStore history, RgbColor color) =>
            Text(history.IsFavorite(color) ? "Str_Color_RemoveFromFavorites" : "Str_Color_MenuAddToFavorites");

        public static string Text(string key) => Application.Current.TryFindResource(key) as string ?? key;

        public static Brush Fill(RgbColor c)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }
}
