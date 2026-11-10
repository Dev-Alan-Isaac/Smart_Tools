using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Resource Hogs History - UI only. Recording and ranking are <see cref="ResourceHogRecorder"/> / <see cref="ResourceHogsManager"/>'s.</summary>
    public partial class ResourceHogsView : UserControl
    {
        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
        private ResourceHogsSettings _settings = new();
        private bool _loaded, _applying;

        private static string Title => Loc.T("Str_Tool_resource-hogs_Name", "Resource Hogs History");

        public ResourceHogsView()
        {
            InitializeComponent();
            _refresh.Tick += (_, _) => Refresh();
            Board.SetTexts("Str_ResourceHogs_ChartHeader", "Str_ResourceHogs_ListHeader", "Str_ResourceHogs_ColProgram", "Str_ResourceHogs_EmptyTitle", "Str_ResourceHogs_EmptyHint");
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = JsonSettings.Load<ResourceHogsSettings>(ResourceHogRecorder.SettingsName);
            _applying = true;
            foreach (var r in new[] { Period1, Period3, Period6, Period24 }) r.IsChecked = (string)r.Tag == _settings.PeriodHours.ToString();
            if (new[] { Period1, Period3, Period6, Period24 }.All(r => r.IsChecked != true)) Period3.IsChecked = true;
            (_settings.Metric == HogMetric.Memory ? MetricMemory : _settings.Metric == HogMetric.Disk ? MetricDisk : MetricCpu).IsChecked = true;
            _applying = false;
            _loaded = true;

            if (_settings.Recording) ResourceHogRecorder.Instance.Start();
            UpdateTexts();
            Refresh();
            _refresh.Start();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelActiveRun();

        public void CancelActiveRun()
        {
            _refresh.Stop();
            if (_loaded) SaveSettings();
            Task.Run(ResourceHogRecorder.Instance.Store.Save);   // recording itself goes on in the background; the file is written off the screen thread
            _loaded = false;
        }

        private void SaveSettings()
        {
            _settings.PeriodHours = Period();
            _settings.Metric = Metric();
            JsonSettings.Save(ResourceHogRecorder.SettingsName, _settings);
        }

        private int Period() => new[] { Period1, Period3, Period6, Period24 }.FirstOrDefault(r => r.IsChecked == true) is { } r ? int.Parse((string)r.Tag) : 3;

        private HogMetric Metric() => MetricMemory.IsChecked == true ? HogMetric.Memory : MetricDisk.IsChecked == true ? HogMetric.Disk : HogMetric.Cpu;

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_applying || !_loaded) return;
            UpdateTexts();
            Refresh();
            SaveSettings();
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            var rec = ResourceHogRecorder.Instance;
            if (rec.IsRunning) { rec.Stop(); _settings.Recording = false; }
            else { rec.Start(); _settings.Recording = true; }
            SaveSettings();
            UpdateTexts();
            Refresh();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Str_ResourceHogs_ClearAsk", "Delete all the recorded history?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ResourceHogRecorder.Instance.Store.Clear();
            Refresh();
        }

        private void UpdateTexts()
        {
            bool on = ResourceHogRecorder.Instance.IsRunning;
            IconRecord.Kind = on ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.Record;
            TxtRecord.Text = on ? Loc.T("Str_ResourceHogs_StopRecording", "Stop recording") : Loc.T("Str_ResourceHogs_StartRecording", "Start recording");
            TxtRecordState.Text = on
                ? Loc.T("Str_ResourceHogs_RecordingOn", "Recording every 5 seconds while Smart Tools is open, even when you leave this page.")
                : Loc.T("Str_ResourceHogs_RecordingOff", "Not recording. Start it and come back in an hour to see which programs used the most.");
        }

        private int _refreshId;

        /// <summary>Reads the history and builds the ranking on a worker thread (a full day is a few MB), then shows it. A newer request makes an older one stand down.</summary>
        private async void Refresh()
        {
            int id = ++_refreshId;
            var rec = ResourceHogRecorder.Instance;
            var metric = Metric();
            var period = TimeSpan.FromHours(Period());
            UsageBoardData data;
            try
            {
                await rec.EnsureLoadedAsync();
                data = await Task.Run(() => ResourceHogsManager.Build(rec.Store, metric, period));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn("History could not be read: " + ex.Message);
                return;
            }
            if (id != _refreshId || !_loaded) return;
            bool recording = rec.IsRunning;
            Board.SetEmptyKeys(recording ? "Str_ResourceHogs_WaitTitle" : "Str_ResourceHogs_EmptyTitle", recording ? "Str_ResourceHogs_WaitHint" : "Str_ResourceHogs_EmptyHint");
            Board.SetData(data);
            var first = rec.Store.FirstUtc;
            TxtHistory.Text = first == null
                ? Loc.T("Str_ResourceHogs_NoHistory", "No history yet.")
                : Loc.F("Str_ResourceHogs_HistorySince", "History since {0:g}.", first.Value.ToLocalTime());
            UpdateTexts();
        }
    }
}
