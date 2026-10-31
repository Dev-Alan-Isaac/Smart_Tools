using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using ScrollEventArgs = System.Windows.Controls.Primitives.ScrollEventArgs;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Audio Trimmer &amp; Joiner - UI only. The clip list, editing, undo, the ffmpeg export and the
    /// preview player are in <see cref="AudioTrimmerManager"/> and <see cref="AudioTrimPlayer"/>;
    /// this file shows them and turns clicks into calls. Playing and exporting are in
    /// AudioTrimmerView.Run.cs.
    ///
    /// Nothing is edited in the files: a clip is only "this part of that file". Only Export writes audio.
    /// Which files and folders were used is never saved; only the setup (format, fades, level ...).
    /// </summary>
    public partial class AudioTrimmerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double ClipsExpandedWidth = 340;
        private const double NarrowLayoutWidth = 1000;
        private const string DialogTitle = "Audio Trimmer & Joiner";

        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".aifc",
            ".mka", ".wv", ".ape", ".amr", ".ac3", ".mp4", ".m4b", ".webm", ".mkv", ".mov", ".avi",
        };

        private readonly ObservableCollection<AudioTrimClip> _clips = new();
        private readonly List<AudioTrimSource> _sources = new();
        private readonly AudioTrimmerManager.History _history = new();
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;

        private AudioTrimClip? _selected;
        private bool _optionsCollapsed;
        private bool _clipsCollapsed;
        private bool _narrow;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _restoring;
        private bool _syncingScroll;
        private bool _isLoading;

        public AudioTrimmerView()
        {
            InitializeComponent();
            ListClips.ItemsSource = _clips;
            _clips.CollectionChanged += Clips_CollectionChanged;

            Wave.SelectionEdited += (_, _) => OnSelectionEdited();
            Wave.SeekRequested += Wave_SeekRequested;
            Wave.ViewChanged += (_, _) => SyncScrollBar();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };

            InitPlayback();
            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            AudioTrimmerManager.SweepPreviewFolder(everything: false);       // copies left by a crashed run
            ApplySettings(AudioTrimmerManager.LoadSettings());
            RefreshAll();
            if (AudioTrimmerManager.MissingEngine() is { } missing) ShowStatus(missing, StatusKind.Error);
        }

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void ApplySettings(AudioTrimSettings s)
        {
            _applyingSettings = true;
            try
            {
                SelectComboByTag(CmbFormat, s.Format.ToString());
                RebuildBitrates(s.Format, s.BitrateKbps);
                SliderFadeIn.Value = s.FadeInSeconds;
                SliderFadeOut.Value = s.FadeOutSeconds;
                (s.Normalize switch { AudioTrimNormalize.Peak => RadioNormPeak, AudioTrimNormalize.Loudness => RadioNormLoud, _ => RadioNormOff }).IsChecked = true;
                (s.SampleRate switch { 44_100 => RadioRate441, 48_000 => RadioRate48, _ => RadioRateAuto }).IsChecked = true;
                (s.Channels switch { AudioTrimChannels.Stereo => RadioChStereo, AudioTrimChannels.Mono => RadioChMono, _ => RadioChAuto }).IsChecked = true;
                UpdateFadeTexts();
                UpdateNormHint();
                UpdateFormatPanels();
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
                SetClipsCollapsed(s.ClipsCollapsed);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private AudioTrimFormat SelectedFormat() =>
            Enum.TryParse<AudioTrimFormat>((CmbFormat.SelectedItem as ComboBoxItem)?.Tag as string, out var f) ? f : AudioTrimFormat.Mp3;

        private AudioTrimNormalize SelectedNormalize() =>
            Enum.TryParse<AudioTrimNormalize>(SelectedTag(RadioNormOff, RadioNormPeak, RadioNormLoud), out var n) ? n : AudioTrimNormalize.Off;

        private static string SelectedTag(params RadioButton[] buttons) => buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string ?? "";

        private AudioTrimExportOptions ReadOptions()
        {
            var format = SelectedFormat();
            return new AudioTrimExportOptions(
                format,
                (CmbBitrate.SelectedItem as ComboBoxItem)?.Tag is int kbps ? kbps : AudioTrimmerManager.DefaultBitrate(format),
                SelectedNormalize(),
                SliderFadeIn.Value,
                SliderFadeOut.Value,
                int.TryParse(SelectedTag(RadioRateAuto, RadioRate441, RadioRate48), out int rate) ? rate : 0,
                Enum.TryParse<AudioTrimChannels>(SelectedTag(RadioChAuto, RadioChStereo, RadioChMono), out var ch) ? ch : AudioTrimChannels.Auto);
        }

        private AudioTrimSettings ReadSettings()
        {
            var o = ReadOptions();
            return new AudioTrimSettings
            {
                Format = o.Format,
                BitrateKbps = o.BitrateKbps,
                Normalize = o.Normalize,
                FadeInSeconds = o.FadeInSeconds,
                FadeOutSeconds = o.FadeOutSeconds,
                SampleRate = o.SampleRate,
                Channels = o.Channels,
                OptionsCollapsed = _optionsCollapsed,
                ClipsCollapsed = _clipsCollapsed,
            };
        }

        private void SaveSettings() => AudioTrimmerManager.SaveSettings(ReadSettings());

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private static void SelectComboByTag(ComboBox box, string tag)
        {
            foreach (ComboBoxItem item in box.Items)
                if ((item.Tag?.ToString()) == tag) { box.SelectedItem = item; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ScheduleSave();
        }

        private void BtnToggleClips_Click(object sender, RoutedEventArgs e) => SetClipsCollapsed(!_clipsCollapsed);

        private void SetClipsCollapsed(bool collapsed)
        {
            if (collapsed == _clipsCollapsed) return;
            _clipsCollapsed = collapsed;
            ApplyClipsPanelLayout();
            ScheduleSave();
        }

        /// <summary>Open / closed like Audio Replay's playlist; also hidden (not forgotten) while the window is narrow.</summary>
        private void ApplyClipsPanelLayout()
        {
            bool show = !_clipsCollapsed && !_narrow;
            ClipsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ClipsColumn.Width = new GridLength(show ? ClipsExpandedWidth : 0);
            IconToggleClips.Kind = _clipsCollapsed ? MaterialDesignThemes.Wpf.PackIconKind.ChevronLeft : MaterialDesignThemes.Wpf.PackIconKind.ChevronRight;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0) return;
            bool narrow = e.NewSize.Width < NarrowLayoutWidth;
            if (narrow == _narrow) return;
            _narrow = narrow;
            ApplyClipsPanelLayout();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            UpdateFadeTexts();
            UpdateSummary();
            ScheduleSave();
        }

        private void UpdateFadeTexts()
        {
            TxtFadeInValue.Text = SliderFadeIn.Value.ToString("0.#", CultureInfo.CurrentCulture) + " s";
            TxtFadeOutValue.Text = SliderFadeOut.Value.ToString("0.#", CultureInfo.CurrentCulture) + " s";
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateNormHint();
            UpdateSummary();
            ScheduleSave();
        }

        private void UpdateNormHint()
        {
            string key = SelectedNormalize() switch
            {
                AudioTrimNormalize.Peak => "Str_AudioTrimmer_NormHintPeak",
                AudioTrimNormalize.Loudness => "Str_AudioTrimmer_NormHintLoud",
                _ => "Str_AudioTrimmer_NormHintOff",
            };
            TxtNormHint.SetResourceReference(TextBlock.TextProperty, key);
        }

        private void CmbFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            var format = SelectedFormat();
            int keep = (CmbBitrate.SelectedItem as ComboBoxItem)?.Tag is int k ? k : AudioTrimmerManager.DefaultBitrate(format);
            RebuildBitrates(format, keep);
            UpdateFormatPanels();
            UpdateSummary();
            ScheduleSave();
        }

        private void CmbBitrate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateSummary();
            ScheduleSave();
        }

        private void RebuildBitrates(AudioTrimFormat format, int wanted)
        {
            var rates = AudioTrimmerManager.BitratesFor(format);
            CmbBitrate.Items.Clear();
            foreach (int kbps in rates) CmbBitrate.Items.Add(new ComboBoxItem { Content = kbps + " kbps", Tag = kbps });
            int chosen = rates.Length > 0 ? AudioTrimmerManager.ClampBitrate(format, wanted) : 0;
            foreach (ComboBoxItem item in CmbBitrate.Items)
                if (item.Tag is int k && k == chosen) CmbBitrate.SelectedItem = item;
        }

        private void UpdateFormatPanels()
        {
            var format = SelectedFormat();
            BitratePanel.Visibility = AudioTrimmerManager.IsLossless(format) ? Visibility.Collapsed : Visibility.Visible;
            string key = format switch
            {
                AudioTrimFormat.Wav => "Str_AudioTrimmer_FormatHintWav",
                AudioTrimFormat.Flac => "Str_AudioTrimmer_FormatHintFlac",
                AudioTrimFormat.Opus => "Str_AudioTrimmer_FormatHintOpus",
                AudioTrimFormat.Mp3 => "Str_AudioTrimmer_FormatHintMp3",
                _ => "Str_AudioTrimmer_FormatHintLossy",
            };
            TxtFormatHint.SetResourceReference(TextBlock.TextProperty, key);
        }

        // =================================================================
        //  Adding files
        // =================================================================

        private static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;
            var dialog = new OpenFileDialog
            {
                Title = DialogTitle,
                Multiselect = true,
                Filter = Str("Str_AudioTrimmer_OpenFilter", "Audio files") + "|" + string.Join(";", AudioExtensions.Select(x => "*" + x)) + "|" + Str("Str_AudioTrimmer_AllFiles", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() == true) await AddFilesAsync(dialog.FileNames);
        }

        private void Root_DragEnter(object sender, DragEventArgs e) => HandleDrag(e, entering: true);

        private void Root_DragOver(object sender, DragEventArgs e) => HandleDrag(e, entering: false);

        private void HandleDrag(DragEventArgs e, bool entering)
        {
            bool files = !_isExporting && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (files && entering) UiMotion.FadeVisibility(DropOverlay, true);
        }

        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            // only hide once the pointer is really outside the page
            var p = System.Windows.Input.Mouse.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth - 1 || p.Y >= ActualHeight - 1) UiMotion.FadeVisibility(DropOverlay, false);
        }

        private async void Root_Drop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropOverlay, false);
            if (_isExporting || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

            var paths = new List<string>();
            foreach (string p in dropped)
            {
                if (Directory.Exists(p))
                {
                    try { paths.AddRange(Directory.EnumerateFiles(p).Where(IsAudioFile).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a folder that can't be read adds nothing */ }
                }
                else paths.Add(p);
            }
            await AddFilesAsync(paths);
        }

        /// <summary>
        /// Reads each file (ffprobe, then its waveform) and appends it as a clip. One batch at a time. Files that
        /// can't be read are listed with the reason; the rest are still added.
        /// </summary>
        private async Task AddFilesAsync(IEnumerable<string> input)
        {
            if (AudioTrimmerManager.MissingEngine() is { } missing) { ShowStatus(missing, StatusKind.Error); return; }

            var paths = input.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return;

            await _loadGate.WaitAsync();
            var ct = _lifetimeCts.Token;
            var problems = new List<string>();
            bool pushedHistory = false;
            AudioTrimClip? firstAdded = null;
            try
            {
                SetLoading(true);
                for (int i = 0; i < paths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = paths[i];
                    string name = Path.GetFileName(path);

                    if (_clips.Count >= AudioTrimmerManager.MaxClips)
                    {
                        problems.Add(string.Format(Str("Str_AudioTrimmer_Err_TooMany", "Only {0} clips fit; the rest weren't added."), AudioTrimmerManager.MaxClips));
                        break;
                    }
                    if (!File.Exists(path)) { problems.Add($"{name}: " + Str("Str_AudioTrimmer_Err_Missing", "the file isn't there.")); continue; }
                    if (!IsAudioFile(path) && Directory.Exists(path)) continue;

                    var source = _sources.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (source == null)
                    {
                        SetLoadingText(string.Format(Str("Str_AudioTrimmer_Reading", "Reading {0}…"), name), i, paths.Count, 0);
                        var probe = await AudioTrimmerManager.ProbeAsync(path, ct);
                        if (probe.Error.Length > 0) { problems.Add($"{name}: {probe.Error}"); continue; }

                        var progress = new Progress<double>(f => SetLoadingText(string.Format(Str("Str_AudioTrimmer_Reading", "Reading {0}…"), name), i, paths.Count, f));
                        var peaks = await AudioTrimmerManager.ReadPeaksAsync(path, probe.Duration, progress, ct);
                        if (peaks == null) { problems.Add($"{name}: " + Str("Str_AudioTrimmer_Err_NoAudio", "no sound could be read from it.")); continue; }

                        source = new AudioTrimSource
                        {
                            Path = path,
                            Duration = probe.Duration > 0 ? probe.Duration : peaks.Value.Seconds,
                            SampleRate = probe.SampleRate > 0 ? probe.SampleRate : 44_100,
                            Channels = probe.Channels > 0 ? probe.Channels : 2,
                            Codec = probe.Codec,
                            Peaks = peaks.Value.Peaks,
                        };
                        _sources.Add(source);
                    }

                    if (source.Duration < AudioTrimmerManager.MinClipSeconds) { problems.Add($"{name}: " + Str("Str_AudioTrimmer_Err_Short", "it is too short.")); continue; }

                    if (!pushedHistory)
                    {
                        _history.Push(AudioTrimmerManager.Snapshot(_clips), SelectedIndex());
                        pushedHistory = true;
                    }
                    var clip = new AudioTrimClip(source, 0, source.Duration);
                    _clips.Add(clip);
                    firstAdded ??= clip;
                }
            }
            catch (OperationCanceledException)
            {
                // the page is closing
            }
            finally
            {
                SetLoading(false);
                _loadGate.Release();
            }

            if (firstAdded != null) SelectClip(firstAdded);
            if (problems.Count > 0)
                ShowStatus(Str("Str_AudioTrimmer_NotAdded", "Some files weren't added:") + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(6)) + (problems.Count > 6 ? $"{Environment.NewLine}… (+{problems.Count - 6})" : ""), StatusKind.Warning);
            else if (firstAdded != null) HideStatus();
        }

        private void SetLoading(bool loading)
        {
            _isLoading = loading;
            WaveLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (loading) WaveEmptyPanel.Visibility = Visibility.Collapsed;
            UpdateButtons();
            if (!loading) RefreshAll();
        }

        private void SetLoadingText(string text, int index, int count, double fraction)
        {
            TxtWaveLoading.Text = count > 1 ? $"{text}  ({index + 1}/{count})" : text;
            WaveLoadingBar.Value = fraction * 100;
        }

        // =================================================================
        //  Clip list and selection
        // =================================================================

        private int SelectedIndex() => _selected == null ? -1 : _clips.IndexOf(_selected);

        private void Clips_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_restoring) return;
            AudioTrimmerManager.Renumber(_clips);
            RefreshAll();
            if (e.Action == NotifyCollectionChangedAction.Add && _clipsCollapsed) SetClipsCollapsed(false);   // new work is never hidden
        }

        private void ClipRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;   // a row button
            if ((sender as FrameworkElement)?.DataContext is AudioTrimClip clip) SelectClip(clip);
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var p = child; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
                if (p is T t) return t;
            return null;
        }

        private void SelectClip(AudioTrimClip? clip)
        {
            if (_selected != null)
            {
                _selected.PropertyChanged -= SelectedClip_PropertyChanged;
                _selected.IsSelected = false;
            }
            bool newSource = clip == null || _selected == null || !ReferenceEquals(_selected.Source, clip.Source);
            _selected = clip;

            if (clip != null)
            {
                clip.IsSelected = true;
                clip.PropertyChanged += SelectedClip_PropertyChanged;
                Wave.Peaks = clip.Source.Peaks;
                Wave.Duration = clip.Source.Duration;
                Wave.ClipStart = clip.Start;
                Wave.ClipEnd = clip.End;
                if (newSource) Wave.ZoomFit();
                if (!_player.IsActive) Wave.Playhead = clip.Start;
                Wave.ClearSelection();
            }
            else
            {
                Wave.Peaks = null;
                Wave.Duration = 0;
                Wave.ClipStart = Wave.ClipEnd = 0;
                Wave.Playhead = double.NaN;
                Wave.ClearSelection();
            }
            RefreshAll();
        }

        private void SelectedClip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_selected == null) return;
            if (e.PropertyName is nameof(AudioTrimClip.Start) or nameof(AudioTrimClip.End))
            {
                Wave.ClipStart = _selected.Start;
                Wave.ClipEnd = _selected.End;
            }
        }

        /// <summary>Everything that depends on the clip list or the selection: pills, empty states, buttons, the summary.</summary>
        private void RefreshAll()
        {
            if (!_viewConstructed) return;
            bool any = _clips.Count > 0;
            double total = AudioTrimmerManager.TotalSeconds(_clips);

            TxtPillClips.Text = string.Format(Str(_clips.Count == 1 ? "Str_AudioTrimmer_PillClipOne" : "Str_AudioTrimmer_PillClips", "{0} clips"), _clips.Count);
            TxtPillTotal.Text = AudioTrimTime.Format(total);
            TxtClipsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            WaveEmptyPanel.Visibility = any || _isLoading ? Visibility.Collapsed : Visibility.Visible;
            TxtWaveTitle.Text = _selected == null ? "" : _selected.Source.FileName;
            Wave.IsHitTestVisible = _selected != null && !_isExporting;
            UpdateSelectionText();
            UpdateTimeText();
            UpdateSummary();
            UpdateButtons();
            SyncScrollBar();
        }

        private void UpdateSummary()
        {
            if (!_viewConstructed) return;
            if (_clips.Count == 0) { TxtExportSummary.Text = Str("Str_AudioTrimmer_AddToStart", "Add audio files to start."); return; }

            var o = ReadOptions();
            var states = AudioTrimmerManager.Snapshot(_clips);
            int rate = AudioTrimmerManager.ResolveSampleRate(states, o), ch = AudioTrimmerManager.ResolveChannels(states, o);
            string quality = AudioTrimmerManager.IsLossless(o.Format) ? "" : $" {AudioTrimmerManager.ClampBitrate(o.Format, o.BitrateKbps)} kbps";
            var (fin, fout) = AudioTrimmerManager.EffectiveFades(AudioTrimmerManager.TotalSeconds(_clips), o.FadeInSeconds, o.FadeOutSeconds);
            string fades = fin > 0 || fout > 0 ? "  ·  " + Str("Str_AudioTrimmer_SummaryFades", "fades") : "";
            string norm = o.Normalize switch
            {
                AudioTrimNormalize.Peak => "  ·  " + Str("Str_AudioTrimmer_SummaryPeak", "peak normalized"),
                AudioTrimNormalize.Loudness => "  ·  " + Str("Str_AudioTrimmer_SummaryLoud", "loudness normalized"),
                _ => "",
            };
            TxtExportSummary.Text = $"{AudioTrimTime.Format(AudioTrimmerManager.TotalSeconds(_clips))}  ·  {o.Format.ToString().ToUpperInvariant()}{quality}  ·  {rate / 1000.0:0.#} kHz {Str(ch == 1 ? "Str_AudioTrimmer_Mono" : "Str_AudioTrimmer_Stereo", ch == 1 ? "Mono" : "Stereo").ToLower(CultureInfo.CurrentCulture)}{fades}{norm}";
        }

        private void UpdateSelectionText()
        {
            if (Wave.GetSelection() is { } sel)
                SetTrimValues(sel.Start, sel.End);
            else if (_selected != null)
                SetTrimValues(_selected.Start, _selected.End);
            else
                TxtSelStart.Text = TxtSelEnd.Text = TxtSelLen.Text = "–";
        }

        private void SetTrimValues(double start, double end)
        {
            TxtSelStart.Text = AudioTrimTime.Format(start, 2);
            TxtSelEnd.Text = AudioTrimTime.Format(end, 2);
            TxtSelLen.Text = AudioTrimTime.Format(end - start, 2);
        }

        private void UpdateButtons()
        {
            if (!_viewConstructed) return;
            bool busy = _isExporting || _isLoading;
            bool hasClip = _selected != null;
            bool hasSel = Wave.HasSelection;
            double ph = Wave.Playhead;
            bool canSplit = hasClip && !double.IsNaN(ph) &&
                            ph - _selected!.Start >= AudioTrimmerManager.MinClipSeconds && _selected.End - ph >= AudioTrimmerManager.MinClipSeconds;

            BtnKeep.IsEnabled = !busy && hasClip && hasSel;
            BtnCut.IsEnabled = !busy && hasClip && hasSel;
            BtnSplit.IsEnabled = !busy && canSplit;
            BtnReset.IsEnabled = !busy && hasClip && !_selected!.IsWholeFile;
            BtnUndo.IsEnabled = !busy && _history.CanUndo;
            BtnRedo.IsEnabled = !busy && _history.CanRedo;
            BtnPlay.IsEnabled = !busy && hasClip;
            BtnStop.IsEnabled = !busy && _player.IsActive;
            BtnPlayAll.IsEnabled = !busy && _clips.Count > 0;
            BtnAddFiles.IsEnabled = !busy;
            BtnEmptyAdd.IsEnabled = !busy;
            BtnClearClips.IsEnabled = !busy && _clips.Count > 0;
            BtnZoomIn.IsEnabled = BtnZoomOut.IsEnabled = BtnZoomFit.IsEnabled = hasClip;
            BtnZoomSel.IsEnabled = hasClip && hasSel;
            ListClips.IsEnabled = !_isExporting;
            if (!_isExporting) BtnExport.IsEnabled = !_isLoading && _clips.Count > 0;
        }

        private void OnSelectionEdited()
        {
            UpdateSelectionText();
            UpdateButtons();
        }

        private void Wave_SeekRequested(object? sender, double t)
        {
            if (_selected == null) return;
            Wave.Playhead = t;
            UpdateButtons();
            UpdateTimeText();
            if (_player.IsPlaying && _playMode == PlayMode.Audition) _ = StartAuditionAsync(t);      // keep playing from the new spot
        }

        // =================================================================
        //  Waveform: zoom and scroll
        // =================================================================

        private void SyncScrollBar()
        {
            if (!_viewConstructed) return;
            _syncingScroll = true;
            try
            {
                double duration = Wave.Duration, view = Wave.ViewDuration;
                bool scrollable = duration > 0 && view > 0 && view < duration - 1e-6;
                WaveScroll.IsEnabled = scrollable;
                WaveScroll.Minimum = 0;
                WaveScroll.Maximum = scrollable ? duration - view : 0;
                WaveScroll.ViewportSize = scrollable ? view : 0;
                WaveScroll.LargeChange = scrollable ? view * 0.9 : 1;
                WaveScroll.SmallChange = scrollable ? view * 0.1 : 1;
                WaveScroll.Value = scrollable ? Wave.ViewStart : 0;
            }
            finally
            {
                _syncingScroll = false;
            }
        }

        private void WaveScroll_Scroll(object sender, ScrollEventArgs e)
        {
            if (_syncingScroll || Wave.Duration <= 0) return;
            Wave.SetView(e.NewValue, Wave.ViewDuration);
        }

        private double ZoomCenter() => !double.IsNaN(Wave.Playhead) && Wave.Playhead >= Wave.ViewStart && Wave.Playhead <= Wave.ViewStart + Wave.ViewDuration
            ? Wave.Playhead
            : Wave.ViewStart + Wave.ViewDuration / 2;

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => Wave.ZoomBy(1.6, ZoomCenter());

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => Wave.ZoomBy(1 / 1.6, ZoomCenter());

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => Wave.ZoomFit();

        private void BtnZoomSel_Click(object sender, RoutedEventArgs e)
        {
            if (Wave.GetSelection() is { } sel) Wave.ZoomToRange(sel.Start, sel.End);
        }

        private void ChkBoost_Click(object sender, RoutedEventArgs e) => Wave.AutoScale = ChkBoost.IsChecked == true;

        // =================================================================
        //  Editing
        // =================================================================

        /// <summary>Runs an edit; if it changed something, remembers the list from before it for undo.</summary>
        private void Edit(Func<bool> change)
        {
            if (_selected == null || _isExporting) return;
            var before = AudioTrimmerManager.Snapshot(_clips);
            int index = SelectedIndex();
            if (!change()) return;
            _history.Push(before, index);
            StopPlayback();
        }

        private void BtnKeep_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || Wave.GetSelection() is not { } sel) return;
            var clip = _selected;
            Edit(() => AudioTrimmerManager.TrimTo(clip, sel.Start, sel.End));
            Wave.ClearSelection();
            Wave.Playhead = clip.Start;
            Wave.ClipStart = clip.Start;
            Wave.ClipEnd = clip.End;
            Wave.ZoomFit();
            RefreshAll();
        }

        private void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || Wave.GetSelection() is not { } sel) return;
            var clip = _selected;
            AudioTrimClip? next = clip;
            Edit(() =>
            {
                next = AudioTrimmerManager.CutOut(_clips, clip, sel.Start, sel.End, out bool changed);
                return changed;
            });
            SelectClip(next);
            if (next != null) Wave.Playhead = Math.Clamp(sel.Start, next.Start, next.End);     // the point where the cut closed
            Wave.ZoomFit();
            RefreshAll();
        }

        private void BtnSplit_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || double.IsNaN(Wave.Playhead)) return;
            var clip = _selected;
            double t = Wave.Playhead;
            Edit(() => AudioTrimmerManager.Split(_clips, clip, t, out _));
            Wave.ClipEnd = clip.End;
            RefreshAll();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var clip = _selected;
            Edit(() =>
            {
                if (clip.IsWholeFile) return false;
                AudioTrimmerManager.ResetRange(clip);
                return true;
            });
            Wave.ClearSelection();
            RefreshAll();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => TimeTravel(undo: true);

        private void BtnRedo_Click(object sender, RoutedEventArgs e) => TimeTravel(undo: false);

        private void TimeTravel(bool undo)
        {
            if (_isExporting) return;
            var now = AudioTrimmerManager.Snapshot(_clips);
            var entry = undo ? _history.Undo(now, SelectedIndex()) : _history.Redo(now, SelectedIndex());
            if (entry == null) return;

            StopPlayback();
            _restoring = true;
            try
            {
                _clips.Clear();
                foreach (var s in entry.Clips) _clips.Add(new AudioTrimClip(s.Source, s.Start, s.End));
                AudioTrimmerManager.Renumber(_clips);
            }
            finally
            {
                _restoring = false;
            }
            if (_selected != null) { _selected.PropertyChanged -= SelectedClip_PropertyChanged; _selected = null; }
            SelectClip(_clips.Count == 0 ? null : _clips[Math.Clamp(entry.Selected, 0, _clips.Count - 1)]);
        }

        private void BtnClipUp_Click(object sender, RoutedEventArgs e) => MoveClip((sender as FrameworkElement)?.DataContext as AudioTrimClip, -1);

        private void BtnClipDown_Click(object sender, RoutedEventArgs e) => MoveClip((sender as FrameworkElement)?.DataContext as AudioTrimClip, +1);

        private void MoveClip(AudioTrimClip? clip, int delta)
        {
            if (clip == null || _isExporting) return;
            int from = _clips.IndexOf(clip), to = from + delta;
            if (from < 0 || to < 0 || to >= _clips.Count) return;
            _history.Push(AudioTrimmerManager.Snapshot(_clips), SelectedIndex());
            StopPlayback();
            _clips.Move(from, to);
        }

        private void BtnClipRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AudioTrimClip clip || _isExporting) return;
            int index = _clips.IndexOf(clip);
            if (index < 0) return;
            _history.Push(AudioTrimmerManager.Snapshot(_clips), SelectedIndex());
            StopPlayback();
            bool wasSelected = ReferenceEquals(clip, _selected);
            _clips.RemoveAt(index);
            if (wasSelected) SelectClip(_clips.Count == 0 ? null : _clips[Math.Min(index, _clips.Count - 1)]);
        }

        private void BtnClearClips_Click(object sender, RoutedEventArgs e)
        {
            if (_clips.Count == 0 || _isExporting) return;
            _history.Push(AudioTrimmerManager.Snapshot(_clips), SelectedIndex());
            StopPlayback();
            _restoring = true;
            try { _clips.Clear(); }
            finally { _restoring = false; }
            SelectClip(null);
        }

        // =================================================================
        //  Keyboard
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // typing in a box or using a button / list keeps its own keys
            if (e.OriginalSource is TextBoxBase or ComboBox or ComboBoxItem or Slider or ButtonBase) return;

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            switch (e.Key)
            {
                case Key.Space when !ctrl:
                    BtnPlay_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.Z when ctrl:
                    TimeTravel(undo: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    e.Handled = true;
                    break;
                case Key.Y when ctrl:
                    TimeTravel(undo: false);
                    e.Handled = true;
                    break;
                case Key.Delete or Key.Back when BtnCut.IsEnabled:
                    BtnCut_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S when !ctrl && BtnSplit.IsEnabled:
                    BtnSplit_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.Home when _selected != null && !_isExporting:
                    Wave.Playhead = _selected.Start;
                    Wave.EnsureVisible(_selected.Start);
                    UpdateButtons();
                    UpdateTimeText();
                    e.Handled = true;
                    break;
            }
        }

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops playing, cancels reading and exporting (no half file is left), tidies the preview copies, keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _lifetimeCts.Cancel();
            _exportCts?.Cancel();
            ShutDownPlayback();
            AudioTrimmerManager.SweepPreviewFolder(everything: true);
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
