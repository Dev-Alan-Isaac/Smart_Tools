using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Metadata Extractor - UI only. File types, the list, the run, ffprobe,
    /// wording, export and settings are in <see cref="MetadataExtractManager"/>;
    /// images are read by <see cref="MagickImageMetadataReader"/>. Files are
    /// only read. Folders picked here are kept for this session only.
    /// </summary>
    public partial class MetadataExtractView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_metadata-extract_Name", "Metadata Extractor");

        private readonly BulkObservableCollection<MetadataFileItem> _files = new();
        private readonly ObservableCollection<MetadataQueueGroup> _groups = new();   // the sidebar queue
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _runCts;
        private string? _lastExportFolder;         // this session only
        private bool _isRunning;
        private int _adding;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public MetadataExtractView()
        {
            InitializeComponent();
            MetadataExtractManager.ImageReader ??= new MagickImageMetadataReader();

            DataGridFiles.ItemsSource = _files;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = MetadataExtractManager.FileDialogFilter;
            _files.CollectionChanged += (_, _) => UpdateListState();
            _groups.CollectionChanged += (_, _) => UpdateListState();
            _viewConstructed = true;
            UpdateListState();
        }

        /// <summary>Called from MainWindow.Window_Closing. Reading never changes a file, so stopping is always safe.</summary>
        public void CancelActiveRun()
        {
            try
            {
                _runCts?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;
        private bool IsBusy => _isRunning || _adding > 0;

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(MetadataExtractManager.LoadSettings());
            }
            ApplyResponsiveLayout(ActualWidth);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

        /// <summary>The list has the whole page: the less important columns appear as the window grows.</summary>
        private void ApplyResponsiveLayout(double width)
        {
            if (width <= 0) return;
            ColSummary.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible;
            ColSize.Visibility = width < 850 ? Visibility.Collapsed : Visibility.Visible;
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
        //  Settings (no paths)
        // =================================================================

        private void ApplySettings(MetadataSettings s)
        {
            _applyingSettings = true;
            try
            {
                ChkIncludeVideo.IsChecked = s.IncludeVideo;
                ChkIncludeAudio.IsChecked = s.IncludeAudio;
                ChkIncludeImages.IsChecked = s.IncludeImages;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                _exportFormat = s.ExportFormat;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateHints();
        }

        private MetadataSettings ReadSettings() => new()
        {
            IncludeVideo = ChkIncludeVideo.IsChecked == true,
            IncludeAudio = ChkIncludeAudio.IsChecked == true,
            IncludeImages = ChkIncludeImages.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            ExportFormat = SelectedExportFormat,
        };

        // The last format exported (remembered in the settings and ticked in the Export menu).
        private MetadataExportFormat _exportFormat = MetadataExportFormat.Json;

        private MetadataExportFormat SelectedExportFormat => _exportFormat;

        private MetadataExtractOptions ReadOptions() => new()
        {
            IncludeVideo = ChkIncludeVideo.IsChecked == true,
            IncludeAudio = ChkIncludeAudio.IsChecked == true,
            IncludeImages = ChkIncludeImages.IsChecked == true,
        };

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            // Keep at least one kind on - with none, Extract would only skip.
            if (ChkIncludeVideo.IsChecked != true && ChkIncludeAudio.IsChecked != true && ChkIncludeImages.IsChecked != true && sender is System.Windows.Controls.CheckBox box)
            {
                box.IsChecked = true;
                TxtProgress.Text = "Keep at least one kind of file switched on.";
            }
            UpdateHints();
            MetadataExtractManager.SaveSettings(ReadSettings());
        }

        /// <summary>Applies to folders added from now on - each queue entry shows how it was added.</summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            MetadataExtractManager.SaveSettings(ReadSettings());
        }

        private void UpdateHints()
        {
            MenuExportJson.ToolTip = MetadataExtractManager.ExportHint(MetadataExportFormat.Json);
            MenuExportCsv.ToolTip = MetadataExtractManager.ExportHint(MetadataExportFormat.Csv);
            MenuExportXml.ToolTip = MetadataExtractManager.ExportHint(MetadataExportFormat.Xml);
        }

        // =================================================================
        //  Adding files
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
            e.Handled = true;
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) _ = AddPathsAsync(paths);
        }

        /// <summary>Adds files and folders; scanning, sizes and dates are read off the UI thread (the old view did it all on it).</summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isRunning) return;
            var token = _lifetimeCts.Token;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateListState();
            TxtProgress.Text = "Looking for video, audio and images…";
            try
            {
                int added = 0, inaccessible = 0;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var (found, unreadable) = await MetadataExtractManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += unreadable;
                    added += MetadataExtractManager.AddFolderToQueue(_files, _known, _groups, folder, subfolders, await MetadataExtractManager.CreateItemsAsync(found, token));
                }
                var loose = paths.Where(p => !Directory.Exists(p)).ToList();
                int skipped = 0;
                if (loose.Count > 0)
                {
                    int n = MetadataExtractManager.AddFilesToQueue(_files, _known, _groups, await MetadataExtractManager.CreateItemsAsync(loose, token));
                    skipped = loose.Count - n;
                    added += n;
                }

                if (added > 0)
                {
                    InputCard.PopDropArea();
                    UiMotion.Pop(SummaryPill, 0.9);
                }
                var parts = new List<string>
                {
                    added == 0 ? "Nothing new added - the files may already be in the list." : $"Added {added:N0} {MetadataText.Plural(added, "file", "files")}. Click Extract to read them.",
                };
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {MetadataText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (skipped > 0) parts.Add($"{skipped:N0} skipped (not video/audio/image, or already listed).");
                if (!_isRunning) TxtProgress.Text = string.Join(" ", parts);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            finally
            {
                _adding--;
                UpdateListState();
            }
        }

        // =================================================================
        //  The list
        // =================================================================

        private void UpdateListState()
        {
            int count = _files.Count;
            // Nothing added yet: one big empty card. Once there are files, the list takes its place.
            PageEmptyCard.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilesCard.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SummaryPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = $"{count:N0} {MetadataText.Plural(count, "file", "files")}";

            int video = 0, audio = 0, image = 0, done = 0, failed = 0, located = 0;
            foreach (var f in _files)
            {
                switch (f.Kind)
                {
                    case MetadataKind.Video: video++; break;
                    case MetadataKind.Audio: audio++; break;
                    default: image++; break;
                }
                if (f.Status == MetadataStatus.Done) done++;
                else if (f.Status == MetadataStatus.Failed) failed++;
                if (f.HasLocation) located++;
            }
            TxtVideoCount.Text = video > 0 ? $"({video:N0})" : "";
            TxtAudioCount.Text = audio > 0 ? $"({audio:N0})" : "";
            TxtImageCount.Text = image > 0 ? $"({image:N0})" : "";

            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtDoneCount.Text = $"{done:N0} read";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtFailedCount.Text = $"{failed:N0} failed";
            PillLocation.Visibility = located > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtLocationCount.Text = $"{located:N0} with GPS location";

            BtnExport.IsEnabled = !IsBusy && done > 0;
            BtnClearAll.IsEnabled = !IsBusy && count > 0;
            BtnRemoveSelected.IsEnabled = !IsBusy && DataGridFiles.SelectedItems.Count > 0;
            InputCard.SetEmpty(_groups.Count == 0);
        }

        private void DataGridFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnRemoveSelected.IsEnabled = !IsBusy && DataGridFiles.SelectedItems.Count > 0;
        }

        /// <summary>Double-click on a row opens that file's details.</summary>
        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(DataGridFiles, source) is DataGridRow { Item: MetadataFileItem item })
            {
                OpenDetails(item);
                e.Handled = true;
            }
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !IsBusy && DataGridFiles.SelectedItems.Count > 0)
            {
                RemoveItems(DataGridFiles.SelectedItems.Cast<MetadataFileItem>().ToList());
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && DataGridFiles.SelectedItem is MetadataFileItem item)
            {
                OpenDetails(item);
                e.Handled = true;
            }
        }

        private void BtnRowDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: MetadataFileItem item }) OpenDetails(item);
        }

        private void BtnRemoveRow_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy && sender is Button { Tag: MetadataFileItem item }) RemoveItems(new[] { item });
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy) RemoveItems(DataGridFiles.SelectedItems.Cast<MetadataFileItem>().ToList());
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearAll();

        /// <summary>The sidebar's clear button - same as Clear all on the list.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isRunning) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearAll();

        private void ClearAll()
        {
            if (IsBusy || _files.Count == 0) return;
            int n = _files.Count;
            MetadataExtractManager.ClearQueue(_files, _known, _groups);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Cleared {n:N0} {MetadataText.Plural(n, "file", "files")} from the list. The files themselves are untouched.";
        }

        /// <summary>The × on a sidebar entry: takes that folder (or those files) and their rows off the list.</summary>
        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not Button { Tag: MetadataQueueGroup group }) return;
            int n = MetadataExtractManager.RemoveGroup(_files, _known, _groups, group);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Removed {group.Name} ({n:N0} {MetadataText.Plural(n, "file", "files")}) from the list. The files themselves are untouched.";
        }

        private void RemoveItems(IReadOnlyCollection<MetadataFileItem> remove)
        {
            if (remove.Count == 0) return;
            var gone = remove.ToHashSet();
            MetadataExtractManager.RemoveFromQueue(_files, _known, _groups, gone);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Removed {gone.Count:N0} from the list. The files themselves are untouched.";
        }

        // =================================================================
        //  Details window
        // =================================================================

        /// <summary>
        /// Opens the details window on this file. Previous / next walk the list in the order the grid shows it,
        /// and the grid selects the file the window ended on.
        /// </summary>
        private void OpenDetails(MetadataFileItem item)
        {
            var order = DataGridFiles.Items.OfType<MetadataFileItem>().ToList();
            int index = order.IndexOf(item);
            if (index < 0) return;

            var owner = Window.GetWindow(this);
            double height = Math.Clamp((owner?.ActualHeight ?? 800) - 170, 520, 800);
            MetadataDetailsView? view = null;
            bool shown = AppModal.Present(owner, close =>
            {
                view = new MetadataDetailsView(order, index, close) { Height = height };
                return (view, view);
            }, 760, 1240);

            if (!shown)
            {
                TxtProgress.Text = Loc.T("Str_MetadataDetails_CantShow", "The details window couldn't be shown.");
                return;
            }
            if (view?.Current is { } last && _files.Contains(last))
            {
                DataGridFiles.SelectedItem = last;
                DataGridFiles.ScrollIntoView(last);
            }
        }

        // =================================================================
        //  Extract / Stop
        // =================================================================

        private async void BtnExtract_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                BtnExtract.IsEnabled = false;   // back on when the run unwinds
                TxtProgress.Text = "Stopping…";
                return;
            }
            if (_files.Count == 0)
            {
                InputCard.Browse();
                return;
            }

            var options = ReadOptions();
            var todo = MetadataExtractManager.SelectForRun(_files, options);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _runCts = cts;
            SetRunning(true);

            var progress = new Progress<MetadataProgress>(p =>
            {
                ProgressExtract.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                if (!cts.IsCancellationRequested) TxtProgress.Text = p.Total == 0 ? "Starting…" : $"{p.Done:N0} of {p.Total:N0} · {p.CurrentFile}";
                UpdateListState();
            });

            try
            {
                var (done, failed, skipped, cancelled) = await MetadataExtractManager.ExtractAsync(todo, options, progress, cts.Token);
                TxtProgress.Text = MetadataExtractManager.DescribeRun(done, failed, skipped, cancelled);
                if (!cancelled)
                {
                    ProgressExtract.Value = 100;
                    TxtProgressPercent.Text = "100%";
                    Toast("Metadata read", $"{done:N0} {MetadataText.Plural(done, "file", "files")}{(failed > 0 ? $", {failed:N0} failed" : "")}.");
                }
            }
            finally
            {
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
                cts.Dispose();
                SetRunning(false);
            }
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("metadata-extract");
            else { _activity?.Dispose(); _activity = null; }
            BtnExtract.IsEnabled = true;
            IconExtract.Kind = running ? PackIconKind.Stop : PackIconKind.TagSearchOutline;
            if (running)
            {
                TxtExtractLabel.Text = "Stop";
                BtnExtract.ToolTip = "Stop - what's read so far is kept";
                ProgressExtract.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            else
            {
                TxtExtractLabel.SetResourceReference(TextBlock.TextProperty, "Str_Metadata_ExtractButton");
                BtnExtract.SetResourceReference(ToolTipProperty, "Str_Metadata_ExtractButtonTooltip");
            }
            OptionsScrollViewer.IsEnabled = !running;   // the run uses the kinds chosen when it started
            UpdateListState();
        }

        // =================================================================
        //  Export
        // =================================================================

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            MenuExportJson.IsChecked = _exportFormat == MetadataExportFormat.Json;
            MenuExportCsv.IsChecked = _exportFormat == MetadataExportFormat.Csv;
            MenuExportXml.IsChecked = _exportFormat == MetadataExportFormat.Xml;
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out MetadataExportFormat format)) return;
            var rows = MetadataExtractManager.Snapshot(_files);
            if (rows.Count == 0 || IsBusy)
            {
                TxtProgress.Text = "Extract first - there's nothing to export yet.";
                return;
            }
            _exportFormat = format;
            MetadataExtractManager.SaveSettings(ReadSettings());
            string ext = MetadataExtractManager.ExtensionFor(format);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export metadata",
                Filter = MetadataExtractManager.ExportFilter(format),
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"Metadata {DateTime.Now:yyyy-MM-dd}{ext}",
            };
            string folder = _lastExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog() != true) return;
            _lastExportFolder = Path.GetDirectoryName(dialog.FileName);

            _adding++;
            UpdateListState();
            TxtProgress.Text = $"Exporting {rows.Count:N0} {MetadataText.Plural(rows.Count, "file", "files")}…";
            try
            {
                int n = await MetadataExtractManager.ExportAsync(rows, dialog.FileName, format, _lifetimeCts.Token);
                TxtProgress.Text = $"Exported {n:N0} {MetadataText.Plural(n, "file", "files")} to {Path.GetFileName(dialog.FileName)}.";
                Toast("Export complete", $"{n:N0} {MetadataText.Plural(n, "file", "files")} · {Path.GetFileName(dialog.FileName)}");
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Loc.T("Str_MB_TheExportCouldnT", "The export couldn't be saved.\n\n") + MetadataExtractManager.PlainExportError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtProgress.Text = "Export failed: " + MetadataExtractManager.PlainExportError(ex);
            }
            finally
            {
                _adding--;
                UpdateListState();
            }
        }

        private static void Toast(string title, string message) => Smart_Tools.WPF.Controllers.ToastNotificationManager.ShowToast(title, message);
    }
}
