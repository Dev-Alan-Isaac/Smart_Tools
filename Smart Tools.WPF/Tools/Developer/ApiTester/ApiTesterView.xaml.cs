using Microsoft.Win32;
using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
// The project also references WinForms (see MainWindow's MessageBox alias),
// so pin the WPF / Win32 versions of the names that exist in both.
using MenuItem = System.Windows.Controls.MenuItem;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Interaction logic for ApiTesterView.xaml.
    ///
    /// UI only: this class reads the controls into ApiModel types, hands
    /// them to <see cref="ApiManager"/>, and renders what comes back. All
    /// HTTP / WebSocket / export / persistence logic lives in ApiManager.
    /// </summary>
    public partial class ApiTesterView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int MaxVisibleLogEntries = 2000;

        private readonly ApiManager _api = new();

        private readonly ObservableCollection<KeyValueRow> _headers = new();
        private readonly ObservableCollection<KeyValueRow> _queryParams = new();
        private readonly ObservableCollection<SavedEndpoint> _savedEndpoints = new();
        private readonly ObservableCollection<WebSocketLogEntry> _webSocketLog = new();

        private readonly bool _viewConstructed;   // handlers can fire inside InitializeComponent, before later controls exist
        private bool _optionsCollapsed;
        private bool _isRestMode = true;
        private string _lastResponseBody = "";

        public ApiTesterView()
        {
            InitializeComponent();
            _viewConstructed = true;

            DataGridHeaders.ItemsSource = _headers;
            DataGridParams.ItemsSource = _queryParams;
            SavedEndpointsList.ItemsSource = _savedEndpoints;
            WebSocketLogList.ItemsSource = _webSocketLog;

            // Start with one blank row in each so the grids don't open empty.
            _headers.Add(new KeyValueRow());
            _queryParams.Add(new KeyValueRow());

            // Loaded fires every time MainWindow swaps this view back in, so
            // one-time setup (like reading saved endpoints) belongs here.
            foreach (var endpoint in ApiManager.LoadSavedEndpoints())
                _savedEndpoints.Add(endpoint);

            _savedEndpoints.CollectionChanged += (_, _) =>
            {
                UpdateSavedEndpointsEmptyState();
                ApiManager.SaveSavedEndpoints(_savedEndpoints);
            };
            UpdateSavedEndpointsEmptyState();

            _api.ConnectionStateChanged += Api_ConnectionStateChanged;
            _api.WebSocketMessageLogged += Api_WebSocketMessageLogged;

            UpdateMethodColor();
            ShowConnectionState(ApiConnectionState.Idle, "Idle");
        }

        /// <summary>
        /// Called from MainWindow.Window_Closing so an open WebSocket or
        /// in-flight request is torn down cleanly instead of by process exit.
        /// </summary>
        public void CloseActiveConnection() => _api.Dispose();

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            // Saved endpoints are restored once in the constructor (Loaded
            // re-fires on every navigation back to this view). Credentials are
            // deliberately never persisted.
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Reserved for narrow-width layout tweaks, matching the other views.
        }

        // ================================================================
        //  Options sidebar
        // ================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);

            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            ModeTogglePanel.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;

            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;

            if (BtnCollapseOptions.Content is StackPanel headerStack)
                headerStack.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // ================================================================
        //  Mode (REST / WebSocket)
        // ================================================================

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed)
                return; // fires once during InitializeComponent before the panels exist

            _isRestMode = RadioModeRest.IsChecked == true;

            var restVisibility = _isRestMode ? Visibility.Visible : Visibility.Collapsed;
            var wsVisibility = _isRestMode ? Visibility.Collapsed : Visibility.Visible;

            RestRequestBar.Visibility = restVisibility;
            RestPanel.Visibility = restVisibility;
            WebSocketConnectionBar.Visibility = wsVisibility;
            WebSocketPanel.Visibility = wsVisibility;
            PanelWebSocketOptions.Visibility = wsVisibility;
            SepWebSocketOptions.Visibility = wsVisibility;

            ClearUrlValidationError(TxtRequestUrl);
            ClearUrlValidationError(TxtWebSocketUrl);

            // Reflect the REAL socket state - switching tabs doesn't close it.
            ShowConnectionState(_api.ConnectionState, _api.IsWebSocketConnected ? "Connected" : "Idle");
        }

        // ================================================================
        //  Request inputs
        // ================================================================

        private void CmbMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed) UpdateMethodColor();
        }

        /// <summary>Colour-codes the selected method (GET green, POST blue, ...) like its dropdown item.</summary>
        private void UpdateMethodColor()
        {
            if (CmbMethod == null)
                return;
            CmbMethod.SetResourceReference(ForegroundProperty, ApiManager.GetMethodColorKey(SelectedMethod));
        }

        private string SelectedMethod =>
            (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        private void CmbAuthType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed)
                return; // fires once during InitializeComponent before the panels exist

            var type = (ApiAuthType)Math.Max(0, CmbAuthType.SelectedIndex);
            PanelAuthBasic.Visibility = type == ApiAuthType.Basic ? Visibility.Visible : Visibility.Collapsed;
            PanelAuthBearer.Visibility = type == ApiAuthType.Bearer ? Visibility.Visible : Visibility.Collapsed;
            PanelAuthApiKey.Visibility = type == ApiAuthType.ApiKey ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnAddHeader_Click(object sender, RoutedEventArgs e) => _headers.Add(new KeyValueRow());

        private void BtnRemoveHeaderRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: KeyValueRow row })
                _headers.Remove(row);
        }

        private void BtnAddParam_Click(object sender, RoutedEventArgs e) => _queryParams.Add(new KeyValueRow());

        private void BtnRemoveParamRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: KeyValueRow row })
                _queryParams.Remove(row);
        }

        /// <summary>Live byte-size readout for the request body.</summary>
        private void TxtRequestBody_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            int byteCount = Encoding.UTF8.GetByteCount(TxtRequestBody.Text ?? string.Empty);
            TxtRequestBodySize.Text = ApiManager.FormatBytes(byteCount);
        }

        /// <summary>Snapshot of every request-related control, as a model.</summary>
        private ApiRequest BuildRequestFromUi()
        {
            // Commit any cell the user is still typing in, or its value is lost.
            DataGridHeaders.CommitEdit(DataGridEditingUnit.Row, true);
            DataGridParams.CommitEdit(DataGridEditingUnit.Row, true);

            return new ApiRequest
            {
                Method = SelectedMethod,
                Url = TxtRequestUrl.Text ?? "",
                BaseUrl = TxtBaseUrl.Text ?? "",
                Headers = _headers.ToList(),
                QueryParams = _queryParams.ToList(),
                Body = TxtRequestBody.Text ?? "",
                BodyFormat = (ApiBodyFormat)Math.Max(0, CmbBodyFormat.SelectedIndex),
                Auth = new ApiAuthSettings(
                    (ApiAuthType)Math.Max(0, CmbAuthType.SelectedIndex),
                    TxtAuthUsername.Text ?? "",
                    PwdAuthPassword.Password ?? "",
                    TxtAuthToken.Text ?? "",
                    TxtAuthKeyName.Text ?? "",
                    TxtAuthKeyValue.Text ?? ""),
            };
        }

        // ================================================================
        //  Saved endpoints
        // ================================================================

        private void UpdateSavedEndpointsEmptyState()
        {
            SavedEndpointsEmptyState.Visibility = _savedEndpoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSaveEndpoint_Click(object sender, RoutedEventArgs e)
        {
            // In WebSocket mode, save the socket URL under a "WS" pseudo-method.
            string method = _isRestMode ? SelectedMethod : "WS";
            string url = (_isRestMode ? TxtRequestUrl.Text : TxtWebSocketUrl.Text)?.Trim() ?? "";

            if (url.Length == 0)
            {
                ShowUrlValidationError(_isRestMode ? TxtRequestUrl : TxtWebSocketUrl, "Enter a URL to save.");
                return;
            }

            if (ApiManager.ContainsEndpoint(_savedEndpoints, method, url))
                return;

            _savedEndpoints.Add(new SavedEndpoint { Method = method, Url = url });
            SavedEndpointsList.SelectedIndex = _savedEndpoints.Count - 1;
            SavedEndpointsList.ScrollIntoView(SavedEndpointsList.SelectedItem);
        }

        private void BtnRemoveEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (SavedEndpointsList.SelectedItem is SavedEndpoint selected)
                _savedEndpoints.Remove(selected);
        }

        private void SavedEndpointsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SavedEndpointsList.SelectedItem is not SavedEndpoint selected)
                return;

            if (string.Equals(selected.Method, "WS", StringComparison.OrdinalIgnoreCase))
            {
                RadioModeWebSocket.IsChecked = true;
                TxtWebSocketUrl.Text = selected.Url;
                return;
            }

            RadioModeRest.IsChecked = true;
            TxtRequestUrl.Text = selected.Url;

            var match = CmbMethod.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), selected.Method, StringComparison.OrdinalIgnoreCase));

            // Don't leave a stale, unrelated method selected if this entry's
            // method no longer matches any option.
            CmbMethod.SelectedItem = match ?? CmbMethod.Items[0];
        }

        // ================================================================
        //  Inline validation
        // ================================================================

        /// <summary>
        /// Red border on the rounded URL bar + message under it, instead of a
        /// blocking dialog. (The TextBox itself is borderless inside the bar.)
        /// </summary>
        private void ShowUrlValidationError(System.Windows.Controls.TextBox target, string message)
        {
            var bar = BarFor(target);
            bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            TxtUrlValidationMessage.Text = message;
            UiMotion.FadeVisibility(TxtUrlValidationMessage, true);
            UiMotion.Pop(bar, fromScale: 0.98);
            target.Focus();
        }

        private void ClearUrlValidationError(System.Windows.Controls.TextBox target)
        {
            BarFor(target).ClearValue(Border.BorderBrushProperty);
            TxtUrlValidationMessage.Visibility = Visibility.Collapsed;
        }

        private Border BarFor(System.Windows.Controls.TextBox target) =>
            ReferenceEquals(target, TxtWebSocketUrl) ? WsUrlBarBorder : UrlBarBorder;

        private void TxtRequestUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) ClearUrlValidationError(TxtRequestUrl);
        }

        private void TxtWebSocketUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) ClearUrlValidationError(TxtWebSocketUrl);
        }

        /// <summary>Enter in the URL box sends, like a browser address bar.</summary>
        private void TxtRequestUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = SendRequestAsync();
            }
        }

        private void TxtWebSocketUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_api.IsWebSocketConnected)
            {
                e.Handled = true;
                _ = ConnectWebSocketAsync();
            }
        }

        // ================================================================
        //  REST
        // ================================================================

        /// <summary>Send while idle; the same button cancels while a request is in flight.</summary>
        private void BtnSendRequest_Click(object sender, RoutedEventArgs e)
        {
            if (_api.IsRequestInFlight)
                _api.CancelRequest();
            else
                _ = SendRequestAsync();
        }

        private async Task SendRequestAsync()
        {
            if (_api.IsRequestInFlight)
                return;

            var request = BuildRequestFromUi();

            if (!ApiManager.TryBuildUrl(request.Url, request.BaseUrl, request.QueryParams, out _, out string urlError))
            {
                ShowUrlValidationError(TxtRequestUrl, urlError);
                return;
            }

            SetSendingState(true);
            SetResponseStatus("Sending...", "StatusInfoBg", "StatusInfoFg", PackIconKind.ProgressUpload);
            TxtResponseLatency.Text = "— ms";
            TxtResponseSize.Text = "— bytes";

            try
            {
                var response = await _api.SendAsync(request);
                ShowResponse(response);
            }
            finally
            {
                SetSendingState(false);
            }
        }

        private void ShowResponse(ApiResponse response)
        {
            TxtResponseLatency.Text = $"{response.ElapsedMs:N0} ms";

            var (bg, fg) = response.ErrorKind == ApiErrorKind.Cancelled
                ? ("RowBg", "TextSecondary")
                : ApiManager.GetStatusColorKeys(response.StatusCode);

            PackIconKind icon = response.ErrorKind switch
            {
                ApiErrorKind.Cancelled => PackIconKind.Cancel,
                ApiErrorKind.Timeout => PackIconKind.TimerSandComplete,
                ApiErrorKind.None => response.StatusCode switch
                {
                    >= 200 and < 300 => PackIconKind.CheckCircleOutline,
                    >= 300 and < 400 => PackIconKind.ArrowRightCircleOutline,
                    >= 400 and < 500 => PackIconKind.AlertOutline,
                    _ => PackIconKind.AlertCircleOutline,
                },
                _ => PackIconKind.LanDisconnect,
            };
            SetResponseStatus(response.StatusText, bg, fg, icon);

            if (!response.HasResponse)
            {
                TxtResponseSize.Text = "— bytes";
                return; // keep the previous body visible; the pill explains what went wrong
            }

            // First response: swap the empty-state illustration for the real content.
            UiMotion.FadeVisibility(ResponseEmptyState, false);
            UiMotion.FadeIn(TabResponseDetails, risePx: 4);

            _lastResponseBody = response.Body;
            TxtResponseSize.Text = ApiManager.FormatBytes(response.SizeBytes);
            TxtResponseBody.Text = ChkPrettyPrint.IsChecked == true
                ? ApiManager.TryPrettyPrintJson(response.Body)
                : response.Body;
            TxtResponseBody.ScrollToHome();
            TxtResponseHeaders.Text = response.HeadersText;
        }

        /// <summary>Progress sweep + Send button flips to Cancel while a request is running.</summary>
        private void SetSendingState(bool sending)
        {
            ProgressSending.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
            ProgressSending.IsIndeterminate = sending;

            if (sending)
                BtnSendRequest.Content = TryFindResource("Str_Common_Cancel") ?? "Cancel";
            else
                BtnSendRequest.SetResourceReference(ContentControl.ContentProperty, "Str_Api_Send");
        }

        private void ChkPrettyPrint_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastResponseBody))
                return;

            TxtResponseBody.Text = ChkPrettyPrint.IsChecked == true
                ? ApiManager.TryPrettyPrintJson(_lastResponseBody)
                : _lastResponseBody;
        }

        /// <summary>
        /// Updates the response pill. SetResourceReference (not a one-off
        /// TryFindResource lookup) so the pill re-colours when the theme is
        /// toggled - previously it kept the old theme's colours until the
        /// next request.
        /// </summary>
        private void SetResponseStatus(string text, string backgroundResourceKey, string foregroundResourceKey, PackIconKind icon)
        {
            TxtResponseStatus.Text = text;
            IconResponseStatus.Kind = icon;
            BorderResponseStatus.SetResourceReference(Border.BackgroundProperty, backgroundResourceKey);
            TxtResponseStatus.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
            UiMotion.Pop(BorderResponseStatus);
        }

        // ================================================================
        //  WebSocket
        // ================================================================

        private async void BtnConnectWebSocket_Click(object sender, RoutedEventArgs e)
        {
            if (_api.IsWebSocketConnected ||
                _api.ConnectionState is ApiConnectionState.Connecting or ApiConnectionState.Reconnecting)
            {
                await _api.DisconnectWebSocketAsync();
                return;
            }

            await ConnectWebSocketAsync();
        }

        private async Task ConnectWebSocketAsync()
        {
            if (!ApiManager.TryValidateWebSocketUrl(TxtWebSocketUrl.Text, out _, out string error))
            {
                ShowUrlValidationError(TxtWebSocketUrl, error);
                return;
            }

            await _api.ConnectWebSocketAsync(TxtWebSocketUrl.Text, ChkAutoReconnect.IsChecked == true);
        }

        private async void BtnSendWebSocketMessage_Click(object sender, RoutedEventArgs e) => await SendWebSocketMessageAsync();

        /// <summary>Ctrl+Enter sends; plain Enter still inserts a new line.</summary>
        private async void TxtWebSocketMessage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await SendWebSocketMessageAsync();
            }
        }

        private async Task SendWebSocketMessageAsync()
        {
            string text = TxtWebSocketMessage.Text ?? "";
            if (text.Length == 0)
                return;

            if (await _api.SendWebSocketMessageAsync(text))
                TxtWebSocketMessage.Clear();

            TxtWebSocketMessage.Focus();
        }

        private void Api_WebSocketMessageLogged(WebSocketLogEntry entry)
        {
            _webSocketLog.Add(entry);
            while (_webSocketLog.Count > MaxVisibleLogEntries)
                _webSocketLog.RemoveAt(0);

            WebSocketEmptyState.Visibility = Visibility.Collapsed;
            WebSocketLogList.ScrollIntoView(entry);
        }

        private void Api_ConnectionStateChanged(ApiConnectionState state, string detail) => ShowConnectionState(state, detail);

        /// <summary>Status pill dot/text + Connect button label + compose enabled state.</summary>
        private void ShowConnectionState(ApiConnectionState state, string detail)
        {
            if (TxtConnectionStatus.Text != detail)
                UiMotion.Pop(ConnectionPill);
            TxtConnectionStatus.Text = detail;

            string dotKey = state switch
            {
                ApiConnectionState.Connected => "StatusSuccessFg",
                ApiConnectionState.Connecting or ApiConnectionState.Reconnecting => "StatusWarningFg",
                ApiConnectionState.Failed => "StatusErrorFg",
                _ => "TextSecondary",
            };
            StatusDot.SetResourceReference(Shape.FillProperty, dotKey);

            bool busyOrOpen = state is ApiConnectionState.Connected
                or ApiConnectionState.Connecting
                or ApiConnectionState.Reconnecting;

            if (busyOrOpen)
                BtnConnectWebSocket.Content = TryFindResource("Str_Network_DisconnectButton") ?? "Disconnect";
            else
                BtnConnectWebSocket.SetResourceReference(ContentControl.ContentProperty, "Str_Network_ConnectButton");

            BtnSendWebSocketMessage.IsEnabled = state == ApiConnectionState.Connected;
            TxtWebSocketUrl.IsReadOnly = busyOrOpen;
        }

        // ================================================================
        //  Copy to clipboard
        // ================================================================

        private void BtnCopyResponseBody_Click(object sender, RoutedEventArgs e) => TryCopyToClipboard(TxtResponseBody.Text);

        private void BtnCopyResponseHeaders_Click(object sender, RoutedEventArgs e) => TryCopyToClipboard(TxtResponseHeaders.Text);

        /// <summary>Clipboard.SetText can throw if another process briefly holds the clipboard.</summary>
        private static void TryCopyToClipboard(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            try { System.Windows.Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        // ================================================================
        //  Export
        // ================================================================

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out ApiExportFormat format)) return;
            bool hasData = _isRestMode ? _api.History.Count > 0 : _api.WebSocketLog.Count > 0;

            if (!hasData)
            {
                AppModal.Show(
                    Window.GetWindow(this)!,
                    _isRestMode ? "Send at least one request before exporting." : "There's no WebSocket traffic to export yet.",
                    "Nothing to export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = ApiManager.FileFilterFor(format),
                DefaultExt = ApiManager.ExtensionFor(format),
                FileName = $"{(_isRestMode ? "api-history" : "websocket-log")}-{DateTime.Now:yyyyMMdd-HHmmss}{ApiManager.ExtensionFor(format)}",
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            try
            {
                if (_isRestMode)
                    _api.ExportHistory(dialog.FileName, format);
                else
                    _api.ExportWebSocketLog(dialog.FileName, format);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Window.GetWindow(this)!, $"Couldn't save the file:\n{ex.Message}",
                    Loc.T("Str_Dlg_ExportFailed", "Export failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
