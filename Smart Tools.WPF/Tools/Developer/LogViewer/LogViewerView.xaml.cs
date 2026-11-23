using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using UserControl = System.Windows.Controls.UserControl;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Log Viewer - UI only. Following a file is <see cref="LogViewerTail"/>;
    /// formatting, rules, filtering, the line cap, export and settings are
    /// <see cref="LogViewerManager"/>.
    ///
    /// Tabs keep following while you're on another page (the old page
    /// stopped them on Unloaded but still showed "Following"); closing the
    /// app stops them all (<see cref="CancelActiveRun"/>).
    /// </summary>
    public partial class LogViewerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        public static readonly DependencyProperty LineWrappingProperty =
            DependencyProperty.Register(nameof(LineWrapping), typeof(TextWrapping), typeof(LogViewerView), new PropertyMetadata(TextWrapping.NoWrap));

        public static readonly DependencyProperty LineNumberVisibilityProperty =
            DependencyProperty.Register(nameof(LineNumberVisibility), typeof(Visibility), typeof(LogViewerView), new PropertyMetadata(Visibility.Visible));

        /// <summary>Bound by every console line (word wrap on/off).</summary>
        public TextWrapping LineWrapping
        {
            get => (TextWrapping)GetValue(LineWrappingProperty);
            set => SetValue(LineWrappingProperty, value);
        }

        /// <summary>Bound by every console line (line numbers on/off).</summary>
        public Visibility LineNumberVisibility
        {
            get => (Visibility)GetValue(LineNumberVisibilityProperty);
            set => SetValue(LineNumberVisibilityProperty, value);
        }

        private readonly ObservableCollection<LogViewerTab> _tabs = new();
        private readonly ObservableCollection<LogViewerRule> _rules = new();
        private readonly Dictionary<LogViewerTab, Func<string, bool>> _filters = new();
        private readonly Dictionary<LogViewerTab, LogViewerLine> _anchors = new();
        private readonly DispatcherTimer _ruleTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        private LogViewerSettings _settings = new();
        private LogViewerTab? _current;
        private ScrollViewer? _scroll;
        private string? _lastOpenFolder;     // saved in the settings (Alan asked for the last files to be remembered)
        private string? _lastExportFolder;   // this session only - never saved
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private bool _optionsCollapsed;

        public LogViewerView()
        {
            InitializeComponent();
            TabsControl.ItemsSource = _tabs;
            RulesList.ItemsSource = _rules;
            _rules.CollectionChanged += Rules_CollectionChanged;
            _ruleTimer.Tick += RuleTimer_Tick;
            _filterTimer.Tick += FilterTimer_Tick;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops following every file and saves rule edits not saved yet.</summary>
        public void CancelActiveRun()
        {
            if (_closed) return;
            _closed = true;
            _filterTimer.Stop();
            if (_ruleTimer.IsEnabled)
            {
                _ruleTimer.Stop();
                SaveSettings(force: true);
            }
            foreach (var tab in _tabs) TailOf(tab)?.Dispose();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce || _closed) return;
            _settings = LogViewerManager.LoadSettings(firstRunRules: () => AppSettingsStore.LogHighlightRules.Select(r => new LogViewerRule
            {
                Name = r.Name,
                Pattern = r.Pattern,
                ColorHex = r.ColorHex,
                Enabled = r.Enabled,
                IsRegex = r.IsRegex,
                CaseSensitive = r.CaseSensitive,
            }));
            _applying = true;
            try
            {
                ChkWordWrap.IsChecked = _settings.WordWrap;
                ChkLineNumbers.IsChecked = _settings.ShowLineNumbers;
                var radios = new[] { RadioInit100, RadioInit500, RadioInit2000, RadioInit5000 };
                var nearest = radios.OrderBy(r => Math.Abs(int.Parse((string)r.Tag) - _settings.InitialLines)).First();
                nearest.IsChecked = true;
                foreach (var rule in _settings.Rules) _rules.Add(rule);
            }
            finally
            {
                _applying = false;
            }
            _ruleTimer.Stop();   // loading the rules isn't an edit
            ApplyDisplayOptions();
            LogViewerManager.Rehighlight(_tabs, _rules.ToList());
            _loadedOnce = true;
            UpdatePage();

            _lastOpenFolder = _settings.LastFolder;
            foreach (var file in _settings.OpenFiles.ToList())
                if (File.Exists(file)) OpenLogFile(file);
            SaveSettings();   // drops files that no longer exist
        }

        private void SaveSettings(bool force = false)
        {
            if ((!force && (_applying || !_loadedOnce)) || (_closed && !force)) return;
            _settings.WordWrap = ChkWordWrap.IsChecked == true;
            _settings.ShowLineNumbers = ChkLineNumbers.IsChecked == true;
            _settings.Rules = _rules.ToList();
            _settings.OpenFiles = _tabs.Select(t => t.FilePath).ToList();
            _settings.LastFolder = _lastOpenFolder;
            try
            {
                LogViewerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not being able to save preferences isn't worth interrupting anyone.
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // =================================================================
        //  Display options
        // =================================================================

        private void DisplayOption_Click(object sender, RoutedEventArgs e)
        {
            ApplyDisplayOptions();
            SaveSettings();
        }

        private void ApplyDisplayOptions()
        {
            bool wrap = ChkWordWrap.IsChecked == true;
            LineWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            LineNumberVisibility = ChkLineNumbers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ScrollViewer.SetHorizontalScrollBarVisibility(LogList, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }

        private void InitialLines_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int n))
            {
                _settings.InitialLines = n;
                SaveSettings();
            }
        }

        // =================================================================
        //  Opening and closing files
        // =================================================================

        private void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open log files to follow",
                Filter = "Log files (*.log;*.txt;*.json;*.jsonl;*.ndjson)|*.log;*.txt;*.json;*.jsonl;*.ndjson|All files (*.*)|*.*",
                Multiselect = true,
            };
            if (_lastOpenFolder != null && Directory.Exists(_lastOpenFolder)) dlg.InitialDirectory = _lastOpenFolder;
            if (dlg.ShowDialog() != true) return;
            _lastOpenFolder = Path.GetDirectoryName(dlg.FileNames[0]);
            foreach (var file in dlg.FileNames) OpenLogFile(file);
            SaveSettings();
        }

        /// <summary>A file already open just gets its tab selected (the old page opened a second tab following the same file).</summary>
        private void OpenLogFile(string path)
        {
            if (_closed) return;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                TabsControl.SelectedItem = existing;
                return;
            }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            var tab = new LogViewerTab(full)
            {
                Format = ext is ".jsonl" or ".ndjson" ? LogViewerFormat.JsonLines : LogViewerFormat.PlainText,
            };
            var tail = new LogViewerTail();
            tab.Tail = tail;
            tail.LinesReceived += lines => Dispatcher.BeginInvoke(() => OnLines(tab, tail, lines));
            tail.FileMissing += () => Dispatcher.BeginInvoke(() => OnFileMissing(tab, tail));
            tail.FileReappeared += () => Dispatcher.BeginInvoke(() => OnFileReappeared(tab, tail));
            tail.FileRolledOver += () => Dispatcher.BeginInvoke(() => OnFileRolledOver(tab, tail));
            tail.Failed += ex => Dispatcher.BeginInvoke(() => OnFailed(tab, tail, ex));
            _filters[tab] = LogViewerManager.BuildFilter("", false, out _);

            _tabs.Add(tab);
            StartFollowing(tab);
            TabsControl.SelectedItem = tab;
            UpdatePage();
        }

        private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not LogViewerTab tab) return;
            TailOf(tab)?.Dispose();
            tab.Tail = null;
            _filters.Remove(tab);
            _anchors.Remove(tab);
            int index = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            if (_tabs.Count > 0) TabsControl.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
            UpdatePage();
            SaveSettings();
            e.Handled = true;
        }

        private static LogViewerTail? TailOf(LogViewerTab tab) => tab.Tail as LogViewerTail;

        // =================================================================
        //  Following
        // =================================================================

        private void BtnToggleFollow_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (_current.IsFollowing) StopFollowing(_current);
            else StartFollowing(_current);
            UiMotion.Pop(StatusPill);
        }

        /// <summary>Starts (again) from the newest lines of the file. A file that isn't there yet is waited for.</summary>
        private void StartFollowing(LogViewerTab tab)
        {
            var tail = TailOf(tab);
            if (tail == null) return;
            LogViewerManager.Clear(tab);
            tab.FollowPausedByScroll = false;
            SetState(tab, LogViewerTabState.Following, "Following");
            tail.Start(tab.FilePath, _settings.InitialLines);
            RefreshTab(tab);
        }

        private void StopFollowing(LogViewerTab tab)
        {
            TailOf(tab)?.Stop();
            SetState(tab, LogViewerTabState.Paused, "Paused");
            AddNotice(tab, "Paused - new lines aren't read until you click Follow (which loads the newest lines again).");
        }

        private void SetState(LogViewerTab tab, LogViewerTabState state, string text)
        {
            tab.State = state;
            tab.StatusText = text;
            RefreshTab(tab);
        }

        private bool IsCurrentRun(LogViewerTab tab, LogViewerTail tail) => !_closed && ReferenceEquals(tab.Tail, tail) && _tabs.Contains(tab);

        private void OnLines(LogViewerTab tab, LogViewerTail tail, IReadOnlyList<string> lines)
        {
            if (!IsCurrentRun(tab, tail) || !tab.IsFollowing) return;
            var filter = FilterOf(tab);
            LogViewerManager.AddLines(tab, lines, _rules, filter);
            if (tab == _current && tab.FollowPausedByScroll)
                tab.UnseenLines += lines.Count(filter);
            RefreshTab(tab);
            if (tab == _current && !tab.FollowPausedByScroll) ScrollToNewest();
        }

        private void OnFileMissing(LogViewerTab tab, LogViewerTail tail)
        {
            if (!IsCurrentRun(tab, tail)) return;
            SetState(tab, LogViewerTabState.Missing, "Waiting for the file");
            AddNotice(tab, "The file isn't there (deleted, renamed or not created yet) - waiting for it to appear…");
        }

        /// <summary>A file that was deleted and created again is a new file: start over rather than stitch the two together.</summary>
        private void OnFileReappeared(LogViewerTab tab, LogViewerTail tail)
        {
            if (!IsCurrentRun(tab, tail)) return;
            LogViewerManager.Clear(tab);
            SetState(tab, LogViewerTabState.Following, "Following");
            AddNotice(tab, "The file is back - showing it from the start.");
        }

        private void OnFileRolledOver(LogViewerTab tab, LogViewerTail tail)
        {
            if (!IsCurrentRun(tab, tail)) return;
            AddNotice(tab, "The file got shorter (rotated or cleared) - reading it again from the start.");
        }

        /// <summary>A note in the tab instead of a dialog per error (the old page showed a message box and kept saying "Following").</summary>
        private void OnFailed(LogViewerTab tab, LogViewerTail tail, Exception ex)
        {
            if (!IsCurrentRun(tab, tail)) return;
            tail.Stop();
            SetState(tab, LogViewerTabState.Error, "Stopped");
            string why = ex is UnauthorizedAccessException ? "Windows won't let this app read the file." : ex.Message;
            AddNotice(tab, $"Stopped following: {why} Click Follow to try again.");
        }

        private void AddNotice(LogViewerTab tab, string text)
        {
            LogViewerManager.AddLines(tab, new[] { text }, _rules, FilterOf(tab), isSystem: true);
            RefreshTab(tab);
            if (tab == _current && !tab.FollowPausedByScroll) ScrollToNewest();
        }

        private Func<string, bool> FilterOf(LogViewerTab tab) =>
            _filters.TryGetValue(tab, out var f) ? f : _ => true;

        // =================================================================
        //  Tabs and the selected tab's view
        // =================================================================

        private void TabsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != TabsControl) return;   // a ComboBox/ListBox inside bubbling up
            var next = TabsControl.SelectedItem as LogViewerTab;
            if (next == _current) return;

            // Remember where a scrolled-up tab was, so coming back shows the same lines.
            if (_current != null && _current.FollowPausedByScroll && Scroll is { } sv)
            {
                int first = (int)sv.VerticalOffset;
                if (first >= 0 && first < _current.Lines.Count) _anchors[_current] = _current.Lines[first];
            }

            _current = next;
            LogList.ItemsSource = next?.Lines;
            if (next != null)
            {
                _applying = true;
                try
                {
                    TxtFilter.Text = next.FilterText;
                    ChkFilterRegex.IsChecked = next.FilterIsRegex;
                    RadioFormatJson.IsChecked = next.Format == LogViewerFormat.JsonLines;
                    RadioFormatPlain.IsChecked = next.Format != LogViewerFormat.JsonLines;
                }
                finally
                {
                    _applying = false;
                }
                _filterTimer.Stop();
                RefreshTab(next);
                Dispatcher.InvokeAsync(() => RestoreScroll(next), DispatcherPriority.Loaded);
                UiMotion.FadeIn(TabBody);
            }
            UpdatePage();
        }

        private void RestoreScroll(LogViewerTab tab)
        {
            if (tab != _current || Scroll is not { } sv) return;
            if (tab.FollowPausedByScroll && _anchors.TryGetValue(tab, out var anchor))
            {
                int index = tab.Lines.IndexOf(anchor);
                if (index >= 0)
                {
                    sv.ScrollToVerticalOffset(index);
                    return;
                }
            }
            tab.FollowPausedByScroll = false;
            tab.UnseenLines = 0;
            RefreshTab(tab);
            sv.ScrollToBottom();
        }

        /// <summary>Tab strip, empty state and tab body.</summary>
        private void UpdatePage()
        {
            bool any = _tabs.Count > 0;
            TabStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if ((EmptyState.Visibility == Visibility.Visible) == any) UiMotion.FadeVisibility(EmptyState, !any);
            TabBody.Visibility = _current != null ? Visibility.Visible : Visibility.Collapsed;
            TxtNoRules.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Refreshes what's shown for a tab, if it's the selected one (tab headers update themselves by binding).</summary>
        private void RefreshTab(LogViewerTab tab)
        {
            if (tab != _current) return;

            TxtFileName.Text = tab.Title;
            TxtFolder.Text = tab.Folder;
            TxtFolder.ToolTip = tab.FilePath;
            StatusPill.DataContext = tab;   // the dot's colour follows State
            if (TxtStatus.Text != tab.StatusText)
            {
                TxtStatus.Text = tab.StatusText;
                UiMotion.Pop(StatusPill);
            }

            IconFollow.Kind = tab.IsFollowing ? PackIconKind.Pause : PackIconKind.Play;
            TxtFollow.Text = tab.IsFollowing ? "Pause" : "Follow";
            BtnToggleFollow.ToolTip = tab.IsFollowing
                ? "Stop reading new lines (what's shown stays)"
                : "Load the newest lines again and keep following";

            bool filtering = TxtFilter.Text.Length > 0;
            int total = tab.Captured.Count(l => !l.IsSystem);
            int shown = filtering ? tab.Lines.Count(l => !l.IsSystem) : total;
            TxtLineCount.Text = LogViewerManager.CountText(shown, total, filtering);
            TxtLineCount.SetResourceReference(TextBlock.ForegroundProperty, filtering ? "AccentColor" : "TextSecondary");

            TxtFilterError.Text = tab.FilterError == null ? "" : $"Not a valid regular expression, so it's matched as plain text: {tab.FilterError}";
            TxtFilterError.Visibility = tab.FilterError == null ? Visibility.Collapsed : Visibility.Visible;

            if (tab.Lines.Count == 0)
            {
                TxtConsoleHint.Text = filtering && total > 0 ? "No lines match the filter."
                    : tab.State == LogViewerTabState.Missing ? "Waiting for the file to appear…"
                    : tab.State == LogViewerTabState.Paused ? "Paused - click Follow to load the newest lines."
                    : tab.State == LogViewerTabState.Error ? "Stopped - click Follow to try again."
                    : "Waiting for lines… (the file is empty so far)";
                TxtConsoleHint.Visibility = Visibility.Visible;
            }
            else
            {
                TxtConsoleHint.Visibility = Visibility.Collapsed;
            }

            bool showResume = tab.FollowPausedByScroll;
            TxtResume.Text = tab.UnseenLines > 0
                ? $"{tab.UnseenLines:N0} new {(tab.UnseenLines == 1 ? "line" : "lines")} - back to the newest"
                : "Back to the newest";
            if ((BtnResumeFollowing.Visibility == Visibility.Visible) != showResume) UiMotion.FadeVisibility(BtnResumeFollowing, showResume);
        }

        // =================================================================
        //  Scrolling: follow the newest line unless the user scrolled up
        // =================================================================

        private ScrollViewer? Scroll
        {
            get
            {
                if (_scroll == null && VisualTreeHelper.GetChildrenCount(LogList) > 0)
                    _scroll = VisualTreeHelper.GetChild(LogList, 0) as ScrollViewer;
                return _scroll;
            }
        }

        private void ScrollToNewest() => Scroll?.ScrollToBottom();

        /// <summary>
        /// Only a scroll the user made (the content didn't change) can pause
        /// following. The old page paused whenever it wasn't at the bottom
        /// after any change - including new lines arriving before it had
        /// scrolled to them, which could stop following by itself.
        /// </summary>
        private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_current is not { } tab || e.OriginalSource is not ScrollViewer sv) return;
            _scroll ??= sv;

            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            {
                if (!tab.FollowPausedByScroll) sv.ScrollToBottom();   // lines added/removed or resized: stay at the newest
                return;
            }
            if (e.VerticalChange == 0) return;

            bool atBottom = sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 0.5;
            if (tab.FollowPausedByScroll == !atBottom) return;
            tab.FollowPausedByScroll = !atBottom;
            if (atBottom) tab.UnseenLines = 0;
            RefreshTab(tab);
        }

        private void BtnResumeFollowing_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.FollowPausedByScroll = false;
            _current.UnseenLines = 0;
            _anchors.Remove(_current);
            RefreshTab(_current);
            ScrollToNewest();
        }

        // =================================================================
        //  Filter and format
        // =================================================================

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_applying || _current == null) return;
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void ChkFilterRegex_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _filterTimer.Stop();
            ApplyFilter(_current);
        }

        private void FilterTimer_Tick(object? sender, EventArgs e)
        {
            _filterTimer.Stop();
            if (_current != null) ApplyFilter(_current);
        }

        private void ApplyFilter(LogViewerTab tab)
        {
            tab.FilterText = TxtFilter.Text;
            tab.FilterIsRegex = ChkFilterRegex.IsChecked == true;
            _filters[tab] = LogViewerManager.BuildFilter(tab.FilterText, tab.FilterIsRegex, out var error);
            tab.FilterError = error;
            LogViewerManager.Refilter(tab, _filters[tab]);
            tab.FollowPausedByScroll = false;
            tab.UnseenLines = 0;
            _anchors.Remove(tab);
            RefreshTab(tab);
            ScrollToNewest();
        }

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out LogViewerFormat format)) return;
            if (_current.Format == format) return;
            _current.Format = format;
            LogViewerManager.Reformat(_current);   // lines already shown change too (the old page only changed new ones)
        }

        // =================================================================
        //  Copy, export, clear, show in folder
        // =================================================================

        private void LogList_Copy(object sender, ExecutedRoutedEventArgs e)
        {
            CopyLines();
            e.Handled = true;
        }

        private void BtnCopyLines_Click(object sender, RoutedEventArgs e) => CopyLines();

        private void CopyLines()
        {
            if (_current == null || _current.Lines.Count == 0) return;
            var selected = LogList.SelectedItems.Cast<LogViewerLine>().ToList();
            string text = LogViewerManager.CopyText(_current, selected.Count > 0 ? selected : _current.Lines);
            try
            {
                Clipboard.SetText(text);
                UiMotion.Pop(BtnCopyLines);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another program has the clipboard open; a second click works.
            }
        }

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { } tab) return;
            if (sender is not MenuItem { Tag: string ext }) return;
            if (!tab.Lines.Any(l => !l.IsSystem))
            {
                AppModal.Show(Loc.T("Str_MB_ThereAreNoLines", "There are no lines to save yet."), Loc.T("Str_Tool_log-viewer_Name", "Log Viewer"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new SaveFileDialog
            {
                Title = "Save the lines shown",
                Filter = ext == "log" ? "Log file (*.log)|*.log" : "Text file (*.txt)|*.txt",
                DefaultExt = "." + ext,
                AddExtension = true,
                FileName = Path.ChangeExtension(LogViewerManager.ExportFileName(tab.FilePath), ext),
            };
            if (_lastExportFolder != null && Directory.Exists(_lastExportFolder)) dlg.InitialDirectory = _lastExportFolder;
            if (dlg.ShowDialog() != true) return;
            if (string.Equals(Path.GetFullPath(dlg.FileName), tab.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                AppModal.Show(Loc.T("Str_MB_ThatSTheLog", "That's the log file being followed - choose another name."), Loc.T("Str_Tool_log-viewer_Name", "Log Viewer"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _lastExportFolder = Path.GetDirectoryName(dlg.FileName);
            try
            {
                int n = LogViewerManager.Export(tab.Lines, dlg.FileName);
                AddNotice(tab, $"Saved {n:N0} {(n == 1 ? "line" : "lines")} to {dlg.FileName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Loc.F("Str_Err_SaveFile", "Couldn't save the file: {0}", ex.Message), Loc.T("Str_Tool_log-viewer_Name", "Log Viewer"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            LogViewerManager.Clear(_current);
            _current.FollowPausedByScroll = false;
            _anchors.Remove(_current);
            RefreshTab(_current);
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            try
            {
                if (File.Exists(_current.FilePath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_current.FilePath}\"") { UseShellExecute = true });
                else if (Directory.Exists(_current.Folder))
                    Process.Start(new ProcessStartInfo(_current.Folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't be started - nothing useful to add.
            }
        }

        // =================================================================
        //  Highlight rules (shared by every tab, saved automatically)
        // =================================================================

        private void Rules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (LogViewerRule r in e.OldItems) r.PropertyChanged -= Rule_PropertyChanged;
            if (e.NewItems != null) foreach (LogViewerRule r in e.NewItems) r.PropertyChanged += Rule_PropertyChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset) foreach (var r in _rules) { r.PropertyChanged -= Rule_PropertyChanged; r.PropertyChanged += Rule_PropertyChanged; }
            RulesChanged();
            UpdatePage();
        }

        private void Rule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogViewerRule.Problem)) return;
            RulesChanged();
        }

        /// <summary>Typing in a rule re-colours the lines a moment after the last key press, then saves.</summary>
        private void RulesChanged()
        {
            if (_applying || _closed) return;
            _ruleTimer.Stop();
            _ruleTimer.Start();
        }

        private void RuleTimer_Tick(object? sender, EventArgs e)
        {
            _ruleTimer.Stop();
            LogViewerManager.Rehighlight(_tabs, _rules.ToList());
            SaveSettings();
        }

        private void BtnAddRule_Click(object sender, RoutedEventArgs e)
        {
            _rules.Add(LogViewerManager.NewRule(_rules.Count));
            UiMotion.Pop(BtnAddRule);
        }

        private void BtnRemoveRule_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is LogViewerRule rule) _rules.Remove(rule);
        }

        private void BtnMoveRuleUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not LogViewerRule rule) return;
            int i = _rules.IndexOf(rule);
            if (i > 0) _rules.Move(i, i - 1);
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not LogViewerRule rule) return;
            var start = System.Drawing.ColorTranslator.FromHtml(LogViewerManager.NormalizeHex(rule.ColorHex) ?? LogViewerManager.RulePalette[0]);
            using var dlg = new WinFormsColorDialog { FullOpen = true, Color = start };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                rule.ColorHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        }

        // =================================================================
        //  Drag and drop
        // =================================================================

        /// <summary>Preview events so the filter box doesn't swallow a file drag; text drags into a box still work.</summary>
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = _closed ? DragDropEffects.None : DragDropEffects.Copy;
            if (!_closed && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Handled = true;
            foreach (var path in paths.Where(File.Exists)) OpenLogFile(path);   // a folder isn't a log - skipped
            SaveSettings();
        }
    }
}
