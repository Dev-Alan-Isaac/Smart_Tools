using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// 2FA Code Generator - UI only. The codes are <see cref="TotpCodeManager"/>'s. The secret lives only in the text box
    /// and in memory while the tool is open; nothing about it is ever written to disk.
    /// </summary>
    public partial class TotpCodeView : UserControl
    {
        private readonly DispatcherTimer _tick;
        private readonly bool _viewConstructed;
        private byte[]? _secret;
        private string _code = "";

        public TotpCodeView()
        {
            InitializeComponent();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _tick.Tick += (_, _) => Refresh();
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _tick.Start();
            Refresh();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => _tick.Stop();

        private int Digits => Digits8.IsChecked == true ? 8 : Digits7.IsChecked == true ? 7 : 6;
        private int Period => Period60.IsChecked == true ? 60 : Period15.IsChecked == true ? 15 : 30;
        private TotpAlgorithm Algorithm => AlgSha512.IsChecked == true ? TotpAlgorithm.Sha512 : AlgSha256.IsChecked == true ? TotpAlgorithm.Sha256 : TotpAlgorithm.Sha1;

        private void Option_Click(object sender, RoutedEventArgs e) => Refresh();

        private void Input_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            string text = TxtSecret.Text.Trim();
            TxtError.Text = "";
            TxtAccount.Text = "";
            _secret = null;

            if (text.Length > 0)
            {
                if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
                {
                    if (TotpCodeManager.TryParseLink(text, out var link) && TotpCodeManager.TryDecodeBase32(link.Secret, out var key))
                    {
                        _secret = key;
                        (link.Digits == 8 ? Digits8 : link.Digits == 7 ? Digits7 : Digits6).IsChecked = true;
                        (link.Period == 60 ? Period60 : link.Period == 15 ? Period15 : Period30).IsChecked = true;
                        (link.Algorithm == TotpAlgorithm.Sha512 ? AlgSha512 : link.Algorithm == TotpAlgorithm.Sha256 ? AlgSha256 : AlgSha1).IsChecked = true;
                        TxtAccount.Text = link.Label.Length > 0 ? link.Label : link.Issuer;
                        if (link.Period != 30 && link.Period != 60 && link.Period != 15)
                            TxtError.Text = $"This link uses {link.Period} s, which isn't one of the choices, so 30 s is used.";
                    }
                    else TxtError.Text = "That otpauth:// link isn't a time-based (TOTP) link with a valid secret.";
                }
                else if (TotpCodeManager.TryDecodeBase32(text, out var key)) _secret = key;
                else TxtError.Text = "A secret key uses the letters A-Z and the digits 2-7 (spaces are fine) and is at least 8 characters.";
            }
            Refresh();
        }

        private void Refresh()
        {
            if (!_viewConstructed) return;
            if (_secret == null)
            {
                _code = "";
                TxtCode.Text = TotpCodeManager.Group(new string('0', Digits));   // a dim placeholder until there is a secret
                TxtCode.Opacity = TxtNext.Opacity = 0.18;
                TxtLeft.Text = "";
                TxtNext.Text = TotpCodeManager.Group(new string('0', Digits));
                BarTime.Value = 0;
                BtnCopy.IsEnabled = false;
                TxtEmpty.Visibility = Visibility.Visible;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            int period = Period, digits = Digits;
            var algorithm = Algorithm;
            _code = TotpCodeManager.Generate(_secret, now, digits, period, algorithm);
            string next = TotpCodeManager.Generate(_secret, now.AddSeconds(period), digits, period, algorithm);
            int left = TotpCodeManager.SecondsLeft(now, period);

            TxtCode.Opacity = TxtNext.Opacity = 1;
            TxtCode.Text = TotpCodeManager.Group(_code);
            TxtLeft.Text = $"New code in {left} s";
            TxtNext.Text = TotpCodeManager.Group(next);
            BarTime.Value = (double)left / period;
            BtnCopy.IsEnabled = true;
            TxtEmpty.Visibility = Visibility.Collapsed;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_code.Length == 0) return;
            TxtCopy.Text = CopySecret(_code) ? Loc.T("Str_Totp_Copied", "Copied") : Loc.T("Str_Totp_ClipboardBusy", "Clipboard busy");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); TxtCopy.Text = Loc.T("Str_SvgTool_CopyCode", "Copy code"); };
            t.Start();
        }

        /// <summary>A one-time code is a secret: keep it out of Windows clipboard history, the cloud clipboard and clipboard monitors.</summary>
        private static bool CopySecret(string text)
        {
            try
            {
                var data = new System.Windows.DataObject();
                data.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[] { 1, 0, 0, 0 }));
                data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(new byte[4]));
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        /// <summary>Called when the window closes: stops the clock and forgets the secret.</summary>
        public void CancelActiveRun()
        {
            _tick.Stop();
            _secret = null;
            TxtSecret.Clear();
        }
    }
}
