using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// JWT Decoder - UI only. Reading the token is in <see cref="JwtDecoderManager"/>. Nothing leaves the PC and nothing
    /// is saved; the token box is emptied by Clear or when the app closes.
    /// </summary>
    public partial class JwtDecoderView : UserControl
    {
        private static readonly string[] KnownClaims = { "iss", "sub", "aud", "exp", "nbf", "iat", "jti", "nonce", "azp", "scope" };

        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly bool _viewConstructed;
        private JwtDecoded? _decoded;

        public JwtDecoderView()
        {
            InitializeComponent();
            _clock.Tick += (_, _) => ShowTimeState();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops the clock and forgets the token.</summary>
        public void CancelActiveRun()
        {
            _clock.Stop();
            TxtToken.Clear();
            TxtKey.Clear();   // a secret is never kept
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_decoded == null) Render();
            TxtToken.Focus();
        }

        // =================================================================
        //  Input
        // =================================================================

        private void TxtToken_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) Render();
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TxtToken.Text = Clipboard.GetText();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtToken.Clear();

        private void TxtKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) ShowVerification();
        }

        private void ChkBase64_Click(object sender, RoutedEventArgs e) => ShowVerification();

        private void BtnClearKey_Click(object sender, RoutedEventArgs e) => TxtKey.Clear();

        // =================================================================
        //  Output
        // =================================================================

        private void Render()
        {
            _decoded = JwtDecoderManager.Decode(TxtToken.Text, DateTimeOffset.Now);
            var d = _decoded;

            bool ok = d.Ok;
            PanelContent.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            PanelEmpty.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            PillRow.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;

            if (!ok)
            {
                _clock.Stop();
                ShowProblem(d.Problem);
                GridClaims.ItemsSource = null;
                return;
            }

            TxtHeader.Text = d.HeaderJson;
            TxtPayload.Text = d.PayloadJson;

            foreach (var claim in d.Claims) claim.Meaning = MeaningOf(claim);
            GridClaims.ItemsSource = d.Claims;

            if (d.IsUnsecured)
            {
                TxtSignature.Text = "";
                TxtSignatureNote.Text = T("Str_Jwt_SigNone", "This token has no signature (alg \"none\"). Anyone can create a token like this, so it proves nothing.");
            }
            else
            {
                TxtSignature.Text = d.Signature;
            }

            ShowAlgorithm(d);
            ShowVerification();
            ShowTimeState();
            if (!_clock.IsEnabled && (d.Expires != null || d.NotBefore != null)) _clock.Start();
            else if (d.Expires == null && d.NotBefore == null) _clock.Stop();
        }

        /// <summary>Checks the signature with the key box and shows the result in the pill and under the signature.</summary>
        private void ShowVerification()
        {
            if (_decoded is not { Ok: true } d) return;
            var v = JwtDecoderManager.Verify(TxtToken.Text, TxtKey.Text, ChkBase64.IsChecked == true);

            string text, tip, kind = "Info";
            PackIconKind icon = PackIconKind.ShieldOffOutline;
            switch (v.State)
            {
                case JwtVerifyState.Valid:
                    text = T("Str_Jwt_VerifyValid", "Signature valid");
                    tip = T("Str_Jwt_VerifyValidTip", "The signature matches the key you gave: the token was not changed and was signed with it.");
                    kind = "Success"; icon = PackIconKind.ShieldCheckOutline;
                    break;
                case JwtVerifyState.Invalid:
                    text = T("Str_Jwt_VerifyInvalid", "Signature does NOT match");
                    tip = T("Str_Jwt_VerifyInvalidTip", "The key is usable but the signature does not match it: the token was changed, or it was signed with another key. Do not trust it.");
                    kind = "Error"; icon = PackIconKind.ShieldAlertOutline;
                    break;
                case JwtVerifyState.Unsecured:
                    text = T("Str_Jwt_VerifyNone", "Unsigned - nothing to check");
                    tip = T("Str_Jwt_SigNone", "This token has no signature (alg \"none\"). Anyone can create a token like this, so it proves nothing.");
                    kind = "Error"; icon = PackIconKind.ShieldOffOutline;
                    break;
                case JwtVerifyState.KeyProblem:
                    text = T("Str_Jwt_VerifyKeyProblem", "The key can't be used");
                    tip = KeyProblemText(v.Problem, v.Algorithm);
                    kind = "Warning"; icon = PackIconKind.ShieldAlertOutline;
                    break;
                case JwtVerifyState.Unsupported:
                    text = string.Format(T("Str_Jwt_VerifyUnsupported", "Can't check {0} here"), v.Algorithm);
                    tip = T("Str_Jwt_VerifyUnsupportedTip", "This tool checks HS256/384/512, RS256/384/512, PS256/384/512 and ES256/384/512.");
                    kind = "Warning"; icon = PackIconKind.ShieldOffOutline;
                    break;
                default:
                    text = T("Str_Jwt_NotVerified", "Not verified");
                    tip = T("Str_Jwt_NotVerifiedTip", "Add the key above to check the signature.");
                    break;
            }

            TxtVerify.Text = text;
            PillVerify.ToolTip = tip;
            IconVerify.Kind = icon;
            SetPill(PillVerify, IconVerify, TxtVerify, kind);

            TxtSignatureNote.Text = d.IsUnsecured ? T("Str_Jwt_SigNone", "This token has no signature (alg \"none\"). Anyone can create a token like this, so it proves nothing.")
                : v.State == JwtVerifyState.NoKey ? string.Format(T("Str_Jwt_SigNote", "{0} bytes. Add the key above to check it."), d.SignatureBytes)
                : $"{string.Format(T("Str_Jwt_SigBytes", "{0} bytes."), d.SignatureBytes)} {tip}";
        }

        private static string KeyProblemText(JwtKeyProblem problem, string alg) => problem switch
        {
            JwtKeyProblem.PublicKeyForHmac => T("Str_Jwt_KeyPublicForHmac", "This token uses an HMAC algorithm, which needs a secret. A public key or certificate is refused on purpose: using one as a secret is a known attack."),
            JwtKeyProblem.SecretForPublicKey => T("Str_Jwt_KeySecretForPublic", "This algorithm needs a public key: paste a PEM public key, a certificate or a JWK, not a secret."),
            JwtKeyProblem.WrongKeyType => string.Format(T("Str_Jwt_KeyWrongType", "This key is of another kind than {0} needs (RSA for RS and PS, EC for ES)."), alg),
            JwtKeyProblem.WrongCurve => string.Format(T("Str_Jwt_KeyWrongCurve", "{0} needs an EC key on its own curve (ES256: P-256, ES384: P-384, ES512: P-521)."), alg),
            JwtKeyProblem.BadBase64 => T("Str_Jwt_KeyBadBase64", "The secret is not valid Base64. Untick \"Secret is Base64\" to use the text as it is."),
            JwtKeyProblem.KidNotFound => T("Str_Jwt_KeyKidNotFound", "The key set has no key with the kid of this token."),
            JwtKeyProblem.BadJwk => T("Str_Jwt_KeyBadJwk", "The JWK is missing fields or they are not valid."),
            _ => T("Str_Jwt_KeyNotRecognized", "The key was not recognised. Use a secret (HS), or a PEM public key, a certificate or a JWK (RS, PS, ES)."),
        };

        private void ShowProblem(JwtProblem problem)
        {
            bool empty = problem == JwtProblem.Empty;
            EmptyBadge.Tag = empty ? PackIconKind.KeyChainVariant : PackIconKind.AlertCircleOutline;
            TxtEmptyTitle.Text = empty ? T("Str_Jwt_EmptyTitle", "Paste a token to read it") : T("Str_Jwt_ProblemTitle", "That doesn't look like a valid token");
            TxtEmptyHint.Text = problem switch
            {
                JwtProblem.Empty => T("Str_Jwt_EmptyHint", "Everything happens on this PC. The token is not sent anywhere or saved, and its signature is not checked."),
                JwtProblem.WrongPartCount => T("Str_Jwt_ProblemParts", "A token has three parts separated by dots: header.payload.signature."),
                JwtProblem.Encrypted => T("Str_Jwt_ProblemEncrypted", "This token is encrypted (it has five parts), so its contents can't be read without the key."),
                JwtProblem.BadEncoding => T("Str_Jwt_ProblemEncoding", "A part of the token isn't valid base64url text. Check that it was copied completely."),
                JwtProblem.HeaderNotJson => T("Str_Jwt_ProblemHeader", "The header doesn't decode to a JSON object."),
                _ => T("Str_Jwt_ProblemPayload", "The payload doesn't decode to a JSON object."),
            };
        }

        private void ShowAlgorithm(JwtDecoded d)
        {
            bool bad = d.IsUnsecured;
            TxtAlg.Text = d.Algorithm.Length == 0 ? T("Str_Jwt_AlgUnknown", "No algorithm") : d.IsUnsecured ? T("Str_Jwt_AlgNone", "alg: none (unsigned)") : $"alg: {d.Algorithm}";
            SetPill(PillAlg, IconAlg, TxtAlg, bad ? "Error" : "Info");
            PillAlg.ToolTip = d.KeyId.Length > 0 ? $"kid: {d.KeyId}" : null;
        }

        /// <summary>The expiry pill; runs every second so "expires in" keeps counting.</summary>
        private void ShowTimeState()
        {
            if (_decoded is not { Ok: true } d) return;
            var now = DateTimeOffset.Now;
            var state = JwtDecoderManager.StateAt(now, d.NotBefore, d.Expires);

            switch (state)
            {
                case JwtTimeState.Expired:
                    TxtTime.Text = string.Format(T("Str_Jwt_Expired", "Expired {0} ago"), Span(now - d.Expires!.Value));
                    SetPill(PillTime, IconTime, TxtTime, "Error", PackIconKind.ClockAlertOutline);
                    break;
                case JwtTimeState.NotYetValid:
                    TxtTime.Text = string.Format(T("Str_Jwt_NotYet", "Not valid yet - starts in {0}"), Span(d.NotBefore!.Value - now));
                    SetPill(PillTime, IconTime, TxtTime, "Warning", PackIconKind.ClockOutline);
                    break;
                case JwtTimeState.Valid:
                    TxtTime.Text = string.Format(T("Str_Jwt_Valid", "Expires in {0}"), Span(d.Expires!.Value - now));
                    SetPill(PillTime, IconTime, TxtTime, "Success", PackIconKind.ClockCheckOutline);
                    break;
                default:
                    TxtTime.Text = T("Str_Jwt_NoExpiry", "No expiry");
                    SetPill(PillTime, IconTime, TxtTime, "Warning", PackIconKind.ClockOutline);
                    break;
            }

            // The claims table shows the same distances.
            foreach (var claim in d.Claims.Where(c => c.Time != null)) claim.Meaning = MeaningOf(claim);
            GridClaims.Items.Refresh();
        }

        private static void SetPill(Border pill, PackIcon icon, TextBlock text, string kind, PackIconKind? iconKind = null)
        {
            pill.SetResourceReference(Border.BackgroundProperty, $"Status{kind}Bg");
            icon.SetResourceReference(PackIcon.ForegroundProperty, $"Status{kind}Fg");
            text.SetResourceReference(TextBlock.ForegroundProperty, $"Status{kind}Fg");
            if (iconKind != null) icon.Kind = iconKind.Value;
        }

        // =================================================================
        //  Text for claims and times
        // =================================================================

        private static string MeaningOf(JwtClaim claim)
        {
            if (claim.Time is { } time)
            {
                var diff = time - DateTimeOffset.Now;
                string when = diff >= TimeSpan.Zero
                    ? string.Format(T("Str_Jwt_InFuture", "in {0}"), Span(diff))
                    : string.Format(T("Str_Jwt_InPast", "{0} ago"), Span(-diff));
                return $"{time.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)} ({when})";
            }
            return Array.IndexOf(KnownClaims, claim.Name) >= 0 ? T("Str_Jwt_Claim_" + claim.Name, "") : "";
        }

        /// <summary>The two biggest units that apply ("2 d 3 h", "5 min 10 s"), so the time still counts down visibly.</summary>
        private static string Span(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            string d = T("Str_Jwt_UnitDay", "{0} d"), h = T("Str_Jwt_UnitHour", "{0} h"), m = T("Str_Jwt_UnitMin", "{0} min"), s = T("Str_Jwt_UnitSec", "{0} s");
            if (span.TotalDays >= 1) return Join(string.Format(d, (int)span.TotalDays), span.Hours > 0 ? string.Format(h, span.Hours) : "");
            if (span.TotalHours >= 1) return Join(string.Format(h, (int)span.TotalHours), span.Minutes > 0 ? string.Format(m, span.Minutes) : "");
            if (span.TotalMinutes >= 1) return Join(string.Format(m, (int)span.TotalMinutes), span.Seconds > 0 ? string.Format(s, span.Seconds) : "");
            return string.Format(s, (int)span.TotalSeconds);

            static string Join(string a, string b) => b.Length == 0 ? a : a + " " + b;
        }

        // =================================================================
        //  Copy
        // =================================================================

        private void BtnCopyHeader_Click(object sender, RoutedEventArgs e) => CopyText(TxtHeader.Text);

        private void BtnCopyPayload_Click(object sender, RoutedEventArgs e) => CopyText(TxtPayload.Text);

        private static void CopyText(string text)
        {
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }
    }
}
