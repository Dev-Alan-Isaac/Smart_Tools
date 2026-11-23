using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RadioButton = System.Windows.Controls.RadioButton;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Network Device Scanner view - UI only. Network detection, the ping
    /// sweep, ARP and name lookups live in <see cref="DeviceScannerManager"/>;
    /// data shapes in DeviceScannerModel.cs.
    ///
    /// Devices appear in the grid the moment they answer (sorted by IP);
    /// names and MACs fill in as they arrive. Cancelling keeps what was found.
    /// </summary>
    public partial class DeviceScannerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_device-scanner_Name", "Network Device Scanner");

        private readonly DeviceScannerManager _scanner = new();
        private readonly ObservableCollection<DeviceScanResult> _results = new();
        private readonly ICollectionView _resultsView;

        private readonly string _networkGroup = "Network" + Guid.NewGuid().ToString("N")[..8];
        private List<LocalNetwork> _networks = new();
        private CancellationTokenSource? _scanCts;
        private bool _isScanning;
        private bool _optionsCollapsed;
        private readonly bool _viewConstructed;

        public DeviceScannerView()
        {
            InitializeComponent();

            _ = Task.Run(() => OuiLookupManager.Shared.IsAvailable);   // load the manufacturer list off the UI thread
            GridDevices.ItemsSource = _results;
            _resultsView = CollectionViewSource.GetDefaultView(_results);
            _resultsView.Filter = FilterDevice;

            _scanner.DeviceFound += Scanner_DeviceFound;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun() => _scanCts?.Cancel();

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_networks.Count == 0) LoadNetworks();
            UpdateEmptyState();
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
        //  Networks + range
        // =================================================================

        private void LoadNetworks()
        {
            string? keep = SelectedNetwork?.LocalAddress;
            _networks = DeviceScannerManager.GetLocalNetworks();
            SelectedNetwork = _networks.FirstOrDefault(n => n.LocalAddress == keep) ?? _networks.FirstOrDefault();

            NetworkChoicesPanel.Children.Clear();
            foreach (var n in _networks)
                NetworkChoicesPanel.Children.Add(MakeNetworkRadio(n, n == SelectedNetwork));

            if (_networks.Count > 0)
            {
                NetworkChoicesPanel.IsEnabled = ChkSubnetOverride.IsChecked != true;
                ShowNetworkInfo(SelectedNetwork!);
            }
            else
            {
                NetworkChoicesPanel.IsEnabled = false;
                TxtNetworkInfo.Text = "No active network found. Connect to Wi-Fi or Ethernet, or scan a custom range below.";
                ChkSubnetOverride.IsChecked = true;
            }
        }

        private RadioButton MakeNetworkRadio(LocalNetwork n, bool isChecked)
        {
            var icon = new PackIcon
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Kind = n.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
                     : n.Kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
                     : n.Kind == "VPN" ? PackIconKind.Vpn
                     : PackIconKind.Ethernet,
            };
            string detail = $"{n.LocalAddress} · {n.Range.Label}";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = n.InterfaceName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            content.Children.Add(icon);
            content.Children.Add(text);

            var radio = new RadioButton
            {
                Content = content,
                GroupName = _networkGroup,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                ToolTip = detail,
                Tag = n,
            };
            radio.SetResourceReference(StyleProperty, "ModeToggleTallStyle");
            radio.Checked += NetworkRadio_Checked;
            return radio;
        }

        private void NetworkRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: LocalNetwork n }) return;
            SelectedNetwork = n;
            ShowNetworkInfo(n);
        }

        private void BtnRefreshNetworks_Click(object sender, RoutedEventArgs e)
        {
            LoadNetworks();
            UiMotion.Pop(NetworkChoicesPanel, 0.95);
        }

        private LocalNetwork? SelectedNetwork { get; set; }

        private void ShowNetworkInfo(LocalNetwork net)
        {

            string info = $"This PC {net.LocalAddress}" +
                          (net.GatewayAddress != null ? $" · router {net.GatewayAddress}" : "") +
                          $" · {net.Range.Count:N0} addresses";
            if (net.WasNarrowed)
                info += $"\nThe network is /{net.PrefixLength} (too large to sweep), so the /24 around this PC is scanned. Use a custom range for more.";
            TxtNetworkInfo.Text = info;
        }

        private void ChkSubnetOverride_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool custom = ChkSubnetOverride.IsChecked == true;
            SubnetBar.IsEnabled = custom;
            NetworkChoicesPanel.IsEnabled = !custom && _networks.Count > 0;
            UiMotion.FadeVisibility(TxtRangeInfo, custom);
            ValidateRange();
            if (custom) TxtSubnetOverride.Focus();
        }

        private void TxtSubnetOverride_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ValidateRange();
        }

        /// <summary>Live feedback under the custom-range box; returns the range when valid.</summary>
        private ScanRange? ValidateRange()
        {
            if (ChkSubnetOverride.IsChecked != true)
            {
                SubnetBar.ClearValue(Border.BorderBrushProperty);
                return SelectedNetwork?.Range;
            }

            if (DeviceScannerManager.TryParseRange(TxtSubnetOverride.Text, out var range, out string error))
            {
                SubnetBar.ClearValue(Border.BorderBrushProperty);
                TxtRangeInfo.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
                TxtRangeInfo.Text = range.Count == 1 ? "1 address" : $"{range.Count:N0} addresses · {range.Label}";
                return range;
            }

            SubnetBar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            TxtRangeInfo.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
            TxtRangeInfo.Text = error;
            return null;
        }

        // =================================================================
        //  Speed
        // =================================================================

        private void BtnSpeedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            string[] parts = tag.Split(',');
            if (parts.Length != 2) return;

            TxtTimeout.Text = parts[0];
            TxtConcurrency.Text = parts[1];
            UiMotion.Pop((FrameworkElement)sender);
        }

        private void ScanField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ValidateSpeed(out _, out _);
        }

        private bool ValidateSpeed(out int timeout, out int concurrency)
        {
            bool timeoutOk = int.TryParse(TxtTimeout.Text.Trim(), out timeout) && timeout is >= 50 and <= DeviceScannerManager.MaxTimeoutMs;
            bool concurrencyOk = int.TryParse(TxtConcurrency.Text.Trim(), out concurrency) && concurrency is >= 1 and <= DeviceScannerManager.MaxConcurrency;

            SetBarError(TimeoutBar, !timeoutOk);
            SetBarError(ConcurrencyBar, !concurrencyOk);

            string? error = !timeoutOk ? $"Timeout: 50 to {DeviceScannerManager.MaxTimeoutMs} ms."
                          : !concurrencyOk ? $"At once: 1 to {DeviceScannerManager.MaxConcurrency}."
                          : null;
            TxtSpeedError.Text = error ?? "";
            TxtSpeedError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            return error == null;
        }

        private static void SetBarError(Border bar, bool error)
        {
            if (error) bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            else bar.ClearValue(Border.BorderBrushProperty);
        }

        // =================================================================
        //  Scan / cancel
        // =================================================================

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _scanCts?.Cancel();
                TxtScanProgress.Text = "Stopping…";
                ScanButton.IsEnabled = false; // re-enabled when the scan winds down
                return;
            }

            if (ValidateRange() is not ScanRange range)
            {
                AppModal.Show(_networks.Count == 0 && ChkSubnetOverride.IsChecked != true
                        ? "No network to scan - connect to a network or enter a custom range."
                        : TxtRangeInfo.Text, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ValidateSpeed(out int timeout, out int concurrency))
            {
                AppModal.Show(TxtSpeedError.Text, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The selected adapter tells us which rows are "This PC" and the router,
            // also for custom ranges on the same network.
            var net = SelectedNetwork ?? _networks.FirstOrDefault();
            var options = new DeviceScanOptions
            {
                Range = range,
                TimeoutMs = timeout,
                MaxConcurrency = concurrency,
                ResolveHostNames = ChkResolveNames.IsChecked == true,
                IncludeArpOnly = ChkIncludeArp.IsChecked == true,
                LocalAddress = net?.LocalAddress,
                LocalMacAddress = net?.MacAddress,
                GatewayAddress = net?.GatewayAddress,
                LocalPrefixLength = net?.PrefixLength ?? 0,
            };

            _results.Clear();
            SetScanning(true, range);

            _scanCts = new CancellationTokenSource();
            var progress = new Progress<DeviceScanProgress>(ReportProgress);

            try
            {
                var summary = await _scanner.ScanAsync(options, progress, _scanCts.Token);
                ShowSummary(summary);
            }
            catch (Exception ex)
            {
                // The manager handles per-address failures itself; anything here is unexpected.
                TxtScanProgress.Text = "Scan failed";
                AppModal.Show(Loc.F("Str_MB_TheScanStoppedUnexpectedly", "The scan stopped unexpectedly: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _scanCts.Dispose();
                _scanCts = null;
                SetScanning(false, range);
            }
        }

        private void SetScanning(bool scanning, ScanRange range)
        {
            _isScanning = scanning;
            ScanButton.IsEnabled = true;

            IconScan.Kind = scanning ? PackIconKind.StopCircleOutline : PackIconKind.Radar;
            if (scanning)
                TxtScanLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else
                TxtScanLabel.SetResourceReference(TextBlock.TextProperty, "Str_DeviceScan_ScanButton");

            NetworkChoicesPanel.IsEnabled = !scanning && ChkSubnetOverride.IsChecked != true && _networks.Count > 0;
            ChkSubnetOverride.IsEnabled = !scanning;
            SubnetBar.IsEnabled = !scanning && ChkSubnetOverride.IsChecked == true;
            BtnRefreshNetworks.IsEnabled = !scanning;
            BtnExport.IsEnabled =!scanning && _results.Count > 0;

            if (scanning)
            {
                ScanProgressBar.Value = 0;
                TxtScanProgressPercent.Text = "0%";
                TxtScanProgress.Text = $"Scanning {range.Label}…";
                SummaryPill.Visibility = NoPingPill.Visibility = Visibility.Collapsed;
            }

            UpdateEmptyState();
        }

        private void ReportProgress(DeviceScanProgress p)
        {
            double percent = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
            ScanProgressBar.Value = percent;
            TxtScanProgressPercent.Text = $"{percent:0}%";

            string devices = p.Found == 1 ? "1 device" : $"{p.Found:N0} devices";
            TxtScanProgress.Text = p.Phase switch
            {
                DeviceScanPhase.Pinging => $"Checked {p.Done:N0} of {p.Total:N0} addresses · {devices} so far",
                DeviceScanPhase.ReadingArp => $"Reading the ARP table for MAC addresses · {devices}",
                DeviceScanPhase.ResolvingNames => $"Looking up device names · {devices}",
                _ => TxtScanProgress.Text,
            };
        }

        /// <summary>A device answered: insert it in IP order and bump the count.</summary>
        private void Scanner_DeviceFound(object? sender, DeviceScanResult device)
        {
            int index = 0;
            while (index < _results.Count && _results[index].IpSortKey < device.IpSortKey) index++;
            _results.Insert(index, device);

            TxtSummaryCount.Text = _results.Count == 1 ? "1 device" : $"{_results.Count:N0} devices";
            if (SummaryPill.Visibility != Visibility.Visible)
            {
                SummaryPill.Visibility = Visibility.Visible;
                UpdateEmptyState();
            }
            UiMotion.Pop(SummaryPill, 0.9);
        }

        private void ShowSummary(DeviceScanSummary summary)
        {
            int count = summary.Devices.Count;
            int noPing = count - summary.Responded;
            string devices = count == 1 ? "1 device" : $"{count:N0} devices";
            string seconds = $"{summary.Elapsed.TotalSeconds:0.0} s";

            ScanProgressBar.Value = summary.Cancelled ? ScanProgressBar.Value : 100;
            TxtScanProgressPercent.Text = summary.Cancelled ? TxtScanProgressPercent.Text : "100%";
            TxtScanProgress.Text = summary.Cancelled
                ? $"Stopped - {devices} found before cancelling"
                : $"Done in {seconds} - {devices} on {summary.Range.Label}";

            TxtSummaryCount.Text = devices;
            SummaryPill.Visibility = Visibility.Visible;

            TxtNoPingCount.Text = noPing == 1 ? "1 ignores ping" : $"{noPing:N0} ignore ping";
            NoPingPill.Visibility = noPing > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateEmptyState();

            if (!summary.Cancelled)
            {
                try
                {
                    ToastNotificationManager.ShowToast("Network Scan Complete", $"Found {devices} on {summary.Range.Label}.");
                }
                catch (Exception)
                {
                    // notifications unavailable - the result is on screen anyway
                }
            }
        }

        private void UpdateEmptyState()
        {
            bool empty = _results.Count == 0;
            if (empty)
            {
                TxtEmptyHint.Text = _isScanning
                    ? "Looking for devices… they'll appear here as they answer."
                    : TryFindResource("Str_DeviceScan_EmptyHint") as string ?? "Press Scan Network to find the devices on your network.";
            }
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
        }

        // =================================================================
        //  Filter
        // =================================================================

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            _resultsView.Refresh();
        }

        private bool FilterDevice(object item)
        {
            string query = TxtFilter.Text.Trim();
            if (query.Length == 0 || item is not DeviceScanResult d) return true;

            return d.IPAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.HostNameDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.MacAddressDisplay.Replace("-", "").Contains(query.Replace("-", "").Replace(":", ""), StringComparison.OrdinalIgnoreCase)
                || (d.Vendor?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        // =================================================================
        //  Row actions
        // =================================================================

        private List<DeviceScanResult> SelectedDevices() =>
            GridDevices.SelectedItems.OfType<DeviceScanResult>().OrderBy(d => d.IpSortKey).ToList();

        private void CopyLines(Func<DeviceScanResult, string?> pick)
        {
            var lines = SelectedDevices().Select(pick).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (lines.Count == 0) return;
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
            catch (Exception) { /* clipboard busy */ }
        }

        private void MenuCopyIp_Click(object sender, RoutedEventArgs e) => CopyLines(d => d.IPAddress);
        private void MenuCopyMac_Click(object sender, RoutedEventArgs e) => CopyLines(d => d.MacAddress);
        private void MenuCopyName_Click(object sender, RoutedEventArgs e) => CopyLines(d => d.HostName);
        private void MenuCopyRow_Click(object sender, RoutedEventArgs e) =>
            CopyLines(d => $"{d.IPAddress}\t{d.HostNameDisplay}\t{d.MacAddressDisplay}\t{d.VendorDisplay}\t{d.ResponseDisplay}");

        private void MenuOpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            var device = SelectedDevices().FirstOrDefault();
            if (device == null) return;
            try
            {
                Process.Start(new ProcessStartInfo($"http://{device.IPAddress}/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldnTOpenThe3", "Couldn't open the browser: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =================================================================
        //  Export
        // =================================================================

        /// <summary>HTML, PDF or text report of the devices shown (respects the filter), same columns as the grid.</summary>
        private void MenuExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_results.Count == 0)
            {
                AppModal.Show(Loc.T("Str_MB_NothingToExportYet3", "Nothing to export yet - run a scan first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (sender is not MenuItem { Tag: string kind }) return;
            var devices = _resultsView.Cast<DeviceScanResult>().ToList();
            string noReply = Loc.T("Str_DeviceScan_NoReply", "no reply");
            var rows = devices.Select(d => new[]
            {
                d.IPAddress, d.HostNameDisplay, d.MacAddressDisplay, d.VendorDisplay, d.ResponseTimeMs is null ? noReply : d.ResponseDisplay,
                d.IsThisDevice ? Loc.T("Str_PortScanner_ThisPc", "This PC") : d.IsGateway ? Loc.T("Str_DeviceScan_Router", "Router") : "",
            }).ToList();
            string title = Loc.T("Str_Tool_device-scanner_Name", "Network Device Scanner");
            string note = Loc.F("Str_DeviceScan_ReportNote", "{0} devices", devices.Count);
            var headers = new[]
            {
                Loc.T("Str_DeviceScan_ColIp", "IP address"), Loc.T("Str_DeviceScan_ColName", "Name"), Loc.T("Str_DeviceScan_ColMac", "MAC address"),
                Loc.T("Str_DeviceScan_ColVendor", "Manufacturer"), Loc.T("Str_DeviceScan_ColResponse", "Response"), Loc.T("Str_DeviceScan_ColRole", "Role"),
            };
            var report = new TableReport(title, note, headers, rows, new[] { 0.14, 0.24, 0.16, 0.24, 0.12, 0.10 });
            if (TableReportSaver.Save(Window.GetWindow(this), report, kind, "network-scan"))
                TxtScanProgress.Text = Loc.F("Str_DeviceScan_ExportedN", "Exported {0} device(s)", devices.Count);
        }

        private void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_results.Count == 0)
            {
                AppModal.Show(Loc.T("Str_MB_NothingToExportYet3", "Nothing to export yet - run a scan first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export scan results",
                Filter = "CSV file (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = $"network_scan_{DateTime.Now:yyyy-MM-dd_HHmm}.csv",
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                // Exports what's visible (respects the filter), in grid order.
                var rows = _resultsView.Cast<DeviceScanResult>().ToList();
                DeviceScannerManager.ExportToCsv(rows, dialog.FileName);
                TxtScanProgress.Text = $"Exported {rows.Count:N0} device(s) to {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_Err_ExportFailed", "Export failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
