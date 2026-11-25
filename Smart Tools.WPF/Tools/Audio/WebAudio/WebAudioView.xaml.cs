using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Web Audio Saver - UI only. yt-dlp and FFmpeg run in <see cref="WebAudioManager"/>; link checks, command lines and the reading of
    /// their text are the pure code in Core\ (tested). The network is used only when a button is pressed.
    /// </summary>
    public partial class WebAudioView : UserControl
    {
        private readonly WebAudioSettings _settings = WebAudioManager.LoadSettings();
        private readonly ObservableCollection<WebAudioRow> _rows = new();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loadedOnce;
        private bool _loading;

        public WebAudioView()
        {
            InitializeComponent();
            GridFiles.ItemsSource = _rows;
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _loading = true;
            Select(CmbFormat, _settings.Format.ToString());
            Select(CmbQuality, _settings.Quality.ToString());
            _loading = false;
            UpdateRunButton();
            UpdateEmpty();
            if (!WebAudioManager.IsAvailable)
            {
                ShowStatus(Loc.T("Str_WebAudio_NoEngine", "The yt-dlp or FFmpeg program that Web Audio Saver uses is missing from this copy of Smart Tools (Engines\\YTDLP, Engines\\FFMPEG)."), StatusKind.Error);
                TxtVersion.Text = "—";
                return;
            }
            TxtVersion.Text = Loc.T("Str_WebAudio_VersionReading", "Reading the version...");
            string version = await WebAudioManager.VersionAsync(CancellationToken.None);
            TxtVersion.Text = version.Length > 0 ? Loc.F("Str_WebAudio_Version", "yt-dlp version {0}", version) : "—";
        }

        private static void Select(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (item.Tag as string == tag)
                {
                    box.SelectedItem = item;
                    return;
                }
            box.SelectedIndex = 0;
        }

        private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        // =================================================================
        //  Options
        // =================================================================

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || !_loadedOnce) return;
            _settings.Format = Enum.TryParse(TagOf(CmbFormat), out WebAudioFormat f) ? f : WebAudioFormat.Mp3;
            _settings.Quality = Enum.TryParse(TagOf(CmbQuality), out WebAudioQuality q) ? q : WebAudioQuality.Best;
            WebAudioManager.SaveSettings(_settings);
            CmbQuality.IsEnabled = _settings.Format is not (WebAudioFormat.Flac or WebAudioFormat.Wav);   // lossless formats have no bitrate
        }

        private void Clip_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce) return;
            PanelClip.Visibility = ChkClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  The link
        // =================================================================

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText()) TxtUrl.Text = System.Windows.Clipboard.GetText().Trim();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus(Loc.T("Str_WebAudio_ClipboardBusy", "The clipboard is busy. Try again."), StatusKind.Warning);   // another program holds it
            }
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            BtnCheck_Click(sender, e);
        }

        private bool ReadUrl(out string url)
        {
            if (WebAudioMath.TryNormalizeUrl(TxtUrl.Text, out url)) return true;
            ShowStatus(Loc.T("Str_WebAudio_BadLink", "That is not a web link. Paste the address of the page (https://...) or of a sound file."), StatusKind.Warning);
            TxtUrl.Focus();
            return false;
        }

        private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !ReadUrl(out string url) || !WebAudioManager.IsAvailable) return;
            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_WebAudio_Checking", "Asking the site about this link...");
            try
            {
                var result = await WebAudioManager.GetInfoAsync(url, _cts.Token);
                SetBusy(false);
                ResetProgress();
                if (result.Info == null) ShowError(result.Error, result.Text);
                else ShowInfo(result.Info);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ResetProgress();
            }
            catch (Exception ex)
            {
                Log.Warn("Web audio: could not check the link", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_WebAudio_Failed", "Something went wrong: {0}", ex.Message), StatusKind.Error);
            }
        }

        private void ShowInfo(WebAudioInfo info)
        {
            TxtInfoTitle.Text = info.Title;
            var parts = new List<string>();
            if (info.Uploader.Length > 0) parts.Add(info.Uploader);
            if (info.DurationSeconds is { } d) parts.Add(WebAudioMath.FormatTime(d));
            if (info.Site.Length > 0) parts.Add(info.Site);
            TxtInfoLine.Text = string.Join(" · ", parts);
            InfoPanel.Visibility = Visibility.Visible;
            if (info.IsLive) ShowStatus(Loc.T("Str_WebAudio_LiveNote", "This is a live stream. It has no end to save; wait until it is over."), StatusKind.Warning);
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();   // the same button stops what runs
                return;
            }
            if (!WebAudioManager.IsAvailable)
            {
                ShowStatus(Loc.T("Str_WebAudio_NoEngine", "The yt-dlp or FFmpeg program that Web Audio Saver uses is missing from this copy of Smart Tools (Engines\\YTDLP, Engines\\FFMPEG)."), StatusKind.Error);
                return;
            }
            if (!ReadUrl(out string url)) return;

            double? from = null, to = null;
            if (ChkClip.IsChecked == true)
            {
                if (TxtFrom.Text.Trim().Length > 0)
                {
                    if (!WebAudioMath.TryParseTime(TxtFrom.Text, out double f)) { BadTime(TxtFrom); return; }
                    from = f;
                }
                if (TxtTo.Text.Trim().Length > 0)
                {
                    if (!WebAudioMath.TryParseTime(TxtTo.Text, out double t)) { BadTime(TxtTo); return; }
                    to = t;
                }
                if (from != null && to != null && to <= from)
                {
                    ShowStatus(Loc.T("Str_WebAudio_ClipOrder", "The end time must be after the start time."), StatusKind.Warning);
                    TxtTo.Focus();
                    return;
                }
            }

            string destination = OutputCard.EffectiveFolder ?? WebAudioManager.DefaultFolder();
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_WebAudio_CantSaveThere", "Can't save there: {0} Choose another folder.", OutputCard.Status.Text), StatusKind.Error);
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            HideStatus();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_WebAudio_Checking", "Asking the site about this link...");
            try
            {
                var infoResult = await WebAudioManager.GetInfoAsync(url, token);
                if (infoResult.Info == null)
                {
                    SetBusy(false);
                    ResetProgress();
                    ShowError(infoResult.Error, infoResult.Text);
                    return;
                }
                ShowInfo(infoResult.Info);
                if (infoResult.Info.IsLive)
                {
                    SetBusy(false);
                    ResetProgress();
                    return;
                }
                if (from != null && infoResult.Info.DurationSeconds is { } length && from >= length)
                {
                    SetBusy(false);
                    ResetProgress();
                    ShowStatus(Loc.F("Str_WebAudio_ClipPastEnd", "The start time is after the end of the sound ({0}).", WebAudioMath.FormatTime(length)), StatusKind.Warning);
                    return;
                }

                Bar.IsIndeterminate = false;
                TxtProgress.Text = Loc.T("Str_WebAudio_Downloading", "Downloading and converting...");
                var progress = new Progress<WebAudioProgress>(p =>
                {
                    ShowPercent((int)p.Percent);
                    string extra = string.Join(" · ", new[] { p.Speed, p.Eta.Length > 0 ? Loc.F("Str_WebAudio_Eta", "{0} left", p.Eta) : "" }.Where(s => s.Length > 0));
                    TxtProgress.Text = Loc.T("Str_WebAudio_Downloading", "Downloading and converting...") + (extra.Length > 0 ? "  " + extra : "");
                });
                var result = await WebAudioManager.DownloadAsync(url, destination, _settings, from, to, progress, token);
                SetBusy(false);
                if (result.Cancelled)
                {
                    ResetProgress();
                    TxtProgress.Text = Loc.T("Str_WebAudio_Stopped", "Stopped. Nothing was left behind.");
                    return;
                }
                if (!result.Ok)
                {
                    ResetProgress();
                    ShowError(result.Error, result.Text);
                    return;
                }

                ShowPercent(100);
                foreach (string file in result.Files) _rows.Insert(0, new WebAudioRow(file));
                UpdateEmpty();
                GridFiles.SelectedIndex = 0;
                ShowStatus(Loc.F("Str_WebAudio_Saved", "Saved {0}. A file that was already there was not replaced.", Path.GetFileName(result.Files[0])), StatusKind.Success);
                TxtProgress.Text = Loc.T("Str_WebAudio_Done", "Done.");
                if (_settings.OpenFolderWhenDone) ShellExplorer.Show(result.Files[0]);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ResetProgress();
                TxtProgress.Text = Loc.T("Str_WebAudio_Stopped", "Stopped. Nothing was left behind.");
            }
            catch (Exception ex)
            {
                Log.Warn("Web audio: the download failed", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_WebAudio_Failed", "Something went wrong: {0}", ex.Message), StatusKind.Error);
            }
        }

        private void BadTime(TextBox box)
        {
            ShowStatus(Loc.T("Str_WebAudio_BadTime", "Write the time like 1:23 or 83 (seconds)."), StatusKind.Warning);
            box.Focus();
        }

        /// <summary>Says what went wrong in plain words, then yt-dlp's own line for the details.</summary>
        private void ShowError(WebAudioError error, string detail)
        {
            string message = error switch
            {
                WebAudioError.Unsupported => Loc.T("Str_WebAudio_ErrUnsupported", "This address has no sound that the tool can find. Open the page in a browser and copy the address of the video or the sound file."),
                WebAudioError.Private => Loc.T("Str_WebAudio_ErrPrivate", "This video is private or for members only."),
                WebAudioError.SignIn => Loc.T("Str_WebAudio_ErrSignIn", "The site wants a signed-in account (age check, or it suspects an automated visitor). This tool does not sign in."),
                WebAudioError.Unavailable => Loc.T("Str_WebAudio_ErrUnavailable", "The video is unavailable: it was removed, or it is not offered in your country."),
                WebAudioError.Blocked => Loc.T("Str_WebAudio_ErrBlocked", "The site refused the download. Try again later, or update the yt-dlp program in the sidebar."),
                WebAudioError.Live => Loc.T("Str_WebAudio_ErrLive", "This is a live stream. Wait until it is over."),
                WebAudioError.Network => Loc.T("Str_WebAudio_ErrNetwork", "The site could not be reached. Check the internet connection."),
                WebAudioError.NoFfmpeg => Loc.T("Str_WebAudio_ErrFfmpeg", "FFmpeg, which converts the sound, was not found."),
                _ => Loc.T("Str_WebAudio_ErrOther", "The sound could not be saved. If the link is right, the site may have changed: update the yt-dlp program in the sidebar and try again."),
            };
            ShowStatus(detail.Length > 0 ? message + "\n" + detail : message, StatusKind.Error);
        }

        // =================================================================
        //  The saved files
        // =================================================================

        private void UpdateEmpty()
        {
            EmptyState.Visibility = _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (!_busy) TxtProgress.Text = Loc.T("Str_WebAudio_Ready", "Paste a link and press Save the sound.");
        }

        private void GridFiles_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridFiles.SelectedItem is WebAudioRow row) Play(row);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: WebAudioRow row }) Play(row);
        }

        private void BtnShow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: WebAudioRow row }) ShellExplorer.Show(row.Path);
        }

        private void Play(WebAudioRow row)
        {
            try
            {
                if (File.Exists(row.Path)) Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });   // the user's own player
                else ShowStatus(Loc.T("Str_WebAudio_FileGone", "That file is no longer there."), StatusKind.Warning);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Loc.F("Str_WebAudio_CantPlay", "Windows could not open the file: {0}", ex.Message), StatusKind.Warning);
            }
        }

        // =================================================================
        //  The yt-dlp program
        // =================================================================

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !WebAudioManager.IsAvailable) return;
            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_WebAudio_Updating", "Updating the yt-dlp program...");
            try
            {
                var (ok, text) = await WebAudioManager.UpdateAsync(_cts.Token);
                SetBusy(false);
                ResetProgress();
                string version = await WebAudioManager.VersionAsync(CancellationToken.None);
                TxtVersion.Text = version.Length > 0 ? Loc.F("Str_WebAudio_Version", "yt-dlp version {0}", version) : "—";
                ShowStatus(ok ? Loc.F("Str_WebAudio_UpdateDone", "The yt-dlp program is up to date. {0}", text) : Loc.F("Str_WebAudio_UpdateFailed", "The update did not work: {0}", text), ok ? StatusKind.Success : StatusKind.Warning);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ResetProgress();
            }
            catch (Exception ex)
            {
                Log.Warn("Web audio: could not update yt-dlp", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_WebAudio_UpdateFailed", "The update did not work: {0}", ex.Message), StatusKind.Warning);
            }
        }

        // =================================================================
        //  State and messages
        // =================================================================

        private void ShowPercent(int percent)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = percent;
            TxtPercent.Text = percent + "%";
        }

        private void ResetProgress()
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            TxtPercent.Text = "";
            UpdateEmpty();
        }

        private void UpdateRunButton()
        {
            TxtStart.Text = _busy ? Loc.T("Str_WebAudio_Stop", "Stop") : Loc.T("Str_WebAudio_Save", "Save the sound");
            IconStart.Kind = _busy ? PackIconKind.Stop : PackIconKind.CloudDownloadOutline;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            UpdateRunButton();
            if (!busy) Bar.IsIndeterminate = false;
        }

        private enum StatusKind { Success, Warning, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops yt-dlp. A stopped run removes its own temp files.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
