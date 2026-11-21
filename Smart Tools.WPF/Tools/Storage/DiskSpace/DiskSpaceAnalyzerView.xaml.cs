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
using System.Windows.Media;
using System.Windows.Shapes;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Disk Space Analyzer view - UI only. Scanning, the treemap layout,
    /// the by-type breakdown, deleting and settings live in
    /// <see cref="SpaceAnalyzerManager"/>; data shapes in SpaceAnalyzerModel.cs.
    ///
    /// Flow: pick a folder (or a whole drive) -> Analyze -> drill in by
    /// double-clicking a folder, a treemap tile or a breadcrumb segment.
    /// Everything after the scan works on the in-memory tree, so
    /// navigating, filtering and deleting never re-read the disk.
    /// </summary>
    public partial class SpaceAnalyzerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowWidth = 1000;
        private const double TileGap = 2;
        private static string DialogTitle => Loc.T("Str_Tool_disk-space_Name", "Disk Space Analyzer");

        private readonly SpaceAnalyzerManager _manager = new();
        private readonly ObservableCollection<SpaceListItem> _rows = new();
        private readonly Dictionary<FileCategory, SolidColorBrush> _categoryBrushes = new();

        private SpaceAnalyzerSettings _settings = new();
        private List<DriveSummary> _drives = new();
        private SpaceNode? _rootNode;
        private SpaceNode? _currentNode;
        private SpaceScanOptions? _lastOptions;
        private Dictionary<SpaceNode, FileCategory> _dominant = new();

        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _insightCts;
        private Task _insightTask = Task.CompletedTask;

        private bool _isScanning;
        private bool _isDeleting;
        private bool _showingTreemap;
        private bool _optionsCollapsed;
        private bool _settingsLoaded;
        private bool _suspendSave;
        private readonly bool _viewConstructed;

        public SpaceAnalyzerView()
        {
            InitializeComponent();
            DataGridSpace.ItemsSource = _rows;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _scanCts?.Cancel();
            _insightCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_settingsLoaded)
            {
                PopulateDrives();                                   // SaveSettings is a no-op until loaded
                ApplySettings(SpaceAnalyzerManager.LoadSettings());
                _settingsLoaded = true;
            }
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Item count + modified are the first things to go on a narrow window.
            var visibility = ActualWidth < NarrowWidth ? Visibility.Collapsed : Visibility.Visible;
            ColItemCount.Visibility = visibility;
            ColModified.Visibility = visibility;
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
            OptionsBottomPanel.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Settings (remembered between sessions)
        // =================================================================

        private void ApplySettings(SpaceAnalyzerSettings s)
        {
            _suspendSave = true;
            try
            {
                _settings = s;

                if (!string.IsNullOrEmpty(s.LastFolder) && Directory.Exists(s.LastFolder))
                    InputCard.FolderPath = s.LastFolder;

                if (!string.IsNullOrEmpty(s.LastDrive))
                {
                    var drive = _drives.FirstOrDefault(d => string.Equals(d.RootPath, s.LastDrive, StringComparison.OrdinalIgnoreCase));
                    if (drive != null) CmbDrive.SelectedItem = drive;
                }

                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                ChkIgnoreSystemFiles.IsChecked = s.IgnoreSystemFiles;
                ChkShowHidden.IsChecked = s.ShowHiddenItems;

                var minRadio = MinSizeRadios().FirstOrDefault(r => TagBytes(r) == s.MinItemSizeBytes) ?? RadioMin10;
                minRadio.IsChecked = true;

                if (s.ScopeDrive) RadioScopeDisk.IsChecked = true;
                else RadioScopeFolder.IsChecked = true;
                ApplyScopeUi(animate: false);

                SetTreemapView(s.TreemapView);
            }
            finally
            {
                _suspendSave = false;
            }
        }

        private void SaveSettings()
        {
            if (_suspendSave || !_settingsLoaded) return;

            _settings.LastFolder = string.IsNullOrWhiteSpace(InputCard.FolderPath) ? _settings.LastFolder : InputCard.FolderPath;
            _settings.LastDrive = (CmbDrive.SelectedItem as DriveSummary)?.RootPath ?? _settings.LastDrive;
            _settings.ScopeDrive = RadioScopeDisk.IsChecked == true;
            _settings.IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true;
            _settings.IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true;
            _settings.ShowHiddenItems = ChkShowHidden.IsChecked == true;
            _settings.MinItemSizeBytes = CurrentMinSize;
            _settings.TreemapView = _showingTreemap;

            try
            {
                SpaceAnalyzerManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                // Not worth interrupting the user over.
                Log.Info($"Space analyzer settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  What to scan: folder or drive
        // =================================================================

        private void RadioScope_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ApplyScopeUi(animate: true);
            SaveSettings();
        }

        private bool IsDriveScope => RadioScopeDisk.IsChecked == true;

        private void ApplyScopeUi(bool animate)
        {
            bool drive = IsDriveScope;

            InputCard.Visibility = drive ? Visibility.Collapsed : Visibility.Visible;
            DiskScopePanel.Visibility = drive ? Visibility.Visible : Visibility.Collapsed;

            if (animate) UiMotion.FadeIn(drive ? DiskScopePanel : InputCard, 4);
        }

        private void PopulateDrives()
        {
            string? previous = (CmbDrive.SelectedItem as DriveSummary)?.RootPath;

            _drives = SpaceAnalyzerManager.GetDrives();
            CmbDrive.ItemsSource = _drives;
            CmbDrive.IsEnabled = _drives.Count > 0;

            if (_drives.Count > 0)
                CmbDrive.SelectedItem = _drives.FirstOrDefault(d => string.Equals(d.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                                        ?? _drives[0];
            else TxtDriveUsage.Text = "No drives are ready.";
        }

        private void BtnRefreshDrives_Click(object sender, RoutedEventArgs e) => PopulateDrives();

        private void CmbDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;

            if (CmbDrive.SelectedItem is DriveSummary d)
            {
                CmbDrive.ScrollIntoView(d);
                TxtDriveUsage.Text = $"{SpaceAnalyzerManager.FormatBytes(d.UsedBytes)} used ({d.UsedPercent:0}% full)";
            }
            else
            {
                TxtDriveUsage.Text = "";
            }

            SaveSettings();
        }

        /// <summary>The Input card's folder (or its check) changed - remember it.</summary>
        private void InputCard_FolderChanged(object? sender, EventArgs e) => SaveSettings();

        // =================================================================
        //  Minimum size (display filter - applies instantly)
        // =================================================================

        private IEnumerable<RadioButton> MinSizeRadios() => new[] { RadioMinAll, RadioMin10, RadioMin100, RadioMin1G };

        private static long TagBytes(RadioButton r) => long.TryParse(r.Tag?.ToString(), out long v) ? v : 0;

        private long CurrentMinSize => TagBytes(MinSizeRadios().FirstOrDefault(r => r.IsChecked == true) ?? RadioMinAll);

        private void RadioMinSize_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (_currentNode != null) NavigateTo(_currentNode, animate: false);
            SaveSettings();
        }

        // =================================================================
        //  Scan
        // =================================================================

        private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _scanCts?.Cancel();
                return;
            }

            var options = BuildOptions();
            if (options != null) _ = RunScanAsync(options);
        }

        private void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning || _lastOptions == null) return;
            _ = RunScanAsync(_lastOptions);
        }

        private SpaceScanOptions? BuildOptions()
        {
            string root;
            long expected = 0;

            if (IsDriveScope)
            {
                if (CmbDrive.SelectedItem is not DriveSummary drive)
                {
                    AppModal.Show(Loc.T("Str_MB_ChooseADriveTo", "Choose a drive to analyze first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
                root = drive.RootPath;
                expected = drive.UsedBytes;
            }
            else
            {
                root = InputCard.FolderPath;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    AppModal.Show(Loc.T("Str_MB_ChooseAFolderTo", "Choose a folder to analyze first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    InputCard.PopDropArea();
                    return null;
                }
            }

            return new SpaceScanOptions
            {
                RootPath = root,
                IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
                IgnoreSystemFiles = ChkIgnoreSystemFiles.IsChecked == true,
                ShowHiddenItems = ChkShowHidden.IsChecked == true,
                ExpectedBytes = expected,
            };
        }

        private async Task RunScanAsync(SpaceScanOptions options)
        {
            SaveSettings();
            _lastOptions = options;

            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            SetScanning(true, options.ExpectedBytes > 0);

            // Progress<T> captures the UI SynchronizationContext here.
            var progress = new Progress<SpaceScanProgress>(ReportProgress);

            try
            {
                var result = await _manager.ScanAsync(options, progress, token);

                if (!result.Success)
                {
                    TxtProgress.Text = result.ErrorMessage ?? "Scan failed.";
                    AppModal.Show(result.ErrorMessage ?? "Scan failed.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = _rootNode == null
                    ? "Scan cancelled."
                    : "Scan cancelled - still showing the previous results.";
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Scan failed.";
                AppModal.Show(Loc.F("Str_Err_ScanFailed", "Scan failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetScanning(false, false);
            }
        }

        private void ReportProgress(SpaceScanProgress p)
        {
            if (!_isScanning) return;

            if (p.Percent is double pct)
            {
                ProgressScan.IsIndeterminate = false;
                ProgressScan.Value = pct;
                TxtProgressPercent.Text = $"{pct:0}%";
            }

            TxtProgress.Text = $"Scanned {p.ItemsScanned:N0} items · {SpaceAnalyzerManager.FormatBytes(p.BytesScanned)} · {p.CurrentPath}";
        }

        private void SetScanning(bool scanning, bool knownTotal)
        {
            _isScanning = scanning;

            IconAnalyze.Kind = scanning ? PackIconKind.StopCircleOutline : PackIconKind.ChartDonut;
            if (scanning) TxtAnalyzeLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtAnalyzeLabel.SetResourceReference(TextBlock.TextProperty, "Str_DiskSpace_AnalyzeButton");
            UiMotion.Pop(BtnAnalyze, 0.96);

            // The sidebar and the result actions stay put while the tree is being replaced.
            OptionsScrollViewer.IsEnabled = !scanning;
            BtnRescan.IsEnabled = !scanning && _lastOptions != null && _rootNode != null;
            UpdateActionButtons();

            if (scanning)
            {
                ProgressScan.IsIndeterminate = !knownTotal;
                ProgressScan.Value = 0;
                TxtProgressPercent.Text = knownTotal ? "0%" : "";
                TxtProgress.Text = "Starting scan...";
            }
            else
            {
                ProgressScan.IsIndeterminate = false;
                ProgressScan.Value = _rootNode != null ? 100 : 0;
                TxtProgressPercent.Text = "";
            }
        }

        private void ShowResult(SpaceScanResult result)
        {
            var root = result.Root!;
            _rootNode = root;

            ProgressScan.Value = 100;
            string secs = result.Elapsed.TotalSeconds < 10 ? $"{result.Elapsed.TotalSeconds:0.0}" : $"{result.Elapsed.TotalSeconds:0}";
            string links = result.SkippedLinks > 0 ? $" · {result.SkippedLinks:N0} links not followed" : "";
            TxtProgress.Text = $"Done in {secs} s · {result.TotalItemsScanned:N0} items{links}";

            NavigateTo(root, animate: true);
            UpdateSummary(result.TotalItemsScanned, result.InaccessibleFolders);

            SummaryPanel.Visibility = Visibility.Visible;
            UiMotion.Pop(SummaryPanel, 0.9);

            try
            {
                ToastNotificationManager.ShowToast("Disk Scan Complete",
                    $"{root.DisplayName}: {SpaceAnalyzerManager.FormatBytes(root.SizeBytes)} in {result.TotalItemsScanned:N0} items.");
            }
            catch (Exception ex)
            {
                Log.Info($"Toast failed: {ex.Message}");
            }
        }

        private int _lastInaccessible;

        private void UpdateSummary(long? itemsScanned = null, int? inaccessible = null)
        {
            if (_rootNode == null) return;
            if (inaccessible.HasValue) _lastInaccessible = inaccessible.Value;

            TxtSummaryTotalSize.Text = SpaceAnalyzerManager.FormatBytes(_rootNode.SizeBytes);
            TxtSummaryCount.Text = $"{(itemsScanned ?? _rootNode.ItemCount):N0} items";

            InaccessiblePill.Visibility = _lastInaccessible > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtInaccessible.Text = _lastInaccessible == 1 ? "1 folder unreadable" : $"{_lastInaccessible:N0} folders unreadable";

            CloudPill.Visibility = _rootNode.CloudBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtCloud.Text = $"{SpaceAnalyzerManager.FormatBytes(_rootNode.CloudBytes)} online-only";
        }

        // =================================================================
        //  Navigation
        // =================================================================

        private void NavigateTo(SpaceNode node, bool animate)
        {
            _currentNode = node;

            _rows.Clear();
            foreach (var row in SpaceAnalyzerManager.BuildRows(node, CurrentMinSize)) _rows.Add(row);

            BuildBreadcrumb(node);

            string count = node.ItemCount == 1 ? "1 item" : $"{node.ItemCount:N0} items";
            TxtCurrentPath.Text = $"{SpaceAnalyzerManager.FormatBytes(node.SizeBytes)} · {count}";

            BtnUpFolder.IsEnabled = node.Parent != null;
            UpdateActionButtons();
            UpdateEmptyState();

            _dominant = new Dictionary<SpaceNode, FileCategory>();
            if (_showingTreemap) RenderTreemap();
            StartInsight(node);

            if (animate)
            {
                UiMotion.FadeIn(_showingTreemap ? TreemapHost : DataGridSpace, 6);
                if (_rows.Count > 0) DataGridSpace.ScrollIntoView(_rows[0]);
            }
        }

        private void BuildBreadcrumb(SpaceNode node)
        {
            BreadcrumbPanel.Children.Clear();
            var path = SpaceAnalyzerManager.PathTo(node);

            for (int i = 0; i < path.Count; i++)
            {
                var segment = path[i];
                bool last = i == path.Count - 1;

                if (i > 0)
                {
                    var chevron = new PackIcon
                    {
                        Kind = PackIconKind.ChevronRight,
                        Width = 14,
                        Height = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    chevron.SetResourceReference(ForegroundProperty, "TextSecondary");
                    BreadcrumbPanel.Children.Add(chevron);
                }

                var crumb = new Button
                {
                    Content = segment.DisplayName,
                    Tag = segment,
                    ToolTip = $"{segment.FullPath}\n{SpaceAnalyzerManager.FormatBytes(segment.SizeBytes)}",
                };
                crumb.SetResourceReference(StyleProperty, "CrumbButtonStyle");
                if (last)
                {
                    crumb.FontWeight = FontWeights.SemiBold;
                    crumb.SetResourceReference(ForegroundProperty, "TextPrimary");
                }
                crumb.Click += Crumb_Click;
                BreadcrumbPanel.Children.Add(crumb);
            }

            BreadcrumbScroller.UpdateLayout();
            BreadcrumbScroller.ScrollToRightEnd();
        }

        private void Crumb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: SpaceNode node } && node != _currentNode && !_isDeleting)
                NavigateTo(node, animate: true);
        }

        private void BtnUpFolder_Click(object sender, RoutedEventArgs e) => GoUp();

        private void GoUp()
        {
            if (_currentNode?.Parent is not SpaceNode parent || _isDeleting) return;
            var from = _currentNode;
            NavigateTo(parent, animate: true);

            // Land on the folder we came out of.
            var row = _rows.FirstOrDefault(r => r.Node == from);
            if (row != null && !_showingTreemap)
            {
                DataGridSpace.SelectedItem = row;
                DataGridSpace.ScrollIntoView(row);
            }
        }

        private void OpenItem(SpaceListItem item)
        {
            if (item.Node == null || _isDeleting) return;

            if (item.Node.Kind == SpaceItemKind.Folder) NavigateTo(item.Node, animate: true);
            else ShowInExplorer(item.FullPath, select: true);
        }

        // =================================================================
        //  By-type breakdown (computed off the UI thread)
        // =================================================================

        private void StartInsight(SpaceNode folder)
        {
            _insightCts?.Cancel();
            _insightCts?.Dispose();
            _insightCts = new CancellationTokenSource();
            var token = _insightCts.Token;

            _insightTask = RunInsightAsync(folder, token);
        }

        private async Task RunInsightAsync(SpaceNode folder, CancellationToken token)
        {
            SpaceAnalyzerManager.FolderInsight insight;
            try
            {
                insight = await Task.Run(() => SpaceAnalyzerManager.Analyze(folder, token), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || folder != _currentNode) return;

            _dominant = insight.DominantByChild;
            ShowBreakdown(insight.Breakdown);
            if (_showingTreemap) RenderTreemap();
        }

        private void ShowBreakdown(List<TypeBreakdownEntry> breakdown)
        {
            TypeBarGrid.ColumnDefinitions.Clear();
            TypeBarGrid.Children.Clear();
            TypeLegend.Children.Clear();

            var visible = breakdown.Where(b => b.Bytes > 0).ToList();
            if (visible.Count == 0)
            {
                TypePanel.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var entry in visible)
            {
                int col = TypeBarGrid.ColumnDefinitions.Count;
                TypeBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(entry.Percent, 0.15), GridUnitType.Star) });

                var brush = BrushFor(entry.Category);
                var segment = new Rectangle
                {
                    Fill = brush,
                    Margin = new Thickness(0, 0, col < visible.Count - 1 ? 1 : 0, 0),
                    ToolTip = LegendText(entry),
                };
                Grid.SetColumn(segment, col);
                TypeBarGrid.Children.Add(segment);

                TypeLegend.Children.Add(BuildLegendChip(entry, brush));
            }

            if (TypePanel.Visibility != Visibility.Visible)
            {
                TypePanel.Visibility = Visibility.Visible;
                UiMotion.FadeIn(TypePanel, 4);
            }
        }

        private static string LegendText(TypeBreakdownEntry e)
        {
            string files = e.Category == FileCategory.Folder ? "" : e.FileCount == 1 ? " · 1 file" : $" · {e.FileCount:N0} files";
            return $"{e.Label}: {SpaceAnalyzerManager.FormatBytes(e.Bytes)} ({e.Percent:0.#}%){files}";
        }

        private FrameworkElement BuildLegendChip(TypeBreakdownEntry entry, Brush brush)
        {
            var text = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Text = $"{entry.Label}  {SpaceAnalyzerManager.FormatBytes(entry.Bytes)}",
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = brush,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(text);

            return new Border
            {
                Child = panel,
                Margin = new Thickness(0, 0, 14, 4),
                ToolTip = LegendText(entry),
            };
        }

        private SolidColorBrush BrushFor(FileCategory category)
        {
            if (!_categoryBrushes.TryGetValue(category, out var brush))
            {
                var color = (Color)ColorConverter.ConvertFromString(SpaceAnalyzerManager.ColorFor(category));
                brush = new SolidColorBrush(color);
                brush.Freeze();
                _categoryBrushes[category] = brush;
            }
            return brush;
        }

        // =================================================================
        //  List / treemap
        // =================================================================

        private void BtnViewList_Click(object sender, RoutedEventArgs e)
        {
            if (!_showingTreemap) return;
            SetTreemapView(false);
            UiMotion.FadeIn(DataGridSpace, 6);
            SaveSettings();
        }

        private void BtnViewTreemap_Click(object sender, RoutedEventArgs e)
        {
            if (_showingTreemap) return;
            SetTreemapView(true);
            UiMotion.FadeIn(TreemapHost, 6);
            SaveSettings();
        }

        private void SetTreemapView(bool treemap)
        {
            _showingTreemap = treemap;

            BtnViewList.SetResourceReference(StyleProperty, treemap ? "ViewToggleButtonStyle" : "ViewToggleButtonActiveStyle");
            BtnViewTreemap.SetResourceReference(StyleProperty, treemap ? "ViewToggleButtonActiveStyle" : "ViewToggleButtonStyle");

            UpdateEmptyState();
            if (treemap) RenderTreemap();
        }

        private void TreemapHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_showingTreemap) RenderTreemap();
        }

        private void RenderTreemap()
        {
            TreemapCanvas.Children.Clear();
            if (!_showingTreemap || _rows.Count == 0) return;

            double w = TreemapHost.ActualWidth, h = TreemapHost.ActualHeight;
            if (w < 20 || h < 20) return;

            foreach (var tile in SpaceAnalyzerManager.Squarify(_rows, w, h))
            {
                double tw = tile.Width - TileGap, th = tile.Height - TileGap;
                if (tw < 1 || th < 1) continue;

                var item = tile.Item;
                var category = item.Kind == SpaceItemKind.Folder && item.Node != null && _dominant.TryGetValue(item.Node, out var dom)
                    ? dom
                    : item.Category;
                string hex = SpaceAnalyzerManager.ColorFor(category);

                var element = BuildTile(item, hex, tw, th);
                Canvas.SetLeft(element, tile.X + TileGap / 2);
                Canvas.SetTop(element, tile.Y + TileGap / 2);
                TreemapCanvas.Children.Add(element);
            }
        }

        private Border BuildTile(SpaceListItem item, string hex, double width, double height)
        {
            var baseColor = (Color)ColorConverter.ConvertFromString(hex);
            // "Smaller items" is muted so real items stand out.
            if (item.Kind == SpaceItemKind.Others) baseColor.A = 0x99;

            var fill = new SolidColorBrush(baseColor);
            fill.Freeze();

            var tile = new Border
            {
                Width = width,
                Height = height,
                Background = fill,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(Math.Min(6, Math.Min(width, height) / 4)),
                Cursor = item.CanOpen ? System.Windows.Input.Cursors.Hand : null,
                Tag = item,
                ToolTip = $"{item.Name}\n{item.SizeText} · {item.PercentText} of this folder" +
                          (item.IsFolder ? $"\n{item.ItemCountText} items · double-click to open" : ""),
            };

            // Labels only where they fit; black or white, whichever reads better on the tile.
            if (width >= 56 && height >= 30)
            {
                Brush ink = Brushes.White;
                if (ColorManager.TryHexToRgb(hex, out var rgb))
                    ink = ColorManager.BestTextColor(rgb).Hex == "#000000" ? Brushes.Black : Brushes.White;

                var labels = new StackPanel { Margin = new Thickness(7, 5, 7, 5), IsHitTestVisible = false };
                labels.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    Foreground = ink,
                    FontSize = 12,
                    FontWeight = item.IsFolder ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                if (height >= 46)
                {
                    labels.Children.Add(new TextBlock
                    {
                        Text = $"{item.SizeText} · {item.PercentText}",
                        Foreground = ink,
                        Opacity = 0.8,
                        FontSize = 10.5,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }
                tile.Child = labels;
            }

            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            tile.MouseLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
            tile.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
            tile.MouseRightButtonUp += (_, _) =>
            {
                if (item.CanOpen) ShowInExplorer(item.FullPath, select: true);
            };

            return tile;
        }

        private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: SpaceListItem item } || !item.CanOpen) return;

            // Single click drills into a folder (the treemap has no selection);
            // double click on a file shows it in Explorer.
            if (item.IsFolder) OpenItem(item);
            else if (e.ClickCount == 2) OpenItem(item);
            e.Handled = true;
        }

        // =================================================================
        //  Grid interaction
        // =================================================================

        private void DataGridSpace_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only a double click on a row - not on the header or scrollbar
            // (the old view opened the selected row for those too).
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not DataGridRow row) return;
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;

            if (row.Item is SpaceListItem item)
            {
                OpenItem(item);
                e.Handled = true;
            }
        }

        private void DataGridSpace_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when DataGridSpace.SelectedItem is SpaceListItem item:
                    OpenItem(item);
                    e.Handled = true;
                    break;

                case Key.Back:
                    GoUp();
                    e.Handled = true;
                    break;

                case Key.Delete when BtnDeleteSelected.IsEnabled:
                    _ = DeleteAsync(SelectedNodes());
                    e.Handled = true;
                    break;
            }
        }

        private void DataGridSpace_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateActionButtons();
        }

        private List<SpaceNode> SelectedNodes() =>
            DataGridSpace.SelectedItems.OfType<SpaceListItem>()
                .Where(r => r.Node != null)
                .Select(r => r.Node!)
                .ToList();

        private void UpdateActionButtons()
        {
            bool idle = !_isScanning && !_isDeleting;
            BtnOpenExplorer.IsEnabled = idle && _currentNode != null;
            BtnDeleteSelected.IsEnabled = idle && !_showingTreemap && SelectedNodes().Count > 0;
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;

            DataGridSpace.Visibility = !_showingTreemap && !empty ? Visibility.Visible : Visibility.Collapsed;
            TreemapHost.Visibility = _showingTreemap && !empty ? Visibility.Visible : Visibility.Collapsed;

            if (_rootNode != null && empty) TxtEmptyHint.Text = "This folder is empty.";
            else TxtEmptyHint.SetResourceReference(TextBlock.TextProperty, "Str_DiskSpace_EmptyHint");

            UiMotion.FadeVisibility(EmptyStatePanel, empty);
            UpdateActionButtons();
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);   // e.g. a Run inside a TextBlock
            }
            return null;
        }

        // =================================================================
        //  Explorer
        // =================================================================

        private void BtnOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            // A single selected row -> select it in Explorer; otherwise open the folder being viewed.
            var selected = SelectedNodes();
            if (selected.Count == 1 && !_showingTreemap) ShowInExplorer(selected[0].FullPath, select: true);
            else if (_currentNode != null) ShowInExplorer(_currentNode.FullPath, select: false);
        }

        private void BtnOpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SpaceListItem { CanOpen: true } item)
                ShowInExplorer(item.FullPath, select: true);
        }

        private static void ShowInExplorer(string path, bool select)
        {
            try
            {
                if (select && (File.Exists(path) || Directory.Exists(path)))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", $"\"{path}\"");
                else
                    AppModal.Show(Loc.F("Str_MB_NoLongerExistsRescan", "\"{0}\" no longer exists. Rescan to refresh.", path), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldnTOpenExplorer", "Couldn't open Explorer: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =================================================================
        //  Delete
        // =================================================================

        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(SelectedNodes());

        private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SpaceListItem { Node: SpaceNode node })
                _ = DeleteAsync(new List<SpaceNode> { node });
        }

        private async Task DeleteAsync(List<SpaceNode> nodes)
        {
            if (nodes.Count == 0 || _isScanning || _isDeleting || _currentNode == null) return;

            long bytes = nodes.Sum(n => n.SizeBytes);
            bool recycle = SpaceAnalyzerManager.HasRecycleBin(nodes[0].FullPath);

            string what = nodes.Count == 1
                ? $"\"{nodes[0].Name}\" ({SpaceAnalyzerManager.FormatBytes(bytes)})"
                : $"{nodes.Count} items ({SpaceAnalyzerManager.FormatBytes(bytes)})";

            string message = recycle
                ? $"Move {what} to the Recycle Bin?"
                : $"Permanently delete {what}?\n\nThis drive has no Recycle Bin, so this can't be undone.";

            var answer = AppModal.Show(message, recycle ? Loc.T("Str_Dlg_Delete", "Delete") : Loc.T("Str_Dlg_DeletePermanently", "Delete permanently"),
                MessageBoxButton.YesNo, recycle ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _isDeleting = true;
            UpdateActionButtons();
            DataGridSpace.IsEnabled = false;
            TreemapHost.IsEnabled = false;
            BtnAnalyze.IsEnabled = false;
            TxtProgress.Text = nodes.Count == 1 ? $"Deleting {nodes[0].Name}..." : $"Deleting {nodes.Count} items...";
            ProgressScan.IsIndeterminate = true;

            try
            {
                var result = await _manager.DeleteAsync(nodes, recycle);

                // The breakdown reads the tree off-thread - let it stop before the tree changes.
                _insightCts?.Cancel();
                try { await _insightTask; } catch (OperationCanceledException) { }

                SpaceAnalyzerManager.ApplyDeletion(result);

                NavigateTo(_currentNode, animate: false);
                UpdateSummary();

                string freed = SpaceAnalyzerManager.FormatBytes(result.BytesFreed);
                TxtProgress.Text = result.Deleted.Count == 0
                    ? Loc.T("Str_MB_NothingDeleted", "Nothing was deleted.")
                    : recycle
                        ? Loc.F("Str_MB_MovedToBin", "Moved {0:N0} item(s) to the Recycle Bin · {1}", result.Deleted.Count, freed)
                        : Loc.F("Str_MB_DeletedFreed", "Deleted {0:N0} item(s) · {1} freed", result.Deleted.Count, freed);

                if (result.Failed.Count > 0)
                {
                    var lines = result.Failed.Take(8).Select(f => $"• {f.Node.Name}: {f.Error}");
                    string more = result.Failed.Count > 8 ? Loc.F("Str_MB_AndNMoreDot", "\n...and {0} more.", result.Failed.Count - 8) : "";
                    AppModal.Show(Loc.F("Str_MB_CouldntDeleteItems", "Couldn't delete {0} item(s):\n\n{1}{2}", result.Failed.Count, string.Join("\n", lines), more),
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_DeleteFailed", "Delete failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isDeleting = false;
                ProgressScan.IsIndeterminate = false;
                DataGridSpace.IsEnabled = true;
                TreemapHost.IsEnabled = true;
                BtnAnalyze.IsEnabled = true;
                UpdateActionButtons();
                DataGridSpace.Focus();
            }
        }
    }
}
