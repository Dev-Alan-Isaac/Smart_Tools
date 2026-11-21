using MaterialDesignThemes.Wpf;
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
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Images to PDF - UI only. Page layout, the list, file names, splitting
    /// and the run are in <see cref="ImageToPdfManager"/>; finding images in
    /// ImageToPdfManager.Files.cs; ImageMagick and PdfSharp in
    /// ImageToPdfManager.Pdf.cs. The list order is the page order and the
    /// tick box decides what goes in. Folders picked here are kept for this
    /// session only - never saved.
    /// </summary>
    public partial class ImageConvertView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double NarrowColumnThreshold = 1000;
        private const int ThumbnailSide = 64;
        private static string DialogTitle => Loc.T("Str_Tool_images-to-pdf_Name", "Images to PDF");

        private readonly BulkObservableCollection<ImagePdfItem> _items = new();
        private readonly ObservableCollection<ImagePdfGroup> _groups = new();
        private readonly SemaphoreSlim _detailsGate = new(3);   // only rows on screen, a few at a time
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private ImagePdfSettings _settings = new();
        private CancellationTokenSource? _runCts;
        private List<string> _lastFiles = new();
        private int _adding;
        private bool _isBusy;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _fileNameEdited;

        public ImageConvertView()
        {
            InitializeComponent();

            DataGridImages.ItemsSource = _items;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = ImageToPdfManager.FileDialogFilter;
            _items.CollectionChanged += (_, _) => UpdateListState();
            _groups.CollectionChanged += (_, _) =>
                InputCard.SetEmpty(_groups.Count == 0);
            DataGridImages.SelectionChanged += (_, _) => UpdateButtons();

            OutputCard.DefaultFolder = ImageToPdfManager.DefaultFolder;
            OutputCard.DefaultFileName = ImageToPdfManager.DefaultFileName;
            SetFileName(ImageToPdfManager.DefaultFileName);
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. A cancelled run saves nothing, so no half-made PDF is left behind.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _lifetimeCts.Cancel();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(ImageToPdfManager.LoadSettings());
            }
            ApplyResponsiveColumns(ActualWidth);
            UpdateListState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            var visibility = width < NarrowColumnThreshold ? Visibility.Collapsed : Visibility.Visible;
            ColFormat.Visibility = visibility;
            ColDimensions.Visibility = visibility;
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
        //  Settings (no folders)
        // =================================================================

        private void ApplySettings(ImagePdfSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                SelectByTag(CmbPageSize, s.PageSize.ToString());
                CheckByTag(s.Orientation.ToString(), RadioPortrait, RadioLandscape, RadioAutoOrientation);
                CheckByTag(s.Margins.ToString(), RadioMarginNone, RadioMarginNarrow, RadioMarginNormal, RadioMarginWide);
                CheckByTag(s.Quality.ToString(), RadioQualityHigh, RadioQualityBalanced, RadioQualitySmall);
                CheckByTag(s.SplitMegabytes.ToString(), RadioSplitOff, RadioSplit10, RadioSplit20, RadioSplit30);
                ChkFitImageToPage.IsChecked = s.FitImageToPage;
                ChkPageNumbering.IsChecked = s.AddPageNumbers;
                ChkAddTitlePage.IsChecked = s.AddTitlePage;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                OutputCard.AskEachTime = !s.SaveWithoutAsking;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateOptionPanels();
        }

        private ImagePdfSettings ReadSettings() => new()
        {
            PageSize = ParseTag(SelectedTag(CmbPageSize), ImagePdfPageSize.A4),
            Orientation = ParseTag(CheckedTag(RadioPortrait, RadioLandscape, RadioAutoOrientation), ImagePdfOrientation.Portrait),
            Margins = ParseTag(CheckedTag(RadioMarginNone, RadioMarginNarrow, RadioMarginNormal, RadioMarginWide), ImagePdfMargins.Normal),
            Quality = ParseTag(CheckedTag(RadioQualityHigh, RadioQualityBalanced, RadioQualitySmall), ImagePdfQuality.Balanced),
            SplitMegabytes = int.TryParse(CheckedTag(RadioSplitOff, RadioSplit10, RadioSplit20, RadioSplit30), out int mb) ? mb : 30,
            FitImageToPage = ChkFitImageToPage.IsChecked == true,
            AddPageNumbers = ChkPageNumbering.IsChecked == true,
            AddTitlePage = ChkAddTitlePage.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SaveWithoutAsking = !OutputCard.AskEachTime,
        };

        private void Option_Changed(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (Ignore) return;
            _settings = ReadSettings();
            UpdateOptionPanels();
            try
            {
                ImageToPdfManager.SaveSettings(_settings);
            }
            catch (Exception)
            {
                // Settings are a convenience - a locked file mustn't stop the tool.
            }
        }

        private void UpdateOptionPanels()
        {
            bool titleWasHidden = TitlePageOptionsPanel.Visibility != Visibility.Visible;
            bool showTitle = ChkAddTitlePage.IsChecked == true;
            TitlePageOptionsPanel.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
            if (showTitle && titleWasHidden && _loadedOnce && !_applyingSettings)
            {
                UiMotion.FadeIn(TitlePageOptionsPanel, 0);
                TxtTitlePageTitle.Focus();
            }

            // "Same as image" pages are always the picture's own shape.
            PaperOptionsPanel.Visibility = SelectedTag(CmbPageSize) == nameof(ImagePdfPageSize.MatchImage) ? Visibility.Collapsed : Visibility.Visible;

            TxtQualityHint.Text = ParseTag(CheckedTag(RadioQualityHigh, RadioQualityBalanced, RadioQualitySmall), ImagePdfQuality.Balanced) switch
            {
                ImagePdfQuality.High => "Full size, best quality. JPEG photos go in untouched; the PDF is about as big as the images.",
                ImagePdfQuality.Small => "Up to 2,000 px on the long side - fine on screens and for email, softer when printed.",
                _ => "Up to 3,500 px on the long side - still about 300 dpi on A4, a fraction of the size.",
            };
        }

        // =================================================================
        //  Adding images
        // =================================================================

        // ---- The Files card (same as Validate Data / Convert Files) ----

        /// <summary>The Clear button on the Files card - same as Clear all above the list.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isBusy) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        /// <summary>
        /// Like Validate Data and Convert Files: changing "Include subfolders"
        /// also updates the folders already added (it used to apply only to
        /// the next folder). Pages already in order keep their place.
        /// </summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            OnOptionChanged();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (_isBusy || folders.Count == 0) return;
            var token = _lifetimeCts.Token;
            _adding++;
            UpdateButtons();
            TxtProgress.Text = includeSubfolders ? "Looking in subfolders…" : "Leaving out subfolders…";
            try
            {
                int added = 0, removed = 0, inaccessible = 0;
                foreach (var group in folders)
                {
                    var scan = await ImageToPdfManager.ScanFolderAsync(group.FolderPath!, includeSubfolders, token);
                    if (scan.Missing) continue;   // folder gone - leave its images alone
                    inaccessible += scan.InaccessibleFolders;
                    var (a, r) = ImageToPdfManager.ApplyRescan(_items, _groups, group, scan.Files, includeSubfolders);
                    added += a;
                    removed += r;
                }
                UpdateListState();
                if (added + removed > 0) InputCard.PopDropArea();
                string text = includeSubfolders
                    ? (added > 0 ? $"Added {added:N0} {ImagePdfText.Plural(added, "image", "images")} from subfolders, at the end of the list." : "No images in the subfolders.")
                    : (removed > 0 ? $"Took {removed:N0} {ImagePdfText.Plural(removed, "subfolder image", "subfolder images")} off the list." : "No subfolder images were in the list.");
                if (inaccessible > 0) text += $" {inaccessible:N0} {ImagePdfText.Plural(inaccessible, "folder", "folders")} couldn't be opened.";
                if (!_isBusy) TxtProgress.Text = text;
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgress.Text = "Couldn't read the folders again: " + ImageToPdfManager.PlainError(ex);
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
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
            if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        /// <summary>
        /// Adds images to the end of the list, reading folders off the UI
        /// thread. Each folder becomes one sidebar entry; loose files share
        /// one. Sizes and thumbnails load later, only for rows on screen (the
        /// old list decoded every whole image on the UI thread first).
        /// </summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isBusy || paths.Count == 0) return;
            var token = _lifetimeCts.Token;
            bool subfolders = InputCard.IncludeSubfolders;
            _adding++;
            UpdateButtons();
            TxtProgress.Text = Loc.T("Str_ImageConvertPDF_StatusLooking", "Looking for images…");
            try
            {
                int added = 0, inaccessible = 0, missing = 0;
                string? firstName = null;
                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var scan = await ImageToPdfManager.ScanFolderAsync(folder, subfolders, token);
                    inaccessible += scan.InaccessibleFolders;
                    if (scan.Missing) missing++;
                    var group = ImageToPdfManager.AddToList(_items, _groups, scan.Files, folder, isFolder: true, subfolders);
                    if (group.Count > 0) firstName ??= Path.GetFileName(folder.TrimEnd('\\', '/'));
                    added += group.Count;
                }

                var files = paths.Where(p => !Directory.Exists(p)).ToList();
                if (files.Count > 0)
                {
                    var found = await ImageToPdfManager.ReadFilesAsync(files, subfolders, token);
                    missing += files.Count - found.Count;
                    var group = ImageToPdfManager.AddToList(_items, _groups, found, null, isFolder: false, includeSubfolders: false);
                    added += group.Count;
                }

                if (firstName != null) SuggestFileName(firstName);
                if (added > 0) InputCard.PopDropArea();

                var parts = new List<string>
                {
                    added == 0
                        ? "No new images found - they may already be in the list."
                        : $"Added {added:N0} {ImagePdfText.Plural(added, "image", "images")}.",
                };
                if (inaccessible > 0) parts.Add($"{inaccessible:N0} {ImagePdfText.Plural(inaccessible, "folder", "folders")} couldn't be opened.");
                if (missing > 0 && files.Count > 0) parts.Add($"{missing:N0} {ImagePdfText.Plural(missing, "file wasn't an image", "files weren't images")} or couldn't be found.");
                if (!_isBusy) TxtProgress.Text = string.Join(" ", parts);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex)
            {
                if (!_isBusy) TxtProgress.Text = "Couldn't add the images: " + ImageToPdfManager.PlainError(ex);
            }
            finally
            {
                _adding--;
                UpdateButtons();
            }
        }

        /// <summary>Names the PDF after the first folder added, unless the user typed a name.</summary>
        private void SuggestFileName(string folderName)
        {
            if (_fileNameEdited || string.IsNullOrWhiteSpace(folderName)) return;
            SetFileName(Path.GetFileName(ImageToPdfManager.ResolveOutputPath(null, folderName)));
        }

        /// <summary>Set by code (a suggestion): the card doesn't count it as the user's own name.</summary>
        private void SetFileName(string name) => OutputCard.FileName = Path.GetFileNameWithoutExtension(name);

        /// <summary>The user typed a name: stop suggesting one from the folders added.</summary>
        private void OutputCard_FileNameEdited(object? sender, EventArgs e) =>
            _fileNameEdited = !string.IsNullOrWhiteSpace(OutputCard.FileName);

        private void OutputCard_AskEachTimeChanged(object? sender, EventArgs e) => OnOptionChanged();

        // =================================================================
        //  Rows: size + thumbnail for rows on screen
        // =================================================================

        private void DataGridImages_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is ImagePdfItem { DetailsRequested: false } item)
            {
                item.DetailsRequested = true;
                _ = LoadDetailsAsync(item, _lifetimeCts.Token);
            }
        }

        private async Task LoadDetailsAsync(ImagePdfItem item, CancellationToken token)
        {
            try
            {
                await _detailsGate.WaitAsync(token);
                try
                {
                    var (size, thumb) = await Task.Run(() =>
                        (ImageToPdfManager.ReadImageSize(item.FullPath), (ImageSource?)ImageViewerManager.LoadThumbnail(item.FullPath, ThumbnailSide)), token);
                    if (token.IsCancellationRequested) return;
                    item.SetSize(size.Width, size.Height);
                    item.Thumbnail = thumb;
                }
                finally
                {
                    _detailsGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                item.DetailsRequested = false;
            }
        }

        // =================================================================
        //  The list: tick, order, remove
        // =================================================================

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateListState();

        private void ChkIncludeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            bool include = ChkIncludeAll.IsChecked == true;
            foreach (var item in _items) item.IsIncluded = include;
            UpdateListState();
        }

        private List<ImagePdfItem> HighlightedItems() => DataGridImages.SelectedItems.OfType<ImagePdfItem>().ToList();

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveHighlighted(-1);

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveHighlighted(+1);

        private void MoveHighlighted(int direction)
        {
            if (_isBusy) return;
            var chosen = HighlightedItems();
            if (!ImageToPdfManager.Move(_items, chosen, direction)) return;
            Reselect(chosen);
        }

        /// <summary>Puts the highlight back after rows moved (replacing a row drops it) and keeps them in view.</summary>
        private void Reselect(IReadOnlyList<ImagePdfItem> chosen)
        {
            DataGridImages.SelectedItems.Clear();
            foreach (var item in chosen) DataGridImages.SelectedItems.Add(item);
            var first = chosen.OrderBy(i => i.Number).FirstOrDefault();
            if (first != null)
            {
                DataGridImages.ScrollIntoView(first);
                DataGridImages.CurrentItem = first;
            }
        }

        private void BtnSortByName_Click(object sender, RoutedEventArgs e) => SortList(byDate: false);

        private void BtnSortByDate_Click(object sender, RoutedEventArgs e) => SortList(byDate: true);

        private void SortList(bool byDate)
        {
            if (_isBusy || _items.Count < 2) return;
            var chosen = HighlightedItems();
            var sorted = ImageToPdfManager.Sorted(_items, byDate);
            _items.Clear();
            _items.AddRange(sorted);
            ImageToPdfManager.Renumber(_items);
            Reselect(chosen);
            TxtProgress.Text = byDate ? "Pages sorted by date, oldest first." : "Pages sorted by name.";
        }

        private void BtnRemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: ImagePdfItem item }) RemoveItems(new[] { item });
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy && sender is Button { Tag: ImagePdfGroup group }) RemoveItems(group.Items.ToList());
        }

        /// <summary>Removes the highlighted rows. (The old button removed every image - all were "selected" with no tick box to see it.)</summary>
        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBusy) RemoveItems(HighlightedItems());
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (_isBusy || _items.Count == 0) return;
            _items.Clear();
            _groups.Clear();
            ResetResult();
            TxtProgress.Text = Loc.T("Str_ImageConvertPDF_AddImagesToMake", "Add images to make a PDF.");
        }

        private void RemoveItems(IReadOnlyCollection<ImagePdfItem> items)
        {
            if (items.Count == 0) return;
            var gone = new HashSet<ImagePdfItem>(items);
            int nextIndex = Math.Max(0, _items.TakeWhile(i => !gone.Contains(i)).Count());
            ImageToPdfManager.RemoveFromList(_items, _groups, items);
            if (_items.Count > 0)
            {
                // Keep the keyboard on the list: highlight the row that took the first removed row's place.
                var next = _items[Math.Clamp(nextIndex, 0, _items.Count - 1)];
                DataGridImages.SelectedItem = next;
                DataGridImages.CurrentItem = next;
            }
        }

        private void DataGridImages_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ButtonBase) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete)
            {
                if (!_isBusy) RemoveItems(HighlightedItems());
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
            {
                MoveHighlighted(key == Key.Up ? -1 : +1);
                e.Handled = true;
            }
        }

        private void DataGridImages_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;   // the remove button / tick box
            if (FindAncestor<DataGridRow>(source)?.Item is ImagePdfItem item) OpenWithShell(item.FullPath);
        }

        private void UpdateListState()
        {
            int count = _items.Count;
            UiMotion.FadeVisibility(EmptyStatePanel, count == 0);
            UiMotion.FadeVisibility(DataGridImages, count > 0);

            bool any = count > 0;
            if (any && SummaryPanel.Visibility != Visibility.Visible) UiMotion.Pop(SummaryPanel, 0.9);
            SummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryCount.Text = ImageToPdfManager.DescribeList(_items);

            int added = _items.Count(i => i.Status == ImagePdfStatus.Added);
            int failed = _items.Count(i => i.Status == ImagePdfStatus.Failed);
            PillAdded.Visibility = added > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryAdded.Text = $"{added:N0} added";
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSummaryFailed.Text = $"{failed:N0} failed";

            int included = _items.Count(i => i.IsIncluded);
            ChkIncludeAll.IsChecked = count == 0 || included == count ? true : included == 0 ? false : null;

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool idle = !_isBusy;
            bool hasHighlight = DataGridImages.SelectedItems.Count > 0;
            BtnMoveUp.IsEnabled = idle && hasHighlight;
            BtnMoveDown.IsEnabled = idle && hasHighlight;
            BtnRemoveSelected.IsEnabled = idle && hasHighlight;
            BtnSortByName.IsEnabled = idle && _items.Count > 1;
            BtnSortByDate.IsEnabled = idle && _items.Count > 1;
            BtnClearAll.IsEnabled = idle && _items.Count > 0;
            BtnCreatePdf.IsEnabled = _isBusy || (_adding == 0 && _items.Any(i => i.IsIncluded));
        }

        // =================================================================
        //  Output (the shared Output card; folder kept for this session only)
        // =================================================================

        // The folder, the file name and "Ask where to save each time" are all in the Output card.

        // =================================================================
        //  Create / Cancel
        // =================================================================

        private async void BtnCreatePdf_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Cancelling…";
                BtnCreatePdf.IsEnabled = false;
                return;
            }

            var snapshot = _items.ToList();
            if (!snapshot.Any(i => i.IsIncluded))
            {
                TxtProgress.Text = Loc.T("Str_ImageConvertPDF_StatusNoneTicked", "Tick at least one image to put in the PDF.");
                return;
            }

            string? outputPath = PickOutputPath(out bool overwriteAllowed);
            if (outputPath == null) return;

            _settings = ReadSettings();
            var options = new ImagePdfOptions
            {
                OutputPath = outputPath,
                OverwriteAllowed = overwriteAllowed,
                PageSize = _settings.PageSize,
                Orientation = _settings.Orientation,
                Margins = _settings.Margins,
                Quality = _settings.Quality,
                FitImageToPage = _settings.FitImageToPage,
                AddPageNumbers = _settings.AddPageNumbers,
                AddTitlePage = _settings.AddTitlePage,
                Title = TxtTitlePageTitle.Text,
                SplitMegabytes = _settings.SplitMegabytes,
            };

            var cts = new CancellationTokenSource();
            _runCts = cts;
            SetBusy(true);
            ResetResult();
            var progress = new Progress<ImagePdfProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                ProgressConversion.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:0}%";
                TxtProgress.Text = $"Page {p.Processed:N0} of {p.Total:N0} · {p.CurrentFile}";
            });

            try
            {
                var result = await ImageToPdfManager.CreateAsync(snapshot, options, progress, cts.Token);
                ShowResult(result);
            }
            catch (Exception ex)
            {
                ProgressConversion.Value = 0;
                TxtProgressPercent.Text = "";
                TxtProgress.Text = Loc.F("Str_ImageConvertPDF_StatusSaveFailed", "The PDF couldn't be saved: {0}", ImageToPdfManager.PlainError(ex));
                AppModal.Show(Loc.F("Str_MB_ThePdfCouldnT", "The PDF couldn't be saved.\n\n{0}\n\nCheck that the folder exists, you can write to it, and the file isn't open in another program.", ex.Message),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _runCts = null;
                cts.Dispose();
                SetBusy(false);
                UpdateListState();
            }
        }

        /// <summary>
        /// Where the PDF goes, from the Output card: its folder + name (never
        /// over an existing file), or a Save dialog when "Ask where to save each
        /// time" is ticked (then replacing a file is the user's choice). A folder
        /// that's gone or not writable stops here with the reason - the old view
        /// only found out when the save failed after every page was made.
        /// </summary>
        private string? PickOutputPath(out bool overwriteAllowed)
        {
            string? path = OutputCard.GetSavePath("PDF files|*.pdf", out overwriteAllowed, out string? problem);
            if (path == null && problem != null)
                TxtProgress.Text = "Can't save there: " + problem + ". Choose another folder in Save to.";
            return path;
        }

        private void ShowResult(ImagePdfResult result)
        {
            TxtProgress.Text = ImageToPdfManager.DescribeResult(result);
            if (result.Cancelled || result.Files.Count == 0)
            {
                ProgressConversion.Value = 0;
                TxtProgressPercent.Text = "";
                if (!result.Cancelled && result.Error != null)
                    AppModal.Show(result.Error, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProgressConversion.Value = 100;
            TxtProgressPercent.Text = "100%";
            _lastFiles = result.Files;
            BtnOpenPdf.IsEnabled = true;
            BtnShowInFolder.IsEnabled = true;
            BtnOpenPdf.ToolTip = result.Files.Count == 1 ? "Open the PDF" : "Open the first PDF";
            UiMotion.Pop(BtnOpenPdf, 0.9);
        }

        private void ResetResult()
        {
            _lastFiles = new List<string>();
            BtnOpenPdf.IsEnabled = false;
            BtnShowInFolder.IsEnabled = false;
            ProgressConversion.Value = 0;
            TxtProgressPercent.Text = "";
            foreach (var item in _items)
            {
                item.Status = ImagePdfStatus.Ready;
                item.Message = "";
            }
        }

        private Log.ActivityScope? _activity;

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            if (busy) _activity ??= Log.Activity("image-convert-pdf");
            else { _activity?.Dispose(); _activity = null; }
            OptionsScrollViewer.IsEnabled = !busy;
            OutputCard.IsLocked = busy;
            if (!busy) OutputCard.Refresh();   // free space, and the "Saves as" name may now be " (2)"
            ChkIncludeAll.IsEnabled = !busy;
            IconCreate.Kind = busy ? PackIconKind.Close : PackIconKind.FilePdfBox;
            if (busy) TxtCreateLabel.Text = "Cancel";
            else TxtCreateLabel.SetResourceReference(TextBlock.TextProperty, "Str_ImgPdf_CreateButton");
            BtnCreatePdf.ToolTip = busy ? "Stop - nothing is saved" : "Make a PDF from the ticked images, in list order";
            UpdateButtons();
        }

        private void BtnOpenPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count > 0) OpenWithShell(_lastFiles[0]);
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 0) return;
            string file = _lastFiles[0];
            try
            {
                if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else if (Directory.Exists(Path.GetDirectoryName(file))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(file)}\"");
                else TxtProgress.Text = Loc.T("Str_ImageConvertPDF_StatusGone", "The PDF is no longer there.");
            }
            catch (Exception ex)
            {
                TxtProgress.Text = "Couldn't open the folder: " + ImageToPdfManager.PlainError(ex);
            }
        }

        private void OpenWithShell(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    TxtProgress.Text = $"{Path.GetFileName(path)} is no longer there.";
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TxtProgress.Text = $"Couldn't open {Path.GetFileName(path)}: " + ImageToPdfManager.PlainError(ex);
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
                if (item.Tag as string == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            combo.SelectedIndex = 0;
        }

        private static string? CheckedTag(params RadioButton[] buttons) => buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;

        private static void CheckByTag(string tag, params RadioButton[] buttons)
        {
            var match = buttons.FirstOrDefault(b => b.Tag as string == tag) ?? buttons[0];
            match.IsChecked = true;
        }

        private static T ParseTag<T>(string? tag, T fallback) where T : struct, Enum =>
            Enum.TryParse(tag, out T value) && Enum.IsDefined(value) ? value : fallback;

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
