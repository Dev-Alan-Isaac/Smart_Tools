using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Unit &amp; currency converter - UI only. The unit tables, the maths and the currency rates are in
    /// <see cref="UnitConverterManager"/>. Only the setup (category and units) is remembered, never a typed value.
    /// </summary>
    public partial class UnitConverterView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private static readonly TimeSpan RatesMaxAge = TimeSpan.FromHours(6);

        private readonly UnitConverterSettings _settings;
        private readonly List<ConvRow> _rows = new();
        private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

        // A pair counts as "recent" once the same valid number has stayed put for a moment, so typing "12" does not log "1" first.
        private readonly DispatcherTimer _recentTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly bool _viewConstructed;

        private ConvCategory? _category;
        private CurrencyRateSet? _rates;
        private CancellationTokenSource? _ratesCts;
        private bool _loadedOnce;
        private bool _applying;
        private bool _selectingCategory;
        private bool _optionsCollapsed;

        public UnitConverterView()
        {
            InitializeComponent();
            _settings = UnitConverterManager.LoadSettings();
            _copyTimer.Tick += (_, _) =>
            {
                _copyTimer.Stop();
                IconCopy.Kind = PackIconKind.ContentCopy;
            };
            _recentTimer.Tick += (_, _) =>
            {
                _recentTimer.Stop();
                RememberCurrent();
            };
            GridAll.ItemsSource = _rows;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a rates download and saves the setup.</summary>
        public void CancelActiveRun()
        {
            _ratesCts?.Cancel();
            _copyTimer.Stop();
            _recentTimer.Stop();
            UnitConverterManager.SaveSettings(_settings);
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            BuildCategoryList();
            RebuildPairLists();
            SelectCategory(UnitConverterManager.Find(_settings.CategoryId)?.Id ?? (_settings.CategoryId == UnitConverterManager.CurrencyId ? UnitConverterManager.CurrencyId : "length"));
        }

        // =================================================================
        //  Categories
        // =================================================================

        private void BuildCategoryList()
        {
            var ids = UnitConverterManager.Categories.Select(c => (c.Id, c.Icon, c.EnglishName)).ToList();
            ids.Add((UnitConverterManager.CurrencyId, "CurrencyUsd", "Currency"));

            foreach (var (id, icon, english) in ids)
            {
                var kind = Enum.TryParse<PackIconKind>(icon, out var k) ? k : PackIconKind.Help;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new PackIcon { Kind = kind, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = T("Str_Conv_Cat_" + id, english), VerticalAlignment = VerticalAlignment.Center });
                CmbCategory.Items.Add(new ComboBoxItem { Content = content, Tag = id });
            }
        }

        private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _selectingCategory) return;
            if (CmbCategory.SelectedItem is ComboBoxItem { Tag: string id }) SelectCategory(id);
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }
        private void SelectCategory(string id)
        {
            _ratesCts?.Cancel();

            bool currency = id == UnitConverterManager.CurrencyId;
            _selectingCategory = true;
            try
            {
                foreach (ComboBoxItem item in CmbCategory.Items)
                    if ((string)item.Tag == id) CmbCategory.SelectedItem = item;
            }
            finally
            {
                _selectingCategory = false;
            }

            _settings.CategoryId = id;
            CurrencyStatus.Visibility = currency ? Visibility.Visible : Visibility.Collapsed;

            if (currency)
            {
                _category = null;
                ShowRatesMessage(T("Str_Conv_GettingRates", "Getting the latest rates..."), T("Str_Conv_GettingRatesHint", "Only the currency list is requested from the internet. Nothing you type is sent."), canRetry: false);
                _ = LoadRatesAsync(force: false);
            }
            else
            {
                PanelNoRates.Visibility = Visibility.Collapsed;
                PanelConvert.Visibility = Visibility.Visible;
                ShowCategory(UnitConverterManager.Find(id) ?? UnitConverterManager.Categories[0]);
            }
            UnitConverterManager.SaveSettings(_settings);
        }

        /// <summary>Fills both unit lists with the localized names, picks the remembered units, and recalculates.</summary>
        private void ShowCategory(ConvCategory category)
        {
            _category = category;
            var units = category.IsCurrency
                ? category.Units
                : category.Units.Select(u => u with { Name = T($"Str_Conv_U_{category.Id}_{u.Id}", u.EnglishName) }).ToList();

            _applying = true;
            try
            {
                CmbFrom.ItemsSource = units;
                CmbTo.ItemsSource = units;
                CmbFrom.SelectedItem = Remembered(_settings.From, category, units) ?? PickDefault(category, units, 0);
                CmbTo.SelectedItem = Remembered(_settings.To, category, units) ?? PickDefault(category, units, 1);
            }
            finally
            {
                _applying = false;
            }

            _rows.Clear();
            _rows.AddRange(units.Select(u => new ConvRow(u)));
            GridAll.Items.Refresh();
            Recalculate();
            UpdateFavoriteButton();
        }

        private static ConvUnit? Remembered(Dictionary<string, string> map, ConvCategory category, IReadOnlyList<ConvUnit> units) =>
            map.TryGetValue(category.Id, out var id) ? units.FirstOrDefault(u => u.Id == id) : null;

        /// <summary>Sensible first picks: meter to foot, US dollar to euro, ...</summary>
        private static ConvUnit PickDefault(ConvCategory category, IReadOnlyList<ConvUnit> units, int slot)
        {
            string[] wanted = category.Id switch
            {
                "length" => new[] { "m", "ft" },
                "mass" => new[] { "kg", "lb" },
                "volume" => new[] { "l", "gal" },
                "area" => new[] { "m2", "ft2" },
                "temperature" => new[] { "degC", "degF" },
                "speed" => new[] { "kmh", "mph" },
                "time" => new[] { "h", "min" },
                "data" => new[] { "GB", "MB" },
                "energy" => new[] { "kWh", "kJ" },
                "power" => new[] { "kW", "hp" },
                "pressure" => new[] { "bar", "psi" },
                UnitConverterManager.CurrencyId => new[] { "USD", "EUR" },
                _ => Array.Empty<string>(),
            };
            return (slot < wanted.Length ? units.FirstOrDefault(u => u.Id == wanted[slot]) : null) ?? units[Math.Min(slot, units.Count - 1)];
        }

        // =================================================================
        //  Converting
        // =================================================================

        private void TxtValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            Recalculate();
            ScheduleRecent();
        }

        private void Unit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying || _category == null) return;
            if (CmbFrom.SelectedItem is ConvUnit f) _settings.From[_category.Id] = f.Id;
            if (CmbTo.SelectedItem is ConvUnit t) _settings.To[_category.Id] = t.Id;
            Recalculate();
            UpdateFavoriteButton();
            ScheduleRecent();
        }

        private void Recalculate()
        {
            if (_category == null || CmbFrom.SelectedItem is not ConvUnit from || CmbTo.SelectedItem is not ConvUnit to) return;

            string text = TxtValue.Text.Trim();
            bool empty = text.Length == 0;
            bool ok = UnitConverterManager.TryParse(text, out double value);

            TxtProblem.Visibility = !empty && !ok ? Visibility.Visible : Visibility.Collapsed;
            TxtProblem.Text = T("Str_Conv_BadNumber", "That isn't a number. Use digits with an optional decimal mark, like 12.5.");

            if (!ok)
            {
                TxtResult.Clear();
                TxtBigResult.Text = "-";
                TxtBigUnit.Text = "";
                TxtFormula.Text = "";
                foreach (var row in _rows) row.Value = "";
                return;
            }

            TxtResult.Text = UnitConverterManager.Format(UnitConverterManager.Convert(value, from, to));
            TxtBigResult.Text = TxtResult.Text + " " + to.Symbol;
            TxtBigUnit.Text = to.Name;
            TxtFormula.Text = UnitConverterManager.Formula(from, to);
            foreach (var row in _rows) row.Value = UnitConverterManager.Format(UnitConverterManager.Convert(value, from, row.Unit));
        }

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            var from = CmbFrom.SelectedItem;
            var to = CmbTo.SelectedItem;
            if (from == null || to == null) return;

            // Keep the answer as the new starting number, so swapping twice returns to where you were.
            string result = TxtResult.Text;

            _applying = true;
            try
            {
                CmbFrom.SelectedItem = to;
                CmbTo.SelectedItem = from;
            }
            finally
            {
                _applying = false;
            }

            if (_category != null)
            {
                _settings.From[_category.Id] = ((ConvUnit)to).Id;
                _settings.To[_category.Id] = ((ConvUnit)from).Id;
            }
            if (result.Length > 0) TxtValue.Text = result;
            else Recalculate();
            UpdateFavoriteButton();
            RememberCurrent();
        }

        private void GridAll_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!IsInsideRow(e.OriginalSource as DependencyObject)) return;
            if (GridAll.SelectedItem is ConvRow row) CmbTo.SelectedItem = row.Unit;
        }

        private static bool IsInsideRow(DependencyObject? source)
        {
            while (source != null && source is not DataGridRow) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            return source != null;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtResult.Text.Length == 0) return;
            try
            {
                Clipboard.SetText(TxtResult.Text);
                IconCopy.Kind = PackIconKind.Check;
                _copyTimer.Stop();
                _copyTimer.Start();
                RememberCurrent();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        // =================================================================
        //  Favorite and recent pairs
        // =================================================================

        /// <summary>The units on screen as a pair, or null while there is nothing to pair.</summary>
        private ConvPair? CurrentPair() =>
            _category != null && CmbFrom.SelectedItem is ConvUnit from && CmbTo.SelectedItem is ConvUnit to
                ? new ConvPair(_category.Id, from.Id, to.Id)
                : null;

        private void ScheduleRecent()
        {
            _recentTimer.Stop();
            if (UnitConverterManager.TryParse(TxtValue.Text, out _)) _recentTimer.Start();
        }

        /// <summary>Adds the pair on screen to Recent, when there is a real number to convert.</summary>
        private void RememberCurrent()
        {
            _recentTimer.Stop();
            if (CurrentPair() is not { } pair || !UnitConverterManager.TryParse(TxtValue.Text, out _)) return;
            if (_settings.Recent.Count > 0 && _settings.Recent[0] == pair) return;
            UnitConverterManager.Remember(_settings.Recent, pair);
            RebuildPairLists();
        }

        private void BtnFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentPair() is not { } pair || pair.FromId == pair.ToId) return;
            UnitConverterManager.ToggleFavorite(_settings.Favorites, pair);
            UnitConverterManager.SaveSettings(_settings);
            RebuildPairLists();
            UpdateFavoriteButton();
        }

        private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
        {
            _settings.Recent.Clear();
            UnitConverterManager.SaveSettings(_settings);
            RebuildPairLists();
        }

        /// <summary>A chip was clicked: go to its category and units, keeping the number that is typed.</summary>
        private void Pair_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: ConvPair pair }) return;
            _settings.From[pair.CategoryId] = pair.FromId;
            _settings.To[pair.CategoryId] = pair.ToId;
            SelectCategory(pair.CategoryId);
        }

        private void UpdateFavoriteButton()
        {
            bool favorite = CurrentPair() is { } pair && UnitConverterManager.IsFavorite(_settings.Favorites, pair);
            IconFavorite.Kind = favorite ? PackIconKind.Star : PackIconKind.StarOutline;
            // Yellow when it is a favorite, the same as the Color Picker
            if (favorite) IconFavorite.SetResourceReference(ForegroundProperty, "StatusWarningFg");
            else IconFavorite.ClearValue(ForegroundProperty);
            BtnFavorite.ToolTip = T(favorite ? "Str_Conv_RemoveFavorite" : "Str_Conv_AddFavorite", favorite ? "Remove from favorites" : "Add to favorites");
        }

        /// <summary>Rebuilds both chip lists, dropping pairs whose units no longer exist.</summary>
        private void RebuildPairLists()
        {
            bool pruned = UnitConverterManager.Prune(_settings.Favorites, _rates) | UnitConverterManager.Prune(_settings.Recent, _rates);
            if (pruned) UnitConverterManager.SaveSettings(_settings);

            ListFavorites.ItemsSource = _settings.Favorites.Select(Describe).ToList();
            ListRecent.ItemsSource = _settings.Recent.Select(Describe).ToList();
            TxtNoFavorites.Visibility = _settings.Favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CardRecent.Visibility = _settings.Recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private ConvPairItem Describe(ConvPair pair)
        {
            string category = T("Str_Conv_Cat_" + pair.CategoryId, pair.CategoryId);
            string Name(string id) =>
                pair.CategoryId == UnitConverterManager.CurrencyId
                    ? (_rates != null && _rates.Names.TryGetValue(id, out var n) && n.Length > 0 ? n : id)
                    : T($"Str_Conv_U_{pair.CategoryId}_{id}", id);
            return new ConvPairItem(pair, UnitConverterManager.Label(pair), $"{category}: {Name(pair.FromId)} → {Name(pair.ToId)}");
        }

        // =================================================================
        //  Currency rates
        // =================================================================

        private void BtnRefreshRates_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.CategoryId == UnitConverterManager.CurrencyId) _ = LoadRatesAsync(force: true);
        }

        private async Task LoadRatesAsync(bool force)
        {
            _ratesCts?.Cancel();
            var cts = _ratesCts = new CancellationTokenSource();

            _rates ??= UnitConverterManager.LoadCachedRates();
            bool fresh = _rates != null && DateTimeOffset.Now - _rates.FetchedAt < RatesMaxAge;

            if (_rates != null) ShowRates(_rates, offline: false);
            if (fresh && !force)
            {
                _ratesCts = null;
                cts.Dispose();
                return;
            }

            BtnRefreshRates.IsEnabled = false;
            SetRatesPill("Info", PackIconKind.Web, T("Str_Conv_Updating", "Updating..."));
            try
            {
                var latest = await UnitConverterManager.FetchRatesAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                _rates = latest;
                ShowRates(latest, offline: false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Left the currency page; the next visit tries again.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Any failure (no network, a bad answer, a locked cache file...) ends the "Updating..." pill; none may leave it stuck.
                if (cts.IsCancellationRequested) return;
                if (_rates != null) ShowRates(_rates, offline: true);
                else
                {
                    CurrencyStatus.Visibility = Visibility.Visible;
                    SetRatesPill("Error", PackIconKind.WebOff, T("Str_Conv_NoConnection", "No connection"));
                    ShowRatesMessage(T("Str_Conv_NoRatesTitle", "Can't get exchange rates"),
                        string.Format(T("Str_Conv_NoRatesHint", "Rates are downloaded from {0} the first time. Check your internet connection and try again."), UnitConverterManager.RatesHost), canRetry: true);
                }
            }
            finally
            {
                if (ReferenceEquals(_ratesCts, cts))
                {
                    _ratesCts = null;
                    BtnRefreshRates.IsEnabled = true;
                }
                cts.Dispose();   // each run disposes its own source; a newer run only cancels it
            }
        }

        private void ShowRates(CurrencyRateSet rates, bool offline)
        {
            if (_settings.CategoryId != UnitConverterManager.CurrencyId) return;
            var category = UnitConverterManager.BuildCurrency(rates);
            if (category == null) return;

            PanelNoRates.Visibility = Visibility.Collapsed;
            PanelConvert.Visibility = Visibility.Visible;
            RebuildPairLists();

            // Keep what's typed and picked when only the rates changed.
            if (_category?.IsCurrency != true || _category.Units.Count != category.Units.Count) ShowCategory(category);
            else
            {
                _category = category;
                _applying = true;
                try
                {
                    string? from = (CmbFrom.SelectedItem as ConvUnit)?.Id, to = (CmbTo.SelectedItem as ConvUnit)?.Id;
                    CmbFrom.ItemsSource = category.Units;
                    CmbTo.ItemsSource = category.Units;
                    CmbFrom.SelectedItem = category.Units.FirstOrDefault(u => u.Id == from);
                    CmbTo.SelectedItem = category.Units.FirstOrDefault(u => u.Id == to);
                }
                finally
                {
                    _applying = false;
                }
                _rows.Clear();
                _rows.AddRange(category.Units.Select(u => new ConvRow(u)));
                GridAll.Items.Refresh();
                Recalculate();
            }

            string age = rates.Date.Length > 0 ? rates.Date : rates.FetchedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
            if (offline)
                SetRatesPill("Warning", PackIconKind.WebOff, string.Format(T("Str_Conv_OfflineRates", "Offline: rates from {0}"), age));
            else
                SetRatesPill("Success", PackIconKind.WebCheck, string.Format(T("Str_Conv_RatesFrom", "Rates from {0}"), age));
            PillRates.ToolTip = T("Str_Conv_RatesTip", "European Central Bank reference rates. They update once per working day and are for reference, not for trading.");
        }

        private void ShowRatesMessage(string title, string hint, bool canRetry)
        {
            PanelConvert.Visibility = Visibility.Collapsed;
            PanelNoRates.Visibility = Visibility.Visible;
            TxtNoRatesTitle.Text = title;
            TxtNoRatesHint.Text = hint;
            BtnTryAgain.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetRatesPill(string kind, PackIconKind icon, string text)
        {
            PillRates.SetResourceReference(Border.BackgroundProperty, $"Status{kind}Bg");
            IconRates.SetResourceReference(PackIcon.ForegroundProperty, $"Status{kind}Fg");
            TxtRates.SetResourceReference(TextBlock.ForegroundProperty, $"Status{kind}Fg");
            IconRates.Kind = icon;
            TxtRates.Text = text;
        }
    }
}
