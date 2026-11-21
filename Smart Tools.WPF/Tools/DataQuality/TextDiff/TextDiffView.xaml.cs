using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Text Diff - UI only. The comparison is File Differences' engine, run by <see cref="TextDiffManager"/> on text
    /// from the two boxes. It re-runs a moment after typing stops and when an option changes.
    /// </summary>
    public partial class TextDiffView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

        private readonly TextDiffSettings _settings;
        private readonly DispatcherTimer _timer = new() { Interval = Debounce };
        private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly HashSet<int> _openedGaps = new();
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _cts;
        private long _generation;
        private FileDiffResult? _result;
        private TextDiffVerdict _verdict = TextDiffVerdict.Nothing;
        private FileDiffProjection? _projection;
        private int _changeCursor = -1;
        private bool _loadedOnce;
        private bool _optionsCollapsed;

        public TextDiffView()
        {
            InitializeComponent();
            _settings = TextDiffManager.LoadSettings();
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _ = CompareAsync();
            };
            _copyTimer.Tick += (_, _) =>
            {
                _copyTimer.Stop();
                IconCopyPatch.Kind = PackIconKind.ContentCopy;
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: drops a comparison in progress and saves the setup. The texts are not kept.</summary>
        public void CancelActiveRun()
        {
            _timer.Stop();
            _copyTimer.Stop();
            _cts?.Cancel();
            TextDiffManager.SaveSettings(_settings);
        }

        private static string T(string key, string fallback = "") => Application.Current?.TryFindResource(key) as string ?? (fallback.Length > 0 ? fallback : key);
        private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            RadSideBySide.IsChecked = _settings.ViewMode == FileDiffViewMode.SideBySide;
            RadInline.IsChecked = _settings.ViewMode == FileDiffViewMode.Inline;
            ChkWhitespace.IsChecked = _settings.IgnoreWhitespace;
            ChkCase.IsChecked = _settings.IgnoreCase;
            ChkOnlyChanges.IsChecked = _settings.OnlyChanges;
            UpdateCounts();
            ShowMessage(TextDiffVerdict.Nothing);
            TxtOriginal.Focus();
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

        // =================================================================
        //  The two boxes
        // =================================================================

        private void Text_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateCounts();
            _timer.Stop();
            _timer.Start();
        }

        private void UpdateCounts()
        {
            TxtCountOriginal.Text = CountText(TxtOriginal.Text);
            TxtCountChanged.Text = CountText(TxtChanged.Text);
        }

        private static string CountText(string text) =>
            F("Str_TextDiff_Count", TextDiffManager.Lines(text).Length.ToString("N0", CultureInfo.CurrentCulture), text.Length.ToString("N0", CultureInfo.CurrentCulture));

        private void BtnPasteOriginal_Click(object sender, RoutedEventArgs e) => Paste(TxtOriginal);
        private void BtnPasteChanged_Click(object sender, RoutedEventArgs e) => Paste(TxtChanged);

        private static void Paste(System.Windows.Controls.TextBox box)
        {
            try
            {
                if (Clipboard.ContainsText()) box.Text = Clipboard.GetText();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        private void BtnClearOriginal_Click(object sender, RoutedEventArgs e) => TxtOriginal.Clear();
        private void BtnClearChanged_Click(object sender, RoutedEventArgs e) => TxtChanged.Clear();

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            (TxtOriginal.Text, TxtChanged.Text) = (TxtChanged.Text, TxtOriginal.Text);
        }

        private void BtnOpenOriginal_Click(object sender, RoutedEventArgs e) => OpenInto(TxtOriginal);
        private void BtnOpenChanged_Click(object sender, RoutedEventArgs e) => OpenInto(TxtChanged);

        private void OpenInto(System.Windows.Controls.TextBox box)
        {
            var dialog = new OpenFileDialog { Filter = T("Str_FileDiff_OpenFilter"), Title = T("Str_TextDiff_OpenFile") };
            if (dialog.ShowDialog() == true) LoadFile(box, dialog.FileName);
        }

        private void Box_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Dropped text is handled by the box itself; only files need a nudge.
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void TxtOriginal_PreviewDrop(object sender, DragEventArgs e) => DropFile(TxtOriginal, e);
        private void TxtChanged_PreviewDrop(object sender, DragEventArgs e) => DropFile(TxtChanged, e);

        private void DropFile(System.Windows.Controls.TextBox box, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            e.Handled = true;
            LoadFile(box, files[0]);
        }

        private void LoadFile(System.Windows.Controls.TextBox box, string path)
        {
            try
            {
                if (Directory.Exists(path)) throw new InvalidDataException("Folder");
                box.Text = TextDiffManager.ReadTextFile(path);
            }
            catch (InvalidDataException ex)
            {
                ShowReadProblem(ex.Message switch
                {
                    "TooBig" => F("Str_TextDiff_FileTooBig", (TextDiffManager.MaxFileBytes / 1024 / 1024).ToString(CultureInfo.CurrentCulture)),
                    "Folder" => T("Str_FileDiff_StatusFoldersNotSupported"),
                    _ => T("Str_TextDiff_FileNotText"),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowReadProblem(F("Str_FileDiff_StatusReadFailed", Path.GetFileName(path), ex.Message));
            }
        }

        private void ShowReadProblem(string text) =>
            ThemedMessageBox.Show(Window.GetWindow(this), text, T("Str_TextDiff_ReadTitle", "Text Difference"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // =================================================================
        //  Options
        // =================================================================

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _settings.ViewMode = RadInline.IsChecked == true ? FileDiffViewMode.Inline : FileDiffViewMode.SideBySide;
            _openedGaps.Clear();
            ShowRows(scrollToTop: true);
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool compareAgain = _settings.IgnoreWhitespace != (ChkWhitespace.IsChecked == true) || _settings.IgnoreCase != (ChkCase.IsChecked == true);
            _settings.IgnoreWhitespace = ChkWhitespace.IsChecked == true;
            _settings.IgnoreCase = ChkCase.IsChecked == true;
            _settings.OnlyChanges = ChkOnlyChanges.IsChecked == true;
            _openedGaps.Clear();

            if (compareAgain) _ = CompareAsync();
            else ShowRows(scrollToTop: true);
        }

        // =================================================================
        //  Comparing
        // =================================================================

        private async Task CompareAsync()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            long generation = ++_generation;

            string original = TxtOriginal.Text, changed = TxtChanged.Text;
            var settings = new TextDiffSettings { IgnoreWhitespace = _settings.IgnoreWhitespace, IgnoreCase = _settings.IgnoreCase };

            try
            {
                var (result, verdict) = await Task.Run(() => TextDiffManager.Compare(original, changed, settings, cts.Token), cts.Token);
                if (generation != _generation) return;   // typed again while this ran

                _result = result;
                _verdict = verdict;
                _openedGaps.Clear();
                ShowResult();
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer text; that comparison shows its own result.
            }
        }

        private void ShowResult()
        {
            var result = _result;
            bool different = _verdict == TextDiffVerdict.Different && result != null;

            if (different)
            {
                TxtAddedChip.Text = "+" + result!.LinesAdded.ToString("N0", CultureInfo.CurrentCulture);
                TxtRemovedChip.Text = "−" + result.LinesRemoved.ToString("N0", CultureInfo.CurrentCulture);
                TxtSimilarityChip.Text = F("Str_FileDiff_SimilarityChip", Percent(result.SimilarityPercent));
            }
            ChipAdded.Visibility = ChipRemoved.Visibility = ChipSimilarity.Visibility = different ? Visibility.Visible : Visibility.Collapsed;
            BtnCopyPatch.IsEnabled = BtnSavePatch.IsEnabled = different;

            if (different)
            {
                ShowRows(scrollToTop: true);
            }
            else
            {
                _projection = null;
                _changeCursor = -1;
                DiffList.ItemsSource = null;
                ShowMessage(_verdict);
                UpdateChangeNavigation();
            }
        }

        private static string Percent(double value) =>
            value >= 99.95 || value <= 0.05 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture);

        private void ShowMessage(TextDiffVerdict verdict)
        {
            (PackIconKind icon, string title, string hint) = verdict switch
            {
                TextDiffVerdict.Identical => (PackIconKind.CheckCircleOutline, T("Str_TextDiff_IdenticalTitle"), T("Str_TextDiff_IdenticalHint")),
                TextDiffVerdict.SameWithOptions => (PackIconKind.CheckCircleOutline, T("Str_FileDiff_StatusIgnoredSame"), T("Str_FileDiff_NoteIgnored")),
                _ => (PackIconKind.FileCompare, T("Str_TextDiff_EmptyTitle"), T("Str_TextDiff_EmptyHint")),
            };
            MessagePanel.Icon = icon;
            MessagePanel.Title = title;
            MessagePanel.Description = hint;
            MessagePanel.Visibility = Visibility.Visible;
            DiffListBorder.Visibility = Visibility.Collapsed;
        }

        /// <summary>Shows the last result's rows for the current view options; opening a gap keeps the scroll position.</summary>
        private void ShowRows(bool scrollToTop)
        {
            if (_result == null || _verdict != TextDiffVerdict.Different) return;

            var scroller = FindScroller(DiffList);
            double offset = scroller?.VerticalOffset ?? 0;

            bool sideBySide = _settings.ViewMode == FileDiffViewMode.SideBySide;
            bool onlyChanges = _settings.OnlyChanges;

            _projection = sideBySide
                ? FileDifferencesManager.Project(_result.Rows, onlyChanges, _openedGaps,
                    (start, count) => new FileDiffRow { Kind = FileDiffLineKind.Gap, GapStart = start, GapCount = count, GapText = GapText(count) })
                : FileDifferencesManager.Project(_result.InlineRows, onlyChanges, _openedGaps,
                    (start, count) => new FileDiffInlineRow { Kind = FileDiffLineKind.Gap, GapStart = start, GapCount = count, GapText = GapText(count) });

            DiffList.ItemTemplate = (DataTemplate)FindResource(sideBySide ? "SideBySideTemplate" : "InlineTemplate");
            DiffList.ItemsSource = _projection.Rows;
            _changeCursor = -1;

            DiffListBorder.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;

            if (scroller != null)
            {
                DiffList.UpdateLayout();
                scroller.ScrollToVerticalOffset(scrollToTop ? 0 : offset);
            }
            UpdateChangeNavigation();
        }

        private static ScrollViewer? FindScroller(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }

        private static string GapText(int count) => F("Str_FileDiff_GapText", count.ToString("N0", CultureInfo.CurrentCulture));

        // =================================================================
        //  Gaps and change navigation
        // =================================================================

        /// <summary>Clicking a "⋯ 120 unchanged lines" row shows those lines in place.</summary>
        private void DiffList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(DiffList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;

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

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F8) return;
            MoveToChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            e.Handled = true;
        }

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
            TxtChangePosition.Text = F("Str_FileDiff_ChangeOf", _changeCursor + 1, count);
        }

        private void UpdateChangeNavigation()
        {
            int count = _projection?.ChangeStarts.Count ?? 0;
            BtnPrevChange.IsEnabled = BtnNextChange.IsEnabled = count > 0;
            TxtChangePosition.Text = count switch
            {
                0 => "",
                1 => T("Str_FileDiff_OneChange"),
                _ => F("Str_FileDiff_ChangesCount", count.ToString("N0", CultureInfo.CurrentCulture)),
            };
        }

        // =================================================================
        //  Copy / save as a unified diff
        // =================================================================

        private string? BuildPatch() =>
            _result != null && _verdict == TextDiffVerdict.Different
                ? TextDiffManager.BuildPatch(_result, T("Str_FileDiff_Original").ToLowerInvariant(), T("Str_FileDiff_Changed").ToLowerInvariant())
                : null;

        private void BtnCopyPatch_Click(object sender, RoutedEventArgs e)
        {
            if (BuildPatch() is not string patch) return;
            try
            {
                Clipboard.SetText(patch);
                IconCopyPatch.Kind = PackIconKind.Check;
                _copyTimer.Stop();
                _copyTimer.Start();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        private async void BtnSavePatch_Click(object sender, RoutedEventArgs e)
        {
            if (BuildPatch() is not string patch) return;

            var dialog = new SaveFileDialog
            {
                Filter = T("Str_FileDiff_SaveFilter"),
                DefaultExt = ".diff",
                FileName = FileDifferencesManager.SuggestedDiffName("original", "changed"),
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                await FileDifferencesManager.SaveDiffAsync(dialog.FileName, patch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowReadProblem(F("Str_FileDiff_StatusSaveFailed", ex.Message));
            }
        }
    }
}
