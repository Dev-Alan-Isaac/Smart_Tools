using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
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

        private const int PreviewLongSide = 1600;

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
        //  Getting a picture in
        // =================================================================

        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = S("Str_Palette_OpenImage"),
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.jxr;*.wdp|All files|*.*",
            };
            if (dialog.ShowDialog() == true)
                _ = LoadImageFileAsync(dialog.FileName);
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
            catch (Exception) { return false; }
        }

        private void PasteImage()
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
                {
                    image.Freeze();
                    _ = UseBitmapAsync(image, S("Str_Palette_Pasted"));
                    return;
                }

                if (Clipboard.ContainsFileDropList()
                    && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile) is string file)
                {
                    _ = LoadImageFileAsync(file);
                    return;
                }

                SetStatus(S("Str_Palette_NoClipboardImage"), StatusKind.Warning, PackIconKind.ContentPaste);
            }
            catch (Exception)
            {
                SetStatus(S("Str_Palette_NoClipboardImage"), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
        }

        private static bool IsImageFile(string path) =>
            ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        private async Task LoadImageFileAsync(string path)
        {
            int id = ++_loadId;
            SetWorking(true);
            try
            {
                var (preview, pixels, w, h) = await Task.Run(() =>
                {
                    // Open through a stream, never new Uri(path): a '#' or '%' in the name breaks a Uri.
                    BitmapImage bitmap;
                    using (var header = File.OpenRead(path))
                    {
                        var frame = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                        int wide = frame.PixelWidth, high = frame.PixelHeight;

                        bitmap = new BitmapImage();
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
                    }
                    var sampled = PaletteExtractorManager.SamplePixels(bitmap);
                    return (bitmap, sampled.Pixels, sampled.Width, sampled.Height);
                });

                if (id != _loadId) return; // a newer load took over
                AcceptImage(preview, pixels, w, h, Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                if (id != _loadId) return;
                SetWorking(false);
                SetStatus(SF("Str_Palette_LoadFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
        }

        private async Task UseBitmapAsync(BitmapSource bitmap, string name)
        {
            int id = ++_loadId;
            SetWorking(true);
            try
            {
                var (pixels, w, h) = await Task.Run(() => PaletteExtractorManager.SamplePixels(bitmap));
                if (id != _loadId) return;
                AcceptImage(bitmap, pixels, w, h, name);
            }
            catch (Exception ex)
            {
                if (id != _loadId) return;
                SetWorking(false);
                SetStatus(SF("Str_Palette_LoadFailed", ex.Message), StatusKind.Error, PackIconKind.AlertCircleOutline);
            }
        }

        private void AcceptImage(BitmapSource preview, byte[] pixels, int w, int h, string name)
        {
            _pixels = pixels;
            _pixelsWidth = w;
            _pixelsHeight = h;
            _imageName = name;

            PreviewImage.Source = preview;
            EmptyState.Visibility = Visibility.Collapsed;
            UiMotion.FadeIn(PreviewImage);
            TxtImageInfo.Text = $"{name} · {preview.PixelWidth} × {preview.PixelHeight}";

            _ = RunExtractAsync();
        }

        // ---- drag and drop ------------------------------------------------

        private static string? DroppedImage(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(IsImageFile) : null;

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (DroppedImage(e) != null) UiMotion.FadeVisibility(DropHighlight, true);
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedImage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
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
            if (DroppedImage(e) is string file)
                _ = LoadImageFileAsync(file);
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
