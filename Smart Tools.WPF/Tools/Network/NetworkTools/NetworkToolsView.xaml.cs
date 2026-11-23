using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Network Tools view - UI only. Parsing, ping, the port scanner, IP
    /// info, adapters (NetworkToolsManager.Adapters.cs) and settings are in
    /// <see cref="NetworkToolsManager"/>; the TCP/UDP
    /// client is <see cref="SocketSession"/>. One tool shows at a time, and
    /// every tool writes to the shared console. Ping, a scan and a connection
    /// can all run at once.
    /// </summary>
    public partial class NetworkToolsView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private readonly ObservableCollection<ConsoleLine> _console = new();
        private NetworkSettings _settings = new();

        // Ping
        private CancellationTokenSource? _pingCts;
        private readonly List<PingSample> _pingSamples = new();
        private PingStats _pingStats = new();
        private string _pingHost = "";

        // Port scan
        private CancellationTokenSource? _scanCts;
        private readonly ObservableCollection<PortResult> _openPorts = new();

        // TCP / UDP
        private SocketSession? _session;
        private CancellationTokenSource? _connectCts;
        private StreamTextDecoder _decoder = new();
        private readonly List<string> _sendHistory = new();
        private int _historyIndex = -1;

        // IP info and adapters
        private CancellationTokenSource? _ipCts;
        private bool _ipLoadedOnce;
        private List<AdapterInfo> _adapters = new();
        private string? _selectedAdapterId;
        private bool _adapterBusy;
        private bool _adaptersStale;
        private bool _networkEventsHooked;
        private bool _closed;
        private readonly bool _isAdmin = NetworkToolsManager.IsAdministrator();

        /// <summary>Windows reports several changes for one plug-in; wait for them to settle, then read once.</summary>
        private readonly DispatcherTimer _networkChangedTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public NetworkToolsView()
        {
            InitializeComponent();
            ConsoleList.ItemsSource = _console;
            OpenPortsList.ItemsSource = _openPorts;
            _networkChangedTimer.Tick += NetworkChangedTimer_Tick;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops ping and scans and closes the connection.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            HookNetworkEvents(false);
            _networkChangedTimer.Stop();
            _pingCts?.Cancel();
            _scanCts?.Cancel();
            _connectCts?.Cancel();
            _ipCts?.Cancel();
            if (_session != null)
            {
                var s = _session;
                _session = null;
                _ = s.DisposeAsync().AsTask();
            }
        }

        /// <summary>Kept for existing callers - same as <see cref="CancelActiveRun"/>.</summary>
        public void CloseActiveSocket() => CancelActiveRun();

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private NetworkTool SelectedTool =>
            ToolTiles.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out NetworkTool t) ? t : NetworkTool.Ping;

        private SocketProtocol SelectedProtocol =>
            ProtocolChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out SocketProtocol p) ? p : SocketProtocol.Tcp;

        private static void CheckTag(System.Windows.Controls.Panel panel, string tag)
        {
            foreach (var r in panel.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == tag;
        }

        private static int CheckedInt(System.Windows.Controls.Panel panel, int fallback) =>
            panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && int.TryParse(s, out int v) ? v : fallback;

        // =================================================================
        //  Loading, layout, settings
        // =================================================================

        private async void NetworkToolsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(NetworkToolsManager.LoadSettings());
                _loadedOnce = true;
            }
            UpdateToolUi(animate: false);
            UpdateConsoleEmpty();
            HookNetworkEvents(true);

            // Once per session (the old view asked ipify again on every visit to this page).
            if (!_ipLoadedOnce)
            {
                _ipLoadedOnce = true;
                await RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
            }
        }

        /// <summary>NetworkChange's events are static: unhooked when the page goes, or the page would never be freed.</summary>
        private void NetworkToolsView_Unloaded(object sender, RoutedEventArgs e)
        {
            HookNetworkEvents(false);
            _networkChangedTimer.Stop();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        private void ApplySettings(NetworkSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                CheckTag(ToolTiles, s.Tool.ToString());
                TxtPingHost.Text = s.PingHost;
                CheckTag(PingCountChips, s.PingCount.ToString(CultureInfo.InvariantCulture));
                TxtScanHost.Text = s.ScanHost;
                TxtScanPorts.Text = s.ScanPorts;
                CheckTag(ScanSpeedChips, s.ScanTimeoutMs.ToString(CultureInfo.InvariantCulture));
                CheckTag(ProtocolChips, s.Protocol.ToString());
                TxtSocketHost.Text = s.SocketHost;
                TxtSocketPort.Text = s.SocketPort.ToString(CultureInfo.InvariantCulture);
                TxtSocketLocalPort.Text = s.LocalPort.ToString(CultureInfo.InvariantCulture);
                ChkAppendNewline.IsChecked = s.AppendNewline;
                ChkSendHex.IsChecked = s.SendHex;
                ChkShowHex.IsChecked = s.ShowReceivedHex;
                ChkLookUpPublicIp.IsChecked = s.LookUpPublicIp;
                ChkShowVirtual.IsChecked = s.ShowVirtualAdapters;
                UpdatePortsHint();
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.Tool = SelectedTool;
            _settings.PingHost = TxtPingHost.Text.Trim();
            _settings.PingCount = CheckedInt(PingCountChips, 4);
            _settings.ScanHost = TxtScanHost.Text.Trim();
            _settings.ScanPorts = TxtScanPorts.Text.Trim();
            _settings.ScanTimeoutMs = CheckedInt(ScanSpeedChips, 800);
            _settings.Protocol = SelectedProtocol;
            _settings.SocketHost = TxtSocketHost.Text.Trim();
            if (NetworkToolsManager.TryPort(TxtSocketPort.Text, out int port)) _settings.SocketPort = port;
            _settings.LocalPort = int.TryParse(TxtSocketLocalPort.Text, out int lp) ? Math.Clamp(lp, 0, 65535) : 0;
            _settings.AppendNewline = ChkAppendNewline.IsChecked == true;
            _settings.SendHex = ChkSendHex.IsChecked == true;
            _settings.ShowReceivedHex = ChkShowHex.IsChecked == true;
            _settings.LookUpPublicIp = ChkLookUpPublicIp.IsChecked == true;
            _settings.ShowVirtualAdapters = ChkShowVirtual.IsChecked == true;
            try
            {
                NetworkToolsManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Smart_Tools.WPF.Log.Info($"Network settings not saved: {ex.Message}");
            }
        }

        private void Setting_LostFocus(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (sender == ChkSendHex) UpdateSendPlaceholder();
            if (sender == ChkShowVirtual) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: false);
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        // =================================================================
        //  Tool switching
        // =================================================================

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateToolUi(animate: true);
            SaveSettings();
        }

        /// <summary>Shows the chosen tool's sidebar panel and live view, and sets the action button.</summary>
        private void UpdateToolUi(bool animate)
        {
            var tool = SelectedTool;
            void Show(FrameworkElement el, bool visible)
            {
                var v = visible ? Visibility.Visible : Visibility.Collapsed;
                if (el.Visibility == v) return;
                el.Visibility = v;
                if (visible && animate) UiMotion.FadeIn(el, 4);
            }
            Show(PanelPing, tool == NetworkTool.Ping);
            Show(PanelPorts, tool == NetworkTool.Ports);
            Show(PanelSocket, tool == NetworkTool.Socket);
            Show(PanelIpInfo, tool == NetworkTool.IpInfo);
            Show(ViewPing, tool == NetworkTool.Ping);
            Show(ViewPorts, tool == NetworkTool.Ports);
            Show(ViewSocket, tool == NetworkTool.Socket);
            Show(ViewIpInfo, tool == NetworkTool.IpInfo);
            PanelUdpLocalPort.Visibility = SelectedProtocol == SocketProtocol.Udp ? Visibility.Visible : Visibility.Collapsed;

            // The adapter view needs room: the tool card grows and the console keeps what's left.
            bool ip = tool == NetworkTool.IpInfo;
            ToolRow.Height = ip ? new GridLength(3, GridUnitType.Star) : GridLength.Auto;
            ConsoleRow.MinHeight = ip ? 120 : 0;
            if (ip && _adaptersStale && _ipLoadedOnce && !_adapterBusy) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: false);

            UpdateActionButton();
            UpdateToolText();
            UpdateStatePill();
            UpdateSendPlaceholder();
        }

        /// <summary>The one action button: Ping/Stop, Scan/Stop, Connect/Disconnect, Refresh.</summary>
        private void UpdateActionButton()
        {
            (PackIconKind icon, string label, string tip) = SelectedTool switch
            {
                NetworkTool.Ping => _pingCts != null
                    ? (PackIconKind.StopCircleOutline, "Stop", "Stop pinging")
                    : (PackIconKind.PlayCircleOutline, "Ping", "Start pinging the host"),
                NetworkTool.Ports => _scanCts != null
                    ? (PackIconKind.StopCircleOutline, "Stop", "Stop the scan (keeps what was found)")
                    : (PackIconKind.MagnifyScan, "Scan", "Check which ports are open"),
                NetworkTool.Socket => _session != null || _connectCts != null
                    ? (PackIconKind.LanDisconnect, _connectCts != null ? "Cancel" : (SelectedProtocol == SocketProtocol.Tcp ? "Disconnect" : "Close"), "Close the connection")
                    : (PackIconKind.LanConnect, SelectedProtocol == SocketProtocol.Tcp ? "Connect" : "Open", "Connect to the host and port"),
                _ => (PackIconKind.Refresh, "Refresh", "Read this computer's addresses again"),
            };
            IconAction.Kind = icon;
            TxtActionLabel.Text = label;
            BtnAction.ToolTip = tip;
        }

        private void UpdateToolText()
        {
            switch (SelectedTool)
            {
                case NetworkTool.Ping:
                    TxtToolTitle.Text = _pingSamples.Count == 0 ? "Ping" : $"Ping {_pingHost}";
                    TxtToolDetail.Text = _pingSamples.Count == 0
                        ? "Checks a host answers, and how quickly."
                        : _pingCts != null ? $"Pinging... {_pingStats.Sent} sent" : NetworkToolsManager.DescribeStats(_pingHost, _pingStats);
                    break;
                case NetworkTool.Ports:
                    if (_scanCts == null && ProgressScan.Value == 0)
                    {
                        TxtToolTitle.Text = "Port scan";
                        TxtToolDetail.Text = "Finds open TCP ports - only scan machines you own or may test.";
                    }
                    break;
                case NetworkTool.Socket:
                    TxtToolTitle.Text = _session != null
                        ? $"{(_session.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP")} {_session.Host}:{_session.Port}"
                        : "TCP / UDP client";
                    TxtToolDetail.Text = _session == null
                        ? "Send text or bytes to a device or server and see what it answers."
                        : _session.Protocol == SocketProtocol.Udp
                            ? $"Listening on local port {_session.LocalPort} · replies from anyone appear below"
                            : $"Local port {_session.LocalPort}" + (_session.Remote != null ? $" · {_session.Remote}" : "");
                    break;
                default:
                    TxtToolTitle.Text = "This computer";
                    TxtToolDetail.Text = "Every network adapter - pick one to see its addresses, change its IP settings, or turn it off and on.";
                    break;
            }
        }

        /// <summary>Connection state pill, themed by resource key (the old one set brushes once and ignored theme changes).</summary>
        private void UpdateStatePill()
        {
            (string text, string bg, string fg, PackIconKind icon) =
                _connectCts != null ? ("Connecting...", "StatusInfoBg", "StatusInfoFg", PackIconKind.LanPending) :
                _session != null ? (_session.Protocol == SocketProtocol.Tcp ? "Connected" : $"UDP open · port {_session.LocalPort}", "StatusSuccessBg", "StatusSuccessFg", PackIconKind.LanConnect) :
                (TryFindResource("Str_Network_Disconnected") as string ?? "Disconnected", "RowBg", "TextSecondary", PackIconKind.LanDisconnect);
            TxtState.Text = text;
            StatePill.SetResourceReference(Border.BackgroundProperty, bg);
            TxtState.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconState.SetResourceReference(ForegroundProperty, fg);
            IconState.Kind = icon;
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            switch (SelectedTool)
            {
                case NetworkTool.Ping: _ = TogglePingAsync(); break;
                case NetworkTool.Ports: _ = ToggleScanAsync(); break;
                case NetworkTool.Socket: _ = ToggleSocketAsync(); break;
                default: _ = RefreshIpInfoAsync(log: true, lookUpPublicIp: true); break;
            }
        }

        /// <summary>Enter in a host/port box runs that tool.</summary>
        private void HostBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            SaveSettings();
            BtnAction_Click(sender, e);
            e.Handled = true;
        }

        // =================================================================
        //  Console
        // =================================================================

        private void Log(ConsoleKind kind, string source, string text)
        {
            var scroller = FindScrollViewer(ConsoleList);
            bool atBottom = scroller == null || scroller.VerticalOffset >= scroller.ScrollableHeight - 4;

            var line = new ConsoleLine(DateTime.Now, kind, source, text);
            _console.Add(line);
            while (_console.Count > NetworkToolsManager.MaxConsoleLines) _console.RemoveAt(0);

            TxtLastActivity.Text = $"Last activity {line.TimeText}";
            UpdateConsoleEmpty();

            // Follow new lines only when already at the bottom - reading older lines isn't interrupted.
            if (atBottom) ConsoleList.ScrollIntoView(line);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is ScrollViewer found) return found;
            return null;
        }

        private void UpdateConsoleEmpty() =>
            ConsoleEmpty.Visibility = _console.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void BtnClearConsole_Click(object sender, RoutedEventArgs e)
        {
            _console.Clear();
            TxtLastActivity.SetResourceReference(TextBlock.TextProperty, "Str_Network_NoActivityYet");
            UpdateConsoleEmpty();
        }

        private string ConsoleText(IEnumerable<ConsoleLine> lines) => string.Join(Environment.NewLine, lines.Select(l => l.ToString()));

        private void BtnCopyConsole_Click(object sender, RoutedEventArgs e) => CopyConsole();

        private void ConsoleList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CopyConsole();
                e.Handled = true;
            }
        }

        private void CopyConsole()
        {
            var selected = ConsoleList.SelectedItems.Cast<ConsoleLine>().OrderBy(l => _console.IndexOf(l)).ToList();
            var lines = selected.Count > 0 ? selected : _console.ToList();
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(ConsoleText(lines));
            }
            catch (Exception ex)
            {
                Smart_Tools.WPF.Log.Info($"Clipboard busy: {ex.Message}");
            }
        }

        private void BtnSaveConsole_Click(object sender, RoutedEventArgs e)
        {
            if (_console.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save console",
                Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log",
                FileName = $"Network log {DateTime.Now:yyyy-MM-dd HH-mm}.txt",
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                AtomicFile.WriteAllText(dialog.FileName, ConsoleText(_console) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log(ConsoleKind.Error, "LOG", "Couldn't save: " + ex.Message);
            }
        }

        // =================================================================
        //  Ping
        // =================================================================

        private void PingCount_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        private async Task TogglePingAsync()
        {
            if (_pingCts != null)
            {
                _pingCts.Cancel();
                return;
            }

            string? host = NetworkToolsManager.CleanHost(TxtPingHost.Text);
            if (host == null)
            {
                Log(ConsoleKind.Warning, "PING", "Enter a host name or IP address first.");
                TxtPingHost.Focus();
                return;
            }
            TxtPingHost.Text = host;
            SaveSettings();

            int count = CheckedInt(PingCountChips, 4);
            _pingHost = host;
            _pingSamples.Clear();
            _pingStats = new PingStats();
            UpdatePingView();

            var cts = _pingCts = new CancellationTokenSource();
            UpdateActionButton();
            UpdateToolText();
            UiMotion.Pop(BtnAction, 0.96);
            Log(ConsoleKind.Info, "PING", count == 0 ? $"Pinging {host} until stopped..." : $"Pinging {host} {count} times...");

            var progress = new Progress<PingSample>(s =>
            {
                _pingSamples.Add(s);
                _pingStats.Add(s);
                Log(s.Success ? ConsoleKind.Received : ConsoleKind.Warning, "PING", NetworkToolsManager.DescribePing(s));
                UpdatePingView();
                UpdateToolText();
            });

            try
            {
                await NetworkToolsManager.PingAsync(host, count, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                Log(ConsoleKind.Error, "PING", ex.Message);
            }
            finally
            {
                _pingCts = null;
                cts.Dispose();
                if (_pingStats.Sent > 0)
                    Log(_pingStats.Received == 0 ? ConsoleKind.Error : _pingStats.Lost > 0 ? ConsoleKind.Warning : ConsoleKind.Success, "PING",
                        NetworkToolsManager.DescribeStats(host, _pingStats));
                UpdateActionButton();
                UpdateToolText();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        private void UpdatePingView()
        {
            var st = _pingStats;
            TxtStatReplies.Text = st.Sent == 0 ? "–" : $"{st.Received} / {st.Sent}";
            TxtStatLoss.Text = st.Sent == 0 ? "–" : $"{st.LossPercent:0}%";
            TxtStatLoss.SetResourceReference(TextBlock.ForegroundProperty, st.Lost > 0 ? "StatusErrorFg" : "TextPrimary");
            TxtStatAvg.Text = NetworkText.Ms(st.Average);
            TxtStatMinMax.Text = st.Min is long mn ? $"{mn} / {st.Max}" : "–";
            TxtStatJitter.Text = NetworkText.Ms(st.Jitter);
            LatencyStrip.ItemsSource = NetworkToolsManager.LatencyBars(_pingSamples);
            TxtLatencyEmpty.Visibility = _pingSamples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Port scan
        // =================================================================

        private void PortPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            TxtScanPorts.Text = tag;
            SaveSettings();
        }

        private void ScanSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdatePortsHint();
            SaveSettings();
        }

        private void TxtScanPorts_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdatePortsHint();
        }

        /// <summary>"1,024 ports · about 5 s" - so a full scan's length isn't a surprise.</summary>
        private void UpdatePortsHint()
        {
            var (ports, error) = NetworkToolsManager.ParsePorts(TxtScanPorts.Text);
            if (error != null)
            {
                TxtPortsHint.Text = error;
                return;
            }
            int timeout = CheckedInt(ScanSpeedChips, 800);
            double worst = Math.Ceiling(ports.Count / (double)NetworkToolsManager.DefaultScanConcurrency) * timeout / 1000.0;
            TxtPortsHint.Text = $"{ports.Count:N0} port{(ports.Count == 1 ? "" : "s")} · up to about {(worst < 60 ? $"{Math.Max(1, worst):0} s" : $"{worst / 60:0.#} min")} (less when ports answer quickly)";
        }

        private async Task ToggleScanAsync()
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
                Log(ConsoleKind.Warning, "SCAN", host == null ? "Enter a host name or IP address first." : error!);
                (host == null ? TxtScanHost : TxtScanPorts).Focus();
                return;
            }
            TxtScanHost.Text = host;
            SaveSettings();

            int timeout = CheckedInt(ScanSpeedChips, 800);
            _openPorts.Clear();
            ProgressScan.Value = 0;
            TxtNoOpenPorts.Visibility = Visibility.Visible;
            TxtToolTitle.Text = $"Scanning {host}";
            TxtToolDetail.Text = $"0 of {ports.Count:N0} ports checked";

            var cts = _scanCts = new CancellationTokenSource();
            UpdateActionButton();
            UiMotion.Pop(BtnAction, 0.96);
            Log(ConsoleKind.Info, "SCAN", $"Scanning {host}: {ports.Count:N0} port{(ports.Count == 1 ? "" : "s")}...");

            int lastChecked = 0;
            var clock = Stopwatch.StartNew();
            var progress = new Progress<PortScanProgress>(p =>
            {
                lastChecked = p.Checked;
                ProgressScan.Value = p.Total == 0 ? 0 : p.Checked * 100.0 / p.Total;
                if (SelectedTool == NetworkTool.Ports)
                    TxtToolDetail.Text = $"{p.Checked:N0} of {p.Total:N0} ports checked · {p.Open} open";
            });

            void OnOpen(PortResult r) => Dispatcher.InvokeAsync(() =>
            {
                int i = 0;
                while (i < _openPorts.Count && _openPorts[i].Port < r.Port) i++;
                _openPorts.Insert(i, r);
                TxtNoOpenPorts.Visibility = Visibility.Collapsed;
                Log(ConsoleKind.Success, "SCAN", $"Port {r.Label} is open ({r.LatencyMs} ms)");
            });

            try
            {
                var open = await NetworkToolsManager.ScanPortsAsync(host, ports, timeout, progress, OnOpen, cts.Token);
                bool cancelled = cts.IsCancellationRequested;
                string summary = NetworkToolsManager.DescribeScan(host, ports.Count, cancelled ? lastChecked : ports.Count, open, clock.Elapsed, cancelled);
                Log(open.Count > 0 ? ConsoleKind.Success : ConsoleKind.Info, "SCAN", summary);
                TxtToolTitle.Text = cancelled ? $"Scan of {host} stopped" : $"Scanned {host}";
                TxtToolDetail.Text = $"{(cancelled ? lastChecked : ports.Count):N0} of {ports.Count:N0} ports checked · {open.Count} open";
                if (!cancelled) ProgressScan.Value = 100;
            }
            catch (OperationCanceledException)
            {
                Log(ConsoleKind.Info, "SCAN", "Stopped.");
            }
            catch (Exception ex)
            {
                Log(ConsoleKind.Error, "SCAN", ex.Message);
                TxtToolTitle.Text = "Port scan";
                TxtToolDetail.Text = ex.Message;
            }
            finally
            {
                _scanCts = null;
                cts.Dispose();
                UpdateActionButton();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        // =================================================================
        //  TCP / UDP
        // =================================================================

        private void Protocol_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UiMotion.FadeVisibility(PanelUdpLocalPort, SelectedProtocol == SocketProtocol.Udp);
            UpdateActionButton();
            SaveSettings();
        }

        private void UpdateSendPlaceholder() =>
            TxtSocketSend.Tag = ChkSendHex.IsChecked == true
                ? "Hex bytes, e.g. 48 65 6C 6C 6F - Enter sends"
                : "Type a message - Enter sends, ↑ ↓ recall earlier ones";

        private async Task ToggleSocketAsync()
        {
            if (_connectCts != null)
            {
                _connectCts.Cancel();
                return;
            }
            if (_session != null)
            {
                await CloseSessionAsync(byUser: true);
                return;
            }

            string? host = NetworkToolsManager.CleanHost(TxtSocketHost.Text);
            bool portOk = NetworkToolsManager.TryPort(TxtSocketPort.Text, out int port);
            int localPort = 0;
            bool localOk = SelectedProtocol == SocketProtocol.Tcp || TxtSocketLocalPort.Text.Trim().Length == 0
                           || (int.TryParse(TxtSocketLocalPort.Text.Trim(), out localPort) && localPort is >= 0 and <= 65535);
            if (host == null || !portOk || !localOk)
            {
                Log(ConsoleKind.Warning, SourceTag, host == null ? "Enter a host name or IP address." : !portOk ? "Enter a port from 1 to 65535." : "The local port must be 0-65535 (0 = any free port).");
                (host == null ? TxtSocketHost : !portOk ? TxtSocketPort : TxtSocketLocalPort).Focus();
                return;
            }
            TxtSocketHost.Text = host;
            SaveSettings();

            var protocol = SelectedProtocol;
            var cts = _connectCts = new CancellationTokenSource();
            SetSocketFieldsEnabled(false);
            UpdateActionButton();
            UpdateStatePill();
            Log(ConsoleKind.Info, SourceTag, protocol == SocketProtocol.Tcp ? $"Connecting to {host}:{port}..." : $"Opening a UDP socket for {host}:{port}...");

            try
            {
                var session = protocol == SocketProtocol.Tcp
                    ? await SocketSession.ConnectTcpAsync(host, port, TimeSpan.FromSeconds(10), cts.Token)
                    : await SocketSession.OpenUdpAsync(host, port, localPort, cts.Token);
                _decoder = new StreamTextDecoder();
                session.Received += Session_Received;
                session.Closed += Session_Closed;
                _session = session;
                Log(ConsoleKind.Success, SourceTag, protocol == SocketProtocol.Tcp
                    ? $"Connected to {session.Remote} (local port {session.LocalPort})"
                    : $"Socket open on local port {session.LocalPort}, sending to {session.Remote}");
                TxtSocketSend.Focus();
            }
            catch (OperationCanceledException)
            {
                Log(ConsoleKind.Info, SourceTag, "Cancelled.");
                SetSocketFieldsEnabled(true);
            }
            catch (Exception ex)
            {
                string reason = ex is SocketException se ? SocketSession.Friendly(se) : ex.Message;
                Log(ConsoleKind.Error, SourceTag, (protocol == SocketProtocol.Tcp ? "Couldn't connect: " : "Couldn't open the socket: ") + reason);
                SetSocketFieldsEnabled(true);
            }
            finally
            {
                _connectCts = null;
                cts.Dispose();
                UpdateActionButton();
                UpdateStatePill();
                UpdateToolText();
                UpdateTraffic();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        private string SourceTag => (_session?.Protocol ?? SelectedProtocol) == SocketProtocol.Tcp ? "TCP" : "UDP";

        /// <summary>Background thread - marshalled without blocking (the old read loop used Dispatcher.Invoke).</summary>
        private void Session_Received(SocketSession session, byte[] data, IPEndPoint? from)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(session, _session)) return;
                string text = ChkShowHex.IsChecked == true ? NetworkToolsManager.FormatHex(data) : NetworkToolsManager.Printable(_decoder.Decode(data));
                string who = session.Protocol == SocketProtocol.Udp && from != null ? $"{from}  " : "";
                Log(ConsoleKind.Received, SourceTag, $"◀ {who}{text}");
                UpdateTraffic();
            });   // same priority as Closed, so the last data isn't dropped behind the close
        }

        private void Session_Closed(SocketSession session, string? reason)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Only the current session may reset the UI - a late close from an old one used to drop a new connection.
                if (!ReferenceEquals(session, _session)) return;
                _session = null;
                Log(reason == null ? ConsoleKind.Info : ConsoleKind.Warning, session.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP",
                    reason == null ? "Disconnected." : "Disconnected: " + reason);
                AfterSessionClosed();
            });
        }

        private async Task CloseSessionAsync(bool byUser)
        {
            var s = _session;
            if (s == null) return;
            _session = null;
            s.Received -= Session_Received;
            s.Closed -= Session_Closed;
            await s.DisposeAsync();
            if (byUser) Log(ConsoleKind.Info, s.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP", "Disconnected.");
            AfterSessionClosed();
        }

        private void AfterSessionClosed()
        {
            SetSocketFieldsEnabled(true);
            UpdateActionButton();
            UpdateStatePill();
            UpdateToolText();
            UiMotion.Pop(StatePill, 0.9);
        }

        private void SetSocketFieldsEnabled(bool enabled)
        {
            ProtocolChips.IsEnabled = enabled;
            TxtSocketHost.IsEnabled = enabled;
            TxtSocketPort.IsEnabled = enabled;
            TxtSocketLocalPort.IsEnabled = enabled;
        }

        private void UpdateTraffic() =>
            TxtTraffic.Text = _session == null
                ? "↑ 0 B   ↓ 0 B"
                : $"↑ {NetworkText.FormatBytes(_session.BytesSent)} sent   ↓ {NetworkText.FormatBytes(_session.BytesReceived)} received";

        private void BtnSocketSend_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

        private void TxtSocketSend_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    _ = SendAsync();
                    e.Handled = true;
                    break;
                case Key.Up when _sendHistory.Count > 0:
                    _historyIndex = _historyIndex < 0 ? _sendHistory.Count - 1 : Math.Max(0, _historyIndex - 1);
                    SetSendText(_sendHistory[_historyIndex]);
                    e.Handled = true;
                    break;
                case Key.Down when _historyIndex >= 0:
                    _historyIndex++;
                    if (_historyIndex >= _sendHistory.Count)
                    {
                        _historyIndex = -1;
                        SetSendText("");
                    }
                    else
                    {
                        SetSendText(_sendHistory[_historyIndex]);
                    }
                    e.Handled = true;
                    break;
            }
        }

        private void SetSendText(string text)
        {
            TxtSocketSend.Text = text;
            TxtSocketSend.CaretIndex = text.Length;
        }

        private async Task SendAsync()
        {
            var session = _session;
            if (session == null)
            {
                // The old view logged "> text" as if it was sent even while still connecting.
                Log(ConsoleKind.Warning, SourceTag, _connectCts != null ? "Still connecting - wait a moment." : "Not connected - press Connect first.");
                UiMotion.Pop(BtnAction, 0.95);
                return;
            }

            string input = TxtSocketSend.Text;
            var (data, display, error) = NetworkToolsManager.BuildPayload(input, ChkSendHex.IsChecked == true, ChkAppendNewline.IsChecked == true);
            if (error != null)
            {
                Log(ConsoleKind.Warning, SourceTag, error);
                return;
            }

            try
            {
                await session.SendAsync(data);
                Log(ConsoleKind.Sent, SourceTag, "▶ " + display);
                if (_sendHistory.Count == 0 || _sendHistory[^1] != input) _sendHistory.Add(input);
                if (_sendHistory.Count > 50) _sendHistory.RemoveAt(0);
                _historyIndex = -1;
                TxtSocketSend.Clear();
                UpdateTraffic();
            }
            catch (Exception ex)
            {
                string reason = ex is SocketException se ? SocketSession.Friendly(se) : ex.InnerException is SocketException ise ? SocketSession.Friendly(ise) : ex.Message;
                Log(ConsoleKind.Error, SourceTag, "Send failed: " + reason);
            }
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
            // Only read while the adapter view is showing; otherwise read when it's next opened.
            if (SelectedTool == NetworkTool.IpInfo && !_adapterBusy) _ = RefreshIpInfoAsync(log: false, lookUpPublicIp: true);
            else _adaptersStale = true;
        }

        private async Task RefreshIpInfoAsync(bool log, bool lookUpPublicIp)
        {
            _ipCts?.Cancel();
            var cts = _ipCts = new CancellationTokenSource();
            BtnAction.IsEnabled = SelectedTool != NetworkTool.IpInfo;
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
                    Log(ConsoleKind.Info, "IP", $"{TxtHostname.Text} · " + (adapters.Count == 0 ? "no adapters" :
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
                        if (log) Log(ConsoleKind.Info, "IP", $"Public address: {TxtPublicIp.Text}");
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
                Log(ConsoleKind.Error, "IP", "Couldn't read the network adapters: " + ex.Message);
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
            if (AppModal.Show(Loc.F("Str_MB_ChangeNN", "Change {0}?\n\n{1}{2}", a.Name, change, risk), Loc.T("Str_Dlg_NetworkTools", "Network Tools"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            Log(ConsoleKind.Info, "IP", "Applying: " + change);
            var result = await RunAdapterChangeAsync(NetworkToolsManager.NetshCommands(a.Name, edit));
            if (result.Ok)
            {
                Log(ConsoleKind.Success, "IP", $"{a.Name}: settings changed.");
                UiMotion.FadeVisibility(EditPanel, false);
            }
            else if (result.Cancelled)
            {
                Log(ConsoleKind.Info, "IP", result.Message);
            }
            else
            {
                Log(ConsoleKind.Error, "IP", $"{a.Name}: {result.Message}");
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

            Log(ConsoleKind.Info, "IP", $"{(enable ? "Enabling" : "Disabling")} {a.Name}...");
            var result = await RunAdapterChangeAsync(NetworkToolsManager.NetshEnableCommands(a.Name, enable));
            if (result.Ok) Log(ConsoleKind.Success, "IP", $"{a.Name} {(enable ? "enabled - it can take a few seconds to connect." : "disabled.")}");
            else Log(result.Cancelled ? ConsoleKind.Info : ConsoleKind.Error, "IP", result.Cancelled ? result.Message : $"{a.Name}: {result.Message}");
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
