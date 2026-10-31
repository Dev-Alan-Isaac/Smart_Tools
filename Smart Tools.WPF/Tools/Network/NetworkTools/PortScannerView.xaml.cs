using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Port Scanner - UI only. Scanning (and sending from a chosen adapter) is
    /// in NetworkToolsManager.Scan.cs; the console and the adapter picker are
    /// shared controls. Settings are saved field by field.
    /// </summary>
    public partial class PortScannerView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private CancellationTokenSource? _scanCts;
        private readonly ObservableCollection<PortResult> _openPorts = new();
        private SourceChoice _sentFrom = NetworkToolsManager.AutomaticSource;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;
        private readonly bool _constructed;

        public PortScannerView()
        {
            InitializeComponent();
            OpenPortsList.ItemsSource = _openPorts;
            SourcePicker.SelectionChanged += SourcePicker_SelectionChanged;
            _constructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a scan.</summary>
        public void CancelActiveRun() => _scanCts?.Cancel();

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                var s = NetworkToolsManager.LoadSettings();
                _applying = true;
                try
                {
                    TxtScanHost.Text = s.ScanHost;
                    TxtScanPorts.Text = s.ScanPorts;
                    foreach (var r in ScanSpeedChips.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == s.ScanTimeoutMs.ToString(CultureInfo.InvariantCulture);
                    SourcePicker.SavedId = s.ScanSourceId;
                }
                finally
                {
                    _applying = false;
                }
            }
            UpdatePortsHint();
            UpdateUi();
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            // A scan keeps running in the background; Window_Closing stops it.
        }

        private int TimeoutMs => ScanSpeedChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string t && int.TryParse(t, out int v) ? v : 800;

        private void Save(Action<NetworkSettings> change)
        {
            if (_applying || !_loadedOnce) return;
            try
            {
                NetworkToolsManager.UpdateSettings(change);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Info($"Port scanner settings not saved: {ex.Message}");
            }
        }

        private void SaveFields()
        {
            string host = TxtScanHost.Text.Trim(), ports = TxtScanPorts.Text.Trim();
            int timeout = TimeoutMs;
            Save(s =>
            {
                s.ScanHost = host;
                s.ScanPorts = ports;
                s.ScanTimeoutMs = timeout;
            });
        }

        private void Setting_LostFocus(object sender, RoutedEventArgs e) => SaveFields();

        private void SourcePicker_SelectionChanged(SourceChoice choice)
        {
            string id = SourcePicker.SavedId;
            Save(s => s.ScanSourceId = id);
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
            SaveFields();
            _ = ToggleAsync();
            e.Handled = true;
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e) => _ = ToggleAsync();

        private void PortPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            TxtScanPorts.Text = tag;
            SaveFields();
        }

        private void ScanSpeed_Click(object sender, RoutedEventArgs e)
        {
            UpdatePortsHint();
            SaveFields();
        }

        private void TxtScanPorts_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_constructed) UpdatePortsHint();
        }

        /// <summary>"1,024 ports · up to about 5 s" - so a full scan's length isn't a surprise.</summary>
        private void UpdatePortsHint()
        {
            var (ports, error) = NetworkToolsManager.ParsePorts(TxtScanPorts.Text);
            if (error != null)
            {
                TxtPortsHint.Text = error;
                return;
            }
            double worst = Math.Ceiling(ports.Count / (double)NetworkToolsManager.DefaultScanConcurrency) * TimeoutMs / 1000.0;
            TxtPortsHint.Text = $"{ports.Count:N0} port{(ports.Count == 1 ? "" : "s")} · up to about {(worst < 60 ? $"{Math.Max(1, worst):0} s" : $"{worst / 60:0.#} min")} (less when ports answer quickly)";
        }

        // =================================================================
        //  Scan
        // =================================================================

        private async Task ToggleAsync()
        {
            if (_scanCts != null)
            {
                _scanCts.Cancel();
                return;
            }
            string? host = NetworkToolsManager.CleanHost(TxtScanHost.Text);
            var (ports, error) = NetworkToolsManager.ParsePorts(TxtScanPorts.Text);
            if (host == null || error != null)
            {
                Results.Log(ConsoleKind.Warning, "SCAN", host == null ? "Enter a host name or IP address first." : error!);
                (host == null ? TxtScanHost : TxtScanPorts).Focus();
                return;
            }
            TxtScanHost.Text = host;
            SaveFields();

            int timeout = TimeoutMs;
            _sentFrom = SourcePicker.Selected;
            _openPorts.Clear();
            ProgressScan.Value = 0;
            TxtNoOpenPorts.Visibility = Visibility.Visible;
            string from = _sentFrom.IsAutomatic ? "" : " " + _sentFrom.FromText;
            TxtToolTitle.Text = $"Scanning {host}{from}";
            TxtToolDetail.Text = $"0 of {ports.Count:N0} ports checked";

            var cts = _scanCts = new CancellationTokenSource();
            SourcePicker.IsEnabled = false;
            UpdateUi();
            UiMotion.Pop(BtnAction, 0.96);
            Results.Log(ConsoleKind.Info, "SCAN", $"Scanning {host}{from}: {ports.Count:N0} port{(ports.Count == 1 ? "" : "s")}...");

            int lastChecked = 0;
            var clock = Stopwatch.StartNew();
            var progress = new Progress<PortScanProgress>(p =>
            {
                lastChecked = p.Checked;
                ProgressScan.Value = p.Total == 0 ? 0 : p.Checked * 100.0 / p.Total;
                TxtToolDetail.Text = $"{p.Checked:N0} of {p.Total:N0} ports checked · {p.Open} open";
            });

            void OnOpen(PortResult r) => Dispatcher.InvokeAsync(() =>
            {
                int i = 0;
                while (i < _openPorts.Count && _openPorts[i].Port < r.Port) i++;
                _openPorts.Insert(i, r);
                TxtNoOpenPorts.Visibility = Visibility.Collapsed;
                Results.Log(ConsoleKind.Success, "SCAN", $"Port {r.Label} is open ({r.LatencyMs} ms)");
            });

            try
            {
                var open = await NetworkToolsManager.ScanPortsAsync(host, ports, timeout, progress, OnOpen, cts.Token, source: _sentFrom.Address);
                bool cancelled = cts.IsCancellationRequested;
                string summary = NetworkToolsManager.DescribeScan(host, ports.Count, cancelled ? lastChecked : ports.Count, open, clock.Elapsed, cancelled);
                Results.Log(open.Count > 0 ? ConsoleKind.Success : ConsoleKind.Info, "SCAN", summary + (_sentFrom.IsAutomatic ? "" : $" · {_sentFrom.FromText}"));
                TxtToolTitle.Text = (cancelled ? $"Scan of {host} stopped" : $"Scanned {host}") + from;
                TxtToolDetail.Text = $"{(cancelled ? lastChecked : ports.Count):N0} of {ports.Count:N0} ports checked · {open.Count} open";
                if (!cancelled) ProgressScan.Value = 100;
            }
            catch (OperationCanceledException)
            {
                Results.Log(ConsoleKind.Info, "SCAN", "Stopped.");
            }
            catch (Exception ex)
            {
                Results.Log(ConsoleKind.Error, "SCAN", ex.Message);
                TxtToolTitle.Text = "Port scan";
                TxtToolDetail.Text = ex.Message;
            }
            finally
            {
                _scanCts = null;
                cts.Dispose();
                SourcePicker.IsEnabled = true;
                UpdateUi();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        private void UpdateUi()
        {
            bool running = _scanCts != null;
            IconAction.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.MagnifyScan;
            TxtActionLabel.Text = running ? "Stop" : "Scan";
            BtnAction.ToolTip = running ? "Stop the scan (keeps what was found)" : "Check which ports are open";

            var source = running ? _sentFrom : SourcePicker.Selected;
            TxtSource.Text = source.IsAutomatic ? "Automatic" : $"{source.Label} · {source.Address}";
            IconSource.Kind = source.IsAutomatic ? PackIconKind.AutoFix
                : source.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
                : source.Kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
                : PackIconKind.Ethernet;

            if (!running && ProgressScan.Value == 0)
            {
                TxtToolTitle.Text = "Port scan";
                TxtToolDetail.Text = "Finds open TCP ports - only scan machines you own or may test.";
            }
        }
    }
}
