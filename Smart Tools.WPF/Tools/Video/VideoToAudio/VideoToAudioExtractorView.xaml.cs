using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Video to Audio - UI only. Formats, arguments, estimates, the file list
    /// and settings are in <see cref="VideoToAudioManager"/>; ffprobe and the
    /// run in VideoToAudioManager.Run.cs.
    /// </summary>
    public partial class VideoToAudioExtractorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_video-to-audio_Name", "Video to Audio");
        private const string ReadyHint = "Add videos, pick a format, then press Extract Audio.";

        private readonly List<VtaSource> _sources = new();
        private readonly ObservableCollection<VtaQueueGroup> _queueDisplay = new();
        private readonly ObservableCollection<VtaFileItem> _rows = new();

        /// <summary>Rows taken off by hand: stay off when folders are read again.</summary>
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _expandCts;
        private CancellationTokenSource _probeCts = new();
        private int _probing;
        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        /// <summary>The run bar shows a run's summary: the Output card's refresh after the run mustn't replace it.</summary>
        private bool _holdStatus;
        private readonly bool _viewConstructed;

        // Made in code: no x:Name inside the Output card's content (MC3093).
        private CheckBox ChkOpenWhenDone = null!;

        public VideoToAudioExtractorView()
        {
            InitializeComponent();
            BuildOutputOptions();
            InputCard.ItemsSource = _queueDisplay;
            InputCard.FileFilter = $"Videos|{string.Join(";", VideoToAudioManager.VideoExtensions.Select(x => "*" + x))}|All files (*.*)|*.*";
            DataGridFiles.ItemsSource = _rows;
            OutputCard.DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Smart Tools", "Video to Audio");
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops ffmpeg, ffprobe and any folder listing.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _expandCts?.Cancel();
            _probeCts.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(VideoToAudioManager.LoadSettings());
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateEngineWarning();
            UpdateFormatUi();
            UpdateQueueHint();
            UpdateSummary();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            ColResult.Visibility = width < 980 ? Visibility.Collapsed : Visibility.Visible;
            ColLength.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void UpdateEngineWarning()
        {
            string? missing = VideoToAudioManager.MissingEngine();
            TxtFfmpegWarning.Text = missing ?? "";
            FfmpegWarning.Visibility = missing == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Files and folders
        // =================================================================

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
            e.Handled = true;
        }

        private void AddSources(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                // A loose file must be a video ("All files" in the dialog lets anything through).
                if (!isFolder && !VideoToAudioManager.IsVideo(path)) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new VtaSource { FullPath = path, IsFolder = isFolder });
                _removed.Remove(path);
                added = true;
            }
            if (!added)
            {
                InputCard.PopDropArea();
                return;
            }
            InputCard.PopDropArea();
            _ = RefreshQueueAsync();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not Button { Tag: VtaQueueGroup group }) return;
            if (group.IsFolder)
                _sources.RemoveAll(s => s.IsFolder && string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase));
            else
                _sources.RemoveAll(s => !s.IsFolder && group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
            _ = RefreshQueueAsync();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning) AddSources(e.Paths);
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isRunning) return;
            _expandCts?.Cancel();
            _probeCts.Cancel();
            _probeCts = new CancellationTokenSource();
            _sources.Clear();
            _removed.Clear();
            _rows.Clear();
            RebuildQueueDisplay();
            UpdateSummary();
            TxtProgress.Text = ReadyHint;
            TxtProgressPercent.Text = "";
            ProgressRun.Value = 0;
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
            else RebuildQueueDisplay();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in VideoToAudioManager.BuildQueueGroups(_sources, _rows, InputCard.IncludeSubfolders))
                _queueDisplay.Add(group);
            UpdateQueueHint();
        }

        private void UpdateQueueHint() =>
            InputCard.SetEmpty(_queueDisplay.Count == 0);

        /// <summary>
        /// Reads the added folders again off the UI thread and merges: rows
        /// still there keep their place, track choice and status; new videos go
        /// at the end and are probed; rows no longer found come off.
        /// </summary>
        private async Task RefreshQueueAsync()
        {
            _holdStatus = false;
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            var sources = _sources.ToList();
            bool subfolders = InputCard.IncludeSubfolders;

            bool slow = sources.Any(s => s.IsFolder);
            if (slow)
            {
                ProgressRun.IsIndeterminate = true;
                TxtProgress.Text = "Reading folders...";
            }

            List<(string Path, long Size)> files;
            try
            {
                files = await Task.Run(() => VideoToAudioManager.ListVideos(sources, subfolders, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't read the folders: " + ex.Message;
                return;
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) ProgressRun.IsIndeterminate = false;
            }
            if (cts.IsCancellationRequested || _isRunning) return;

            // Merge on the UI thread, after the await (duplicate check can't race a second add).
            var wanted = files.Where(f => !_removed.Contains(f.Path)).ToList();
            var keep = new HashSet<string>(wanted.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _rows.Where(r => !keep.Contains(r.FullPath)).ToList()) _rows.Remove(gone);

            var have = new HashSet<string>(_rows.Select(r => r.FullPath), StringComparer.OrdinalIgnoreCase);
            var fresh = new List<VtaFileItem>();
            foreach (var (path, size) in wanted)
            {
                if (!have.Add(path)) continue;
                var item = new VtaFileItem { FullPath = path, SizeBytes = size };
                _rows.Add(item);
                fresh.Add(item);
            }

            RebuildQueueDisplay();
            UpdateSummary();
            if (slow || fresh.Count > 0) TxtProgress.Text = fresh.Count > 0 ? $"Reading {VtaText.Plural(fresh.Count, "video", "videos")}..." : ReadyHint;
            if (wanted.Count == 0 && sources.Count > 0) TxtProgress.Text = "No videos found in what was added.";
            if (fresh.Count > 0) await ProbeAsync(fresh);
        }

        /// <summary>ffprobe for new rows, four at a time; each row updates as soon as it's read.</summary>
        private async Task ProbeAsync(IReadOnlyList<VtaFileItem> items)
        {
            var token = _probeCts.Token;
            _probing++;
            using var gate = new SemaphoreSlim(4);
            var options = BuildOptions();
            try
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        VtaProbe probe;
                        try
                        {
                            probe = await VideoToAudioManager.ProbeAsync(item.FullPath, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        ApplyProbe(item, probe);
                        UpdateSummary();
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
            finally
            {
                _probing--;
            }
            if (!_isRunning && _probing == 0 && !token.IsCancellationRequested) TxtProgress.Text = ReadyText();
        }

        private void ApplyProbe(VtaFileItem item, VtaProbe probe)
        {
            if (!_rows.Contains(item)) return;   // removed while it was being read
            item.Duration = probe.Duration;
            item.Tracks = probe.Tracks;
            item.SelectedTrack = probe.Tracks.FirstOrDefault();
            if (probe.Error != null)
            {
                item.Message = probe.Error;
                item.Status = VtaItemStatus.Unreadable;
            }
            else if (probe.Tracks.Count == 0)
            {
                item.Message = "This video has no sound track, so there's nothing to extract.";
                item.Status = VtaItemStatus.NoAudio;
            }
            else
            {
                item.Message = probe.Tracks.Count > 1
                    ? $"{probe.Tracks.Count} sound tracks - pick one in the Audio track column, or choose Every track in the sidebar."
                    : "Ready to extract.";
                item.Status = VtaItemStatus.Ready;
            }
            item.ResultText = VideoToAudioManager.EstimateText(item, BuildOptions());
        }

        // =================================================================
        //  Settings controls
        // =================================================================

        private static T CheckedTag<T>(Panel panel, T fallback) where T : struct, Enum =>
            panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void CheckTag(Panel panel, string tag)
        {
            var radio = panel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);
            if (radio != null) radio.IsChecked = true;
        }

        private static T ComboTag<T>(ComboBox combo, T fallback) where T : struct, Enum =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void SelectTag(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null) combo.SelectedItem = item;
        }

        private VtaFormat SelectedFormat => CheckedTag(FormatChips, VtaFormat.Mp3);

        private int SelectedBitrate =>
            BitrateChips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && int.TryParse(s, out int v) ? v : 192;

        private VtaOptions BuildOptions() => new()
        {
            Format = SelectedFormat,
            BitrateKbps = SelectedBitrate,
            SampleRate = ComboTag(CmbSampleRate, VtaSampleRate.Original),
            TrackMode = CheckedTag(TrackModeChips, VtaTrackMode.Chosen),
            KeepTags = ChkKeepTags.IsChecked == true,
            TitleFromFileName = ChkTitleFromName.IsChecked == true,
            Artist = TxtArtist.Text,
            Album = TxtAlbum.Text,
        };

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Setting_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            AfterSettingChanged();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            AfterSettingChanged();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
        }

        private void TrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore || sender is not ComboBox { DataContext: VtaFileItem item }) return;
            item.ResultText = VideoToAudioManager.EstimateText(item, BuildOptions());
            UpdateSummary();
        }

        private void AfterSettingChanged()
        {
            _holdStatus = false;
            UpdateFormatUi();
            var options = BuildOptions();
            foreach (var row in _rows.Where(r => r.Status != VtaItemStatus.Done))
                row.ResultText = VideoToAudioManager.EstimateText(row, options);
            UpdateSummary();
            SaveSettings();
        }

        private void UpdateFormatUi()
        {
            var format = SelectedFormat;
            bool lossless = VideoToAudioManager.IsLossless(format);
            BitrateChips.IsEnabled = !lossless;
            BitrateChips.Opacity = lossless ? 0.5 : 1;
            TxtFormatNote.Text = VideoToAudioManager.FormatNote(format);
            TxtTrackNote.Text = CheckedTag(TrackModeChips, VtaTrackMode.Chosen) == VtaTrackMode.Every
                ? "Videos with several tracks give one file each, named by language: \"Movie (eng).mp3\", \"Movie (spa).mp3\"."
                : "Most videos have one sound track. When one has several (languages, commentary), pick it on its row.";
            OutputCard.ExampleFileName = "video" + VideoToAudioManager.Extension(format);
        }

        private void BuildOutputOptions()
        {
            ChkOpenWhenDone = new CheckBox { Content = "Open the folder when done", Margin = new Thickness(0, 8, 0, 0) };
            ChkOpenWhenDone.SetResourceReference(ForegroundProperty, "SidebarText");
            ChkOpenWhenDone.Checked += Option_Changed;
            ChkOpenWhenDone.Unchecked += Option_Changed;
            OutputCard.Options = ChkOpenWhenDone;
        }

        private void OutputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (!_isRunning && !_holdStatus && _probing == 0 && _rows.Count > 0) TxtProgress.Text = ReadyText();
        }

        private void ApplySettings(VtaSettings s)
        {
            _applyingSettings = true;
            try
            {
                CheckTag(FormatChips, s.Format.ToString());
                CheckTag(BitrateChips, s.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SelectTag(CmbSampleRate, s.SampleRate.ToString());
                CheckTag(TrackModeChips, s.TrackMode.ToString());
                ChkKeepTags.IsChecked = s.KeepTags;
                ChkTitleFromName.IsChecked = s.TitleFromFileName;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkOpenWhenDone.IsChecked = s.OpenWhenDone;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            var o = BuildOptions();
            VideoToAudioManager.SaveSettings(new VtaSettings
            {
                Format = o.Format,
                BitrateKbps = o.BitrateKbps,
                SampleRate = o.SampleRate,
                TrackMode = o.TrackMode,
                KeepTags = o.KeepTags,
                TitleFromFileName = o.TitleFromFileName,
                IncludeSubfolders = InputCard.IncludeSubfolders,
                OpenWhenDone = ChkOpenWhenDone.IsChecked == true,
            });
        }

        // =================================================================
        //  The list
        // =================================================================

        private string ReadyText()
        {
            int ready = _rows.Count(r => r.CanRun);
            if (_rows.Count == 0) return ReadyHint;
            if (ready == 0)
                return _rows.Any(r => r.Status == VtaItemStatus.Done)
                    ? "Everything in the list is extracted. Add more videos, or clear the list."
                    : "None of these videos has a sound track that can be read.";
            string where = OutputCard.IsSameAsSource ? "next to the videos" : "to " + (OutputCard.EffectiveFolder ?? "the output folder");
            return $"{VtaText.Plural(ready, "video", "videos")} ready · saves {where}";
        }

        private void UpdateSummary()
        {
            int count = _rows.Count;
            bool any = count > 0;
            if (any && SummaryPanel.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPanel, 0.9);
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFiles.Text = VideoToAudioManager.DescribeList(_rows);

            string? estimate = VideoToAudioManager.DescribeEstimate(_rows, BuildOptions());
            TxtSummaryEstimate.Text = estimate ?? "";
            PillEstimate.Visibility = estimate == null ? Visibility.Collapsed : Visibility.Visible;

            int done = _rows.Count(r => r.Status == VtaItemStatus.Done);
            int failed = _rows.Count(r => r.Status == VtaItemStatus.Failed);
            TxtSummaryDone.Text = $"{done:N0} done";
            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFailed.Text = $"{failed:N0} failed";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

            UiMotion.FadeVisibility(EmptyStatePanel, !any);
            UiMotion.FadeVisibility(DataGridFiles, any);
            BtnRemoveFiles.IsEnabled = !_isRunning && any;
            BtnClearFiles.IsEnabled = !_isRunning && any;
        }

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (_isRunning) return;
            var chosen = DataGridFiles.SelectedItems.OfType<VtaFileItem>().ToList();
            if (chosen.Count == 0) return;
            int next = _rows.IndexOf(chosen[0]);
            foreach (var item in chosen)
            {
                _removed.Add(item.FullPath);
                _rows.Remove(item);
                // A loose file taken off the grid also leaves the Files card.
                _sources.RemoveAll(s => !s.IsFolder && string.Equals(s.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
            }
            if (_rows.Count > 0) DataGridFiles.SelectedItem = _rows[Math.Clamp(next, 0, _rows.Count - 1)];
            RebuildQueueDisplay();
            UpdateSummary();
            TxtProgress.Text = ReadyText();
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ComboBox) return;
            if (e.Key == Key.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
        }

        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) != null) return;
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not VtaFileItem item) return;
            string path = item.Status == VtaItemStatus.Done && item.OutputPaths.Count > 0 && File.Exists(item.OutputPaths[0])
                ? item.OutputPaths[0]
                : item.FullPath;
            OpenWithShell(path);
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_OpenFile", "Couldn't open {0}: {1}", Path.GetFileName(path), ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e) => OpenOutput(quiet: false);

        private void OpenOutput(bool quiet)
        {
            string? firstResult = (DataGridFiles.SelectedItem as VtaFileItem)?.OutputPaths.FirstOrDefault(File.Exists)
                                  ?? _rows.SelectMany(r => r.OutputPaths).FirstOrDefault(File.Exists);
            string? target = OutputCard.IsSameAsSource ? firstResult : firstResult ?? OutputCard.EffectiveFolder;
            if (target != null && ShellExplorer.Show(target) == ShellExplorer.ShowResult.Shown) return;
            if (!quiet)
                MessageBox.Show(OutputCard.IsSameAsSource
                        ? "The audio is saved next to each video - extract something first."
                        : $"Nothing has been saved to {OutputCard.EffectiveFolder} yet.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Extract
        // =================================================================

        private async void BtnExtract_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Cancelling - finished files are kept...";
                return;
            }
            await StartAsync();
        }

        private async Task StartAsync()
        {
            UpdateEngineWarning();
            if (VideoToAudioManager.MissingEngine() is string missing)
            {
                TxtProgress.Text = missing;
                UiMotion.Pop(FfmpegWarning, 0.9);
                return;
            }
            if (_rows.Count == 0)
            {
                TxtProgress.Text = "Add some videos first.";
                InputCard.PopDropArea();
                return;
            }
            if (_probing > 0 || _rows.Any(r => r.Status == VtaItemStatus.Reading))
            {
                TxtProgress.Text = "Still reading the videos - try again in a moment.";
                return;
            }
            var pending = _rows.Where(r => r.CanRun).ToList();
            if (pending.Count == 0)
            {
                TxtProgress.Text = ReadyText();
                return;
            }
            if (!OutputCard.IsSameAsSource)
            {
                if (!OutputCard.Status.CanSave && OutputCard.Status.State != OutputFolderState.None)
                {
                    TxtProgress.Text = "Can't save there: " + OutputCard.Status.Text;
                    return;
                }
                try
                {
                    OutputCard.EnsureFolder();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    TxtProgress.Text = "The output folder couldn't be created: " + ex.Message;
                    return;
                }
            }

            var options = BuildOptions();
            var cts = _runCts = new CancellationTokenSource();
            SetRunning(true);
            foreach (var row in pending) row.Status = VtaItemStatus.Ready;
            var progress = new Progress<VtaProgress>(ReportProgress);

            VtaRunResult? result = null;
            try
            {
                result = await VideoToAudioManager.RunAsync(pending, options, (source, name) => OutputCard.ResultPathFor(source, name), progress, cts.Token);
            }
            catch (Exception ex)
            {
                TxtProgress.Text = ex.Message;
            }
            finally
            {
                _holdStatus = true;
                SetRunning(false);
                _runCts = null;
                cts.Dispose();
            }
            UpdateSummary();
            if (result == null) return;

            ProgressRun.Value = 100;
            TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
            TxtProgress.Text = VideoToAudioManager.DescribeResult(result);
            if (result.Files.Count > 0)
            {
                UiMotion.Pop(PillDone, 0.85);
                if (ChkOpenWhenDone.IsChecked == true) OpenOutput(quiet: true);
            }
        }

        private void ReportProgress(VtaProgress p)
        {
            if (!_isRunning) return;
            ProgressRun.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            string count = $"{p.Finished:N0} of {p.Total:N0}";
            TxtProgress.Text = p.CurrentName != null ? $"Extracting {p.CurrentName} · {count}" : $"Extracting · {count} done";
            UpdateSummary();
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("video-to-audio-extractor");
            else { _activity?.Dispose(); _activity = null; }
            IconExtract.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.FileMusicOutline;
            if (running) TxtExtractLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtExtractLabel.SetResourceReference(TextBlock.TextProperty, "Str_VideoToAudio_ExtractAudio");
            UiMotion.Pop(BtnExtract, 0.96);

            OptionsScrollViewer.IsEnabled = !running;
            OutputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();   // free space changed (and a default folder may now exist)
            foreach (var row in _rows) row.Locked = running;
            BtnRemoveFiles.IsEnabled = !running && _rows.Count > 0;
            BtnClearFiles.IsEnabled = !running && _rows.Count > 0;

            if (running)
            {
                ProgressRun.IsIndeterminate = false;
                ProgressRun.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = "Starting...";
            }
        }
    }
}
