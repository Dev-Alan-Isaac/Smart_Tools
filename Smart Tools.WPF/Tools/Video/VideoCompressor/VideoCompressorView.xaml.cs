using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Video Compressor &amp; Trimmer - UI only. The FFmpeg arguments, target-size maths, estimates and settings are in
    /// <see cref="VideoCompressorManager"/>; ffprobe and the run in VideoCompressorManager.Run.cs; picking the part to keep is
    /// the trim window, <see cref="VideoCompressorTrimWindow"/>.
    /// </summary>
    public partial class VideoCompressorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_video-compressor_Name", "Video Compressor");

        private readonly ObservableCollection<VcFileItem> _rows = new();
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _runCts;
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

        private string S(string name, string fallback) => TryFindResource("Str_Vc_" + name) as string ?? fallback;

        public VideoCompressorView()
        {
            InitializeComponent();
            BuildOutputOptions();

            InputCard.ItemsSource = _rows;
            InputCard.ShowSubfolders = false;
            InputCard.FileFilter = $"Videos|{string.Join(";", VideoToAudioManager.VideoExtensions.Select(x => "*" + x))}|All files (*.*)|*.*";
            DataGridFiles.ItemsSource = _rows;
            OutputCard.DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Smart Tools", "Video Compressor");
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops FFmpeg and ffprobe.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _probeCts.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(VideoCompressorManager.LoadSettings());
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateEngineWarning();
            UpdateModeUi();
            UpdateSummary();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            ColResult.Visibility = width < 980 ? Visibility.Collapsed : Visibility.Visible;
            ColResolution.Visibility = width < 880 ? Visibility.Collapsed : Visibility.Visible;
            ColLength.Visibility = width < 780 ? Visibility.Collapsed : Visibility.Visible;
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
            string? missing = VideoCompressorManager.MissingEngine();
            TxtFfmpegWarning.Text = missing ?? "";
            FfmpegWarning.Visibility = missing == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Files
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
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
            e.Handled = true;
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning) AddPaths(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearAll();

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => ClearAll();

        /// <summary>Files are added as they are; a folder adds every video inside it, subfolders too.</summary>
        private void AddPaths(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    if (VideoToAudioManager.IsVideo(path)) found.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    try
                    {
                        found.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                            .Where(VideoToAudioManager.IsVideo).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            var fresh = new List<VcFileItem>();
            foreach (string path in found)
            {
                if (!_paths.Add(path)) continue;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var item = new VcFileItem { FullPath = path, SizeBytes = size };
                _rows.Add(item);
                fresh.Add(item);
            }

            InputCard.PopDropArea();
            _holdStatus = false;
            UpdateSummary();
            if (fresh.Count > 0)
            {
                TxtProgress.Text = string.Format(S("Reading", "Reading {0}..."), VcText.Plural(fresh.Count, "video", "videos"));
                _ = ProbeAsync(fresh);
            }
        }

        private void BtnRemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || (sender as FrameworkElement)?.Tag is not VcFileItem item) return;
            RemoveRows(new[] { item });
        }

        private void ClearAll()
        {
            if (_isRunning) return;
            _probeCts.Cancel();
            _probeCts = new CancellationTokenSource();
            _rows.Clear();
            _paths.Clear();
            UpdateSummary();
            TxtProgress.Text = S("ReadyHint", "Add videos, pick what to do, then press Compress.");
            TxtProgressPercent.Text = "";
            ProgressRun.Value = 0;
        }

        /// <summary>ffprobe for new rows, four at a time; each row updates as soon as it's read.</summary>
        private async Task ProbeAsync(IReadOnlyList<VcFileItem> items)
        {
            var token = _probeCts.Token;
            _probing++;
            using var gate = new SemaphoreSlim(4);
            try
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        VcProbe probe;
                        try
                        {
                            probe = await VideoCompressorManager.ProbeAsync(item.FullPath, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        if (!_rows.Contains(item)) return;   // removed while it was being read

                        item.Duration = probe.Duration;
                        item.Width = probe.Width;
                        item.Height = probe.Height;
                        item.Fps = probe.Fps;
                        item.HasAudio = probe.HasAudio;
                        if (probe.Error != null)
                        {
                            item.Message = probe.Error;
                            item.Status = VcItemStatus.Unreadable;
                        }
                        else if (probe.Duration is null)
                        {
                            item.Message = S("NoLength", "This video's length couldn't be read, so it can't be compressed.");
                            item.Status = VcItemStatus.Unreadable;
                        }
                        else
                        {
                            item.Message = "";
                            item.Status = VcItemStatus.Ready;
                            item.ResultText = VideoCompressorManager.EstimateText(item, BuildOptions());
                        }
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

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveRows(DataGridFiles.SelectedItems.OfType<VcFileItem>().ToList());

        private void RemoveRows(IReadOnlyList<VcFileItem> chosen)
        {
            if (_isRunning || chosen.Count == 0) return;
            int next = _rows.IndexOf(chosen[0]);
            foreach (var item in chosen)
            {
                _paths.Remove(item.FullPath);
                _rows.Remove(item);
            }
            if (_rows.Count > 0) DataGridFiles.SelectedItem = _rows[Math.Clamp(next, 0, _rows.Count - 1)];
            UpdateSummary();
            TxtProgress.Text = ReadyText();
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveRows(DataGridFiles.SelectedItems.OfType<VcFileItem>().ToList());
            e.Handled = true;
        }

        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) != null) return;
            if (DataGridFiles.SelectedItem is not VcFileItem item) return;
            string path = item.Status == VcItemStatus.Done && File.Exists(item.OutputPath) ? item.OutputPath : item.FullPath;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(Loc.F("Str_Err_OpenFile", "Couldn't open {0}: {1}", Path.GetFileName(path), ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e) => OpenOutput(quiet: false);

        private void OpenOutput(bool quiet)
        {
            string? firstResult = (DataGridFiles.SelectedItem as VcFileItem)?.OutputPath is { Length: > 0 } selected && File.Exists(selected)
                ? selected
                : _rows.Select(r => r.OutputPath).FirstOrDefault(p => p.Length > 0 && File.Exists(p));
            string? target = OutputCard.IsSameAsSource ? firstResult : firstResult ?? OutputCard.EffectiveFolder;
            if (target != null && ShellExplorer.Show(target) == ShellExplorer.ShowResult.Shown) return;
            if (!quiet)
                ThemedMessageBox.Show(OutputCard.IsSameAsSource
                        ? S("NothingSavedSource", "The videos are saved next to the originals - compress something first.")
                        : $"Nothing has been saved to {OutputCard.EffectiveFolder} yet.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Trim
        // =================================================================

        /// <summary>The Keep button on a row: open the trim window on that video, then refresh its estimate and the summary.</summary>
        private void BtnTrim_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || (sender as FrameworkElement)?.Tag is not VcFileItem item || item.Duration is null) return;
            if (item.Status is VcItemStatus.Done or VcItemStatus.Working or VcItemStatus.Reading or VcItemStatus.Unreadable) return;

            var window = new VideoCompressorTrimWindow(item, Window.GetWindow(this));
            window.TrimChanged += () => item.ResultText = VideoCompressorManager.EstimateText(item, BuildOptions());
            window.ShowDialog();

            item.ResultText = VideoCompressorManager.EstimateText(item, BuildOptions());
            _holdStatus = false;
            UpdateSummary();
            if (!_isRunning && _probing == 0) TxtProgress.Text = ReadyText();
        }

        // =================================================================
        //  Settings controls
        // =================================================================

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private static T CheckedTag<T>(Panel panel, T fallback) where T : struct, Enum =>
            panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void CheckTag(Panel panel, string tag)
        {
            var radio = panel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);
            if (radio != null) radio.IsChecked = true;
        }

        private static double ComboNumber(ComboBox combo, double fallback) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        private static void SelectNumber(ComboBox combo, double value)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
                double.TryParse((string)i.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && Math.Abs(v - value) < 0.001);
            if (item != null) combo.SelectedItem = item;
        }

        private int TargetMb => int.TryParse(TxtTargetMb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) ? Math.Clamp(mb, 1, 100_000) : 25;

        private VcOptions BuildOptions() => new()
        {
            Mode = CheckedTag(ModeChips, VcMode.Quality),
            Quality = CheckedTag(QualityChips, VcQuality.Balanced),
            TargetMb = TargetMb,
            MaxHeight = (int)ComboNumber(CmbHeight, 0),
            MaxFps = (int)ComboNumber(CmbFps, 0),
            Audio = CheckedTag(AudioChips, VcAudio.Keep),
            Speed = ComboNumber(CmbSpeed, 1),
        };

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

        private void AfterSettingChanged()
        {
            _holdStatus = false;
            UpdateModeUi();
            RefreshEstimates();
            SaveSettings();
            if (!_isRunning && _rows.Count > 0 && _probing == 0) TxtProgress.Text = ReadyText();
        }

        private void RefreshEstimates()
        {
            var options = BuildOptions();
            foreach (var row in _rows.Where(r => r.Status is VcItemStatus.Ready or VcItemStatus.Failed or VcItemStatus.Cancelled))
                row.ResultText = VideoCompressorManager.EstimateText(row, options);
            UpdateSummary();
        }

        private void UpdateModeUi()
        {
            var mode = CheckedTag(ModeChips, VcMode.Quality);
            PanelQuality.Visibility = mode == VcMode.Quality ? Visibility.Visible : Visibility.Collapsed;
            PanelSize.Visibility = mode == VcMode.TargetSize ? Visibility.Visible : Visibility.Collapsed;
            TxtTrimNote.Visibility = mode == VcMode.TrimOnly ? Visibility.Visible : Visibility.Collapsed;
            PanelPicture.Visibility = mode == VcMode.TrimOnly ? Visibility.Collapsed : Visibility.Visible;
            OutputCard.ExampleFileName = mode == VcMode.TrimOnly ? "video - trimmed.mp4" : "video - compressed.mp4";
            TxtRunLabel.SetResourceReference(TextBlock.TextProperty, _isRunning ? "Str_Common_Cancel" : mode == VcMode.TrimOnly ? "Str_Vc_RunTrim" : "Str_Vc_Run");
        }

        private void TxtTargetMb_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        private void TxtTargetMb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore || TxtTargetMb.Text.Length == 0) return;
            AfterSettingChanged();
        }

        private void TxtTargetMb_LostFocus(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            TxtTargetMb.Text = TargetMb.ToString(CultureInfo.InvariantCulture);
        }

        private void SizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string mb }) TxtTargetMb.Text = mb;
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

        private void ApplySettings(VcSettings s)
        {
            _applyingSettings = true;
            try
            {
                CheckTag(ModeChips, s.Mode.ToString());
                CheckTag(QualityChips, s.Quality.ToString());
                TxtTargetMb.Text = s.TargetMb.ToString(CultureInfo.InvariantCulture);
                SelectNumber(CmbHeight, s.MaxHeight);
                SelectNumber(CmbFps, s.MaxFps);
                SelectNumber(CmbSpeed, s.Speed);
                CheckTag(AudioChips, s.Audio.ToString());
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
            VideoCompressorManager.SaveSettings(new VcSettings
            {
                Mode = o.Mode,
                Quality = o.Quality,
                TargetMb = o.TargetMb,
                MaxHeight = o.MaxHeight,
                MaxFps = o.MaxFps,
                Audio = o.Audio,
                Speed = o.Speed,
                OpenWhenDone = ChkOpenWhenDone.IsChecked == true,
            });
        }

        // =================================================================
        //  The list
        // =================================================================

        private string ReadyText()
        {
            int ready = _rows.Count(r => r.CanRun);
            if (_rows.Count == 0) return S("ReadyHint", "Add videos, pick what to do, then press Compress.");
            if (ready == 0)
                return _rows.Any(r => r.Status == VcItemStatus.Done)
                    ? S("AllDone", "Everything in the list is done. Add more videos, or clear the list.")
                    : S("NoneReadable", "None of these videos can be read.");
            string where = OutputCard.IsSameAsSource ? S("NextToVideos", "next to the videos") : "to " + (OutputCard.EffectiveFolder ?? "the output folder");
            return $"{VcText.Plural(ready, "video", "videos")} ready · saves {where}";
        }

        private void UpdateSummary()
        {
            int count = _rows.Count;
            bool any = count > 0;
            if (any && SummaryPanel.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPanel, 0.9);
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFiles.Text = VideoCompressorManager.DescribeList(_rows);

            string? estimate = VideoCompressorManager.DescribeEstimate(_rows, BuildOptions());
            TxtSummaryEstimate.Text = estimate ?? "";
            PillEstimate.Visibility = estimate == null ? Visibility.Collapsed : Visibility.Visible;

            int done = _rows.Count(r => r.Status == VcItemStatus.Done);
            int failed = _rows.Count(r => r.Status == VcItemStatus.Failed);
            TxtSummaryDone.Text = $"{done:N0} done";
            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFailed.Text = $"{failed:N0} failed";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

            UiMotion.FadeVisibility(EmptyStatePanel, !any);
            UiMotion.FadeVisibility(DataGridFiles, any);
            BtnRemoveFiles.IsEnabled = !_isRunning && any;
            BtnClearFiles.IsEnabled = !_isRunning && any;
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = S("Canceling", "Canceling - finished files are kept...");
                return;
            }
            await StartAsync();
        }

        private async Task StartAsync()
        {
            UpdateEngineWarning();
            if (VideoCompressorManager.MissingEngine() is string missing)
            {
                TxtProgress.Text = missing;
                UiMotion.Pop(FfmpegWarning, 0.9);
                return;
            }
            if (_rows.Count == 0)
            {
                TxtProgress.Text = S("AddFirst", "Add some videos first.");
                InputCard.PopDropArea();
                return;
            }
            if (_probing > 0 || _rows.Any(r => r.Status == VcItemStatus.Reading))
            {
                TxtProgress.Text = S("StillReading", "Still reading the videos - try again in a moment.");
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
                    TxtProgress.Text = Loc.F("Str_Vc_MsgCantSave", "Can't save there: {0}", OutputCard.Status.Text);
                    return;
                }
                try
                {
                    OutputCard.EnsureFolder();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    TxtProgress.Text = Loc.F("Str_Vc_MsgOutputFolder", "The output folder couldn't be created: {0}", ex.Message);
                    return;
                }
            }

            var options = BuildOptions();
            var cts = _runCts = new CancellationTokenSource();
            SetRunning(true);
            foreach (var row in pending) row.Status = VcItemStatus.Ready;
            var progress = new Progress<VcProgress>(ReportProgress);

            VcRunResult? result = null;
            try
            {
                result = await VideoCompressorManager.RunAsync(pending, options, (source, name) => OutputCard.ResultPathFor(source, name), progress, cts.Token);
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
            TxtProgress.Text = VideoCompressorManager.DescribeResult(result);
            if (result.Files.Count > 0)
            {
                UiMotion.Pop(PillDone, 0.85);
                if (ChkOpenWhenDone.IsChecked == true) OpenOutput(quiet: true);
            }
        }

        private void ReportProgress(VcProgress p)
        {
            if (!_isRunning) return;
            ProgressRun.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            string count = $"{p.Finished + p.Failed:N0} of {p.Total:N0}";
            TxtProgress.Text = p.CurrentName != null
                ? string.Format(S("Working", "Working on {0} · {1}"), p.CurrentName, count)
                : $"{count} done";
            UpdateSummary();
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("video-compressor");
            else { _activity?.Dispose(); _activity = null; }
            IconRun.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.VideoMinusOutline;
            UpdateModeUi();
            UiMotion.Pop(BtnRun, 0.96);

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
                TxtProgress.Text = S("Starting", "Starting...");
            }
        }
    }
}
