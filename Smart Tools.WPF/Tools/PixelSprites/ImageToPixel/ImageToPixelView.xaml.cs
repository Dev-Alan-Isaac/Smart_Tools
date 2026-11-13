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
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image to Pixel Art - UI only. Pictures (files or whole folders) come in through the Input card; the one you click is
    /// previewed with the current settings, and "Convert all" saves every picture to the chosen folder.
    /// The shrinking, palettes, dithering and the batch are <see cref="ImageToPixelManager"/>'s.
    /// </summary>
    public partial class ImageToPixelView : UserControl
    {
        private readonly ObservableCollection<PixelSource> _sources = new();
        private readonly ObservableCollection<PixelItem> _items = new();
        private readonly SemaphoreSlim _thumbGate = new(3);
        private readonly DispatcherTimer _runTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly bool _viewConstructed;

        private bool _loadedOnce;
        private bool _applying;
        private bool _busy;
        private int _run;
        private int _syncRun;
        private int _loadRun;
        private string? _lastFolder;
        private CancellationTokenSource? _cts;

        // The picture being looked at.
        private PixelItem? _current;
        private byte[]? _bgra;
        private int _width, _height;
        private PixelizeResult? _result;

        public ImageToPixelView()
        {
            InitializeComponent();
            CmbPalette.Items.Add(new ComboBoxItem { Content = Loc.T("Str_ImageToPixel_MsgFromPicture", "From the picture"), Tag = "Auto" });
            foreach (var p in PixelArtManager.Palettes) CmbPalette.Items.Add(new ComboBoxItem { Content = p.Name + " (" + p.Colors.Length + " colors)", Tag = p.Name });
            foreach (int scale in ImageToPixelManager.Scales) CmbScale.Items.Add(new ComboBoxItem { Content = scale + " ×", Tag = scale });

            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.F("Str_ImageToPixel_MsgPicturesFilter", "Pictures|{0}|All files|*.*", ImageToPixelManager.PictureFilter);
            PictureList.ItemsSource = _items;
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            _items.CollectionChanged += (_, _) => UpdateListState();

            _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _runTimer.Tick += (_, _) => { _runTimer.Stop(); _ = ProcessAsync(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
            _viewConstructed = true;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = ImageToPixelManager.Clamp(JsonSettings.Load<ImageToPixelSettings>(ImageToPixelManager.SettingsName));
            _applying = true;
            TxtGrid.Text = s.GridWidth.ToString(CultureInfo.InvariantCulture);
            TxtColors.Text = s.Colors.ToString(CultureInfo.InvariantCulture);
            TxtSuffix.Text = s.Suffix;
            ChkDither.IsChecked = s.Dither;
            InputCard.IncludeSubfolders = s.IncludeSubfolders;
            foreach (ComboBoxItem item in CmbPalette.Items) if ((string)item.Tag == s.Palette) CmbPalette.SelectedItem = item;
            foreach (ComboBoxItem item in CmbScale.Items) if ((int)item.Tag == s.ExportScale) CmbScale.SelectedItem = item;
            _applying = false;
            UpdatePanels();
            UpdateListState();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private ImageToPixelSettings ReadSettings()
        {
            int Num(string t, int fallback) => int.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
            return ImageToPixelManager.Clamp(new ImageToPixelSettings
            {
                GridWidth = Num(TxtGrid.Text, 48),
                Colors = Num(TxtColors.Text, 16),
                Palette = (CmbPalette.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto",
                Dither = ChkDither.IsChecked == true,
                ExportScale = (CmbScale.SelectedItem as ComboBoxItem)?.Tag is int scale ? scale : 8,
                IncludeSubfolders = InputCard.IncludeSubfolders,
                Suffix = TxtSuffix.Text,
            });
        }

        private void SaveSettings() => JsonSettings.Save(ImageToPixelManager.SettingsName, ReadSettings());

        private void UpdatePanels()
        {
            PanelColors.Visibility = ReadSettings().Palette == "Auto" ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            var s = ReadSettings();
            if (_bgra != null)
            {
                int gw = Math.Min(s.GridWidth, _width);
                int gh = Math.Max(1, (int)Math.Round((double)_height * gw / _width));
                TxtGridInfo.Text = Loc.F("Str_ImageToPixel_MsgResultSize", "Result: {0} × {1} pixels", gw, gh);
                TxtSaveInfo.Text = Loc.F("Str_ImageToPixel_MsgSavedSize", "Saved pictures are {0:N0} × {1:N0} px.", gw * s.ExportScale, gh * s.ExportScale);
            }
            else
            {
                TxtGridInfo.Text = "";
                TxtSaveInfo.Text = Loc.F("Str_ImageToPixel_MsgSavedAs", "Saved as PNG, for example \"photo{0}.png\".", s.Suffix);
            }
        }

        private void Changed()
        {
            if (!_viewConstructed || _applying) return;
            UpdatePanels();
            _runTimer.Stop();
            _runTimer.Start();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Option_TextChanged(object sender, TextChangedEventArgs e) => Changed();
        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => Changed();
        private void Option_Click(object sender, RoutedEventArgs e) => Changed();

        

        private void GridText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PresetGrid != null) PresetGrid.Selected = TxtGrid.Text.Trim();
            Changed();
        }
        private void GridPreset_Chosen(object? sender, string size) => TxtGrid.Text = size;

        // =================================================================
        //  Pictures in (the Input card, or dropped on the page)
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => AddSources(e.Paths);

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            Changed();
            _ = ResyncAsync();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _syncRun++;
            _sources.Clear();
            _items.Clear();
            HideStatus();
            ClearPreview();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not PixelSource source) return;
            _sources.Remove(source);
            _ = ResyncAsync();
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddSources(files);
        }

        private void AddSources(IEnumerable<string> paths)
        {
            if (_busy) return;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && ImageToPixelManager.IsPicture(path))) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new PixelSource { FullPath = path, IsFolder = isFolder });
            }
            _ = ResyncAsync();
        }

        /// <summary>Makes the list match the sources: new pictures are added, ones no longer wanted are removed.</summary>
        private async Task ResyncAsync()
        {
            int run = ++_syncRun;
            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, inaccessible) = await Task.Run(() => ImageToPixelManager.FindPictures(paths, include));
            if (run != _syncRun) return;

            bool capped = files.Count > ImageToPixelManager.MaxPictures;
            if (capped) files = files.Take(ImageToPixelManager.MaxPictures).ToList();
            var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            for (int i = _items.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_items[i].FullPath)) _items.RemoveAt(i);

            var have = new HashSet<string>(_items.Select(t => t.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<PixelItem>();
            foreach (string file in files)
            {
                if (have.Contains(file)) continue;
                var item = new PixelItem { FullPath = file };
                _items.Add(item);
                added.Add(item);
            }

            foreach (var source in _sources)
            {
                if (!source.IsFolder) continue;
                string prefix = source.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                source.Count = files.Count(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            InputCard.ItemsSource = null;
            InputCard.ItemsSource = _sources;

            if (inaccessible > 0) ShowStatus(Loc.F("Str_ImageToPixel_MsgInaccessible", "{0} folder(s) couldn't be read and were skipped.", inaccessible), StatusKind.Warning);
            else if (capped) ShowStatus(Loc.F("Str_ImageToPixel_MsgListFull", "The list is full ({0:N0} pictures).", ImageToPixelManager.MaxPictures), StatusKind.Warning);
            else if (_sources.Count > 0 && files.Count == 0) ShowStatus(Loc.T("Str_ImageToPixel_MsgNoneFound", "No pictures were found there."), StatusKind.Warning);
            else HideStatus();

            LoadThumbnails(added);
            if (_items.Count == 0) ClearPreview();
            else if (PictureList.SelectedItem == null) PictureList.SelectedIndex = 0;
        }

        private async void LoadThumbnails(IReadOnlyList<PixelItem> items)
        {
            foreach (var item in items)
            {
                await _thumbGate.WaitAsync();
                _ = Task.Run(() =>
                {
                    try
                    {
                        var thumb = SpritePackerManager.LoadThumbnail(item.FullPath, 76);
                        Dispatcher.InvokeAsync(() => item.Thumbnail = thumb);
                    }
                    finally
                    {
                        _thumbGate.Release();
                    }
                });
            }
        }

        private void UpdateListState()
        {
            ListEmpty.Visibility = _items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtCount.Text = _items.Count == 0 ? "" : $"{_items.Count:N0} picture{(_items.Count == 1 ? "" : "s")}";
            TxtConvert.Text = _busy ? Loc.T("Str_ImageToPixel_MsgStop", "Stop") : _items.Count > 1 ? Loc.F("Str_ImageToPixel_MsgConvertAll", "Convert all {0:N0}", _items.Count) : Loc.T("Str_ImageToPixel_MsgConvert", "Convert");
            if (!_busy) BtnConvertAll.IsEnabled = _items.Count > 0;
        }

        // =================================================================
        //  The picture you click
        // =================================================================

        private async void PictureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PictureList.SelectedItem is not PixelItem item) return;
            int run = ++_loadRun;
            var loaded = await Task.Run(() =>
            {
                var bgra = SpritePackerManager.LoadBgra(item.FullPath, out int w, out int h, out string problem);
                return (bgra, w, h, problem);
            });
            if (run != _loadRun) return;

            if (loaded.bgra == null)
            {
                ClearPreview();
                ShowStatus(loaded.problem == "ImageTooBig" ? Loc.F("Str_ImageToPixel_MsgTooBig", "{0} is too big to open.", item.Name) : Loc.F("Str_ImageToPixel_MsgCantOpen", "{0} couldn't be opened.", item.Name), StatusKind.Error);
                return;
            }
            _current = item;
            _bgra = loaded.bgra;
            _width = loaded.w;
            _height = loaded.h;
            HideStatus();
            OriginalImage.Source = SpritePackerManager.CreateBitmap(_bgra, _width, _height);
            OriginalEmpty.Visibility = Visibility.Collapsed;
            TxtHeroHint.Text = $"{item.Name} · {_width} × {_height} px";
            UpdateInfo();
            await ProcessAsync();
        }

        private void ClearPreview()
        {
            _current = null;
            _bgra = null;
            _result = null;
            OriginalImage.Source = null;
            OriginalEmpty.Visibility = Visibility.Visible;
            ResultBox.Visibility = Visibility.Collapsed;
            PreviewEmpty.Visibility = Visibility.Visible;
            PillSize.Visibility = PillColors.Visibility = Visibility.Collapsed;
            BtnSave.IsEnabled = false;
            TxtHeroHint.Text = Loc.T("Str_ImageToPixel_MsgAddHint", "Add pictures or folders on the left to start.");
            UpdateInfo();
        }

        private async Task ProcessAsync()
        {
            if (_bgra == null) return;
            int run = ++_run;
            var source = _bgra;
            int w = _width, h = _height;
            var settings = ReadSettings();
            var result = await Task.Run(() => ImageToPixelManager.Pixelize(source, w, h, settings));
            if (run != _run) return;

            _result = result;
            ResultImage.Source = SpritePackerManager.CreateBitmap(ImageToPixelManager.ToBgra(result), result.Width, result.Height);
            ResultGrid.Width = result.Width;
            ResultGrid.Height = result.Height;
            ResultBox.Visibility = Visibility.Visible;
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PillSize.Visibility = PillColors.Visibility = Visibility.Visible;
            TxtPillSize.Text = $"{result.Width} × {result.Height} px";
            TxtPillColors.Text = Loc.N(result.ColorsUsed, "Str_ImageToPixel_MsgColorOne", "{0:N0} color", "Str_ImageToPixel_MsgColorMany", "{0:N0} colors", result.ColorsUsed);
            BtnSave.IsEnabled = true;
            UpdateInfo();
        }

        // =================================================================
        //  Saving: this one, or all of them
        // =================================================================

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _current == null) return;
            var settings = ReadSettings();
            var png = ImageToPixelManager.ToPng(_result, settings.ExportScale, out string? problem);
            if (png == null) { ShowStatus(problem ?? Loc.T("Str_ImageToPixel_MsgNoPng", "Couldn't make the PNG."), StatusKind.Error); return; }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Str_ImageToPixel_MsgSaveTitle", "Save pixel art"),
                Filter = Loc.T("Str_ImageToPixel_MsgPngFilter", "PNG picture|*.png"),
                FileName = ImageToPixelManager.OutputName(_current.FullPath, settings.Suffix),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            string? error = SafeFile.WriteAllBytes(dialog.FileName, png);
            if (error != null) { ShowStatus("Couldn't save: " + error, StatusKind.Error); return; }

            HideStatus();
            ShowDone("Saved " + Path.GetFileName(dialog.FileName));
        }

        private async void BtnConvertAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                return;
            }
            if (_items.Count == 0) return;
            if (OutputCard.Status.IsError) { ShowStatus(Loc.F("Str_ImageToPixel_MsgCantSaveThere", "Can't save there: {0}", OutputCard.Status.Text), StatusKind.Error); return; }

            string? folder;
            try
            {
                folder = OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowStatus(Loc.F("Str_ImageToPixel_MsgCantSaveThere", "Can't save there: {0}", ex.Message), StatusKind.Error);
                return;
            }
            if (folder == null) { ShowStatus(Loc.T("Str_ImageToPixel_MsgPickFolder", "Choose a folder to save the pictures into first."), StatusKind.Warning); return; }

            var snapshot = _items.ToList();
            var settings = ReadSettings();
            foreach (var item in snapshot) { item.Status = PixelItemStatus.Pending; item.Message = ""; }
            snapshot[0].Status = PixelItemStatus.Working;
            HideStatus();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            RunProgress.Maximum = snapshot.Count;
            RunProgress.Value = 0;
            var progress = new Progress<(int Index, ImageToPixelManager.PixelItemResult Result)>(p =>
            {
                var item = snapshot[p.Index];
                item.Message = p.Result.Error ?? Path.GetFileName(p.Result.Output ?? "");
                item.Status = p.Result.Error == null ? PixelItemStatus.Done : PixelItemStatus.Failed;
                if (p.Index + 1 < snapshot.Count) snapshot[p.Index + 1].Status = PixelItemStatus.Working;
                RunProgress.Value = p.Index + 1;
            });

            var paths = snapshot.Select(s => s.FullPath).ToList();
            var batch = await Task.Run(() => ImageToPixelManager.ConvertAll(paths, settings, folder, settings.Suffix,
                p => { var b = SpritePackerManager.LoadBgra(p, out int w, out int h, out string problem); return (b, w, h, problem); }, progress, token));
            SetBusy(false);

            foreach (var item in snapshot.Where(s => s.Status == PixelItemStatus.Working || s.Status == PixelItemStatus.Pending)) item.Status = PixelItemStatus.Pending;
            _lastFolder = folder;
            BtnOpenFolder.Visibility = batch.Done > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowDone(batch.Cancelled ? $"Stopped - {batch.Done:N0} saved" : $"Saved {batch.Done:N0} picture{(batch.Done == 1 ? "" : "s")}");
            if (batch.Failed > 0) ShowStatus(Loc.N(batch.Failed, "Str_ImageToPixel_MsgFailedOne", "{0:N0} picture couldn't be converted - the list shows why.", "Str_ImageToPixel_MsgFailedMany", "{0:N0} pictures couldn't be converted - the list shows why.", batch.Failed), StatusKind.Warning);
            OutputCard.Refresh();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            PictureList.IsEnabled = !busy;
            OutputCard.IsLocked = busy;
            RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            IconConvert.Kind = busy ? PackIconKind.Stop : PackIconKind.ImageFilterFrames;
            BtnConvertAll.IsEnabled = busy || _items.Count > 0;
            UpdateListState();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFolder == null || !Directory.Exists(_lastFolder)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Loc.F("Str_ImageToPixel_MsgOpenFolderFailed", "Couldn't open the folder: {0}", ex.Message), StatusKind.Error);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Error, Warning }

        private void ShowDone(string text)
        {
            TxtDonePill.Text = text;
            DonePill.Visibility = Visibility.Visible;
            _doneTimer.Stop();
            _doneTimer.Start();
        }

        private void ShowStatus(string text, StatusKind kind)
        {
            StatusBanner.SetResourceReference(Border.BackgroundProperty, kind == StatusKind.Error ? "StatusErrorBg" : "StatusWarningBg");
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind == StatusKind.Error ? "StatusErrorFg" : "StatusWarningFg");
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops a running conversion (finished files stay) and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _run++;
            _loadRun++;
            _syncRun++;
            _runTimer.Stop();
            _doneTimer.Stop();
            _cts?.Cancel();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
