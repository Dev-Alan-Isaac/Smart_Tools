using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using DataObject = System.Windows.DataObject;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Send to Phone - UI only. The server, the device approval, the encryption and the transfers are in
    /// PhoneSession (+ PhoneCrypto, PhoneCertificate); the page the phone opens is in the Web folder.
    /// The session lives on while you visit other tools and ends when you press Stop, after the idle
    /// time you chose, or when the app closes.
    /// </summary>
    public partial class SendToPhoneView : UserControl
    {
        private const string SettingsName = "send_to_phone";

        /// <summary>One row of the "what to send" list (a file or a text). <c>Id</c> is set when a running session has taken it.</summary>
        private sealed class OfferRow
        {
            public string? Id { get; set; }
            public required string Name { get; init; }
            public required string Detail { get; init; }
            public string? Path { get; init; }
            public string? Text { get; init; }
            public bool IsText => Text != null;
        }

        private PhoneSession? _session;
        private PhoneSettings _s = new();
        private readonly ObservableCollection<OfferRow> _offers = new();
        private readonly ObservableCollection<PhoneTransfer> _transfers = new();
        private readonly HashSet<PhoneTransfer> _finished = new();
        private List<LocalNetwork> _nets = new();
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(60) };
        private string? _clipboardSecret;
        private bool _applying = true;
        private bool _loadedOnce;
        private bool _closed;

        public SendToPhoneView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _offers;
            TransferList.ItemsSource = _transfers;
            _tick.Tick += Tick_Tick;
            _clipboardTimer.Tick += ClipboardTimer_Tick;
            FileDrop.Attach(this, path => AddFiles(new[] { path }));
        }

        /// <summary>Called from MainWindow.Window_Closing: ends the session (removing any half-received file) and saves the setup.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _tick.Stop();
            var session = _session;
            _session = null;
            if (session != null)
            {
                try { Task.Run(() => session.StopAsync("user")).Wait(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { Log.Info($"Send to Phone: stop: {ex.Message}"); }
            }
            SaveSettings();
        }

        // =================================================================
        //  Start, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _s = JsonSettings.Load<PhoneSettings>(SettingsName);
                _applying = true;
                RadioEncrypted.IsChecked = _s.Security == PhoneSecurity.Encrypted;
                RadioTls.IsChecked = _s.Security == PhoneSecurity.TlsOnly;
                SelectByTag(CmbMaxSize, _s.MaxFileMb.ToString(), "2048");
                SelectByTag(CmbIdle, _s.IdleMinutes.ToString(), "15");
                ChkBlock.IsChecked = _s.BlockPrograms;
                LoadNetworks();
                _applying = false;
                UpdateHints();
                UpdateStateUi(PhoneState.Stopped);
            }
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => SaveSettings();

        private static void SelectByTag(ComboBox box, string tag, string fallback)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string) == fallback) { box.SelectedItem = item; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static int TagInt(ComboBox box, int fallback) => int.TryParse((box.SelectedItem as ComboBoxItem)?.Tag as string, out int v) ? v : fallback;

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            try { ReadUi(); JsonSettings.Save(SettingsName, _s); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Info($"Send to Phone settings not saved: {ex.Message}");
            }
        }

        private void ReadUi()
        {
            _s.Security = RadioTls.IsChecked == true ? PhoneSecurity.TlsOnly : PhoneSecurity.Encrypted;
            _s.MaxFileMb = TagInt(CmbMaxSize, 2048);
            _s.IdleMinutes = TagInt(CmbIdle, 15);
            _s.BlockPrograms = ChkBlock.IsChecked == true;
            if (CmbNetwork.SelectedIndex >= 0 && CmbNetwork.SelectedIndex < _nets.Count) _s.InterfaceName = _nets[CmbNetwork.SelectedIndex].InterfaceName;
        }

        private static bool IsUsable(LocalNetwork n)
        {
            if (!IPAddress.TryParse(n.LocalAddress, out var ip)) return false;
            var b = ip.GetAddressBytes();
            if (b.Length != 4 || (b[0] == 169 && b[1] == 254)) return false;   // no address from DHCP: nothing a phone can reach
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        private void LoadNetworks()
        {
            List<LocalNetwork> found;
            try { found = DeviceScannerManager.GetLocalNetworks().Where(IsUsable).ToList(); }
            catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
            {
                found = new();
            }
            _nets = found;
            CmbNetwork.Items.Clear();
            foreach (var n in _nets) CmbNetwork.Items.Add($"{n.Kind} · {n.LocalAddress}");
            int keep = _nets.FindIndex(n => n.InterfaceName == _s.InterfaceName);
            CmbNetwork.SelectedIndex = _nets.Count == 0 ? -1 : Math.Max(0, keep);
            TxtNetworkHint.Text = _nets.Count == 0
                ? Loc.T("Str_SendToPhone_NoNetwork", "No private network was found. Connect this PC to the same Wi-Fi or router as the phone.")
                : Loc.T("Str_SendToPhone_NetworkHint", "The phone must be on this same network (the same Wi-Fi or router).");
        }

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();
        private void Option_Changed(object sender, SelectionChangedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (_applying || !_loadedOnce) return;
            ReadUi();
            UpdateHints();
        }

        private void UpdateHints()
        {
            TxtSecurityHint.Text = RadioTls.IsChecked == true
                ? Loc.T("Str_SendToPhone_HintTls", "Protected by HTTPS. Faster, and big downloads stream straight to the phone, but the files are not end-to-end encrypted.")
                : Loc.T("Str_SendToPhone_HintEncrypted", "HTTPS plus end-to-end encryption: every file and name is sealed with a key that only the QR code carries. Large downloads are held in the phone's memory.");
        }

        // =================================================================
        //  What to send
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddFiles(e.Paths);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _offers.Clear();
            _session?.ClearOffers();
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path) || _offers.Count >= 200) continue;
                if (_offers.Any(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var row = new OfferRow { Name = Path.GetFileName(path), Path = path, Detail = PhoneTransfer.FormatSize(size) };
                if (_session != null)
                {
                    var offer = _session.AddFile(path);
                    if (offer == null) continue;
                    row.Id = offer.Id;
                }
                _offers.Add(row);
            }
        }

        private void BtnAddText_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtOfferText.Text.Trim();
            if (text.Length == 0) return;
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024)
            {
                ShowStatus(Loc.T("Str_SendToPhone_MsgTextTooLong", "That text is too long to send this way (about 60,000 characters at most). Save it as a file and send the file."), StatusKind.Error);
                return;
            }
            var row = new OfferRow
            {
                Name = text.Length > 40 ? text[..40] + "…" : text,
                Text = text,
                Detail = Loc.F("Str_SendToPhone_TextDetail", "Text · {0} characters", text.Length),
            };
            if (_session != null)
            {
                var offer = _session.AddText(text);
                if (offer == null) return;
                row.Id = offer.Id;
            }
            _offers.Add(row);
            TxtOfferText.Clear();
        }

        private void BtnRemoveOffer_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not OfferRow row) return;
            _offers.Remove(row);
            if (row.Id != null) _session?.RemoveOffer(row.Id);
        }

        // =================================================================
        //  Session
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_session != null) { await StopSessionAsync(); return; }
            await StartSessionAsync();
        }

        private async Task StartSessionAsync()
        {
            HideStatus();
            ReadUi();
            if (CmbNetwork.SelectedIndex < 0 || CmbNetwork.SelectedIndex >= _nets.Count || !IPAddress.TryParse(_nets[CmbNetwork.SelectedIndex].LocalAddress, out var address))
            {
                ShowStatus(Loc.T("Str_SendToPhone_MsgNoNetwork", "Pick a network first. The PC and the phone must be on the same Wi-Fi or router."), StatusKind.Error);
                return;
            }
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_SendToPhone_MsgFolder", "The folder for received files cannot be used: {0}", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            string? folder;
            try { folder = OutputCard.EnsureFolder(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Loc.F("Str_SendToPhone_MsgFolder", "The folder for received files cannot be used: {0}", ex.Message), StatusKind.Error);
                return;
            }
            if (string.IsNullOrEmpty(folder))
            {
                ShowStatus(Loc.F("Str_SendToPhone_MsgFolder", "The folder for received files cannot be used: {0}", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            SaveSettings();

            var session = new PhoneSession(new PhoneSession.Options(address, _s.Security, folder,
                _s.MaxFileMb <= 0 ? 0 : _s.MaxFileMb * 1024L * 1024, TimeSpan.FromMinutes(Math.Max(1, _s.IdleMinutes)), _s.BlockPrograms));
            session.StateChanged += st => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) UpdateStateUi(st); });
            session.DeviceRequested += (ip, name) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) OnDeviceRequested(ip, name); });
            session.TransferAdded += t => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) _transfers.Insert(0, t); });
            session.Ended += reason => Dispatcher.BeginInvoke(() => OnEnded(session, reason));

            _session = session;
            BtnStart.IsEnabled = false;
            try
            {
                await session.StartAsync();
                foreach (var row in _offers.ToList())
                {
                    var offer = row.IsText ? session.AddText(row.Text!) : session.AddFile(row.Path!);
                    if (offer == null) _offers.Remove(row); else row.Id = offer.Id;
                }
                var qr = new QrCodeManager().Generate(session.Url, new QrCodeOptions { WidthPx = 520, HeightPx = 520, QuietZoneModules = 2 });
                if (!qr.Success || qr.Image == null) throw new InvalidOperationException(qr.ErrorMessage ?? "QR");
                ImgQr.Source = qr.Image;
                QrFrame.Visibility = Visibility.Visible;
                TxtLink.Text = session.UrlWithoutKey;
                LinkButtons.Visibility = Visibility.Visible;
                TxtKeyWarning.Text = _s.Security == PhoneSecurity.Encrypted
                    ? Loc.T("Str_SendToPhone_KeyWarning", "The QR code contains the encryption key. Do not share a picture of it, and press Stop when you are done.")
                    : Loc.T("Str_SendToPhone_KeyWarningTls", "Anyone on your network who gets this link could ask to connect, but you must allow each phone here first. Press Stop when you are done.");
                TxtKeyWarning.Visibility = Visibility.Visible;
                TxtFingerprint.Text = PhoneCertificate.Pretty(session.Fingerprint);
                FingerprintPanel.Visibility = Visibility.Visible;
                _tick.Start();
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or CryptographicException
                                          or AuthenticationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Warn($"Send to Phone could not start: {ex.Message}");
                _session = null;
                try { await session.StopAsync("error"); } catch (Exception inner) when (inner is InvalidOperationException or IOException) { }
                ResetQr();
                UpdateStateUi(PhoneState.Stopped);
                ShowStatus(Loc.F("Str_SendToPhone_MsgCantStart", "Could not start: {0}", ex.Message), StatusKind.Error);
            }
            finally
            {
                BtnStart.IsEnabled = true;
            }
        }

        private async Task StopSessionAsync()
        {
            var session = _session;
            if (session == null) return;
            BtnStart.IsEnabled = false;
            try { await session.StopAsync("user"); }
            finally { BtnStart.IsEnabled = true; }
        }

        private void OnEnded(PhoneSession session, string reason)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            _tick.Stop();
            RefreshTransfers(final: true);
            ResetQr();
            UpdateStateUi(PhoneState.Stopped);
            switch (reason)
            {
                case "idle":
                    ShowStatus(Loc.F("Str_SendToPhone_MsgIdle", "Closed by itself after {0} minutes without activity.", _s.IdleMinutes), StatusKind.Info);
                    break;
                case "failures":
                    ShowStatus(Loc.T("Str_SendToPhone_MsgFailures", "Stopped: too many wrong requests reached this PC. Start again for a new code."), StatusKind.Error);
                    break;
                case "user":
                    break;
                default:
                    ShowStatus(Loc.T("Str_SendToPhone_MsgEnded", "The session ended."), StatusKind.Info);
                    break;
            }
            foreach (var row in _offers) row.Id = null;
        }

        private void ResetQr()
        {
            ImgQr.Source = null;
            QrFrame.Visibility = Visibility.Collapsed;
            TxtLink.Text = "";
            LinkButtons.Visibility = Visibility.Collapsed;
            BtnDisconnect.Visibility = Visibility.Collapsed;
            TxtKeyWarning.Visibility = Visibility.Collapsed;
            FingerprintPanel.Visibility = Visibility.Collapsed;
            ApprovalCard.Visibility = Visibility.Collapsed;
        }

        // ---- the phone asks to connect ----

        private void OnDeviceRequested(string ip, string name)
        {
            string who = name.Length > 0 ? name : Loc.T("Str_SendToPhone_UnknownDevice", "A phone");
            TxtApproval.Text = Loc.F("Str_SendToPhone_ApprovalText", "{0} ({1}) wants to connect. Only allow it if it is your phone: it will be able to see what you offer and send files to this PC.", who, ip);
            ApprovalCard.Visibility = Visibility.Visible;
            ToastNotificationManager.ShowToast(Loc.T("Str_Tool_send-to-phone_Name", "Send to Phone"),
                Loc.F("Str_SendToPhone_ToastApproval", "{0} wants to connect. Open Smart Tools to allow it.", who));
        }

        private void BtnAllow_Click(object sender, RoutedEventArgs e)
        {
            _session?.Approve();
            ApprovalCard.Visibility = Visibility.Collapsed;
        }

        private void BtnDeny_Click(object sender, RoutedEventArgs e)
        {
            _session?.Deny();
            ApprovalCard.Visibility = Visibility.Collapsed;
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _session?.Disconnect();
            BtnDisconnect.Visibility = Visibility.Collapsed;
        }

        // ---- state display ----

        private void UpdateStateUi(PhoneState state)
        {
            (string text, string bg, string fg) = state switch
            {
                PhoneState.Starting => (Loc.T("Str_SendToPhone_StateStarting", "Starting..."), "StatusInfoBg", "StatusInfoFg"),
                PhoneState.WaitingForPhone => (Loc.T("Str_SendToPhone_StateWaiting", "Waiting for a phone"), "StatusInfoBg", "StatusInfoFg"),
                PhoneState.WaitingForApproval => (Loc.T("Str_SendToPhone_StateApproval", "Waiting for your approval"), "StatusWarningBg", "StatusWarningFg"),
                PhoneState.Connected => (Loc.T("Str_SendToPhone_StateConnected", "Phone connected"), "StatusSuccessBg", "StatusSuccessFg"),
                _ => (Loc.T("Str_SendToPhone_StateStopped", "Stopped"), "RowBg", "TextSecondary"),
            };
            PillState.SetResourceReference(Border.BackgroundProperty, bg);
            TxtPillState.SetResourceReference(TextBlock.ForegroundProperty, fg);
            TxtPillState.Text = text;

            bool running = state != PhoneState.Stopped;
            TxtStart.Text = running ? Loc.T("Str_SendToPhone_Stop", "Stop") : Loc.T("Str_SendToPhone_Start", "Start");
            IconStart.Kind = running ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.Cellphone;
            TxtHeroHint.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            TxtQrHint.Text = running
                ? Loc.T("Str_SendToPhone_QrScan", "Scan this with the phone's camera. Its browser opens the page; you then allow the phone here.")
                : Loc.T("Str_SendToPhone_QrIdle", "Press Start to make a one-time QR code.");

            CmbNetwork.IsEnabled = RadioEncrypted.IsEnabled = RadioTls.IsEnabled = CmbMaxSize.IsEnabled = CmbIdle.IsEnabled = ChkBlock.IsEnabled = !running;
            OutputCard.IsLocked = running;
            if (state != PhoneState.WaitingForApproval) ApprovalCard.Visibility = Visibility.Collapsed;
            BtnDisconnect.Visibility = state == PhoneState.Connected ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- transfers ----

        private void Tick_Tick(object? sender, EventArgs e) => RefreshTransfers(final: false);

        private void RefreshTransfers(bool final)
        {
            TxtNoTransfers.Visibility = _transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in _transfers)
            {
                if (_finished.Contains(t)) continue;
                t.Notify();
                bool ended = t.State is PhoneTransferState.Done or PhoneTransferState.Failed;
                if (!ended && !final) continue;
                _finished.Add(t);
                if (t.State == PhoneTransferState.Done && t.Direction == PhoneDirection.FromPhone)
                    ToastNotificationManager.ShowToast(Loc.T("Str_Tool_send-to-phone_Name", "Send to Phone"),
                        t.IsText ? Loc.T("Str_SendToPhone_ToastText", "Text received from the phone.") : Loc.F("Str_SendToPhone_ToastFile", "Received {0}", t.Name));
            }
        }

        private void BtnClearDone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in _transfers.Where(t => t.State is PhoneTransferState.Done or PhoneTransferState.Failed).ToList())
            {
                _transfers.Remove(t);
                _finished.Remove(t);
            }
            TxtNoTransfers.Visibility = _transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnCopyTransferText_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PhoneTransfer { Text: { } text }) return;
            try { Clipboard.SetText(text); ShowStatus(Loc.T("Str_SendToPhone_MsgTextCopied", "Text copied."), StatusKind.Success); }
            catch (ExternalException) { ShowStatus(Loc.T("Str_SendToPhone_MsgClipboardBusy", "Windows would not let this program use the clipboard right now. Try again."), StatusKind.Error); }
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PhoneTransfer { SavedPath: { } path } || !File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Info($"Send to Phone: could not open the folder: {ex.Message}");
            }
        }

        // ---- the link ----

        private void BtnCopyLink_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            string link = _session.Url;
            try
            {
                // The link holds the encryption key: keep it out of Windows clipboard history and cloud sync, and clear it after a minute.
                var data = new DataObject();
                data.SetText(link, System.Windows.TextDataFormat.UnicodeText);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, true);
                _clipboardSecret = link;
                _clipboardTimer.Stop();
                _clipboardTimer.Start();
                ShowStatus(Loc.T("Str_SendToPhone_MsgLinkCopied", "Link copied (it includes the key, and is cleared from the clipboard after a minute)."), StatusKind.Success);
            }
            catch (ExternalException)
            {
                ShowStatus(Loc.T("Str_SendToPhone_MsgClipboardBusy", "Windows would not let this program use the clipboard right now. Try again."), StatusKind.Error);
            }
        }

        private void ClipboardTimer_Tick(object? sender, EventArgs e)
        {
            _clipboardTimer.Stop();
            try
            {
                if (_clipboardSecret != null && Clipboard.ContainsText() && Clipboard.GetText() == _clipboardSecret) Clipboard.Clear();
            }
            catch (ExternalException) { }
            _clipboardSecret = null;
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Info, Success, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            (string bg, string fg) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg"),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg"),
                _ => ("StatusInfoBg", "StatusInfoFg"),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;
    }
}
