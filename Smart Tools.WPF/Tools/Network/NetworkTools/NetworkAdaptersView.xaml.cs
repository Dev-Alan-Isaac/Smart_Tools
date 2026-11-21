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
using RadioButton = System.Windows.Controls.RadioButton;
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
        private CancellationTokenSource? _btCts;
        private bool _ipLoadedOnce;
        private List<AdapterInfo> _adapters = new();        // what the list shows: the real adapters plus the Bluetooth radio
        private List<AdapterInfo> _realAdapters = new();    // as read from Windows
        private BluetoothInfo? _bt;
        private List<AdapterInfo> _hardware = new();       // display, audio, storage and USB adapters (read-only)
        private string _category = "All";
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
            TxtToolTitle.Text = Dns.GetHostName();
            TxtToolDetail.Text = "Every adapter on this computer - Wi-Fi, Ethernet, Bluetooth... pick one to see its own details.";
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
            _btCts?.Cancel();
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
                await RefreshIpInfoAsync(log: false);
            }
            else if (_adaptersStale && !_adapterBusy)
            {
                await RefreshIpInfoAsync(log: false);
            }
        }

        /// <summary>NetworkChange's events are static: unhooked when the page goes, or the page would never be freed.</summary>
        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            HookNetworkEvents(false);
            _networkChangedTimer.Stop();
            _adaptersStale = true;   // something may change while away
        }

        /// <summary>Folded: the Activity card is only its header (the adapters get the room); open: a quarter of the page.</summary>
        private void Results_CollapsedChanged(object? sender, bool collapsed)
        {
            if (ConsoleRow == null) return;
            ConsoleRow.Height = collapsed ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            ConsoleRow.MinHeight = collapsed ? 0 : 130;
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
            bool showVirtual = ChkShowVirtual.IsChecked == true;
            try
            {
                NetworkToolsManager.UpdateSettings(s =>
                {
                    s.ShowVirtualAdapters = showVirtual;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Adapter settings not saved: {ex.Message}");
            }
            if (sender == ChkShowVirtual) _ = RefreshIpInfoAsync(log: false);
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(BtnAction, 0.96);
            _ = RefreshIpInfoAsync(log: true);
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
            if (IsVisible && !_adapterBusy) _ = RefreshIpInfoAsync(log: false);
            else _adaptersStale = true;
        }

        private async Task RefreshIpInfoAsync(bool log)
        {
            _ipCts?.Cancel();
            var cts = _ipCts = new CancellationTokenSource();
            BtnAction.IsEnabled = false;
            _adaptersStale = false;
            try
            {
                TxtToolTitle.Text = Dns.GetHostName();
                _ = RefreshHardwareAsync();
                _ = RefreshBluetoothAsync();   // independent of the adapter list: its own token and error handling
                bool includeVirtual = ChkShowVirtual.IsChecked == true;
                var adapters = await Task.Run(() => NetworkToolsManager.GetAdapters(includeVirtual), cts.Token);
                if (cts.IsCancellationRequested || _closed) return;
                ShowAdapters(adapters);
                if (log)
                {
                    Results.Log(ConsoleKind.Info, "IP", $"{TxtToolTitle.Text} · " + (adapters.Count == 0 ? "no adapters" :
                        string.Join(" · ", adapters.Select(a => a.State == AdapterState.Connected ? $"{a.Name} {a.AddressText}" : $"{a.Name} ({a.StatusText.ToLowerInvariant()})"))));
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
        private void ShowAdapters(List<AdapterInfo> real)
        {
            _realAdapters = real;
            ApplyList();
        }

        private async Task RefreshHardwareAsync()
        {
            try
            {
                var found = await Task.Run(NetworkToolsManager.GetHardwareAdapters);
                if (_closed) return;
                _hardware = found;
                if (_realAdapters.Count > 0) ApplyList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log.Info($"Hardware adapters: {ex.Message}");
            }
        }

        /// <summary>A Bluetooth radio is an adapter like the others: it gets its own list entry (unless Windows already lists a Bluetooth network adapter).</summary>
        private List<AdapterInfo> WithBluetooth(List<AdapterInfo> real)
        {
            var list = new List<AdapterInfo>(real);
            if (_bt != null && _bt.Radios.Count > 0) AddRadios(list);
            list.AddRange(_hardware.OrderBy(h => Array.IndexOf(NetworkToolsManager.HardwareCategories, h.HardwareCategory)).ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase));
            return list;
        }

        private void AddRadios(List<AdapterInfo> list)
        {
            foreach (var r in _bt!.Radios)
            {
                list.Add(new AdapterInfo
                {
                    Name = r.Name,
                    Id = "bt:" + r.Name,
                    Description = Loc.T("Str_NetworkAdapters_BtRadio", "Bluetooth adapter"),
                    Kind = "Bluetooth",
                    IsBluetoothRadio = true,
                    State = r.State switch
                    {
                        BluetoothRadioState.On => AdapterState.Connected,
                        BluetoothRadioState.Off => AdapterState.Disconnected,
                        BluetoothRadioState.Disabled => AdapterState.Disabled,
                        _ => AdapterState.Unknown,
                    },
                    StatusOverride = BtStateText(r.State),
                    ExtraRows = BtRadioRows(r),
                });
            }
        }

        private static readonly string[] CategoryOrder = { "Network", "Bluetooth", "Display", "Audio", "Storage", "USB" };

        private static string CategoryName(string c) => c switch
        {
            "All" => Loc.T("Str_NetworkAdapters_CatAll", "All"),
            "Network" => Loc.T("Str_NetworkAdapters_CatNetwork", "Network"),
            "Bluetooth" => Loc.T("Str_NetworkAdapters_CatBluetooth", "Bluetooth"),
            "Display" => Loc.T("Str_NetworkAdapters_CatDisplay", "Display"),
            "Audio" => Loc.T("Str_NetworkAdapters_CatAudio", "Audio"),
            "Storage" => Loc.T("Str_NetworkAdapters_CatStorage", "Storage"),
            _ => Loc.T("Str_NetworkAdapters_CatUsb", "USB"),
        };

        /// <summary>One tab per kind of adapter this PC has, with how many. A kind that has gone falls back to All.</summary>
        private void BuildCategoryBar(List<AdapterInfo> all)
        {
            var present = CategoryOrder.Where(c => all.Any(a => a.Category == c)).ToList();
            if (_category != "All" && !present.Contains(_category)) _category = "All";
            CategoryBar.Children.Clear();
            var style = (Style)FindResource("ModeTabStyle");
            foreach (var c in new[] { "All" }.Concat(present))
            {
                int n = c == "All" ? all.Count : all.Count(a => a.Category == c);
                var tab = new RadioButton
                {
                    Content = $"{CategoryName(c)} ({n})",
                    Tag = c,
                    GroupName = "AdapterCategory",
                    Style = style,
                    IsChecked = c == _category,
                };
                tab.Click += CategoryTab_Click;
                CategoryBar.Children.Add(tab);
            }
        }

        private void CategoryTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string c } || c == _category) return;
            _category = c;
            ApplyList();
        }

        /// <summary>The radio's own facts: address, what it supports, its driver. Anything Windows did not answer is left out.</summary>
        private List<NetworkInfoRow> BtRadioRows(BluetoothRadioInfo r)
        {
            var rows = new List<NetworkInfoRow>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new NetworkInfoRow(label, value));
            }
            string Yes(bool b) => b ? Loc.T("Str_NetworkAdapters_BtYes", "Yes") : Loc.T("Str_NetworkAdapters_BtNo", "No");
            Add(Loc.T("Str_NetworkAdapters_BtRowType", "Type"), "Bluetooth");
            Add(Loc.T("Str_NetworkAdapters_BtRowAdapter", "Adapter"), r.Name);
            if (_bt?.Adapter is BluetoothAdapterDetails a)
            {
                Add(Loc.T("Str_NetworkAdapters_BtRowAddress", "Bluetooth address"), a.Address);
                Add(Loc.T("Str_NetworkAdapters_BtRowClassic", "Classic Bluetooth"), Yes(a.Classic));
                Add(Loc.T("Str_NetworkAdapters_BtRowLe", "Low Energy"), Yes(a.LowEnergy));
                if (a.LowEnergy) Add(Loc.T("Str_NetworkAdapters_BtRowSecureLe", "Secure LE connections"), Yes(a.SecureLowEnergy));
                Add(Loc.T("Str_NetworkAdapters_BtRowRoles", "Roles"),
                    string.Join(", ", new[]
                    {
                        a.CentralRole ? Loc.T("Str_NetworkAdapters_BtRoleCentral", "connects to devices") : "",
                        a.PeripheralRole ? Loc.T("Str_NetworkAdapters_BtRolePeripheral", "can be found by others") : "",
                    }.Where(x => x.Length > 0)));
                Add(Loc.T("Str_NetworkAdapters_BtRowMaker", "Driver maker"), a.DriverMaker);
                Add(Loc.T("Str_NetworkAdapters_BtRowDriver", "Driver version"), a.DriverVersion + (a.DriverDate.Length > 0 ? $" ({a.DriverDate})" : ""));
            }
            return rows;
        }

        private static string BtStateText(BluetoothRadioState s) => s switch
        {
            BluetoothRadioState.On => Loc.T("Str_NetworkAdapters_BtOn", "On"),
            BluetoothRadioState.Off => Loc.T("Str_NetworkAdapters_BtOff", "Off"),
            BluetoothRadioState.Disabled => Loc.T("Str_NetworkAdapters_BtDisabled", "Disabled"),
            _ => Loc.T("Str_NetworkAdapters_BtUnknown", "Unknown"),
        };

        /// <summary>New list, same adapter still chosen (by its id, so a rename doesn't lose it). An open edit form is left as typed.</summary>
        private void ApplyList()
        {
            var all = WithBluetooth(_realAdapters);
            int connected = all.Count(a => a.State == AdapterState.Connected);
            TxtToolDetail.Text = all.Count == 0 ? "" : Loc.F("Str_NetworkAdapters_CountOn", "{0} of {1} on", connected, all.Count);
            BuildCategoryBar(all);
            var adapters = _adapters = _category == "All" ? all : all.Where(a => a.Category == _category).ToList();
            TxtNoAdapters.Text = all.Count == 0
                ? "No adapters found." + (ChkShowVirtual.IsChecked == true ? "" : " Tick \"Show virtual adapters\" on the left to list VPN and Hyper-V ones.")
                : "Nothing in this group.";
            TxtNoAdapters.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var keep = adapters.FirstOrDefault(a => a.Id == _selectedAdapterId) ?? adapters.FirstOrDefault();
            AdapterList.ItemsSource = adapters;
            AdapterList.SelectedItem = keep;
            if (keep == null) AdapterDetail.Visibility = Visibility.Collapsed;
            else ShowAdapter(keep, animate: false);
        }

        // ---------------- Bluetooth (read-only) ----------------

        /// <summary>One line of the Bluetooth card (a radio or a paired device); Tone is Good / Warn / Neutral.</summary>
        private sealed record BtRow(string Title, string Detail, string Extra, string Pill, string Tone, PackIconKind Icon);

        private static string BtKindName(string kind) => kind switch
        {
            "Headphones" => Loc.T("Str_NetworkAdapters_BtKind_Headphones", "Headphones"),
            "Speaker" => Loc.T("Str_NetworkAdapters_BtKind_Speaker", "Speaker"),
            "Microphone" => Loc.T("Str_NetworkAdapters_BtKind_Microphone", "Microphone"),
            "Phone" => Loc.T("Str_NetworkAdapters_BtKind_Phone", "Phone"),
            "Computer" => Loc.T("Str_NetworkAdapters_BtKind_Computer", "Computer"),
            "Keyboard" => Loc.T("Str_NetworkAdapters_BtKind_Keyboard", "Keyboard"),
            "Mouse" => Loc.T("Str_NetworkAdapters_BtKind_Mouse", "Mouse"),
            "Gamepad" => Loc.T("Str_NetworkAdapters_BtKind_Gamepad", "Game controller"),
            "Input" => Loc.T("Str_NetworkAdapters_BtKind_Input", "Keyboard or mouse"),
            "Watch" => Loc.T("Str_NetworkAdapters_BtKind_Watch", "Watch"),
            "Display" => Loc.T("Str_NetworkAdapters_BtKind_Display", "Display"),
            "Camera" => Loc.T("Str_NetworkAdapters_BtKind_Camera", "Camera"),
            "Car" => Loc.T("Str_NetworkAdapters_BtKind_Car", "Car audio"),
            "Health" => Loc.T("Str_NetworkAdapters_BtKind_Health", "Health sensor"),
            "Tag" => Loc.T("Str_NetworkAdapters_BtKind_Tag", "Tracker"),
            _ => "",
        };

        private static PackIconKind BtKindIcon(string kind) => kind switch
        {
            "Headphones" => PackIconKind.Headphones,
            "Speaker" => PackIconKind.Speaker,
            "Microphone" => PackIconKind.Microphone,
            "Phone" => PackIconKind.Cellphone,
            "Computer" => PackIconKind.Laptop,
            "Keyboard" or "Input" => PackIconKind.Keyboard,
            "Mouse" => PackIconKind.Mouse,
            "Gamepad" => PackIconKind.GamepadVariant,
            "Watch" => PackIconKind.Watch,
            "Display" => PackIconKind.Monitor,
            "Camera" => PackIconKind.Camera,
            "Car" => PackIconKind.Car,
            "Health" => PackIconKind.HeartPulse,
            "Tag" => PackIconKind.TagOutline,
            _ => PackIconKind.Bluetooth,
        };

        /// <summary>"Battery 80 % · Encrypted · Last seen 11/19/2026 10:20" - only what this device reported.</summary>
        private static string BtExtraLine(BluetoothDeviceInfo d)
        {
            var parts = new List<string>();
            if (d.Battery is int pct) parts.Add($"{Loc.T("Str_NetworkAdapters_BtBattery", "Battery")} {pct} %");
            string? protection = d.Protection switch
            {
                BluetoothProtection.None => Loc.T("Str_NetworkAdapters_BtNotEncrypted", "Not encrypted"),
                BluetoothProtection.Encrypted => Loc.T("Str_NetworkAdapters_BtEncrypted", "Encrypted"),
                BluetoothProtection.EncryptedAndVerified => Loc.T("Str_NetworkAdapters_BtVerified", "Encrypted and verified"),
                _ => null,
            };
            if (protection != null) parts.Add(protection);
            if (d.LastSeen is DateTimeOffset seen) parts.Add($"{Loc.T("Str_NetworkAdapters_BtLastSeen", "Last seen")} {seen.ToLocalTime():g}");
            return string.Join(" · ", parts);
        }

        private async Task RefreshBluetoothAsync()
        {
            _btCts?.Cancel();
            var cts = _btCts = new CancellationTokenSource();
            var ct = cts.Token;
            try
            {
                var info = await NetworkToolsManager.GetBluetoothAsync(ct);
                if (ct.IsCancellationRequested || _closed) return;

                _bt = info;
                BtDevices.ItemsSource = info.Devices.Select(d => new BtRow(
                    d.Name,
                    string.Join(" · ", new[]
                    {
                        BtKindName(d.Kind),
                        d.LowEnergy ? Loc.T("Str_NetworkAdapters_BtLowEnergy", "Low Energy") : Loc.T("Str_NetworkAdapters_BtClassic", "Classic"),
                        d.Address,
                    }.Where(x => x.Length > 0)),
                    BtExtraLine(d),
                    d.Connected ? Loc.T("Str_NetworkAdapters_BtConnected", "Connected") : Loc.T("Str_NetworkAdapters_BtNotConnected", "Not connected"),
                    d.Connected ? "Good" : "Neutral",
                    BtKindIcon(d.Kind))).ToList();

                TxtBtPairedLabel.Visibility = info.Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtBtMessage.Text = info.Problem switch
                {
                    BluetoothProblem.NoAdapter => Loc.T("Str_NetworkAdapters_BtNone", "No Bluetooth adapter was found on this computer."),
                    BluetoothProblem.Unavailable => Loc.T("Str_NetworkAdapters_BtUnavailable", "Windows did not answer about Bluetooth (the Bluetooth service may be stopped)."),
                    _ when info.Devices.Count == 0 => Loc.T("Str_NetworkAdapters_BtNoPaired", "No paired devices."),
                    _ => "",
                };
                TxtBtMessage.Visibility = TxtBtMessage.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (_realAdapters.Count > 0) ApplyList();   // the radio joins the list
            }
            catch (OperationCanceledException)
            {
                // superseded or the page closed
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log.Info($"Bluetooth section: {ex.Message}");
                TxtBtMessage.Text = Loc.T("Str_NetworkAdapters_BtUnavailable", "Windows did not answer about Bluetooth (the Bluetooth service may be stopped).");
                TxtBtMessage.Visibility = Visibility.Visible;
            }
            finally
            {
                if (ReferenceEquals(_btCts, cts)) _btCts = null;
                cts.Dispose();
            }
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
            AdapterInfoRows.ItemsSource = a.IsHardwareOnly ? a.ExtraRows
                : a.IsBluetoothRadio ? a.ExtraRows
                : NetworkToolsManager.DescribeAdapter(a);
            ShowBluetoothParts(a);

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

            if (a.IsHardwareOnly)
                IconAdapterState.Kind = a.State switch
                {
                    AdapterState.Connected => PackIconKind.CheckCircleOutline,
                    AdapterState.Disabled => PackIconKind.PowerPlugOff,
                    _ => PackIconKind.AlertCircleOutline,
                };
            if (a.IsBluetoothRadio) IconAdapterState.Kind = a.State == AdapterState.Connected ? PackIconKind.Bluetooth : PackIconKind.BluetoothOff;
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

        /// <summary>Bluetooth: its own tiles and the paired devices inside the adapter; every other adapter shows its network tiles.</summary>
        private void ShowBluetoothParts(AdapterInfo a)
        {
            bool radio = a.IsBluetoothRadio;
            NetTiles.Visibility = a.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
            BtTiles.Visibility = radio ? Visibility.Visible : Visibility.Collapsed;
            BtSection.Visibility = radio ? Visibility.Visible : Visibility.Collapsed;
            if (!radio) return;
            TxtBtState.Text = a.StatusText;
            TxtBtPairedCount.Text = (_bt?.Devices.Count ?? 0).ToString();
            TxtBtConnectedCount.Text = (_bt?.Devices.Count(d => d.Connected) ?? 0).ToString();
        }

        private void UpdateAdapterButtons()
        {
            bool radio = SelectedAdapter?.IsReadOnly == true;   // Bluetooth and the non-network adapters are read-only here
            BtnEditIp.Visibility = radio ? Visibility.Collapsed : Visibility.Visible;
            BtnToggleAdapter.Visibility = radio ? Visibility.Collapsed : Visibility.Visible;
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
            if (AppModal.Show(Loc.F("Str_MB_ChangeNN", "Change {0}?\n\n{1}{2}", a.Name, change, risk), Loc.T("Str_Dlg_NetworkTools", "Network Tools"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

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
                if (AppModal.Show(Loc.F("Str_MB_Disable", "Disable {0}?{1}", a.Name, warn), Loc.T("Str_Dlg_NetworkTools", "Network Tools"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            if (!_closed && !_adapterBusy) await RefreshIpInfoAsync(log: false);
            await Task.Delay(3500);
            if (!_closed && !_adapterBusy) await RefreshIpInfoAsync(log: false);
        }

    }
}
