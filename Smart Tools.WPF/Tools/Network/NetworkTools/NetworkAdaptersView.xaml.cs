using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// My IP &amp; Adapters - UI only. Reading every adapter, checking an edit and
    /// running netsh are in NetworkToolsManager.Adapters.cs; the public IP in
    /// NetworkToolsManager.cs. The list refreshes itself when an adapter is
    /// plugged in, unplugged, enabled or disabled.
    /// </summary>
    public partial class NetworkAdaptersView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private CancellationTokenSource? _ipCts;
        private bool _ipLoadedOnce;
        private List<AdapterInfo> _adapters = new();
        private string? _selectedAdapterId;
        private bool _adapterBusy;
        private bool _adaptersStale;
        private bool _networkEventsHooked;
        private bool _closed;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;
        private readonly bool _viewConstructed;
        private readonly bool _isAdmin = NetworkToolsManager.IsAdministrator();

        /// <summary>Windows reports several changes for one plug-in; wait for them to settle, then read once.</summary>
        private readonly DispatcherTimer _networkChangedTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

        public NetworkAdaptersView()
        {
            InitializeComponent();
            _networkChangedTimer.Tick += NetworkChangedTimer_Tick;
            TxtToolTitle.Text = "This computer";
            TxtToolDetail.Text = "Every network adapter - pick one to see its addresses, change its IP settings, or turn it off and on.";
            BtnAction.ToolTip = "Read this computer's adapters and addresses again";
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops listening for network changes and any lookup.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            HookNetworkEvents(false);
            _networkChangedTimer.Stop();
            _ipCts?.Cancel();
        }

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                var s = NetworkToolsManager.LoadSettings();
                _applying = true;
                try
                {
                    ChkLookUpPublicIp.IsChecked = s.LookUpPublicIp;
                    ChkShowVirtual.IsChecked = s.ShowVirtualAdapters;
                }
                finally
                {
                    _applying = false;
                }
            }
            HookNetworkEvents(true);
            if (!_ipLoadedOnce)
            {
                _ipLoadedOnce = true;   // once per session - not on every visit
                await RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
            }
            else if (_adaptersStale && !_adapterBusy)
            {
                await RefreshIpInfoAsync(log: false, lookUpPublicIp: false);
            }
        }

        /// <summary>NetworkChange's events are static: unhooked when the page goes, or the page would never be freed.</summary>
        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            HookNetworkEvents(false);
            _networkChangedTimer.Stop();
            _adaptersStale = true;   // something may change while away
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

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying || !_loadedOnce) return;
            bool lookUp = ChkLookUpPublicIp.IsChecked == true, showVirtual = ChkShowVirtual.IsChecked == true;
            try
            {
                NetworkToolsManager.UpdateSettings(s =>
                {
                    s.LookUpPublicIp = lookUp;
                    s.ShowVirtualAdapters = showVirtual;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Adapter settings not saved: {ex.Message}");
            }
            if (sender == ChkShowVirtual) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: false);
            if (sender == ChkLookUpPublicIp) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(BtnAction, 0.96);
            _ = RefreshIpInfoAsync(log: true, lookUpPublicIp: true);
        }

        // =================================================================
        //  IP info and adapters
        // =================================================================

        private void HookNetworkEvents(bool hook)
        {
            if (hook == _networkEventsHooked || (hook && _closed)) return;
            _networkEventsHooked = hook;
            if (hook)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            }
            else
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            }
        }

        private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, EventArgs.Empty);

        /// <summary>Background thread: a cable, a USB adapter, an address changed. Restart the settle timer on the UI thread.</summary>
        private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            if (_closed) return;
            _networkChangedTimer.Stop();
            _networkChangedTimer.Start();
        });

        private void NetworkChangedTimer_Tick(object? sender, EventArgs e)
        {
            _networkChangedTimer.Stop();
            if (_closed) return;
            // Only read while this page is showing; otherwise read when it's next opened.
            if (IsVisible && !_adapterBusy) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
            else _adaptersStale = true;
        }

        private async Task RefreshIpInfoAsync(bool log, bool lookUpPublicIp)
        {
            _ipCts?.Cancel();
            var cts = _ipCts = new CancellationTokenSource();
            BtnAction.IsEnabled = false;
            _adaptersStale = false;
            try
            {
                TxtHostname.Text = Dns.GetHostName();
                bool includeVirtual = ChkShowVirtual.IsChecked == true;
                var adapters = await Task.Run(() => NetworkToolsManager.GetAdapters(includeVirtual), cts.Token);
                if (cts.IsCancellationRequested || _closed) return;
                ShowAdapters(adapters);
                if (log)
                {
                    Results.Log(ConsoleKind.Info, "IP", $"{TxtHostname.Text} · " + (adapters.Count == 0 ? "no adapters" :
                        string.Join(" · ", adapters.Select(a => a.State == AdapterState.Connected ? $"{a.Name} {a.AddressText}" : $"{a.Name} ({a.StatusText.ToLowerInvariant()})"))));
                }

                if (!lookUpPublicIp) return;
                if (ChkLookUpPublicIp.IsChecked == true)
                {
                    TxtPublicIp.Text = "Looking up...";
                    TxtPublicIp.ToolTip = null;
                    try
                    {
                        TxtPublicIp.Text = await NetworkToolsManager.GetPublicIpAsync(cts.Token);
                        if (log) Results.Log(ConsoleKind.Info, "IP", $"Public address: {TxtPublicIp.Text}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
                    {
                        TxtPublicIp.Text = "Unavailable";
                        TxtPublicIp.ToolTip = "No internet, a firewall blocked it, or api.ipify.org is down.";
                    }
                }
                else
                {
                    TxtPublicIp.Text = "Not looked up";
                    TxtPublicIp.ToolTip = "Tick \"Look up my public IP\" on the left.";
                }
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            catch (Exception ex)
            {
                Results.Log(ConsoleKind.Error, "IP", "Couldn't read the network adapters: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_ipCts, cts))
                {
                    _ipCts = null;
                    BtnAction.IsEnabled = true;
                }
                cts.Dispose();
            }
        }

        /// <summary>New list, same adapter still chosen (by its id, so a rename doesn't lose it). An open edit form is left as typed.</summary>
        private void ShowAdapters(List<AdapterInfo> adapters)
        {
            _adapters = adapters;
            int connected = adapters.Count(a => a.State == AdapterState.Connected);
            TxtAdapterCount.Text = adapters.Count == 0 ? "0" : $"{connected} of {adapters.Count} on";
            TxtNoAdapters.Text = "No network adapters found." + (ChkShowVirtual.IsChecked == true ? "" : " Tick \"Show virtual adapters\" on the left to list VPN and Hyper-V ones.");
            TxtNoAdapters.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var keep = adapters.FirstOrDefault(a => a.Id == _selectedAdapterId) ?? adapters.FirstOrDefault();
            AdapterList.ItemsSource = adapters;
            AdapterList.SelectedItem = keep;
            if (keep == null) AdapterDetail.Visibility = Visibility.Collapsed;
            else ShowAdapter(keep, animate: false);
        }

        private AdapterInfo? SelectedAdapter => AdapterList.SelectedItem as AdapterInfo;

        private void AdapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedAdapter is not AdapterInfo a) return;
            bool changed = a.Id != _selectedAdapterId;
            if (changed && EditPanel.Visibility == Visibility.Visible) EditPanel.Visibility = Visibility.Collapsed;   // the form was for the other adapter
            ShowAdapter(a, animate: changed);
        }

        private void ShowAdapter(AdapterInfo a, bool animate)
        {
            _selectedAdapterId = a.Id;
            AdapterDetail.Visibility = Visibility.Visible;
            AdapterDetailPanel.DataContext = a;
            AdapterInfoRows.ItemsSource = NetworkToolsManager.DescribeAdapter(a);

            (string bg, string fg, PackIconKind icon) = a.State switch
            {
                AdapterState.Connected => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.LanConnect),
                AdapterState.Connecting => ("StatusInfoBg", "StatusInfoFg", PackIconKind.LanPending),
                AdapterState.Disconnected => ("StatusWarningBg", "StatusWarningFg", a.Kind.Contains("Wi-Fi") ? PackIconKind.WifiOff : PackIconKind.EthernetOff),
                AdapterState.Disabled => ("StatusErrorBg", "StatusErrorFg", PackIconKind.PowerPlugOff),
                _ => ("RowBg", "TextSecondary", PackIconKind.LanPending),
            };
            AdapterStatePill.SetResourceReference(Border.BackgroundProperty, bg);
            TxtAdapterState.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconAdapterState.SetResourceReference(ForegroundProperty, fg);
            IconAdapterState.Kind = icon;

            TxtToggleAdapter.Text = a.IsEnabled ? "Disable" : "Enable";
            IconToggleAdapter.Kind = a.IsEnabled ? PackIconKind.PowerPlugOff : PackIconKind.PowerPlug;
            BtnToggleAdapter.ToolTip = a.IsEnabled
                ? "Turn this adapter off (like unplugging it) - Windows asks for permission"
                : "Turn this adapter back on - Windows asks for permission";
            UpdateAdapterButtons();

            if (animate)
            {
                UiMotion.FadeIn(AdapterDetailPanel, 4);
                UiMotion.Pop(AdapterChip, 0.85);
            }
        }

        private void UpdateAdapterButtons()
        {
            bool usable = SelectedAdapter is AdapterInfo a && NetworkToolsManager.IsUsableAdapterName(a.Name);
            BtnEditIp.IsEnabled = usable && !_adapterBusy;
            BtnToggleAdapter.IsEnabled = usable && !_adapterBusy;
            BtnApplyIp.IsEnabled = usable && !_adapterBusy;
            AdapterBusyBar.Visibility = _adapterBusy ? Visibility.Visible : Visibility.Collapsed;
            TxtAdminHint.Text = _isAdmin ? "Running as administrator - applies straight away" : "Windows will ask for permission";
        }

        // ---------------- Edit ----------------

        private void BtnEditIp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAdapter is not AdapterInfo a) return;
            if (EditPanel.Visibility == Visibility.Visible)
            {
                UiMotion.FadeVisibility(EditPanel, false);
                return;
            }
            FillEdit(NetworkToolsManager.EditFromAdapter(a));
            UiMotion.FadeVisibility(EditPanel, true);
            (RadioIpManual.IsChecked == true ? TxtEditIp : (System.Windows.Controls.Control)RadioIpAuto).Focus();
        }

        private void FillEdit(IpConfigEdit e)
        {
            RadioIpAuto.IsChecked = e.Dhcp;
            RadioIpManual.IsChecked = !e.Dhcp;
            TxtEditIp.Text = e.Ip;
            TxtEditMask.Text = e.Mask;
            TxtEditGateway.Text = e.Gateway;
            RadioDnsAuto.IsChecked = e.DnsAutomatic;
            RadioDnsManual.IsChecked = !e.DnsAutomatic;
            TxtEditDns1.Text = e.Dns1;
            TxtEditDns2.Text = e.Dns2;
            TxtEditError.Visibility = Visibility.Collapsed;
            UpdateEditUi();
        }

        private IpConfigEdit ReadEdit() => new(
            RadioIpAuto.IsChecked == true,
            TxtEditIp.Text.Trim(),
            TxtEditMask.Text.Trim(),
            TxtEditGateway.Text.Trim(),
            RadioDnsAuto.IsChecked == true,
            TxtEditDns1.Text.Trim(),
            TxtEditDns2.Text.Trim());

        private void EditMode_Click(object sender, RoutedEventArgs e) => UpdateEditUi();

        /// <summary>A manual address has no DHCP to hand out DNS, so DNS becomes manual too (as in Windows' own dialog).</summary>
        private void UpdateEditUi()
        {
            bool manualIp = RadioIpManual.IsChecked == true;
            if (manualIp && RadioDnsAuto.IsChecked == true)
            {
                RadioDnsManual.IsChecked = true;
                if (TxtEditDns1.Text.Trim().Length == 0 && TxtEditGateway.Text.Trim().Length > 0) TxtEditDns1.Text = TxtEditGateway.Text.Trim();   // the router usually answers DNS
            }
            RadioDnsAuto.IsEnabled = !manualIp;
            RadioDnsAuto.ToolTip = manualIp ? "Not available with a manual address - there's no DHCP to hand out DNS" : null;
            UiMotion.FadeVisibility(ManualIpFields, manualIp);
            UiMotion.FadeVisibility(ManualDnsFields, RadioDnsManual.IsChecked == true);
            TxtEditError.Visibility = Visibility.Collapsed;
        }

        private void EditField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            TxtEditError.Visibility = Visibility.Collapsed;   // problems are shown again on Apply, not while typing
        }

        private void BtnCancelEdit_Click(object sender, RoutedEventArgs e) => UiMotion.FadeVisibility(EditPanel, false);

        private void ShowEditError(string message)
        {
            TxtEditError.Text = message;
            TxtEditError.Visibility = Visibility.Visible;
            UiMotion.Pop(TxtEditError, 0.95);
        }

        private async void BtnApplyIp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAdapter is not AdapterInfo a || _adapterBusy) return;
            var edit = ReadEdit();
            if (NetworkToolsManager.ValidateEdit(edit) is string problem)
            {
                ShowEditError(problem);
                return;
            }

            string change = NetworkToolsManager.DescribeEdit(a.Name, edit);
            string risk = a.State == AdapterState.Connected && a.Gateways.Count > 0
                ? "\n\nThis adapter is your internet connection right now: it drops for a moment, and stays off if the new settings are wrong (you can switch back to Automatic here)."
                : "";
            if (MessageBox.Show($"Change {a.Name}?\n\n{change}{risk}", "Network Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            Results.Log(ConsoleKind.Info, "IP", "Applying: " + change);
            var result = await RunAdapterChangeAsync(NetworkToolsManager.NetshCommands(a.Name, edit));
            if (result.Ok)
            {
                Results.Log(ConsoleKind.Success, "IP", $"{a.Name}: settings changed.");
                UiMotion.FadeVisibility(EditPanel, false);
            }
            else if (result.Cancelled)
            {
                Results.Log(ConsoleKind.Info, "IP", result.Message);
            }
            else
            {
                Results.Log(ConsoleKind.Error, "IP", $"{a.Name}: {result.Message}");
                ShowEditError(result.Message);
            }
        }

        // ---------------- Enable / disable ----------------

        private async void BtnToggleAdapter_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAdapter is not AdapterInfo a || _adapterBusy) return;
            bool enable = !a.IsEnabled;
            if (!enable)
            {
                string warn = a.State == AdapterState.Connected && a.Gateways.Count > 0
                    ? "\n\nIt's your internet connection right now - the internet stops until you enable it again (or use another adapter)."
                    : "";
                if (MessageBox.Show($"Disable {a.Name}?{warn}", "Network Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }

            Results.Log(ConsoleKind.Info, "IP", $"{(enable ? "Enabling" : "Disabling")} {a.Name}...");
            var result = await RunAdapterChangeAsync(NetworkToolsManager.NetshEnableCommands(a.Name, enable));
            if (result.Ok) Results.Log(ConsoleKind.Success, "IP", $"{a.Name} {(enable ? "enabled - it can take a few seconds to connect." : "disabled.")}");
            else Results.Log(result.Cancelled ? ConsoleKind.Info : ConsoleKind.Error, "IP", result.Cancelled ? result.Message : $"{a.Name}: {result.Message}");
        }

        /// <summary>Runs the change with the buttons locked; the adapters are read again in the background.</summary>
        private async Task<NetshResult> RunAdapterChangeAsync(IReadOnlyList<string> commands)
        {
            _adapterBusy = true;
            UpdateAdapterButtons();
            NetshResult result;
            try
            {
                result = await NetworkToolsManager.RunNetshAsync(commands);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result = new NetshResult(false, false, ex.Message);
            }
            finally
            {
                _adapterBusy = false;
                if (!_closed) UpdateAdapterButtons();
            }
            if (!result.Cancelled && !_closed) _ = RefreshAfterChangeAsync();
            return result;
        }

        /// <summary>Reads the adapters again shortly after a change, and once more a few seconds later (an enabled adapter takes a while to get its address).</summary>
        private async Task RefreshAfterChangeAsync()
        {
            await Task.Delay(1500);
            if (!_closed && !_adapterBusy) await RefreshIpInfoAsync(log: false, lookUpPublicIp: false);
            await Task.Delay(3500);
            if (!_closed && !_adapterBusy) await RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
        }

    }
}
