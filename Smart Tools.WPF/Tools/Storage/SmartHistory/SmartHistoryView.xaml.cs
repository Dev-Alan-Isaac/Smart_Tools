using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// SMART Trend History - UI only. Reading the drives is <see cref="DriveHealthManager"/>; the saved
    /// readings, warnings and CSV are in <see cref="SmartHistoryManager"/>.
    /// </summary>
    public partial class SmartHistoryView : UserControl
    {
        private const string DialogTitle = "SMART Trend History";

        private SmartHistoryFile _file = new();
        private CancellationTokenSource? _cts;
        private bool _loadedOnce;
        private bool _busy;
        private bool _updating;

        public SmartHistoryView()
        {
            InitializeComponent();
            foreach (SmartMetric m in Enum.GetValues<SmartMetric>())
                CmbMetric.Items.Add(new ComboBoxItem { Content = SmartHistoryText.Label(m), Tag = m });
            CmbMetric.SelectedIndex = 0;
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _file = SmartHistoryManager.Load();
            FillDrives(null);
            Refresh();
            await ReadDrivesAsync(force: false);
        }

        // =================================================================
        //  Reading the drives
        // =================================================================

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }
            _ = ReadDrivesAsync(force: true);
        }

        private async Task ReadDrivesAsync(bool force)
        {
            if (_busy) return;
            _busy = true;
            _cts = new CancellationTokenSource();
            BtnRecord.IsEnabled = true;
            Bar.IsIndeterminate = true;
            TxtProgress.Text = "Reading the drives...";
            DriveHealthManager.InvalidateCache();

            try
            {
                var scan = await DriveHealthManager.GetAllDrivesAsync(_cts.Token);
                if (scan.ErrorMessage != null)
                {
                    TxtProgress.Text = "Windows' storage information couldn't be read: " + scan.ErrorMessage;
                    return;
                }

                string? keep = (CmbDrive.SelectedItem as SmartDriveHistory)?.Key;
                int added = SmartHistoryManager.Record(_file, scan.Drives, DateTime.Now, SmartHistoryManager.AutoGap, force);
                if (added > 0)
                {
                    try
                    {
                        SmartHistoryManager.Save(_file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TxtProgress.Text = "The readings couldn't be saved: " + ex.Message;
                        FillDrives(keep);
                        Refresh();
                        return;
                    }
                }

                int withSmart = scan.Drives.Count(d => d.Smart != null);
                TxtProgress.Text = withSmart == 0
                    ? "None of the drives reported SMART data" + (scan.IsElevated ? "." : " - running Smart Tools as administrator may help.")
                    : added > 0
                        ? $"Recorded {added:N0} reading{(added == 1 ? "" : "s")} at {DateTime.Now:t}."
                        : $"Up to date - the last reading is less than {SmartHistoryManager.AutoGap.TotalHours:0} hours old. Press Record now to add one anyway.";
                FillDrives(keep);
                Refresh();
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Stopped.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "The drives couldn't be read: " + ex.Message;
            }
            finally
            {
                _busy = false;
                Bar.IsIndeterminate = false;
                Bar.Value = 0;
            }
        }

        // =================================================================
        //  Drive / metric / period
        // =================================================================

        private void FillDrives(string? keepKey)
        {
            _updating = true;
            CmbDrive.Items.Clear();
            foreach (var d in _file.Drives.Where(d => d.Readings.Count > 0).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                CmbDrive.Items.Add(d);
            var pick = CmbDrive.Items.OfType<SmartDriveHistory>().FirstOrDefault(d => d.Key == keepKey) ?? CmbDrive.Items.OfType<SmartDriveHistory>().FirstOrDefault();
            CmbDrive.SelectedItem = pick;
            _updating = false;
        }

        private void CmbDrive_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) Refresh(); }

        private void CmbMetric_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && IsLoaded) Refresh(); }

        private void CmbPeriod_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && IsLoaded) Refresh(); }

        private SmartMetric Metric => (CmbMetric.SelectedItem as ComboBoxItem)?.Tag is SmartMetric m ? m : SmartMetric.Temperature;

        private int PeriodDays => int.TryParse((CmbPeriod.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 0;

        private static bool HigherIsWorse(SmartMetric m) => m is SmartMetric.Temperature or SmartMetric.Reallocated or SmartMetric.Pending
            or SmartMetric.Uncorrectable or SmartMetric.MediaErrors or SmartMetric.UnsafeShutdowns;

        // =================================================================
        //  Showing it
        // =================================================================

        private void Refresh()
        {
            var drive = CmbDrive.SelectedItem as SmartDriveHistory;
            bool has = drive != null && drive.Readings.Count > 0;

            EmptyStatePanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            ChartCard.Visibility = ReadingsGrid.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BtnCopyCsv.IsEnabled = BtnSaveCsv.IsEnabled = BtnClearDrive.IsEnabled = has;
            SummaryPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

            if (!has)
            {
                WarningsPanel.Visibility = Visibility.Collapsed;
                TxtDriveInfo.Text = _file.Drives.Count == 0 ? "No drive has been recorded yet." : "";
                ReadingsGrid.ItemsSource = null;
                Chart.SetData(Array.Empty<(DateTime, double)>(), v => v.ToString("0"), false);
                return;
            }

            var visible = drive!.Readings.Where(r => PeriodDays == 0 || DateTime.Now - r.When <= TimeSpan.FromDays(PeriodDays)).ToList();
            var view = new SmartDriveHistory { Key = drive.Key, Name = drive.Name, IsNvme = drive.IsNvme, IsSsd = drive.IsSsd, Readings = visible };

            // chart
            var metric = Metric;
            TxtChartTitle.Text = SmartHistoryText.Label(metric) + (SmartHistoryManager.HasMetric(drive, metric) ? "" : " - this drive doesn't report it");
            Chart.SetData(SmartHistoryManager.Series(view, metric), v => SmartHistoryText.Format(metric, v), HigherIsWorse(metric));

            // grid, newest first
            var rows = new List<SmartReadingRow>(visible.Count);
            SmartReading? prev = null;
            foreach (var r in visible) { rows.Add(new SmartReadingRow(r, prev)); prev = r; }
            rows.Reverse();
            ReadingsGrid.ItemsSource = rows;

            // warnings use the whole history, not just the period shown
            var warnings = SmartHistoryManager.Analyze(drive, DateTime.Now);
            var facts = SmartHistoryManager.Facts(drive);
            WarningList.ItemsSource = warnings;
            TxtFacts.Text = string.Join("  ", facts);
            TxtFacts.Visibility = facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            WarningsPanel.Visibility = warnings.Count > 0 || facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            int danger = warnings.Count(w => w.Level == SmartWarningLevel.Danger);
            int warn = warnings.Count(w => w.Level == SmartWarningLevel.Warning);
            TxtSummaryReadings.Text = $"{drive.Readings.Count:N0} reading{(drive.Readings.Count == 1 ? "" : "s")} · {SpanText(drive)}";
            TxtSummaryDanger.Text = $"{danger:N0} serious";
            TxtSummaryWarn.Text = $"{warn:N0} to watch";
            PillDanger.Visibility = danger > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillWarn.Visibility = warn > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillOk.Visibility = danger + warn == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryOk.Text = drive.Readings.Count < 2 ? "Not enough readings to compare" : "Stable";
            TxtDriveInfo.Text = $"{drive.Readings.Count:N0} readings since {drive.Readings[0].When:d MMM yyyy}. " + (drive.IsNvme ? "NVMe SSD." : drive.IsSsd ? "SSD." : "Hard disk.");
        }

        private static string SpanText(SmartDriveHistory d)
        {
            double days = (d.Readings[^1].When - d.Readings[0].When).TotalDays;
            return days < 1 ? "today" : days < 60 ? $"{days:0} days" : $"{days / 30:0} months";
        }

        // =================================================================
        //  Export / clear
        // =================================================================

        private void BtnCopyCsv_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDrive.SelectedItem is not SmartDriveHistory d) return;
            try
            {
                System.Windows.Clipboard.SetText(SmartHistoryManager.BuildCsv(d));
                TxtProgress.Text = "History copied to the clipboard.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "The clipboard is busy: " + ex.Message;
            }
        }

        private async void BtnSaveCsv_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDrive.SelectedItem is not SmartDriveHistory d) return;
            string safe = string.Concat(d.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dialog = new SaveFileDialog { Title = DialogTitle, FileName = $"{safe} SMART history.csv", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string written = await FolderTreeManager.SaveAsync(SmartHistoryManager.BuildCsv(d), dialog.FileName);
                TxtProgress.Text = "Saved " + Path.GetFileName(written);
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "The file couldn't be saved: " + ex.Message;
            }
        }

        private void BtnClearDrive_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDrive.SelectedItem is not SmartDriveHistory d) return;
            var answer = System.Windows.MessageBox.Show(
                $"Forget all {d.Readings.Count:N0} readings of {d.Name}?\n\nThe drive itself isn't touched; only this history is removed. It starts again the next time you open the tool.",
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _file.Drives.Remove(d);
            try
            {
                SmartHistoryManager.Save(_file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtProgress.Text = "The change couldn't be saved: " + ex.Message;
            }
            FillDrives(null);
            Refresh();
        }

        /// <summary>Called when the window closes: stops a drive read in progress. Readings already saved stay.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
