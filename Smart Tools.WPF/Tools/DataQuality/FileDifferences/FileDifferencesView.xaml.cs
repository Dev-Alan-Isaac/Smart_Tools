using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// File Differences - UI only. Reading, comparing and exporting run in
    /// <see cref="FileDifferencesManager"/> (off the UI thread, cancellable);
    /// data shapes live in FileDifferencesModel.cs.
    ///
    /// Picking or dropping the second file compares straight away. The
    /// ignore options re-compare the files already read (no disk access);
    /// the view options (side by side / inline, hide unchanged lines) only
    /// re-project the rows of the last comparison. Compare / F5 reads both
    /// files again, so changes saved in an editor show up.
    /// </summary>
    public partial class FileDifferencesView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int BusyDelayMs = 150;

        private enum Slot { A, B }
        private enum StatusKind { Neutral, Busy, Success, Warning, Error }

        private readonly FileDifferencesManager _manager = new();
        private readonly FileDiffSettings _settings;

        private string? _pathA, _pathB;
        private FileDiffDocument? _docA, _docB;
        private FileDiffResult? _result;
        private FileDiffProjection? _projection;
        private readonly HashSet<int> _openedGaps = new();
        private int _changeCursor = -1;

        private CancellationTokenSource? _runCts;
        private bool _optionsCollapsed;

        public FileDifferencesView()
        {
            InitializeComponent();

            _settings = FileDifferencesManager.LoadSettings();
            ChkIgnoreWhitespace.IsChecked = _settings.IgnoreWhitespace;
            ChkIgnoreCase.IsChecked = _settings.IgnoreCase;
            ChkOnlyChanges.IsChecked = _settings.OnlyChanges;
            (_settings.ViewMode == FileDiffViewMode.Inline ? ViewInline : ViewSideBySide).IsChecked = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun() => _runCts?.Cancel();

        private bool IsRunning => _runCts != null;

        private FileDiffOptions CurrentOptions => new()
        {
            IgnoreWhitespace = ChkIgnoreWhitespace.IsChecked == true,
            IgnoreCase = ChkIgnoreCase.IsChecked == true,
        };

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void CompareOption_Click(object sender, RoutedEventArgs e)
        {
            _settings.IgnoreWhitespace = ChkIgnoreWhitespace.IsChecked == true;
            _settings.IgnoreCase = ChkIgnoreCase.IsChecked == true;
            FileDifferencesManager.SaveSettings(_settings);

            if (_docA != null && _docB != null)
                _ = RunAsync(reload: false);
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out FileDiffViewMode mode) || mode == _settings.ViewMode)
                return;

            _settings.ViewMode = mode;
            FileDifferencesManager.SaveSettings(_settings);
            _openedGaps.Clear();   // gap positions are per list
            ShowRows(scrollToTop: true);
        }

        private void ChkOnlyChanges_Click(object sender, RoutedEventArgs e)
        {
            _settings.OnlyChanges = ChkOnlyChanges.IsChecked == true;
            FileDifferencesManager.SaveSettings(_settings);
            _openedGaps.Clear();
            ShowRows(scrollToTop: true);
        }

        // =================================================================
        //  Picking files
        // =================================================================

        private void SlotA_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => PickInto(Slot.A);
        private void SlotB_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => PickInto(Slot.B);

        private void PickInto(Slot slot)
        {
            var dialog = new OpenFileDialog
            {
                Title = T(slot == Slot.A ? "Str_FileDiff_PickATitle" : "Str_FileDiff_PickBTitle"),
                Filter = T("Str_FileDiff_OpenFilter"),
            };
            string? current = slot == Slot.A ? _pathA : _pathB;
            if (current != null) dialog.InitialDirectory = Path.GetDirectoryName(current);

            if (dialog.ShowDialog() == true)
                _ = SetFileAsync(slot, dialog.FileName);
        }

        /// <summary>One or two files: two fill both slots, one fills the empty slot (or Changed when both are full).</summary>
        private void BtnChooseFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = T("Str_FileDiff_PickTitle"),
                Filter = T("Str_FileDiff_OpenFilter"),
                Multiselect = true,
            };
            if (dialog.ShowDialog() == true)
                _ = OpenFilesAsync(dialog.FileNames);
        }

        private async Task OpenFilesAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count >= 2)
            {
                await SetFileAsync(Slot.A, paths[0], compare: false);
                await SetFileAsync(Slot.B, paths[1]);
            }
            else if (paths.Count == 1)
            {
                await SetFileAsync(_pathA == null ? Slot.A : Slot.B, paths[0]);
            }
        }

        private void BtnClearA_Click(object sender, RoutedEventArgs e) => ClearSlot(Slot.A);
        private void BtnClearB_Click(object sender, RoutedEventArgs e) => ClearSlot(Slot.B);

        private void ClearSlot(Slot slot)
        {
            _runCts?.Cancel();
            if (slot == Slot.A) { _pathA = null; _docA = null; }
            else { _pathB = null; _docB = null; }

            UpdateSlot(slot);
            ClearResult();
            SetStatus(T(_pathA == null && _pathB == null ? "Str_FileDiff_StatusPick" : "Str_FileDiff_StatusPickOther"), StatusKind.Neutral);
            UpdateButtons();
        }

        private async void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            (_pathA, _pathB) = (_pathB, _pathA);
            (_docA, _docB) = (_docB, _docA);
            UpdateSlot(Slot.A);
            UpdateSlot(Slot.B);
            UiMotion.Pop(SlotA);
            UiMotion.Pop(SlotB);

            if (_docA != null && _docB != null) await RunAsync(reload: false);
        }

        private async void BtnReload_Click(object sender, RoutedEventArgs e) => await RunAsync(reload: true);

        private async void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (IsRunning)
            {
                _runCts?.Cancel();
                return;
            }
            await RunAsync(reload: true);
        }

        /// <summary>Reads one file into a slot; compares when both slots are filled.</summary>
        private async Task SetFileAsync(Slot slot, string path, bool compare = true)
        {
            if (slot == Slot.A) { _pathA = path; _docA = null; }
            else { _pathB = path; _docB = null; }
            UpdateSlot(slot);
            UiMotion.Pop(slot == Slot.A ? SlotA : SlotB);
            UpdateButtons();

            if (compare && _pathA != null && _pathB != null)
            {
                await RunAsync(reload: false);
                return;
            }

            // Only one file so far: read it now so its slot can show what it is.
            var cts = BeginRun();
            try
            {
                var doc = await FileDifferencesManager.LoadAsync(path, cts.Token);
                if (!ReferenceEquals(_runCts, cts)) return;
                if (slot == Slot.A && _pathA == path) _docA = doc;
                if (slot == Slot.B && _pathB == path) _docB = doc;
                UpdateSlot(slot);
                if (compare) SetStatus(T("Str_FileDiff_StatusPickOther"), StatusKind.Neutral);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ReferenceEquals(_runCts, cts))
                    SetStatus(T("Str_FileDiff_StatusReadFailed", Path.GetFileName(path), ReadError(ex)), StatusKind.Error);
            }
            finally
            {
                EndRun(cts);
            }
        }

        // =================================================================
        //  Comparing
        // =================================================================

        private CancellationTokenSource BeginRun()
        {
            _runCts?.Cancel();
            var cts = new CancellationTokenSource();
            _runCts = cts;
            return cts;
        }

        private void EndRun(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_runCts, cts))
            {
                _runCts = null;
                ShowRunning(false);
            }
            cts.Dispose();
        }

        /// <summary>
        /// Compares the two slots. reload = read both files from disk again;
        /// otherwise only files not read yet are read. A newer run cancels
        /// this one, and a superseded run shows nothing (not even its error).
        /// </summary>
        private async Task RunAsync(bool reload)
        {
            if (_pathA == null || _pathB == null) return;

            var cts = BeginRun();
            var token = cts.Token;
            string pathA = _pathA, pathB = _pathB;
            _ = ShowBusyLaterAsync(cts);

            try
            {
                if (reload || _docA == null || _docA.Path != Path.GetFullPath(pathA))
                {
                    _docA = null;
                    _docA = await LoadOrReportAsync(pathA, token);
                    if (!ReferenceEquals(_runCts, cts)) return;
                    UpdateSlot(Slot.A);
                    if (_docA == null) { ClearResult(); return; }
                }
                if (reload || _docB == null || _docB.Path != Path.GetFullPath(pathB))
                {
                    _docB = null;
                    _docB = await LoadOrReportAsync(pathB, token);
                    if (!ReferenceEquals(_runCts, cts)) return;
                    UpdateSlot(Slot.B);
                    if (_docB == null) { ClearResult(); return; }
                }

                var result = await _manager.CompareAsync(_docA, _docB, CurrentOptions, token);
                if (!ReferenceEquals(_runCts, cts)) return;

                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(_runCts, cts)) SetStatus(T("Str_FileDiff_StatusCancelled"), StatusKind.Warning);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_runCts, cts))
                {
                    ClearResult();
                    SetStatus(T("Str_FileDiff_StatusCompareFailed", ex.Message), StatusKind.Error);
                }
            }
            finally
            {
                EndRun(cts);
            }
        }

        /// <summary>The document, or null after putting the reason in the status pill.</summary>
        private async Task<FileDiffDocument?> LoadOrReportAsync(string path, CancellationToken token)
        {
            try
            {
                return await FileDifferencesManager.LoadAsync(path, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!token.IsCancellationRequested)
                    SetStatus(T("Str_FileDiff_StatusReadFailed", Path.GetFileName(path), ReadError(ex)), StatusKind.Error);
                return null;
            }
        }

        /// <summary>"Comparing…" and the Cancel button only for runs that take a moment (quick ones don't flicker).</summary>
        private async Task ShowBusyLaterAsync(CancellationTokenSource cts)
        {
            try { await Task.Delay(BusyDelayMs, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            if (!ReferenceEquals(_runCts, cts)) return;
            ShowRunning(true);
            SetStatus(T("Str_FileDiff_StatusComparing"), StatusKind.Busy);
        }

        private void ShowRunning(bool running)
        {
            IconCompare.Kind = running ? PackIconKind.Stop : PackIconKind.FileCompare;
            TxtCompareLabel.SetResourceReference(TextBlock.TextProperty, running ? "Str_Common_Cancel" : "Str_FileDiff_CompareButton");
            UpdateButtons();
        }

        // =================================================================
        //  Showing the result
        // =================================================================

        private void ShowResult(FileDiffResult result)
        {
            _result = result;
            _openedGaps.Clear();

            // Status + chips
            bool binary = result.Outcome is FileDiffOutcome.BinaryIdentical or FileDiffOutcome.BinaryDifferent;
            switch (result.Outcome)
            {
                case FileDiffOutcome.Different:
                    SetStatus(result.ChangeBlocks == 1 ? T("Str_FileDiff_StatusOneChange") : T("Str_FileDiff_StatusChanges", result.ChangeBlocks.ToString("N0")), StatusKind.Warning);
                    break;
                case FileDiffOutcome.Identical:
                    SetStatus(T("Str_FileDiff_StatusIdentical"), StatusKind.Success);
                    break;
                case FileDiffOutcome.SameText:
                    SetStatus(T(result.Notes.Contains(FileDiffNote.IgnoredDifferences) ? "Str_FileDiff_StatusIgnoredSame" : "Str_FileDiff_StatusSameText"), StatusKind.Success);
                    break;
                case FileDiffOutcome.BinaryIdentical:
                    SetStatus(T("Str_FileDiff_StatusBinarySame"), StatusKind.Success);
                    break;
                case FileDiffOutcome.BinaryDifferent:
                    SetStatus(T("Str_FileDiff_StatusBinaryDiff"), StatusKind.Warning);
                    break;
            }
            UiMotion.Pop(BorderStatus);

            bool firstShow = ChipAdded.Visibility != Visibility.Visible;
            TxtAddedChip.Text = $"+{result.LinesAdded:N0}";
            TxtRemovedChip.Text = $"−{result.LinesRemoved:N0}";
            TxtSimilarityChip.Text = T("Str_FileDiff_SimilarityChip", FormatPercent(result.SimilarityPercent));
            ChipAdded.Visibility = ChipRemoved.Visibility = ChipSimilarity.Visibility = binary ? Visibility.Collapsed : Visibility.Visible;
            if (!binary && firstShow)
            {
                UiMotion.Pop(ChipAdded);
                UiMotion.Pop(ChipRemoved);
                UiMotion.Pop(ChipSimilarity);
            }

            ShowNotes(result);

            // Verdict instead of a list when there's nothing line-by-line to show.
            if (result.Outcome == FileDiffOutcome.Different)
            {
                VerdictPanel.Visibility = Visibility.Collapsed;
                ShowRows(scrollToTop: true);
                UiMotion.FadeIn(DiffListBorder, 4);
            }
            else
            {
                ShowVerdict(result);
            }

            UpdateButtons();
        }

        private void ShowNotes(FileDiffResult result)
        {
            var lines = new List<string>();
            foreach (var note in result.Notes)
            {
                lines.Add(note switch
                {
                    FileDiffNote.LineEndingsDiffer => T("Str_FileDiff_NoteLineEndings", _docA!.LineEnding, _docB!.LineEnding),
                    FileDiffNote.EncodingDiffers => T("Str_FileDiff_NoteEncoding", _docA!.EncodingName, _docB!.EncodingName),
                    FileDiffNote.FinalNewlineDiffers => T("Str_FileDiff_NoteFinalNewline", _docA!.EndsWithNewline ? "A" : "B"),
                    FileDiffNote.IgnoredDifferences => T("Str_FileDiff_NoteIgnored"),
                    _ => "",
                });
            }

            TxtNotes.Text = string.Join("\n", lines.Where(l => l.Length > 0));
            bool show = TxtNotes.Text.Length > 0;
            if (show != (NotesBanner.Visibility == Visibility.Visible))
                UiMotion.FadeVisibility(NotesBanner, show);
        }

        private void ShowVerdict(FileDiffResult result)
        {
            DiffList.ItemsSource = null;
            _projection = null;
            DiffListBorder.Visibility = Visibility.Collapsed;
            ColumnHeaders.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;

            var docA = _docA!;
            var docB = _docB!;
            string why = T(docA.IsTooLarge || docB.IsTooLarge ? "Str_FileDiff_ReasonTooLarge" : "Str_FileDiff_ReasonBinary");

            (PackIconKind icon, string title, string detail) = result.Outcome switch
            {
                FileDiffOutcome.Identical => (PackIconKind.CheckCircleOutline, T("Str_FileDiff_VerdictIdentical"),
                    T("Str_FileDiff_VerdictIdenticalDetail", FileDifferencesManager.FormatBytes(docA.Size))),
                FileDiffOutcome.SameText => (PackIconKind.CheckCircleOutline, T("Str_FileDiff_VerdictSameText"),
                    T(result.Notes.Contains(FileDiffNote.IgnoredDifferences) ? "Str_FileDiff_VerdictIgnoredDetail" : "Str_FileDiff_VerdictSameTextDetail")),
                FileDiffOutcome.BinaryIdentical => (PackIconKind.CheckCircleOutline, T("Str_FileDiff_VerdictIdentical"),
                    T("Str_FileDiff_BinarySameDetail", why)),
                _ => (PackIconKind.FileAlertOutline, T("Str_FileDiff_VerdictDifferent"),
                    result.FirstDifferentByte is long offset
                        ? T("Str_FileDiff_BinaryDiffAt", why, offset.ToString("N0"))
                        : T("Str_FileDiff_BinaryDiffLength", why, FileDifferencesManager.FormatBytes(docA.Size), FileDifferencesManager.FormatBytes(docB.Size))),
            };

            VerdictBadge.Tag = icon;
            TxtVerdictTitle.Text = title;
            TxtVerdictDetail.Text = detail;
            VerdictPanel.Visibility = Visibility.Visible;
            UiMotion.FadeIn(VerdictPanel, 4);
            UpdateChangeNavigation();
        }

        /// <summary>
        /// Projects the last result's rows for the current view options.
        /// Keeps the scroll position unless asked not to (opening a gap
        /// expands it in place).
        /// </summary>
        private void ShowRows(bool scrollToTop)
        {
            if (_result is not { Outcome: FileDiffOutcome.Different } result)
                return;

            var scroller = DiffList.Template?.FindName("DiffScroll", DiffList) as ScrollViewer;
            double offset = scroller?.VerticalOffset ?? 0;

            bool sideBySide = _settings.ViewMode == FileDiffViewMode.SideBySide;
            bool onlyChanges = _settings.OnlyChanges;

            _projection = sideBySide
                ? FileDifferencesManager.Project(result.Rows, onlyChanges, _openedGaps,
                    (start, count) => new FileDiffRow { Kind = FileDiffLineKind.Gap, GapStart = start, GapCount = count, GapText = GapText(count) })
                : FileDifferencesManager.Project(result.InlineRows, onlyChanges, _openedGaps,
                    (start, count) => new FileDiffInlineRow { Kind = FileDiffLineKind.Gap, GapStart = start, GapCount = count, GapText = GapText(count) });

            DiffList.ItemTemplate = (DataTemplate)FindResource(sideBySide ? "SideBySideTemplate" : "InlineTemplate");
            DiffList.ItemsSource = _projection.Rows;
            _changeCursor = -1;

            TxtHeaderA.Text = _docA?.Name ?? "";
            TxtHeaderB.Text = _docB?.Name ?? "";
            ColumnHeaders.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
            DiffListBorder.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            VerdictPanel.Visibility = Visibility.Collapsed;

            if (scroller != null)
            {
                DiffList.UpdateLayout();
                scroller.ScrollToVerticalOffset(scrollToTop ? 0 : offset);
            }

            UpdateChangeNavigation();
        }

        private string GapText(int count) => T("Str_FileDiff_GapText", count.ToString("N0"));

        private void ClearResult()
        {
            _result = null;
            _projection = null;
            _changeCursor = -1;
            _openedGaps.Clear();
            DiffList.ItemsSource = null;

            DiffListBorder.Visibility = Visibility.Collapsed;
            ColumnHeaders.Visibility = Visibility.Collapsed;
            VerdictPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            NotesBanner.Visibility = Visibility.Collapsed;
            ChipAdded.Visibility = ChipRemoved.Visibility = ChipSimilarity.Visibility = Visibility.Collapsed;

            UpdateChangeNavigation();
            UpdateButtons();
        }

        // =================================================================
        //  Gaps and change navigation
        // =================================================================

        /// <summary>Clicking a "⋯ 120 unchanged lines" row shows those lines in place.</summary>
        private void DiffList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(DiffList, e.OriginalSource as DependencyObject) is not ListBoxItem item)
                return;

            int gapStart = item.DataContext switch
            {
                FileDiffRow { Kind: FileDiffLineKind.Gap } row => row.GapStart,
                FileDiffInlineRow { Kind: FileDiffLineKind.Gap } row => row.GapStart,
                _ => -1,
            };
            if (gapStart < 0) return;

            e.Handled = true;
            _openedGaps.Add(gapStart);
            ShowRows(scrollToTop: false);
        }

        private void BtnPrevChange_Click(object sender, RoutedEventArgs e) => MoveToChange(-1);
        private void BtnNextChange_Click(object sender, RoutedEventArgs e) => MoveToChange(+1);

        private void MoveToChange(int direction)
        {
            if (_projection is not { ChangeStarts.Count: > 0 } projection) return;

            int count = projection.ChangeStarts.Count;
            _changeCursor = _changeCursor < 0
                ? (direction > 0 ? 0 : count - 1)
                : ((_changeCursor + direction) % count + count) % count;

            var row = projection.Rows[projection.ChangeStarts[_changeCursor]];
            DiffList.SelectedItem = row;
            DiffList.ScrollIntoView(row);
            TxtChangePosition.Text = T("Str_FileDiff_ChangeOf", _changeCursor + 1, count);
        }

        private void UpdateChangeNavigation()
        {
            int count = _projection?.ChangeStarts.Count ?? 0;
            BtnPrevChange.IsEnabled = BtnNextChange.IsEnabled = count > 0;
            TxtChangePosition.Text = count switch
            {
                0 => "",
                1 => T("Str_FileDiff_OneChange"),
                _ => T("Str_FileDiff_ChangesCount", count.ToString("N0")),
            };
        }

        // =================================================================
        //  Copy / save as a unified diff
        // =================================================================

        private string? BuildUnifiedDiff() =>
            _result is { Outcome: FileDiffOutcome.Different } result && _docA != null && _docB != null
                ? FileDifferencesManager.BuildUnifiedDiff(result, _docA.Name, _docB.Name)
                : null;

        private void BtnCopyDiff_Click(object sender, RoutedEventArgs e)
        {
            if (BuildUnifiedDiff() is not string diff) return;
            try
            {
                Clipboard.SetText(diff);
                SetStatus(T("Str_FileDiff_StatusCopied"), StatusKind.Success);
                UiMotion.Pop(BorderStatus);
            }
            catch (Exception ex)
            {
                SetStatus(T("Str_FileDiff_StatusCopyFailed", ex.Message), StatusKind.Error);
            }
        }

        private async void BtnSaveDiff_Click(object sender, RoutedEventArgs e)
        {
            if (BuildUnifiedDiff() is not string diff) return;

            var dialog = new SaveFileDialog
            {
                Filter = T("Str_FileDiff_SaveFilter"),
                DefaultExt = ".diff",
                FileName = FileDifferencesManager.SuggestedDiffName(_docA!.Name, _docB!.Name),
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string saved = await FileDifferencesManager.SaveDiffAsync(dialog.FileName, diff);
                bool renamed = !string.Equals(saved, dialog.FileName, StringComparison.OrdinalIgnoreCase);
                SetStatus(T(renamed ? "Str_FileDiff_StatusSavedRenamed" : "Str_FileDiff_StatusSaved", Path.GetFileName(saved)), StatusKind.Success);
                UiMotion.Pop(BorderStatus);
            }
            catch (Exception ex)
            {
                SetStatus(T("Str_FileDiff_StatusSaveFailed", ex.Message), StatusKind.Error);
            }
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (e.Key == Key.F8)
            {
                e.Handled = true;
                MoveToChange(shift ? -1 : +1);
            }
            else if (e.Key == Key.F5 || (ctrl && e.Key == Key.Enter))
            {
                e.Handled = true;
                if (_pathA != null && _pathB != null) _ = RunAsync(reload: true);
            }
            else if (ctrl && e.Key == Key.O)
            {
                e.Handled = true;
                BtnChooseFiles_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.Escape && IsRunning)
            {
                e.Handled = true;
                _runCts?.Cancel();
            }
        }

        // =================================================================
        //  Drag and drop
        // =================================================================

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private Border? SlotUnder(object? source)
        {
            var element = source as DependencyObject;
            while (element != null)
            {
                if (element == SlotA) return SlotA;
                if (element == SlotB) return SlotB;
                element = element is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        private void HighlightSlot(Border? slot)
        {
            foreach (var s in new[] { SlotA, SlotB })
            {
                if (s == slot) s.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
                else s.BorderBrush = Brushes.Transparent;
            }
        }

        private void Root_PreviewDragEnter(object sender, DragEventArgs e)
        {
            if (!HasFiles(e)) return;

            int count = (e.Data.GetData(DataFormats.FileDrop) as string[])?.Length ?? 0;
            TxtDropHint.Text = T(count >= 2 ? "Str_FileDiff_DropTwoHint" : "Str_FileDiff_DropOneHint");
            if (DropOverlay.Visibility != Visibility.Visible)
                UiMotion.FadeVisibility(DropOverlay, true);
        }

        private void Root_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            HighlightSlot(SlotUnder(e.OriginalSource));
        }

        /// <summary>Only hide once the pointer has really left (DragLeave also fires between child elements).</summary>
        private void Root_PreviewDragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            {
                UiMotion.FadeVisibility(DropOverlay, false);
                HighlightSlot(null);
            }
        }

        private void Root_PreviewDrop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropOverlay, false);
            var slot = SlotUnder(e.OriginalSource);
            HighlightSlot(null);

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
            e.Handled = true;

            var files = dropped.Where(File.Exists).ToList();
            if (files.Count == 0)
            {
                if (dropped.Length > 0) SetStatus(T("Str_FileDiff_StatusFoldersNotSupported"), StatusKind.Warning);
                return;
            }

            if (slot != null && files.Count == 1)
                _ = SetFileAsync(slot == SlotA ? Slot.A : Slot.B, files[0]);
            else
                _ = OpenFilesAsync(files);
        }

        // =================================================================
        //  Slots, buttons, status
        // =================================================================

        private void UpdateSlot(Slot slot)
        {
            string? path = slot == Slot.A ? _pathA : _pathB;
            var doc = slot == Slot.A ? _docA : _docB;
            var name = slot == Slot.A ? TxtNameA : TxtNameB;
            var info = slot == Slot.A ? TxtInfoA : TxtInfoB;
            var clear = slot == Slot.A ? BtnClearA : BtnClearB;
            var border = slot == Slot.A ? SlotA : SlotB;

            if (path == null)
            {
                name.SetResourceReference(TextBlock.TextProperty, "Str_FileDiff_SlotEmpty");
                name.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
                info.Visibility = Visibility.Collapsed;
                clear.Visibility = Visibility.Collapsed;
                border.SetResourceReference(ToolTipProperty, slot == Slot.A ? "Str_FileDiff_SlotATooltip" : "Str_FileDiff_SlotBTooltip");
                return;
            }

            name.Text = Path.GetFileName(path);
            name.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");
            clear.Visibility = Visibility.Visible;
            border.ToolTip = path;

            info.Text = doc == null ? T("Str_FileDiff_Reading")
                : doc.IsTooLarge ? T("Str_FileDiff_FileInfoTooLarge", FileDifferencesManager.FormatBytes(doc.Size))
                : doc.IsBinary ? T("Str_FileDiff_FileInfoBinary", FileDifferencesManager.FormatBytes(doc.Size))
                : T("Str_FileDiff_FileInfo",
                    FileDifferencesManager.FormatBytes(doc.Size),
                    doc.Lines!.Length.ToString("N0"),
                    doc.LineEnding.Length > 0 ? $"{doc.EncodingName} · {doc.LineEnding}" : doc.EncodingName);
            info.Visibility = Visibility.Visible;
        }

        private void UpdateButtons()
        {
            bool both = _pathA != null && _pathB != null;
            BtnCompare.IsEnabled = both || IsRunning;
            BtnReload.IsEnabled = both && !IsRunning;
            BtnSwap.IsEnabled = _pathA != null || _pathB != null;

            bool hasDiff = _result is { Outcome: FileDiffOutcome.Different };
            BtnCopyDiff.IsEnabled = BtnSaveDiff.IsEnabled = hasDiff;
        }

        private void SetStatus(string text, StatusKind kind)
        {
            TxtStatus.Text = text;
            TxtStatus.ToolTip = text;

            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                StatusKind.Busy => ("StatusInfoBg", "StatusInfoFg", PackIconKind.Cached),
                _ => ("RowBg", "TextSecondary", PackIconKind.InformationOutline),
            };

            BorderStatus.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconStatus.Kind = icon;
        }

        private string ReadError(Exception ex) => ex switch
        {
            FileNotFoundException => T("Str_FileDiff_ErrorMissing"),
            UnauthorizedAccessException => T("Str_FileDiff_ErrorAccess"),
            _ => ex.Message,
        };

        private static string FormatPercent(double value) =>
            value >= 99.95 || value <= 0.05 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture);

        // Localized strings (Language/en.xaml, es.xaml).
        private string T(string key) => TryFindResource(key) as string ?? key;
        private string T(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    /// <summary>
    /// Draws an <see cref="IFileDiffText"/> into a TextBlock: plain text, or
    /// the line's pieces with the changed words on the stronger tint
    /// (theme tokens DiffAddedStrongBg / DiffRemovedStrongBg).
    /// </summary>
    public static class FileDiffTextView
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(object), typeof(FileDiffTextView), new PropertyMetadata(null, OnSourceChanged));

        public static object? GetSource(DependencyObject element) => element.GetValue(SourceProperty);
        public static void SetSource(DependencyObject element, object? value) => element.SetValue(SourceProperty, value);

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block) return;
            block.Inlines.Clear();

            if (e.NewValue is not IFileDiffText line || line.Kind == FileDiffLineKind.Filler || line.Kind == FileDiffLineKind.Gap)
                return;

            if (line.Segments == null)
            {
                block.Inlines.Add(new Run(line.DisplayText));
                return;
            }

            string strongKey = line.Kind == FileDiffLineKind.Added ? "DiffAddedStrongBg" : "DiffRemovedStrongBg";
            foreach (var segment in line.Segments)
            {
                var run = new Run(segment.Text);
                if (segment.IsChanged) run.SetResourceReference(TextElement.BackgroundProperty, strongKey);
                block.Inlines.Add(run);
            }
        }
    }
}
