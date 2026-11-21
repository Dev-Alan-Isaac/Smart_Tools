using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Disk Speed Test view - UI only. Picking a writable folder, the
    /// unbuffered write/read passes, speed tiers and history live in
    /// <see cref="DiskSpeedTestManager"/>; data shapes in DiskSpeedTestModel.cs.
    ///
    /// Flow: pick a drive (or a folder) and a size -> Start. The tiles and
    /// the chart update live (write pass on the left half, read on the
    /// right); the finished run goes to the saved history.
    /// </summary>
    public partial class DiskSpeedTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowWidth = 1150;
        private static string DialogTitle => Loc.T("Str_Tool_disk-speed_Name", "Disk Speed Test");

        private readonly DiskSpeedTestManager _manager = new();
        private readonly ObservableCollection<DiskSpeedTestResult> _history = new();

        // Chart points: X = overall progress 0-100, Y = MB/s.
        private readonly List<Point> _writePoints = new();
        private readonly List<Point> _readPoints = new();
        private double _chartMax = 100;

        private List<DriveTarget> _drives = new();
        private string _targetFolder = "";
        private CancellationTokenSource? _cts;
        private SpeedTestPhase _phase = SpeedTestPhase.Idle;
        private int _checkVersion;
        private bool _isTesting;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool? _narrow;
        private readonly bool _viewConstructed;

        public DiskSpeedTestView()
        {
            InitializeComponent();
            HistoryList.ItemsSource = _history;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. A test file left by a hard exit is removed on the next run.</summary>
        public void CancelActiveRun() => _cts?.Cancel();

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            PopulateDrives();

            foreach (var r in DiskSpeedTestManager.LoadHistory()) _history.Add(r);
            RefreshHistory();

            UpdateTargetCheck();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Narrow windows: history drops under the results instead of beside them.
            bool narrow = ActualWidth < NarrowWidth;
            if (_narrow == narrow) return;
            _narrow = narrow;

            Grid.SetColumn(HistoryCard, narrow ? 0 : 2);
            Grid.SetRow(HistoryCard, narrow ? 1 : 0);
            HistoryCard.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
            HistoryGapColumn.Width = new GridLength(narrow ? 0 : 14);
            HistoryColumn.Width = new GridLength(narrow ? 0 : 330);
            HistoryRow.Height = new GridLength(narrow ? 260 : 0);
        }

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
            OptionsBottomPanel.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Target: drive or folder
        // =================================================================

        private void PopulateDrives()
        {
            _drives = DiskSpeedTestManager.GetDrives();   // system drive first
            CmbDrive.ItemsSource = _drives;
            CmbDrive.IsEnabled = _drives.Count > 0;
            if (_drives.Count > 0) CmbDrive.SelectedIndex = 0;
        }

        private void CmbDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (CmbDrive.SelectedItem is not DriveTarget drive) return;   // cleared after picking a folder

            SetTarget(drive.RootPath, animate: false);
        }

        /// <summary>
        /// The user chose, dropped or reset a folder on the Input card. A folder set by the
        /// drive picker is already in <c>_targetFolder</c>, so only a different path is a new choice.
        /// </summary>
        private void InputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (!_viewConstructed || string.Equals(InputCard.FolderPath, _targetFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (InputCard.FolderPath.Length > 0) SetFolder(InputCard.FolderPath);
            else
            {
                _targetFolder = "";
                UpdateTargetCheck();
            }
        }

        /// <summary>An explicitly chosen folder wins over the drive picker.</summary>
        private void SetFolder(string folder)
        {
            CmbDrive.SelectedIndex = -1;
            SetTarget(folder, animate: true);
        }

        private void SetTarget(string folder, bool animate)
        {
            _targetFolder = folder;
            InputCard.FolderPath = folder;
            if (animate) InputCard.PopDropArea();
            UpdateTargetCheck();
        }

        private long SelectedTestSize
        {
            get
            {
                var radio = new[] { RadioSize100, RadioSize500, RadioSize1G, RadioSize2G }.FirstOrDefault(r => r.IsChecked == true) ?? RadioSize1G;
                return long.TryParse(radio.Tag?.ToString(), out long v) ? v : 1_073_741_824;
            }
        }

        private void RadioTestSize_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateTargetCheck();
        }

        /// <summary>
        /// Live preflight in the sidebar: where the file will go, free space
        /// vs. needed, or why it can't run. Runs off the UI thread (it
        /// probes the disk, which can stall on a sleeping network share).
        /// </summary>
        private async void UpdateTargetCheck()
        {
            if (!_loadedOnce || _isTesting) return;

            int version = ++_checkVersion;
            string target = _targetFolder;
            long size = SelectedTestSize;

            if (string.IsNullOrWhiteSpace(target))
            {
                TargetCheckPanel.Visibility = Visibility.Collapsed;
                return;
            }

            SpeedTestPreflight check;
            try
            {
                check = await Task.Run(() => DiskSpeedTestManager.Prepare(target, size));
            }
            catch (Exception ex)
            {
                check = new SpeedTestPreflight { Error = ex.Message };
            }

            if (version != _checkVersion) return;   // superseded by a newer choice
            ShowTargetCheck(check);
        }

        private void ShowTargetCheck(SpeedTestPreflight check)
        {
            bool wasHidden = TargetCheckPanel.Visibility != Visibility.Visible;
            TargetCheckPanel.Visibility = Visibility.Visible;

            if (!check.CanRun)
            {
                IconTargetCheck.Kind = PackIconKind.AlertCircleOutline;
                IconTargetCheck.SetResourceReference(ForegroundProperty, "StatusErrorFg");
                TxtTargetCheck.Text = check.Error;
            }
            else
            {
                IconTargetCheck.Kind = check.Note != null ? PackIconKind.InformationOutline : PackIconKind.CheckCircleOutline;
                IconTargetCheck.SetResourceReference(ForegroundProperty, check.Note != null ? "StatusInfoFg" : "StatusSuccessFg");

                string space = check.FreeBytes >= 0
                    ? $"{DiskSizeText.Format(check.FreeBytes)} free · this test needs {DiskSizeText.Format(check.RequiredBytes)}"
                    : $"Free space unknown · this test needs {DiskSizeText.Format(check.RequiredBytes)}";
                TxtTargetCheck.Text = check.Note != null ? $"{check.Note}\n{space}" : space;
            }

            if (wasHidden) UiMotion.FadeIn(TargetCheckPanel, 4);
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnStartTest_Click(object sender, RoutedEventArgs e)
        {
            if (_isTesting)
            {
                _cts?.Cancel();
                return;
            }

            long size = SelectedTestSize;
            string target = _targetFolder;

            SpeedTestPreflight check;
            try
            {
                check = await Task.Run(() => DiskSpeedTestManager.Prepare(target, size));
            }
            catch (Exception ex)
            {
                check = new SpeedTestPreflight { Error = ex.Message };
            }

            ShowTargetCheck(check);
            if (!check.CanRun)
            {
                UiMotion.Pop(TargetCheckPanel, 0.95);
                AppModal.Show(check.Error ?? (TryFindResource("Str_DiskSpeed_NoTargetMessage") as string ?? "Choose a drive or folder to test."),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = new DiskSpeedTestOptions
            {
                TargetFolder = check.Folder!,
                TestFileSizeBytes = size,
                BypassCache = ChkBypassCache.IsChecked == true,
            };

            _cts = new CancellationTokenSource();
            SetTesting(true);

            // Progress<T> captures the UI SynchronizationContext here.
            var progress = new Progress<SpeedSample>(OnSample);

            try
            {
                var result = await _manager.RunAsync(options, progress, _cts.Token);
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                SetPhase(SpeedTestPhase.Cancelled);
                TxtStatus.Text = "Test cancelled - the test file was removed.";
            }
            catch (Exception ex)
            {
                SetPhase(SpeedTestPhase.Failed);
                TxtStatus.Text = $"Test failed: {ex.Message}";
                string format = TryFindResource("Str_DiskSpeed_ErrorMessageFormat") as string ?? "The test couldn't finish: {0}";
                AppModal.Show(string.Format(format, ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetTesting(false);
                UpdateTargetCheck();   // free space moved
            }
        }

        private void SetTesting(bool testing)
        {
            _isTesting = testing;

            IconStart.Kind = testing ? PackIconKind.StopCircleOutline : PackIconKind.SpeedometerMedium;
            TxtStartLabel.SetResourceReference(TextBlock.TextProperty, testing ? "Str_DiskSpeed_CancelButton" : "Str_DiskSpeed_StartButton");
            BtnStartTest.SetResourceReference(ToolTipProperty, testing ? "Str_DiskSpeed_CancelButtonTooltip" : "Str_DiskSpeed_StartButtonTooltip");
            UiMotion.Pop(BtnStartTest, 0.96);

            OptionsScrollViewer.IsEnabled = !testing;
            BtnClearHistory.IsEnabled = !testing && _history.Count > 0;

            if (testing)
            {
                _writePoints.Clear();
                _readPoints.Clear();
                _chartMax = 100;
                TxtWriteValue.Text = TxtReadValue.Text = "-";
                TxtWriteDetail.Text = "Sequential write";
                TxtReadDetail.Text = "Sequential read";
                WriteMeter.Value = ReadMeter.Value = 0;
                TierPill.Visibility = CachePill.Visibility = Visibility.Collapsed;
                ProgressStage.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtChartEmpty.Visibility = Visibility.Collapsed;
                SetPhase(SpeedTestPhase.Preparing);
                TxtStatus.Text = "Preparing test file...";
                RenderChart();
            }
            else
            {
                TxtProgressPercent.Text = "";
                TxtChartEmpty.Visibility = _writePoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnSample(SpeedSample s)
        {
            if (!_isTesting) return;
            if (s.Phase != _phase) SetPhase(s.Phase);

            ProgressStage.Value = s.OverallPercent;
            TxtProgressPercent.Text = $"{s.OverallPercent:0}%";

            string key = s.Phase == SpeedTestPhase.Writing ? "Str_DiskSpeed_StatusWriting" : "Str_DiskSpeed_StatusReading";
            string fallback = s.Phase == SpeedTestPhase.Writing ? "Writing test file..." : "Reading test file...";
            TxtStatus.Text = $"{TryFindResource(key) as string ?? fallback}  {DiskSizeText.Format(s.PhaseBytes)} of {DiskSizeText.Format(s.PhaseTotalBytes)}";

            if (s.PhaseBytes <= 0) return;   // the zero point at a phase start would draw a false dip

            var points = s.Phase == SpeedTestPhase.Writing ? _writePoints : _readPoints;
            points.Add(new Point(s.OverallPercent, s.CurrentMBps));

            double max = Math.Max(_writePoints.DefaultIfEmpty().Max(p => p.Y), _readPoints.DefaultIfEmpty().Max(p => p.Y));
            _chartMax = DiskSpeedTestManager.NiceCeiling(max * 1.1);

            // Tiles show the live speed; the average fills in below.
            var (value, meter, detail) = s.Phase == SpeedTestPhase.Writing
                ? (TxtWriteValue, WriteMeter, TxtWriteDetail)
                : (TxtReadValue, ReadMeter, TxtReadDetail);
            value.Text = DiskSizeText.SpeedNumber(s.CurrentMBps);
            meter.Value = Math.Min(100, s.CurrentMBps * 100 / _chartMax);
            detail.Text = $"Now · average so far {DiskSizeText.Speed(s.AverageMBps)}";

            RenderChart();
        }

        private void SetPhase(SpeedTestPhase phase)
        {
            _phase = phase;

            bool writing = phase == SpeedTestPhase.Writing;
            bool reading = phase == SpeedTestPhase.Reading;
            bool writeDone = reading || phase == SpeedTestPhase.Complete;
            bool readDone = phase == SpeedTestPhase.Complete;

            StyleStep(StepWrite, IconStepWrite, TxtStepWrite, PackIconKind.ArrowUpBold, writing, writeDone, "AccentColorSubtle", "AccentColor");
            StyleStep(StepRead, IconStepRead, TxtStepRead, PackIconKind.ArrowDownBold, reading, readDone, "StatusInfoBg", "StatusInfoFg");

            // The tile being measured gets an accent outline.
            if (writing) WriteTile.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            else WriteTile.BorderBrush = Brushes.Transparent;
            if (reading) ReadTile.SetResourceReference(Border.BorderBrushProperty, "StatusInfoFg");
            else ReadTile.BorderBrush = Brushes.Transparent;

            if (writing) UiMotion.Pop(StepWrite, 0.9);
            if (reading) UiMotion.Pop(StepRead, 0.9);
        }

        private static void StyleStep(Border pill, PackIcon icon, TextBlock text, PackIconKind arrow, bool active, bool done, string activeBg, string activeFg)
        {
            if (active)
            {
                pill.SetResourceReference(Border.BackgroundProperty, activeBg);
                icon.SetResourceReference(ForegroundProperty, activeFg);
                text.SetResourceReference(TextBlock.ForegroundProperty, activeFg);
            }
            else if (done)
            {
                pill.SetResourceReference(Border.BackgroundProperty, "StatusSuccessBg");
                icon.SetResourceReference(ForegroundProperty, "StatusSuccessFg");
                text.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
            }
            else
            {
                pill.SetResourceReference(Border.BackgroundProperty, "RowBg");
                icon.SetResourceReference(ForegroundProperty, "TextSecondary");
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            }

            icon.Kind = done ? PackIconKind.CheckBold : arrow;   // tick when done
        }

        private void ShowResult(DiskSpeedTestResult result)
        {
            SetPhase(SpeedTestPhase.Complete);
            ProgressStage.Value = 100;

            double scale = DiskSpeedTestManager.NiceCeiling(Math.Max(result.WriteMBps, result.ReadMBps) * 1.1);
            _chartMax = Math.Max(_chartMax, scale);

            TxtWriteValue.Text = DiskSizeText.SpeedNumber(result.WriteMBps);
            TxtReadValue.Text = DiskSizeText.SpeedNumber(result.ReadMBps);
            WriteMeter.Value = result.WriteMBps * 100 / _chartMax;
            ReadMeter.Value = result.ReadMBps * 100 / _chartMax;
            TxtWriteDetail.Text = $"Average · peak {DiskSizeText.Speed(result.WritePeakMBps)}";
            TxtReadDetail.Text = $"Average · peak {DiskSizeText.Speed(result.ReadPeakMBps)}";
            UiMotion.Pop(TxtWriteValue, 0.85);
            UiMotion.Pop(TxtReadValue, 0.85);

            TxtTier.Text = result.TierLabel;
            TierPill.Visibility = Visibility.Visible;
            UiMotion.Pop(TierPill, 0.9);
            CachePill.Visibility = result.CacheBypassed ? Visibility.Collapsed : Visibility.Visible;

            string done = TryFindResource("Str_DiskSpeed_StatusComplete") as string ?? "Test complete.";
            TxtStatus.Text = $"{done}  {result.SizeDisplay} in {result.TargetDisplay}";
            RenderChart();

            _history.Insert(0, result);
            while (_history.Count > DiskSpeedTestManager.MaxHistory) _history.RemoveAt(_history.Count - 1);
            RefreshHistory();
            SaveHistory();

            try
            {
                ToastNotificationManager.ShowToast("Disk Speed Test Complete",
                    $"Write {result.WriteDisplay} · Read {result.ReadDisplay} ({result.TierLabel})");
            }
            catch (Exception ex)
            {
                Log.Info($"Toast failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Live chart
        // =================================================================

        private void ChartHost_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

        private void RenderChart()
        {
            SpeedChart.Children.Clear();

            double w = ChartHost.ActualWidth, h = ChartHost.ActualHeight;
            if (w < 20 || h < 20) return;

            bool hasData = _writePoints.Count > 0 || _readPoints.Count > 0;
            TxtChartMax.Text = hasData ? $"scale {DiskSizeText.Speed(_chartMax)}" : "";

            // Grid: quarter lines + the write/read divider.
            for (int i = 1; i <= 3; i++)
            {
                double y = h * i / 4;
                AddLine(0, y, w, y, dashed: true);
            }
            AddLine(w / 2, 0, w / 2, h, dashed: false);

            if (!hasData) return;

            DrawSeries(_writePoints, "AccentColor", w, h);
            DrawSeries(_readPoints, "StatusInfoFg", w, h);
        }

        private void AddLine(double x1, double y1, double x2, double y2, bool dashed)
        {
            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                StrokeThickness = 1,
                Opacity = dashed ? 0.25 : 0.4,
                StrokeDashArray = dashed ? new DoubleCollection { 3, 4 } : null,
                SnapsToDevicePixels = true,
            };
            line.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
            SpeedChart.Children.Add(line);
        }

        private void DrawSeries(List<Point> data, string brushKey, double w, double h)
        {
            if (data.Count == 0) return;

            Point Map(Point p) => new(p.X / 100 * w, h - Math.Min(1, p.Y / _chartMax) * (h - 2) - 1);

            var line = new Polyline { StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
            var area = new Polygon { Opacity = 0.16 };
            line.SetResourceReference(Shape.StrokeProperty, brushKey);
            area.SetResourceReference(Shape.FillProperty, brushKey);

            var first = Map(data[0]);
            area.Points.Add(new Point(first.X, h));
            foreach (var p in data)
            {
                var m = Map(p);
                line.Points.Add(m);
                area.Points.Add(m);
            }
            area.Points.Add(new Point(Map(data[^1]).X, h));

            SpeedChart.Children.Add(area);
            SpeedChart.Children.Add(line);

            // Live dot on the newest point.
            var last = Map(data[^1]);
            var dot = new Ellipse { Width = 8, Height = 8 };
            dot.SetResourceReference(Shape.FillProperty, brushKey);
            Canvas.SetLeft(dot, last.X - 4);
            Canvas.SetTop(dot, last.Y - 4);
            SpeedChart.Children.Add(dot);
        }

        // =================================================================
        //  History (saved between sessions)
        // =================================================================

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) return;
            var answer = AppModal.Show(Loc.F("Str_MB_ClearAllSavedResults", "Clear all {0} saved results?", _history.Count), DialogTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _history.Clear();
            RefreshHistory();
            SaveHistory();
        }

        private void RefreshHistory()
        {
            // Bars are relative to the fastest saved run; the model isn't
            // observable, so re-template the rows.
            DiskSpeedTestManager.ScaleHistoryBars(_history);
            HistoryList.Items.Refresh();

            UiMotion.FadeVisibility(HistoryEmptyPanel, _history.Count == 0);
            BtnClearHistory.IsEnabled = !_isTesting && _history.Count > 0;
        }

        private void SaveHistory()
        {
            try
            {
                DiskSpeedTestManager.SaveHistory(_history);
            }
            catch (Exception ex)
            {
                // Not worth interrupting the user over.
                Log.Info($"Disk speed history not saved: {ex.Message}");
            }
        }
    }
}
