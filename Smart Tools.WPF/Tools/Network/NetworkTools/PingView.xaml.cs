using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Ping - UI only. Pinging (and sending from a chosen adapter) is in
    /// <see cref="NetworkToolsManager"/>; the console and the adapter picker are
    /// shared controls. Settings are saved field by field, so the other network
    /// tools' settings in the same file are never overwritten.
    /// </summary>
    public partial class PingView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private CancellationTokenSource? _pingCts;
        private readonly List<PingSample> _samples = new();
        private PingStats _stats = new();
        private string _host = "";
        private SourceChoice _sentFrom = NetworkToolsManager.AutomaticSource;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;

        public PingView()
        {
            InitializeComponent();
            SourcePicker.SelectionChanged += SourcePicker_SelectionChanged;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops pinging.</summary>
        public void CancelActiveRun() => _pingCts?.Cancel();

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                var s = NetworkToolsManager.LoadSettings();
                _applying = true;
                try
                {
                    TxtPingHost.Text = s.PingHost;
                    foreach (var r in PingCountChips.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == s.PingCount.ToString(CultureInfo.InvariantCulture);
                    SourcePicker.SavedId = s.PingSourceId;
                }
                finally
                {
                    _applying = false;
                }
            }
            UpdateUi();
            UpdateStats();
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            // Keeps pinging in the background like the other tools' runs; Window_Closing stops it.
        }

        private int Count => PingCountChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string t && int.TryParse(t, out int v) ? v : 4;

        private void Save(Action<NetworkSettings> change)
        {
            if (_applying || !_loadedOnce) return;
            try
            {
                NetworkToolsManager.UpdateSettings(change);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Info($"Ping settings not saved: {ex.Message}");
            }
        }

        private void Setting_LostFocus(object sender, RoutedEventArgs e)
        {
            string host = TxtPingHost.Text.Trim();
            Save(s => s.PingHost = host);
        }

        private void PingCount_Click(object sender, RoutedEventArgs e)
        {
            int count = Count;
            Save(s => s.PingCount = count);
        }

        private void SourcePicker_SelectionChanged(SourceChoice choice)
        {
            string id = SourcePicker.SavedId;
            Save(s => s.PingSourceId = id);
            UpdateUi();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        private void HostBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Setting_LostFocus(sender, e);
            _ = ToggleAsync();
            e.Handled = true;
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e) => _ = ToggleAsync();

        // =================================================================
        //  Ping
        // =================================================================

        private async Task ToggleAsync()
        {
            if (_pingCts != null)
            {
                _pingCts.Cancel();
                return;
            }
            string? host = NetworkToolsManager.CleanHost(TxtPingHost.Text);
            if (host == null)
            {
                Results.Log(ConsoleKind.Warning, "PING", "Enter a host name or IP address first.");
                TxtPingHost.Focus();
                return;
            }
            TxtPingHost.Text = host;
            Save(s => s.PingHost = host);

            int count = Count;
            _host = host;
            _sentFrom = SourcePicker.Selected;
            _samples.Clear();
            _stats = new PingStats();
            UpdateStats();

            var cts = _pingCts = new CancellationTokenSource();
            SourcePicker.IsEnabled = false;   // the adapter can't change halfway through
            UpdateUi();
            UiMotion.Pop(BtnAction, 0.96);
            string from = _sentFrom.IsAutomatic ? "" : " " + _sentFrom.FromText;
            Results.Log(ConsoleKind.Info, "PING", count == 0 ? $"Pinging {host}{from} until stopped..." : $"Pinging {host}{from} {count} times...");

            var progress = new Progress<PingSample>(s =>
            {
                _samples.Add(s);
                _stats.Add(s);
                Results.Log(s.Success ? ConsoleKind.Received : ConsoleKind.Warning, "PING", NetworkToolsManager.DescribePing(s));
                UpdateStats();
                UpdateUi();
            });

            try
            {
                await NetworkToolsManager.PingAsync(host, count, progress, cts.Token, source: _sentFrom.Address);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                Results.Log(ConsoleKind.Error, "PING", ex.Message);
            }
            finally
            {
                _pingCts = null;
                cts.Dispose();
                SourcePicker.IsEnabled = true;
                if (_stats.Sent > 0)
                    Results.Log(_stats.Received == 0 ? ConsoleKind.Error : _stats.Lost > 0 ? ConsoleKind.Warning : ConsoleKind.Success, "PING",
                        NetworkToolsManager.DescribeStats(host, _stats) + (_sentFrom.IsAutomatic ? "" : $" · {_sentFrom.FromText}"));
                UpdateUi();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        /// <summary>Action button, title (which host, from which adapter) and the adapter pill.</summary>
        private void UpdateUi()
        {
            bool running = _pingCts != null;
            IconAction.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.PlayCircleOutline;
            TxtActionLabel.Text = running ? "Stop" : "Ping";
            BtnAction.ToolTip = running ? "Stop pinging" : "Start pinging the host";

            var source = running ? _sentFrom : SourcePicker.Selected;
            TxtSource.Text = source.IsAutomatic ? "Automatic" : $"{source.Label} · {source.Address}";
            IconSource.Kind = source.IsAutomatic ? PackIconKind.AutoFix
                : source.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
                : source.Kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
                : PackIconKind.Ethernet;

            if (_samples.Count == 0 && !running)
            {
                TxtToolTitle.Text = "Ping";
                TxtToolDetail.Text = "Checks a host answers, and how quickly - from the adapter you choose.";
            }
            else
            {
                TxtToolTitle.Text = $"Ping {_host}" + (_sentFrom.IsAutomatic ? "" : $" {_sentFrom.FromText}");
                TxtToolDetail.Text = running ? $"Pinging... {_stats.Sent} sent" : NetworkToolsManager.DescribeStats(_host, _stats);
            }
        }

        private void UpdateStats()
        {
            var st = _stats;
            TxtStatReplies.Text = st.Sent == 0 ? "–" : $"{st.Received} / {st.Sent}";
            TxtStatLoss.Text = st.Sent == 0 ? "–" : $"{st.LossPercent:0}%";
            TxtStatLoss.SetResourceReference(TextBlock.ForegroundProperty, st.Lost > 0 ? "StatusErrorFg" : "TextPrimary");
            TxtStatAvg.Text = NetworkText.Ms(st.Average);
            TxtStatMinMax.Text = st.Min is long mn ? $"{mn} / {st.Max}" : "–";
            TxtStatJitter.Text = NetworkText.Ms(st.Jitter);
            LatencyStrip.ItemsSource = NetworkToolsManager.LatencyBars(_samples);
            TxtLatencyEmpty.Visibility = _samples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
