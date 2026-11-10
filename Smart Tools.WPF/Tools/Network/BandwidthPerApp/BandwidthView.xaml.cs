using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Bandwidth per App - UI only. Recording and ranking are <see cref="BandwidthRecorder"/> / <see cref="BandwidthManager"/>'s.</summary>
    public partial class BandwidthView : UserControl
    {
        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
        private BandwidthSettings _settings = new();
        private bool _loaded, _applying;
        private string? _startError;

        private static string Title => Loc.T("Str_Tool_bandwidth-per-app_Name", "Bandwidth per App");

        public BandwidthView()
        {
            InitializeComponent();
            _refresh.Tick += (_, _) => Refresh();
            Board.SetTexts("Str_Bandwidth_ChartHeader", "Str_Bandwidth_ListHeader", "Str_Bandwidth_ColProgram", "Str_Bandwidth_EmptyTitle", "Str_Bandwidth_EmptyHint");
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = JsonSettings.Load<BandwidthSettings>(BandwidthRecorder.SettingsName);
            _applying = true;
            foreach (var r in new[] { Period1, Period3, Period6, Period24 }) r.IsChecked = (string)r.Tag == _settings.PeriodHours.ToString();
            if (new[] { Period1, Period3, Period6, Period24 }.All(r => r.IsChecked != true)) Period3.IsChecked = true;
            (_settings.Metric == BandwidthMetric.Download ? MetricDown : _settings.Metric == BandwidthMetric.Upload ? MetricUp : MetricTotal).IsChecked = true;
            _applying = false;
            _loaded = true;

            BandwidthRecorder.Instance.EnsureLoaded();
            AdminBanner.ShowIfNotElevated();
            if (_settings.Recording) _startError = BandwidthRecorder.Instance.Start();
            UpdateTexts();
            Refresh();
            _refresh.Start();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelActiveRun();

        public void CancelActiveRun()
        {
            _refresh.Stop();
            if (_loaded) SaveSettings();
            BandwidthRecorder.Instance.Store.Save();   // recording itself goes on in the background
            _loaded = false;
        }

        private void SaveSettings()
        {
            _settings.PeriodHours = Period();
            _settings.Metric = Metric();
            JsonSettings.Save(BandwidthRecorder.SettingsName, _settings);
        }

        private int Period() => new[] { Period1, Period3, Period6, Period24 }.FirstOrDefault(r => r.IsChecked == true) is { } r ? int.Parse((string)r.Tag) : 3;

        private BandwidthMetric Metric() => MetricDown.IsChecked == true ? BandwidthMetric.Download : MetricUp.IsChecked == true ? BandwidthMetric.Upload : BandwidthMetric.Total;

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_applying || !_loaded) return;
            UpdateTexts();
            Refresh();
            SaveSettings();
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            var rec = BandwidthRecorder.Instance;
            if (rec.IsRunning) { rec.Stop(); _settings.Recording = false; _startError = null; }
            else
            {
                _startError = rec.Start();
                _settings.Recording = true;   // the wish: it starts by itself next time it can (administrator)
                if (_startError == "NotAdministrator") AdminBanner.ShowIfNotElevated();
            }
            SaveSettings();
            UpdateTexts();
            Refresh();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Str_Bandwidth_ClearAsk", "Delete all the recorded history?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            BandwidthRecorder.Instance.Store.Clear();
            Refresh();
        }

        private void UpdateTexts()
        {
            bool on = BandwidthRecorder.Instance.IsRunning;
            IconRecord.Kind = on ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.Record;
            TxtRecord.Text = on ? Loc.T("Str_Bandwidth_StopRecording", "Stop recording") : Loc.T("Str_Bandwidth_StartRecording", "Start recording");
            TxtRecordState.Text = _startError == "NotAdministrator"
                ? Loc.T("Str_Bandwidth_NeedAdmin", "Windows only lets an administrator read network use per program. Restart Smart Tools as administrator.")
                : _startError != null
                    ? Loc.F("Str_Bandwidth_StartFailed", "Couldn't start: {0}", _startError)
                    : on
                        ? Loc.F("Str_Bandwidth_RecordingOn", "Recording while Smart Tools is open, even when you leave this page. {0:N0} transfers seen so far.", BandwidthRecorder.Instance.EventsSeen)
                        : Loc.T("Str_Bandwidth_RecordingOff", "Not recording. Start it and come back later to see which programs used the most data.");
        }

        private void Refresh()
        {
            var rec = BandwidthRecorder.Instance;
            var data = BandwidthManager.Build(rec.Store, Metric(), TimeSpan.FromHours(Period()));
            bool recording = rec.IsRunning;
            Board.SetEmptyKeys(recording ? "Str_Bandwidth_WaitTitle" : "Str_Bandwidth_EmptyTitle", recording ? "Str_Bandwidth_WaitHint" : "Str_Bandwidth_EmptyHint");
            Board.SetData(data);
            var first = rec.Store.FirstUtc;
            TxtHistory.Text = first == null
                ? Loc.T("Str_Bandwidth_NoHistory", "No history yet.")
                : Loc.F("Str_Bandwidth_HistorySince", "History since {0:g}.", first.Value.ToLocalTime());
            UpdateTexts();
        }
    }
}
