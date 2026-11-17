using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Speech to Text - UI only. Models, languages, whisper's arguments and the subtitle formats are in
    /// <see cref="SpeechToTextManager"/>; the engines in SpeechToTextManager.Run.cs; model downloads in
    /// <see cref="WhisperModelManager"/>.
    /// </summary>
    public partial class SpeechToTextView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_speech-to-text_Name", "Speech to Text");

        private readonly ObservableCollection<SttFileItem> _rows = new();
        private readonly ObservableCollection<SttModelItem> _models = new();
        private readonly Dictionary<string, CancellationTokenSource> _downloads = new();
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource _probeCts = new();
        private SttFileItem? _shown;
        private int _probing;
        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _settingText;

        /// <summary>The run bar shows a run's summary: the Output card's refresh after the run mustn't replace it.</summary>
        private bool _holdStatus;
        private readonly bool _viewConstructed;

        // Made in code: no x:Name inside the Output card's content (MC3093).
        private CheckBox ChkOpenWhenDone = null!;

        private string S(string name, string fallback) => TryFindResource("Str_Stt_" + name) as string ?? fallback;

        public SpeechToTextView()
        {
            InitializeComponent();
            BuildOutputOptions();

            InputCard.ItemsSource = _rows;
            InputCard.ShowSubfolders = false;
            InputCard.FileFilter = $"Audio and video|{string.Join(";", SpeechToTextManager.MediaExtensions.Select(x => "*" + x))}|All files (*.*)|*.*";
            DataGridFiles.ItemsSource = _rows;
            ListModels.ItemsSource = _models;
            CmbLanguage.ItemsSource = SpeechToTextManager.Languages;
            CmbLanguage.SelectedIndex = 0;
            OutputCard.DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "Speech to Text");

            foreach (var info in SpeechToTextManager.Models) _models.Add(new SttModelItem(info));
            TxtTranscript.TextChanged += TxtTranscript_TextChanged;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops the speech engine, FFmpeg, ffprobe, and any model download.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _probeCts.Cancel();
            foreach (var cts in _downloads.Values.ToList()) cts.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                RefreshModels();
                ApplySettings(SpeechToTextManager.LoadSettings());
                UpdateLanguageNote(switchModel: false);
            }
            else
            {
                RefreshModels();
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateEngineWarning();
            UpdateSummary();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            ColLanguage.Visibility = width < 980 ? Visibility.Collapsed : Visibility.Visible;
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

        // =================================================================
        //  Models
        // =================================================================

        private string SelectedModelId => (CmbModel.SelectedItem as SttModelInfo)?.Id ?? "";

        /// <summary>Reads which models are on disk, then refills the "model to use" list (keeping the choice when it is still there).</summary>
        private void RefreshModels()
        {
            var installed = WhisperModelManager.InstalledIds();
            foreach (var item in _models.Where(m => !m.IsBusy))
            {
                item.IsInstalled = installed.Contains(item.Id);
                item.Detail = ModelDetail(item);
            }

            string keep = SelectedModelId;
            bool was = _applyingSettings;
            _applyingSettings = true;
            try
            {
                var usable = SpeechToTextManager.Models.Where(m => installed.Contains(m.Id)).ToList();
                CmbModel.ItemsSource = usable;
                CmbModel.IsEnabled = usable.Count > 0;
                CmbModel.SelectedItem = usable.FirstOrDefault(m => m.Id == keep) ?? usable.FirstOrDefault(m => m.Id == "base") ?? usable.FirstOrDefault();
            }
            finally
            {
                _applyingSettings = was;
            }
            UpdateModelHint();
            UpdateLanguageNote(switchModel: false);
        }

        private string ModelDetail(SttModelItem item)
        {
            if (item.IsInstalled)
            {
                long bytes = WhisperModelManager.SizeOf(item.Id);
                return $"{item.Info.Hint} · {bytes / 1048576.0:0} MB";
            }
            return $"{item.Info.Hint} · about {item.Info.ApproxMb:N0} MB";
        }

        private void UpdateModelHint()
        {
            var model = CmbModel.SelectedItem as SttModelInfo;
            TxtModelHint.Text = model == null
                ? S("NoModel", "No model is installed yet. Press Install next to one below. Base is a good start.")
                : model.Hint + ".";
        }

        private async void BtnInstallModel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SttModelItem item || item.IsBusy) return;

            using var cts = new CancellationTokenSource();
            _downloads[item.Id] = cts;
            item.IsBusy = true;
            item.Progress = 0;
            item.Detail = S("Downloading", "Downloading...");
            var progress = new Progress<double>(p =>
            {
                item.Progress = p * 100;
                item.Detail = string.Format(S("DownloadingPercent", "Downloading... {0}%"), (int)(p * 100));
            });

            string? problem;
            try
            {
                problem = await WhisperModelManager.InstallAsync(item.Id, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                problem = S("Canceled", "Canceled");
            }
            finally
            {
                _downloads.Remove(item.Id);
                item.IsBusy = false;
            }

            item.IsInstalled = problem == null && WhisperModelManager.IsInstalled(item.Id);
            item.Detail = problem ?? ModelDetail(item);
            string wanted = problem == null ? item.Id : SelectedModelId;
            RefreshModels();
            if (problem == null && CmbModel.Items.OfType<SttModelInfo>().FirstOrDefault(m => m.Id == wanted) is { } installed)
                CmbModel.SelectedItem = installed;   // a model just installed is the one the user wants
            UpdateEngineWarning();
        }

        private void BtnCancelModel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SttModelItem item && _downloads.TryGetValue(item.Id, out var cts)) cts.Cancel();
        }

        private void BtnRemoveModel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SttModelItem item || _isRunning) return;
            if (!WhisperModelManager.Remove(item.Id))
            {
                item.Detail = S("RemoveFailed", "Couldn't remove this model.");
                return;
            }
            item.IsInstalled = false;
            item.Detail = ModelDetail(item);
            RefreshModels();
            SaveSettings();
            UpdateEngineWarning();
        }

        private void UpdateEngineWarning()
        {
            string? missing = !SpeechToTextManager.IsWhisperAvailable
                ? S("NoEngine", "The speech engine isn't in this copy of Smart Tools.")
                : ConverterManager.FfmpegExe == null
                    ? S("NoFfmpeg", "FFmpeg isn't installed with this copy of Smart Tools, so the sound can't be prepared.")
                    : null;
            TxtEngineWarning.Text = missing ?? "";
            EngineWarning.Visibility = missing == null ? Visibility.Collapsed : Visibility.Visible;
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

        /// <summary>Files are added as they are; a folder adds every audio and video file inside it, subfolders too.</summary>
        private void AddPaths(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    if (SpeechToTextManager.IsMedia(path)) found.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    try
                    {
                        found.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                            .Where(SpeechToTextManager.IsMedia).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            var fresh = new List<SttFileItem>();
            foreach (string path in found)
            {
                if (!_paths.Add(path)) continue;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var item = new SttFileItem { FullPath = path, SizeBytes = size };
                _rows.Add(item);
                fresh.Add(item);
            }

            InputCard.PopDropArea();
            _holdStatus = false;
            UpdateSummary();
            if (fresh.Count > 0)
            {
                TxtProgress.Text = string.Format(S("Reading", "Reading {0}..."), SttText.Plural(fresh.Count, "file", "files"));
                _ = ProbeAsync(fresh);
            }
        }

        private void BtnRemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || (sender as FrameworkElement)?.Tag is not SttFileItem item) return;
            RemoveRows(new[] { item });
        }

        private void ClearAll()
        {
            if (_isRunning) return;
            _probeCts.Cancel();
            _probeCts = new CancellationTokenSource();
            _rows.Clear();
            _paths.Clear();
            ShowTranscript(null);
            UpdateSummary();
            TxtProgress.Text = S("ReadyHint", "Add audio or video files, then press Transcribe.");
            TxtProgressPercent.Text = "";
            ProgressRun.Value = 0;
        }

        /// <summary>ffprobe for new rows, four at a time; each row updates as soon as it's read.</summary>
        private async Task ProbeAsync(IReadOnlyList<SttFileItem> items)
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
                        VtaProbe probe;
                        try
                        {
                            probe = await VideoToAudioManager.ProbeAsync(item.FullPath, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        if (!_rows.Contains(item)) return;   // removed while it was being read

                        item.Duration = probe.Duration?.TotalSeconds;
                        if (probe.Error != null)
                        {
                            item.Message = probe.Error;
                            item.Status = SttItemStatus.Unreadable;
                        }
                        else if (probe.Tracks.Count == 0)
                        {
                            item.Message = S("NoSound", "This file has no sound, so there is nothing to transcribe.");
                            item.Status = SttItemStatus.Unreadable;
                        }
                        else
                        {
                            item.Message = "";
                            item.Status = SttItemStatus.Ready;
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

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveRows(DataGridFiles.SelectedItems.OfType<SttFileItem>().ToList());

        private void RemoveRows(IReadOnlyList<SttFileItem> chosen)
        {
            if (_isRunning || chosen.Count == 0) return;
            int next = _rows.IndexOf(chosen[0]);
            foreach (var item in chosen)
            {
                _paths.Remove(item.FullPath);
                _rows.Remove(item);
            }
            if (_rows.Count > 0) DataGridFiles.SelectedItem = _rows[Math.Clamp(next, 0, _rows.Count - 1)];
            else ShowTranscript(null);
            UpdateSummary();
            TxtProgress.Text = ReadyText();
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveRows(DataGridFiles.SelectedItems.OfType<SttFileItem>().ToList());
            e.Handled = true;
        }

        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridFiles.SelectedItem is not SttFileItem item) return;
            string path = item.Status == SttItemStatus.Done && item.OutputPaths.Count > 0 && File.Exists(item.OutputPaths[0]) ? item.OutputPaths[0] : item.FullPath;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(Loc.F("Str_Err_OpenFile", "Couldn't open {0}: {1}", Path.GetFileName(path), ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e) => OpenOutput(quiet: false);

        private void OpenOutput(bool quiet)
        {
            string? firstResult = (DataGridFiles.SelectedItem as SttFileItem)?.OutputPaths.FirstOrDefault(File.Exists)
                                  ?? _rows.SelectMany(r => r.OutputPaths).FirstOrDefault(File.Exists);
            string? target = OutputCard.IsSameAsSource ? firstResult : firstResult ?? OutputCard.EffectiveFolder;
            if (target != null && ShellExplorer.Show(target) == ShellExplorer.ShowResult.Shown) return;
            if (!quiet)
                ThemedMessageBox.Show(OutputCard.IsSameAsSource
                        ? S("NothingSavedSource", "The transcripts are saved next to each file - transcribe something first.")
                        : $"Nothing has been saved to {OutputCard.EffectiveFolder} yet.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  The transcript
        // =================================================================

        private void DataGridFiles_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            ShowTranscript(DataGridFiles.SelectedItems.Count == 1 ? DataGridFiles.SelectedItem as SttFileItem : null);

        private void ShowTranscript(SttFileItem? item)
        {
            _shown = item;
            _settingText = true;
            try
            {
                TxtTranscript.Text = item?.Text ?? "";
            }
            finally
            {
                _settingText = false;
            }
            UpdateTranscriptInfo();
        }

        /// <summary>An edit in the box belongs to the file it is showing, so it survives clicking another row and back.</summary>
        private void TxtTranscript_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settingText) return;
            if (_shown != null) _shown.Text = TxtTranscript.Text;
            UpdateTranscriptInfo();
        }

        private void UpdateTranscriptInfo()
        {
            bool any = TxtTranscript.Text.Length > 0;
            BtnCopy.IsEnabled = any;
            BtnSaveAs.IsEnabled = any && _shown != null;
            if (_shown == null || !any)
            {
                TxtTranscriptInfo.Text = "";
                return;
            }
            string language = _shown.Language.Length > 0 ? (SpeechToTextManager.Languages.FirstOrDefault(l => l.Code == _shown.Language)?.Name ?? _shown.Language) : "";
            string words = SttText.Plural(SpeechToTextManager.WordCount(TxtTranscript.Text), "word", "words");
            TxtTranscriptInfo.Text = language.Length > 0 ? $"{language} · {words}" : words;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtTranscript.SelectionLength > 0 ? TxtTranscript.SelectedText : TxtTranscript.Text;
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
                UiMotion.Pop(BtnCopy, 0.9);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ThemedMessageBox.Show(S("ClipboardBusy", "The clipboard is in use by another program. Try again."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_shown == null || TxtTranscript.Text.Length == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                FileName = Path.GetFileNameWithoutExtension(_shown.FullPath) + ".txt",
                Filter = "Text (*.txt)|*.txt|SubRip subtitles (*.srt)|*.srt|WebVTT subtitles (*.vtt)|*.vtt",
                InitialDirectory = OutputCard.EffectiveFolder is { } folder && Directory.Exists(folder) ? folder : _shown.Folder,
            };
            if (dialog.ShowDialog() != true) return;

            string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            string content = ext switch
            {
                ".srt" when _shown.Segments.Count > 0 => SpeechToTextManager.FormatSrt(_shown.Segments),
                ".vtt" when _shown.Segments.Count > 0 => SpeechToTextManager.FormatVtt(_shown.Segments),
                _ => TxtTranscript.Text,
            };
            try
            {
                AtomicFile.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));
                TxtProgress.Text = string.Format(S("SavedAs", "Saved {0}"), Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =================================================================
        //  Settings controls
        // =================================================================

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private string SelectedLanguage => (CmbLanguage.SelectedItem as SttLanguage)?.Code ?? "";

        /// <summary>The model a run uses: the chosen one, unless it is English only and the language or Translate needs another.</summary>
        private string EffectiveModelId() =>
            SpeechToTextManager.ModelFor(SelectedModelId, SelectedLanguage, ChkTranslate.IsChecked == true, WhisperModelManager.InstalledIds());

        /// <summary>
        /// Picking a language an English-only model can't do switches to the installed multilingual model (and says so); when there is
        /// none, it says which one to install. Nothing is shown when the combination is fine.
        /// </summary>
        private void UpdateLanguageNote(bool switchModel)
        {
            string chosen = SelectedModelId;
            string effective = EffectiveModelId();
            string language = (CmbLanguage.SelectedItem as SttLanguage)?.Name ?? "";
            string? note = null;

            if (effective != chosen && switchModel)
            {
                bool was = _applyingSettings;
                _applyingSettings = true;
                try
                {
                    CmbModel.SelectedItem = CmbModel.Items.OfType<SttModelInfo>().FirstOrDefault(m => m.Id == effective) ?? CmbModel.SelectedItem;
                }
                finally
                {
                    _applyingSettings = was;
                }
                UpdateModelHint();
                note = string.Format(S("LanguageSwitched", "Switched to {0} because English-only models can't do {1}."),
                    SpeechToTextManager.FindModel(effective)?.Name ?? effective, ChkTranslate.IsChecked == true && SelectedLanguage.Length == 0 ? S("ThisAudio", "this audio") : language);
            }
            else if (SpeechToTextManager.FindModel(chosen)?.EnglishOnly == true)
            {
                bool conflict = ChkTranslate.IsChecked == true || (SelectedLanguage.Length > 0 && SelectedLanguage != "en");
                if (conflict)
                    note = string.Format(S("LanguageNeedsModel", "{0} is English only. Install a model without \"English only\" below to use {1}."),
                        SpeechToTextManager.FindModel(chosen)?.Name ?? chosen, language.Length > 0 ? language : S("ThisAudio", "this audio"));
                else if (SelectedLanguage.Length == 0)
                    note = S("AutoEnglishOnly", "English-only models only understand English. For other languages, use a model without \"English only\".");
            }

            TxtLanguageNote.Text = note ?? "";
            TxtLanguageNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        private SttOptions BuildOptions() => new()
        {
            ModelId = EffectiveModelId(),
            Language = SelectedLanguage,
            Translate = ChkTranslate.IsChecked == true,
            WriteTxt = ChkTxt.IsChecked == true,
            WriteSrt = ChkSrt.IsChecked == true,
            WriteVtt = ChkVtt.IsChecked == true,
            TimestampsInText = ChkTimestamps.IsChecked == true,
        };

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            if (ReferenceEquals(sender, CmbModel)) UpdateModelHint();
            UpdateLanguageNote(switchModel: ReferenceEquals(sender, CmbLanguage));
            _holdStatus = false;
            SaveSettings();
            if (!_isRunning && _rows.Count > 0 && _probing == 0) TxtProgress.Text = ReadyText();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            if (ReferenceEquals(sender, ChkTranslate)) UpdateLanguageNote(switchModel: true);
            SaveSettings();
        }

        /// <summary>At least one format stays on: a run with nothing to write would waste the time.</summary>
        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            if (ChkTxt.IsChecked != true && ChkSrt.IsChecked != true && ChkVtt.IsChecked != true && sender is CheckBox box) box.IsChecked = true;
            SaveSettings();
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

        private void ApplySettings(SttSettings s)
        {
            _applyingSettings = true;
            try
            {
                CmbModel.SelectedItem = CmbModel.Items.OfType<SttModelInfo>().FirstOrDefault(m => m.Id == s.ModelId) ?? CmbModel.SelectedItem;
                CmbLanguage.SelectedItem = SpeechToTextManager.Languages.FirstOrDefault(l => l.Code == s.Language) ?? SpeechToTextManager.Languages[0];
                ChkTranslate.IsChecked = s.Translate;
                ChkTxt.IsChecked = s.WriteTxt;
                ChkSrt.IsChecked = s.WriteSrt;
                ChkVtt.IsChecked = s.WriteVtt;
                ChkTimestamps.IsChecked = s.TimestampsInText;
                ChkOpenWhenDone.IsChecked = s.OpenWhenDone;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateModelHint();
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            var o = BuildOptions();
            var previous = SpeechToTextManager.LoadSettings();
            SpeechToTextManager.SaveSettings(new SttSettings
            {
                ModelId = o.ModelId.Length > 0 ? o.ModelId : previous.ModelId,   // no model installed: keep the last choice
                Language = o.Language,
                Translate = o.Translate,
                WriteTxt = o.WriteTxt,
                WriteSrt = o.WriteSrt,
                WriteVtt = o.WriteVtt,
                TimestampsInText = o.TimestampsInText,
                OpenWhenDone = ChkOpenWhenDone.IsChecked == true,
            });
        }

        // =================================================================
        //  The list
        // =================================================================

        private string ReadyText()
        {
            int ready = _rows.Count(r => r.CanRun);
            if (_rows.Count == 0) return S("ReadyHint", "Add audio or video files, then press Transcribe.");
            if (ready == 0)
                return _rows.Any(r => r.Status == SttItemStatus.Done)
                    ? S("AllDone", "Everything in the list is transcribed. Add more files, or clear the list.")
                    : S("NoneReadable", "None of these files has sound that can be read.");
            string where = OutputCard.IsSameAsSource ? S("NextToFiles", "next to the files") : "to " + (OutputCard.EffectiveFolder ?? "the output folder");
            return $"{SttText.Plural(ready, "file", "files")} ready · saves {where}";
        }

        private void UpdateSummary()
        {
            int count = _rows.Count;
            bool any = count > 0;
            if (any && SummaryPanel.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPanel, 0.9);
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            double seconds = _rows.Sum(r => r.Duration ?? 0);
            TxtSummaryFiles.Text = seconds > 0 ? $"{SttText.Plural(count, "file", "files")} · {SttText.FormatTime(seconds)}" : SttText.Plural(count, "file", "files");

            int done = _rows.Count(r => r.Status == SttItemStatus.Done);
            int failed = _rows.Count(r => r.Status == SttItemStatus.Failed);
            TxtSummaryDone.Text = $"{done:N0} done";
            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFailed.Text = $"{failed:N0} failed";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

            UiMotion.FadeVisibility(EmptyStatePanel, !any);
            DataGridFiles.Visibility = any ? Visibility.Visible : Visibility.Hidden;
            TranscriptHeader.Visibility = TxtTranscript.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            BtnRemoveFiles.IsEnabled = !_isRunning && any;
            BtnClearFiles.IsEnabled = !_isRunning && any;
            UpdateTranscriptInfo();
        }

        // =================================================================
        //  Transcribe
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
            var options = BuildOptions();
            if (SpeechToTextManager.MissingEngine(options.ModelId.Length > 0 ? options.ModelId : "base") is string missing)
            {
                TxtProgress.Text = missing;
                UiMotion.Pop(EngineWarning, 0.9);
                if (!OptionsScrollViewer.IsVisible) BtnCollapseOptions_Click(this, new RoutedEventArgs());
                return;
            }
            if (_rows.Count == 0)
            {
                TxtProgress.Text = S("AddFirst", "Add some audio or video files first.");
                InputCard.PopDropArea();
                return;
            }
            if (_probing > 0 || _rows.Any(r => r.Status == SttItemStatus.Reading))
            {
                TxtProgress.Text = S("StillReading", "Still reading the files - try again in a moment.");
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

            var cts = _runCts = new CancellationTokenSource();
            SetRunning(true);
            foreach (var row in pending) row.Status = SttItemStatus.Ready;
            var progress = new Progress<SttProgress>(ReportProgress);

            SttRunResult? result = null;
            try
            {
                result = await SpeechToTextManager.RunAsync(pending, options, (source, name) => OutputCard.ResultPathFor(source, name), progress, cts.Token);
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
            TxtProgress.Text = DescribeResult(result);

            if (DataGridFiles.SelectedItem is not SttFileItem && _rows.FirstOrDefault(r => r.Status == SttItemStatus.Done) is { } firstDone)
                DataGridFiles.SelectedItem = firstDone;
            else
                ShowTranscript(DataGridFiles.SelectedItems.Count == 1 ? DataGridFiles.SelectedItem as SttFileItem : null);

            if (result.Done > 0)
            {
                UiMotion.Pop(PillDone, 0.85);
                if (ChkOpenWhenDone.IsChecked == true) OpenOutput(quiet: true);
            }
        }

        private string DescribeResult(SttRunResult r)
        {
            var parts = new List<string>();
            if (r.Done > 0) parts.Add(SttText.Plural(r.Done, "file transcribed", "files transcribed"));
            if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
            if (r.Cancelled > 0) parts.Add($"{r.Cancelled:N0} canceled");
            if (parts.Count == 0) parts.Add("Nothing to do");
            return string.Join(" · ", parts) + $" · {SttText.FormatTime(r.Elapsed.TotalSeconds)}";
        }

        private void ReportProgress(SttProgress p)
        {
            if (!_isRunning) return;
            ProgressRun.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            string count = $"{p.Finished + p.Failed:N0} of {p.Total:N0}";
            TxtProgress.Text = p.CurrentName != null
                ? string.Format(S("Transcribing", "Transcribing {0} · {1}"), p.CurrentName, count)
                : $"{count} done";
            UpdateSummary();
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("speech-to-text");
            else { _activity?.Dispose(); _activity = null; }
            IconRun.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.TextBoxOutline;
            if (running) TxtRunLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtRunLabel.SetResourceReference(TextBlock.TextProperty, "Str_Stt_Transcribe");
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
