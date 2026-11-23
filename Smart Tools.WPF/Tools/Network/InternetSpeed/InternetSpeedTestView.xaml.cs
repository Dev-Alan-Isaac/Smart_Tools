using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Internet Speed Test. UI only: the measurements live in
    /// Controllers/InternetSpeedTestManager.Engine.cs (SpeedTestEngine) and the
    /// maths, verdicts, history and settings in InternetSpeedTestManager.cs.
    /// Looks like Disk Speed Test: step pills in the run bar, tiles with an
    /// icon chip, big number and meter, a filled live chart, and history
    /// cards with bars.
    /// </summary>
    public partial class InternetSpeedTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowWidth = 1150;      // history moves under the results
        private const double OneColumnWidth = 760;    // tiles stack in one column
        private static string DialogTitle => Loc.T("Str_Tool_internet-speed_Name", "Internet Speed Test");

        // The chart's x axis: ping takes the first 10% of the progress bar.
        private const double ChartXStart = 10, ChartXSpan = 90;

        private bool _optionsCollapsed;
        private bool _viewConstructed;
        private bool _applyingSettings;
        private bool? _narrow;

        private readonly SpeedTestEngine _engine = new();
        private readonly ObservableCollection<SpeedTestResult> _history;
        private CancellationTokenSource? _cts;

        // Live chart points: x = overall progress (0-100), y = Mbps.
        private readonly List<Point> _downloadPoints = new();
        private readonly List<Point> _uploadPoints = new();
        private double _chartMax = 10;
        private bool _uploadPlanned = true;
        private SpeedPhase _activePhase = SpeedPhase.Idle;

        public InternetSpeedTestView()
        {
            InitializeComponent();

            _history = new ObservableCollection<SpeedTestResult>(InternetSpeedTestManager.LoadHistory());
            HistoryGrid.ItemsSource = _history;
            ServerList.ItemsSource = InternetSpeedTestManager.Servers;

            ApplySettings(InternetSpeedTestManager.LoadSettings());
            RefreshHistory();
            UpdateHint();
            SetPhase(SpeedPhase.Idle);

            _viewConstructed = true;
        }

        /// <summary>Called by MainWindow when the app closes.</summary>
        public void CancelActiveRun()
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private bool IsTesting => _cts != null;

        // ---------------- Layout ----------------

        /// <summary>The address the internet sees. Only asked when the user presses the button (it contacts api.ipify.org).</summary>
        private async void BtnLookUpIp_Click(object sender, RoutedEventArgs e)
        {
            BtnLookUpIp.IsEnabled = false;
            TxtMyPublicIp.Text = Loc.T("Str_MyIp_Looking", "Looking up...");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                TxtMyPublicIp.Text = await NetworkToolsManager.GetPublicIpAsync(cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or OperationCanceledException)
            {
                TxtMyPublicIp.Text = Loc.T("Str_MyIp_Unavailable", "Unavailable");
            }
            finally { BtnLookUpIp.IsEnabled = true; }
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtMyPublicIp.Text;
            if (text.Length == 0 || !char.IsDigit(text[0]) && !text.Contains(':')) return;   // nothing looked up yet
            try { System.Windows.Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
        }

        private void Root_Loaded(object sender, RoutedEventArgs e) => UpdateLayoutForWidth();

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutForWidth();

        /// <summary>Narrow windows: history drops under the results (like Disk Speed Test) and tiles stack.</summary>
        private void UpdateLayoutForWidth()
        {
            if (ActualWidth <= 0) return;
            TileGrid.Columns = ActualWidth < OneColumnWidth ? 1 : 2;

            bool narrow = ActualWidth < NarrowWidth;
            if (_narrow == narrow) return;
            _narrow = narrow;

            Grid.SetColumn(HistoryPanel, narrow ? 0 : 2);
            Grid.SetRow(HistoryPanel, narrow ? 1 : 0);
            HistoryPanel.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
            HistoryGap.Width = new GridLength(narrow ? 0 : 14);
            HistoryColumn.Width = new GridLength(narrow ? 0 : 330);
            HistoryRow.Height = new GridLength(narrow ? 260 : 0);
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);

            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
        }

        // ---------------- Options ----------------

        private RadioButton[] LengthChips => new[] { RadioLength5, RadioLength10, RadioLength15 };
        private RadioButton[] LimitChips => new[] { RadioLimitNone, RadioLimit100, RadioLimit250, RadioLimit1000 };

        private static int ChipValue(IEnumerable<RadioButton> chips, int fallback)
        {
            foreach (var chip in chips)
                if (chip.IsChecked == true && int.TryParse(chip.Tag?.ToString(), out int value)) return value;
            return fallback;
        }

        private static void CheckChip(IEnumerable<RadioButton> chips, int value)
        {
            var list = chips.ToList();
            var match = list.FirstOrDefault(c => c.Tag?.ToString() == value.ToString()) ?? list[0];
            match.IsChecked = true;
        }

        private SpeedTestServer SelectedServer => ServerList.SelectedItem as SpeedTestServer ?? InternetSpeedTestManager.Servers[0];

        private void ApplySettings(SpeedTestSettings settings)
        {
            _applyingSettings = true;
            try
            {
                ServerList.SelectedItem = InternetSpeedTestManager.ServerById(settings.ServerId);
                CheckChip(LengthChips, settings.DurationSeconds);
                CheckChip(LimitChips, settings.DataLimitMb);
                ChkMeasureUpload.IsChecked = settings.MeasureUpload;
            }
            finally { _applyingSettings = false; }
        }

        private void SaveSettings()
        {
            if (!_viewConstructed || _applyingSettings) return;
            InternetSpeedTestManager.SaveSettings(new SpeedTestSettings
            {
                ServerId = SelectedServer.Id,
                DurationSeconds = ChipValue(LengthChips, 10),
                DataLimitMb = ChipValue(LimitChips, 0),
                MeasureUpload = ChkMeasureUpload.IsChecked == true,
            });
        }

        private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Never leave the list empty (clicking the selected item with Ctrl clears it).
            if (ServerList.SelectedItem == null && InternetSpeedTestManager.Servers.Count > 0 && e.RemovedItems.Count > 0)
            {
                ServerList.SelectedItem = e.RemovedItems[0];
                return;
            }
            UpdateHint();
            SaveSettings();
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            UpdateHint();
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateHint();
            SaveSettings();
        }

        private void UpdateHint()
        {
            if (TxtTestHint == null) return;
            var server = SelectedServer;
            int seconds = ChipValue(LengthChips, 10);
            int limit = ChipValue(LimitChips, 0);
            bool upload = ChkMeasureUpload.IsChecked == true && server.CanUpload;

            ChkMeasureUpload.IsEnabled = server.CanUpload && !IsTesting;
            string parts = upload ? $"About {seconds * 2 + 3} seconds: ping, then {seconds} s download and {seconds} s upload."
                                  : $"About {seconds + 3} seconds: ping, then {seconds} s download.";
            if (!server.CanUpload) parts += $" {server.Name} only offers downloads - pick Cloudflare to measure upload.";
            parts += limit > 0 ? $" Stops early after {SpeedText.FormatBytes(limit * 1_000_000L)} each way."
                               : " Fast lines can use a lot of data - set a limit on mobile or metered connections.";
            TxtTestHint.Text = parts;

            // The upload step pill shows whether this run will measure upload.
            if (!IsTesting) ShowUploadStep(upload);
        }

        private void ShowUploadStep(bool planned)
        {
            var visibility = planned ? Visibility.Visible : Visibility.Collapsed;
            StepUpload.Visibility = visibility;
            ChevronUpload.Visibility = visibility;
        }

        // ---------------- Run / cancel ----------------

        private async void BtnStartTest_Click(object sender, RoutedEventArgs e)
        {
            if (IsTesting)
            {
                CancelActiveRun();
                BtnStartTest.IsEnabled = false;   // re-enabled when the run unwinds
                TxtStatus.Text = "Stopping…";
                return;
            }

            var options = new SpeedTestOptions
            {
                Server = SelectedServer,
                Duration = TimeSpan.FromSeconds(ChipValue(LengthChips, 10)),
                DataLimitBytes = ChipValue(LimitChips, 0) * 1_000_000L,
                MeasureUpload = ChkMeasureUpload.IsChecked == true,
            };

            var cts = new CancellationTokenSource();
            _cts = cts;
            SetRunning(true);
            ResetResults(options);

            var progress = new Progress<NetSpeedSample>(OnProgress);
            try
            {
                // A server that does not answer is not the end of the test: the next one is tried (at most three in all).
                SpeedTestResult? result = null;
                Exception? failure = null;
                var order = new List<SpeedTestServer> { options.Server };
                order.AddRange(InternetSpeedTestManager.Servers.Where(s => s.Id != options.Server.Id));
                for (int i = 0; i < Math.Min(3, order.Count) && result == null; i++)
                {
                    var server = order[i];
                    var attempt = new SpeedTestOptions
                    {
                        Server = server,
                        Duration = options.Duration,
                        DataLimitBytes = options.DataLimitBytes,
                        MeasureUpload = options.MeasureUpload,
                    };
                    try
                    {
                        result = await _engine.RunAsync(attempt, progress, cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && !cts.IsCancellationRequested)
                    {
                        failure = ex;
                        options = attempt;   // the error text names the server that failed last
                        Log.Warn($"Speed test: {server.Display} failed: {ex.Message}");
                        if (i + 1 < Math.Min(3, order.Count))
                        {
                            ResetResults(attempt);
                            TxtStatus.Text = Loc.F("Str_NetSpeed_TryingNext", "{0} didn't answer - trying {1}…", server.Name, order[i + 1].Name);
                        }
                    }
                }
                if (result == null) throw failure ?? new System.IO.IOException("No test server answered.");
                ShowResult(result);

                _history.Insert(0, result);
                while (_history.Count > InternetSpeedTestManager.HistoryLimit) _history.RemoveAt(_history.Count - 1);
                InternetSpeedTestManager.SaveHistory(_history);
                RefreshHistory();

                TxtStatus.Text = "Done · " + InternetSpeedTestManager.DescribeResult(result);
                try { ToastNotificationManager.ShowToast("Speed test complete", InternetSpeedTestManager.DescribeResult(result)); }
                catch { /* a toast is a nice-to-have */ }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                SetPhase(SpeedPhase.Idle);
                TxtStatus.Text = "Test stopped. Nothing was saved.";
                ProgressStage.Value = 0;
                TxtProgressPercent.Text = "";
            }
            catch (Exception ex)
            {
                SetPhase(SpeedPhase.Idle);
                TxtError.Text = InternetSpeedTestManager.PlainError(ex, options.Server);
                UiMotion.FadeVisibility(ErrorBanner, true);
                TxtStatus.Text = "The test couldn't finish.";
                ProgressStage.Value = 0;
                TxtProgressPercent.Text = "";
            }
            finally
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
                cts.Dispose();
                _activePhase = SpeedPhase.Idle;
                HighlightTile(null);
                SetRunning(false);
            }
        }

        private void SetRunning(bool running)
        {
            BtnStartTest.IsEnabled = true;
            IconStart.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.Speedometer;
            if (running)
            {
                TxtStartLabel.Text = Str("Str_NetSpeed_CancelButton", "Stop test");
                BtnStartTest.ToolTip = Str("Str_NetSpeed_CancelButtonTooltip", "Stop the test now");
            }
            else
            {
                TxtStartLabel.SetResourceReference(TextBlock.TextProperty, "Str_NetSpeed_StartButton");
                BtnStartTest.SetResourceReference(ToolTipProperty, "Str_NetSpeed_StartButtonTooltip");
            }
            UiMotion.Pop(BtnStartTest, 0.96);

            // Changing the server or length mid-run would not affect the run, so lock them.
            ServerList.IsEnabled = !running;
            foreach (var chip in LengthChips.Concat(LimitChips)) chip.IsEnabled = !running;
            ChkMeasureUpload.IsEnabled = !running && SelectedServer.CanUpload;
            BtnClearHistory.IsEnabled = !running && _history.Count > 0;
            if (!running) UpdateHint();
        }

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void ResetResults(SpeedTestOptions options)
        {
            _uploadPlanned = options.MeasureUpload && options.Server.CanUpload;
            ShowUploadStep(_uploadPlanned);

            ErrorBanner.Visibility = Visibility.Collapsed;
            TxtError.Text = "";
            ServerPill.Visibility = DataPill.Visibility = NoUploadPill.Visibility = Visibility.Collapsed;

            TxtDownloadValue.Text = "–";
            TxtDownloadUnit.Text = "Mbps";
            TxtDownloadDetail.Text = "Download speed";
            TxtUploadValue.Text = _uploadPlanned ? "–" : "n/a";
            TxtUploadUnit.Text = "Mbps";
            TxtUploadDetail.Text = _uploadPlanned ? "Upload speed" : UploadSkippedReason(options);
            DownloadMeter.Value = UploadMeter.Value = 0;
            TxtPingValue.Text = "–";
            TxtJitterValue.Text = "Round trip to the server";
            TxtLoadedValue.Text = "–";
            TxtLoadedDetail.Text = "While the line is busy";
            TxtChartUploadLegend.Visibility = _uploadPlanned ? Visibility.Visible : Visibility.Hidden;

            VerdictList.ItemsSource = null;
            TxtVerdictEmpty.Visibility = Visibility.Visible;
            ProgressStage.Value = 0;
            TxtProgressPercent.Text = "0%";

            _downloadPoints.Clear();
            _uploadPoints.Clear();
            _chartMax = 10;
            SetPhase(SpeedPhase.Latency);
            RedrawChart();
        }

        private static string UploadSkippedReason(SpeedTestOptions options) =>
            options.Server.CanUpload ? "Not measured - switched off" : $"Not measured - {options.Server.Name} is download-only";

        private void OnProgress(NetSpeedSample sample)
        {
            if (!IsTesting) return;   // a late report after cancel

            ProgressStage.Value = Math.Clamp(sample.Percent, 0, 100);
            TxtProgressPercent.Text = $"{Math.Clamp(sample.Percent, 0, 100):0}%";
            if (!string.IsNullOrEmpty(sample.Text)) TxtStatus.Text = sample.Text;

            if (sample.Phase != _activePhase && sample.Phase != SpeedPhase.Done)
            {
                _activePhase = sample.Phase;
                SetPhase(sample.Phase);
                HighlightTile(sample.Phase switch
                {
                    SpeedPhase.Latency => TilePing,
                    SpeedPhase.Download => TileDownload,
                    SpeedPhase.Upload => TileUpload,
                    _ => null,
                });
            }

            switch (sample.Phase)
            {
                case SpeedPhase.Download:
                    AddPoint(_downloadPoints, sample);
                    ShowLive(TxtDownloadValue, TxtDownloadUnit, DownloadMeter, TxtDownloadDetail, sample.Mbps, _downloadPoints);
                    break;
                case SpeedPhase.Upload:
                    AddPoint(_uploadPoints, sample);
                    ShowLive(TxtUploadValue, TxtUploadUnit, UploadMeter, TxtUploadDetail, sample.Mbps, _uploadPoints);
                    break;
            }
        }

        private void AddPoint(List<Point> points, NetSpeedSample sample)
        {
            if (sample.Mbps <= 0 && points.Count == 0) return;   // the zero at a phase start would draw a false dip
            points.Add(new Point(sample.Percent, sample.Mbps));
            double max = _downloadPoints.Concat(_uploadPoints).DefaultIfEmpty().Max(p => p.Y);
            _chartMax = NiceCeiling(Math.Max(max * 1.1, 1));
            RedrawChart();
        }

        private void ShowLive(TextBlock value, TextBlock unit, System.Windows.Controls.ProgressBar meter, TextBlock detail, double mbps, List<Point> points)
        {
            ShowSpeed(value, unit, mbps);
            meter.Value = Math.Min(100, mbps * 100 / _chartMax);
            double average = points.Count == 0 ? 0 : points.Average(p => p.Y);
            detail.Text = $"Now · average so far {SpeedText.FormatMbps(average)}";
        }

        private static void ShowSpeed(TextBlock value, TextBlock unit, double mbps)
        {
            value.Text = SpeedText.FormatMbpsNumber(mbps);
            unit.Text = SpeedText.Unit(mbps);
        }

        private void HighlightTile(Border? active)
        {
            foreach (var tile in new[] { TileDownload, TileUpload, TilePing, TileLoaded })
            {
                if (!ReferenceEquals(tile, active)) tile.BorderBrush = Brushes.Transparent;
                else tile.SetResourceReference(Border.BorderBrushProperty, tile == TileUpload ? "StatusInfoFg" : "AccentColor");
            }
        }

        // ---------------- Step pills (Ping → Download → Upload) ----------------

        private void SetPhase(SpeedPhase phase)
        {
            int at = phase switch
            {
                SpeedPhase.Latency => 1,
                SpeedPhase.Download => 2,
                SpeedPhase.Upload => 3,
                SpeedPhase.Done => 4,
                _ => 0,
            };
            StyleStep(StepPing, IconStepPing, TxtStepPing, PackIconKind.Pulse, at == 1, at > 1, "StatusSuccessBg", "StatusSuccessFg");
            StyleStep(StepDownload, IconStepDownload, TxtStepDownload, PackIconKind.ArrowDownBold, at == 2, at > 2, "AccentColorSubtle", "AccentColor");
            StyleStep(StepUpload, IconStepUpload, TxtStepUpload, PackIconKind.ArrowUpBold, at == 3, at > 3, "StatusInfoBg", "StatusInfoFg");

            if (at == 1) UiMotion.Pop(StepPing, 0.9);
            if (at == 2) UiMotion.Pop(StepDownload, 0.9);
            if (at == 3) UiMotion.Pop(StepUpload, 0.9);
        }

        private static void StyleStep(Border pill, PackIcon icon, TextBlock text, PackIconKind kind, bool active, bool done, string activeBg, string activeFg)
        {
            string bg = active ? activeBg : done ? "StatusSuccessBg" : "RowBg";
            string fg = active ? activeFg : done ? "StatusSuccessFg" : "TextSecondary";
            pill.SetResourceReference(Border.BackgroundProperty, bg);
            icon.SetResourceReference(ForegroundProperty, fg);
            text.SetResourceReference(TextBlock.ForegroundProperty, fg);
            icon.Kind = done ? PackIconKind.CheckBold : kind;   // tick when done
        }

        // ---------------- Result ----------------

        private void ShowResult(SpeedTestResult r)
        {
            SetPhase(SpeedPhase.Done);

            double top = Math.Max(r.DownloadMbps, r.UploadMbps);
            _chartMax = Math.Max(_chartMax, NiceCeiling(Math.Max(top * 1.1, 1)));

            ShowSpeed(TxtDownloadValue, TxtDownloadUnit, r.DownloadMbps);
            DownloadMeter.Value = Math.Min(100, r.DownloadMbps * 100 / _chartMax);
            TxtDownloadDetail.Text = PeakText("Steady speed", _downloadPoints);
            UiMotion.Pop(TxtDownloadValue, 0.85);

            if (r.UploadMbps >= 0)
            {
                ShowSpeed(TxtUploadValue, TxtUploadUnit, r.UploadMbps);
                UploadMeter.Value = Math.Min(100, r.UploadMbps * 100 / _chartMax);
                TxtUploadDetail.Text = PeakText("Steady speed", _uploadPoints);
                UiMotion.Pop(TxtUploadValue, 0.85);
            }
            else
            {
                TxtUploadValue.Text = "n/a";
                UploadMeter.Value = 0;
            }

            TxtPingValue.Text = r.PingMs < 0 ? "–" : $"{r.PingMs:0}";
            TxtJitterValue.Text = r.JitterMs < 0
                ? "Ping didn't answer"
                : $"Jitter {r.JitterDisplay}" + (string.IsNullOrEmpty(r.LatencyMethod) ? "" : $" · {r.LatencyMethod}");
            TxtLoadedValue.Text = r.LoadedPingMs < 0 ? "–" : $"{r.LoadedPingMs:0}";
            TxtLoadedDetail.Text = r.LoadedPingMs < 0 || r.PingMs < 0
                ? "While the line is busy"
                : $"+{Math.Max(0, r.LoadedPingMs - r.PingMs):0} ms while downloading";
            UiMotion.Pop(TxtPingValue, 0.85);
            UiMotion.Pop(TxtLoadedValue, 0.85);

            TxtServerPill.Text = r.ServerName;
            ServerPill.Visibility = string.IsNullOrEmpty(r.ServerName) ? Visibility.Collapsed : Visibility.Visible;
            TxtDataPill.Text = $"{r.DataDisplay} used";
            DataPill.Visibility = r.BytesUsed > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoUploadPill.Visibility = r.UploadMbps < 0 ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.Pop(ServerPill, 0.9);

            var verdicts = InternetSpeedTestManager.Explain(r);
            VerdictList.ItemsSource = verdicts;
            TxtVerdictEmpty.Visibility = verdicts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (verdicts.Count > 0) UiMotion.FadeIn(VerdictList, 6);

            ProgressStage.Value = 100;
            TxtProgressPercent.Text = "100%";
            RedrawChart();
        }

        private static string PeakText(string label, List<Point> points) =>
            points.Count == 0 ? label : $"{label} · peak {SpeedText.FormatMbps(points.Max(p => p.Y))}";

        // ---------------- Live chart (same look as Disk Speed Test) ----------------

        private void LiveChart_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawChart();

        private void RedrawChart()
        {
            LiveChart.Children.Clear();

            double w = ChartHost.ActualWidth, h = ChartHost.ActualHeight;
            bool hasData = _downloadPoints.Count + _uploadPoints.Count > 0;
            TxtChartEmpty.Visibility = hasData || IsTesting ? Visibility.Collapsed : Visibility.Visible;
            TxtChartMax.Text = hasData ? $"scale {SpeedText.FormatMbps(_chartMax)}" : "";
            if (w < 20 || h < 20) return;

            // Grid: quarter lines
            for (int i = 1; i <= 3; i++)
            {
                double y = h * i / 4;
                AddLine(0, y, w, y, dashed: true);
            }

            // The download/upload boundary is a fixed share of the run (see
            // InternetSpeedTestManager.DownloadEndPercent), known before the first
            // sample ever arrives - so it's drawn once, in its final spot, and never
            // has to jump later as if it had been a guess (it previously defaulted to
            // the chart's centre until the first upload sample landed, then jumped
            // to the real spot).
            if (_uploadPlanned)
            {
                double x = MapX(InternetSpeedTestManager.DownloadEndPercent(true), w);
                AddLine(x, 0, x, h, dashed: false);
            }

            if (!hasData) return;
            DrawSeries(_downloadPoints, "AccentColor", w, h);
            DrawSeries(_uploadPoints, "StatusInfoFg", w, h);
        }

        private static double MapX(double percent, double w) => Math.Clamp((percent - ChartXStart) / ChartXSpan, 0, 1) * w;

        private void AddLine(double x1, double y1, double x2, double y2, bool dashed)
        {
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = 1,
                Opacity = dashed ? 0.25 : 0.4,
                StrokeDashArray = dashed ? new DoubleCollection { 3, 4 } : null,
                SnapsToDevicePixels = true,
            };
            line.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
            LiveChart.Children.Add(line);
        }

        private void DrawSeries(List<Point> data, string brushKey, double w, double h)
        {
            if (data.Count == 0) return;

            Point Map(Point p) => new(MapX(p.X, w), h - Math.Min(1, p.Y / _chartMax) * (h - 2) - 1);

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

            LiveChart.Children.Add(area);
            LiveChart.Children.Add(line);

            // Live dot on the newest point.
            var last = Map(data[^1]);
            var dot = new Ellipse { Width = 8, Height = 8 };
            dot.SetResourceReference(Shape.FillProperty, brushKey);
            Canvas.SetLeft(dot, last.X - 4);
            Canvas.SetTop(dot, last.Y - 4);
            LiveChart.Children.Add(dot);
        }

        /// <summary>Rounds the chart's top up to 1, 2 or 5 × a power of ten so it doesn't jump on every sample.</summary>
        private static double NiceCeiling(double value)
        {
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double step in new[] { 1.0, 2.0, 5.0, 10.0 })
                if (step * magnitude >= value) return step * magnitude;
            return 10 * magnitude;
        }

        // ---------------- History ----------------

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) return;
            var answer = AppModal.Show(Loc.F("Str_MB_ClearAllSavedResults", "Clear all {0} saved results?", _history.Count), DialogTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _history.Clear();
            InternetSpeedTestManager.SaveHistory(_history);
            RefreshHistory();
        }

        private void RefreshHistory()
        {
            // Bars are relative to the fastest saved run; re-template the rows.
            InternetSpeedTestManager.ScaleHistoryBars(_history);
            HistoryGrid.Items.Refresh();

            bool empty = _history.Count == 0;
            if (empty != (HistoryEmptyPanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(HistoryEmptyPanel, empty);
            BtnClearHistory.IsEnabled = !IsTesting && !empty;
        }
    }
}
