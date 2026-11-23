using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Loudness Meter &amp; Normalizer - UI only. Measuring, the normalized copies and the report are in
    /// <see cref="LoudnessManager"/>. Originals are only ever read.
    /// </summary>
    public partial class LoudnessView : UserControl, IHubPage
    {
        /// <summary>Hub tab strip slot (see <see cref="IHubPage"/>).</summary>
        public void SetHubTabs(UIElement? strip) => HubTabsHost.Content = strip;

        private static string DialogTitle => Loc.T("Str_Tool_loudness-meter_Name", "Loudness Meter & Normalizer");

        private readonly BulkObservableCollection<LoudnessItem> _items = new();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loadedOnce;
        private bool _hasResult;

        public LoudnessView()
        {
            InitializeComponent();
            foreach (var t in LoudnessManager.Targets)
                CmbTarget.Items.Add(new ComboBoxItem { Content = $"{t.Name}  ({t.Lufs:0} LUFS)", Tag = t.Lufs });
            CmbTarget.Items.Add(new ComboBoxItem { Content = "Custom...", Tag = "custom" });
            CmbTarget.SelectedIndex = 1;   // YouTube

            InputCard.ItemsSource = _items;
            ResultGrid.ItemsSource = _items;
            BuildResultsContextMenu();
            _items.CollectionChanged += (_, _) => InputCard.SetEmpty(_items.Count == 0);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ShortcutManager.AttachCommon("audio-editor", ShortcutManager.RunId, () => BtnMeasure_Click(this, new RoutedEventArgs()));
            UpdateState();
        }

        // =================================================================
        //  Files and options
        // =================================================================

        private async void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            var paths = e.Paths.ToList();
            var files = await Task.Run(() => LoudnessManager.ExpandPaths(paths));
            // One set lookup per file (was a list search per file: 10,000 files = 50 million compares) and ONE list change.
            var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var fresh = new List<LoudnessItem>();
            foreach (string f in files)
                if (known.Add(f)) fresh.Add(new LoudnessItem(f));
            _items.AddRange(fresh);
            if (files.Count == 0) ShowStatus("No audio files found there (mp3, wav, flac, m4a, aac, ogg, opus, wma, aiff).", StatusKind.Warning);
            else HideStatus();
            _hasResult = false;
            UpdateState();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        private void BtnClearList_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (_busy) return;
            _items.Clear();
            _hasResult = false;
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            HideStatus();
            UpdateState();
        }

        private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not LoudnessItem item) return;
            _items.Remove(item);
            UpdateState();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _busy) return;
            bool custom = (CmbTarget.SelectedItem as ComboBoxItem)?.Tag is "custom";
            TxtCustomLufs.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            LoudnessTargetPanel.Visibility = RadioPeak.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            Rejudge();   // a new target only changes the verdicts, not the measurements
        }

        private LoudnessOptions ReadOptions()
        {
            double target = -14;
            if ((CmbTarget.SelectedItem as ComboBoxItem)?.Tag is double t) target = t;
            else if (double.TryParse(TxtCustomLufs.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double c)) target = Math.Clamp(c, -40, -5);

            double ceiling = double.TryParse((CmbCeiling.SelectedItem as ComboBoxItem)?.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double ce) ? ce : -1;
            int kbps = int.TryParse((CmbBitrate.SelectedItem as ComboBoxItem)?.Tag as string, out int b) ? b : 192;
            return new LoudnessOptions
            {
                Mode = RadioPeak.IsChecked == true ? LoudnessMode.Peak : LoudnessMode.Loudness,
                TargetLufs = target,
                CeilingDb = ceiling,
                BitrateKbps = kbps,
                OutputFolder = OutputCard.EffectiveFolder ?? "",
            };
        }

        private void Rejudge()
        {
            var o = ReadOptions();
            foreach (var i in _items)
            {
                var (verdict, delta) = LoudnessManager.Judge(i.Measure, o);
                i.Verdict = verdict;
                i.Delta = delta;
            }
            UpdateSummary();
        }

        // =================================================================
        //  Measure / normalize
        // =================================================================

        private void BtnMeasure_Click(object sender, RoutedEventArgs e) => StartOrStop(normalize: false);

        private void BtnNormalize_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) StartOrStop(normalize: true);
        }

        private async void StartOrStop(bool normalize)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }
            if (_items.Count == 0)
            {
                ShowStatus("Add at least one audio file.", StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }
            if (AudioTrimmerManager.MissingEngine() is string missing)
            {
                ShowStatus(missing, StatusKind.Error);
                return;
            }

            var o = ReadOptions();
            if (normalize)
            {
                if (OutputCard.Status.IsError)
                {
                    ShowStatus("Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save copies to.", StatusKind.Error);
                    return;
                }
                string folder = o.OutputFolder;
                var shortfalls = OutputFolderManager.CheckSpace(_items.Select(i =>
                    (Path.Combine(string.IsNullOrEmpty(folder) ? i.Folder : folder, i.Name), SafeLength(i.Path), true)));
                if (shortfalls.Count > 0)
                {
                    ShowStatus(OutputFolderManager.DescribeShortfalls(shortfalls) + " Free some space or choose another folder.", StatusKind.Error);
                    return;
                }
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            HideStatus();
            SetBusy(true, normalize);
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            foreach (var i in _items)
            {
                if (normalize || i.Measure == null) i.Status = LoudnessStatus.Waiting;
                i.Message = "";
            }

            int failed = 0, done = 0;
            var list = _items.ToList();
            try
            {
                for (int n = 0; n < list.Count; n++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = list[n];
                    int index = n;
                    void Fraction(double f) => Dispatcher.BeginInvoke(() => ShowProgress(index, list.Count, f, item.Name));

                    try
                    {
                        await ProcessAsync(item, o, normalize, Fraction, token);
                        if (item.Status == LoudnessStatus.Done) done++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        item.Status = LoudnessStatus.Failed;
                        item.Message = ex.Message;
                        failed++;
                    }
                    ShowProgress(n + 1, list.Count, 0, item.Name);
                }
            }
            catch (OperationCanceledException)
            {
                foreach (var i in list.Where(i => i.Status is LoudnessStatus.Waiting or LoudnessStatus.Measuring or LoudnessStatus.Normalizing))
                    i.Status = i.Measure != null ? LoudnessStatus.Measured : LoudnessStatus.Cancelled;
                SetBusy(false, normalize);
                _hasResult = true;
                TxtProgress.Text = "Stopped. Nothing was left half-written.";
                UpdateState();
                return;
            }

            SetBusy(false, normalize);
            _hasResult = true;
            Bar.Value = 100;
            TxtProgressPercent.Text = "100%";
            TxtProgress.Text = normalize
                ? $"{done:N0} cop{(done == 1 ? "y" : "ies")} saved" + (failed > 0 ? $" · {failed:N0} failed" : "")
                : $"{list.Count(i => i.Measure != null):N0} measured" + (failed > 0 ? $" · {failed:N0} failed" : "");
            if (failed > 0) ShowStatus($"{failed:N0} file{(failed == 1 ? "" : "s")} couldn't be processed - hover a row for the reason. The originals were not touched.", StatusKind.Error);
            else if (normalize && done > 0) ShowStatus("Copies saved. Each one was measured again after writing - the loudness shown is what the copy really has.", StatusKind.Info);
            UpdateState();
        }

        private async Task ProcessAsync(LoudnessItem item, LoudnessOptions o, bool normalize, Action<double> fraction, CancellationToken token)
        {
            if (item.Measure == null)
            {
                item.Status = LoudnessStatus.Measuring;
                var probe = await AudioTrimmerManager.ProbeAsync(item.Path, token);
                if (!string.IsNullOrEmpty(probe.Error)) throw new InvalidOperationException(probe.Error);
                item.Duration = probe.Duration;
                var m = await LoudnessManager.MeasureAsync(item.Path, probe.Duration, normalize ? f => fraction(f * 0.3) : fraction, token);
                item.Measure = m;
                var (verdict, delta) = LoudnessManager.Judge(m, o);
                item.Verdict = verdict;
                item.Delta = delta;
            }

            if (item.Measure!.IntegratedLufs == null)
            {
                item.Status = LoudnessStatus.Silent;
                item.Message = "The file is silent, so there is nothing to measure or bring to a level.";
                return;
            }
            if (!normalize)
            {
                item.Status = LoudnessStatus.Measured;
                return;
            }

            item.Status = LoudnessStatus.Normalizing;
            var (path, achieved) = await LoudnessManager.NormalizeAsync(item, item.Measure, o, f => fraction(0.3 + f * 0.7), token);
            item.OutputPath = path;
            item.Achieved = achieved.IntegratedLufs;
            item.Status = LoudnessStatus.Done;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        private void ShowProgress(int index, int total, double fraction, string current)
        {
            double percent = Math.Min(100, (index + fraction) * 100.0 / Math.Max(1, total));
            Bar.Value = percent;
            TxtProgressPercent.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%", percent);
            TxtProgress.Text = $"{Math.Min(index + 1, total):N0} of {total:N0} · {current}";
            UpdateSummary();
        }

        // =================================================================
        //  Grid interaction
        // =================================================================

        private void ResultGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete || _busy) return;
            RemoveSelectedRows();
            e.Handled = true;
        }

        /// <summary>Takes the highlighted rows off the list. Only the list - no file is touched.</summary>
        private void RemoveSelectedRows()
        {
            foreach (var item in ResultGrid.SelectedItems.OfType<LoudnessItem>().ToList()) _items.Remove(item);
            UpdateState();
        }

        private void ResultGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Right-clicking a row selects it (unless it is already part of the selection), like the other result grids.
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is DataGridRow row)
                {
                    if (!row.IsSelected) { ResultGrid.SelectedItems.Clear(); row.IsSelected = true; }
                    return;
                }
            }
        }

        private void BuildResultsContextMenu()
        {
            var menu = new ContextMenu();
            MenuItem Item(string header, PackIconKind icon, Action action)
            {
                var mi = new MenuItem { Header = header, Icon = new PackIcon { Kind = icon } };
                mi.Click += (_, _) => action();
                menu.Items.Add(mi);
                return mi;
            }

            Item("Show in folder", PackIconKind.FolderOpenOutline, () =>
            {
                if (ResultGrid.SelectedItem is LoudnessItem i) ShellExplorer.Show(i.Path);
            });
            var copy = Item("Show the copy in folder", PackIconKind.FolderStarOutline, () =>
            {
                if (ResultGrid.SelectedItem is LoudnessItem i && i.OutputPath.Length > 0) ShellExplorer.Show(i.OutputPath);
            });
            menu.Items.Add(new Separator());
            var remove = Item("Take off the list", PackIconKind.PlaylistRemove, () => { if (!_busy) RemoveSelectedRows(); });

            menu.Opened += (_, _) =>
            {
                copy.IsEnabled = ResultGrid.SelectedItem is LoudnessItem { OutputPath.Length: > 0 };
                remove.IsEnabled = !_busy;
            };
            ResultGrid.ContextMenu = menu;
        }

        // =================================================================
        //  Report
        // =================================================================

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            if (!_items.Any(i => i.Measure != null)) return;
            try
            {
                System.Windows.Clipboard.SetText(LoudnessManager.BuildCsv(_items, ReadOptions()));
                TxtProgress.Text = "Report copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ShowStatus($"The clipboard is busy: {ex.Message}", StatusKind.Error);
            }
        }

        private async void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            if (!_items.Any(i => i.Measure != null)) return;
            var dialog = new SaveFileDialog { Title = DialogTitle, FileName = "loudness report.csv", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string written = await FolderTreeManager.SaveAsync(LoudnessManager.BuildCsv(_items, ReadOptions()), dialog.FileName);
                TxtProgress.Text = "Saved " + Path.GetFileName(written);
            }
            catch (Exception ex)
            {
                ShowStatus($"The report couldn't be saved: {ex.Message}", StatusKind.Error);
            }
        }

        // =================================================================
        //  State and messages
        // =================================================================

        private void SetBusy(bool busy, bool normalizing)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            OutputCard.IsLocked = busy;
            BtnNormalize.IsEnabled = !busy;
            TxtMeasure.Text = busy ? "Stop" : "Measure";
            IconMeasure.Kind = busy ? PackIconKind.Stop : PackIconKind.Speedometer;
        }

        private void UpdateState()
        {
            bool empty = _items.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ResultGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (!_busy && !_hasResult)
                TxtProgress.Text = _items.Count == 0 ? "Add audio files to start." : "Ready - press Measure.";
            bool any = _items.Any(i => i.Measure != null);
            BtnCopyReport.IsEnabled = any;
            BtnSaveReport.IsEnabled = any;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int files = _items.Count;
            int onTarget = _items.Count(i => i.Verdict == LoudnessVerdict.OnTarget);
            int off = _items.Count(i => i.Verdict is LoudnessVerdict.TooLoud or LoudnessVerdict.TooQuiet);
            int clip = _items.Count(i => i.Verdict == LoudnessVerdict.MayClip);
            int copies = _items.Count(i => i.Status == LoudnessStatus.Done);
            int failed = _items.Count(i => i.Status == LoudnessStatus.Failed);

            TxtSummaryFiles.Text = $"{files:N0} file{(files == 1 ? "" : "s")}";
            TxtSummaryOnTarget.Text = $"{onTarget:N0} on target";
            TxtSummaryOff.Text = $"{off:N0} off target";
            TxtSummaryClip.Text = $"{clip:N0} may clip";
            TxtSummaryCopies.Text = $"{copies:N0} cop{(copies == 1 ? "y" : "ies")}";
            TxtSummaryFailed.Text = $"{failed:N0} failed";
            PillOnTarget.Visibility = onTarget > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillOff.Visibility = off > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillClip.Visibility = clip > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillCopies.Visibility = copies > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool show = files > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private enum StatusKind { Info, Warning, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Info => ("StatusInfoBg", "StatusInfoFg", PackIconKind.InformationOutline),
                _ => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops measuring or writing. A copy in progress is removed; originals are never touched.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
