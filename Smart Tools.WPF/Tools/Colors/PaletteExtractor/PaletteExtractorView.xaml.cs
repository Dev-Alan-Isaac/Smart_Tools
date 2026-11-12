using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using FontFamily = System.Windows.Media.FontFamily;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image Color Palette Extractor. UI only: reads the controls, calls
    /// <see cref="PaletteExtractorManager"/> off the UI thread, and draws the result.
    /// </summary>
    public partial class PaletteExtractorView : UserControl
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".jxr", ".wdp" };

        private const int PreviewLongSide = 900;   // pictures stay in memory while they are listed
        private const int MaxPictures = 40;

        private readonly ObservableCollection<PaletteSource> _sources = new();
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private int _pasteCount;
        private string _note = "";       // appended to the next status line (pictures skipped, list full)

        private readonly ColorHistoryStore _history = new();
        private readonly PaletteExtractOptions _options;
        private readonly DispatcherTimer _debounce;
        private readonly bool _viewConstructed;

        private bool _applying;          // setting controls from stored values
        private bool _settingsLoaded;
        private CancellationTokenSource? _extractCts;
        private int _loadId;             // newest image load; older ones are dropped

        private byte[]? _pixels;
        private int _pixelsWidth, _pixelsHeight;
        private string _imageName = "";

        private PaletteExtractResult? _result;
        private int _selected = -1;

        // Contrast pickers: the palette plus black and white.
        private List<RgbColor> _contrastColors = new();
        private int _foreground, _background;

        public PaletteExtractorView()
        {
            InitializeComponent();

            _options = new PaletteExtractOptions();
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunExtractAsync(); };

            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.F("Str_PaletteExtractor_MsgImageFilter2", "Pictures|{0}|All files|*.*", PictureFilter);
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            InputCard.SetEmpty(true);

            PreviewKeyDown += Root_PreviewKeyDown;
            _viewConstructed = true;
        }

        // =================================================================
        //  Lifetime
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _history.Load(); // favorites are shared with the other colour tools - pick up changes made there
            RebuildFavorites();

            if (_settingsLoaded) return;
            _settingsLoaded = true;

            var stored = PaletteExtractorManager.LoadOptions();
            _applying = true;
            try
            {
                _options.ColorCount = stored.ColorCount;
                _options.Method = stored.Method;
                _options.IgnoreTransparent = stored.IgnoreTransparent;
                _options.IgnoreNearWhite = stored.IgnoreNearWhite;
                _options.IgnoreNearBlack = stored.IgnoreNearBlack;

                CountSlider.Value = _options.ColorCount;
                TxtCountValue.Text = _options.ColorCount.ToString(CultureInfo.CurrentCulture);
                RadioKMeans.IsChecked = _options.Method == PaletteMethod.KMeans;
                RadioMedianCut.IsChecked = _options.Method == PaletteMethod.MedianCut;
                ChkIgnoreTransparent.IsChecked = _options.IgnoreTransparent;
                ChkIgnoreWhite.IsChecked = _options.IgnoreNearWhite;
                ChkIgnoreBlack.IsChecked = _options.IgnoreNearBlack;
                ShowMethodHint();
            }
            finally
            {
                _applying = false;
            }
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelActiveRun();

        /// <summary>Wired in MainWindow.RegisterTools(): stops a running extraction and saves the setup.</summary>
        public void CancelActiveRun()
        {
            _debounce.Stop();
            try { _extractCts?.Cancel(); } catch (ObjectDisposedException) { }
            if (_settingsLoaded) PaletteExtractorManager.SaveOptions(_options);
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = OptionsColumn.Width.Value > 100;
            OptionsColumn.Width = new GridLength(collapse ? 56 : 280);
            OptionsScrollViewer.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            SepHeader.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Strings and small helpers
        // =================================================================

        private string S(string key) => TryFindResource(key) as string ?? key;
        private string SF(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, S(key), args);

        private static Brush Fill(RgbColor c)
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private enum StatusKind { Info, Success, Warning, Error }

        private void SetStatus(string text, StatusKind kind, PackIconKind icon)
        {
            StatusText.Text = text;
            IconStatus.Kind = icon;
            string fg = kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Warning => "StatusWarningFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            };
            string bg = kind switch
            {
                StatusKind.Success => "StatusSuccessBg",
                StatusKind.Warning => "StatusWarningBg",
                StatusKind.Error => "StatusErrorBg",
                _ => "RowBg",
            };
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusPill.SetResourceReference(Border.BackgroundProperty, bg);
            UiMotion.Pop(StatusPill, 0.94);
        }

        private bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                SetStatus(S("Str_Palette_CopyFailed"), StatusKind.Error, PackIconKind.AlertCircleOutline);
                return false;
            }
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void CountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _applying) return;
            _options.ColorCount = (int)Math.Round(CountSlider.Value);
            TxtCountValue.Text = _options.ColorCount.ToString(CultureInfo.CurrentCulture);
            OptionChanged();
        }

        private void Method_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _options.Method = (sender as FrameworkElement)?.Tag as string == "MedianCut" ? PaletteMethod.MedianCut : PaletteMethod.KMeans;
            ShowMethodHint();
            OptionChanged();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _options.IgnoreTransparent = ChkIgnoreTransparent.IsChecked == true;
            _options.IgnoreNearWhite = ChkIgnoreWhite.IsChecked == true;
            _options.IgnoreNearBlack = ChkIgnoreBlack.IsChecked == true;
            OptionChanged();
        }

        private void ShowMethodHint() =>
            TxtMethodHint.Text = S(_options.Method == PaletteMethod.MedianCut ? "Str_Palette_MedianCutHint" : "Str_Palette_KMeansHint");

        /// <summary>Any setting change: save it, then redo the palette ~250 ms after the last change.</summary>
        private void OptionChanged()
        {
            PaletteExtractorManager.SaveOptions(_options);
            _debounce.Stop();
            if (_pixels != null) _debounce.Start();
        }

        // =================================================================
        //  Getting pictures in
        // =================================================================

        private const string PictureFilter = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.jxr;*.wdp";

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => _ = AddPathsAsync(e.Paths);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _sources.Clear();
            _note = "";
            ClearResult();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PaletteSource source) return;
            _sources.Remove(source);
            RefreshCombined();
        }

        private void BtnPasteImage_Click(object sender, RoutedEventArgs e) => PasteImage();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
                && Keyboard.FocusedElement is not TextBox && ClipboardHasPicture())
            {
                PasteImage();
                e.Handled = true;
            }
        }

        private static bool ClipboardHasPicture()
        {
            try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
            catch (System.Runtime.InteropServices.ExternalException) { return false; }
        }

        private void PasteImage()
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
                {
                    image.Freeze();
                    _ = UseBitmapAsync(image);
                    return;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    _ = AddPathsAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
                    return;
                }

                SetStatus(S("Str_Palette_NoClipboardImage"), StatusKind.Warning, PackIconKind.ContentPaste);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                SetStatus(S("Str_Palette_NoClipboardImage"), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
        }

        private static bool IsImageFile(string path) =>
            ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>Files and folders in, pictures out (a folder gives its pictures; subfolders only when the card's box is ticked).</summary>
        private static List<string> ExpandPictures(IEnumerable<string> paths, bool subfolders)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = subfolders, IgnoreInaccessible = true };

            foreach (string path in paths)
            {
                if (found.Count > MaxPictures) break;
                if (Directory.Exists(path))
                {
                    foreach (string file in Directory.EnumerateFiles(path, "*", options).Where(IsImageFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (found.Count > MaxPictures) break;
                        if (seen.Add(file)) found.Add(file);
                    }
                }
                else if (File.Exists(path) && IsImageFile(path) && seen.Add(path))
                {
                    found.Add(path);
                }
            }
            return found;
        }

        private async Task AddPathsAsync(IEnumerable<string> paths)
        {
            var wanted = paths.ToList();
            bool subfolders = InputCard.IncludeSubfolders;
            List<string> files;
            try
            {
                files = await Task.Run(() => ExpandPictures(wanted, subfolders));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(SF("Str_Palette_LoadFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
                return;
            }

            if (files.Count == 0)
            {
                SetStatus(Loc.T("Str_PaletteExtractor_MsgNoPictures", "No pictures were found there."), StatusKind.Warning, PackIconKind.ImageOffOutline);
                return;
            }

            var fresh = new List<PaletteSource>();
            bool full = false;
            foreach (string file in files)
            {
                if (_sources.Concat(fresh).Any(s => string.Equals(s.FullPath, file, StringComparison.OrdinalIgnoreCase))) continue;
                if (_sources.Count + fresh.Count >= MaxPictures) { full = true; break; }
                fresh.Add(new PaletteSource { FullPath = file, Name = Path.GetFileName(file), Subtitle = Loc.T("Str_PaletteExtractor_MsgReading", "Reading…") });
            }
            if (full) _note = Loc.F("Str_PaletteExtractor_MsgListFull", "the list holds {0} pictures at most", MaxPictures);
            if (fresh.Count == 0)
            {
                if (full) SetStatus(_note, StatusKind.Warning, PackIconKind.AlertCircleOutline);
                return;
            }

            foreach (var source in fresh) _sources.Add(source);
            await LoadAsync(fresh);
        }

        /// <summary>Reads each new picture (preview + sampled pixels) one after another, then rebuilds the palette from the whole list.</summary>
        private async Task LoadAsync(IReadOnlyList<PaletteSource> fresh)
        {
            await _loadGate.WaitAsync();
            try
            {
                SetWorking(true);
                int failed = 0;
                string lastError = "";
                foreach (var source in fresh)
                {
                    if (!_sources.Contains(source)) continue;   // taken off the list meanwhile
                    try
                    {
                        var (preview, pixels, w, h) = await Task.Run(() => DecodeFile(source.FullPath));
                        source.Preview = preview;
                        source.Pixels = pixels;
                        source.Subtitle = $"{w} × {h}";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
                    {
                        failed++;
                        lastError = ex.Message;
                        _sources.Remove(source);
                    }
                }

                if (failed > 0)
                    _note = (_note.Length > 0 ? _note + " · " : "") + (failed == 1
                        ? Loc.F("Str_PaletteExtractor_MsgSkippedOne", "1 picture couldn't be read ({0})", lastError)
                        : Loc.F("Str_PaletteExtractor_MsgSkippedMany", "{0} pictures couldn't be read", failed));
                RefreshCombined(fresh.FirstOrDefault(s => s.Pixels != null));
            }
            finally
            {
                _loadGate.Release();
            }
        }

        /// <summary>Preview (long side capped) and the downsized pixels. Opens through a stream: a '#' or '%' in the name breaks a Uri.</summary>
        private static (BitmapSource Preview, byte[] Pixels, int Width, int Height) DecodeFile(string path)
        {
            int wide, high;
            using (var header = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                wide = frame.PixelWidth;
                high = frame.PixelHeight;
            }

            var bitmap = new BitmapImage();
            using var stream = File.OpenRead(path);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            if (Math.Max(wide, high) > PreviewLongSide)
            {
                if (wide >= high) bitmap.DecodePixelWidth = PreviewLongSide;
                else bitmap.DecodePixelHeight = PreviewLongSide;
            }
            bitmap.EndInit();
            bitmap.Freeze();

            var sampled = PaletteExtractorManager.SamplePixels(bitmap);
            return (bitmap, sampled.Pixels, wide, high);
        }

        private async Task UseBitmapAsync(BitmapSource bitmap)
        {
            var source = new PaletteSource
            {
                FullPath = "",
                Name = _sources.Any(s => s.FullPath.Length == 0)
                    ? S("Str_Palette_Pasted") + " " + (++_pasteCount + 1).ToString(CultureInfo.CurrentCulture)
                    : S("Str_Palette_Pasted"),
                Subtitle = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}",
            };
            if (_sources.Count >= MaxPictures)
            {
                SetStatus(Loc.F("Str_PaletteExtractor_MsgListFull", "the list holds {0} pictures at most", MaxPictures), StatusKind.Warning, PackIconKind.AlertCircleOutline);
                return;
            }

            await _loadGate.WaitAsync();
            try
            {
                SetWorking(true);
                var (pixels, _, _) = await Task.Run(() => PaletteExtractorManager.SamplePixels(bitmap));
                source.Preview = bitmap;
                source.Pixels = pixels;
                _sources.Add(source);
                RefreshCombined(source);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                SetWorking(false);
                SetStatus(SF("Str_Palette_LoadFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private int _current;   // index (among the read pictures) of the one whose palette is shown

        private List<PaletteSource> ReadySources() => _sources.Where(s => s.Pixels != null).ToList();

        /// <summary>Shows one picture and its own palette. <paramref name="focus"/>: jump to this picture (e.g. the one just added).</summary>
        private void RefreshCombined(PaletteSource? focus = null)
        {
            var ready = ReadySources();
            if (ready.Count == 0)
            {
                ClearResult();
                return;
            }

            if (focus != null && ready.IndexOf(focus) is var at and >= 0) _current = at;
            _current = Math.Clamp(_current, 0, ready.Count - 1);
            var source = ready[_current];

            EmptyState.Visibility = Visibility.Collapsed;
            PreviewImage.Source = source.Preview;
            UiMotion.FadeIn(PreviewImage);
            UpdateNavigation(ready);

            _pixels = source.Pixels;
            _pixelsWidth = source.Pixels!.Length / 4;
            _pixelsHeight = 1;
            _imageName = source.Name;

            _ = RunExtractAsync();
        }

        private void UpdateNavigation(IReadOnlyList<PaletteSource> ready)
        {
            PictureNav.Visibility = ready.Count > 1 ? Visibility.Visible : Visibility.Hidden;   // hidden keeps its space, so nothing jumps
            BtnPrevPicture.IsEnabled = _current > 0;
            BtnNextPicture.IsEnabled = _current < ready.Count - 1;
            TxtPictureName.Text = ready[_current].Name;
            TxtPictureName.ToolTip = ready[_current].Name;
            TxtPictureIndex.Text = $"{_current + 1} / {ready.Count}";
        }

        private void BtnPrevPicture_Click(object sender, RoutedEventArgs e) => Step(-1);
        private void BtnNextPicture_Click(object sender, RoutedEventArgs e) => Step(1);

        private void Step(int by)
        {
            int count = ReadySources().Count;
            if (count < 2 || _current + by < 0 || _current + by >= count) return;
            _current += by;
            RefreshCombined();
        }

        /// <summary>Nothing listed: back to the empty page.</summary>
        private void ClearResult()
        {
            try { _extractCts?.Cancel(); } catch (ObjectDisposedException) { }
            _pixels = null;
            _result = null;
            _selected = -1;
            _imageName = "";

            PreviewImage.Source = null;
            PictureNav.Visibility = Visibility.Hidden;
            _current = 0;
            EmptyState.Visibility = Visibility.Visible;
            ShareBar.Children.Clear();
            ShareBar.ColumnDefinitions.Clear();
            SwatchPanel.Children.Clear();
            ExportPanel.Visibility = Visibility.Collapsed;
            SelectedPanel.Visibility = Visibility.Collapsed;
            VariantsPanel.Visibility = Visibility.Collapsed;
            TxtVariantsHint.Visibility = Visibility.Visible;
            ContrastPanel.Visibility = Visibility.Collapsed;
            TxtContrastHint.Visibility = Visibility.Visible;
            SetWorking(false);
            SetStatus(S("Str_Palette_Ready"), StatusKind.Info, PackIconKind.InformationOutline);
        }

        // ---- drag and drop (the big picture area takes several files or folders) ----

        private static string[] DroppedPaths(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(f => Directory.Exists(f) || IsImageFile(f)).ToArray()
                : Array.Empty<string>();

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (DroppedPaths(e).Length > 0) UiMotion.FadeVisibility(DropHighlight, true);
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedPaths(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(DropArea);
            if (p.X <= 0 || p.Y <= 0 || p.X >= DropArea.ActualWidth || p.Y >= DropArea.ActualHeight)
                UiMotion.FadeVisibility(DropHighlight, false);
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropHighlight, false);
            string[] paths = DroppedPaths(e);
            if (paths.Length > 0) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        // =================================================================
        //  Extraction
        // =================================================================

        private void SetWorking(bool working) =>
            WorkingBar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;

        private async Task RunExtractAsync()
        {
            if (_pixels == null) return;

            try { _extractCts?.Cancel(); } catch (ObjectDisposedException) { }
            var cts = new CancellationTokenSource();
            _extractCts = cts;

            byte[] pixels = _pixels;
            int w = _pixelsWidth, h = _pixelsHeight;
            var options = new PaletteExtractOptions
            {
                ColorCount = _options.ColorCount,
                Method = _options.Method,
                IgnoreTransparent = _options.IgnoreTransparent,
                IgnoreNearWhite = _options.IgnoreNearWhite,
                IgnoreNearBlack = _options.IgnoreNearBlack,
            };

            SetWorking(true);
            try
            {
                var result = await Task.Run(() => PaletteExtractorManager.Extract(pixels, w, h, options, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                // a newer run (or leaving the page) took over
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                SetStatus(SF("Str_Palette_ExtractFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
            finally
            {
                if (ReferenceEquals(_extractCts, cts)) SetWorking(false);
            }
        }

        // =================================================================
        //  Showing the result
        // =================================================================

        private void ShowResult(PaletteExtractResult result)
        {
            _result = result;
            _selected = -1;

            ShareBar.Children.Clear();
            ShareBar.ColumnDefinitions.Clear();
            SwatchPanel.Children.Clear();

            if (result.NothingLeft)
            {
                ExportPanel.Visibility = Visibility.Collapsed;
                SelectedPanel.Visibility = Visibility.Collapsed;
                VariantsPanel.Visibility = Visibility.Collapsed;
                TxtVariantsHint.Visibility = Visibility.Visible;
                ContrastPanel.Visibility = Visibility.Collapsed;
                TxtContrastHint.Visibility = Visibility.Visible;
                SetStatus(S("Str_Palette_NothingLeft"), StatusKind.Warning, PackIconKind.FilterOffOutline);
                return;
            }

            for (int i = 0; i < result.Colors.Count; i++)
            {
                var c = result.Colors[i];

                ShareBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(c.Share, 0.0001), GridUnitType.Star) });
                var seg = new Border { Background = Fill(c.Color), ToolTip = $"{c.Hex} · {c.SharePercent}" };
                Grid.SetColumn(seg, i);
                ShareBar.Children.Add(seg);

                SwatchPanel.Children.Add(MakeSwatch(c, i));
            }
            SwatchPanel.Columns = result.Colors.Count <= 4 ? 2 : result.Colors.Count <= 9 ? 3 : 4;

            ExportPanel.Visibility = Visibility.Visible;
            UiMotion.FadeIn(SwatchPanel);

            string status = SF("Str_Palette_StatusColors", result.Colors.Count, result.CountedPixels.ToString("N0", CultureInfo.CurrentCulture));
            if (result.SkippedPixels > 0)
                status += " · " + SF("Str_Palette_StatusSkipped", result.SkippedPixels.ToString("N0", CultureInfo.CurrentCulture));
            if (_note.Length > 0) { status += " · " + _note; _note = ""; }
            SetStatus(status, StatusKind.Success, PackIconKind.CheckCircleOutline);

            BuildContrastPickers(result);
            Select(0);
        }

        private Button MakeSwatch(PaletteColor c, int index)
        {
            var text = Fill(c.TextColor);
            var panel = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = c.Hex,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = text,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = c.SharePercent,
                FontSize = 11,
                Foreground = text,
                Opacity = 0.85,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });

            var button = new Button
            {
                MinWidth = 72,
                MinHeight = 56,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Fill(c.Color),
                Content = panel,
                Tag = index,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Style = (Style)FindResource("PaletteTileStyle"),
                ToolTip = S("Str_Palette_SwatchTooltip"),
            };
            button.Click += Swatch_Click;

            var favorite = new MenuItem();
            favorite.Click += (_, _) =>
            {
                _history.ToggleFavorite(c.Color);
                RebuildFavorites();
            };
            var menu = new ContextMenu();
            menu.Opened += (_, _) => favorite.Header = _history.IsFavorite(c.Color) ? S("Str_Color_RemoveFromFavorites") : S("Str_Color_MenuAddToFavorites");
            menu.Items.Add(favorite);
            button.ContextMenu = menu;
            return button;
        }

        // ---- favorites ------------------------------------------------------

        private void RebuildFavorites() =>
            SwatchChips.Rebuild(FavoritesPanel, TxtFavoritesEmptyHint, _history.Favorites,
                onClick: (_, hex) =>
                {
                    if (TryCopy(hex)) SetStatus(SF("Str_Palette_Copied", hex), StatusKind.Success, PackIconKind.ContentCopy);
                },
                menuItems: (_, hex) => new[]
                {
                    SwatchChips.Item(S("Str_Color_RemoveFromFavorites"), () =>
                    {
                        _history.RemoveFavorite(hex);
                        RebuildFavorites();
                    }),
                });

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: int index } || _result == null) return;
            Select(index);
            string hex = _result.Colors[index].Hex;
            if (TryCopy(hex))
                SetStatus(SF("Str_Palette_Copied", hex), StatusKind.Success, PackIconKind.ContentCopy);
        }

        private void Select(int index)
        {
            if (_result == null || index < 0 || index >= _result.Colors.Count) return;
            _selected = index;
            var c = _result.Colors[index];

            for (int i = 0; i < SwatchPanel.Children.Count; i++)
            {
                if (SwatchPanel.Children[i] is not Button b) continue;
                if (i == index) b.SetResourceReference(Button.BorderBrushProperty, "AccentColor");
                else b.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }

            SelectedSwatch.Background = Fill(c.Color);
            TxtSelectedHex.Text = c.Hex;
            TxtSelectedDetail.Text = $"{ColorManager.ToCssRgb(c.Color)}  {ColorManager.ToCssHsl(c.Color)}";
            TxtSelectedText.Text = SF("Str_Palette_ReadableText",
                c.TextColor.R == 0 ? S("Str_Palette_Black") : S("Str_Palette_White"),
                c.TextContrast.ToString("0.0", CultureInfo.CurrentCulture));
            SelectedPanel.Visibility = Visibility.Visible;

            BuildVariants(c.Color);
        }

        // ---- related palettes ---------------------------------------------

        private void BuildVariants(RgbColor color)
        {
            VariantsPanel.Children.Clear();

            (PaletteVariantKind Kind, string Key)[] rows =
            {
                (PaletteVariantKind.LightDark, "Str_Palette_LightDark"),
                (PaletteVariantKind.Complementary, "Str_Palette_Complementary"),
                (PaletteVariantKind.Analogous, "Str_Palette_Analogous"),
                (PaletteVariantKind.Triadic, "Str_Palette_Triadic"),
            };

            foreach (var (kind, key) in rows)
            {
                var colors = PaletteExtractorManager.BuildVariant(color, kind);

                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

                var copy = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("NeutralIconButtonStyle"),
                    ToolTip = S("Str_Palette_CopyRowTooltip"),
                    Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 14, Height = 14 },
                };
                var captured = colors;
                copy.Click += (_, _) =>
                {
                    if (TryCopy(PaletteExtractorManager.ToHexList(captured)))
                        SetStatus(S("Str_Palette_CopiedRow"), StatusKind.Success, PackIconKind.ContentCopy);
                };
                DockPanel.SetDock(copy, Dock.Right);
                line.Children.Add(copy);

                var label = new TextBlock
                {
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                    Text = S(key),
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                DockPanel.SetDock(label, Dock.Left);
                line.Children.Add(label);

                var tiles = new WrapPanel();
                foreach (var v in colors)
                {
                    var hex = v.Hex;
                    var tile = new Button
                    {
                        Width = 84,
                        Height = 44,
                        Margin = new Thickness(0, 0, 8, 0),
                        Background = Fill(v),
                        BorderBrush = System.Windows.Media.Brushes.Transparent,
                        Style = (Style)FindResource("PaletteTileStyle"),
                        ToolTip = S("Str_Palette_SwatchTooltip"),
                        Content = new TextBlock
                        {
                            Text = hex,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 11.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Fill(ColorManager.BestTextColor(v)),
                        },
                    };
                    tile.Click += (_, _) =>
                    {
                        if (TryCopy(hex)) SetStatus(SF("Str_Palette_Copied", hex), StatusKind.Success, PackIconKind.ContentCopy);
                    };
                    tiles.Children.Add(tile);
                }
                line.Children.Add(tiles);

                VariantsPanel.Children.Add(line);
            }

            TxtVariantsHint.Visibility = Visibility.Collapsed;
            VariantsPanel.Visibility = Visibility.Visible;
            UiMotion.FadeIn(VariantsPanel);
        }

        // ---- contrast ------------------------------------------------------

        private void BuildContrastPickers(PaletteExtractResult result)
        {
            _contrastColors = result.Colors.Select(c => c.Color).ToList();
            if (!_contrastColors.Contains(ColorManager.White)) _contrastColors.Add(ColorManager.White);
            if (!_contrastColors.Contains(ColorManager.Black)) _contrastColors.Add(ColorManager.Black);

            _background = 0;
            _foreground = _contrastColors.IndexOf(result.Colors[0].TextColor);

            TxtContrastHint.Visibility = Visibility.Collapsed;
            ContrastPanel.Visibility = Visibility.Visible;
            RefreshContrast();
        }

        private void RefreshContrast()
        {
            FillPicker(ForegroundPanel, _foreground, i => { _foreground = i; RefreshContrast(); });
            FillPicker(BackgroundPanel, _background, i => { _background = i; RefreshContrast(); });

            if (_foreground < 0 || _foreground >= _contrastColors.Count || _background < 0 || _background >= _contrastColors.Count)
                return;

            var fg = _contrastColors[_foreground];
            var bg = _contrastColors[_background];
            var contrast = ColorManager.Contrast(fg, bg);

            SampleBorder.Background = Fill(bg);
            TxtSampleLarge.Foreground = Fill(fg);
            TxtSampleSmall.Foreground = Fill(fg);
            TxtRatio.Text = contrast.Ratio.ToString("0.00", CultureInfo.CurrentCulture);

            // Large text (18 pt+, or 14 pt bold) only needs 3:1 for AA and 4.5:1 for AAA.
            SetPill(PillAaNormal, TxtAaNormal, "AA", S("Str_Palette_NormalText"), contrast.PassesAA);
            SetPill(PillAaaNormal, TxtAaaNormal, "AAA", S("Str_Palette_NormalText"), contrast.PassesAAA);
            SetPill(PillAaLarge, TxtAaLarge, "AA", S("Str_Palette_LargeText"), contrast.PassesAALarge);
            SetPill(PillAaaLarge, TxtAaaLarge, "AAA", S("Str_Palette_LargeText"), contrast.Ratio >= 4.5);
        }

        private void SetPill(Border pill, TextBlock text, string level, string kind, bool pass)
        {
            text.Text = $"{level} · {kind} · {S(pass ? "Str_Palette_Pass" : "Str_Palette_Fail")}";
            pill.SetResourceReference(Border.BackgroundProperty, pass ? "StatusSuccessBg" : "StatusErrorBg");
            text.SetResourceReference(TextBlock.ForegroundProperty, pass ? "StatusSuccessFg" : "StatusErrorFg");
        }

        private void FillPicker(WrapPanel panel, int selected, Action<int> choose)
        {
            panel.Children.Clear();
            for (int i = 0; i < _contrastColors.Count; i++)
            {
                int index = i;
                var color = _contrastColors[i];
                var button = new Button
                {
                    Width = 30,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 6),
                    Background = Fill(color),
                    Style = (Style)FindResource("PaletteTileStyle"),
                    ToolTip = color.Hex,
                };
                if (i == selected) button.SetResourceReference(Button.BorderBrushProperty, "AccentColor");
                else button.BorderBrush = System.Windows.Media.Brushes.Transparent;
                button.Click += (_, _) => choose(index);
                panel.Children.Add(button);
            }
        }

        // =================================================================
        //  Copy and save
        // =================================================================

        private IReadOnlyList<PaletteColor>? CurrentColors =>
            _result is { NothingLeft: false } ? _result.Colors : null;

        private void BtnCopyHex_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentColors is not { } colors) return;
            if (TryCopy(PaletteExtractorManager.ToHexList(colors.Select(c => c.Color))))
                SetStatus(SF("Str_Palette_CopiedCount", colors.Count), StatusKind.Success, PackIconKind.ContentCopy);
        }

        private void BtnCopyCss_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentColors is not { } colors) return;
            if (TryCopy(PaletteExtractorManager.ToCss(colors.Select(c => c.Color))))
                SetStatus(S("Str_Palette_CopiedCss"), StatusKind.Success, PackIconKind.ContentCopy);
        }

        private void BtnSaveJson_Click(object sender, RoutedEventArgs e) => Save(PaletteExportFormat.Json);
        private void BtnSavePng_Click(object sender, RoutedEventArgs e) => Save(PaletteExportFormat.Png);

        private void Save(PaletteExportFormat format)
        {
            if (CurrentColors is not { } colors) return;

            string baseName = Path.GetFileNameWithoutExtension(_imageName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "image";

            var dialog = new SaveFileDialog
            {
                Title = S(format == PaletteExportFormat.Png ? "Str_Palette_SavePng" : "Str_Palette_SaveJson"),
                Filter = PaletteExtractorManager.FileFilterFor(format),
                FileName = SafeFileName($"palette {baseName}") + PaletteExtractorManager.ExtensionFor(format),
                AddExtension = true,
                DefaultExt = PaletteExtractorManager.ExtensionFor(format),
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                if (format == PaletteExportFormat.Png) PaletteExtractorManager.SaveStripPng(colors, dialog.FileName);
                else PaletteExtractorManager.WriteText(dialog.FileName, PaletteExtractorManager.ToJson(colors));
                SetStatus(SF("Str_Palette_Saved", Path.GetFileName(dialog.FileName)), StatusKind.Success, PackIconKind.ContentSaveOutline);
            }
            catch (Exception ex)
            {
                SetStatus(SF("Str_Palette_SaveFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return name.Trim();
        }
    }
}
