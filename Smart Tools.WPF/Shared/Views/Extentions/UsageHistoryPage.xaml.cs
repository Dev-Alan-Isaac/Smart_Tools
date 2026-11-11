using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>How a history tool dresses the shared <see cref="UsageHistoryPage"/>.</summary>
    public sealed class UsageHistoryConfig
    {
        public required UsageRecorder Recorder { get; init; }

        /// <summary>The tool's measures, first = the default.</summary>
        public required IReadOnlyList<UsageMetric> Metrics { get; init; }

        /// <summary>Texts come from Str_&lt;Prefix&gt;_* keys (ChartHeader, ListHeader, ColProgram, EmptyTitle, WaitTitle, RecordHeader, Hour1...) and the tour from Str_Tour_&lt;Prefix&gt;_*.</summary>
        public required string Prefix { get; init; }

        /// <summary>Shows the "needs administrator" banner when not elevated.</summary>
        public bool NeedsAdmin { get; init; }

        /// <summary>The line under the Record button: (why it could not start or null, is it recording).</summary>
        public required Func<string?, bool, string> StateText { get; init; }
    }

    /// <summary>
    /// The page the per-program history tools share: record button, delete, a measure and a period on top, then the board. The refresh loop,
    /// the saved choices and the "recording goes on in the background" rules live here once; a tool only supplies a <see cref="UsageHistoryConfig"/>.
    /// </summary>
    public partial class UsageHistoryPage : UserControl
    {
        private static readonly int[] Hours = { 1, 3, 6, 24 };

        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(AppUsageStore.SampleSeconds) };
        private readonly Dictionary<RadioButton, UsageMetric> _metricButtons = new();
        private readonly Dictionary<RadioButton, int> _periodButtons = new();
        private UsageHistoryConfig? _config;
        private UsageSettings _settings = new();
        private bool _loaded, _applying;
        private string? _startError;
        private int _refreshId;

        public UsageHistoryPage()
        {
            InitializeComponent();
            _refresh.Tick += (_, _) => Refresh();
        }

        private UsageHistoryConfig Config => _config ?? throw new InvalidOperationException("UsageHistoryPage.Configure was not called.");

        private UsageRecorder Recorder => Config.Recorder;

        private string Title => Loc.T("Str_Tool_" + Recorder.ToolId + "_Name", Recorder.ToolId);

        private string Key(string name) => "Str_" + Config.Prefix + "_" + name;

        /// <summary>Sets the page up for one tool. Called once, right after the page is created.</summary>
        public void Configure(UsageHistoryConfig config)
        {
            _config = config;
            string p = config.Prefix;

            RecordHeader.SetResourceReference(ContentControl.ContentProperty, Key("RecordHeader"));
            DataHeader.SetResourceReference(ContentControl.ContentProperty, Key("DataHeader"));
            DataNote.SetResourceReference(TextBlock.TextProperty, Key("DataNote"));
            BtnClear.SetResourceReference(ContentControl.ContentProperty, Key("Clear"));
            Board.SetTexts(Key("ChartHeader"), Key("ListHeader"), Key("ColProgram"), Key("EmptyTitle"), Key("EmptyHint"));

            Tour.SetKey(RecordCard, "Str_Tour_" + p + "_Record"); Tour.SetOrder(RecordCard, 1);
            Tour.SetKey(MetricBar, "Str_Tour_" + p + "_Metric"); Tour.SetOrder(MetricBar, 2);
            Tour.SetKey(PeriodBar, "Str_Tour_" + p + "_Period"); Tour.SetOrder(PeriodBar, 3);
            Tour.SetKey(Board, "Str_Tour_" + p + "_Board"); Tour.SetOrder(Board, 4);
            Tour.SetKey(DataCard, "Str_Tour_" + p + "_Data"); Tour.SetOrder(DataCard, 5);

            if (config.NeedsAdmin)
            {
                AdminBanner.TitleKey = Key("AdminTitle");
                AdminBanner.MessageKey = Key("AdminWarning");
            }

            string group = Guid.NewGuid().ToString("N");
            for (int i = 0; i < Hours.Length; i++)
            {
                var button = NewToggle(Key(Hours[i] == 1 ? "Hour1" : "Hours" + Hours[i]), "UhPeriod" + group, i, Hours.Length);
                _periodButtons[button] = Hours[i];
                PeriodBar.Children.Add(button);
            }
            for (int i = 0; i < config.Metrics.Count; i++)
            {
                var button = NewToggle(config.Metrics[i].LabelKey, "UhMetric" + group, i, config.Metrics.Count);
                _metricButtons[button] = config.Metrics[i];
                MetricBar.Children.Add(button);
            }
        }

        private RadioButton NewToggle(string labelKey, string group, int index, int count)
        {
            var button = new RadioButton
            {
                GroupName = group,
                Margin = new Thickness(index == 0 ? 0 : 3, 0, index == count - 1 ? 0 : 3, 0),
                Style = (Style)FindResource("ModeToggleButtonStyle"),
            };
            button.SetResourceReference(ContentControl.ContentProperty, labelKey);
            button.Checked += Option_Changed;
            return button;
        }

        private UsageMetric CurrentMetric() => _metricButtons.FirstOrDefault(kv => kv.Key.IsChecked == true).Value ?? Config.Metrics[0];

        private int CurrentHours() => _periodButtons.Where(kv => kv.Key.IsChecked == true).Select(kv => kv.Value).DefaultIfEmpty(3).First();

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            _settings = Recorder.LoadSettings();
            _applying = true;
            foreach (var (button, hours) in _periodButtons) button.IsChecked = hours == _settings.PeriodHours;
            if (_periodButtons.Keys.All(b => b.IsChecked != true)) _periodButtons.First(kv => kv.Value == 3).Key.IsChecked = true;
            var chosen = _metricButtons.FirstOrDefault(kv => kv.Value.Name == _settings.Metric).Key ?? _metricButtons.Keys.First();
            chosen.IsChecked = true;
            _applying = false;
            _loaded = true;

            if (Config.NeedsAdmin) AdminBanner.ShowIfNotElevated();
            if (_settings.Recording) _startError = Recorder.Start();
            UpdateTexts();
            Refresh();
            _refresh.Start();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelActiveRun();

        /// <summary>Leaving the page: stops refreshing and saves. Recording itself goes on in the background.</summary>
        public void CancelActiveRun()
        {
            if (_config == null) return;
            _refresh.Stop();
            if (_loaded) SaveSettings();
            Task.Run(Recorder.Store.Save);   // the file can be several MB: never on the screen's thread
            _loaded = false;
        }

        private void SaveSettings()
        {
            _settings.PeriodHours = CurrentHours();
            _settings.Metric = CurrentMetric().Name;
            Recorder.SaveSettings(_settings);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_applying || !_loaded) return;
            UpdateTexts();
            Refresh();
            SaveSettings();
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (Recorder.IsRunning)
            {
                Recorder.Stop();
                _settings.Recording = false;
                _startError = null;
            }
            else
            {
                _startError = Recorder.Start();
                _settings.Recording = true;   // the wish: a tool that needs administrator starts by itself next time it can
                if (_startError == UsageRecorder.NotAdministrator && Config.NeedsAdmin) AdminBanner.ShowIfNotElevated();
            }
            SaveSettings();
            UpdateTexts();
            Refresh();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T(Key("ClearAsk"), "Delete all the recorded history?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Recorder.Store.Clear();
            Refresh();
        }

        /// <summary>One report with a section per measure: the top programs over the period as a chart, then the ranking. HTML and PDF carry the charts.</summary>
        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            string kind = (sender as System.Windows.Controls.MenuItem)?.Tag as string ?? "html";
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Title,
                Filter = Loc.T("Str_SystemMonitor_ReportFilterHtml", "Web page with charts (*.html)|*.html") + "|" + Loc.T("Str_SystemMonitor_ReportFilterPdf", "PDF with charts (*.pdf)|*.pdf"),
                FilterIndex = kind == "pdf" ? 2 : 1,
                FileName = Config.Prefix + "-" + stamp + "." + kind,
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = OutputFolderManager.UniquePath(dialog.FileName);
            var recorder = Recorder;
            var metrics = Config.Metrics;
            var period = TimeSpan.FromHours(CurrentHours());
            string prefix = Config.Prefix, title = Title;
            try
            {
                await recorder.EnsureLoadedAsync();
                bool pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                byte[] bytes = await Task.Run(() =>
                {
                    var report = BuildReport(recorder, metrics, period, prefix, title);
                    return pdf ? PerfReportBuilder.ToPdf(report) : new System.Text.UTF8Encoding(false).GetBytes(PerfReportBuilder.ToHtml(report));
                });
                AtomicFile.WriteAllBytes(path, bytes);
                if (MessageBox.Show(Loc.F("Str_MB_SavedToNN", "Saved to:\n{0}{1}\n\nOpen it now?", path, ""), title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                MessageBox.Show(Loc.T("Str_MB_CouldnTSaveThe2", "Couldn't save the report: ") + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static readonly string[] ReportColors = { PerfReportBuilder.Blue, PerfReportBuilder.Orange, PerfReportBuilder.Green, PerfReportBuilder.Red, "#8b5cf6", "#14b8a6", "#94a3b8" };

        private static PerfReportData BuildReport(UsageRecorder recorder, IReadOnlyList<UsageMetric> metrics, TimeSpan period, string prefix, string title)
        {
            var sections = new List<PerfSection>();
            foreach (var metric in metrics)
            {
                var data = UsageBoardBuilder.Build(recorder.Store, metric, period, prefix);
                var series = data.Stack.Series.Select(s => new PerfSeries(s.Name, s.Values, ReportColors[Math.Min(s.ColorIndex, ReportColors.Length - 1)])).ToList();
                var charts = new List<PerfChart>();
                if (series.Count > 0) charts.Add(new PerfChart(Loc.T(metric.LabelKey, metric.Name), series, metric.ChartFormat));
                var rows = data.Rows.Select(r => new PerfRow(r.Rank + ". " + r.Name, r.ValueText + "  |  " + r.PeakText)).ToList();
                var highlights = data.Highlights.Select(h => new PerfRow(h.Label, (h.Value + " " + h.Sub).Trim())).ToList();
                sections.Add(new PerfSection(Loc.T(metric.LabelKey, metric.Name), Loc.F("Str_UsageReport_Period", "Last {0} h", period.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)), highlights.Concat(rows).ToList(), charts));
            }
            return new PerfReportData(title, DateTime.Now, period.TotalSeconds / UsageMath.ChartPoints, Loc.T("Str_UsageReport_Note", "Chart: the programs that used the most, over the chosen period."), sections);
        }

        private void UpdateTexts()
        {
            bool on = Recorder.IsRunning;
            IconRecord.Kind = on ? PackIconKind.Stop : PackIconKind.Record;
            TxtRecord.Text = on ? Loc.T(Key("StopRecording"), "Stop recording") : Loc.T(Key("StartRecording"), "Start recording");
            TxtRecordState.Text = Config.StateText(_startError, on);
        }

        /// <summary>Reads the history and builds the ranking on a worker thread (a full day is a few MB), then shows it. A newer request makes an older one stand down.</summary>
        private async void Refresh()
        {
            int id = ++_refreshId;
            var recorder = Recorder;
            var metric = CurrentMetric();
            var period = TimeSpan.FromHours(CurrentHours());
            string prefix = Config.Prefix;
            UsageBoardData data;
            try
            {
                await recorder.EnsureLoadedAsync();
                data = await Task.Run(() => UsageBoardBuilder.Build(recorder.Store, metric, period, prefix));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn("History could not be read: " + ex.Message);
                return;
            }
            if (id != _refreshId || !_loaded) return;
            bool recording = recorder.IsRunning;
            Board.SetEmptyKeys(Key(recording ? "WaitTitle" : "EmptyTitle"), Key(recording ? "WaitHint" : "EmptyHint"));
            Board.SetData(data);
            BtnExport.IsEnabled = data.Rows.Count > 0;
            var first = recorder.Store.FirstUtc;
            TxtHistory.Text = first == null
                ? Loc.T(Key("NoHistory"), "No history yet.")
                : Loc.F(Key("HistorySince"), "History since {0:g}.", first.Value.ToLocalTime());
            UpdateTexts();
        }
    }
}
