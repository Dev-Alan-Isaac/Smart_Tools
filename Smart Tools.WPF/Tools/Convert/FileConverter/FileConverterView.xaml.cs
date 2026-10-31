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
    /// File Converter view - UI only. What can be converted, codec rules,
    /// ffmpeg arguments, output paths and the file list are planned in
    /// <see cref="ConverterManager"/>; running a batch is in
    /// ConverterManager.Run.cs; images in ConverterManager.Images.cs.
    /// </summary>
    public partial class FileConverterView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "File Converter";
        private const string ReadyHint = "Add files, choose what to convert them to, then press Convert.";

        private readonly ConverterManager _manager = new();

        /// <summary>What the user added (files and folders) - folders are expanded into <see cref="_queued"/>.</summary>
        private readonly List<ConvertSource> _sources = new();
        private readonly ObservableCollection<ConvertQueueGroup> _queueDisplay = new();
        private List<ConverterManager.QueuedFile> _queued = new();
        private ConverterManager.QueueResult? _lastQueue;
        private ConverterManager.ListResult? _lastList;

        /// <summary>Removed with "Remove selected" - stays removed when the list is rebuilt (it came back before).</summary>
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files this session wrote - not offered again when a folder is re-read.</summary>
        private readonly HashSet<string> _produced = new(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<ConvertFileItem> _rows = new();
        private ConvertRunResult? _lastRun;
        private string? _listingError;

        private CancellationTokenSource? _convertCts;
        private CancellationTokenSource? _expandCts;
        private Task? _expandTask;
        private ConverterSettings _settings = new();
        private ConvertVideoCodec _preferredCodec = ConvertVideoCodec.H264;

        private bool _isConverting;
        private bool _startingConvert;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _updatingCodecs;
        private readonly bool _viewConstructed;

        // Made in code and placed in the Output card (no x:Name inside another UserControl - MC3093).
        private CheckBox ChkKeepStructure = null!;
        private CheckBox ChkOpenWhenDone = null!;

        public FileConverterView()
        {
            InitializeComponent();
            BuildOutputOptions();
            InputCard.ItemsSource = _queueDisplay;
            InputCard.FileFilterProvider = BuildFileFilter;
            DataGridFiles.ItemsSource = _rows;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops ffmpeg and any folder listing.</summary>
        public void CancelActiveRun()
        {
            _convertCts?.Cancel();
            _expandCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(ConverterManager.LoadSettings());
                _loadedOnce = true;
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateQueueHint();
            UpdateSummary();
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            ColResult.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible;
            ColFormats.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Files and folders
        // =================================================================

        private string BuildFileFilter()
        {
            static string Pattern(IEnumerable<string> exts) => string.Join(";", exts.Select(x => "*" + x));
            return SelectedType switch
            {
                ConvertFileType.Image => $"Images|{Pattern(ConverterManager.ImageInputExtensions)}|All files (*.*)|*.*",
                ConvertFileType.Audio => $"Audio and video|{Pattern(ConverterManager.AudioInputExtensions.Concat(ConverterManager.VideoInputExtensions))}|All files (*.*)|*.*",
                _ => $"Videos|{Pattern(ConverterManager.VideoInputExtensions)}|All files (*.*)|*.*",
            };
        }

        private void AddSources(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new ConvertSource { FullPath = path, IsFolder = isFolder });
                _removed.Remove(path);
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            _autoPickType = true;
            _ = RefreshQueueAsync();
        }

        private bool _autoPickType;

        /// <summary>
        /// After files are added: if none of them can be converted as the chosen type, switch to the
        /// type most of them are (images -> Image...), so its formats (ICO, WebP...) are on screen
        /// and the rows say "Ready" instead of being left out.
        /// </summary>
        private void AutoPickType()
        {
            if (!_autoPickType) return;
            _autoPickType = false;
            if (_queued.Count == 0) return;

            var current = SelectedType;
            if (_queued.Any(f => ConverterManager.CanConvert(f.Path, current))) return;

            var dominant = _queued
                .Select(f => ConverterManager.KindOf(f.Path))
                .Where(k => k != MediaKind.Unsupported)
                .GroupBy(k => k)
                .OrderByDescending(g => g.Count())
                .Select(g => (MediaKind?)g.Key)
                .FirstOrDefault();
            if (dominant == null) return;

            var radio = dominant switch
            {
                MediaKind.Image => RadioTypeImage,
                MediaKind.Audio => RadioTypeAudio,
                _ => RadioTypeVideo,
            };
            if (radio.IsChecked == true) return;
            radio.IsChecked = true;
            UpdateTypeUi(animate: true);
            SaveSettings();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting || sender is not Button { Tag: ConvertQueueGroup group }) return;
            if (group.IsFolder)
                _sources.RemoveAll(s => s.IsFolder && string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase));
            else
                _sources.RemoveAll(s => !s.IsFolder && group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
            _ = RefreshQueueAsync();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isConverting) AddSources(e.Paths);
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isConverting) return;
            _expandCts?.Cancel();
            _sources.Clear();
            _queued = new();
            _lastQueue = null;
            _removed.Clear();
            _lastRun = null;
            RebuildQueueDisplay();
            RebuildList();
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
            else RebuildQueueDisplay();
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting) return;
            if (_sources.Count == 0)
            {
                InputCard.PopDropArea();
                return;
            }
            _lastRun = null;
            _ = RefreshQueueAsync();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in ConverterManager.BuildQueueGroups(_sources, InputCard.IncludeSubfolders))
                _queueDisplay.Add(group);
            UpdateQueueHint();
        }

        private void UpdateQueueHint() =>
            InputCard.SetEmpty(_queueDisplay.Count == 0);

        /// <summary>Re-reads the added folders off the UI thread (the old view listed them on it), then rebuilds the grid.</summary>
        private Task RefreshQueueAsync()
        {
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            _expandTask = ExpandAsync(cts);
            return _expandTask;
        }

        private async Task ExpandAsync(CancellationTokenSource cts)
        {
            if (_sources.Count == 0)
            {
                _queued = new();
                _lastQueue = null;
                RebuildList();
                return;
            }

            if (_sources.Any(s => s.IsFolder))
            {
                ProgressConversion.IsIndeterminate = true;
                TxtProgress.Text = "Reading folders...";
            }
            _listingError = null;
            try
            {
                var result = await ConverterManager.ExpandQueueAsync(_sources.ToList(), InputCard.IncludeSubfolders, cts.Token);
                if (cts.IsCancellationRequested) return;
                _queued = result.Files;
                _lastQueue = result;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Was only written to the debug log, so the list just stayed empty.
                Log.Info($"Converter: listing failed: {ex.Message}");
                if (!cts.IsCancellationRequested) _listingError = ex.Message;
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) ProgressConversion.IsIndeterminate = false;
            }
            AutoPickType();
            RebuildList();
        }

        // =================================================================
        //  Settings controls
        // =================================================================

        private ConvertFileType SelectedType =>
            RadioTypeImage.IsChecked == true ? ConvertFileType.Image :
            RadioTypeAudio.IsChecked == true ? ConvertFileType.Audio : ConvertFileType.Video;

        /// <summary>
        /// Every chip under the panel, including ones in a nested grid (the image formats sit in a
        /// UniformGrid). Looking only at direct children missed those chips, so the chosen format
        /// silently stayed JPG.
        /// </summary>
        private static IEnumerable<RadioButton> ChipsIn(Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is RadioButton r) yield return r;
                else if (child is Panel nested)
                    foreach (var inner in ChipsIn(nested)) yield return inner;
            }
        }

        private static T CheckedTag<T>(Panel panel, T fallback) where T : struct, Enum =>
            ChipsIn(panel).FirstOrDefault(r => r.IsChecked == true)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void CheckTag(Panel panel, string tag)
        {
            var radio = ChipsIn(panel).FirstOrDefault(r => (string)r.Tag == tag);
            if (radio != null) radio.IsChecked = true;
        }

        private static T ComboTag<T>(ComboBox combo, T fallback) where T : struct, Enum =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is string s && Enum.TryParse(s, out T v) ? v : fallback;

        private static void SelectTag(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null) combo.SelectedItem = item;
        }

        private static int? ParseSize(string text) => int.TryParse(text, out int v) && v > 0 ? v : null;

        /// <summary>The chosen folder (kept for this session only), else this file type's default.</summary>
        private string EffectiveOutputFolder => OutputCard.EffectiveFolder ?? ConverterManager.DefaultOutputFolder(SelectedType);

        private ConvertOptions BuildOptions() => new()
        {
            FileType = SelectedType,

            VideoFormat = CheckedTag(VideoFormatChips, ConvertVideoFormat.Mp4),
            VideoCodec = ComboTag(CmbVideoCodec, ConvertVideoCodec.H264),
            VideoResolution = ComboTag(CmbVideoResolution, ConvertVideoResolution.Original),
            VideoQuality = CheckedTag(VideoQualityChips, ConvertVideoQuality.Balanced),

            ImageFormat = CheckedTag(ImageFormatChips, ConvertImageFormat.Jpg),
            ImageQuality = ComboTag(CmbImageQuality, ConvertImageQuality.High),
            ImageResize = ComboTag(CmbImageResize, ConvertImageResize.Original),
            ImageCustomWidth = ParseSize(TxtImageCustomWidth.Text),
            ImageCustomHeight = ParseSize(TxtImageCustomHeight.Text),

            AudioFormat = CheckedTag(AudioFormatChips, ConvertAudioFormat.Mp3),
            AudioBitrate = ComboTag(CmbAudioBitrate, ConvertAudioBitrate.K256),
            AudioSampleRate = ComboTag(CmbAudioSampleRate, ConvertAudioSampleRate.Original),

            IncludeSubfolders = InputCard.IncludeSubfolders,
            MatchSelectedTypeOnly = ChkMatchType.IsChecked == true,
            OverwriteExisting = ChkOverwriteExisting.IsChecked == true,
            DeleteOriginalAfterConversion = ChkDeleteOriginal.IsChecked == true,
            DeleteToRecycleBin = true,
            OutputMode = OutputCard.IsSameAsSource ? ConvertOutputMode.NextToOriginal : ConvertOutputMode.Folder,
            OutputFolder = EffectiveOutputFolder,
            KeepFolderStructure = ChkKeepStructure.IsChecked == true,
        };

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void RadioType_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: true);
            AfterSettingChanged();
        }

        private void Setting_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            if (sender is RadioButton { GroupName: "VideoFormat" }) UpdateCodecChoices();
            UpdateTypeUi(animate: false);
            AfterSettingChanged();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            UpdateTypeUi(animate: false);
            AfterSettingChanged();
        }

        private void CmbVideoCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore || _updatingCodecs) return;
            _preferredCodec = ComboTag(CmbVideoCodec, ConvertVideoCodec.H264);
            AfterSettingChanged();
        }

        private void Setting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            AfterSettingChanged();
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            AfterSettingChanged();
        }

        private void AfterSettingChanged()
        {
            SaveSettings();
            RebuildList();
        }

        /// <summary>
        /// The Output card changed (folder chosen, dropped or reset, "next to
        /// the originals" ticked, or its check finished): re-plan the list so
        /// the Result column shows where each file will go.
        /// </summary>
        private void OutputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (Ignore || _isConverting) return;
            bool nextTo = OutputCard.IsSameAsSource;
            var v = nextTo ? Visibility.Collapsed : Visibility.Visible;
            if (ChkKeepStructure.Visibility != v) UiMotion.FadeVisibility(ChkKeepStructure, !nextTo);
            if (_lastOutputMode == nextTo && _lastOutputFolder == OutputCard.EffectiveFolder) return;   // only the folder check finished
            _lastOutputMode = nextTo;
            _lastOutputFolder = OutputCard.EffectiveFolder;
            AfterSettingChanged();
        }

        private bool? _lastOutputMode;
        private string? _lastOutputFolder;

        /// <summary>Shows the chosen type's settings, hints and warnings.</summary>
        private void UpdateTypeUi(bool animate)
        {
            var type = SelectedType;
            void Show(FrameworkElement panel, bool visible)
            {
                var v = visible ? Visibility.Visible : Visibility.Collapsed;
                if (panel.Visibility == v) return;
                panel.Visibility = v;
                if (visible && animate) UiMotion.FadeIn(panel, 4);
            }
            Show(PanelVideoOptions, type == ConvertFileType.Video);
            Show(PanelImageOptions, type == ConvertFileType.Image);
            Show(PanelAudioOptions, type == ConvertFileType.Audio);

            var o = BuildOptions();

            // Settings that don't apply are disabled, with the reason, instead of silently ignored.
            bool lossyImage = o.ImageFormat is ConvertImageFormat.Jpg or ConvertImageFormat.Webp or ConvertImageFormat.Avif;
            CmbImageQuality.IsEnabled = lossyImage;
            TxtImageQualityNote.Visibility = lossyImage ? Visibility.Collapsed : Visibility.Visible;
            PanelImageCustomSize.Visibility = o.ImageResize == ConvertImageResize.Custom ? Visibility.Visible : Visibility.Collapsed;

            bool lossless = ConverterManager.IsLossless(o.AudioFormat);
            CmbAudioBitrate.IsEnabled = !lossless;
            TxtBitrateNote.Visibility = lossless ? Visibility.Visible : Visibility.Collapsed;

            TxtTypeHint.Text = type switch
            {
                ConvertFileType.Image => o.ImageFormat == ConvertImageFormat.Ico
                    ? "Icons are made at up to 256 x 256. Photos are turned upright using their camera rotation."
                    : "Photos are turned upright using their camera rotation. Animated GIF/WEBP stay animated as GIF or WEBP.",
                ConvertFileType.Audio => "Videos can be added too - their soundtrack is saved as audio.",
                _ => "H.264 MP4 plays everywhere. H.265 and AV1 make much smaller files but take longer to convert.",
            };

            bool needsFfmpeg = type != ConvertFileType.Image && ConverterManager.FfmpegExe == null;
            TxtFfmpegWarning.Text = $"Needs ffmpeg.exe in {ConverterManager.FfmpegFolder} - it wasn't found.";
            UiMotion.FadeVisibility(FfmpegWarning, needsFfmpeg);

            UpdateOutputUi(animate: false);
        }

        /// <summary>
        /// Enables only the codecs the chosen container can hold, keeping the
        /// user's pick when it fits (the old view let you choose e.g.
        /// WEBM + H.264 and then every file failed).
        /// </summary>
        private void UpdateCodecChoices()
        {
            var format = CheckedTag(VideoFormatChips, ConvertVideoFormat.Mp4);
            var allowed = ConverterManager.CodecsFor(format);
            _updatingCodecs = true;
            try
            {
                foreach (var item in CmbVideoCodec.Items.OfType<ComboBoxItem>())
                    item.IsEnabled = Enum.TryParse((string)item.Tag, out ConvertVideoCodec c) && allowed.Contains(c);
                SelectTag(CmbVideoCodec, ConverterManager.ResolveCodec(format, _preferredCodec).ToString());
            }
            finally
            {
                _updatingCodecs = false;
            }

            if (allowed.Count < 4)
            {
                string names = string.Join(" or ", allowed.Select(c => ConverterManager.CodecLabel(c).Split(' ')[0]));
                TxtCodecNote.Text = $"{format.ToString().ToUpperInvariant()} can only hold {names}.";
                TxtCodecNote.Visibility = Visibility.Visible;
            }
            else
            {
                TxtCodecNote.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>The Output card's extra options for this tool.</summary>
        private void BuildOutputOptions()
        {
            ChkKeepStructure = OptionBox("Keep folder structure", isChecked: true,
                "Files from an added folder keep their subfolders, so two 'cover.jpg' from different albums don't collide");
            ChkOpenWhenDone = OptionBox("Open the folder when done", isChecked: false, null);
            var panel = new StackPanel();
            panel.Children.Add(ChkKeepStructure);
            panel.Children.Add(ChkOpenWhenDone);
            OutputCard.Options = panel;
        }

        private CheckBox OptionBox(string text, bool isChecked, string? tip)
        {
            var box = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 8, 0, 0), ToolTip = tip };
            box.SetResourceReference(ForegroundProperty, "SidebarText");
            box.Checked += Option_Changed;
            box.Unchecked += Option_Changed;
            return box;
        }

        /// <summary>Each file type has its own default folder; the card shows it until the user picks one.</summary>
        private void UpdateOutputUi(bool animate)
        {
            OutputCard.DefaultFolder = ConverterManager.DefaultOutputFolder(SelectedType);
            ChkKeepStructure.Visibility = OutputCard.IsSameAsSource ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The card's example line: where the first file will be written.</summary>
        private void UpdateOutputExample()
        {
            var first = _rows.FirstOrDefault(i => ConverterManager.IsPending(i) && !string.IsNullOrEmpty(i.OutputPath));
            OutputCard.ExampleFileName = first?.OutputPath is { } planned ? Path.GetFileName(planned) : "";
        }

        // ---------------- Settings ----------------

        private void ApplySettings(ConverterSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                (s.FileType switch
                {
                    ConvertFileType.Image => RadioTypeImage,
                    ConvertFileType.Audio => RadioTypeAudio,
                    _ => RadioTypeVideo,
                }).IsChecked = true;

                CheckTag(VideoFormatChips, s.VideoFormat.ToString());
                _preferredCodec = s.VideoCodec;
                SelectTag(CmbVideoResolution, s.VideoResolution.ToString());
                CheckTag(VideoQualityChips, s.VideoQuality.ToString());

                CheckTag(ImageFormatChips, s.ImageFormat.ToString());
                SelectTag(CmbImageQuality, s.ImageQuality.ToString());
                SelectTag(CmbImageResize, s.ImageResize.ToString());
                TxtImageCustomWidth.Text = s.ImageCustomWidth > 0 ? s.ImageCustomWidth.ToString() : "";
                TxtImageCustomHeight.Text = s.ImageCustomHeight > 0 ? s.ImageCustomHeight.ToString() : "";

                CheckTag(AudioFormatChips, s.AudioFormat.ToString());
                SelectTag(CmbAudioBitrate, s.AudioBitrate.ToString());
                SelectTag(CmbAudioSampleRate, s.AudioSampleRate.ToString());

                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkMatchType.IsChecked = s.MatchSelectedTypeOnly;
                ChkOverwriteExisting.IsChecked = s.OverwriteExisting;
                ChkDeleteOriginal.IsChecked = s.DeleteOriginal;
                OutputCard.SameAsSource = s.OutputMode == ConvertOutputMode.NextToOriginal;
                // The folder itself is never restored: chosen folders are kept for the session only.
                ChkKeepStructure.IsChecked = s.KeepFolderStructure;
                ChkOpenWhenDone.IsChecked = s.OpenFolderWhenDone;

                UpdateCodecChoices();
                UpdateTypeUi(animate: false);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            var o = BuildOptions();
            _settings.FileType = o.FileType;
            _settings.VideoFormat = o.VideoFormat;
            _settings.VideoResolution = o.VideoResolution;
            _settings.VideoCodec = _preferredCodec;
            _settings.VideoQuality = o.VideoQuality;
            _settings.ImageFormat = o.ImageFormat;
            _settings.ImageQuality = o.ImageQuality;
            _settings.ImageResize = o.ImageResize;
            _settings.ImageCustomWidth = o.ImageCustomWidth ?? 0;
            _settings.ImageCustomHeight = o.ImageCustomHeight ?? 0;
            _settings.AudioFormat = o.AudioFormat;
            _settings.AudioBitrate = o.AudioBitrate;
            _settings.AudioSampleRate = o.AudioSampleRate;
            _settings.IncludeSubfolders = o.IncludeSubfolders;
            _settings.MatchSelectedTypeOnly = o.MatchSelectedTypeOnly;
            _settings.OverwriteExisting = o.OverwriteExisting;
            _settings.DeleteOriginal = o.DeleteOriginalAfterConversion;
            _settings.OutputMode = o.OutputMode;
            _settings.OutputFolder = "";   // never save a folder path (this also clears one saved by the old view)
            _settings.KeepFolderStructure = o.KeepFolderStructure;
            _settings.OpenFolderWhenDone = ChkOpenWhenDone.IsChecked == true;
            try
            {
                ConverterManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Converter settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  The file list
        // =================================================================

        /// <summary>
        /// Rebuilds the grid from the already-listed files (no disk access),
        /// so every setting change shows its effect at once. A new
        /// collection is swapped in rather than cleared and refilled.
        /// </summary>
        private void RebuildList()
        {
            if (_isConverting || !_viewConstructed) return;

            var hidden = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
            hidden.UnionWith(_produced);
            _lastList = ConverterManager.BuildList(_queued, BuildOptions(), hidden);
            _rows = new ObservableCollection<ConvertFileItem>(_lastList.Items);
            DataGridFiles.ItemsSource = _rows;
            _lastRun = null;

            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
            UpdateOutputExample();
        }

        private void UpdateReadyText()
        {
            if (_isConverting) return;
            ProgressConversion.IsIndeterminate = false;
            ProgressConversion.Value = 0;
            TxtProgressPercent.Text = "";
            if (_rows.Count == 0 && _sources.Count == 0)
            {
                TxtProgress.Text = ReadyHint;
                return;
            }

            int pending = _rows.Count(ConverterManager.IsPending);
            var notes = new List<string> { $"{pending:N0} file{(pending == 1 ? "" : "s")} ready to convert" };
            if (_lastList?.SkippedOtherType > 0) notes.Add($"{_lastList.SkippedOtherType:N0} of another type left out");
            if (_lastQueue?.Inaccessible > 0) notes.Add($"{_lastQueue.Inaccessible:N0} folders couldn't be read");
            if (_lastQueue?.Links > 0) notes.Add($"{_lastQueue.Links:N0} linked folders not followed");
            if (_lastQueue?.Missing > 0) notes.Add($"{_lastQueue.Missing:N0} no longer exist");
            if (_listingError != null) notes.Add("couldn't read the folders: " + _listingError);
            TxtProgress.Text = string.Join(" · ", notes);
        }

        private void UpdateSummary()
        {
            var pending = _rows.Where(ConverterManager.IsPending).ToList();
            TxtSummaryFiles.Text = $"{pending.Count:N0} file{(pending.Count == 1 ? "" : "s")} · {ConvertText.FormatBytes(pending.Sum(i => i.SizeBytes))}";
            TxtSummaryTarget.Text = ConverterManager.DescribeTarget(BuildOptions());

            int skipped = _rows.Count(i => i.Status is ConvertStatus.Skipped or ConvertStatus.Unsupported);
            TxtSummarySkipped.Text = $"{skipped:N0} skipped";
            PillSkipped.Visibility = skipped > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_lastRun is { } run)
            {
                long saved = run.InputBytes - run.OutputBytes;
                TxtSummaryDone.Text = $"{run.Converted:N0} converted" + (saved > 0 ? $" · {ConvertText.FormatBytes(saved)} smaller" : "");
                TxtSummaryFailed.Text = $"{run.Failed:N0} failed";
                PillDone.Visibility = run.Converted > 0 ? Visibility.Visible : Visibility.Collapsed;
                PillFailed.Visibility = run.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                PillDone.Visibility = Visibility.Collapsed;
                PillFailed.Visibility = Visibility.Collapsed;
            }

            bool show = _rows.Count > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            EmptyStatePanel.Description = _sources.Count > 0 && empty
                ? $"None of the added files are {SelectedType.ToString().ToLowerInvariant()} files this tool can read. Pick another type above, or untick \"Only list files of this type\" to see them."
                : "Drop files or folders on the left, pick what to turn them into, then press Convert.";
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            UiMotion.FadeVisibility(DataGridFiles, !empty);
        }

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (_isConverting) return;
            var selected = DataGridFiles.SelectedItems.Cast<ConvertFileItem>().ToList();
            if (selected.Count == 0) return;
            foreach (var item in selected)
            {
                _removed.Add(item.FullPath);
                _rows.Remove(item);
            }
            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveSelected();
            e.Handled = true;
        }

        /// <summary>Double-click: the converted file if there is one, else the original, in Explorer.</summary>
        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridFiles.SelectedItem is not ConvertFileItem item) return;
            string path = item.Status == ConvertStatus.Converted && File.Exists(item.OutputPath) ? item.OutputPath! : item.FullPath;
            if (ShellExplorer.Show(path) == ShellExplorer.ShowResult.NotFound)
                MessageBox.Show("That file no longer exists.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e) => OpenOutput(BuildOptions(), quiet: false);

        private void OpenOutput(ConvertOptions o, bool quiet)
        {
            string? target = o.OutputMode == ConvertOutputMode.Folder
                ? o.OutputFolder
                : _rows.FirstOrDefault(i => i.Status == ConvertStatus.Converted && File.Exists(i.OutputPath))?.OutputPath
                  ?? _rows.FirstOrDefault()?.Folder;

            if (target != null && ShellExplorer.Show(target) == ShellExplorer.ShowResult.Shown) return;
            if (!quiet)
                MessageBox.Show(o.OutputMode == ConvertOutputMode.Folder
                        ? $"Nothing has been saved to {o.OutputFolder} yet."
                        : "Converted files are saved next to their originals - convert something first.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Convert
        // =================================================================

        private async void BtnConvertFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting)
            {
                _convertCts?.Cancel();
                TxtProgress.Text = "Cancelling...";
                return;
            }
            // A second click while the folders are still being read used to
            // start a second batch on the same files once the reading finished.
            if (_startingConvert) return;
            _startingConvert = true;
            try
            {
                await StartConvertAsync();
            }
            finally
            {
                _startingConvert = false;
            }
        }

        private async Task StartConvertAsync()
        {
            if (_expandTask is { IsCompleted: false })
            {
                TxtProgress.Text = "Reading folders...";
                await _expandTask;
            }

            var pending = _rows.Where(ConverterManager.IsPending).ToList();
            if (pending.Count == 0)
            {
                InputCard.PopDropArea();
                MessageBox.Show(_rows.Count == 0
                        ? "Add some files or a folder first (left side, or drop them there)."
                        : "Nothing left to convert - every file is already converted or doesn't need it.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            if (options.FileType != ConvertFileType.Image && ConverterManager.FfmpegExe == null)
            {
                MessageBox.Show($"Converting {(options.FileType == ConvertFileType.Video ? "videos" : "audio")} needs ffmpeg.exe in {ConverterManager.FfmpegFolder}. It wasn't found there.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (options.OutputMode == ConvertOutputMode.Folder)
            {
                if (OutputCard.Status.State is OutputFolderState.Missing or OutputFolderState.NotWritable)
                {
                    UiMotion.Pop(OutputCard, 0.95);
                    MessageBox.Show($"Can't save to {options.OutputFolder}: {OutputCard.Status.Text}.\n\nChoose another folder in Save to.",
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(options.OutputFolder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    MessageBox.Show($"Couldn't create {options.OutputFolder}: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (options.DeleteOriginalAfterConversion && !ConfirmDeleteOriginals(pending)) return;

            SaveSettings();
            _convertCts?.Dispose();
            _convertCts = new CancellationTokenSource();
            SetConverting(true);

            try
            {
                var progress = new Progress<ConvertProgress>(ReportProgress);
                var result = await _manager.RunAsync(_rows.ToList(), options, progress, _convertCts.Token);
                _lastRun = result;

                foreach (var item in _rows.Where(i => i.Status == ConvertStatus.Converted && i.OutputPath != null))
                    _produced.Add(item.OutputPath!);

                SetConverting(false);
                ProgressConversion.Value = result.Cancelled > 0 ? ProgressConversion.Value : 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = ConverterManager.DescribeRun(result);

                try
                {
                    ToastNotificationManager.ShowToast("Conversion Complete",
                        result.Failed > 0
                            ? $"Converted {result.Converted} of {result.Converted + result.Failed + result.Cancelled} files - {result.Failed} failed."
                            : $"Converted {result.Converted} file{(result.Converted == 1 ? "" : "s")}.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }

                if (ChkOpenWhenDone.IsChecked == true && result.Converted > 0 && result.Cancelled == 0) OpenOutput(options, quiet: true);
            }
            catch (Exception ex)
            {
                SetConverting(false);
                TxtProgress.Text = "Conversion failed.";
                MessageBox.Show($"Conversion failed: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isConverting) SetConverting(false);
                UpdateSummary();
            }
        }

        private bool ConfirmDeleteOriginals(IReadOnlyList<ConvertFileItem> pending)
        {
            int noBin = pending.Count(i => !ConverterManager.HasRecycleBin(i.FullPath));
            string text = noBin == 0
                ? $"After each file converts, its original will be moved to the Recycle Bin ({pending.Count:N0} files).\n\nContinue?"
                : $"After each file converts, its original will be deleted.\n\n{noBin:N0} of them are on a drive without a Recycle Bin (network or removable) and will be deleted PERMANENTLY.\n\nContinue?";
            return MessageBox.Show(text, DialogTitle, MessageBoxButton.YesNo, noBin == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void ReportProgress(ConvertProgress p)
        {
            if (!_isConverting) return;
            ProgressConversion.IsIndeterminate = false;
            ProgressConversion.Value = p.OverallPercent;
            TxtProgressPercent.Text = $"{p.OverallPercent:0}%";
            string current = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" · {p.CurrentFile}" + (p.CurrentFilePercent >= 1 ? $" ({p.CurrentFilePercent:0}%)" : "");
            TxtProgress.Text = $"{p.Completed:N0} of {p.Total:N0} done{current}";
        }

        private void SetConverting(bool converting)
        {
            _isConverting = converting;

            IconConvert.Kind = converting ? PackIconKind.StopCircleOutline : PackIconKind.SwapHorizontal;
            if (converting) TxtConvertLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtConvertLabel.SetResourceReference(TextBlock.TextProperty, "Str_Convert_ConvertButton");
            UiMotion.Pop(BtnConvertFiles, 0.96);

            // Changing settings mid-run used to rebuild the list under the running batch.
            OptionsScrollViewer.IsEnabled = !converting;
            OutputCard.IsLocked = converting;
            if (!converting) OutputCard.Refresh();   // free space changed (and a default folder may now exist)
            BtnPreview.IsEnabled = !converting;
            BtnRemoveFiles.IsEnabled = !converting;
            BtnClearFiles.IsEnabled = !converting;

            if (converting)
            {
                ProgressConversion.IsIndeterminate = false;
                ProgressConversion.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = "Starting...";
            }
        }
    }
}
