using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image Sprite Sheet Packer - UI only. Packing, slicing, the coordinate files, saving and the settings are in
    /// <see cref="SpritePackerManager"/>. Two modes: Pack (many images become sheets, with a live preview that is the real
    /// packed picture) and Slice (one sheet is cut into a grid). The images and names are never saved, only the setup.
    /// </summary>
    public partial class SpritePackerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const long CacheLimitBytes = 500L * 1024 * 1024;

        private readonly ObservableCollection<SpriteItem> _items = new();
        private readonly ObservableCollection<SpriteGroup> _groups = new();
        private readonly DispatcherTimer _packTimer;
        private readonly DispatcherTimer _sliceTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly SemaphoreSlim _metaGate = new(3);
        private readonly bool _viewConstructed;

        private SpriteMode _mode = SpriteMode.Pack;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _busy;
        private int _packRun;
        private int _pageIndex;
        private CancellationTokenSource? _runCts;
        private string? _lastOutput;

        // The last pack: what was packed (by index), under which names, and where it went.
        private SpritePackResult? _pack;
        private List<SpriteItem> _packItems = new();
        private Dictionary<SpriteItem, SpritePlacement> _placementByItem = new();
        private SpriteCoordFormat _packFormat = SpriteCoordFormat.Json;
        private int _packPadding;

        // Decoded pictures are kept so a setting change repacks without reading the disk again.
        private readonly Dictionary<string, CachedSprite> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new();
        private long _cacheBytes;

        // Slice mode.
        private byte[]? _sheetBgra;
        private int _sheetWidth, _sheetHeight;
        private string _sheetName = "";
        private SpriteSliceResult? _slice;

        private sealed record CachedSprite(long Length, DateTime Modified, byte[]? Bgra, int Width, int Height, string Problem);

        private enum StatusKind { Error, Success, Warning }

        public SpritePackerView()
        {
            InitializeComponent();
            SpriteList.ItemsSource = _items;
            InputCard.ItemsSource = _groups;
            InputCard.FileFilterProvider = () => Str("Str_SpritePacker_FilterImages", "Pictures") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp|" + Str("Str_SpritePacker_FilterAll", "All files") + "|*.*";
            _items.CollectionChanged += (_, _) => UpdateListState();
            _groups.CollectionChanged += (_, _) => InputCard.SetEmpty(_groups.Count == 0);

            _packTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _packTimer.Tick += (_, _) => { _packTimer.Stop(); RunPack(); };
            _sliceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _sliceTimer.Tick += (_, _) => { _sliceTimer.Stop(); UpdateSlice(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); UiMotion.FadeVisibility(DonePill, false); };

            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(SpritePackerManager.LoadSettings());
            SheetViewbox.SizeChanged += (_, _) => UpdateHighlightThickness();
            SliceViewbox.SizeChanged += (_, _) => UpdateSliceThickness();
            UpdateListState();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void ApplySettings(SpritePackerSettings s)
        {
            _applyingSettings = true;
            try
            {
                TxtPadding.Text = s.Padding.ToString(CultureInfo.InvariantCulture);
                (s.MaxSide switch { 512 => RadioMax512, 1024 => RadioMax1024, 4096 => RadioMax4096, 8192 => RadioMax8192, _ => RadioMax2048 }).IsChecked = true;
                ChkPowerOfTwo.IsChecked = s.PowerOfTwo;
                ChkTrim.IsChecked = s.Trim;
                (s.Format switch { SpriteCoordFormat.Css => RadioFormatCss, SpriteCoordFormat.Xml => RadioFormatXml, SpriteCoordFormat.None => RadioFormatNone, _ => RadioFormatJson }).IsChecked = true;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                (s.SplitMode == SpriteSplitMode.ByCellSize ? RadioSplitCell : RadioSplitCount).IsChecked = true;
                TxtColumns.Text = s.Columns.ToString(CultureInfo.InvariantCulture);
                TxtRows.Text = s.Rows.ToString(CultureInfo.InvariantCulture);
                TxtCellWidth.Text = s.CellWidth.ToString(CultureInfo.InvariantCulture);
                TxtCellHeight.Text = s.CellHeight.ToString(CultureInfo.InvariantCulture);
                TxtMargin.Text = s.Margin.ToString(CultureInfo.InvariantCulture);
                TxtSpacing.Text = s.Spacing.ToString(CultureInfo.InvariantCulture);
                ChkSkipEmpty.IsChecked = s.SkipEmpty;
                TxtPrefix.Text = s.NamePrefix;
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(s.Mode);
        }

        private SpritePackerSettings ReadSettings() => new()
        {
            Mode = _mode,
            Padding = ParseInt(TxtPadding.Text, 0, 32) ?? 2,
            MaxSide = MaxSide(),
            PowerOfTwo = ChkPowerOfTwo.IsChecked == true,
            Trim = ChkTrim.IsChecked == true,
            Format = Format(),
            IncludeSubfolders = InputCard.IncludeSubfolders,
            SplitMode = RadioSplitCell.IsChecked == true ? SpriteSplitMode.ByCellSize : SpriteSplitMode.ByCount,
            Columns = ParseInt(TxtColumns.Text, 1, 1000) ?? 4,
            Rows = ParseInt(TxtRows.Text, 1, 1000) ?? 4,
            CellWidth = ParseInt(TxtCellWidth.Text, 1, SpritePackerManager.MaxSide) ?? 32,
            CellHeight = ParseInt(TxtCellHeight.Text, 1, SpritePackerManager.MaxSide) ?? 32,
            Margin = ParseInt(TxtMargin.Text, 0, 1000) ?? 0,
            Spacing = ParseInt(TxtSpacing.Text, 0, 1000) ?? 0,
            SkipEmpty = ChkSkipEmpty.IsChecked == true,
            NamePrefix = SpritePackerManager.SafeName(TxtPrefix.Text),
            OptionsCollapsed = _optionsCollapsed,
        };

        private void SaveSettings() => SpritePackerManager.SaveSettings(ReadSettings());

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private string Str(string key, string fallback = "") => TryFindResource(key) as string ?? fallback;

        private static int? ParseInt(string? text, int min, int max) =>
            int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max ? n : null;

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

        // =================================================================
        //  Modes
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            SetMode(tag == "Slice" ? SpriteMode.Slice : SpriteMode.Pack);
            ScheduleSave();
        }

        private void SetMode(SpriteMode mode)
        {
            _mode = mode;
            bool pack = mode == SpriteMode.Pack;
            (pack ? RadioModePack : RadioModeSlice).IsChecked = true;
            PackSidebar.Visibility = pack ? Visibility.Visible : Visibility.Collapsed;
            SliceSidebar.Visibility = pack ? Visibility.Collapsed : Visibility.Visible;
            PackContent.Visibility = pack ? Visibility.Visible : Visibility.Collapsed;
            SliceContent.Visibility = pack ? Visibility.Collapsed : Visibility.Visible;
            TxtModeHint.Text = Str(pack ? "Str_SpritePacker_ModeHintPack" : "Str_SpritePacker_ModeHintSlice");
            HeaderTitle.SetResourceReference(ContentControl.ContentProperty, pack ? "Str_SpritePacker_ModePack" : "Str_SpritePacker_ModeSlice");
            HeaderTitle.Tag = pack ? PackIconKind.ViewGridPlusOutline : PackIconKind.ContentCut;
            UiMotion.FadeIn(HeaderTitle);

            // The Output card names one file for a packed sheet, and just a folder for the frames.
            OutputCard.ShowFileName = pack;
            TxtRun.SetResourceReference(TextBlock.TextProperty, pack ? "Str_SpritePacker_SaveSheet" : "Str_SpritePacker_SaveFrames");
            IconRun.Kind = PackIconKind.ContentSaveOutline;
            BtnOpenResult.Visibility = Visibility.Collapsed;
            _lastOutput = null;
            HideStatus();
            UpdateFormatHint();

            if (pack) RunPack();
            else UpdateSlice();
        }

        // =================================================================
        //  Settings that change the result
        // =================================================================

        private int MaxSide() =>
            RadioMax512.IsChecked == true ? 512 : RadioMax1024.IsChecked == true ? 1024 : RadioMax4096.IsChecked == true ? 4096 : RadioMax8192.IsChecked == true ? 8192 : 2048;

        private SpriteCoordFormat Format() =>
            RadioFormatCss.IsChecked == true ? SpriteCoordFormat.Css : RadioFormatXml.IsChecked == true ? SpriteCoordFormat.Xml : RadioFormatNone.IsChecked == true ? SpriteCoordFormat.None : SpriteCoordFormat.Json;

        private void UpdateFormatHint() =>
            TxtFormatHint.Text = Str("Str_SpritePacker_FormatHint_" + Format());

        private void PackSetting_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFormatHint();
            if (sender == RadioFormatJson || sender == RadioFormatCss || sender == RadioFormatXml || sender == RadioFormatNone) { _packFormat = Format(); }
            else SchedulePack();
            ScheduleSave();
        }

        private void PackSetting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool bad = !string.IsNullOrWhiteSpace(TxtPadding.Text) && ParseInt(TxtPadding.Text, 0, 32) == null;
            TxtPaddingError.Visibility = bad ? Visibility.Visible : Visibility.Collapsed;
            if (!bad) SchedulePack();
            ScheduleSave();
        }

        private void PaddingChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string n) TxtPadding.Text = n;
        }

        private void SchedulePack()
        {
            if (!_viewConstructed || _applyingSettings || _mode != SpriteMode.Pack) return;
            _packTimer.Stop();
            _packTimer.Start();
        }

        // =================================================================
        //  Adding images
        // =================================================================

        /// <summary>Adds files and folders: each folder becomes its own row on the Files card, loose files share one.</summary>
        private async void AddSources(IEnumerable<string> paths)
        {
            var all = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            bool include = InputCard.IncludeSubfolders;
            var added = new List<SpriteItem>();
            int inaccessible = 0;

            var loose = all.Where(File.Exists).ToList();
            var folders = all.Where(Directory.Exists).ToList();
            if (loose.Count > 0)
            {
                var scan = await SpritePackerManager.ScanAsync(loose, includeSubfolders: false);
                added.AddRange(SpritePackerManager.AddToList(_items, _groups, scan.Files, null, isFolder: false, includeSubfolders: false));
            }
            foreach (string folder in folders)
            {
                var scan = await SpritePackerManager.ScanAsync(new[] { folder }, include);
                inaccessible += scan.InaccessibleFolders;
                added.AddRange(SpritePackerManager.AddToList(_items, _groups, scan.Files, folder, isFolder: true, includeSubfolders: include));
            }

            if (inaccessible > 0)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_Inaccessible", "{0} folders couldn't be read and were skipped."), inaccessible), StatusKind.Warning);
            else if (added.Count == 0 && all.Count > 0)
                ShowStatus(Str("Str_SpritePacker_NothingAdded", "No new pictures were found there."), StatusKind.Warning);
            else if (_items.Count >= SpritePackerManager.MaxSprites)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_LimitReached", "The list is full ({0} pictures)."), SpritePackerManager.MaxSprites), StatusKind.Warning);
            else HideStatus();

            LoadMeta(added);
            SchedulePack();
        }

        /// <summary>Sizes from the file header and a small thumbnail, a few at a time, so adding a big folder never freezes the page.</summary>
        private async void LoadMeta(IReadOnlyList<SpriteItem> items)
        {
            foreach (var item in items)
            {
                await _metaGate.WaitAsync();
                _ = Task.Run(() =>
                {
                    try
                    {
                        bool ok = SpritePackerManager.TryReadSize(item.FullPath, out int w, out int h);
                        var thumb = ok ? SpritePackerManager.LoadThumbnail(item.FullPath, 64) : null;
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!_items.Contains(item)) return;
                            if (ok)
                            {
                                item.Width = w;
                                item.Height = h;
                                item.Thumbnail = thumb;
                            }
                            else
                            {
                                item.Status = SpriteItemStatus.Failed;
                                item.StatusText = Str("Str_SpritePacker_Problem_ImageUnreadable", "This file couldn't be read as a picture.");
                            }
                        });
                    }
                    finally
                    {
                        _metaGate.Release();
                    }
                });
            }
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            AddSources(e.Paths);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => SaveSettings();

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _items.Clear();
            _groups.Clear();
            ClearPack();
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SpriteGroup group) return;
            SpritePackerManager.RemoveFromList(_items, _groups, group.Items.ToList());
            SchedulePack();
        }

        private async void ChkIncludeSubfolders_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            bool include = InputCard.IncludeSubfolders;
            var added = new List<SpriteItem>();
            foreach (var group in _groups.Where(g => g.IsFolder && g.FolderPath != null).ToList())
            {
                var scan = await SpritePackerManager.ScanAsync(new[] { group.FolderPath! }, include);
                added.AddRange(SpritePackerManager.ApplyRescan(_items, _groups, group, scan.Files, include).Added);
            }
            LoadMeta(added);
            SchedulePack();
            ScheduleSave();
        }

        // ---- drag and drop

        private static List<string> DroppedPaths(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList()
                : new List<string>();

        private void ListArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ListArea_DragLeave(object sender, DragEventArgs e) { }

        private void ListArea_Drop(object sender, DragEventArgs e)
        {
            var paths = DroppedPaths(e);
            if (paths.Count > 0) AddSources(paths);
            e.Handled = true;
        }

        // =================================================================
        //  The list
        // =================================================================

        private void UpdateListState()
        {
            bool any = _items.Count > 0;
            ListEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            InputCard.SetEmpty(_groups.Count == 0);
            BtnMoveUp.IsEnabled = BtnMoveDown.IsEnabled = BtnRemoveSelected.IsEnabled = any;
            BtnSortName.IsEnabled = _items.Count > 1;
            if (_mode == SpriteMode.Pack)
                TxtPillCount.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PillSprites", "{0} sprites"), _items.Count(i => i.IsChecked));
        }

        private List<SpriteItem> SelectedItems() => SpriteList.SelectedItems.Cast<SpriteItem>().ToList();

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void MoveSelected(int direction)
        {
            var chosen = SelectedItems();
            if (SpritePackerManager.Move(_items, chosen, direction))
            {
                foreach (var item in chosen) SpriteList.SelectedItems.Add(item);      // keep the selection after the move
                SchedulePack();
            }
        }

        private void BtnSortName_Click(object sender, RoutedEventArgs e)
        {
            var sorted = SpritePackerManager.SortedByName(_items);
            for (int i = 0; i < sorted.Count; i++)
            {
                int old = _items.IndexOf(sorted[i]);
                if (old != i) _items.Move(old, i);
            }
            SchedulePack();
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            var chosen = SelectedItems();
            if (chosen.Count == 0) return;
            SpritePackerManager.RemoveFromList(_items, _groups, chosen);
            SchedulePack();
        }

        private void SpriteList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveSelected();
            e.Handled = true;
        }

        private void SpriteCheck_Click(object sender, RoutedEventArgs e)
        {
            UpdateListState();
            SchedulePack();
        }

        // =================================================================
        //  Packing (live)
        // =================================================================

        /// <summary>Reads a picture's pixels once and keeps them (until the file changes) so repacking is instant.</summary>
        private byte[]? GetSprite(string path, out int width, out int height, out string problem)
        {
            long length = 0;
            DateTime modified = default;
            try
            {
                var info = new FileInfo(path);
                length = info.Length;
                modified = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(path, out var hit) && hit.Length == length && hit.Modified == modified)
                {
                    (width, height, problem) = (hit.Width, hit.Height, hit.Problem);
                    return hit.Bgra;
                }
            }

            var bgra = SpritePackerManager.LoadBgra(path, out width, out height, out problem);
            lock (_cacheLock)
            {
                if (_cacheBytes > CacheLimitBytes) { _cache.Clear(); _cacheBytes = 0; }
                _cache[path] = new CachedSprite(length, modified, bgra, width, height, problem);
                _cacheBytes += bgra?.Length ?? 0;
            }
            return bgra;
        }

        private sealed record PackJob(List<SpriteItem> Loaded, List<string> Names, List<(int X, int Y, int W, int H)> Trims,
            Dictionary<SpriteItem, string> Failed, SpritePackResult Result);

        private async void RunPack()
        {
            if (!_viewConstructed || _applyingSettings || _mode != SpriteMode.Pack) return;
            _packTimer.Stop();
            int id = ++_packRun;
            UpdateListState();

            var ticked = _items.Where(i => i.IsChecked).ToList();
            if (ticked.Count == 0) { ClearPack(); return; }

            int padding = ParseInt(TxtPadding.Text, 0, 32) ?? 2;
            var options = new SpritePackOptions { Padding = padding, MaxSide = MaxSide(), PowerOfTwo = ChkPowerOfTwo.IsChecked == true };
            bool trim = ChkTrim.IsChecked == true;
            PillWorking.Visibility = Visibility.Visible;

            PackJob job = await Task.Run(() =>
            {
                var loaded = new List<SpriteItem>();
                var sizes = new List<(int W, int H, byte[] Bgra)>();
                var failed = new Dictionary<SpriteItem, string>();
                foreach (var item in ticked)
                {
                    var bgra = GetSprite(item.FullPath, out int w, out int h, out string problem);
                    if (bgra == null) { failed[item] = problem; continue; }
                    loaded.Add(item);
                    sizes.Add((w, h, bgra));
                }
                var names = SpritePackerManager.UniqueNames(loaded.Select(i => SpritePackerManager.SpriteNameFromFile(i.Name)));
                var trims = new List<(int X, int Y, int W, int H)>();
                var inputs = new List<SpritePackInput>();
                for (int i = 0; i < loaded.Count; i++)
                {
                    var (w, h, bgra) = sizes[i];
                    var t = trim ? SpritePackerManager.ComputeTrim(bgra, w, h) : (X: 0, Y: 0, Width: w, Height: h);
                    trims.Add((t.X, t.Y, t.Width, t.Height));
                    inputs.Add(new SpritePackInput(i, names[i], w, h, t.X, t.Y, t.Width, t.Height));
                }
                return new PackJob(loaded, names, trims, failed, SpritePackerManager.Pack(inputs, options));
            });

            if (id != _packRun) return;                   // a newer request overtook this one
            PillWorking.Visibility = Visibility.Collapsed;
            ApplyPack(job, ticked, padding);
        }

        private void ApplyPack(PackJob job, List<SpriteItem> ticked, int padding)
        {
            _pack = job.Result;
            _packItems = job.Loaded;
            _packPadding = padding;
            _packFormat = Format();
            _placementByItem = new Dictionary<SpriteItem, SpritePlacement>();

            foreach (var item in ticked)
            {
                if (job.Failed.TryGetValue(item, out string? problem))
                {
                    item.Status = SpriteItemStatus.Failed;
                    item.StatusText = Str("Str_SpritePacker_Problem_" + problem, problem);
                }
            }
            var tooBig = new HashSet<int>(job.Result.TooBig);
            for (int i = 0; i < job.Loaded.Count; i++)
            {
                var item = job.Loaded[i];
                var (_, _, w, h) = job.Trims[i];
                item.TrimWidth = w;
                item.TrimHeight = h;
                if (tooBig.Contains(i))
                {
                    item.Status = SpriteItemStatus.DoesNotFit;
                    item.StatusText = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_TooBig", "Bigger than the biggest sheet ({0} px). Left out."), MaxSide());
                }
                else if (item.Status != SpriteItemStatus.Failed || job.Loaded.Contains(item))
                {
                    item.Status = SpriteItemStatus.Ready;
                    item.StatusText = "";
                }
            }
            foreach (var page in job.Result.Pages)
                foreach (var p in page.Placements)
                    _placementByItem[job.Loaded[p.Index]] = p;

            _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, job.Result.Pages.Count - 1));
            UpdatePackInfo(job, ticked);
            ShowPage();
        }

        private void UpdatePackInfo(PackJob job, List<SpriteItem> ticked)
        {
            int packed = job.Result.Pages.Sum(p => p.Placements.Count);
            TxtPillCount.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PillSprites", "{0} sprites"), packed);

            var notes = new List<string>();
            if (job.Result.Pages.Count > 0)
                notes.Add(string.Format(CultureInfo.CurrentCulture, Str(job.Result.Pages.Count == 1 ? "Str_SpritePacker_InfoOneSheet" : "Str_SpritePacker_InfoSheets", "{0} sprites on {1} sheet(s)."), packed, job.Result.Pages.Count));
            if (job.Result.TooBig.Count > 0)
                notes.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_InfoTooBig", "{0} too big to fit, left out."), job.Result.TooBig.Count));
            if (job.Failed.Count > 0)
                notes.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_InfoFailed", "{0} couldn't be read, left out."), job.Failed.Count));
            TxtActionInfo.Text = string.Join(" ", notes);
            BtnRun.IsEnabled = job.Result.Pages.Count > 0 && !_busy;
        }

        private void ClearPack()
        {
            _packRun++;
            _pack = null;
            _packItems = new List<SpriteItem>();
            _placementByItem = new Dictionary<SpriteItem, SpritePlacement>();
            SheetImage.Source = null;
            PreviewEmpty.Visibility = Visibility.Visible;
            PillSize.Visibility = PillUsed.Visibility = PillWorking.Visibility = Visibility.Collapsed;
            PageNav.Visibility = Visibility.Collapsed;
            SelectRect.Visibility = HoverRect.Visibility = Visibility.Collapsed;
            TxtHover.Text = "";
            TxtActionInfo.Text = "";
            BtnRun.IsEnabled = false;
            UpdateListState();
        }

        /// <summary>Draws the real packed picture of the chosen sheet (the same pixels that will be saved).</summary>
        private async void ShowPage()
        {
            var pack = _pack;
            if (pack == null || pack.Pages.Count == 0) { ClearPack(); return; }
            _pageIndex = Math.Clamp(_pageIndex, 0, pack.Pages.Count - 1);
            var page = pack.Pages[_pageIndex];
            int run = _packRun, index = _pageIndex;
            var items = _packItems;

            var bgra = await Task.Run(() => SpritePackerManager.ComposeSheet(page, i =>
                GetSprite(items[i].FullPath, out _, out _, out _) ?? new byte[items[i].Width * items[i].Height * 4]));
            if (run != _packRun || index != _pageIndex) return;

            SheetImage.Source = SpritePackerManager.CreateBitmap(bgra, page.Width, page.Height);
            SheetGrid.Width = page.Width;
            SheetGrid.Height = page.Height;
            PreviewEmpty.Visibility = Visibility.Collapsed;

            TxtPillSize.Text = $"{page.Width} × {page.Height}";
            TxtPillUsed.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PillUsed", "{0}% used"), Math.Round(page.UsedPercent));
            PillSize.Visibility = PillUsed.Visibility = Visibility.Visible;
            UiMotion.Pop(PillSize, 0.9, 200);

            PageNav.Visibility = pack.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            TxtPageLabel.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PageLabel", "Sheet {0} of {1}"), _pageIndex + 1, pack.Pages.Count);
            BtnPrevPage.IsEnabled = _pageIndex > 0;
            BtnNextPage.IsEnabled = _pageIndex < pack.Pages.Count - 1;
            UpdateHighlightThickness();
            ShowSelection();
        }

        private void BtnPrevPage_Click(object sender, RoutedEventArgs e) { _pageIndex--; ShowPage(); }
        private void BtnNextPage_Click(object sender, RoutedEventArgs e) { _pageIndex++; ShowPage(); }

        // ---- hover and selection on the preview

        private double SheetScale() => SheetGrid.Width > 0 && SheetViewbox.ActualWidth > 0 ? SheetViewbox.ActualWidth / SheetGrid.Width : 1;

        private void UpdateHighlightThickness()
        {
            double t = Math.Max(1, 2 / Math.Max(SheetScale(), 0.05));
            SelectRect.StrokeThickness = HoverRect.StrokeThickness = t;
        }

        private SpritePlacement? PlacementAt(System.Windows.Point p)
        {
            if (_pack == null || _pageIndex >= _pack.Pages.Count) return null;
            return _pack.Pages[_pageIndex].Placements.FirstOrDefault(s => p.X >= s.X && p.X < s.X + s.Width && p.Y >= s.Y && p.Y < s.Y + s.Height);
        }

        private static void Place(System.Windows.Shapes.Rectangle rect, SpritePlacement p)
        {
            Canvas.SetLeft(rect, p.X);
            Canvas.SetTop(rect, p.Y);
            rect.Width = p.Width;
            rect.Height = p.Height;
            rect.Visibility = Visibility.Visible;
        }

        private void SheetGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (PlacementAt(e.GetPosition(SheetGrid)) is { } p)
            {
                Place(HoverRect, p);
                TxtHover.Text = $"{p.Name}   x {p.X}  y {p.Y}   {p.Width} × {p.Height}" + (p.Trimmed ? "   " + Str("Str_SpritePacker_TrimmedTag", "(trimmed)") : "");
            }
            else
            {
                HoverRect.Visibility = Visibility.Collapsed;
                TxtHover.Text = "";
            }
        }

        private void SheetGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            HoverRect.Visibility = Visibility.Collapsed;
            TxtHover.Text = "";
        }

        private void SheetGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PlacementAt(e.GetPosition(SheetGrid)) is not { } p || p.Index >= _packItems.Count) return;
            var item = _packItems[p.Index];
            SpriteList.SelectedItem = item;
            SpriteList.ScrollIntoView(item);
        }

        private void SpriteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (SpriteList.SelectedItems.Cast<SpriteItem>().FirstOrDefault(i => _placementByItem.ContainsKey(i)) is { } item
                && _placementByItem[item].Page != _pageIndex)
            {
                _pageIndex = _placementByItem[item].Page;
                ShowPage();
                return;
            }
            ShowSelection();
        }

        private void ShowSelection()
        {
            var item = SpriteList.SelectedItems.Cast<SpriteItem>().FirstOrDefault(i => _placementByItem.TryGetValue(i, out var p) && p.Page == _pageIndex);
            if (item != null) Place(SelectRect, _placementByItem[item]);
            else SelectRect.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Slicing
        // =================================================================

        private void SliceSetting_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool byCell = RadioSplitCell.IsChecked == true;
            CountPanel.Visibility = byCell ? Visibility.Collapsed : Visibility.Visible;
            CellPanel.Visibility = byCell ? Visibility.Visible : Visibility.Collapsed;
            ScheduleSlice();
            ScheduleSave();
        }

        private void SliceSetting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleSlice();
            ScheduleSave();
        }

        private void ScheduleSlice()
        {
            if (!_viewConstructed || _applyingSettings || _mode != SpriteMode.Slice) return;
            _sliceTimer.Stop();
            _sliceTimer.Start();
        }

        private SpriteSliceOptions? ReadSliceOptions()
        {
            var columns = ParseInt(TxtColumns.Text, 1, 1000);
            var rows = ParseInt(TxtRows.Text, 1, 1000);
            var cw = ParseInt(TxtCellWidth.Text, 1, SpritePackerManager.MaxSide);
            var ch = ParseInt(TxtCellHeight.Text, 1, SpritePackerManager.MaxSide);
            var margin = ParseInt(TxtMargin.Text, 0, 1000);
            var spacing = ParseInt(TxtSpacing.Text, 0, 1000);
            bool byCell = RadioSplitCell.IsChecked == true;
            bool ok = margin != null && spacing != null && (byCell ? cw != null && ch != null : columns != null && rows != null);
            TxtSliceNumbersError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) return null;
            return new SpriteSliceOptions
            {
                Mode = byCell ? SpriteSplitMode.ByCellSize : SpriteSplitMode.ByCount,
                Columns = columns ?? 1,
                Rows = rows ?? 1,
                CellWidth = cw ?? 1,
                CellHeight = ch ?? 1,
                Margin = margin!.Value,
                Spacing = spacing!.Value,
            };
        }

        private double SliceScale() => _sheetWidth > 0 && SliceViewbox.ActualWidth > 0 ? SliceViewbox.ActualWidth / _sheetWidth : 1;

        private void UpdateSliceThickness() => CellsPath.StrokeThickness = Math.Max(1, 1.5 / Math.Max(SliceScale(), 0.05));

        private void UpdateSlice()
        {
            if (_mode != SpriteMode.Slice) return;
            var example = SpritePackerManager.SliceName(TxtPrefix.Text, 0, 16) + ".png";
            TxtPrefixExample.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PrefixExample", "e.g. {0}"), example);

            _slice = null;
            CellsPath.Data = SkipPath.Data = null;
            BtnRun.IsEnabled = false;
            if (_sheetBgra == null)
            {
                SliceEmpty.Visibility = Visibility.Visible;
                SliceImage.Source = null;
                TxtSliceInfo.Text = "";
                PillSize.Visibility = PillUsed.Visibility = Visibility.Collapsed;
                TxtPillCount.Text = "";
                TxtActionInfo.Text = "";
                return;
            }
            SliceEmpty.Visibility = Visibility.Collapsed;
            if (ReadSliceOptions() is not { } options) { TxtSliceInfo.Text = ""; return; }

            var result = SpritePackerManager.Slice(_sheetWidth, _sheetHeight, options);
            if (!result.Ok)
            {
                TxtSliceInfo.Text = Str("Str_SpritePacker_Problem_" + result.ProblemCode, result.ProblemCode);
                TxtActionInfo.Text = TxtSliceInfo.Text;
                TxtPillCount.Text = "0";
                return;
            }
            _slice = result;

            var cells = new GeometryGroup();
            var skipped = new GeometryGroup();
            int empty = 0;
            bool skipEmpty = ChkSkipEmpty.IsChecked == true;
            foreach (var c in result.Cells)
            {
                var rect = new RectangleGeometry(new Rect(c.X + 0.5, c.Y + 0.5, c.Width - 1, c.Height - 1));
                cells.Children.Add(rect);
                if (SpritePackerManager.IsCellEmpty(_sheetBgra, _sheetWidth, c))
                {
                    empty++;
                    if (skipEmpty) skipped.Children.Add(new RectangleGeometry(new Rect(c.X, c.Y, c.Width, c.Height)));
                }
            }
            cells.Freeze();
            skipped.Freeze();
            CellsPath.Data = cells;
            SkipPath.Data = skipped;
            UpdateSliceThickness();

            int saved = result.Cells.Count - (skipEmpty ? empty : 0);
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SliceGrid", "{0} × {1} cells of {2} × {3} px"), result.Columns, result.Rows, result.CellWidth, result.CellHeight),
            };
            if (result.LeftoverX > 0 || result.LeftoverY > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SliceLeftover", "{0} px right and {1} px bottom are left over."), result.LeftoverX, result.LeftoverY));
            if (empty > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Str(skipEmpty ? "Str_SpritePacker_SliceEmptySkipped" : "Str_SpritePacker_SliceEmptyKept", "{0} empty cells."), empty));
            TxtSliceInfo.Text = string.Join("\n", lines);

            TxtPillCount.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_PillFrames", "{0} frames"), saved);
            TxtPillSize.Text = $"{_sheetWidth} × {_sheetHeight}";
            PillSize.Visibility = Visibility.Visible;
            PillUsed.Visibility = Visibility.Collapsed;
            TxtActionInfo.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_ActionSlice", "{0} files go into a new folder."), saved);
            BtnRun.IsEnabled = saved > 0 && !_busy;
        }

        private void BtnChooseSheet_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Str("Str_SpritePacker_ChooseSheet"),
                Filter = Str("Str_SpritePacker_FilterImages", "Pictures") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp|" + Str("Str_SpritePacker_FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() == true) LoadSheet(dialog.FileName);
        }

        private async void LoadSheet(string path)
        {
            int w = 0, h = 0;
            string problem = "";
            var bgra = await Task.Run(() => SpritePackerManager.LoadBgra(path, out w, out h, out problem));
            if (bgra == null)
            {
                ShowStatus(Str("Str_SpritePacker_Problem_" + problem, problem), StatusKind.Error);
                return;
            }
            HideStatus();
            _sheetBgra = bgra;
            _sheetWidth = w;
            _sheetHeight = h;
            _sheetName = Path.GetFileNameWithoutExtension(path);
            SliceImage.Source = SpritePackerManager.CreateBitmap(bgra, w, h);
            SliceGrid.Width = w;
            SliceGrid.Height = h;
            TxtSheetName.Text = Path.GetFileName(path);
            TxtSheetInfo.Text = $"{w} × {h} px";
            SheetIcon.Kind = PackIconKind.ImageCheckOutline;
            UiMotion.FadeIn(SliceContent);
            UpdateSlice();
        }

        private void SheetDrop_DragOver(object sender, DragEventArgs e)
        {
            bool ok = _mode == SpriteMode.Slice && DroppedPaths(e).Any(File.Exists);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) SheetDropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;
        }

        private void SheetDrop_DragLeave(object sender, DragEventArgs e) => SheetDropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");

        private void SheetDrop_Drop(object sender, DragEventArgs e)
        {
            SheetDropArea.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
            if (_mode == SpriteMode.Slice && DroppedPaths(e).FirstOrDefault(p => File.Exists(p) && SpritePackerManager.IsImage(p)) is { } path) LoadSheet(path);
            e.Handled = true;
        }

        // =================================================================
        //  Save
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _runCts?.Cancel();                              // Stop: finished files are kept
                return;
            }
            if (_mode == SpriteMode.Pack) await SavePackAsync();
            else await SaveSlicesAsync();
        }

        private async Task SavePackAsync()
        {
            var pack = _pack;
            if (pack == null || pack.Pages.Count == 0) return;
            string? path = OutputCard.GetSavePath("PNG|*.png", out bool overwriteAllowed, out string? problem);
            if (path == null)
            {
                if (problem != null) ShowStatus(Str("Str_SpritePacker_CantSave", "Can't save there: ") + problem, StatusKind.Error);
                return;
            }

            var items = _packItems;
            var format = Format();
            int padding = _packPadding;
            SetBusy(true, stoppable: false);
            var result = await Task.Run(() => SpritePackerManager.SavePack(path, overwriteAllowed, pack.Pages,
                i => GetSprite(items[i].FullPath, out _, out _, out _) ?? new byte[items[i].Width * items[i].Height * 4], format, padding));
            SetBusy(false, stoppable: false);

            if (result.Problem != null)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SaveFailed", "Couldn't save: {0}"), result.Problem), StatusKind.Error);
                return;
            }
            HideStatus();
            _lastOutput = result.Files.FirstOrDefault();
            BtnOpenResult.Visibility = _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;
            ShowDone(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SavedFiles", "Saved {0} files"), result.Files.Count));
            OutputCard.Refresh();
        }

        private async Task SaveSlicesAsync()
        {
            var slice = _slice;
            var bgra = _sheetBgra;
            if (slice == null || bgra == null) return;
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Str("Str_SpritePacker_CantSave", "Can't save there: ") + OutputCard.Status.Text, StatusKind.Error);
                return;
            }
            string? folder;
            try
            {
                folder = OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Str("Str_SpritePacker_CantSave", "Can't save there: ") + ex.Message, StatusKind.Error);
                return;
            }
            if (folder == null)
            {
                ShowStatus(Str("Str_SpritePacker_ChooseFolderFirst", "Choose a folder to save into first."), StatusKind.Warning);
                return;
            }

            int total = ChkSkipEmpty.IsChecked == true ? slice.Cells.Count(c => !SpritePackerManager.IsCellEmpty(bgra, _sheetWidth, c)) : slice.Cells.Count;
            var cells = slice.Cells;
            string name = _sheetName, prefix = TxtPrefix.Text;
            bool skip = ChkSkipEmpty.IsChecked == true;
            int width = _sheetWidth;

            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            RunProgress.Value = 0;
            RunProgress.Visibility = Visibility.Visible;
            SetBusy(true, stoppable: true);
            var progress = new Progress<int>(done => RunProgress.Value = total == 0 ? 100 : 100.0 * done / total);

            var result = await Task.Run(() => SpritePackerManager.SaveSlices(folder, name, bgra, width, cells, prefix, skip, progress, token));

            RunProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, stoppable: true);
            _runCts.Dispose();
            _runCts = null;

            if (result.Problem != null)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SaveFailed", "Couldn't save: {0}"), result.Problem), StatusKind.Error);
                return;
            }
            _lastOutput = result.Folder;
            BtnOpenResult.Visibility = Visibility.Visible;
            if (result.Failed > 0)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SpritePacker_SliceSomeFailed", "{0} files saved, {1} couldn't be written."), result.Written, result.Failed), StatusKind.Warning);
            else HideStatus();
            ShowDone(string.Format(CultureInfo.CurrentCulture, Str(result.Cancelled ? "Str_SpritePacker_Stopped" : "Str_SpritePacker_SavedFrames", "Saved {0} files"), result.Written));
            OutputCard.Refresh();
        }

        private void SetBusy(bool busy, bool stoppable)
        {
            _busy = busy;
            OutputCard.IsLocked = busy;
            SidebarStack.IsEnabled = !busy;
            ListArea.IsEnabled = !busy;
            if (stoppable && busy)
            {
                TxtRun.SetResourceReference(TextBlock.TextProperty, "Str_SpritePacker_Stop");
                IconRun.Kind = PackIconKind.Stop;
                BtnRun.IsEnabled = true;
            }
            else
            {
                TxtRun.SetResourceReference(TextBlock.TextProperty, _mode == SpriteMode.Pack ? "Str_SpritePacker_SaveSheet" : "Str_SpritePacker_SaveFrames");
                IconRun.Kind = PackIconKind.ContentSaveOutline;
                BtnRun.IsEnabled = !busy && (_mode == SpriteMode.Pack ? _pack is { Pages.Count: > 0 } : _slice != null);
                if (busy) BtnRun.IsEnabled = false;
            }
        }

        private void BtnOpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutput == null) return;
            try
            {
                if (Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true });
                else if (File.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private void ShowDone(string text)
        {
            TxtDonePill.Text = text;
            DonePill.Visibility = Visibility.Visible;
            UiMotion.Pop(DonePill);
            _doneTimer.Stop();
            _doneTimer.Start();
        }

        private void ShowStatus(string text, StatusKind kind)
        {
            var (bg, fg, icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
            UiMotion.FadeIn(StatusBanner);
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops a running save (finished files stay) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _packRun++;
            _packTimer.Stop();
            _sliceTimer.Stop();
            _doneTimer.Stop();
            _runCts?.Cancel();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
            lock (_cacheLock)
            {
                _cache.Clear();
                _cacheBytes = 0;
            }
        }
    }
}
