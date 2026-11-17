using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
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
    /// Port Scanner - UI only. What can be scanned (this PC, each adapter's network, another host) is built in
    /// PortScannerView.Targets.cs, the scan itself runs in PortScannerView.Run.cs; the sweep and the port tests are in
    /// NetworkToolsManager.Sweep.cs / .Scan.cs. Settings are saved field by field.
    /// </summary>
    public partial class PortScannerView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private CancellationTokenSource? _scanCts;
        private readonly ObservableCollection<HostPorts> _hosts = new();
        private SourceChoice _sentFrom = NetworkToolsManager.AutomaticSource;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;
        private readonly bool _constructed;

        public PortScannerView()
        {
            InitializeComponent();
            HostsList.ItemsSource = _hosts;
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
                    _savedTargetId = s.ScanTargetId;
                    _prefix = s.ScanPrefix;
                }
                finally
                {
                    _applying = false;
                }
                _ = RefreshTargetsAsync();
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
            UpdateRangeHint();
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
            TxtPortsHint.Text = Loc.F("Str_PortScanner_PortsHint", "{0} ports · up to about {1} per host (less when ports answer quickly)", ports.Count.ToString("N0"), FormatSeconds(worst));
        }

        private static string FormatSeconds(double seconds) =>
            seconds < 60 ? $"{Math.Max(1, seconds):0} s" : $"{seconds / 60:0.#} min";

        private void UpdateUi()
        {
            bool running = _scanCts != null;
            IconAction.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.MagnifyScan;
            TxtActionLabel.Text = running ? Loc.T("Str_PortScanner_Stop", "Stop") : Loc.T("Str_Network_ScanButton", "Scan");
            BtnAction.ToolTip = running
                ? Loc.T("Str_PortScanner_StopTip", "Stop the scan (keeps what was found)")
                : Loc.T("Str_PortScanner_ScanTip", "Check which ports are open");

            ShowSource(running);

            if (!running && ProgressScan.Value == 0)
            {
                TxtToolTitle.Text = Loc.T("Str_PortScanner_Idle", "Port scan");
                TxtToolDetail.Text = Loc.T("Str_PortScanner_IdleDetail", "Finds open TCP ports - only scan machines you own or may test.");
            }
        }

        /// <summary>The pill on the tool card: where the connections leave from.</summary>
        private void ShowSource(bool running)
        {
            var t = _target;
            if (t?.Kind == ScanTargetKind.ThisPc)
            {
                TxtSource.Text = Loc.T("Str_PortScanner_ThisPc", "This PC");
                IconSource.Kind = PackIconKind.Laptop;
                return;
            }
            var source = running ? _sentFrom : t?.Kind == ScanTargetKind.Network ? new SourceChoice(t.Id, t.Label, "", t.Address, t.Adapter) : SourcePicker.Selected;
            TxtSource.Text = source.IsAutomatic ? Loc.T("Str_NetworkAdapters_Automatic", "Automatic") : $"{source.Label} · {source.Address}";
            IconSource.Kind = AdapterIcon(source.IsAutomatic ? "" : source.Kind, source.IsAutomatic);
        }

        private static PackIconKind AdapterIcon(string kind, bool automatic) =>
            automatic ? PackIconKind.AutoFix
            : kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
            : kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
            : kind == "VPN" ? PackIconKind.Vpn
            : PackIconKind.Ethernet;
    }
}
