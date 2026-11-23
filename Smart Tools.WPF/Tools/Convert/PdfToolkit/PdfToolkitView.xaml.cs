using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// PDF Toolkit - UI only. Page ranges, names, the list and the merge /
    /// split / compress runs are in <see cref="PdfToolkitManager"/>; finding
    /// PDFs in PdfToolkitManager.Files.cs; PdfSharp in
    /// PdfToolkitManager.Pdf.cs. One list serves all three operations
    /// (switching operation used to empty it). Folders picked here are kept
    /// for this session only - never saved.
    /// </summary>
    public partial class PdfToolkitView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowColumnThreshold = 1000;
        private static string DialogTitle => Loc.T("Str_Tool_pdf-toolkit_Name", "PDF Toolkit");

        private readonly ObservableCollection<PdfToolItem> _items = new();
        private readonly ObservableCollection<PdfToolkitGroup> _groups = new();
        private readonly SemaphoreSlim _infoGate = new(3);   // page counts for rows on screen, a few at a time
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private PdfToolSettings _settings = new();
        private CancellationTokenSource? _runCts;
        private CheckBox AutoSaveCheckBox = null!;   // inside the Output card (built in BuildOutputOptions)
        private List<string> _lastFiles = new();
        private int _adding;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public PdfToolkitView()
        {
            InitializeComponent();

            DataGridResults.ItemsSource = _items;
            _items.CollectionChanged += (_, _) => UpdateListState();
            DataGridResults.SelectionChanged += (_, _) => UpdateButtons();

            // The Files card: one row per added folder (or set of loose files).
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = PdfToolkitManager.FileDialogFilter;
            _groups.CollectionChanged += (_, _) =>
                InputCard.SetEmpty(_groups.Count == 0);

            OutputCard.DefaultFolder = PdfToolkitManager.DefaultFolder;
            BuildOutputOptions();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Nothing half-written is left behind: unfinished files only ever exist under temporary names.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _lifetimeCts.Cancel();
            PdfToolkitManager.Passwords.Clear();   // typed passwords never outlive the tool
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(PdfToolkitManager.LoadSettings());
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateListState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width) =>
            ColSize.Visibility = width < NarrowColumnThreshold ? Visibility.Collapsed : Visibility.Visible;

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
        //  Settings (no folders or file names)
        // =================================================================

        private PdfToolOperation CurrentOperation =>
            ParseTag(CheckedTag(RbOpMerge, RbOpSplit, RbOpCompress), PdfToolOperation.Merge);

        private void ApplySettings(PdfToolSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                CheckByTag(s.Operation.ToString(), RbOpMerge, RbOpSplit, RbOpCompress);
                CheckByTag(s.SplitMode.ToString(), RbSplitEveryPage, RbSplitEveryN, RbSplitPageRanges);
                CheckByTag(s.CompressLevel.ToString(), RbCompressLight, RbCompressBalanced, RbCompressStrong);
                TxtEveryN.Text = s.EveryNPages.ToString();
                ChkRangesInOneFile.IsChecked = s.RangesInOneFile;
                ChkAddBookmarks.IsChecked = s.AddBookmarks;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                AutoSaveCheckBox.IsChecked = s.SaveWithoutAsking;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateOptionPanels(animate: false);
            UpdateRangesHint();
        }

        private PdfToolSettings ReadSettings() => new()
        {
            Operation = CurrentOperation,
            SplitMode = ParseTag(CheckedTag(RbSplitEveryPage, RbSplitEveryN, RbSplitPageRanges), PdfSplitMode.EveryPage),
            EveryNPages = int.TryParse(TxtEveryN.Text, out int n) ? Math.Clamp(n, 1, 9999) : 2,
            RangesInOneFile = ChkRangesInOneFile.IsChecked == true,
            CompressLevel = ParseTag(CheckedTag(RbCompressLight, RbCompressBalanced, RbCompressStrong), PdfCompressLevel.Balanced),
            AddBookmarks = ChkAddBookmarks.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SaveWithoutAsking = AutoSaveCheckBox.IsChecked == true,
        };

        private void SaveSettingsQuietly()
        {
            _settings = ReadSettings();
            try
            {
                PdfToolkitManager.SaveSettings(_settings);
            }
            catch (Exception)
            {
                // Settings are a convenience - a locked file mustn't stop the tool.
            }
        }

        /// <summary>Switching operation keeps the list (it used to be emptied) and clears the last run's results.</summary>
        private void Operation_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || _isBusy) return;
            if (_settings.Operation == CurrentOperation) return;
            ResetResults();
            TxtProgressStatus.Text = _items.Count == 0 ? "Add PDFs to get started." : "Ready.";
            UpdateOptionPanels(animate: true);
            SaveSettingsQuietly();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (Ignore) return;
            UpdateOptionPanels(animate: true);
            SaveSettingsQuietly();
        }

        private void UpdateOptionPanels(bool animate)
        {
            var op = CurrentOperation;
            ShowPanel(PanelMergeOptions, op == PdfToolOperation.Merge, animate);
            ShowPanel(PanelSplitOptions, op == PdfToolOperation.Split, animate);
            ShowPanel(PanelCompressOptions, op == PdfToolOperation.Compress, animate);

            var mode = ParseTag(CheckedTag(RbSplitEveryPage, RbSplitEveryN, RbSplitPageRanges), PdfSplitMode.EveryPage);
            ShowPanel(PanelEveryN, mode == PdfSplitMode.EveryNPages, animate);
            ShowPanel(PanelPageRangesInput, mode == PdfSplitMode.Ranges, animate);

            TxtOperationHint.Text = op switch
            {
                PdfToolOperation.Split => "Each ticked PDF is cut into new files. The original stays as it is.",
                PdfToolOperation.Compress => "Each ticked PDF gets a smaller copy, named …_compressed.pdf.",
                _ => "The ticked PDFs are joined into one new PDF, top to bottom.",
            };
            TxtCompressHint.Text = ParseTag(CheckedTag(RbCompressLight, RbCompressBalanced, RbCompressStrong), PdfCompressLevel.Balanced) switch
            {
                PdfCompressLevel.Light => "Pictures up to 3,000 px at high quality - a modest saving, still print-ready.",
                PdfCompressLevel.Strong => "Pictures up to 1,500 px at lower quality - the smallest files, best for screens.",
                _ => "Pictures up to 2,200 px - sharp on screen and fine to print, much smaller.",
            };

            if (!_isBusy)
            {
                (IconRun.Kind, TxtRunLabel.Text, BtnRun.ToolTip) = op switch
                {
                    PdfToolOperation.Split => (PackIconKind.CallSplit, "Split PDFs", (object)"Split each ticked PDF"),
                    PdfToolOperation.Compress => (PackIconKind.ArrowCollapseAll, "Compress PDFs", (object)"Save a smaller copy of each ticked PDF"),
                    _ => (PackIconKind.CallMerge, "Merge PDFs", (object)"Join the ticked PDFs into one, in list order"),
                };
            }
            ColResult.Header = op switch
            {
                PdfToolOperation.Split => "Files made",
                PdfToolOperation.Compress => "Size",
                _ => "Pages in result",
            };
            UpdateButtons();
        }

        private static void ShowPanel(FrameworkElement panel, bool show, bool animate)
        {
            bool wasVisible = panel.Visibility == Visibility.Visible;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !wasVisible && animate) UiMotion.FadeIn(panel, 4);
        }

        private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
            e.Handled = !e.Text.All(char.IsDigit);

        private void TxtEveryN_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!Ignore) SaveSettingsQuietly();
        }

        private void TxtPageRanges_TextChanged(object sender, TextChangedEventArgs e) => UpdateRangesHint();

        /// <summary>Reads the page box as you type, so a mistake shows before Split is pressed (the old one skipped it silently).</summary>
        private void UpdateRangesHint()
        {
            var parse = PdfToolkitManager.ParseRanges(TxtPageRanges.Text);
            TxtRangesHint.Text = PdfToolkitManager.DescribeRanges(parse);
            TxtRangesHint.SetResourceReference(TextBlock.ForegroundProperty, parse.Problems.Count > 0 ? "StatusErrorFg" : "SidebarTextMuted");
        }

        // =================================================================
        //  Adding PDFs
        // =================================================================

        // ---- The Files card (same as Validate Data / Convert Files / Images to PDF) ----

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: PdfToolkitGroup group }) RemoveItems(group.Items.ToList());
        }

        /// <summary>The Clear button on the Files card - same as Clear above the list.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isBusy) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        /// <summary>
        /// Saved as a preference, and also updates the folders already added
        /// (it used to apply only to the next folder). PDFs already listed keep
        /// their place in the merge order.
        /// </summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            OnOptionChanged();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (_isBusy || folders.Count == 0) return;
            var token = _lifetimeCts.Token;
            _adding++;
            UpdateButtons();
            TxtProgressStatus.Text = includeSubfolders ? "Looking in subfolders…" : "Leaving out subfolders…";
            try
            {
                int added = 0, removed = 0, inaccessible = 0;
                foreach (var group in folders)
                {
                    var scan = await PdfToolkitManager.ScanFolderAsync(group.FolderPath!, includeSubfolders, token);
                    if (scan.Missing || _isBusy) continue;   // folder gone - leave its PDFs alone
                    inaccessible += scan.InaccessibleFolders;
                    var (a, r) = PdfToolkitManager.ApplyRescan(_items, _groups, group, scan.Files, includeSubfolders);
                    added += a.Count;
                    removed += r.Count;
                }
                if (added + removed > 0) InputCard.PopDropArea();
                string text = includeSubfolders
                    ? (added > 0 ? $"Added {added:N0} {PdfToolText.Plural(added, "PDF", "PDFs")} from subfolders, at the end of the list." : "No PDFs in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} subfolder {PdfToolText.Plural(removed, "PDF", "PDFs")} off the list." : "No subfolder PDFs were in the list.");
                if (inaccessible > 0) text += $" {inaccessible:N0} {PdfToolText.Plural(inaccessible, "folder", "folders")} couldn't be opened.";
                if (!_isBusy) TxtProgressStatus.Text = text;
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgressStatus.Text = "Couldn't read the folders again: " + PdfToolkitManager.PlainError(ex);
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
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
            if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isBusy || paths.Count == 0) return;
            _adding++;
            UpdateButtons();
            TxtProgressStatus.Text = Loc.T("Str_PdfToolkit_StatusLooking", "Looking for PDFs…");
            try
            {
                // Each folder is one Files card row; loose PDFs share one.
                bool subfolders = InputCard.IncludeSubfolders;
                var token = _lifetimeCts.Token;
                var files = new List<PdfFileFound>();
                var added = new List<PdfToolItem>();
                int inaccessible = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var scan = await PdfToolkitManager.ScanFolderAsync(folder, subfolders, token);
                    if (_isBusy) return;
                    inaccessible += scan.InaccessibleFolders;
                    files.AddRange(scan.Files);
                    added.AddRange(PdfToolkitManager.AddToList(_items, _groups, scan.Files, folder, isFolder: true, subfolders));
                }
                var (loose, skipped, _) = await PdfToolkitManager.ReadPathsAsync(paths.Where(p => !Directory.Exists(p)), false, token);
                if (_isBusy) return;
                files.AddRange(loose);
                added.AddRange(PdfToolkitManager.AddToList(_items, _groups, loose, null, isFolder: false, includeSubfolders: false));
                var parts = new List<string>
                {
                    added.Count == 0
                        ? files.Count > 0 ? "Those PDFs are already in the list." : "No PDFs found."
                        : $"Added {added.Count:N0} {PdfToolText.Plural(added.Count, "PDF", "PDFs")}.",
                };
                if (skipped > 0) parts.Add($"{skipped:N0} {PdfToolText.Plural(skipped, "file wasn't a PDF", "files weren't PDFs")} or couldn't be found.");
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {PdfToolText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (!_isBusy) TxtProgressStatus.Text = string.Join(" ", parts);
                if (added.Count > 0)
                {
                    DataGridResults.ScrollIntoView(added[0]);
                    UiMotion.Pop(SummaryPanel, 0.95);
                    InputCard.PopDropArea();
                }
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgressStatus.Text = "Couldn't add the files: " + PdfToolkitManager.PlainError(ex);
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        // =================================================================
        //  Rows: page count for rows on screen
        // =================================================================

        private void DataGridResults_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is PdfToolItem { InfoRequested: false } item)
            {
                item.InfoRequested = true;
                _ = LoadInfoAsync(item, _lifetimeCts.Token);
            }
        }

        private bool _askingPassword;
        private readonly HashSet<string> _declinedPassword = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A PDF needs a password: ask for it now (one box at a time; a cancelled file is not asked again), then read it again.</summary>
        private void AskPassword(PdfToolItem item)
        {
            if (_askingPassword || _declinedPassword.Contains(item.FullPath)) return;
            _askingPassword = true;
            string? typed;
            try
            {
                typed = PdfPasswordDialog.Ask(Loc.T("Str_Tool_pdf-toolkit_Name", "PDF Merge & Split"), item.Name, wrong: PdfToolkitManager.Passwords.ContainsKey(item.FullPath));
            }
            finally
            {
                _askingPassword = false;
            }

            if (typed == null) { _declinedPassword.Add(item.FullPath); return; }
            PdfToolkitManager.Passwords[item.FullPath] = typed;
            item.PageCount = 0;
            item.Message = "";
            item.InfoRequested = true;
            _ = LoadInfoAsync(item, _lifetimeCts.Token);
        }

        private async Task LoadInfoAsync(PdfToolItem item, CancellationToken token)
        {
            bool needsPassword = false;
            try
            {
                await _infoGate.WaitAsync(token);
                try
                {
                    var (pages, error, locked) = await Task.Run(() =>
                    {
                        try
                        {
                            return (PdfToolkitManager.CountPages(item.FullPath), (string?)null, false);
                        }
                        catch (Exception ex)
                        {
                            return (-1, PdfToolkitManager.PlainError(ex), PdfToolkitManager.NeedsPassword(ex));
                        }
                    }, token);
                    if (token.IsCancellationRequested || item.PageCount != 0) return;   // a run may have filled it in already
                    item.PageCount = pages;
                    if (error != null && item.Status == PdfItemStatus.Ready) item.Message = error;
                    needsPassword = locked;
                }
                finally
                {
                    _infoGate.Release();
                }
                if (needsPassword) AskPassword(item);
            }
            catch (OperationCanceledException)
            {
                item.InfoRequested = false;
            }
        }

        // =================================================================
        //  The list: tick, order, remove
        // =================================================================

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateListState();

        private void ChkIncludeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            bool include = ChkIncludeAll.IsChecked == true;
            foreach (var item in _items) item.IsIncluded = include;
            UpdateListState();
        }

        private List<PdfToolItem> HighlightedItems() => DataGridResults.SelectedItems.OfType<PdfToolItem>().ToList();

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isBusy) return;
            var chosen = HighlightedItems();
            if (PdfToolkitManager.Move(_items, chosen, direction)) Reselect(chosen);
        }

        /// <summary>Puts the highlight back after rows moved (replacing a row drops it) and keeps them in view.</summary>
        private void Reselect(IReadOnlyList<PdfToolItem> chosen)
        {
            DataGridResults.SelectedItems.Clear();
            foreach (var item in chosen) DataGridResults.SelectedItems.Add(item);
            var first = chosen.OrderBy(i => i.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridResults.ScrollIntoView(first);
                DataGridResults.CurrentItem = first;
            }
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_isBusy || _items.Count < 2) return;
            var chosen = HighlightedItems();
            var sorted = PdfToolkitManager.Sorted(_items, byDate);
            _items.Clear();
            foreach (var item in sorted) _items.Add(item);
            PdfToolkitManager.Renumber(_items);
            Reselect(chosen);
            TxtProgressStatus.Text = byDate ? "Sorted by date, oldest first." : "Sorted by name.";
        }

        private void BtnRowRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: PdfToolItem item }) RemoveItems(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy) RemoveItems(HighlightedItems());
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (_isBusy || _items.Count == 0) return;
            _items.Clear();
            _groups.Clear();
            ResetResults();
            TxtProgressStatus.Text = Loc.T("Str_PdfToolkit_AddPdfsToGet", "Add PDFs to get started.");
        }

        private void RemoveItems(IReadOnlyCollection<PdfToolItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<PdfToolItem>(items);
            int nextIndex = _items.TakeWhile(i => !gone.Contains(i)).Count();
            PdfToolkitManager.RemoveFromList(_items, _groups, items);
            if (_items.Count > 0)
            {
                // Keep the keyboard on the list: highlight the row that took the first removed row's place.
                var next = _items[Math.Clamp(nextIndex, 0, _items.Count - 1)];
                DataGridResults.SelectedItem = next;
                DataGridResults.CurrentItem = next;
            }
        }

        private void DataGridResults_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ButtonBase) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete)
            {
                if (!_isBusy) RemoveItems(HighlightedItems());
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
            {
                MoveHighlighted(key == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        private void DataGridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;   // the remove button / tick box
            if (FindAncestor<DataGridRow>(source)?.Item is PdfToolItem item) OpenWithShell(item.FullPath);
        }

        private void UpdateListState()
        {
            int count = _items.Count;
            UiMotion.FadeVisibility(EmptyStatePanel, count == 0);
            UiMotion.FadeVisibility(DataGridResults, count > 0);
            SummaryPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = PdfToolkitManager.DescribeList(_items);

            int done = _items.Count(i => i.Status == PdfItemStatus.Done);
            int failed = _items.Count(i => i.Status == PdfItemStatus.Failed);
            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryDone.Text = $"{done:N0} done";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFailed.Text = $"{failed:N0} failed";

            int included = _items.Count(i => i.IsIncluded);
            ChkIncludeAll.IsChecked = count == 0 || included == count ? true : included == 0 ? false : null;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool idle = !_isBusy;
            bool hasHighlight = DataGridResults.SelectedItems.Count > 0;
            BtnMoveUp.IsEnabled = idle && hasHighlight;
            BtnMoveDown.IsEnabled = idle && hasHighlight;
            BtnRemoveSelected.IsEnabled = idle && hasHighlight;
            BtnSortByName.IsEnabled = idle && _items.Count > 1;
            BtnSortByDate.IsEnabled = idle && _items.Count > 1;
            BtnClearAll.IsEnabled = idle && _items.Count > 0;
            int ticked = _items.Count(i => i.IsIncluded);
            BtnRun.IsEnabled = _isBusy || (_adding == 0 && ticked >= (CurrentOperation == PdfToolOperation.Merge ? 2 : 1));
        }

        // =================================================================
        //  Output folder (this session only)
        // =================================================================

        /// <summary>"Save without asking" sits at the bottom of the Output card; built here because the card's Options can't hold named controls.</summary>
        private void BuildOutputOptions()
        {
            AutoSaveCheckBox = new CheckBox
            {
                Content = Loc.T("Str_PdfToolkit_SaveWithoutAsking", "Save without asking"),
                IsChecked = true,
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = Loc.T("Str_PdfToolkit_SaveWithoutAskingTip", "Save straight into the folder above. Off: pick where to save each time."),
            };
            AutoSaveCheckBox.SetResourceReference(ForegroundProperty, "SidebarText");
            AutoSaveCheckBox.Checked += Option_Changed;
            AutoSaveCheckBox.Unchecked += Option_Changed;
            OutputCard.Options = AutoSaveCheckBox;
        }

        private string? PickFolder(string title)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            string? start = OutputCard.EffectiveFolder;
            if (start != null && Directory.Exists(start)) dialog.InitialDirectory = start;
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        // =================================================================
        //  Run / Cancel
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                TxtProgressStatus.Text = "Cancelling…";
                BtnRun.IsEnabled = false;
                return;
            }

            var op = CurrentOperation;
            var snapshot = _items.ToList();
            int ticked = snapshot.Count(i => i.IsIncluded);
            if (ticked < (op == PdfToolOperation.Merge ? 2 : 1))
            {
                TxtProgressStatus.Text = op == PdfToolOperation.Merge ? "Tick at least two PDFs to merge." : "Tick at least one PDF first.";
                return;
            }
            _settings = ReadSettings();
            if (op == PdfToolOperation.Split && _settings.SplitMode == PdfSplitMode.Ranges && !PdfToolkitManager.ParseRanges(TxtPageRanges.Text).IsValid)
            {
                TxtProgressStatus.Text = Loc.F("Str_PdfToolkit_StatusCheckPages", "Check the pages to split out - {0}", PdfToolkitManager.DescribeRanges(PdfToolkitManager.ParseRanges(TxtPageRanges.Text)));
                TxtPageRanges.Focus();
                return;
            }

            // Where to save: the Output card's folder, or ask when "Save without asking" is off.
            string? folder = OutputCard.EffectiveFolder;
            if (_settings.SaveWithoutAsking)
            {
                if (OutputCard.Status.IsError)
                {
                    TxtProgressStatus.Text = "Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.";
                    return;
                }
                try
                {
                    folder = OutputCard.EnsureFolder();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    TxtProgressStatus.Text = Loc.F("Str_PdfToolkit_StatusFolderFailed", "The folder couldn't be created: {0}", PdfToolkitManager.PlainError(ex));
                    return;
                }
            }
            folder ??= PdfToolkitManager.DefaultFolder;
            string mergeName = TxtMergeFileName.Text;
            bool overwriteAllowed = false;
            if (!_settings.SaveWithoutAsking)
            {
                if (op == PdfToolOperation.Merge)
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "Save the merged PDF",
                        Filter = "PDF files|*.pdf",
                        DefaultExt = ".pdf",
                        AddExtension = true,
                        OverwritePrompt = true,
                        FileName = Path.GetFileName(PdfToolkitManager.ResolveMergePath(folder, mergeName)),
                    };
                    if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
                    if (dialog.ShowDialog() != true) return;
                    folder = Path.GetDirectoryName(dialog.FileName)!;
                    mergeName = Path.GetFileName(dialog.FileName);
                    overwriteAllowed = true;
                }
                else
                {
                    string? picked = PickFolder(op == PdfToolOperation.Split ? "Where should the split files go?" : "Where should the smaller copies go?");
                    if (picked == null) return;
                    folder = picked;
                }
            }

            var options = new PdfToolOptions
            {
                Operation = op,
                OutputFolder = folder,
                MergeFileName = mergeName,
                OverwriteAllowed = overwriteAllowed,
                AddBookmarks = _settings.AddBookmarks,
                SplitMode = _settings.SplitMode,
                EveryNPages = _settings.EveryNPages,
                Ranges = TxtPageRanges.Text,
                RangesInOneFile = _settings.RangesInOneFile,
                CompressLevel = _settings.CompressLevel,
            };

            var cts = new CancellationTokenSource();
            _runCts = cts;
            SetBusy(true);
            ResetResults();
            var progress = new Progress<PdfToolProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                ProgressBarRun.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgressStatus.Text = $"{p.Processed:N0} of {p.Total:N0} · {p.CurrentFile}";
            });

            try
            {
                var result = await PdfToolkitManager.RunAsync(snapshot, options, PdfToolkitManager.Engine, progress, cts.Token);
                ShowResult(op, result);
            }
            catch (Exception ex)
            {
                ProgressBarRun.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgressStatus.Text = "Couldn't save the result: " + PdfToolkitManager.PlainError(ex);
                AppModal.Show(Loc.F("Str_MB_TheResultCouldnT", "The result couldn't be saved.\n\n{0}\n\nCheck that the folder exists, you can write to it, and the file isn't open in another program.", ex.Message),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _runCts = null;
                cts.Dispose();
                SetBusy(false);
                UpdateListState();
            }
        }

        private void ShowResult(PdfToolOperation op, PdfToolResult result)
        {
            TxtProgressStatus.Text = PdfToolkitManager.DescribeResult(op, result);
            _lastFiles = result.Files;
            bool any = result.Files.Count > 0;
            BtnOpenOutput.IsEnabled = any;
            BtnShowInFolder.IsEnabled = any;
            BtnOpenOutput.ToolTip = result.Files.Count > 1 ? "Open the folder with the results" : "Open the result";
            ProgressBarRun.Value = any && !result.Cancelled ? 100 : 0;
            TxtProgressPercent.Text = any && !result.Cancelled ? "100%" : "";
            if (any) UiMotion.Pop(BtnOpenOutput, 0.9);
            if (result.Error != null && !result.Cancelled)
                AppModal.Show(result.Error, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ResetResults()
        {
            _lastFiles = new List<string>();
            BtnOpenOutput.IsEnabled = false;
            BtnShowInFolder.IsEnabled = false;
            ProgressBarRun.Value = 0;
            TxtProgressPercent.Text = "";
            foreach (var item in _items)
            {
                item.Status = PdfItemStatus.Ready;
                item.Result = "";
                if (item.PageCount >= 0) item.Message = "";
                item.Outputs.Clear();
            }
            UpdateListState();
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            OptionsScrollViewer.IsEnabled = !busy;
            OutputCard.IsLocked = busy;
            if (!busy) OutputCard.Refresh();   // free space may have changed
            ChkIncludeAll.IsEnabled = !busy;
            if (busy)
            {
                IconRun.Kind = PackIconKind.Close;
                TxtRunLabel.Text = "Cancel";
                BtnRun.ToolTip = "Stop - files already finished are kept, the one in progress is not saved";
            }
            UpdateOptionPanels(animate: false);   // puts the run button's label back when idle
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 1) OpenWithShell(_lastFiles[0]);
            else if (_lastFiles.Count > 1) OpenWithShell(Path.GetDirectoryName(_lastFiles[0])!);
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 0) return;
            string file = _lastFiles[0];
            try
            {
                if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else if (Directory.Exists(Path.GetDirectoryName(file))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(file)}\"");
                else TxtProgressStatus.Text = Loc.T("Str_PdfToolkit_StatusGone", "The result is no longer there.");
            }
            catch (Exception ex)
            {
                TxtProgressStatus.Text = "Couldn't open the folder: " + PdfToolkitManager.PlainError(ex);
            }
        }

        private void OpenWithShell(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    TxtProgressStatus.Text = $"{Path.GetFileName(path)} is no longer there.";
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TxtProgressStatus.Text = $"Couldn't open {Path.GetFileName(path)}: " + PdfToolkitManager.PlainError(ex);
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static string? CheckedTag(params RadioButton[] buttons) => buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;

        private static void CheckByTag(string tag, params RadioButton[] buttons)
        {
            var match = buttons.FirstOrDefault(b => b.Tag as string == tag) ?? buttons[0];
            match.IsChecked = true;
        }

        private static T ParseTag<T>(string? tag, T fallback) where T : struct, Enum =>
            Enum.TryParse(tag, out T value) && Enum.IsDefined(value) ? value : fallback;

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
    }
}
