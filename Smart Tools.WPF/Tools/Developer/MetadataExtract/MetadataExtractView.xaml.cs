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
using MessageBox = System.Windows.MessageBox;
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
        private const double StackDetailsBelowWidth = 1250;
        private const string DialogTitle = "Metadata Extractor";

        private readonly BulkObservableCollection<MetadataFileItem> _files = new();
        private readonly ObservableCollection<MetadataQueueGroup> _groups = new();   // the sidebar queue
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _runCts;
        private MetadataFileItem? _shown;          // the row in the details panel
        private ListCollectionView? _detailsView;
        private string? _lastExportFolder;         // this session only
        private bool _isRunning;
        private int _adding;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _detailsBelow;

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
            ShowDetails(null);
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

        /// <summary>Details beside the list on wide windows, under it on narrow ones; less important columns go first.</summary>
        private void ApplyResponsiveLayout(double width)
        {
            if (width <= 0) return;
            bool below = width < StackDetailsBelowWidth;
            if (below != _detailsBelow)
            {
                _detailsBelow = below;
                Grid.SetColumn(DetailsCard, below ? 0 : 2);
                Grid.SetRow(DetailsCard, below ? 2 : 0);
                DetailsColumn.Width = new GridLength(below ? 0 : 430);
                DetailsGapColumn.Width = new GridLength(below ? 0 : 14);
                DetailsGapRow.Height = new GridLength(below ? 14 : 0);
                DetailsRow.Height = below ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
            ColSummary.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible;
            ColSize.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
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
            EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            ShowDetails(DataGridFiles.SelectedItem as MetadataFileItem);
        }

        /// <summary>Double-click opens the file in its usual program.</summary>
        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridFiles.SelectedItem is not MetadataFileItem item) return;
            if (!File.Exists(item.FullPath))
            {
                TxtProgress.Text = $"{item.Name} is no longer there.";
                return;
            }
            // Programs and scripts are shown in Explorer instead of being run.
            if (!Smart_Tools.WPF.Controllers.ShellOpen.OpenFile(item.FullPath))
                TxtProgress.Text = $"Couldn't open {item.Name}.";
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !IsBusy && DataGridFiles.SelectedItems.Count > 0)
            {
                RemoveItems(DataGridFiles.SelectedItems.Cast<MetadataFileItem>().ToList());
                e.Handled = true;
            }
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
            ShowDetails(null);
            MetadataExtractManager.ClearQueue(_files, _known, _groups);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Cleared {n:N0} {MetadataText.Plural(n, "file", "files")} from the list. The files themselves are untouched.";
        }

        /// <summary>The × on a sidebar entry: takes that folder (or those files) and their rows off the list.</summary>
        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not Button { Tag: MetadataQueueGroup group }) return;
            if (_shown?.Group == group) ShowDetails(null);
            int n = MetadataExtractManager.RemoveGroup(_files, _known, _groups, group);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Removed {group.Name} ({n:N0} {MetadataText.Plural(n, "file", "files")}) from the list. The files themselves are untouched.";
        }

        private void RemoveItems(IReadOnlyCollection<MetadataFileItem> remove)
        {
            if (remove.Count == 0) return;
            var gone = remove.ToHashSet();
            if (_shown != null && gone.Contains(_shown)) ShowDetails(null);
            MetadataExtractManager.RemoveFromQueue(_files, _known, _groups, gone);
            ProgressExtract.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = $"Removed {gone.Count:N0} from the list. The files themselves are untouched.";
        }

        // =================================================================
        //  Details panel
        // =================================================================

        private void ShowDetails(MetadataFileItem? item)
        {
            if (_shown != null) _shown.PropertyChanged -= Shown_PropertyChanged;
            _shown = item;
            if (_shown != null) _shown.PropertyChanged += Shown_PropertyChanged;
            RenderDetails();
        }

        /// <summary>The panel follows the row live - the old one stayed empty until you clicked the row again.</summary>
        private void Shown_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MetadataFileItem.Fields) or nameof(MetadataFileItem.Status) or nameof(MetadataFileItem.Message) or nameof(MetadataFileItem.HasLocation))
                RenderDetails();
        }

        private void RenderDetails()
        {
            var item = _shown;
            BtnShowInFolder.IsEnabled = item != null;
            BtnOpenMap.Visibility = item?.HasLocation == true ? Visibility.Visible : Visibility.Collapsed;

            if (item == null)
            {
                TxtDetailsHeader.SetResourceReference(TextBlock.TextProperty, "Str_Metadata_DetailsHeader");
                TxtDetailsSub.Text = "";
                DetailsKindIcon.Kind = PackIconKind.TagMultiple;
                SetDetailsRows(null);
                DetailsErrorBanner.Visibility = Visibility.Collapsed;
                TxtDetailsEmpty.SetResourceReference(TextBlock.TextProperty, "Str_Metadata_DetailsEmptyHint");
                return;
            }

            TxtDetailsHeader.Text = item.Name;
            TxtDetailsSub.Text = item.Summary.Length > 0 ? item.Summary : $"{item.Kind} · {item.Format} · {item.SizeText}";
            DetailsKindIcon.Kind = item.Kind switch
            {
                MetadataKind.Audio => PackIconKind.MusicNote,
                MetadataKind.Image => PackIconKind.ImageOutline,
                _ => PackIconKind.FilmstripBox,
            };

            bool failed = item.Status == MetadataStatus.Failed;
            DetailsErrorBanner.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            TxtDetailsError.Text = failed ? item.Message : "";

            SetDetailsRows(item.Fields);
            if (item.Fields is not { Count: > 0 })
            {
                TxtDetailsEmpty.Text = item.Status switch
                {
                    MetadataStatus.Working => "Reading…",
                    MetadataStatus.Skipped => item.Message + ". Switch it on in the sidebar and click Extract.",
                    MetadataStatus.Failed => "Nothing could be read from this file.",
                    _ => "Not read yet - click Extract.",
                };
            }
        }

        private void SetDetailsRows(IReadOnlyList<MetadataField>? fields)
        {
            bool any = fields is { Count: > 0 };
            if (any)
            {
                _detailsView = new ListCollectionView(fields!.ToList());
                _detailsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MetadataField.Group)));
                _detailsView.Filter = DetailsFilter;
                DataGridDetails.ItemsSource = _detailsView;
                UiMotion.FadeIn(DataGridDetails, 4);
            }
            else
            {
                _detailsView = null;
                DataGridDetails.ItemsSource = null;
            }
            DataGridDetails.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            DetailsFilterBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            DetailsEmptyStatePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            BtnCopyDetails.IsEnabled = any;
        }

        private bool DetailsFilter(object o)
        {
            string term = TxtDetailsFilter.Text.Trim();
            return term.Length == 0 || o is MetadataField f &&
                   (f.Property.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    f.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    f.Group.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        private void TxtDetailsFilter_TextChanged(object sender, TextChangedEventArgs e) => _detailsView?.Refresh();

        private void BtnCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_shown == null || _detailsView == null) return;
            var sb = new StringBuilder().AppendLine(_shown.FullPath);
            foreach (var group in _detailsView.Cast<MetadataField>().GroupBy(f => f.Group))
            {
                sb.AppendLine().AppendLine(group.Key);
                foreach (var f in group) sb.Append("  ").Append(f.Property).Append(": ").AppendLine(f.Value);
            }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetText(sb.ToString());
                    TxtProgress.Text = $"Copied the details of {_shown.Name}.";
                    UiMotion.Pop(BtnCopyDetails, 0.85);
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(60);   // another program briefly holds the clipboard
                }
            }
            TxtProgress.Text = "Another program is using the clipboard - try again.";
        }

        private void BtnOpenMap_Click(object sender, RoutedEventArgs e)
        {
            if (_shown is not { Latitude: { } lat, Longitude: { } lon }) return;
            if (!Smart_Tools.WPF.Controllers.ShellOpen.OpenUrl(MetadataExtractManager.MapUrl(lat, lon)))
                TxtProgress.Text = "Couldn't open the browser.";
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            try
            {
                if (File.Exists(_shown.FullPath)) Process.Start("explorer.exe", $"/select,\"{_shown.FullPath}\"");
                else TxtProgress.Text = $"{_shown.Name} is no longer there.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                TxtProgress.Text = "Couldn't open the folder: " + ex.Message;
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
                if (_shown == null && _files.FirstOrDefault(f => f.Status == MetadataStatus.Done) is { } first)
                {
                    DataGridFiles.SelectedItem = first;   // show something straight away
                    DataGridFiles.ScrollIntoView(first);
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
                MessageBox.Show("The export couldn't be saved.\n\n" + MetadataExtractManager.PlainExportError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtProgress.Text = "Export failed: " + MetadataExtractManager.PlainExportError(ex);
            }
            finally
            {
                _adding--;
                UpdateListState();
            }
        }

        private static void Toast(string title, string message)
        {
            try
            {
                ToastNotificationManager.ShowToast(title, message);
            }
            catch (Exception)
            {
                // a toast is a nice-to-have
            }
        }
    }
}
