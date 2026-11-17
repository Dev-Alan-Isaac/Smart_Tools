using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// JSON / YAML / XML Formatter, the Files card: files and folders to open one by one, or to run with the current options
    /// in one go. Results are written next to each file or into the folder chosen on the card, under a name that does not
    /// exist yet (a file is never replaced).
    /// </summary>
    public partial class DataFormatterView
    {
        private static readonly string[] Extensions = { ".json", ".yaml", ".yml", ".xml" };
        private const int MaxFiles = 2000;

        private readonly ObservableCollection<BatchSource> _sources = new();
        private CancellationTokenSource? _batchCts;
        private int _syncRun;

        private bool BatchBusy => _batchCts != null;

        private static bool Accepts(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private void InitFiles()
        {
            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.T("Str_Formatter_FilterFiles", "JSON, YAML, XML") + "|*.json;*.yaml;*.yml;*.xml|" + Loc.T("Str_Formatter_FilterAll", "All files") + "|*.*";
            _sources.CollectionChanged += (_, _) =>
            {
                InputCard.SetEmpty(_sources.Count == 0);
                PanelBatch.Visibility = _sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        private void MarkCurrent(string? path)
        {
            foreach (var s in _sources) s.IsCurrent = path != null && !s.IsFolder && string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        //  Files in
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddSources(e.Paths, openFirst: false);

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => _ = CountFoldersAsync();

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (BatchBusy) return;
            _syncRun++;
            _sources.Clear();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (BatchBusy || (sender as FrameworkElement)?.Tag is not BatchSource source) return;
            _sources.Remove(source);
            e.Handled = true;
        }

        private void SourceRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (BatchBusy || (sender as FrameworkElement)?.Tag is not BatchSource { IsFolder: false } source) return;
            LoadFile(source.FullPath);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !BatchBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (!BatchBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddSources(files, openFirst: true);
        }

        /// <summary>Adds the files and folders to the card. A single dropped file opens in the editor, as it did before the card existed.</summary>
        private void AddSources(IEnumerable<string> paths, bool openFirst)
        {
            if (BatchBusy) return;
            var list = paths.ToList();
            BatchSource? first = null;
            foreach (string path in list)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && Accepts(path))) continue;
                var existing = _sources.FirstOrDefault(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { first ??= existing; continue; }
                var source = new BatchSource { FullPath = path, IsFolder = isFolder };
                _sources.Add(source);
                first ??= source;
            }
            _ = CountFoldersAsync();
            if (openFirst && list.Count == 1 && first is { IsFolder: false } one) LoadFile(one.FullPath);
        }

        private async Task CountFoldersAsync()
        {
            int run = ++_syncRun;
            var folders = _sources.Where(s => s.IsFolder).ToList();
            bool include = InputCard.IncludeSubfolders;
            var counts = await Task.Run(() => folders.ToDictionary(f => f, f => BatchFiles.Find(new[] { f.FullPath }, include, Accepts, MaxFiles).Files.Count));
            if (run != _syncRun) return;
            foreach (var (folder, count) in counts) folder.Count = count;
        }

        // =================================================================
        //  Run on every file
        // =================================================================

        private async void BtnRunAll_Click(object sender, RoutedEventArgs e)
        {
            if (BatchBusy) { _batchCts?.Cancel(); return; }
            if (_sources.Count == 0) return;
            if (OutputCard.Status.IsError)
            {
                ThemedMessageBox.Show(Loc.F("Str_Formatter_CantSaveThere", "Can't save there: {0}", OutputCard.Status.Text), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { OutputCard.EnsureFolder(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ThemedMessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, _) = await Task.Run(() => BatchFiles.Find(paths, include, Accepts, MaxFiles));
            if (files.Count == 0)
            {
                SetBatchResult("Info", Loc.T("Str_Formatter_NoFilesFound", "No JSON, YAML or XML files were found."), "");
                return;
            }

            var (from, to, action, indent) = (_from, _to, _action, _indent);
            string ext = to switch { DataFormatKind.Yaml => ".yaml", DataFormatKind.Xml => ".xml", _ => ".json" };
            var cts = _batchCts = new CancellationTokenSource();
            SetBatchUi(true);
            int done = 0, failed = 0;
            var problems = new List<string>();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string file = files[i];
                    TxtResult.Text = Loc.F("Str_Formatter_BatchProgress", "File {0} of {1}", i + 1, files.Count);
                    TxtResultDetail.Text = Path.GetFileName(file);
                    var (ok, message) = await Task.Run(() => ProcessFile(file, from, to, action, indent, ext), cts.Token);
                    if (ok) done++;
                    else { failed++; if (problems.Count < 5) problems.Add($"{Path.GetFileName(file)}: {message}"); }
                }
                string summary = failed == 0
                    ? Loc.N(done, "Str_Formatter_BatchDoneOne", "{0} file done", "Str_Formatter_BatchDoneMany", "{0} files done", done)
                    : Loc.F("Str_Formatter_BatchSome", "{0} done, {1} could not be read", done, failed);
                SetBatchResult(failed == 0 ? "Success" : "Warning", summary, string.Join("  ·  ", problems));
            }
            catch (OperationCanceledException)
            {
                SetBatchResult("Info", Loc.F("Str_Formatter_BatchStopped", "Stopped after {0} file(s)", done), Loc.T("Str_Formatter_BatchStoppedDetail", "Nothing already written was removed."));
            }
            finally
            {
                _batchCts = null;
                cts.Dispose();
                SetBatchUi(false);
                OutputCard.Refresh();
            }
        }

        /// <summary>Reads one file, runs the options on it and writes the result under a new name. Off the UI thread.</summary>
        private (bool Ok, string Message) ProcessFile(string file, DataFormatKind? from, DataFormatKind to, DataFormatterAction action, DataFormatterIndent indent, string ext)
        {
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) return (false, T("Str_Formatter_TooBig", "That file is larger than 20 MB, which is too big to format here."));
                string text = File.ReadAllText(file);
                var kind = from ?? DataFormatterManager.Detect(text);
                var result = DataFormatterManager.Run(text, kind, to, action, indent);
                if (!result.Ok)
                {
                    string where = result.Line > 0 ? string.Format(T("Str_Formatter_At", "Line {0}, column {1}: "), result.Line, Math.Max(result.Column, 1)) : "";
                    return (false, where + result.Message);
                }
                string stem = Path.GetFileNameWithoutExtension(file);
                bool sameExt = string.Equals(Path.GetExtension(file), ext, StringComparison.OrdinalIgnoreCase);
                string target = Dispatcher.Invoke(() => OutputCard.ResultPathFor(file, stem + (sameExt ? ".formatted" : "") + ext));
                AtomicFile.WriteAllText(target, result.Text);
                return (true, "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return (false, ex.Message);
            }
        }

        private void SetBatchResult(string kind, string title, string detail)
        {
            TxtResult.Text = title;
            TxtResultDetail.Text = detail;
            TxtResultDetail.ToolTip = detail.Length > 0 ? detail : null;
            TxtSize.Text = "";
            SetPill(kind, kind switch { "Success" => MaterialDesignThemes.Wpf.PackIconKind.CheckCircleOutline, "Warning" => MaterialDesignThemes.Wpf.PackIconKind.AlertOutline, _ => MaterialDesignThemes.Wpf.PackIconKind.InformationOutline });
        }

        private void SetBatchUi(bool busy)
        {
            TxtRunAll.Text = busy ? Loc.T("Str_Common_Cancel", "Cancel") : Loc.T("Str_Formatter_RunAll", "Run on all files");
            IconRunAll.Kind = busy ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.PlaylistPlay;
            InputCard.IsLocked = busy;
            OutputCard.IsLocked = busy;
        }
    }
}
