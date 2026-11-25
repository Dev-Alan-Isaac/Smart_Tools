using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// ASCII Video Maker - UI only. <see cref="AsciiVideoManager"/> runs yt-dlp and FFmpeg and streams the frames; the maths, the text file and
    /// the command lines are the pure code in Core\ (tested). The page always says the user is responsible for what they convert.
    /// </summary>
    public partial class AsciiVideoView : UserControl
    {
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".m4v", ".flv", ".gif", ".mpg", ".mpeg", ".ts" };

        private readonly AsciiVideoSettings _settings = AsciiVideoManager.LoadSettings();
        private readonly ObservableCollection<string> _outputs = new();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loadedOnce;
        private bool _loading = true;
        private string _file = "";

        public AsciiVideoView()
        {
            InitializeComponent();
            ListOutputs.ItemsSource = _outputs;
            FileDrop.Attach(this, path =>
            {
                if (_busy || !VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
                SetFile(path);
            });
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            SliderColumns.Value = Math.Clamp(_settings.Columns, AsciiVideoMath.MinColumns, AsciiVideoMath.MaxColumns);
            TxtColumns.Text = Loc.F("Str_AsciiVideo_Columns", "{0} characters", (int)SliderColumns.Value);
            Select(CmbFps, _settings.Fps.ToString());
            Select(CmbRamp, _settings.Ramp.ToString());
            RbWhiteOnBlack.IsChecked = _settings.WhiteOnBlack;
            RbBlackOnWhite.IsChecked = !_settings.WhiteOnBlack;
            ChkMp4.IsChecked = _settings.MakeMp4;
            ChkSound.IsChecked = _settings.KeepSound;
            ChkGif.IsChecked = _settings.MakeGif;
            ChkText.IsChecked = _settings.MakeText;
            _loading = false;
            UpdateState();
            if (!AsciiVideoManager.IsAvailable)
                ShowStatus(Loc.T("Str_AsciiVideo_NoEngine", "FFmpeg, which ASCII Video Maker uses, is missing from this copy of Smart Tools (Engines\\FFMPEG)."), StatusKind.Error);
        }

        private static void Select(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (item.Tag as string == tag)
                {
                    box.SelectedItem = item;
                    return;
                }
            box.SelectedIndex = box.Items.Count - 1;
        }

        private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        // =================================================================
        //  Options
        // =================================================================

        private void Source_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelLink == null) return;
            bool link = RbLink.IsChecked == true;
            PanelLink.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
            PanelFile.Visibility = link ? Visibility.Collapsed : Visibility.Visible;
            UpdateState();
        }

        private void SliderColumns_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtColumns == null) return;
            TxtColumns.Text = Loc.F("Str_AsciiVideo_Columns", "{0} characters", (int)SliderColumns.Value);
            Option_Changed(sender, e);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || !_loadedOnce) return;
            _settings.Columns = (int)SliderColumns.Value;
            _settings.Fps = int.TryParse(TagOf(CmbFps), out int fps) ? fps : 30;
            _settings.Ramp = Enum.TryParse(TagOf(CmbRamp), out AsciiRamp ramp) ? ramp : AsciiRamp.Smooth;
            _settings.WhiteOnBlack = RbWhiteOnBlack.IsChecked == true;
            _settings.MakeMp4 = ChkMp4.IsChecked == true;
            _settings.KeepSound = ChkSound.IsChecked == true;
            _settings.MakeGif = ChkGif.IsChecked == true;
            _settings.MakeText = ChkText.IsChecked == true;
            AsciiVideoManager.SaveSettings(_settings);
            UpdateState();
        }

        private void Clip_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelClip != null) PanelClip.Visibility = ChkClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateState()
        {
            if (!_loadedOnce) return;
            ChkSound.IsEnabled = ChkMp4.IsChecked == true || ChkText.IsChecked == true;
            BtnPreview.IsEnabled = !_busy && RbFile.IsChecked == true && _file.Length > 0;
            TxtStart.Text = _busy ? Loc.T("Str_AsciiVideo_Stop", "Stop") : Loc.T("Str_AsciiVideo_Make", "Make ASCII video");
            IconStart.Kind = _busy ? PackIconKind.Stop : PackIconKind.MovieFilterOutline;
            if (!_busy && TxtProgress.Text.Length == 0) TxtProgress.Text = Loc.T("Str_AsciiVideo_Ready", "Choose a link or a video file, then press Make ASCII video.");
        }

        // =================================================================
        //  The source
        // =================================================================

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText()) TxtUrl.Text = System.Windows.Clipboard.GetText().Trim();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus(Loc.T("Str_AsciiVideo_ClipboardBusy", "The clipboard is busy. Try again."), StatusKind.Warning);
            }
        }

        private void BtnChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Tool_ascii-video_Name", "ASCII Video Maker"),
                Filter = Loc.T("Str_AsciiVideo_VideoFilter", "Videos") + "|" + string.Join(";", VideoExtensions.Select(x => "*" + x)) + "|" + Loc.T("Str_AsciiVideo_AllFiles", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() == true) SetFile(dialog.FileName);
        }

        private void SetFile(string path)
        {
            _file = path;
            TxtFile.Text = Path.GetFileName(path);
            TxtFile.ToolTip = path;
            RbFile.IsChecked = true;
            HideStatus();
            UpdateState();
        }

        private bool ReadRange(out double? from, out double? to)
        {
            from = to = null;
            if (ChkClip.IsChecked != true) return true;
            if (TxtFrom.Text.Trim().Length > 0)
            {
                if (!WebAudioMath.TryParseTime(TxtFrom.Text, out double f)) return BadTime(TxtFrom);
                from = f;
            }
            if (TxtTo.Text.Trim().Length > 0)
            {
                if (!WebAudioMath.TryParseTime(TxtTo.Text, out double t)) return BadTime(TxtTo);
                to = t;
            }
            if (from != null && to != null && to <= from)
            {
                ShowStatus(Loc.T("Str_AsciiVideo_ClipOrder", "The end time must be after the start time."), StatusKind.Warning);
                TxtTo.Focus();
                return false;
            }
            return true;
        }

        private bool BadTime(System.Windows.Controls.TextBox box)
        {
            ShowStatus(Loc.T("Str_AsciiVideo_BadTime", "Write the time like 1:23 or 83 (seconds)."), StatusKind.Warning);
            box.Focus();
            return false;
        }

        // =================================================================
        //  Preview
        // =================================================================

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _file.Length == 0 || !ReadRange(out double? from, out _)) return;
            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_AsciiVideo_Previewing", "Making a preview frame...");
            try
            {
                string? text = await AsciiVideoManager.PreviewAsync(_file, from, _settings, _cts.Token);
                if (text == null) ShowStatus(Loc.T("Str_AsciiVideo_NotVideo", "That file has no video that FFmpeg can read."), StatusKind.Error);
                else ShowPreview(text);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                Log.Warn("ASCII video: the preview failed", ex);
                ShowStatus(Loc.F("Str_AsciiVideo_Failed", "Something went wrong: {0}", ex.Message), StatusKind.Error);
            }
            finally
            {
                SetBusy(false);
                ResetProgress();
            }
        }

        private void ShowPreview(string text)
        {
            bool white = _settings.WhiteOnBlack;
            PreviewHost.Background = white ? Brushes.Black : Brushes.White;   // the video's own colours, not the theme's
            TxtPreview.Foreground = white ? Brushes.White : Brushes.Black;
            TxtPreview.Text = text.TrimEnd('\n');
            EmptyState.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }
            if (!AsciiVideoManager.IsAvailable)
            {
                ShowStatus(Loc.T("Str_AsciiVideo_NoEngine", "FFmpeg, which ASCII Video Maker uses, is missing from this copy of Smart Tools (Engines\\FFMPEG)."), StatusKind.Error);
                return;
            }
            Option_Changed(sender, e);
            if (!_settings.MakeMp4 && !_settings.MakeGif && !_settings.MakeText)
            {
                ShowStatus(Loc.T("Str_AsciiVideo_NoOutput", "Tick at least one thing to make: MP4, GIF or text."), StatusKind.Warning);
                return;
            }
            if (!ReadRange(out double? from, out double? to)) return;

            bool link = RbLink.IsChecked == true;
            string url = "";
            if (link)
            {
                if (!WebAudioMath.TryNormalizeUrl(TxtUrl.Text, out url))
                {
                    ShowStatus(Loc.T("Str_AsciiVideo_BadLink", "That is not a web link. Paste the address of the video page (https://...)."), StatusKind.Warning);
                    TxtUrl.Focus();
                    return;
                }
                if (!AsciiVideoManager.CanDownload)
                {
                    ShowStatus(Loc.T("Str_AsciiVideo_NoDownloader", "The yt-dlp program is missing (Engines\\YTDLP). Choose a video file instead."), StatusKind.Error);
                    return;
                }
            }
            else if (_file.Length == 0 || !File.Exists(_file))
            {
                ShowStatus(Loc.T("Str_AsciiVideo_ChooseFile", "Choose a video file"), StatusKind.Warning);
                return;
            }
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_AsciiVideo_CantSaveThere", "Can't save there: {0} Choose another folder.", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            string destination = OutputCard.EffectiveFolder ?? AsciiVideoManager.DefaultFolder();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            HideStatus();
            SetBusy(true);
            string? downloadFolder = null;
            var progress = new Progress<AsciiVideoProgress>(ShowProgress);
            try
            {
                string input = _file, title = Path.GetFileNameWithoutExtension(_file);
                if (link)
                {
                    downloadFolder = Path.Combine(Path.GetTempPath(), "Smart Tools", "ascii-video", "dl-" + Guid.NewGuid().ToString("N"));
                    TxtProgress.Text = Loc.T("Str_AsciiVideo_Downloading", "Downloading the video...");
                    var (file, error, detail) = await AsciiVideoManager.DownloadAsync(url, downloadFolder, progress, token);
                    if (file == null)
                    {
                        ShowStatus(Loc.T("Str_AsciiVideo_DownloadFailed", "The video could not be downloaded.") + " " + DescribeDownload(error) + (detail.Length > 0 ? "\n" + detail : ""), StatusKind.Error);
                        return;
                    }
                    input = file;
                    title = Path.GetFileNameWithoutExtension(file);
                }

                var result = await AsciiVideoManager.ConvertAsync(input, title, destination, _settings, from, to, progress,
                    text => Dispatcher.BeginInvoke(() => ShowPreview(text)), token);
                if (result.Cancelled)
                {
                    TxtProgress.Text = Loc.T("Str_AsciiVideo_Stopped", "Stopped. What was started has been removed.");
                    return;
                }
                if (!result.Ok)
                {
                    ShowStatus(result.Problem switch
                    {
                        AsciiVideoProblem.NotVideo => Loc.T("Str_AsciiVideo_NotVideo", "That file has no video that FFmpeg can read."),
                        AsciiVideoProblem.TooLong => Loc.F("Str_AsciiVideo_TooLong", "The video is longer than {0} minutes. Tick 'Only a part' and choose up to {0} minutes.", (int)(AsciiVideoMath.MaxSeconds / 60)),
                        _ => Loc.F("Str_AsciiVideo_Failed", "Something went wrong: {0}", result.Detail),
                    }, StatusKind.Error);
                    return;
                }
                _outputs.Clear();
                foreach (string output in result.Outputs) _outputs.Add(output);
                ShowStatus(Loc.T("Str_AsciiVideo_Done", "Done. Nothing that was already there was replaced. For the text version, double-click play.cmd in its folder."), StatusKind.Success);
                TxtProgress.Text = Loc.T("Str_AsciiVideo_DoneShort", "Done.");
                if (result.Outputs.Count > 0) ShellExplorer.Show(result.Outputs[0]);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = Loc.T("Str_AsciiVideo_Stopped", "Stopped. What was started has been removed.");
            }
            catch (Exception ex)
            {
                Log.Warn("ASCII video: the run failed", ex);
                ShowStatus(Loc.F("Str_AsciiVideo_Failed", "Something went wrong: {0}", ex.Message), StatusKind.Error);
            }
            finally
            {
                if (downloadFolder != null)
                {
                    try
                    {
                        if (Directory.Exists(downloadFolder)) Directory.Delete(downloadFolder, recursive: true);   // the downloaded video was only needed for this run
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a leftover temp file is harmless
                    }
                }
                SetBusy(false);
                Bar.IsIndeterminate = false;
                if (Bar.Value < 100) ResetProgress();
            }
        }

        private static string DescribeDownload(WebAudioError error) => error switch
        {
            WebAudioError.Unsupported => Loc.T("Str_WebAudio_ErrUnsupported", "This address has no sound that the tool can find. Open the page in a browser and copy the address of the video or the sound file."),
            WebAudioError.Private => Loc.T("Str_WebAudio_ErrPrivate", "This video is private or for members only."),
            WebAudioError.SignIn => Loc.T("Str_WebAudio_ErrSignIn", "The site wants a signed-in account (age check, or it suspects an automated visitor). This tool does not sign in."),
            WebAudioError.Unavailable => Loc.T("Str_WebAudio_ErrUnavailable", "The video is unavailable: it was removed, or it is not offered in your country."),
            WebAudioError.Blocked => Loc.T("Str_WebAudio_ErrBlocked", "The site refused the download. Try again later, or update the yt-dlp program in the sidebar."),
            WebAudioError.Network => Loc.T("Str_WebAudio_ErrNetwork", "The site could not be reached. Check the internet connection."),
            _ => Loc.T("Str_AsciiVideo_DownloadOther", "If the link is right, the site may have changed: update yt-dlp in Web Audio Saver and try again."),
        };

        private void ShowProgress(AsciiVideoProgress p)
        {
            Bar.IsIndeterminate = p.Stage is AsciiVideoStage.Reading or AsciiVideoStage.Gif or AsciiVideoStage.Sound;
            if (!Bar.IsIndeterminate)
            {
                Bar.Value = p.Percent;
                TxtPercent.Text = (int)p.Percent + "%";
            }
            TxtProgress.Text = p.Stage switch
            {
                AsciiVideoStage.Downloading => Loc.T("Str_AsciiVideo_Downloading", "Downloading the video..."),
                AsciiVideoStage.Reading => Loc.T("Str_AsciiVideo_Reading", "Reading the video..."),
                AsciiVideoStage.Converting => Loc.F("Str_AsciiVideo_Converting", "Frame {0:N0} of about {1:N0}...", p.Frame, p.Frames),
                AsciiVideoStage.Gif => Loc.T("Str_AsciiVideo_MakingGif", "Making the GIF..."),
                _ => Loc.T("Str_AsciiVideo_MakingSound", "Saving the sound for the text player..."),
            };
        }

        // =================================================================
        //  Results
        // =================================================================

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Loc.F("Str_AsciiVideo_CantOpen", "Windows could not open it: {0}", ex.Message), StatusKind.Warning);
            }
        }

        private void BtnShowOutput_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string path }) ShellExplorer.Show(path);
        }

        // =================================================================
        //  State and messages
        // =================================================================

        private void ResetProgress()
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            TxtPercent.Text = "";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            UpdateState();
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

        /// <summary>Called when the window closes: stops yt-dlp and FFmpeg. A stopped run removes what it had made.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
