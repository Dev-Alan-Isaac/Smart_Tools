using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Home page - UI only. A greeting with the search, Favorites and Recent as small tiles, a row of section chips
    /// (All or one section) and every tool as an even tile under its section's heading. Tile widths are worked out
    /// from the page width so a row is always filled edge to edge. The list itself is <see cref="ToolCatalogManager"/>.
    /// Clicking a tool raises <see cref="ToolRequested"/>; MainWindow opens it.
    /// </summary>
    public partial class HomeView : UserControl
    {
        private const double TileMinWidth = 270;
        private const double QuickTileMinWidth = 200;
        private const double TileGap = 10;          // the right margin every tile carries
        private const double PageSideMargins = 28 + 18;

        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(HomeView), new PropertyMetadata(TileMinWidth + TileGap));

        public static readonly DependencyProperty QuickTileWidthProperty =
            DependencyProperty.Register(nameof(QuickTileWidth), typeof(double), typeof(HomeView), new PropertyMetadata(QuickTileMinWidth + 8));

        /// <summary>Width of one tool tile, its gap included (bound by the tile WrapPanels).</summary>
        public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

        /// <summary>Width of one Favorites / Recent tile, its gap included.</summary>
        public double QuickTileWidth { get => (double)GetValue(QuickTileWidthProperty); set => SetValue(QuickTileWidthProperty, value); }

        private string _sectionFilter = "";          // "" = all sections
        private bool _loadedOnce;

        /// <summary>The tool id to open.</summary>
        public event Action<string>? ToolRequested;

        /// <summary>Toxy's name was typed in the search (the memorial easter egg).</summary>
        public event Action? MemorialRequested;

        public HomeView()
        {
            InitializeComponent();
            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(ToolButton_ContextMenuOpening));
            UpdateTexts();
            BuildSectionChips();
            ShowSections();
        }

        private string S(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void UpdateTexts()
        {
            int hour = DateTime.Now.Hour;
            TxtGreeting.Text = hour < 12 ? S("Str_Home_GoodMorning", "Good morning")
                             : hour < 19 ? S("Str_Home_GoodAfternoon", "Good afternoon")
                             : S("Str_Home_GoodEvening", "Good evening");
            TxtToolCount.Text = string.Format(S("Str_Home_ToolCount", "{0} tools in {1} sections. Pick one, or type what you want to do."),
                ToolCatalogManager.Tools.Count, ToolCatalogManager.Categories.Count);
        }

        /// <summary>One chip per section plus "All"; the chosen one filters the tiles below.</summary>
        private void BuildSectionChips()
        {
            SectionChips.Children.Clear();
            SectionChips.Children.Add(MakeChip("", S("Str_Home_AllSections", "All"), PackIconKind.ViewGridOutline));
            foreach (var category in ToolCatalogManager.Categories)
            {
                var kind = Enum.TryParse<PackIconKind>(category.Icon, out var k) ? k : PackIconKind.Circle;
                SectionChips.Children.Add(MakeChip(category.Id, category.Title, kind));
            }
        }

        private RadioButton MakeChip(string id, string title, PackIconKind icon)
        {
            var glyph = new PackIcon { Kind = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
            var chip = new RadioButton
            {
                GroupName = "HomeSections",
                Tag = id,
                IsChecked = id == _sectionFilter,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, text } },
            };
            chip.SetResourceReference(StyleProperty, "SectionChipStyle");
            chip.Checked += (_, _) =>
            {
                if (_sectionFilter == id) return;
                _sectionFilter = id;
                ShowSections();
                UiMotion.FadeIn(CardsHost, 6);
            };
            return chip;
        }

        private void ShowSections() =>
            CardsHost.ItemsSource = _sectionFilter.Length == 0
                ? ToolCatalogManager.Categories
                : ToolCatalogManager.Categories.Where(c => c.Id == _sectionFilter).ToList();

        /// <summary>
        /// A new language: tool and section names are plain properties (they don't notify), so the chips, the sections,
        /// Favorites, Recent and the search results are rebuilt. Called by MainWindow from LanguageManager.Changed.
        /// </summary>
        public void RefreshLanguage()
        {
            UpdateTexts();
            BuildSectionChips();
            CardsHost.ItemsSource = null;
            ShowSections();
            FavoritesList.ItemsSource = ToolCatalogManager.FavoriteTools(_favoriteIds);
            RecentList.ItemsSource = null;
            ShowRecent();
            if (TxtSearch.Text.Trim().Length > 0) TxtSearch_TextChanged(TxtSearch, null!);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTexts();
            FitTiles(ActualWidth);
            if (_loadedOnce) return;
            _loadedOnce = true;
            UiMotion.FadeIn(PageStack, 8);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => FitTiles(e.NewSize.Width);

        /// <summary>
        /// Fills each row: as many tiles as fit at their minimum width, stretched to share what is left. Favorites and
        /// Recent sit side by side on a wide page and one above the other on a narrow one.
        /// </summary>
        private void FitTiles(double width)
        {
            if (width <= 0) return;
            double usable = Math.Max(0, Math.Min(width - PageSideMargins - SystemParameters.VerticalScrollBarWidth, PageStack.MaxWidth));
            int columns = Math.Max(1, (int)(usable / (TileMinWidth + TileGap)));
            TileWidth = Math.Floor(usable / columns);

            bool recentShown = RecentPanel.Visibility == Visibility.Visible;
            bool sideBySide = recentShown && usable >= 4 * (QuickTileMinWidth + 8);
            if (QuickRow.RowDefinitions.Count == 0)
            {
                QuickRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                QuickRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetColumn(RecentPanel, sideBySide ? 1 : 0);
            Grid.SetRow(RecentPanel, sideBySide ? 0 : 1);
            RecentPanel.Margin = sideBySide ? new Thickness(0) : new Thickness(0, 8, 0, 0);
            FavoritesPanel.Margin = sideBySide ? new Thickness(0, 0, 10, 0) : new Thickness(0);
            RecentColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            double half = sideBySide ? (usable - 10) / 2 : usable;
            int quickColumns = Math.Max(1, (int)(half / (QuickTileMinWidth + 8)));
            QuickTileWidth = Math.Floor(half / quickColumns);
        }

        // =================================================================
        //  Recent
        // =================================================================

        private IReadOnlyList<string> _favoriteIds = Array.Empty<string>();

        /// <summary>The person pinned or unpinned a tool (the id); the window saves it and calls <see cref="SetFavorites"/>.</summary>
        public event Action<string>? FavoriteToggled;

        public void SetFavorites(IReadOnlyList<string> ids)
        {
            _favoriteIds = ids.ToList();
            var tools = ToolCatalogManager.FavoriteTools(ids);
            FavoritesList.ItemsSource = tools;
            TxtNoFavoritesBox.Visibility = tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowRecent();
        }

        /// <summary>
        /// Gives a tool button its Add / Remove favorite menu the first time it is right-clicked. It is built here, not in a
        /// Style setter in the XAML: the XAML compiler wires event handlers of a menu inside a Style to the wrong element,
        /// which throws when the view loads and the main window never opens.
        /// </summary>
        private void ToolButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            for (DependencyObject? d = source; d != null && d != this; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is not System.Windows.Controls.Button { DataContext: ToolInfo } button) continue;
                if (button.ContextMenu == null)
                {
                    var item = new MenuItem { Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.StarOutline } };
                    item.Click += ToolMenuFavorite_Click;
                    var menu = new ContextMenu();
                    menu.Items.Add(item);
                    menu.Opened += ToolMenu_Opened;
                    button.ContextMenu = menu;
                }
                return;
            }
        }

        /// <summary>The menu says Add or Remove depending on whether the tool under the pointer is pinned.</summary>
        private void ToolMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: ToolInfo tool } } menu || menu.Items[0] is not MenuItem item) return;
            bool pinned = ToolCatalogManager.IsFavorite(_favoriteIds, tool.Id);
            item.Header = Application.Current.TryFindResource(pinned ? "Str_Home_RemoveFavorite" : "Str_Home_AddFavorite") as string ?? (pinned ? "Remove from favorites" : "Add to favorites");
            if (item.Icon is MaterialDesignThemes.Wpf.PackIcon icon) icon.Kind = pinned ? MaterialDesignThemes.Wpf.PackIconKind.StarOff : MaterialDesignThemes.Wpf.PackIconKind.StarOutline;
        }

        private void ToolMenuFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: ToolInfo tool } } })
                FavoriteToggled?.Invoke(tool.Id);
        }

        private IReadOnlyList<ToolInfo> _recent = Array.Empty<ToolInfo>();

        public void SetRecent(IReadOnlyList<ToolInfo> recent)
        {
            _recent = recent;
            ShowRecent();
        }

        /// <summary>Recent leaves out what is already a favorite: the same tool twice in two rows is noise (Hick's Law).</summary>
        private void ShowRecent()
        {
            var shown = _recent.Where(t => !_favoriteIds.Contains(t.Id)).ToList();
            RecentList.ItemsSource = shown;
            RecentPanel.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FitTiles(ActualWidth);
        }

        // =================================================================
        //  Search
        // =================================================================

        /// <summary>Ctrl+K from anywhere: focus the search box with its text selected.</summary>
        public void FocusSearch()
        {
            Dispatcher.BeginInvoke(() =>
            {
                TxtSearch.Focus();
                Keyboard.Focus(TxtSearch);
                TxtSearch.SelectAll();
                UiMotion.Pop(SearchBar, 0.97);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private List<ToolInfo> _matches = new();

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = TxtSearch.Text.Trim();
            if (MemorialEgg.Matches(query))
            {
                TxtSearch.Clear();   // runs this handler again with an empty box, which restores the page
                MemorialRequested?.Invoke();
                return;
            }
            bool searching = query.Length > 0;
            BtnClearSearch.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

            if (!searching)
            {
                _matches = new();
                ResultsList.ItemsSource = null;
                ResultsPanel.Visibility = Visibility.Collapsed;
                NoResultsPanel.Visibility = Visibility.Collapsed;
                if (BrowsePanel.Visibility != Visibility.Visible) UiMotion.FadeVisibility(BrowsePanel, true);
                return;
            }

            _matches = ToolCatalogManager.Search(query);
            BrowsePanel.Visibility = Visibility.Collapsed;
            ResultsList.ItemsSource = _matches;
            ResultsPanel.Visibility = _matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResultsPanel.Visibility = _matches.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            string noun = _matches.Count == 1 ? S("Str_Catalog_Tool", "tool") : S("Str_Catalog_Tools", "tools");
            TxtResultsTitle.Text = string.Format(S("Str_Home_ResultsTitle", "{0} {1} for \"{2}\" - Enter opens the first"), _matches.Count, noun, query);
            TxtNoResults.Text = string.Format(S("Str_Home_NoResults", "No tool matches \"{0}\""), query);
            CardsScroll.ScrollToTop();
        }

        private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when _matches.Count > 0:
                    e.Handled = true;
                    ToolRequested?.Invoke(_matches[0].Id);
                    break;
                case Key.Escape when TxtSearch.Text.Length > 0:
                    e.Handled = true;
                    TxtSearch.Clear();
                    break;
                case Key.Down when _matches.Count > 0:
                    // Move into the results so arrow keys / Tab / Enter work there.
                    e.Handled = true;
                    ResultsList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    break;
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        // =================================================================
        //  Open a tool
        // =================================================================

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ToolInfo tool)
                ToolRequested?.Invoke(tool.Id);
        }
    }
}
