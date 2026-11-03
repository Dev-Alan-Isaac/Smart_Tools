using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Wi-Fi Analyzer - UI only. Scanning, the channel advice and saved profiles are in <see cref="WifiManager"/>
    /// (Windows' Wi-Fi API in <c>WlanNative</c>). It only listens: nothing is connected or changed.
    /// </summary>
    public partial class WifiView : UserControl
    {
        private const string DialogTitle = "Wi-Fi Analyzer";
        private static readonly TimeSpan ScanWait = TimeSpan.FromSeconds(3);

        private readonly ObservableCollection<WifiNetwork> _networks = new();
        private readonly ObservableCollection<WifiProfile> _profiles = new();
        private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(15) };

        private CancellationTokenSource? _cts;
        private WifiScan? _last;
        private bool _busy;
        private bool _loadedOnce;
        private bool _updating;

        public WifiView()
        {
            InitializeComponent();
            NetworkGrid.ItemsSource = _networks;
            ProfileGrid.ItemsSource = _profiles;
            BuildProfileMenu();

            _autoTimer.Tick += async (_, _) => { if (!_busy && IsVisible) await ScanAsync(auto: true); };
            _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideStatus(); };
            Unloaded += (_, _) => _autoTimer.Stop();
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                UpdateState();
                await ScanAsync(auto: false);
            }
            if (ChkAuto.IsChecked == true) _autoTimer.Start();
        }

        // =================================================================
        //  Scanning
        // =================================================================

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }
            if (RadioSaved.IsChecked == true) _ = LoadProfilesAsync();
            else _ = ScanAsync(auto: false);
        }

        private async Task ScanAsync(bool auto)
        {
            if (_busy) return;
            _busy = true;
            _cts = new CancellationTokenSource();
            TxtScan.Text = "Stop";
            IconScan.Kind = PackIconKind.Stop;
            Bar.IsIndeterminate = true;
            if (!auto) TxtProgress.Text = "Listening for networks...";

            try
            {
                var scan = await WifiManager.ScanAsync(ScanWait, _cts.Token);
                _last = scan;
                ApplyScan(scan, firstTime: _networks.Count == 0);
                TxtProgress.Text = scan.Problem == null
                    ? $"{scan.Networks.Count:N0} access point{(scan.Networks.Count == 1 ? "" : "s")} heard at {scan.When:T}."
                    : "Nothing to show.";
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Stopped.";
            }
            catch (Exception ex)
            {
                ShowStatus("The scan failed: " + ex.Message, StatusKind.Error);
            }
            finally
            {
                _busy = false;
                _cts = null;
                TxtScan.Text = "Scan";
                IconScan.Kind = PackIconKind.Radar;
                Bar.IsIndeterminate = false;
            }
        }

        private void ApplyScan(WifiScan scan, bool firstTime)
        {
            // Keep the highlighted access point across refreshes.
            string? selected = (NetworkGrid.SelectedItem as WifiNetwork)?.Bssid;
            _networks.Clear();
            foreach (var n in scan.Networks) _networks.Add(n);
            if (selected != null) NetworkGrid.SelectedItem = _networks.FirstOrDefault(n => n.Bssid == selected);

            TxtAdapter.Text = scan.AdapterName.Length > 0 ? scan.AdapterName : "No Wi-Fi adapter found";
            TxtLink.Text = scan.Link is { } l
                ? $"Connected to {l.Ssid} · {l.Standard} · {l.RxKbps / 1000.0:0} / {l.TxKbps / 1000.0:0} Mbps (receive / send)"
                : scan.AdapterName.Length > 0 ? "Not connected to a network." : "";

            if (firstTime && scan.Link != null)
            {
                var yours = scan.Networks.FirstOrDefault(n => n.IsConnected);
                _updating = true;
                if (yours?.Band == WifiBand.Ghz5) RadioBand5.IsChecked = true;
                else if (yours?.Band == WifiBand.Ghz6) RadioBand6.IsChecked = true;
                _updating = false;
            }

            if (scan.Problem != null)
            {
                ShowStatus(scan.Problem, StatusKind.Warning, locationButton: scan.Problem.Contains("Location", StringComparison.OrdinalIgnoreCase));
            }
            else if (StatusBanner.Visibility == Visibility.Visible && BtnOpenLocation.Visibility == Visibility.Visible)
            {
                HideStatus();
            }
            RefreshChart();
            UpdateState();
        }

        private WifiBand SelectedBand => RadioBand5.IsChecked == true ? WifiBand.Ghz5 : RadioBand6.IsChecked == true ? WifiBand.Ghz6 : WifiBand.Ghz24;

        private void RefreshChart()
        {
            var band = SelectedBand;
            var advice = WifiManager.Advise(_networks, band);
            var best = advice.FirstOrDefault();
            int onBand = _networks.Count(n => n.Band == band);

            Chart.SetData(_networks, band, onBand > 0 || _networks.Count > 0 ? best?.Channel ?? 0 : 0);
            TxtChartTitle.Text = $"{WifiText.BandName(band)} · {onBand:N0} access point{(onBand == 1 ? "" : "s")}";
            TxtChartAdvice.Text = best == null || _networks.Count == 0 ? "" : $"Best channel: {best.Channel} ({best.Reason})";

            var yours = _networks.FirstOrDefault(n => n.IsConnected);
            TxtSummaryCount.Text = $"{_networks.Count:N0} access point{(_networks.Count == 1 ? "" : "s")}";
            PillBest.Visibility = best != null && _networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryBest.Text = best == null ? "" : $"Best on {WifiText.BandName(band)}: ch {best.Channel}";
            PillYours.Visibility = yours != null ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryYours.Text = yours == null ? "" : $"You: ch {yours.Channel} · {yours.BandText}";
        }

        // =================================================================
        //  Options
        // =================================================================

        private void Band_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _updating) return;
            RefreshChart();
        }

        private async void View_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateState();
            if (RadioSaved.IsChecked == true) await LoadProfilesAsync();
        }

        private void ChkAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkAuto.IsChecked == true) _autoTimer.Start();
            else _autoTimer.Stop();
        }

        private void UpdateState()
        {
            bool saved = RadioSaved.IsChecked == true;
            NearbyPanel.Visibility = !saved && _networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProfileGrid.Visibility = saved && _profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BandPanel.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
            EmptyStatePanel.Visibility = (saved ? _profiles.Count : _networks.Count) > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyStatePanel.Description = saved
                ? "No saved Wi-Fi networks found on this PC."
                : "Press Scan to see the Wi-Fi networks around you, how strong they are and which channel is least crowded.";
            CardTitle.Content = saved ? "Saved networks" : "Networks";
            SummaryPanel.Visibility = !saved && _networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnCopy.IsEnabled = BtnSave.IsEnabled = !saved && _networks.Count > 0;
            BtnCopy.Visibility = BtnSave.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Saved networks
        // =================================================================

        private async Task LoadProfilesAsync()
        {
            if (_busy) return;
            _busy = true;
            Bar.IsIndeterminate = true;
            TxtProgress.Text = "Reading the saved networks...";
            try
            {
                var list = await WifiManager.ProfilesAsync(CancellationToken.None);
                _profiles.Clear();
                foreach (var p in list) _profiles.Add(p);
                TxtProgress.Text = $"{list.Count:N0} saved network{(list.Count == 1 ? "" : "s")}. Right-click one to show its password.";
            }
            catch (Exception ex)
            {
                ShowStatus("The saved networks couldn't be read: " + ex.Message, StatusKind.Error);
            }
            finally
            {
                _busy = false;
                Bar.IsIndeterminate = false;
                UpdateState();
            }
        }

        private void BuildProfileMenu()
        {
            var menu = new ContextMenu();
            var show = new MenuItem { Header = "Show the password", Icon = new PackIcon { Kind = PackIconKind.EyeOutline } };
            show.Click += async (_, _) => await RevealAsync(copy: false);
            var copy = new MenuItem { Header = "Copy the password", Icon = new PackIcon { Kind = PackIconKind.ContentCopy } };
            copy.Click += async (_, _) => await RevealAsync(copy: true);
            menu.Items.Add(show);
            menu.Items.Add(copy);
            menu.Opened += (_, _) => show.IsEnabled = copy.IsEnabled = ProfileGrid.SelectedItem is WifiProfile { IsOpen: false };
            ProfileGrid.ContextMenu = menu;
        }

        private async Task RevealAsync(bool copy)
        {
            if (ProfileGrid.SelectedItem is not WifiProfile p) return;
            var (password, problem) = await WifiManager.RevealPasswordAsync(p.Name, CancellationToken.None);
            if (password == null)
            {
                ShowStatus(problem ?? "The password isn't available.", StatusKind.Warning);
                return;
            }
            if (copy)
            {
                try
                {
                    System.Windows.Clipboard.SetText(password);
                    ShowStatus($"The password of {p.Name} is on the clipboard. Clipboard history may keep it - clear it when you are done.", StatusKind.Info);
                }
                catch (Exception ex)
                {
                    ShowStatus("The clipboard is busy: " + ex.Message, StatusKind.Error);
                }
            }
            else
            {
                ShowStatus($"Password of {p.Name}:  {password}   (hidden again in 15 seconds)", StatusKind.Info);
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        }

        // =================================================================
        //  Report
        // =================================================================

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_networks.Count == 0) return;
            try
            {
                System.Windows.Clipboard.SetText(WifiManager.BuildCsv(_networks));
                TxtProgress.Text = "List copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ShowStatus("The clipboard is busy: " + ex.Message, StatusKind.Error);
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_networks.Count == 0) return;
            var dialog = new SaveFileDialog { Title = DialogTitle, FileName = "wifi networks.csv", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string written = await FolderTreeManager.SaveAsync(WifiManager.BuildCsv(_networks), dialog.FileName);
                TxtProgress.Text = "Saved " + Path.GetFileName(written);
            }
            catch (Exception ex)
            {
                ShowStatus("The file couldn't be saved: " + ex.Message, StatusKind.Error);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Info, Warning, Error }

        private void ShowStatus(string text, StatusKind kind, bool locationButton = false)
        {
            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Info => ("StatusInfoBg", "StatusInfoFg", PackIconKind.InformationOutline),
                _ => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            BtnOpenLocation.Visibility = locationButton ? Visibility.Visible : Visibility.Collapsed;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusBanner.Visibility = Visibility.Collapsed;
            BtnOpenLocation.Visibility = Visibility.Collapsed;
        }

        private void BtnOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowStatus("Windows Settings couldn't be opened: " + ex.Message, StatusKind.Error);
            }
        }

        /// <summary>Called when the window closes: stops a scan and the auto-refresh. Nothing was ever changed on the PC.</summary>
        public void CancelActiveRun()
        {
            _autoTimer.Stop();
            _hideTimer.Stop();
            _cts?.Cancel();
        }
    }
}
