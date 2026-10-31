using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Home page - UI only. Every section is a card of its tools (laid out
    /// in as many columns as fit, shortest column first), with search and
    /// Recent on top. The list itself is <see cref="ToolCatalogManager"/>.
    /// Clicking a tool raises <see cref="ToolRequested"/>; MainWindow opens it.
    /// </summary>
    public partial class HomeView : UserControl
    {
        private const double CardMinWidth = 330;
        private const double ColumnGap = 14;

        private readonly List<ContentControl> _cards = new();
        private int _columns;
        private bool _loadedOnce;

        /// <summary>The tool id to open.</summary>
        public event Action<string>? ToolRequested;

        public HomeView()
        {
            InitializeComponent();
            UpdateToolCount();
            BuildCards();
        }

        private string S(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void UpdateToolCount() =>
            TxtToolCount.Text = string.Format(S("Str_Home_ToolCount", "{0} tools in {1} sections - pick one, or search."),
                ToolCatalogManager.Tools.Count, ToolCatalogManager.Categories.Count);

        private void BuildCards()
        {
            var template = (DataTemplate)FindResource("CategoryCardTemplate");
            _cards.Clear();
            foreach (var category in ToolCatalogManager.Categories)
                _cards.Add(new ContentControl { Content = category, ContentTemplate = template, Focusable = false });
        }

        /// <summary>
        /// A new language: tool and section names are plain properties (they don't notify), so the cards and the
        /// search results are rebuilt. Called by MainWindow from LanguageManager.Changed.
        /// </summary>
        public void RefreshLanguage()
        {
            UpdateToolCount();
            BuildCards();
            LayoutCards(ActualWidth, force: true);
            if (TxtSearch.Text.Trim().Length > 0) TxtSearch_TextChanged(TxtSearch, null!);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            LayoutCards(ActualWidth, force: true);
            if (_loadedOnce) return;
            _loadedOnce = true;
            for (int i = 0; i < CardsHost.Children.Count; i++)
                if (CardsHost.Children[i] is FrameworkElement column) UiMotion.FadeIn(column, 8 + i * 4);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCards(e.NewSize.Width, force: false);

        /// <summary>Puts the section cards into as many columns as fit, each card in the currently shortest column.</summary>
        private void LayoutCards(double width, bool force)
        {
            double usable = Math.Max(0, width - 32);
            int columns = Math.Clamp((int)((usable + ColumnGap) / (CardMinWidth + ColumnGap)), 1, 4);
            if (!force && columns == _columns) return;
            _columns = columns;

            foreach (var card in _cards)
                if (card.Parent is Panel old) old.Children.Remove(card);
            CardsHost.Children.Clear();
            CardsHost.ColumnDefinitions.Clear();

            var stacks = new StackPanel[columns];
            var heights = new double[columns];
            for (int c = 0; c < columns; c++)
            {
                CardsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                stacks[c] = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(c == 0 ? 0 : ColumnGap / 2, 0, c == columns - 1 ? 0 : ColumnGap / 2, 0) };
                Grid.SetColumn(stacks[c], c);
                CardsHost.Children.Add(stacks[c]);
            }

            for (int i = 0; i < _cards.Count; i++)
            {
                int target = Array.IndexOf(heights, heights.Min());
                stacks[target].Children.Add(_cards[i]);
                heights[target] += 90 + 46 * ToolCatalogManager.Categories[i].Tools.Count;   // header + rows (estimate)
            }
        }

        // =================================================================
        //  Recent
        // =================================================================

        public void SetRecent(IReadOnlyList<ToolInfo> recent)
        {
            RecentList.ItemsSource = recent;
            RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            bool searching = query.Length > 0;
            BtnClearSearch.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

            if (!searching)
            {
                _matches = new();
                ResultsList.ItemsSource = null;
                ResultsPanel.Visibility = Visibility.Collapsed;
                NoResultsPanel.Visibility = Visibility.Collapsed;
                if (CardsHost.Visibility != Visibility.Visible) UiMotion.FadeVisibility(CardsHost, true);
                return;
            }

            _matches = ToolCatalogManager.Search(query);
            CardsHost.Visibility = Visibility.Collapsed;
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
