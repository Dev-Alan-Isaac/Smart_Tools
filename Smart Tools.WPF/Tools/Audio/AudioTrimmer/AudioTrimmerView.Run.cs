using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Audio Trimmer &amp; Joiner - playing the preview and exporting the result.</summary>
    public partial class AudioTrimmerView
    {
        private enum PlayMode { None, Audition, All }

        private enum StatusKind { Info, Success, Warning, Error }

        private AudioTrimPlayer _player = null!;
        private DispatcherTimer _posTimer = null!;
        private PlayMode _playMode = PlayMode.None;
        private CancellationTokenSource? _exportCts;
        private bool _isExporting;
        private string _lastExportPath = "";

        // =================================================================
        //  Preview
        // =================================================================

        private void InitPlayback()
        {
            _player = new AudioTrimPlayer();
            _player.Ended += OnPlayerEnded;
            _player.Failed += OnPlayerFailed;
            _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _posTimer.Tick += (_, _) => PositionTick();
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting || _isLoading) return;
            if (_player.IsPlaying) { _player.Pause(); UpdatePlayUi(); return; }
            if (_player.IsPaused) { _player.Resume(); UpdatePlayUi(); return; }
            await StartAuditionAsync(null);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        /// <summary>Plays the selection, or the clip from the playhead. No fades: it is for finding the right spot.</summary>
        private async Task StartAuditionAsync(double? from)
        {
            if (_selected == null) return;
            var clip = _selected;

            double a, b;
            if (Wave.GetSelection() is { } sel)
            {
                a = from is double f && f >= sel.Start && f < sel.End ? f : sel.Start;
                b = sel.End;
            }
            else
            {
                a = from ?? (double.IsNaN(Wave.Playhead) ? clip.Start : Wave.Playhead);
                b = clip.End;
                if (a < clip.Start || a >= clip.End - 0.05) a = clip.Start;
            }

            if (!await EnsurePlayableAsync(clip.Source)) return;
            if (_isExporting) return;

            MarkPlaying(-1);
            _playMode = PlayMode.Audition;
            _player.Play(new[] { new AudioTrimPlayItem(clip.Source.PlayPath ?? clip.Source.Path, a, b, 0, 0, _clips.IndexOf(clip)) });
            _posTimer.Start();
            UpdatePlayUi();
        }

        /// <summary>Plays every clip in order with the join fades and the fade in / out of the whole result.</summary>
        private async void BtnPlayAll_Click(object sender, RoutedEventArgs e)
        {
            if (_clips.Count == 0 || _isExporting || _isLoading) return;
            var snapshot = _clips.ToList();
            foreach (var source in snapshot.Select(c => c.Source).Distinct())
                if (!await EnsurePlayableAsync(source)) return;
            if (_isExporting) return;

            var items = new List<AudioTrimPlayItem>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var c = snapshot[i];
                items.Add(new AudioTrimPlayItem(c.Source.PlayPath ?? c.Source.Path, c.Start, c.End,
                    i > 0 ? AudioTrimmerManager.JoinFadeSeconds : 0,
                    i < snapshot.Count - 1 ? AudioTrimmerManager.JoinFadeSeconds : 0, i));
            }

            var o = ReadOptions();
            _playMode = PlayMode.All;
            _player.Play(items, o.FadeInSeconds, o.FadeOutSeconds);
            _posTimer.Start();
            UpdatePlayUi();
        }

        /// <summary>A format Windows can't play is converted to a WAV once, in the background.</summary>
        private async Task<bool> EnsurePlayableAsync(AudioTrimSource source)
        {
            if (source.PlayPath != null) return true;
            if (!AudioTrimmerManager.NeedsPlaybackCopy(source.Path)) { source.PlayPath = source.Path; return true; }

            ShowStatus(Str("Str_AudioTrimmer_PreparingPreview", "Preparing the preview…"), StatusKind.Info);
            try
            {
                source.PlayPath = await AudioTrimmerManager.MakePlaybackCopyAsync(source.Path, source.Duration, _lifetimeCts.Token);
                HideStatus();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (InvalidOperationException ex)
            {
                ShowStatus(ex.Message, StatusKind.Error);
                return false;
            }
        }

        private void PositionTick()
        {
            if (!_player.IsActive) { _posTimer.Stop(); return; }

            int index = _player.ItemIndex;
            if (_playMode == PlayMode.All && index >= 0 && index < _clips.Count && !ReferenceEquals(_clips[index], _selected))
                SelectClip(_clips[index]);                          // the waveform follows the clip that is playing

            double t = _player.SourceTime;
            Wave.Playhead = t;
            if (_player.IsPlaying) Wave.EnsureVisible(t);
            MarkPlaying(_player.IsPlaying ? index : -1);
            UpdateTimeText();
            UpdateButtonsForPlayhead();
        }

        private void MarkPlaying(int index)
        {
            for (int i = 0; i < _clips.Count; i++)
            {
                bool playing = i == index;
                if (_clips[i].IsPlaying != playing) _clips[i].IsPlaying = playing;
            }
        }

        /// <summary>Only the split button depends on the playhead; cheap to refresh while it moves.</summary>
        private void UpdateButtonsForPlayhead()
        {
            if (_selected == null || _isExporting) return;
            double ph = Wave.Playhead;
            BtnSplit.IsEnabled = !double.IsNaN(ph) && ph - _selected.Start >= AudioTrimmerManager.MinClipSeconds && _selected.End - ph >= AudioTrimmerManager.MinClipSeconds;
        }

        private void StopPlayback()
        {
            if (!_viewConstructed) return;
            _player.Stop();
            _posTimer.Stop();
            _playMode = PlayMode.None;
            MarkPlaying(-1);
            UpdatePlayUi();
            UpdateButtons();
            UpdateTimeText();
        }

        private void OnPlayerEnded()
        {
            _posTimer.Stop();
            _playMode = PlayMode.None;
            MarkPlaying(-1);
            UpdatePlayUi();
            UpdateButtons();
            UpdateTimeText();
        }

        private void OnPlayerFailed(string message)
        {
            OnPlayerEnded();
            ShowStatus(message, StatusKind.Error);
        }

        private void UpdatePlayUi()
        {
            bool playing = _player.IsPlaying;
            IconPlay.Kind = playing ? PackIconKind.Pause : PackIconKind.Play;
            BtnPlay.SetResourceReference(FrameworkElement.ToolTipProperty, playing ? "Str_AudioTrimmer_Pause" : _player.IsPaused ? "Str_AudioTrimmer_Resume" : "Str_AudioTrimmer_PlayTooltip");
            BtnStop.IsEnabled = _player.IsActive && !_isExporting;
        }

        /// <summary>"0:12.3 / 4:30.0": where the playhead is in the joined result, and how long the result is.</summary>
        private void UpdateTimeText()
        {
            if (!_viewConstructed) return;
            double total = AudioTrimmerManager.TotalSeconds(_clips);
            int index;
            double t;
            if (_player.IsActive) { index = _player.ItemIndex; t = _player.SourceTime; }
            else { index = SelectedIndex(); t = _selected == null ? 0 : double.IsNaN(Wave.Playhead) ? _selected.Start : Wave.Playhead; }

            double position = 0;
            if (index >= 0 && index < _clips.Count)
            {
                for (int i = 0; i < index; i++) position += _clips[i].Duration;
                position += Math.Clamp(t - _clips[index].Start, 0, _clips[index].Duration);
            }
            TxtTime.Text = $"{AudioTrimTime.Format(position)} / {AudioTrimTime.Format(total)}";
        }

        private void ShutDownPlayback()
        {
            _posTimer.Stop();
            _player.Dispose();
        }

        // =================================================================
        //  Export
        // =================================================================

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) { _exportCts?.Cancel(); return; }        // the same button is "Cancel" while exporting
            if (_clips.Count == 0 || _isLoading) return;
            if (AudioTrimmerManager.MissingEngine() is { } missing) { ShowStatus(missing, StatusKind.Error); return; }

            var options = ReadOptions();
            var clips = AudioTrimmerManager.Snapshot(_clips);
            StopPlayback();

            string ext = AudioTrimmerManager.FileExtension(options.Format);
            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                Filter = AudioTrimmerManager.DialogFilter(options.Format),
                FileName = SuggestedName(clips) + "." + ext,
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = true,
            };
            string? firstFolder = Path.GetDirectoryName(clips[0].Source.Path);
            if (firstFolder != null && Directory.Exists(firstFolder)) dialog.InitialDirectory = firstFolder;      // only for this dialog, never saved
            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;
            _exportCts = new CancellationTokenSource();
            var ct = _exportCts.Token;
            SetExporting(true);
            var started = DateTime.UtcNow;
            DateTime lastShown = DateTime.MinValue;
            var progress = new Progress<double>(f =>
            {
                if (!_isExporting || (DateTime.UtcNow - lastShown).TotalMilliseconds < 100) return;      // about 10 updates a second
                lastShown = DateTime.UtcNow;
                ExportProgress.Value = f * 100;
                TxtStatus.Text = string.Format(Str("Str_AudioTrimmer_Exporting", "Exporting… {0}"), f.ToString("P0", CultureInfo.CurrentCulture));
            });

            try
            {
                var result = await Task.Run(() => AudioTrimmerManager.ExportAsync(clips, options, path, progress, ct));
                _lastExportPath = result.Path;
                SetExporting(false);

                string gain = result.GainDb is double g
                    ? "  " + string.Format(Str("Str_AudioTrimmer_PeakNote", "Peak normalized ({0} dB)."), g.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture))
                    : result.WasSilent ? "  " + Str("Str_AudioTrimmer_SilentNote", "The audio is silent, so the level was left as it is.") : "";
                ShowStatus(string.Format(Str("Str_AudioTrimmer_Saved", "Saved {0}  ({1}, {2})"),
                    Path.GetFileName(result.Path), AudioTrimTime.Format(result.Seconds), OutputFolderManager.FormatBytes(result.Bytes)) + gain, StatusKind.Success);
                BtnShowFolder.Visibility = Visibility.Visible;

                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                {
                    try { ToastNotificationManager.ShowToast(Str("Str_AudioTrimmer_ToastTitle", "Audio exported"), Path.GetFileName(result.Path)); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { /* no toast is not a problem */ }
                }
            }
            catch (OperationCanceledException)
            {
                SetExporting(false);
                ShowStatus(Str("Str_AudioTrimmer_ExportCancelled", "Cancelled. Nothing was saved."), StatusKind.Info);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SetExporting(false);
                ShowStatus(string.Format(Str("Str_AudioTrimmer_ExportFailed", "Couldn't export: {0}"), ex.Message), StatusKind.Error);
            }
            finally
            {
                _exportCts?.Dispose();
                _exportCts = null;
            }
        }

        private static string SuggestedName(IReadOnlyList<AudioTrimClipState> clips)
        {
            bool oneSource = clips.Select(c => c.Source).Distinct().Count() == 1;
            string name = oneSource ? Path.GetFileNameWithoutExtension(clips[0].Source.Path) + " (edited)" : "Joined audio";
            return string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        }

        /// <summary>Locks the setup while a file is written; the Export button becomes Cancel.</summary>
        private void SetExporting(bool exporting)
        {
            _isExporting = exporting;
            OptionsScrollViewer.IsEnabled = !exporting;
            IconExport.Kind = exporting ? PackIconKind.Close : PackIconKind.FileExportOutline;
            TxtExportLabel.SetResourceReference(TextBlock.TextProperty, exporting ? "Str_Common_Cancel" : "Str_AudioTrimmer_ExportAudio");
            ExportProgress.Value = 0;
            ExportProgress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
            if (exporting)
            {
                BtnExport.IsEnabled = true;
                BtnShowFolder.Visibility = Visibility.Collapsed;
                ShowStatus(string.Format(Str("Str_AudioTrimmer_Exporting", "Exporting… {0}"), 0.ToString("P0", CultureInfo.CurrentCulture)), StatusKind.Info);
            }
            Wave.IsHitTestVisible = _selected != null && !exporting;
            UpdateButtons();
        }

        private void BtnShowFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastExportPath.Length == 0 || !File.Exists(_lastExportPath)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastExportPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing more to try */ }
        }

        // =================================================================
        //  Status line
        // =================================================================

        private void ShowStatus(string text, StatusKind kind)
        {
            StatusRow.Visibility = Visibility.Visible;
            TxtStatus.Text = text;
            string token = kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Warning => "StatusWarningFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            };
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, token);
            if (kind != StatusKind.Success) BtnShowFolder.Visibility = Visibility.Collapsed;
            if (!_isExporting) UiMotion.FadeIn(TxtStatus);
        }

        private void HideStatus()
        {
            if (_isExporting) return;
            StatusRow.Visibility = Visibility.Collapsed;
            BtnShowFolder.Visibility = Visibility.Collapsed;
        }
    }
}
