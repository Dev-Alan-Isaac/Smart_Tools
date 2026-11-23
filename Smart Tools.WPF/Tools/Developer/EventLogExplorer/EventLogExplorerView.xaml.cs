using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataGrid = System.Windows.Controls.DataGrid;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Event Log Explorer - UI only. Listing logs, reading and filtering events, event details
    /// and exports are in <see cref="EventExplorerManager"/>. Reading happens off the UI thread;
    /// a newer request cancels the one before it, and cancelling keeps what was read so far.
    /// </summary>
    public partial class EventLogExplorerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_event-log-explorer_Name", "Event Log Explorer");

        private enum EmptyKind { Pick, NoMatch, NoLevels, Denied, Failed, BadIds }

        private enum StatusKind { Success, Error, Info }

        private readonly EventExplorerManager _manager = new();
        private readonly EventExplorerSettings _settings;
        private readonly DispatcherTimer _debounce;
        private readonly DispatcherTimer _liveTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private List<EventExplorerLogInfo> _logs = new();
        private List<EventExplorerEntry> _entries = new();
        private string? _currentLog;
        private bool _applying;          // controls are being set from code: don't treat it as a change
        private bool _loadedOnce;
        private bool _loading;
        private int _loadVersion;
        private int _detailsVersion;
        private EventExplorerEntry? _shownEntry;
        private string _shownXml = "";
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _detailsCts;

        public EventLogExplorerView()
        {
            InitializeComponent();
            _settings = EventExplorerManager.LoadSettings();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _ = ReloadAsync();
            };

            _liveTimer = new DispatcherTimer();
            _liveTimer.Tick += (_, _) =>
            {
                if (!_loading && IsVisible) _ = ReloadAsync(quiet: true);
            };

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            ApplySettings();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _debounce.Stop();
            _liveTimer.Stop();
            _statusTimer.Stop();
            _loadCts?.Cancel();
            _detailsCts?.Cancel();
            SaveSettings();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await LoadLogsAsync();
        }

        /// <summary>Live refresh runs only while the page is on screen.</summary>
        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateLiveTimer();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = ReloadAsync();
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TxtSearch.Focus();
                TxtSearch.SelectAll();
            }
        }

        private void ApplySettings()
        {
            _applying = true;
            try
            {
                SelectByTag(CmbRange, _settings.TimeRange.ToString());
                SelectByTag(CmbMax, _settings.MaxEvents.ToString(CultureInfo.InvariantCulture));
                ChkCritical.IsChecked = _settings.Levels.Contains(EventExplorerLevel.Critical);
                ChkError.IsChecked = _settings.Levels.Contains(EventExplorerLevel.Error);
                ChkWarning.IsChecked = _settings.Levels.Contains(EventExplorerLevel.Warning);
                ChkInformation.IsChecked = _settings.Levels.Contains(EventExplorerLevel.Information);
                ChkVerbose.IsChecked = _settings.Levels.Contains(EventExplorerLevel.Verbose);
                ChkHideEmpty.IsChecked = _settings.HideEmptyLogs;
                RadioLive0.IsChecked = _settings.LiveSeconds == 0;
                RadioLive5.IsChecked = _settings.LiveSeconds == 5;
                RadioLive10.IsChecked = _settings.LiveSeconds == 10;
                RadioLive30.IsChecked = _settings.LiveSeconds == 30;
                if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);
                DetailsCard.Visibility = _settings.ShowDetails ? Visibility.Visible : Visibility.Collapsed;
                ShowEmpty(EmptyKind.Pick);
                UpdateLiveTimer();
            }
            finally
            {
                _applying = false;
            }
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void SaveSettings()
        {
            try
            {
                EventExplorerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Event Log Explorer settings: {ex.Message}");
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarCollapsed(!_settings.SidebarCollapsed);
            SaveSettings();
        }

        private void SetSidebarCollapsed(bool collapsed)
        {
            _settings.SidebarCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  The log picker
        // =================================================================

        private async Task LoadLogsAsync()
        {
            LogsBusy.Visibility = Visibility.Visible;
            try
            {
                _logs = await _manager.ListLogsAsync();
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logs = new List<EventExplorerLogInfo>();
                ShowEmpty(EmptyKind.Failed, ex.Message);
            }
            finally
            {
                LogsBusy.Visibility = Visibility.Collapsed;
            }

            _currentLog = _logs.Any(l => l.Name.Equals(_settings.LastLog, StringComparison.OrdinalIgnoreCase))
                ? _logs.First(l => l.Name.Equals(_settings.LastLog, StringComparison.OrdinalIgnoreCase)).Name
                : _logs.FirstOrDefault()?.Name;
            RefreshLogList();
            UpdateTitle();
            if (_currentLog != null) await ReloadAsync();
        }

        /// <summary>Applies the search box and "hide empty" to the picker; keeps the chosen log selected.</summary>
        private void RefreshLogList()
        {
            string search = TxtLogSearch.Text.Trim();
            bool hideEmpty = ChkHideEmpty.IsChecked == true;
            var shown = _logs.Where(l =>
                    (!hideEmpty || !l.IsEmpty || string.Equals(l.Name, _currentLog, StringComparison.OrdinalIgnoreCase)) &&
                    (search.Length == 0 || l.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _applying = true;
            try
            {
                ListLogs.ItemsSource = shown;
                var current = shown.FirstOrDefault(l => string.Equals(l.Name, _currentLog, StringComparison.OrdinalIgnoreCase));
                ListLogs.SelectedItem = current;
                if (current != null) ListLogs.ScrollIntoView(current);
            }
            finally
            {
                _applying = false;
            }
        }

        private void TxtLogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) RefreshLogList();
        }

        private void ChkHideEmpty_Click(object sender, RoutedEventArgs e)
        {
            _settings.HideEmptyLogs = ChkHideEmpty.IsChecked == true;
            RefreshLogList();
            SaveSettings();
        }

        private void ListLogs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || !_viewConstructed || ListLogs.SelectedItem is not EventExplorerLogInfo log) return;
            _currentLog = log.Name;
            _settings.LastLog = log.Name;
            SaveSettings();
            UpdateTitle();
            ClearDetails();
            _ = ReloadAsync();
        }

        private EventExplorerLogInfo? CurrentLogInfo =>
            _logs.FirstOrDefault(l => string.Equals(l.Name, _currentLog, StringComparison.OrdinalIgnoreCase));

        private void UpdateTitle()
        {
            var info = CurrentLogInfo;
            TxtLogTitle.Text = info?.DisplayName ?? _currentLog ?? Str("Str_EventExplorer_NoLog");
            TxtLogSubtitle.Text = info == null ? "" : info.Name == info.DisplayName ? info.Hint : info.Name;
        }

        // =================================================================
        //  Filters
        // =================================================================

        private HashSet<EventExplorerLevel> SelectedLevels()
        {
            var levels = new HashSet<EventExplorerLevel>();
            if (ChkCritical.IsChecked == true) levels.Add(EventExplorerLevel.Critical);
            if (ChkError.IsChecked == true) levels.Add(EventExplorerLevel.Error);
            if (ChkWarning.IsChecked == true) levels.Add(EventExplorerLevel.Warning);
            if (ChkInformation.IsChecked == true) levels.Add(EventExplorerLevel.Information);
            if (ChkVerbose.IsChecked == true) levels.Add(EventExplorerLevel.Verbose);
            return levels;
        }

        private EventExplorerTimeRange SelectedRange =>
            Enum.TryParse(TagOf(CmbRange), out EventExplorerTimeRange range) ? range : EventExplorerTimeRange.Last24Hours;

        private int SelectedMax =>
            int.TryParse(TagOf(CmbMax), out int max) && EventExplorerManager.MaxEventChoices.Contains(max) ? max : 1000;

        private EventExplorerQuery BuildQuery() => new()
        {
            LogName = _currentLog ?? "",
            Levels = SelectedLevels(),
            FromLocal = EventExplorerManager.RangeStart(SelectedRange, DateTime.Now),
            EventIds = TxtEventIds.Text,
            Source = TxtSource.Text,
            Text = TxtSearch.Text,
            MaxEvents = SelectedMax,
        };

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (_applying || !_viewConstructed) return;
            _settings.Levels = SelectedLevels().ToList();
            SaveSettings();
            ScheduleReload(150);
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || !_viewConstructed) return;
            _settings.TimeRange = SelectedRange;
            _settings.MaxEvents = SelectedMax;
            SaveSettings();
            ScheduleReload(150);
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_applying || !_viewConstructed) return;
            BtnClearSearch.Visibility = TxtSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScheduleReload(450);
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private void ScheduleReload(int milliseconds)
        {
            _debounce.Stop();
            _debounce.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _debounce.Start();
        }

        // =================================================================
        //  Live refresh
        // =================================================================

        private void RadioLive_Checked(object sender, RoutedEventArgs e)
        {
            if (_applying || !_viewConstructed || sender is not RadioButton { Tag: string tag }) return;
            _settings.LiveSeconds = int.TryParse(tag, out int seconds) ? seconds : 0;
            SaveSettings();
            UpdateLiveTimer();
        }

        private void UpdateLiveTimer()
        {
            _liveTimer.Stop();
            if (_settings.LiveSeconds <= 0 || !IsVisible) return;
            _liveTimer.Interval = TimeSpan.FromSeconds(_settings.LiveSeconds);
            _liveTimer.Start();
        }

        // =================================================================
        //  Reading events
        // =================================================================

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

        private void BtnCancelLoad_Click(object sender, RoutedEventArgs e) => _loadCts?.Cancel();

        /// <param name="quiet">Live refresh: no progress bar, no flicker, no empty-state change when nothing is new.</param>
        private async Task ReloadAsync(bool quiet = false)
        {
            _debounce.Stop();
            if (string.IsNullOrEmpty(_currentLog))
            {
                ShowEmpty(EmptyKind.Pick);
                return;
            }

            // Filters that can't be run: say why instead of reading.
            if (SelectedLevels().Count == 0)
            {
                SetResults(new List<EventExplorerEntry>());
                ShowEmpty(EmptyKind.NoLevels);
                return;
            }
            EventExplorerManager.ParseEventIds(TxtEventIds.Text, out string? idError);
            TxtIdError.Visibility = idError == null ? Visibility.Collapsed : Visibility.Visible;
            if (idError != null)
            {
                TxtIdError.Text = string.Format(Str("Str_EventExplorer_BadIds"), idError);
                return;
            }

            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();
            int version = ++_loadVersion;
            _loading = true;

            if (!quiet)
            {
                TxtLoading.Text = Str("Str_EventExplorer_Loading");
                UiMotion.FadeVisibility(LoadBar, true);
            }

            var query = BuildQuery();
            var progress = new Progress<EventExplorerProgress>(p =>
            {
                if (version == _loadVersion && !quiet)
                    TxtLoading.Text = string.Format(Str("Str_EventExplorer_LoadingProgress"), p.Scanned, p.Matched);
            });

            EventExplorerResult result;
            try
            {
                result = await _manager.QueryAsync(query, progress, cts.Token);
            }
            catch (Exception ex)
            {
                result = new EventExplorerResult { Error = EventExplorerError.Other, ErrorText = ex.Message };
            }

            if (version != _loadVersion) return;   // a newer read took over
            _loading = false;
            UiMotion.FadeVisibility(LoadBar, false);
            ApplyResult(result, quiet);
        }

        private void ApplyResult(EventExplorerResult result, bool quiet)
        {
            var info = CurrentLogInfo;
            bool needsAdmin = info?.NeedsAdmin == true || result.Error == EventExplorerError.AccessDenied;
            if (needsAdmin) ElevationBanner.ShowIfNotElevated();
            else UiMotion.FadeVisibility(ElevationBanner, false);

            if (result.Error != EventExplorerError.None)
            {
                SetResults(new List<EventExplorerEntry>());
                ShowEmpty(result.Error == EventExplorerError.AccessDenied ? EmptyKind.Denied : EmptyKind.Failed, result.ErrorText);
                return;
            }

            // Live refresh with nothing new: leave the list (selection, scroll) alone.
            bool unchanged = quiet && result.Entries.Count == _entries.Count &&
                (result.Entries.Count == 0 || result.Entries[0].RecordId == _entries[0].RecordId);
            if (!unchanged) SetResults(result.Entries);

            string range = (CmbRange.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            TxtLogSubtitle.Text = $"{string.Format(Str("Str_EventExplorer_SubtitleCount"), result.Entries.Count)} · {range} · {result.Elapsed.TotalSeconds:0.0} s";

            if (result.Entries.Count == 0 && !result.Cancelled) ShowEmpty(EmptyKind.NoMatch);
            else HideEmpty();

            if (result.Truncated)
                ShowStatus(string.Format(Str("Str_EventExplorer_Truncated"), result.Entries.Count), StatusKind.Info);
            else if (result.Cancelled)
                ShowStatus(Str("Str_EventExplorer_Cancelled"), StatusKind.Info);
        }

        /// <summary>Shows these events and keeps the highlighted one highlighted (by record id).</summary>
        private void SetResults(List<EventExplorerEntry> entries)
        {
            long? keep = (GridEvents.SelectedItem as EventExplorerEntry)?.RecordId;
            _entries = entries;
            GridEvents.ItemsSource = _entries;

            if (keep != null)
            {
                var same = _entries.FirstOrDefault(e => e.RecordId == keep);
                if (same != null) GridEvents.SelectedItem = same;
            }
            if (GridEvents.SelectedItem == null) ClearDetails();

            var (critical, error, warning, information, verbose) = EventExplorerManager.CountLevels(_entries);
            SetCount(TxtCritical, PillCritical, critical);
            SetCount(TxtErrors, PillError, error);
            SetCount(TxtWarnings, PillWarning, warning);
            SetCount(TxtInfos, PillInfo, information + verbose);
            TxtTotal.Text = _entries.Count.ToString("N0");
        }

        private static void SetCount(TextBlock label, Border pill, int count)
        {
            string text = count.ToString("N0");
            if (label.Text != text)
            {
                label.Text = text;
                UiMotion.Pop(pill, 0.92);
            }
            pill.Opacity = count == 0 ? 0.55 : 1;
        }

        // =================================================================
        //  Empty states
        // =================================================================

        private void ShowEmpty(EmptyKind kind, string? detail = null)
        {
            switch (kind)
            {
                case EmptyKind.NoMatch:
                    EmptyStatePanel.Icon = PackIconKind.FilterRemoveOutline;
                    EmptyStatePanel.Title = Str("Str_EventExplorer_EmptyNoMatchTitle");
                    EmptyStatePanel.Description = Str("Str_EventExplorer_EmptyNoMatchHint");
                    break;
                case EmptyKind.NoLevels:
                    EmptyStatePanel.Icon = PackIconKind.FilterVariant;
                    EmptyStatePanel.Title = Str("Str_EventExplorer_EmptyNoLevelsTitle");
                    EmptyStatePanel.Description = Str("Str_EventExplorer_EmptyNoLevelsHint");
                    break;
                case EmptyKind.Denied:
                    EmptyStatePanel.Icon = PackIconKind.ShieldLockOutline;
                    EmptyStatePanel.Title = Str("Str_EventExplorer_EmptyDeniedTitle");
                    EmptyStatePanel.Description = Str("Str_EventExplorer_EmptyDeniedHint");
                    break;
                case EmptyKind.Failed:
                    EmptyStatePanel.Icon = PackIconKind.AlertCircleOutline;
                    EmptyStatePanel.Title = Str("Str_EventExplorer_EmptyFailedTitle");
                    EmptyStatePanel.Description = string.IsNullOrWhiteSpace(detail) ? Str("Str_EventExplorer_EmptyFailedHint") : detail;
                    break;
                default:
                    EmptyStatePanel.Icon = PackIconKind.ScriptTextOutline;
                    EmptyStatePanel.Title = Str("Str_EventExplorer_EmptyPickTitle");
                    EmptyStatePanel.Description = Str("Str_EventExplorer_EmptyPickHint");
                    break;
            }
            UiMotion.FadeVisibility(EmptyStatePanel, true);
            UiMotion.FadeVisibility(GridEvents, false);
        }

        private void HideEmpty()
        {
            UiMotion.FadeVisibility(EmptyStatePanel, false);
            UiMotion.FadeVisibility(GridEvents, true);
        }

        // =================================================================
        //  Event details
        // =================================================================

        private void GridEvents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (GridEvents.SelectedItem is EventExplorerEntry entry) _ = ShowDetailsAsync(entry);
            else ClearDetails();
        }

        private void ClearDetails()
        {
            _detailsCts?.Cancel();
            _detailsVersion++;
            _shownEntry = null;
            _shownXml = "";
            TxtXml.Clear();
            PanelDetails.Visibility = Visibility.Collapsed;
            TxtDetailsEmpty.Visibility = Visibility.Visible;
            HeaderDetails.Content = Str("Str_EventExplorer_DetailsHeader");
            BtnCopyDetails.IsEnabled = BtnSearchOnline.IsEnabled = false;
        }

        private async Task ShowDetailsAsync(EventExplorerEntry entry)
        {
            _detailsCts?.Cancel();
            var cts = _detailsCts = new CancellationTokenSource();
            int version = ++_detailsVersion;

            _shownEntry = entry;
            _shownXml = "";
            if (_settings.ShowDetails || DetailsCard.Visibility != Visibility.Visible)
            {
                _settings.ShowDetails = true;
                UiMotion.FadeVisibility(DetailsCard, true);
            }

            HeaderDetails.Content = string.Format(Str("Str_EventExplorer_DetailsTitle"), entry.EventId, entry.Provider);
            TxtDetailsEmpty.Visibility = Visibility.Collapsed;
            PanelDetails.Visibility = Visibility.Visible;
            TxtDetailMessage.Text = string.IsNullOrWhiteSpace(entry.Message) ? Str("Str_EventExplorer_NoDescription") : entry.Message;
            ListData.ItemsSource = null;
            ListFields.ItemsSource = null;
            LabelData.Visibility = Visibility.Collapsed;
            TxtXml.Text = Str("Str_EventExplorer_Loading");
            BtnCopyDetails.IsEnabled = true;
            BtnSearchOnline.IsEnabled = true;

            if (entry.RecordId is not { } recordId) return;
            EventExplorerDetails? details;
            try
            {
                details = await _manager.GetDetailsAsync(entry.LogName, recordId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (version != _detailsVersion) return;

            if (details == null)
            {
                TxtXml.Text = Str("Str_EventExplorer_DetailsGone");
                return;
            }
            _shownXml = details.Xml;
            TxtXml.Text = details.Xml;
            ListData.ItemsSource = details.Data;
            LabelData.Visibility = details.Data.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ListFields.ItemsSource = details.Fields;
        }

        private void RadioTab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool xml = RadioXml.IsChecked == true;
            ScrollGeneral.Visibility = xml ? Visibility.Collapsed : Visibility.Visible;
            TxtXml.Visibility = xml ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnHideDetails_Click(object sender, RoutedEventArgs e)
        {
            _settings.ShowDetails = false;
            UiMotion.FadeVisibility(DetailsCard, false);
            SaveSettings();
        }

        private void BtnCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_shownEntry == null) return;
            string text = RadioXml.IsChecked == true && _shownXml.Length > 0
                ? _shownXml
                : $"{_shownEntry.TimeText}  {_shownEntry.LevelText}  {_shownEntry.Provider}  Event {_shownEntry.EventId}{Environment.NewLine}{_shownEntry.Message}";
            CopyToClipboard(text);
        }

        // =================================================================
        //  Row menu
        // =================================================================

        private EventExplorerEntry? Selected => GridEvents.SelectedItem as EventExplorerEntry;

        private void RowMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (Selected == null && sender is ContextMenu menu) menu.IsOpen = false;
        }

        /// <summary>Right-click highlights the row under the pointer before the menu opens.</summary>
        private void GridEvents_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not DataGridRow) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            if (source is DataGridRow row) row.IsSelected = true;
        }

        private void MenuCopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } entry) CopyToClipboard(entry.Message);
        }

        private void MenuCopyRow_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } entry)
                CopyToClipboard(string.Join('\t', entry.TimeText, entry.LevelText, entry.EventId, entry.Provider, EventExplorerManager.Summarize(entry.Message, 500)));
        }

        private void MenuOnlyThisId_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } entry) TxtEventIds.Text = entry.EventId.ToString(CultureInfo.InvariantCulture);
        }

        private void MenuOnlyThisSource_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } entry) TxtSource.Text = entry.Provider;
        }

        private void MenuSearchOnline_Click(object sender, RoutedEventArgs e)
        {
            var entry = Selected ?? _shownEntry;
            if (entry == null) return;
            string query = Uri.EscapeDataString($"Windows event {entry.EventId} {entry.Provider}");
            try
            {
                Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={query}") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Str("Str_EventExplorer_BrowserFailed"), StatusKind.Error);
            }
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                ShowStatus(Str("Str_EventExplorer_Copied"), StatusKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus(Str("Str_EventExplorer_CopyFailed"), StatusKind.Error);   // another program holds the clipboard
            }
        }

        // =================================================================
        //  Export / Event Viewer
        // =================================================================

        private void MenuExportCsv_Click(object sender, RoutedEventArgs e) => Export(EventExplorerExportFormat.Csv);

        private void MenuExportJson_Click(object sender, RoutedEventArgs e) => Export(EventExplorerExportFormat.Json);

        private void MenuExportEvtx_Click(object sender, RoutedEventArgs e) => Export(EventExplorerExportFormat.Evtx);

        private async void Export(EventExplorerExportFormat format)
        {
            if (string.IsNullOrEmpty(_currentLog)) return;
            if (format != EventExplorerExportFormat.Evtx && _entries.Count == 0)
            {
                ShowStatus(Str("Str_EventExplorer_NothingToExport"), StatusKind.Info);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                FileName = EventExplorerManager.SuggestFileName(_currentLog, format),
                Filter = format switch
                {
                    EventExplorerExportFormat.Csv => "CSV (*.csv)|*.csv",
                    EventExplorerExportFormat.Json => "JSON (*.json)|*.json",
                    _ => "Event log (*.evtx)|*.evtx",
                },
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                switch (format)
                {
                    case EventExplorerExportFormat.Csv:
                        EventExplorerManager.WriteTextExport(dialog.FileName, EventExplorerManager.BuildCsv(_entries));
                        break;
                    case EventExplorerExportFormat.Json:
                        EventExplorerManager.WriteTextExport(dialog.FileName, EventExplorerManager.BuildJson(_entries));
                        break;
                    default:
                        await _manager.ExportEvtxAsync(BuildQuery(), dialog.FileName);
                        break;
                }
                ShowStatus(Str("Str_EventExplorer_Saved"), StatusKind.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EventLogException)
            {
                AppModal.Show(string.Format(Str("Str_EventExplorer_SaveFailed"), ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenViewer_Click(object sender, RoutedEventArgs e)
        {
            if (!EventExplorerManager.OpenEventViewer())
                ShowStatus(Str("Str_EventExplorer_ViewerFailed"), StatusKind.Error);
        }

        // =================================================================
        //  Status pill
        // =================================================================

        private void ShowStatus(string text, StatusKind kind)
        {
            var (foreground, background, icon) = kind switch
            {
                StatusKind.Success => ("StatusSuccessFg", "StatusSuccessBg", PackIconKind.CheckCircleOutline),
                StatusKind.Error => ("StatusErrorFg", "StatusErrorBg", PackIconKind.AlertCircleOutline),
                _ => ("StatusInfoFg", "StatusInfoBg", PackIconKind.InformationOutline),
            };
            TxtStatus.Text = text;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, foreground);
            BorderStatus.SetResourceReference(Border.BackgroundProperty, background);
            IconStatus.Kind = icon;
            UiMotion.FadeVisibility(BorderStatus, true);
            UiMotion.Pop(BorderStatus, 0.92);
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }

    /// <summary>"ProcessId" → the localized "Process ID" label.</summary>
    public sealed class EventFieldLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value as string ?? "";
            return Application.Current?.TryFindResource("Str_EventExplorer_Field_" + key) as string ?? key;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
