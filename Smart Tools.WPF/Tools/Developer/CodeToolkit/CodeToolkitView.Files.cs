using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Code Toolkit, the Files card (Minify and Beautify): files and folders to open in Input one by one, or to run with the
    /// current mode and language options in one go. The language of each file comes from its extension; files of other kinds
    /// are left out. Results are written next to each file or into the folder chosen on the card, under a name that does not
    /// exist yet (a file is never replaced).
    /// </summary>
    public partial class CodeToolkitView
    {
        private const int MaxFiles = 2000;

        private readonly ObservableCollection<BatchSource> _sources = new();
        private CancellationTokenSource? _batchCts;
        private int _syncRun;
        private bool _filesInitialised;

        private bool BatchBusy => _batchCts != null;

        private static bool Accepts(string path) => CodeToolkitManager.LanguageFromExtension(Path.GetExtension(path)) != null;

        private void EnsureFilesCard()
        {
            if (_filesInitialised) return;
            _filesInitialised = true;
            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => CodeToolkitManager.OpenFileFilter;
            _sources.CollectionChanged += (_, _) =>
            {
                InputCard.SetEmpty(_sources.Count == 0);
                PanelBatch.Visibility = _sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        /// <summary>True when a drag is over the Files card, which handles its own drops (the page would otherwise take them).</summary>
        private bool IsOverFilesCard(object? source)
        {
            for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, PanelFiles)) return PanelFiles.Visibility == Visibility.Visible;
            return false;
        }

        // =================================================================
        //  Files in
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            EnsureFilesCard();
            if (BatchBusy) return;
            foreach (string path in e.Paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && Accepts(path))) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new BatchSource { FullPath = path, IsFolder = isFolder });
            }
            _ = CountFoldersAsync();
        }

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

        private async void SourceRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (BatchBusy || (sender as FrameworkElement)?.Tag is not BatchSource { IsFolder: false } source) return;
            foreach (var s in _sources) s.IsCurrent = ReferenceEquals(s, source);
            await LoadFileIntoAsync(source.FullPath, TxtInput);
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
            if (_sources.Count == 0 || _mode == CodeToolMode.Diff) return;
            if (OutputCard.Status.IsError)
            {
                SetStatus(Loc.F("Str_CodeToolkit_CantSaveThere", "Can't save there: {0}", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            try { OutputCard.EnsureFolder(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SetStatus(ex.Message, StatusKind.Error);
                return;
            }

            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, _) = await Task.Run(() => BatchFiles.Find(paths, include, Accepts, MaxFiles));
            if (files.Count == 0)
            {
                SetStatus(Loc.T("Str_CodeToolkit_NoFilesFound", "No CSS, JavaScript, HTML or JSON files were found."), StatusKind.Neutral);
                return;
            }

            var mode = _mode;
            var indent = _indent;
            var cts = _batchCts = new CancellationTokenSource();
            SetBatchUi(true);
            int done = 0, failed = 0;
            string? firstProblem = null;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string file = files[i];
                    SetStatus(Loc.F("Str_CodeToolkit_BatchProgress", "File {0} of {1}: {2}", i + 1, files.Count, Path.GetFileName(file)), StatusKind.Busy);
                    try
                    {
                        var language = CodeToolkitManager.LanguageFromExtension(Path.GetExtension(file)) ?? CodeLanguage.Json;
                        string text = await CodeToolkitManager.ReadFileAsync(file, cts.Token);
                        var result = await _manager.RunTransformAsync(text, mode, language, indent, cts.Token);
                        string stem = Path.GetFileNameWithoutExtension(file);
                        string suffix = mode == CodeToolMode.Minify ? ".min" : ".formatted";
                        string target = OutputCard.ResultPathFor(file, stem + suffix + Path.GetExtension(file));
                        await CodeToolkitManager.SaveFileAsync(target, result.Output, cts.Token);
                        done++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is CodeFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        failed++;
                        firstProblem ??= $"{Path.GetFileName(file)}: {FirstLine(ex.Message)}";
                    }
                }
                SetStatus(failed == 0
                    ? Loc.N(done, "Str_CodeToolkit_BatchDoneOne", "{0} file done", "Str_CodeToolkit_BatchDoneMany", "{0} files done", done)
                    : Loc.F("Str_CodeToolkit_BatchSome", "{0} done, {1} could not be processed ({2})", done, failed, firstProblem ?? ""),
                    failed == 0 ? StatusKind.Success : StatusKind.Error);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc.F("Str_CodeToolkit_BatchStopped", "Stopped after {0} file(s). Nothing already written was removed.", done), StatusKind.Neutral);
            }
            finally
            {
                _batchCts = null;
                cts.Dispose();
                SetBatchUi(false);
                OutputCard.Refresh();
            }
        }

        private void SetBatchUi(bool busy)
        {
            TxtRunAll.Text = busy ? Loc.T("Str_Common_Cancel", "Cancel") : Loc.T("Str_CodeToolkit_RunAll", "Run on all files");
            IconRunAll.Kind = busy ? MaterialDesignThemes.Wpf.PackIconKind.Stop : MaterialDesignThemes.Wpf.PackIconKind.PlaylistPlay;
            InputCard.IsLocked = busy;
            OutputCard.IsLocked = busy;
        }
    }
}
