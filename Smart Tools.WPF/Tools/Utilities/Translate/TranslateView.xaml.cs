using Microsoft.ML.OnnxRuntime;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Translate - UI only. The model catalog, download, tokenizer and decoder are in
    /// <see cref="TranslateManager"/>. The text is translated off the UI thread a sentence at a time;
    /// Cancel keeps what was finished.
    /// </summary>
    public partial class TranslateView : UserControl
    {
        private readonly TranslateManager _manager = TranslateManager.Shared;
        private CancellationTokenSource? _translateCts;
        private CancellationTokenSource? _downloadCts;
        private bool _viewConstructed;
        private bool _applying;
        private bool _closing;
        private readonly System.Collections.ObjectModel.ObservableCollection<TranslateFileItem> _files = new();

        public TranslateView()
        {
            InitializeComponent();

            ResultsList.ItemsSource = _files;
            InputCard.ItemsSource = _files;
            InputCard.FileFilter = Loc.T("Str_Translate_FileFilter", "Text and subtitles") + "|"
                + string.Join(";", TranslateManager.FileExtensions.Select(x => "*" + x)) + "|" + Loc.T("Str_Translate_FileFilterAll", "All files") + " (*.*)|*.*";
            _files.CollectionChanged += (_, _) => UpdateFilesState();

            UpdateFilesState();
            LoadLanguages("en", "es");
            _viewConstructed = true;

            UpdateModelPanel();
            TranslateManager.PairsChanged += RefreshLanguages;   // a pair was added or removed in Settings
        }

        /// <summary>Fills the From box from the catalog and selects the pair, or English → Spanish, or the first pair there is.</summary>
        private void LoadLanguages(string from, string to)
        {
            _applying = true;
            FillCombo(CmbFrom, TranslateManager.Pairs.Select(p => p.FromCode).Distinct());
            var start = TranslateManager.Find(from, to) ?? TranslateManager.Find("en", "es") ?? TranslateManager.Pairs.FirstOrDefault();
            if (start != null) ShowPair(start.FromCode, start.ToCode);
            _applying = false;
        }

        private void RefreshLanguages()
        {
            LoadLanguages(TagOf(CmbFrom), TagOf(CmbTo));
            UpdateModelPanel();
        }

        /// <summary>Called when the app closes: stops a running translation or download and frees the model.</summary>
        public void CancelActiveRun()
        {
            _closing = true;
            _translateCts?.Cancel();
            _downloadCts?.Cancel();
            if (_translateCts == null) _manager.Dispose();   // otherwise the run frees it when it finishes
        }

        // ---- Languages ------------------------------------------------------------------------

        private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private TranslateLanguagePair? SelectedPair() => TranslateManager.Find(TagOf(CmbFrom), TagOf(CmbTo));

        /// <summary>Fills a box with one item per language code; the name comes from Str_Translate_Lang_{code} (the code itself if there is none).</summary>
        private static void FillCombo(ComboBox box, IEnumerable<string> codes)
        {
            box.Items.Clear();
            foreach (string code in codes)
            {
                var item = new ComboBoxItem { Tag = code };
                string key = $"Str_Translate_Lang_{code}";
                if (Application.Current.TryFindResource(key) != null) item.SetResourceReference(ContentControl.ContentProperty, key);
                else item.Content = code.ToUpperInvariant();
                box.Items.Add(item);
            }
        }

        private static void SelectByTag(ComboBox box, string code)
        {
            var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == code) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
            box.SelectedItem = item;
        }

        /// <summary>Shows a pair: picks From, then lists only the languages that From can be translated into. Call with _applying set.</summary>
        private void ShowPair(string from, string to)
        {
            SelectByTag(CmbFrom, from);
            FillCombo(CmbTo, TranslateManager.TargetsOf(TagOf(CmbFrom)));
            SelectByTag(CmbTo, to);
        }

        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;

            // A new From changes which languages are on offer for To; keep the old To when it still exists.
            if (sender == CmbFrom)
            {
                string previousTo = TagOf(CmbTo);
                _applying = true;
                ShowPair(TagOf(CmbFrom), previousTo);
                _applying = false;
            }
            UpdateModelPanel();
        }

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            string from = TagOf(CmbFrom), to = TagOf(CmbTo);
            if (TranslateManager.Route(to, from) == null && TranslateManager.Find(to, from) == null) return;
            _applying = true;
            ShowPair(to, from);
            _applying = false;

            // The translation becomes the next input, which is what swapping is usually for.
            if (!string.IsNullOrWhiteSpace(TxtOutput.Text))
            {
                TxtInput.Text = TxtOutput.Text;
                TxtOutput.Clear();
            }
            UpdateModelPanel();

            // Each direction is its own model: say so when the way back isn't installed yet.
            var pair = SelectedPair();
            if (pair != null && !TranslateManager.IsInstalled(pair))
                SetStatus(string.Format(Str("Str_Translate_StatusSwapNeedsModel"), LanguageName(pair.FromCode), LanguageName(pair.ToCode), pair.ApproxMb));
        }

        // ---- Model ----------------------------------------------------------------------------

        private void UpdateModelPanel()
        {
            var pair = SelectedPair();
            bool installed = pair != null && TranslateManager.IsInstalled(pair);
            bool downloading = _downloadCts != null;
            var route = TranslateManager.Route(TagOf(CmbFrom), TagOf(CmbTo));
            bool indirect = TranslateManager.IsIndirect(route);

            if (!downloading)
            {
                TxtModelStatus.Text = indirect
                    ? string.Format(Str("Str_Translate_StatusIndirect"), LanguageName(TagOf(CmbFrom)), LanguageName(TagOf(CmbTo)))
                    : pair == null
                        ? Str("Str_Translate_StatusSamePair")
                        : installed
                            ? string.Format(Str("Str_Translate_ModelReady"), FormatSize(TranslateManager.SizeOf(pair)))
                            : string.Format(Str("Str_Translate_ModelMissing"), pair.ApproxMb);
            }

            TxtModelPath.Text = pair != null ? TranslateManager.FolderOf(pair) : TranslateManager.ModelsRoot;
            BtnDownload.Visibility = downloading || (pair != null && !installed) ? Visibility.Visible : Visibility.Collapsed;
            BtnRemove.Visibility = !downloading && installed ? Visibility.Visible : Visibility.Collapsed;
            BtnRun.IsEnabled = !downloading;
            bool idle = !downloading && _translateCts == null;
            CmbFrom.IsEnabled = CmbTo.IsEnabled = idle;
            BtnSwap.IsEnabled = idle && (TranslateManager.Find(TagOf(CmbTo), TagOf(CmbFrom)) != null || TranslateManager.Route(TagOf(CmbTo), TagOf(CmbFrom)) != null);   // no way back → nothing to swap to
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadCts != null)
            {
                _downloadCts.Cancel();
                return;
            }
            if (SelectedPair() is not { } pair) return;

            _downloadCts = new CancellationTokenSource();
            IconDownload.Kind = MaterialDesignThemes.Wpf.PackIconKind.Close;
            TxtDownloadLabel.Text = Str("Str_Common_Cancel");
            ProgressDownload.Value = 0;
            ProgressDownload.Visibility = Visibility.Visible;
            TxtModelStatus.Text = string.Format(Str("Str_Translate_Downloading"), 0);
            UpdateModelPanel();

            var progress = new Progress<double>(v =>
            {
                ProgressDownload.Value = v * 100;
                TxtModelStatus.Text = string.Format(Str("Str_Translate_Downloading"), (int)(v * 100));
            });

            string? error = null;
            bool canceled = false;
            try
            {
                error = await _manager.InstallAsync(pair, progress, _downloadCts.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            finally
            {
                _downloadCts.Dispose();
                _downloadCts = null;
                ProgressDownload.Visibility = Visibility.Collapsed;
                IconDownload.Kind = MaterialDesignThemes.Wpf.PackIconKind.Download;
                TxtDownloadLabel.Text = Str("Str_Translate_Download");
                UpdateModelPanel();
            }

            if (canceled) SetStatus(Str("Str_Translate_StatusDownloadCanceled"));
            else if (error != null) SetStatus(error);
            else SetStatus(Str("Str_Translate_StatusIdle"));
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            // The pair's folder when it's downloaded, otherwise the folder the models will go in.
            string folder = SelectedPair() is { } pair && TranslateManager.IsInstalled(pair) ? TranslateManager.FolderOf(pair) : TranslateManager.ModelsRoot;
            if (!TranslateManager.OpenInExplorer(folder)) SetStatus(Str("Str_Translate_StatusOpenFailed"));
        }

        private void BtnManage_Click(object sender, RoutedEventArgs e)
        {
            SettingsView.RequestedSection = "Translate";
            ShortcutManager.OpenToolRequested?.Invoke(ToolCatalogManager.SettingsId);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPair() is not { } pair) return;
            if (!_manager.Remove(pair)) SetStatus(Str("Str_Translate_StatusRemoveFailed"));
            UpdateModelPanel();
        }

        // ---- Translating ----------------------------------------------------------------------

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_translateCts != null)
            {
                _translateCts.Cancel();
                return;
            }

            if (FilesMode)
            {
                await RunFilesAsync();
                return;
            }

            string text = TxtInput.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(Str("Str_Translate_StatusEmpty"));
                return;
            }
            var route = TranslateManager.Route(TagOf(CmbFrom), TagOf(CmbTo));
            if (route == null)
            {
                SetStatus(Str("Str_Translate_StatusSamePair"));
                return;
            }
            if (!route.All(TranslateManager.IsInstalled))
            {
                SetStatus(Str("Str_Translate_StatusNeedModel"));
                return;
            }

            _translateCts = new CancellationTokenSource();
            SetRunning(true);

            try
            {
                string current = text;
                bool canceled = false;
                int failed = 0, total = 0;
                for (int step = 0; step < route.Count && !canceled; step++)
                {
                    int done = step, steps = route.Count;
                    var progress = new Progress<double>(f => ShowProgress((done + f) / steps));   // the steps share the bar
                    var result = await _manager.TranslateAsync(current, route[step], progress, _translateCts.Token);
                    current = result.Text;
                    canceled = result.Canceled;
                    failed += result.FailedParts;
                    total += result.TotalParts;
                }
                TxtOutput.Text = current;
                bool indirect = TranslateManager.IsIndirect(route);
                SetStatus(canceled
                    ? Str("Str_Translate_StatusCanceled")
                    : failed > 0
                        ? string.Format(Str("Str_Translate_StatusPartial"), failed, total)
                        : indirect ? Str("Str_Translate_StatusDoneIndirect") : Str("Str_Translate_StatusDone"));
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidDataException or JsonException or InvalidOperationException)
            {
                SetStatus(Str("Str_Translate_StatusLoadFailed"));
            }
            finally
            {
                _translateCts.Dispose();
                _translateCts = null;
                SetRunning(false);
                if (_closing) _manager.Dispose();
            }
        }

        // ---- Files mode -----------------------------------------------------------------------

        private bool FilesMode => RadioModeFiles.IsChecked == true;

        private void RadioMode_Click(object sender, RoutedEventArgs e) => ApplyMode();

        private void ApplyMode()
        {
            bool files = FilesMode;
            TextPanes.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
            FilesPanel.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            InputCard.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            TxtRunLabel.Text = _translateCts != null ? Str("Str_Common_Cancel") : files ? Loc.T("Str_Translate_RunFiles", "Translate files") : Str("Str_Translate_Run");
            SetStatus(files ? Loc.T("Str_Translate_StatusFilesIdle", "Add files, then press Translate files.") : Str("Str_Translate_StatusIdle"));
            UpdateFilesState();
        }

        private void UpdateFilesState()
        {
            FilesEmptyHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputCard.SetEmpty(_files.Count == 0);   // the card keeps its "drop files here" hint on top of the list until told
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_translateCts != null) return;

            int added = 0, skipped = 0;
            foreach (string path in e.Paths)
            {
                IEnumerable<string> candidates;
                try
                {
                    candidates = Directory.Exists(path)
                        ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList()
                        : new[] { path };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in candidates)
                {
                    if (!TranslateManager.IsSupportedFile(file)) { if (!Directory.Exists(path)) skipped++; continue; }
                    if (_files.Any(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
                    _files.Add(new TranslateFileItem(file) { Status = Loc.T("Str_Translate_FileWaiting", "Waiting") });
                    added++;
                }
            }

            if (skipped > 0)
                SetStatus(Loc.N(skipped, "Str_Translate_FilesSkipped_One", "{0} file skipped: only .txt, .md, .srt, .vtt and .json files can be translated.", "Str_Translate_FilesSkipped_Many", "{0} files skipped: only .txt, .md, .srt, .vtt and .json files can be translated.", skipped));
            else if (added > 0)
                SetStatus(Loc.N(added, "Str_Translate_FilesAdded_One", "{0} file added.", "Str_Translate_FilesAdded_Many", "{0} files added.", added));
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_translateCts == null) _files.Clear();
        }

        private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (_translateCts == null && (sender as FrameworkElement)?.Tag is TranslateFileItem item) _files.Remove(item);
        }

        private void BtnShowFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TranslateFileItem { OutputPath: { } path }) return;
            if (!TranslateManager.OpenInExplorer(Path.GetDirectoryName(path) ?? "")) SetStatus(Str("Str_Translate_StatusOpenFailed"));
        }

        /// <summary>
        /// Translates every file that is not done yet, one after the other. The bar and the percentage cover the whole
        /// batch (each file counts the same); each row also shows its own progress. Cancel stops after the sentence being
        /// translated and saves nothing for that file; files finished before keep their translation.
        /// </summary>
        private async Task RunFilesAsync()
        {
            var pending = _files.Where(f => f.State != TranslateFileState.Done).ToList();
            if (_files.Count == 0) { SetStatus(Loc.T("Str_Translate_StatusNoFiles", "Add at least one file first.")); InputCard.PopDropArea(); return; }
            if (pending.Count == 0) { SetStatus(Loc.T("Str_Translate_StatusAllDone", "Every file in the list is already translated. Add more files, or remove them and add them again to translate them once more.")); return; }

            var route = TranslateManager.Route(TagOf(CmbFrom), TagOf(CmbTo));
            if (route == null) { SetStatus(Str("Str_Translate_StatusSamePair")); return; }
            if (!route.All(TranslateManager.IsInstalled)) { SetStatus(Str("Str_Translate_StatusNeedModel")); return; }

            _translateCts = new CancellationTokenSource();
            var token = _translateCts.Token;
            SetRunning(true);
            foreach (var f in pending) { f.State = TranslateFileState.Waiting; f.Percent = 0; f.OutputPath = null; f.Status = Loc.T("Str_Translate_FileWaiting", "Waiting"); }

            int saved = 0, failedFiles = 0;
            bool stopped = false;
            try
            {
                for (int k = 0; k < pending.Count && !stopped; k++)
                {
                    var item = pending[k];
                    int index = k;
                    item.State = TranslateFileState.Running;
                    item.Status = Loc.T("Str_Translate_FileRunning", "Translating...");
                    SetStatus(Loc.F("Str_Translate_StatusFiles", "Translating file {0} of {1} · {2}", k + 1, pending.Count, item.Name));
                    ShowProgress((double)k / pending.Count);

                    var progress = new Progress<double>(f =>
                    {
                        item.Percent = f * 100;
                        ShowProgress((index + f) / pending.Count);
                    });

                    TranslateFileResult result;
                    try
                    {
                        result = await _manager.TranslateFileAsync(item.Path, route, progress, token);
                    }
                    catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidDataException or JsonException or InvalidOperationException)
                    {
                        result = new TranslateFileResult(null, false, 0, 0, "Str_Translate_FileError");
                    }

                    if (result.Canceled)
                    {
                        item.State = TranslateFileState.Stopped;
                        item.Percent = 0;
                        item.Status = Loc.T("Str_Translate_FileStopped", "Stopped");
                        stopped = true;
                    }
                    else if (result.OutputPath is { } output)
                    {
                        item.State = TranslateFileState.Done;
                        item.Percent = 100;
                        item.OutputPath = output;
                        item.Status = result.FailedParts > 0
                            ? Loc.F("Str_Translate_FileSavedPartial", "Saved as {0} · {1} of {2} sentences were left as they were", Path.GetFileName(output), result.FailedParts, result.TotalParts)
                            : Loc.F("Str_Translate_FileSaved", "Saved as {0}", Path.GetFileName(output));
                        saved++;
                    }
                    else
                    {
                        item.State = TranslateFileState.Failed;
                        item.Percent = 0;
                        item.Status = Loc.T(result.ErrorKey ?? "Str_Translate_FileError", "Couldn't translate this file");
                        failedFiles++;
                    }
                }

                foreach (var f in pending.Where(f => f.State == TranslateFileState.Waiting))
                {
                    f.State = TranslateFileState.Stopped;
                    f.Status = Loc.T("Str_Translate_FileStopped", "Stopped");
                }

                SetStatus(stopped
                    ? Loc.N(saved, "Str_Translate_StatusFilesStopped_One", "Stopped: {0} file was saved before stopping.", "Str_Translate_StatusFilesStopped_Many", "Stopped: {0} files were saved before stopping.", saved)
                    : failedFiles > 0
                        ? Loc.F("Str_Translate_StatusFilesPartial", "Done: {0} saved next to the originals, {1} could not be translated.", saved, failedFiles)
                        : Loc.N(saved, "Str_Translate_StatusFilesDone_One", "Done: {0} file saved next to the original.", "Str_Translate_StatusFilesDone_Many", "Done: {0} files saved next to the originals.", saved));
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidDataException or JsonException or InvalidOperationException)
            {
                SetStatus(Str("Str_Translate_StatusLoadFailed"));
            }
            finally
            {
                _translateCts.Dispose();
                _translateCts = null;
                SetRunning(false);
                if (_closing) _manager.Dispose();
            }
        }

        private void SetRunning(bool running)
        {
            IconRun.Kind = running ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.Translate;
            TxtRunLabel.Text = running ? Str("Str_Common_Cancel") : FilesMode ? Loc.T("Str_Translate_RunFiles", "Translate files") : Str("Str_Translate_Run");
            ProgressTranslate.Value = 0;
            ProgressTranslate.Visibility = TxtPercent.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            TxtPercent.Text = "0%";
            TxtInput.IsReadOnly = running;
            BtnClear.IsEnabled = !running;
            InputCard.IsLocked = running;
            RadioModeText.IsEnabled = RadioModeFiles.IsEnabled = !running;
            if (running && !FilesMode) SetStatus(Str("Str_Translate_StatusTranslating"));
            UpdateModelPanel();
        }

        private void ShowProgress(double fraction)
        {
            ProgressTranslate.Value = fraction * 100;
            TxtPercent.Text = $"{(int)(fraction * 100)}%";
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtInput.Clear();
            TxtOutput.Clear();
            SetStatus(Str("Str_Translate_StatusIdle"));
            TxtInput.Focus();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtOutput.Text)) return;
            try
            {
                Clipboard.SetText(TxtOutput.Text);
                SetStatus(Str("Str_Translate_StatusCopied"));
            }
            catch (COMException)
            {
                SetStatus(Str("Str_Translate_StatusCopyFailed"));   // another app holds the clipboard
            }
        }

        // ---- Helpers --------------------------------------------------------------------------

        private void SetStatus(string text) => TxtStatus.Text = text;

        private string Str(string key) => TryFindResource(key) as string ?? key;

        private static string FormatSize(long bytes) => $"{bytes / 1048576.0:N0} MB";

        private string LanguageName(string code) => TryFindResource($"Str_Translate_Lang_{code}") as string ?? code.ToUpperInvariant();
    }
}
