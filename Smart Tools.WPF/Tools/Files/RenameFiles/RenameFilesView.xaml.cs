using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Rename Files view - UI only. New names, clashes and the plan are
    /// worked out in <see cref="RenameManager"/>; reading folders, renaming
    /// and undo in RenameManager.Run.cs. The preview rebuilds itself
    /// (debounced) when a setting changes, and Rename always runs the plan
    /// for the settings on screen.
    /// </summary>
    public partial class RenameFilesView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowColumnThreshold = 1000;
        private const string DialogTitle = "Rename Files";

        private readonly ObservableCollection<FolderListEntry> _folders = new();
        private readonly DispatcherTimer _refreshTimer;

        private ObservableCollection<RenamePreviewItem> _rows = new();
        private RenameScanResult? _scan;
        private RenameRunResult? _lastRun;
        private RenameJournal? _journal;
        private RenameSettings _settings = new();

        private CancellationTokenSource? _refreshCts;
        private CancellationTokenSource? _runCts;
        private Task? _refreshTask;
        private bool _pendingRescan;
        private bool _planStale;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public RenameFilesView()
        {
            InitializeComponent();

            InputCard.ItemsSource = _folders;
            _folders.CollectionChanged += (_, _) => InputCard.SetEmpty(_folders.Count == 0);
            DataGridPreview.ItemsSource = CreateGroupedView(_rows);

            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                bool rescan = _pendingRescan;
                _pendingRescan = false;
                _ = RefreshAsync(rescan);
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Files already renamed stay renamed (each rename is complete on its own) and can be undone next time.</summary>
        public void CancelActiveRun()
        {
            _refreshTimer.Stop();
            _refreshCts?.Cancel();
            _runCts?.Cancel();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(RenameManager.LoadSettings());
                _journal = RenameManager.LoadJournal();
                _loadedOnce = true;
                UpdateUndoButton();
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width) =>
            ColExtension.Visibility = width < NarrowColumnThreshold ? Visibility.Collapsed : Visibility.Visible;

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Folders (kept for this session only - never saved)
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isBusy) AddFolders(e.Paths);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => ScanOption_Changed(sender ?? this, new RoutedEventArgs());

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isBusy || _folders.Count == 0) return;
            _folders.Clear();
            ScheduleRefresh(rescan: true);
        }

        private void BtnRemoveSingleFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || sender is not Button { Tag: FolderListEntry entry }) return;
            _folders.Remove(entry);
            ScheduleRefresh(rescan: true);
        }

        private void AddFolders(IEnumerable<string> folders)
        {
            if (_isBusy) return;
            bool added = false;
            foreach (string folder in folders)
            {
                if (_folders.Any(f => string.Equals(f.FullPath, folder, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(new FolderListEntry { FullPath = folder });
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            ScheduleRefresh(rescan: true);
        }

        private void ScanOption_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            ScheduleRefresh(rescan: true);
        }

        // =================================================================
        //  Method and settings
        // =================================================================

        private static T TagOf<T>(object? item, T fallback) where T : struct, Enum =>
            item is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out T value) ? value : fallback;

        private RenameMethod SelectedMethod => TagOf(CmbRenameMethod.SelectedItem, RenameMethod.FindReplace);

        private static T CheckedTag<T>(T fallback, params RadioButton[] radios) where T : struct, Enum =>
            TagOf(radios.FirstOrDefault(r => r.IsChecked == true), fallback);

        private static void SelectByTag(ComboBox combo, string tag) =>
            combo.SelectedItem = combo.Items.OfType<FrameworkElement>().FirstOrDefault(i => (string?)i.Tag == tag) ?? combo.Items[0];

        private static void CheckByTag(string tag, params RadioButton[] radios) =>
            (radios.FirstOrDefault(r => (string?)r.Tag == tag) ?? radios[0]).IsChecked = true;

        private RadioButton[] PositionRadios => new[] { RadioNumberSuffix, RadioNumberPrefix, RadioNumberReplace };
        private RadioButton[] OrderRadios => new[] { RadioOrderName, RadioOrderDate };
        private RadioButton[] ExtensionRadios => new[] { RadioExtKeep, RadioExtLower, RadioExtUpper, RadioExtChange };

        private void CmbRenameMethod_SelectionChanged(object sender, SelectionChangedEventArgs e) => SettingChanged();

        private void Setting_Changed(object sender, RoutedEventArgs e) => SettingChanged();

        /// <summary>Chip rows use Click (Checked also fires when settings are applied).</summary>
        private void Option_Click(object sender, RoutedEventArgs e) => SettingChanged();

        private void AutoPreview_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (ChkAutoPreview.IsChecked == true && _planStale) ScheduleRefresh(rescan: false);
        }

        private void SettingChanged()
        {
            if (Ignore) return;
            UpdateMethodUi(animate: true);
            SaveSettings();
            if (ChkAutoPreview.IsChecked == true)
            {
                ScheduleRefresh(rescan: false);
            }
            else if (_rows.Count > 0 && !_isBusy)
            {
                _planStale = true;
                TxtProgress.Text = "Settings changed - press Preview to update the list. Rename uses the new settings either way.";
            }
        }

        private void UpdateMethodUi(bool animate)
        {
            var method = SelectedMethod;
            void Show(FrameworkElement panel, RenameMethod m)
            {
                bool visible = method == m;
                if (visible && panel.Visibility != Visibility.Visible && animate) UiMotion.FadeIn(panel, 4);
                panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            Show(PanelFindReplace, RenameMethod.FindReplace);
            Show(PanelPrefixSuffix, RenameMethod.PrefixSuffix);
            Show(PanelReplaceName, RenameMethod.ReplaceName);
            Show(PanelNumberSequence, RenameMethod.NumberSequence);
            // Replace-whole-name reuses the numbering boxes (not the before/after choice).
            bool replaceName = method == RenameMethod.ReplaceName;
            if (replaceName) PanelNumberSequence.Visibility = Visibility.Visible;
            PanelNumberPosition.Visibility = replaceName ? Visibility.Collapsed : Visibility.Visible;
            Show(PanelChangeCase, RenameMethod.ChangeCase);
            Show(PanelTrimStrip, RenameMethod.TrimStrip);
            Show(PanelRegexReplace, RenameMethod.RegexReplace);
            Show(PanelRemoveCharacters, RenameMethod.RemoveCharacters);
            Show(PanelCleanSuffixes, RenameMethod.CleanSuffixes);

            TxtMethodHint.Text = method switch
            {
                RenameMethod.PrefixSuffix => "Adds text before and after each name.",
                RenameMethod.NumberSequence => "Numbers the files - by name or by date, per folder or across them all.",
                RenameMethod.ReplaceName => "Gives every file the same new name plus a number: 1.png and abc.png become album_001.png and album_002.png.",
                RenameMethod.ChangeCase => "Changes upper and lower case. A change of case alone works too.",
                RenameMethod.TrimStrip => "Cuts characters off the start or end of each name.",
                RenameMethod.RegexReplace => "For advanced patterns. A pattern that isn't valid is explained above the list.",
                RenameMethod.RemoveCharacters => "Removes the characters you choose from each name.",
                RenameMethod.CleanSuffixes => "Takes copy leftovers off the end of names: Ia (1)_1 becomes Ia (1). If the clean name is taken, the file gets (2).",
                _ => "Replaces text in every name. The extension is never touched.",
            };

            bool change = CheckedTag(RenameExtensionMode.Keep, ExtensionRadios) == RenameExtensionMode.Change;
            if (animate && change != (PanelNewExtension.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(PanelNewExtension, change);
            else PanelNewExtension.Visibility = change ? Visibility.Visible : Visibility.Collapsed;

            // The extension chips decide the extension; "apply case" only matters when they say Keep.
            ChkCaseExtension.IsEnabled = !change && CheckedTag(RenameExtensionMode.Keep, ExtensionRadios) == RenameExtensionMode.Keep;
        }

        /// <summary>Whole number from a box; a problem is noted instead of silently using a default.</summary>
        private static int ReadInt(TextBox box, string label, int fallback, int min, int max, List<string> problems)
        {
            string text = box.Text.Trim();
            if (text.Length == 0) return fallback;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
            {
                problems.Add($"{label} must be a whole number - using {fallback}.");
                return fallback;
            }
            if (value < min || value > max)
            {
                problems.Add($"{label} must be between {min} and {max}.");
                return Math.Clamp(value, min, max);
            }
            return value;
        }

        private RenameOptions BuildOptions()
        {
            var problems = new List<string>();
            var o = new RenameOptions
            {
                Method = SelectedMethod,
                IncludeSubfolders = InputCard.IncludeSubfolders,
                SkipHiddenAndSystem = ChkSkipHidden.IsChecked == true,
                ExtensionMode = CheckedTag(RenameExtensionMode.Keep, ExtensionRadios),
                NewExtension = TxtNewExtension.Text,

                FindText = TxtFind.Text,
                ReplaceText = TxtReplace.Text,
                MatchCase = ChkMatchCase.IsChecked == true,
                WholeWordOnly = ChkWholeWord.IsChecked == true,

                Prefix = TxtPrefix.Text,
                Suffix = TxtSuffix.Text,
                SuffixBeforeExtension = ChkSuffixBeforeExtension.IsChecked == true,

                NewName = TxtNewName.Text,

                StartAt = ReadInt(TxtStartAt, "Start", 1, -1_000_000_000, 1_000_000_000, problems),
                Increment = ReadInt(TxtIncrement, "Step", 1, -1_000_000, 1_000_000, problems),
                Padding = ReadInt(TxtPadding, "Digits", 0, 0, 10, problems),
                Separator = TxtSeparator.Text,
                NumberPosition = CheckedTag(RenameNumberPosition.Suffix, PositionRadios),
                NumberOrder = CheckedTag(RenameNumberOrder.Name, OrderRadios),
                NumberPerFolder = ChkNumberPerFolder.IsChecked == true,

                CaseStyle = TagOf(ComboCaseStyle.SelectedItem, RenameCaseStyle.Upper),
                ApplyCaseToExtension = ChkCaseExtension.IsChecked == true,

                TrimStart = ReadInt(TxtTrimStart, "Trim from start", 0, 0, 255, problems),
                TrimEnd = ReadInt(TxtTrimEnd, "Trim from end", 0, 0, 255, problems),
                StripLeadingTrailingSpaces = ChkStripSpaces.IsChecked == true,
                CollapseRepeatedSpaces = ChkCollapseSpaces.IsChecked == true,

                Pattern = TxtPattern.Text,
                Replacement = TxtReplacement.Text,
                RegexCaseInsensitive = ChkRegexCaseInsensitive.IsChecked == true,
                ReplaceAllMatches = ChkReplaceAll.IsChecked == true,

                CharsToRemove = TxtRemoveCharacters.Text,
                RemoveNumbers = ChkRemoveNumbers.IsChecked == true,
                RemoveSymbols = ChkRemoveSymbols.IsChecked == true,
                RemoveSpaces = ChkRemoveSpaces.IsChecked == true,
                RemoveDiacritics = ChkRemoveDiacritics.IsChecked == true,

                StripUnderscoreCounter = ChkStripUnderscore.IsChecked == true,
                StripDashCounter = ChkStripDash.IsChecked == true,
                StripCopyWords = ChkStripCopyWords.IsChecked == true,
                StripWindowsCounter = ChkStripWindowsCounter.IsChecked == true,
            };

            // Only the chosen method's boxes matter.
            bool relevant = o.Method is RenameMethod.NumberSequence or RenameMethod.ReplaceName or RenameMethod.TrimStrip;
            TxtSettingError.Text = relevant ? string.Join(" ", problems) : "";
            TxtSettingError.Visibility = relevant && problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return o;
        }

        private void ApplySettings(RenameSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                var o = s.Options;
                SelectByTag(CmbRenameMethod, o.Method.ToString());
                InputCard.IncludeSubfolders = o.IncludeSubfolders;
                ChkSkipHidden.IsChecked = o.SkipHiddenAndSystem;
                CheckByTag(o.ExtensionMode.ToString(), ExtensionRadios);
                TxtNewExtension.Text = o.NewExtension;

                TxtFind.Text = o.FindText;
                TxtReplace.Text = o.ReplaceText;
                ChkMatchCase.IsChecked = o.MatchCase;
                ChkWholeWord.IsChecked = o.WholeWordOnly;

                TxtPrefix.Text = o.Prefix;
                TxtSuffix.Text = o.Suffix;
                ChkSuffixBeforeExtension.IsChecked = o.SuffixBeforeExtension;

                TxtNewName.Text = o.NewName;
                TxtStartAt.Text = o.StartAt.ToString(CultureInfo.CurrentCulture);
                TxtIncrement.Text = o.Increment.ToString(CultureInfo.CurrentCulture);
                TxtPadding.Text = o.Padding.ToString(CultureInfo.CurrentCulture);
                TxtSeparator.Text = o.Separator;
                CheckByTag(o.NumberPosition.ToString(), PositionRadios);
                CheckByTag(o.NumberOrder.ToString(), OrderRadios);
                ChkNumberPerFolder.IsChecked = o.NumberPerFolder;

                SelectByTag(ComboCaseStyle, o.CaseStyle.ToString());
                ChkCaseExtension.IsChecked = o.ApplyCaseToExtension;

                TxtTrimStart.Text = o.TrimStart.ToString(CultureInfo.CurrentCulture);
                TxtTrimEnd.Text = o.TrimEnd.ToString(CultureInfo.CurrentCulture);
                ChkStripSpaces.IsChecked = o.StripLeadingTrailingSpaces;
                ChkCollapseSpaces.IsChecked = o.CollapseRepeatedSpaces;

                TxtPattern.Text = o.Pattern;
                TxtReplacement.Text = o.Replacement;
                ChkRegexCaseInsensitive.IsChecked = o.RegexCaseInsensitive;
                ChkReplaceAll.IsChecked = o.ReplaceAllMatches;

                TxtRemoveCharacters.Text = o.CharsToRemove;
                ChkRemoveNumbers.IsChecked = o.RemoveNumbers;
                ChkRemoveSymbols.IsChecked = o.RemoveSymbols;
                ChkRemoveSpaces.IsChecked = o.RemoveSpaces;
                ChkRemoveDiacritics.IsChecked = o.RemoveDiacritics;

                ChkStripUnderscore.IsChecked = o.StripUnderscoreCounter;
                ChkStripDash.IsChecked = o.StripDashCounter;
                ChkStripCopyWords.IsChecked = o.StripCopyWords;
                ChkStripWindowsCounter.IsChecked = o.StripWindowsCounter;

                ChkAutoPreview.IsChecked = s.AutoPreview;
                UpdateMethodUi(animate: false);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.Options = BuildOptions();
            _settings.AutoPreview = ChkAutoPreview.IsChecked == true;
            try
            {
                RenameManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Rename settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  Preview
        // =================================================================

        /// <summary>Debounced, so typing in a box doesn't rebuild the list per key.</summary>
        private void ScheduleRefresh(bool rescan)
        {
            if (!_viewConstructed || _isBusy) return;
            _pendingRescan |= rescan;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private Task RefreshAsync(bool rescan)
        {
            _refreshCts?.Cancel();
            var cts = _refreshCts = new CancellationTokenSource();
            _refreshTask = RefreshCoreAsync(rescan || _scan == null, cts);
            return _refreshTask;
        }

        private async Task RefreshCoreAsync(bool rescan, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var options = BuildOptions();

            if (_folders.Count == 0)
            {
                _scan = null;
                ShowPlan(null);
                TxtProgress.Text = "Add a folder to see the new names.";
                return;
            }

            var progress = new Progress<RenameProgress>(p =>
            {
                if (ReferenceEquals(_refreshCts, cts)) ReportProgress(p);
            });
            ProgressRename.IsIndeterminate = true;
            TxtProgressPercent.Text = "";

            try
            {
                if (rescan)
                {
                    TxtProgress.Text = "Reading the folders...";
                    _scan = await RenameManager.ScanAsync(_folders.Select(f => f.FullPath).ToList(), options, progress, token);
                }
                var scan = _scan!;
                var plan = await Task.Run(() => RenameManager.BuildPlan(scan.Files, options, null, token), token);
                if (token.IsCancellationRequested) return;

                ShowPlan(plan);
                _planStale = false;
                TxtProgress.Text = PlanText(plan.Items);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer refresh
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_refreshCts, cts)) TxtProgress.Text = $"Couldn't build the preview: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, cts))
                {
                    ProgressRename.IsIndeterminate = false;
                    ProgressRename.Value = 0;
                    TxtProgressPercent.Text = "";
                }
            }
        }

        private string PlanText(IReadOnlyCollection<RenamePreviewItem> items)
        {
            var parts = new List<string> { RenameManager.DescribePlan(items) };
            if (_scan?.HiddenSkipped > 0) parts.Add($"{_scan.HiddenSkipped:N0} hidden left alone");
            if (_scan?.InaccessibleFolders > 0) parts.Add($"{_scan.InaccessibleFolders:N0} folders couldn't be read");
            if (_scan?.LinksSkipped > 0) parts.Add($"{_scan.LinksSkipped:N0} linked folders not followed");
            if (_scan?.MissingFolders > 0) parts.Add($"{_scan.MissingFolders:N0} folders no longer exist");
            return string.Join(" · ", parts);
        }

        private static ListCollectionView CreateGroupedView(ObservableCollection<RenamePreviewItem> rows)
        {
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RenamePreviewItem.Folder)));
            return view;   // the plan is already in folder + name order
        }

        /// <summary>A new collection and grouped view per plan (never refill a grouped view in place).</summary>
        private void ShowPlan(RenamePlan? plan)
        {
            _rows = new ObservableCollection<RenamePreviewItem>(plan?.Items ?? new List<RenamePreviewItem>());
            DataGridPreview.ItemsSource = CreateGroupedView(_rows);
            if (_rows.Count > 0) UiMotion.FadeIn(DataGridPreview, 6);
            _lastRun = null;

            bool hasError = plan?.Error != null;
            TxtPlanError.Text = plan?.Error ?? "";
            if (hasError != (PlanErrorBanner.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(PlanErrorBanner, hasError);

            bool hasExample = plan?.Example != null;
            TxtExample.Text = plan?.Example ?? "";
            ExampleBox.Visibility = hasExample ? Visibility.Visible : Visibility.Collapsed;

            UpdateSummary();
            UpdateEmptyState();
        }

        private void UpdateSummary()
        {
            int ready = _rows.Count(i => i.Status == RenameStatus.Ready);
            int same = _rows.Count(i => i.Status == RenameStatus.NoChange);
            int adjusted = _rows.Count(i => i.Status == RenameStatus.Ready && i.IsAdjusted);
            int invalid = _rows.Count(i => i.Status == RenameStatus.Invalid);

            TxtSummaryReady.Text = $"{ready:N0} to rename";
            TxtSummarySame.Text = $"{same:N0} unchanged";
            PillSame.Visibility = same > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryAdjusted.Text = $"{adjusted:N0} name taken";
            PillAdjusted.Visibility = adjusted > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryInvalid.Text = $"{invalid:N0} can't use";
            PillInvalid.Visibility = invalid > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_lastRun is { } run)
            {
                TxtSummaryDone.Text = $"{run.Renamed:N0} renamed";
                TxtSummaryFailed.Text = $"{run.Failed:N0} failed";
                PillDone.Visibility = run.Renamed > 0 ? Visibility.Visible : Visibility.Collapsed;
                PillFailed.Visibility = run.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                PillDone.Visibility = Visibility.Collapsed;
                PillFailed.Visibility = Visibility.Collapsed;
            }

            bool show = _rows.Count > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            EmptyStatePanel.Description = _folders.Count > 0
                ? "No files to rename in these folders. Try including subfolders, or hidden files."
                : "Add a folder on the left. Every new name shows here before anything is renamed.";
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            UiMotion.FadeVisibility(DataGridPreview, !empty);
        }

        private void BtnGeneratePreview_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _refreshTimer.Stop();
            _pendingRescan = false;
            _ = RefreshAsync(rescan: true);
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _refreshTimer.Stop();
            _refreshCts?.Cancel();
            _folders.Clear();
            _scan = null;
            ShowPlan(null);
            TxtProgress.Text = "Add a folder to see the new names.";
        }

        /// <summary>Double-click a row: the file where it is now.</summary>
        private void DataGridPreview_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not RenamePreviewItem item) return;
            string path = item.Status == RenameStatus.Renamed ? item.NewFullPath : item.FullPath;
            if (ShellExplorer.Show(path) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show("That file is no longer there.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        // =================================================================
        //  Rename and undo
        // =================================================================

        private async void BtnRenameFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping after the file in progress...";
                return;
            }

            // Always run the plan for the settings on screen (the old view could rename with a stale preview).
            if (_refreshTimer.IsEnabled || _planStale)
            {
                _refreshTimer.Stop();
                bool rescan = _pendingRescan;
                _pendingRescan = false;
                await RefreshAsync(rescan);
            }
            else if (_refreshTask is { IsCompleted: false })
            {
                await _refreshTask;
            }

            if (_rows.Count == 0)
            {
                InputCard.PopDropArea();
                MessageBox.Show("Add a folder with files in it first.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (TxtPlanError.Text.Length > 0)
            {
                UiMotion.Pop(PlanErrorBanner, 0.95);
                return;
            }

            var ready = _rows.Where(i => i.Status == RenameStatus.Ready).ToList();
            if (ready.Count == 0)
            {
                MessageBox.Show("None of these names change with the current settings.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int adjusted = ready.Count(i => i.IsAdjusted);
            string question = $"Rename {ready.Count:N0} file{(ready.Count == 1 ? "" : "s")}?" +
                (adjusted > 0 ? $"\n\n{adjusted:N0} of them get \" (2)\" because their new name is taken." : "") +
                "\n\nYou can undo this afterwards.";
            if (MessageBox.Show(question, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetBusy(true, undo: false);

            try
            {
                var progress = new Progress<RenameProgress>(ReportProgress);
                var (result, journal) = await RenameManager.RunAsync(_rows.ToList(), progress, _runCts.Token);
                _lastRun = result;

                if (journal.Steps.Count > 0)
                {
                    _journal = journal;
                    try
                    {
                        RenameManager.SaveJournal(journal);
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Undo journal not saved: {ex.Message}");
                    }
                }

                // The names on disk changed: the next setting change re-reads the folders.
                _scan = null;
                SetBusy(false, undo: false);
                ProgressRename.Value = 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = RenameManager.DescribeRun(result);
                UpdateSummary();

                try
                {
                    ToastNotificationManager.ShowToast("Rename Complete",
                        result.Failed > 0 ? $"Renamed {result.Renamed:N0} files - {result.Failed:N0} failed." : $"Renamed {result.Renamed:N0} file{(result.Renamed == 1 ? "" : "s")}.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, undo: false);
                TxtProgress.Text = "Rename failed.";
                MessageBox.Show($"Rename failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isBusy) SetBusy(false, undo: false);
                UpdateUndoButton();
            }
        }

        private async void BtnUndoRename_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                return;
            }

            var journal = _journal ?? RenameManager.LoadJournal();
            if (journal == null)
            {
                UpdateUndoButton();
                return;
            }

            int files = Math.Max(journal.FileCount, journal.Steps.Count(s => !RenameManager.IsTempName(s.From)));
            if (MessageBox.Show($"Give the {files:N0} file{(files == 1 ? "" : "s")} renamed on {journal.When:g} their old names back?",
                    "Undo Rename", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetBusy(true, undo: true);

            try
            {
                var progress = new Progress<RenameProgress>(ReportProgress);
                var result = await RenameManager.UndoAsync(journal, progress, _runCts.Token);

                if (result.Remaining.Count > 0)
                {
                    _journal = new RenameJournal { When = journal.When, FileCount = result.Remaining.Count(s => !RenameManager.IsTempName(s.From)), Steps = result.Remaining };
                    RenameManager.SaveJournal(_journal);
                }
                else
                {
                    RenameManager.DeleteJournal();
                    _journal = null;
                }

                var parts = new List<string> { $"Undone: {result.Restored:N0} back to their old names" };
                if (result.RestoredWithNewName > 0) parts.Add($"{result.RestoredWithNewName:N0} got \" (2)\" (the old name was taken)");
                if (result.Missing > 0) parts.Add($"{result.Missing:N0} no longer there");
                if (result.Failed > 0) parts.Add($"{result.Failed:N0} couldn't be undone");
                if (result.Remaining.Count > 0) parts.Add("stopped - press Undo again to finish");
                SetBusy(false, undo: true);
                TxtProgress.Text = string.Join(" · ", parts);

                if (result.Errors.Count > 0)
                    MessageBox.Show("Some files couldn't be undone:\n\n" + string.Join("\n", result.Errors.Take(12)) + (result.Errors.Count > 12 ? $"\n...and {result.Errors.Count - 12} more" : ""),
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                SetBusy(false, undo: true);
                MessageBox.Show($"Undo failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isBusy) SetBusy(false, undo: true);
                UpdateUndoButton();
                _scan = null;
                ScheduleRefresh(rescan: true);
            }
        }

        private void UpdateUndoButton()
        {
            BtnUndoRename.IsEnabled = !_isBusy && _journal != null;
            BtnUndoRename.ToolTip = _journal != null
                ? $"Undo the last rename ({_journal.When:g} · {_journal.FileCount:N0} file{(_journal.FileCount == 1 ? "" : "s")})"
                : "Nothing to undo yet";
        }

        private void ReportProgress(RenameProgress p)
        {
            if (p.Percent is double pct)
            {
                ProgressRename.IsIndeterminate = false;
                ProgressRename.Value = pct;
                TxtProgressPercent.Text = $"{pct:0}%";
            }
            else
            {
                ProgressRename.IsIndeterminate = true;
                TxtProgressPercent.Text = "";
            }

            string phase = p.Phase switch
            {
                RenamePhase.Scanning => $"Reading the folders · {p.Processed:N0} files so far",
                RenamePhase.Undoing => $"Undoing · {p.Processed:N0} of {p.Total:N0}",
                _ => $"Renaming · {p.Processed:N0} of {p.Total:N0}",
            };
            TxtProgress.Text = string.IsNullOrEmpty(p.CurrentFile) ? phase : $"{phase} · {p.CurrentFile}";
        }

        private void SetBusy(bool busy, bool undo)
        {
            _isBusy = busy;

            if (busy)
            {
                _refreshTimer.Stop();
                _refreshCts?.Cancel();
                IconRename.Kind = PackIconKind.StopCircleOutline;
                TxtRenameLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
                ProgressRename.IsIndeterminate = false;
                ProgressRename.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = undo ? "Undoing..." : "Starting...";
            }
            else
            {
                IconRename.Kind = PackIconKind.RenameOutline;
                TxtRenameLabel.Text = "Rename files";
            }
            UiMotion.Pop(BtnRenameFiles, 0.96);

            // Nothing may change the plan while it runs (the old view kept the sidebar live).
            OptionsScrollViewer.IsEnabled = !busy;
            BtnGeneratePreview.IsEnabled = !busy;
            BtnReset.IsEnabled = !busy;
            BtnRenameFiles.IsEnabled = !(busy && undo);   // an undo is cancelled from the undo button
            BtnUndoRename.IsEnabled = busy ? undo : _journal != null;
        }
    }
}
