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
            AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(Root_PreviewMouseRightButtonUp), handledEventsToo: true);
            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(Root_ContextMenuOpening));
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

        // The Add / Remove favorite menu. It is built and opened here on the right-click itself. (It used to be attached to the tile
        // inside ContextMenuOpening on the first right-click, which is too late for that click: WPF has already found no menu, so
        // the first right-click on a tile - often on its text - did nothing until the mouse left and came back.) It is never put in
        // a Style setter in the XAML: the XAML compiler wires a menu's handlers inside a Style to the wrong element and the view throws.

        private void Root_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ToolUnder(e.OriginalSource as DependencyObject) is not { } hit) return;
            OpenToolMenu(hit.Tool, hit.Target);
            e.Handled = true;
        }

        /// <summary>The keyboard's menu key or Shift+F10 on a focused tile.</summary>
        private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (ToolUnder(e.OriginalSource as DependencyObject) is not { } hit) return;
            OpenToolMenu(hit.Tool, hit.Target);
            e.Handled = true;
        }

        /// <summary>
        /// The tool a clicked element belongs to (the text, the icon, the badge or the tile's border) and the tile to place the menu
        /// at. Text pieces are not always visuals, so the walk goes through the logical tree when the visual one has no parent.
        /// </summary>
        private (ToolInfo Tool, FrameworkElement Target)? ToolUnder(DependencyObject? source)
        {
            ToolInfo? tool = null;
            for (DependencyObject? d = source; d != null && d != this; d = Parent(d))
            {
                if (tool == null && d is FrameworkElement { DataContext: ToolInfo t }) tool = t;
                if (tool != null && d is System.Windows.Controls.Button { DataContext: ToolInfo } tile) return (tool, tile);
            }
            return null;
        }

        private static DependencyObject? Parent(DependencyObject d) =>
            d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);

        private void OpenToolMenu(ToolInfo tool, FrameworkElement target)
        {
            bool pinned = ToolCatalogManager.IsFavorite(_favoriteIds, tool.Id);
            var item = new MenuItem
            {
                Header = Application.Current.TryFindResource(pinned ? "Str_Home_RemoveFavorite" : "Str_Home_AddFavorite") as string
                         ?? (pinned ? "Remove from favorites" : "Add to favorites"),
                Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = pinned ? MaterialDesignThemes.Wpf.PackIconKind.StarOff : MaterialDesignThemes.Wpf.PackIconKind.StarOutline },
            };
            item.Click += (_, _) => FavoriteToggled?.Invoke(tool.Id);
            var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            menu.Items.Add(item);
            menu.IsOpen = true;
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
