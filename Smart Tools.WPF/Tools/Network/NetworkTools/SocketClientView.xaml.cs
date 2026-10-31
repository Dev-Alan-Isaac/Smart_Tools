using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// TCP / UDP Client - UI only. The connection is a <see cref="SocketSession"/>
    /// (NetworkToolsManager.Socket.cs), bound to the chosen adapter when one is
    /// picked; payloads and hex are in <see cref="NetworkToolsManager"/>.
    /// Settings are saved field by field.
    /// </summary>
    public partial class SocketClientView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;

        private SocketSession? _session;
        private CancellationTokenSource? _connectCts;
        private StreamTextDecoder _decoder = new();
        private readonly List<string> _sendHistory = new();
        private int _historyIndex = -1;
        private SourceChoice _sentFrom = NetworkToolsManager.AutomaticSource;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;
        private readonly bool _constructed;

        public SocketClientView()
        {
            InitializeComponent();
            SourcePicker.SelectionChanged += SourcePicker_SelectionChanged;
            _constructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: cancels a connect and closes the connection.</summary>
        public void CancelActiveRun()
        {
            _connectCts?.Cancel();
            if (_session != null)
            {
                var s = _session;
                _session = null;
                _ = s.DisposeAsync().AsTask();
            }
        }

        private SocketProtocol SelectedProtocol =>
            ProtocolChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out SocketProtocol p) ? p : SocketProtocol.Tcp;

        private string SourceTag => (_session?.Protocol ?? SelectedProtocol) == SocketProtocol.Tcp ? "TCP" : "UDP";

        // =================================================================
        //  Loading, layout, settings
        // =================================================================

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                var s = NetworkToolsManager.LoadSettings();
                _applying = true;
                try
                {
                    foreach (var r in ProtocolChips.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == s.Protocol.ToString();
                    TxtSocketHost.Text = s.SocketHost;
                    TxtSocketPort.Text = s.SocketPort.ToString(CultureInfo.InvariantCulture);
                    TxtSocketLocalPort.Text = s.LocalPort.ToString(CultureInfo.InvariantCulture);
                    ChkAppendNewline.IsChecked = s.AppendNewline;
                    ChkSendHex.IsChecked = s.SendHex;
                    ChkShowHex.IsChecked = s.ShowReceivedHex;
                    SourcePicker.SavedId = s.SocketSourceId;
                }
                finally
                {
                    _applying = false;
                }
            }
            PanelUdpLocalPort.Visibility = SelectedProtocol == SocketProtocol.Udp ? Visibility.Visible : Visibility.Collapsed;
            UpdateSendPlaceholder();
            UpdateUi();
            UpdateTraffic();
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            // The connection stays open while you look at another tool; Window_Closing closes it.
        }

        private void Save(Action<NetworkSettings> change)
        {
            if (_applying || !_loadedOnce) return;
            try
            {
                NetworkToolsManager.UpdateSettings(change);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Info($"TCP/UDP settings not saved: {ex.Message}");
            }
        }

        private void SaveFields()
        {
            var protocol = SelectedProtocol;
            string host = TxtSocketHost.Text.Trim();
            bool portOk = NetworkToolsManager.TryPort(TxtSocketPort.Text, out int port);
            int local = int.TryParse(TxtSocketLocalPort.Text, out int lp) ? Math.Clamp(lp, 0, 65535) : 0;
            bool crlf = ChkAppendNewline.IsChecked == true, sendHex = ChkSendHex.IsChecked == true, showHex = ChkShowHex.IsChecked == true;
            Save(s =>
            {
                s.Protocol = protocol;
                s.SocketHost = host;
                if (portOk) s.SocketPort = port;
                s.LocalPort = local;
                s.AppendNewline = crlf;
                s.SendHex = sendHex;
                s.ShowReceivedHex = showHex;
            });
        }

        private void Setting_LostFocus(object sender, RoutedEventArgs e) => SaveFields();

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_constructed || _applying) return;
            SaveFields();
            if (sender == ChkSendHex) UpdateSendPlaceholder();
        }

        private void SourcePicker_SelectionChanged(SourceChoice choice)
        {
            string id = SourcePicker.SavedId;
            Save(s => s.SocketSourceId = id);
            UpdateUi();
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        private void Protocol_Click(object sender, RoutedEventArgs e)
        {
            if (_applying) return;
            UiMotion.FadeVisibility(PanelUdpLocalPort, SelectedProtocol == SocketProtocol.Udp);
            UpdateUi();
            SaveFields();
        }

        private void HostBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            SaveFields();
            _ = ToggleAsync();
            e.Handled = true;
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e) => _ = ToggleAsync();

        private void UpdateSendPlaceholder() =>
            TxtSocketSend.Tag = ChkSendHex.IsChecked == true
                ? "Hex bytes, e.g. 48 65 6C 6C 6F - Enter sends"
                : "Type a message - Enter sends, ↑ ↓ recall earlier ones";

        // =================================================================
        //  Connect / close
        // =================================================================

        private async Task ToggleAsync()
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
                Results.Log(ConsoleKind.Warning, SourceTag, host == null ? "Enter a host name or IP address." : !portOk ? "Enter a port from 1 to 65535." : "The local port must be 0-65535 (0 = any free port).");
                (host == null ? TxtSocketHost : !portOk ? TxtSocketPort : TxtSocketLocalPort).Focus();
                return;
            }
            TxtSocketHost.Text = host;
            SaveFields();

            var protocol = SelectedProtocol;
            _sentFrom = SourcePicker.Selected;
            string from = _sentFrom.IsAutomatic ? "" : " " + _sentFrom.FromText;
            var cts = _connectCts = new CancellationTokenSource();
            SetFieldsEnabled(false);
            UpdateUi();
            Results.Log(ConsoleKind.Info, SourceTag, protocol == SocketProtocol.Tcp ? $"Connecting to {host}:{port}{from}..." : $"Opening a UDP socket for {host}:{port}{from}...");

            try
            {
                var session = protocol == SocketProtocol.Tcp
                    ? await SocketSession.ConnectTcpAsync(host, port, TimeSpan.FromSeconds(10), cts.Token, _sentFrom.Address)
                    : await SocketSession.OpenUdpAsync(host, port, localPort, cts.Token, _sentFrom.Address);
                _decoder = new StreamTextDecoder();
                session.Received += Session_Received;
                session.Closed += Session_Closed;
                _session = session;
                Results.Log(ConsoleKind.Success, SourceTag, protocol == SocketProtocol.Tcp
                    ? $"Connected to {session.Remote} (local {(session.Source != null ? session.Source + ":" : "port ")}{session.LocalPort})"
                    : $"Socket open on local {(session.Source != null ? session.Source + ":" : "port ")}{session.LocalPort}, sending to {session.Remote}");
                TxtSocketSend.Focus();
            }
            catch (OperationCanceledException)
            {
                Results.Log(ConsoleKind.Info, SourceTag, "Cancelled.");
                SetFieldsEnabled(true);
            }
            catch (Exception ex)
            {
                string reason = ex is SocketException se ? SocketSession.Friendly(se) : ex.Message;
                if (ex is SocketException { SocketErrorCode: SocketError.AddressNotAvailable } && !_sentFrom.IsAutomatic)
                    reason = $"{_sentFrom.Label} ({_sentFrom.Address}) isn't connected any more - pick another adapter under Send from.";
                Results.Log(ConsoleKind.Error, SourceTag, (protocol == SocketProtocol.Tcp ? "Couldn't connect: " : "Couldn't open the socket: ") + reason);
                SetFieldsEnabled(true);
            }
            finally
            {
                _connectCts = null;
                cts.Dispose();
                UpdateUi();
                UpdateTraffic();
                UiMotion.Pop(BtnAction, 0.96);
            }
        }

        /// <summary>Background thread - marshalled without blocking.</summary>
        private void Session_Received(SocketSession session, byte[] data, IPEndPoint? from)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(session, _session)) return;
                string text = ChkShowHex.IsChecked == true ? NetworkToolsManager.FormatHex(data) : NetworkToolsManager.Printable(_decoder.Decode(data));
                string who = session.Protocol == SocketProtocol.Udp && from != null ? $"{from}  " : "";
                Results.Log(ConsoleKind.Received, SourceTag, $"◀ {who}{text}");
                UpdateTraffic();
            });
        }

        private void Session_Closed(SocketSession session, string? reason)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Only the current session may reset the UI - a late close from an old one used to drop a new connection.
                if (!ReferenceEquals(session, _session)) return;
                _session = null;
                Results.Log(reason == null ? ConsoleKind.Info : ConsoleKind.Warning, session.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP",
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
            if (byUser) Results.Log(ConsoleKind.Info, s.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP", "Disconnected.");
            AfterSessionClosed();
        }

        private void AfterSessionClosed()
        {
            SetFieldsEnabled(true);
            UpdateUi();
            UiMotion.Pop(StatePill, 0.9);
        }

        private void SetFieldsEnabled(bool enabled)
        {
            ProtocolChips.IsEnabled = enabled;
            TxtSocketHost.IsEnabled = enabled;
            TxtSocketPort.IsEnabled = enabled;
            TxtSocketLocalPort.IsEnabled = enabled;
            SourcePicker.IsEnabled = enabled;
        }

        /// <summary>Action button, state pill, adapter pill and title.</summary>
        private void UpdateUi()
        {
            bool busy = _session != null || _connectCts != null;
            bool tcp = SelectedProtocol == SocketProtocol.Tcp;
            IconAction.Kind = busy ? PackIconKind.LanDisconnect : PackIconKind.LanConnect;
            TxtActionLabel.Text = busy ? (_connectCts != null ? "Cancel" : tcp ? "Disconnect" : "Close") : tcp ? "Connect" : "Open";
            BtnAction.ToolTip = busy ? "Close the connection" : "Connect to the host and port";

            (string text, string bg, string fg, PackIconKind icon) =
                _connectCts != null ? ("Connecting...", "StatusInfoBg", "StatusInfoFg", PackIconKind.LanPending) :
                _session != null ? (_session.Protocol == SocketProtocol.Tcp ? "Connected" : $"UDP open · port {_session.LocalPort}", "StatusSuccessBg", "StatusSuccessFg", PackIconKind.LanConnect) :
                (TryFindResource("Str_Network_Disconnected") as string ?? "Disconnected", "RowBg", "TextSecondary", PackIconKind.LanDisconnect);
            TxtState.Text = text;
            StatePill.SetResourceReference(Border.BackgroundProperty, bg);
            TxtState.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconState.SetResourceReference(ForegroundProperty, fg);
            IconState.Kind = icon;

            var source = busy ? _sentFrom : SourcePicker.Selected;
            TxtSource.Text = source.IsAutomatic ? "Automatic" : $"{source.Label} · {source.Address}";
            IconSource.Kind = source.IsAutomatic ? PackIconKind.AutoFix
                : source.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? PackIconKind.Wifi
                : source.Kind.StartsWith("USB", StringComparison.Ordinal) ? PackIconKind.Usb
                : PackIconKind.Ethernet;

            TxtToolTitle.Text = _session != null
                ? $"{(_session.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP")} {_session.Host}:{_session.Port}" + (_sentFrom.IsAutomatic ? "" : $" {_sentFrom.FromText}")
                : "TCP / UDP client";
            TxtToolDetail.Text = _session == null
                ? "Send text or bytes to a device or server and see what it answers - from the adapter you choose."
                : _session.Protocol == SocketProtocol.Udp
                    ? $"Listening on local port {_session.LocalPort} · replies from anyone appear below"
                    : $"Local port {_session.LocalPort}" + (_session.Remote != null ? $" · {_session.Remote}" : "");
        }

        private void UpdateTraffic() =>
            TxtTraffic.Text = _session == null
                ? "↑ 0 B   ↓ 0 B"
                : $"↑ {NetworkText.FormatBytes(_session.BytesSent)} sent   ↓ {NetworkText.FormatBytes(_session.BytesReceived)} received";

        // =================================================================
        //  Sending
        // =================================================================

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
                Results.Log(ConsoleKind.Warning, SourceTag, _connectCts != null ? "Still connecting - wait a moment." : "Not connected - press Connect first.");
                UiMotion.Pop(BtnAction, 0.95);
                return;
            }

            string input = TxtSocketSend.Text;
            var (data, display, error) = NetworkToolsManager.BuildPayload(input, ChkSendHex.IsChecked == true, ChkAppendNewline.IsChecked == true);
            if (error != null)
            {
                Results.Log(ConsoleKind.Warning, SourceTag, error);
                return;
            }

            try
            {
                await session.SendAsync(data);
                Results.Log(ConsoleKind.Sent, SourceTag, "▶ " + display);
                if (_sendHistory.Count == 0 || _sendHistory[^1] != input) _sendHistory.Add(input);
                if (_sendHistory.Count > 50) _sendHistory.RemoveAt(0);
                _historyIndex = -1;
                TxtSocketSend.Clear();
                UpdateTraffic();
            }
            catch (Exception ex)
            {
                string reason = ex is SocketException se ? SocketSession.Friendly(se) : ex.InnerException is SocketException ise ? SocketSession.Friendly(ise) : ex.Message;
                Results.Log(ConsoleKind.Error, SourceTag, "Send failed: " + reason);
            }
        }
    }
}
