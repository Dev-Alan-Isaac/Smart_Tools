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
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Color Picker view - UI only. Conversions, parsing, contrast, batch
    /// and export live in <see cref="ColorManager"/>; Recent / Favorites in
    /// <see cref="ColorHistoryStore"/>; data shapes in ColorModel.cs.
    ///
    /// The picker's source of truth is <see cref="_hsv"/> while dragging;
    /// typed values go RGB -> HSV. Hue (and saturation) are kept when the
    /// color becomes grey or black, so the cursors don't jump back to red.
    /// </summary>
    public partial class ColorPickerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_color-picker_Name", "Color Picker");
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico" };

        private readonly ColorHistoryStore _history = new();

        private HsvColor _hsv = new(0, 0.58, 1.0);
        private RgbColor _current = new(0xFF, 0x6B, 0x6B);

        private bool _optionsCollapsed;
        private bool _isDraggingSv;
        private bool _isDraggingHue;

        // Eyedropper: the loaded image converted once to Bgra32, so pixel
        // reads are a cheap CopyPixels instead of a crop+convert per mouse move.
        private BitmapSource? _pickBitmap;

        private List<BatchColorRow>? _batchRows;
        private readonly bool _viewConstructed;

        public ColorPickerView()
        {
            InitializeComponent();
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_history.Recent.Count == 0 && _history.Favorites.Count == 0)
                _history.Load();

            RebuildHistoryPanels();
            SetColorFromRgb(_current, addToRecent: false);
        }

        // =================================================================
        //  Sidebar + mode
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            ModeTogglePanel.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private bool IsBatchMode => RadioModeBatch.IsChecked == true;

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            bool batch = IsBatchMode;
            UiMotion.FadeVisibility(PanelSingleMode, !batch);
            UiMotion.FadeVisibility(PanelBatchMode, batch);
            UiMotion.FadeVisibility(SingleModeArea, !batch);
            UiMotion.FadeVisibility(BatchModeArea, batch);
            MagnifierPopup.IsOpen = false;
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (ctrl && e.Key == Key.Enter && IsBatchMode)
            {
                e.Handled = true;
                BtnConvertBatch_Click(this, new RoutedEventArgs());
            }
            else if (ctrl && e.Key == Key.V && !IsBatchMode && Keyboard.FocusedElement is not TextBox && Clipboard.ContainsImage())
            {
                e.Handled = true;
                BtnPasteImage_Click(this, new RoutedEventArgs());
            }
        }

        // =================================================================
        //  The one "a color was chosen" pipeline
        // =================================================================

        /// <summary>From the picker (HSV already set).</summary>
        private void ApplyHsv(bool addToRecent)
        {
            _current = ColorManager.HsvToRgb(_hsv);
            RefreshColorUi(addToRecent);
        }

        /// <summary>From a typed value, a swatch or the eyedropper.</summary>
        private void SetColorFromRgb(RgbColor rgb, bool addToRecent = true)
        {
            var hsv = ColorManager.RgbToHsv(rgb);

            // Grey / black have no hue (and black no saturation): keep the
            // previous ones so the cursors stay where the person left them.
            if (hsv.S == 0 || hsv.V == 0) hsv = hsv with { H = _hsv.H };
            if (hsv.V == 0) hsv = hsv with { S = _hsv.S };

            _hsv = hsv;
            _current = rgb;
            RefreshColorUi(addToRecent);
        }

        private void RefreshColorUi(bool addToRecent)
        {
            var hsl = ColorManager.RgbToHsl(_current);

            TxtHex.Text = _current.Hex;
            TxtR.Text = _current.R.ToString(CultureInfo.InvariantCulture);
            TxtG.Text = _current.G.ToString(CultureInfo.InvariantCulture);
            TxtB.Text = _current.B.ToString(CultureInfo.InvariantCulture);
            TxtH.Text = hsl.H.ToString("0", CultureInfo.InvariantCulture);
            TxtS.Text = hsl.S.ToString("0", CultureInfo.InvariantCulture);
            TxtL.Text = hsl.L.ToString("0", CultureInfo.InvariantCulture);
            ClearFieldError();

            var brush = ToBrush(_current);
            PreviewSwatch.Background = brush;
            var ink = ToBrush(ColorManager.BestTextColor(_current));
            TxtPreviewSample.Foreground = ink;
            TxtPreviewHex.Foreground = ink;
            TxtPreviewHex.Text = _current.Hex;

            SvHueLayer.Background = ToBrush(ColorManager.HsvToRgb(_hsv.H, 1, 1));
            PositionCursors();

            UpdateContrast();
            UpdateShades();
            UpdateFavoriteButton();

            if (addToRecent && _history.AddRecent(_current))
                RebuildSwatchPanel(RecentColorsPanel, _history.Recent, isFavorites: false, TxtRecentEmptyHint);
        }

        private static SolidColorBrush ToBrush(RgbColor c)
        {
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        private void UpdateContrast()
        {
            ContrastWhiteTile.Background = PreviewSwatch.Background;
            ContrastBlackTile.Background = PreviewSwatch.Background;

            var onWhite = ColorManager.Contrast(_current, ColorManager.White);
            var onBlack = ColorManager.Contrast(_current, ColorManager.Black);

            TxtContrastWhite.Text = $"Aa  {onWhite.Ratio:0.##}:1";
            TxtContrastBlack.Text = $"Aa  {onBlack.Ratio:0.##}:1";
            SetGrade(GradeWhitePill, TxtGradeWhite, onWhite);
            SetGrade(GradeBlackPill, TxtGradeBlack, onBlack);
        }

        private static void SetGrade(Border pill, TextBlock text, ContrastResult contrast)
        {
            text.Text = contrast.Grade;
            (string bg, string fg) = contrast.PassesAA ? ("StatusSuccessBg", "StatusSuccessFg")
                                   : contrast.PassesAALarge ? ("StatusWarningBg", "StatusWarningFg")
                                   : ("StatusErrorBg", "StatusErrorFg");
            pill.SetResourceReference(Border.BackgroundProperty, bg);
            text.SetResourceReference(TextBlock.ForegroundProperty, fg);
            pill.ToolTip = contrast.Grade switch
            {
                "AAA" => Loc.T("Str_Color_MsgPassAaa", "Passes AAA - fine for any text"),
                "AA" => Loc.T("Str_Color_MsgPassAa", "Passes AA for normal text"),
                "AA Large" => Loc.T("Str_Color_MsgAaLarge", "Only for large or bold text (and icons)"),
                _ => Loc.T("Str_Color_MsgTooLittle", "Too little contrast for text"),
            };
        }

        private void UpdateShades()
        {
            var (shades, originalIndex) = ColorManager.GenerateShades(_current, 9);
            ShadesPanel.Children.Clear();

            for (int i = 0; i < shades.Count; i++)
            {
                var shade = shades[i];
                var button = new Button
                {
                    Background = ToBrush(shade),
                    Tag = shade.Hex,
                    ToolTip = shade.Hex,
                };
                button.SetResourceReference(StyleProperty, "ShadeButtonStyle");
                button.Click += SwatchButton_Click;

                if (i == originalIndex)
                {
                    // Mark the current color with a small dot in its best text color.
                    button.Content = null;
                    button.BorderThickness = new Thickness(0);
                    button.ToolTip = $"{shade.Hex} (current)";
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = ToBrush(ColorManager.BestTextColor(shade)),
                        IsHitTestVisible = false,
                    };
                    var cell = new Grid();
                    cell.Children.Add(button);
                    cell.Children.Add(dot);
                    ShadesPanel.Children.Add(cell);
                }
                else
                {
                    ShadesPanel.Children.Add(button);
                }
            }
        }

        // =================================================================
        //  Picker cursors + dragging
        // =================================================================

        private void PickerArea_SizeChanged(object sender, SizeChangedEventArgs e) => PositionCursors();

        private void PositionCursors()
        {
            if (SvArea.ActualWidth > 0 && SvArea.ActualHeight > 0)
            {
                double x = _hsv.S * SvArea.ActualWidth;
                double y = (1 - _hsv.V) * SvArea.ActualHeight;
                SvCursor.Margin = new Thickness(x - SvCursor.Width / 2, y - SvCursor.Height / 2, 0, 0);
            }

            if (HueArea.ActualWidth > 0)
            {
                double x = _hsv.H / 360.0 * HueArea.ActualWidth;
                HueCursor.Margin = new Thickness(x - HueCursor.Width / 2, -3, 0, 0);
            }
        }

        private void SvArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSv = true;
            SvArea.CaptureMouse();
            UpdateSvFromPoint(e.GetPosition(SvArea));
        }

        private void SvArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSv) UpdateSvFromPoint(e.GetPosition(SvArea));
        }

        private void SvArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingSv) return;
            _isDraggingSv = false;
            SvArea.ReleaseMouseCapture();
            CommitToRecent();
        }

        // If capture is lost mid-drag (Alt-Tab, a dialog), Up never fires - reset.
        private void SvArea_LostMouseCapture(object sender, MouseEventArgs e) => _isDraggingSv = false;

        private void UpdateSvFromPoint(Point p)
        {
            double w = SvArea.ActualWidth, h = SvArea.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _hsv = _hsv with
            {
                S = Math.Clamp(p.X, 0, w) / w,
                V = 1 - Math.Clamp(p.Y, 0, h) / h,
            };
            ApplyHsv(addToRecent: false); // Recent is updated once, on release
        }

        private void HueArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            HueArea.CaptureMouse();
            UpdateHueFromPoint(e.GetPosition(HueArea));
        }

        private void HueArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHue) UpdateHueFromPoint(e.GetPosition(HueArea));
        }

        private void HueArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingHue) return;
            _isDraggingHue = false;
            HueArea.ReleaseMouseCapture();
            CommitToRecent();
        }

        private void HueArea_LostMouseCapture(object sender, MouseEventArgs e) => _isDraggingHue = false;

        private void UpdateHueFromPoint(Point p)
        {
            double w = HueArea.ActualWidth;
            if (w <= 0) return;

            _hsv = _hsv with { H = Math.Clamp(p.X, 0, w) / w * 359.999 };
            ApplyHsv(addToRecent: false);
        }

        private void CommitToRecent()
        {
            if (_history.AddRecent(_current))
                RebuildSwatchPanel(RecentColorsPanel, _history.Recent, isFavorites: false, TxtRecentEmptyHint);
        }

        // =================================================================
        //  Typed values
        // =================================================================

        /// <summary>Enter commits the field it was pressed in.</summary>
        private void ColorField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            if (sender == TxtHex) TxtHex_LostFocus(sender, e);
            else if (sender == TxtR || sender == TxtG || sender == TxtB) RgbField_LostFocus(sender, e);
            else HslField_LostFocus(sender, e);

            if (sender is TextBox box) box.SelectAll();
        }

        /// <summary>The HEX box accepts any notation: #hex, rgb(), hsl() or a CSS name.</summary>
        private void TxtHex_LostFocus(object sender, RoutedEventArgs e)
        {
            string text = TxtHex.Text.Trim();
            if (string.Equals(text, _current.Hex, StringComparison.OrdinalIgnoreCase)) return;

            if (ColorManager.TryParseColor(text, out var rgb, out string error))
            {
                SetColorFromRgb(rgb);
            }
            else
            {
                TxtHex.Text = _current.Hex; // back to the last valid value
                ShowFieldError(HexBar, error);
            }
        }

        private void RgbField_LostFocus(object sender, RoutedEventArgs e)
        {
            bool ok = TryChannel(TxtR, 255, out int r) & TryChannel(TxtG, 255, out int g) & TryChannel(TxtB, 255, out int b);
            if (!ok)
            {
                ShowFieldError(RgbBar, Loc.T("Str_Color_MsgRgbWhole", "RGB values are whole numbers from 0 to 255"));
                return;
            }

            var rgb = new RgbColor((byte)r, (byte)g, (byte)b);
            if (rgb != _current) SetColorFromRgb(rgb);
            else ClearFieldError();
        }

        private void HslField_LostFocus(object sender, RoutedEventArgs e)
        {
            bool ok = TryNumber(TxtH, 360, out double h) & TryNumber(TxtS, 100, out double s) & TryNumber(TxtL, 100, out double l);
            if (!ok)
            {
                ShowFieldError(HslBar, Loc.T("Str_Color_MsgHslRanges", "Hue is 0-360; saturation and lightness 0-100"));
                return;
            }

            var rgb = ColorManager.HslToRgb(new HslColor(h, s, l));
            if (rgb == _current)
            {
                ClearFieldError();
                return;
            }

            // Keep the typed hue even where HSL->RGB would lose it (greys).
            SetColorFromRgb(rgb);
            if (s == 0 || l == 0 || l == 100)
            {
                _hsv = _hsv with { H = h % 360 };
                SvHueLayer.Background = ToBrush(ColorManager.HsvToRgb(_hsv.H, 1, 1));
                PositionCursors();
            }
        }

        private static bool TryChannel(TextBox box, int max, out int value) =>
            int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= max;

        /// <summary>Accepts "12.5" and "12,5" whatever the Windows number format.</summary>
        private static bool TryNumber(TextBox box, double max, out double value) =>
            double.TryParse(box.Text.Trim().TrimEnd('%', '°').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value >= 0 && value <= max;

        private void ShowFieldError(Border bar, string message)
        {
            ClearFieldError();
            bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            TxtFieldError.Text = message;
            UiMotion.FadeVisibility(TxtFieldError, true);
        }

        private void ClearFieldError()
        {
            HexBar.ClearValue(Border.BorderBrushProperty);
            RgbBar.ClearValue(Border.BorderBrushProperty);
            HslBar.ClearValue(Border.BorderBrushProperty);
            TxtFieldError.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Copy
        // =================================================================

        private void BtnCopyHex_Click(object sender, RoutedEventArgs e) => Copy(_current.Hex, BtnCopyHex);
        private void BtnCopyRgb_Click(object sender, RoutedEventArgs e) => Copy(ColorManager.ToCssRgb(_current), BtnCopyRgb);
        private void BtnCopyHsl_Click(object sender, RoutedEventArgs e) => Copy(ColorManager.ToCssHsl(_current), BtnCopyHsl);

        /// <summary>Copies, then briefly swaps the button's icon to a check mark.</summary>
        private static void Copy(string text, Button button)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
                return; // clipboard busy - nothing useful to tell the person
            }

            if (button.Content is not PackIcon icon) return;
            icon.Kind = PackIconKind.Check;
            UiMotion.Pop(button);
            button.ToolTip = $"Copied {text}";

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                icon.Kind = PackIconKind.ContentCopy;
            };
            timer.Start();
        }

        // =================================================================
        //  Recent + favorites
        // =================================================================

        private void BtnAddFavorite_Click(object sender, RoutedEventArgs e)
        {
            _history.ToggleFavorite(_current);
            RebuildSwatchPanel(FavoritesPanel, _history.Favorites, isFavorites: true, TxtFavoritesEmptyHint);
            UpdateFavoriteButton();
            UiMotion.Pop(IconFavorite, 0.6);
        }

        private void UpdateFavoriteButton()
        {
            bool favorite = _history.IsFavorite(_current);
            IconFavorite.Kind = favorite ? PackIconKind.Star : PackIconKind.StarOutline;
            if (favorite)
            {
                IconFavorite.SetResourceReference(ForegroundProperty, "StatusWarningFg");
                TxtFavoriteLabel.SetResourceReference(TextBlock.TextProperty, "Str_Color_InFavorites");
            }
            else
            {
                IconFavorite.ClearValue(ForegroundProperty);
                TxtFavoriteLabel.SetResourceReference(TextBlock.TextProperty, "Str_Color_AddToFavorites");
            }
        }

        private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
        {
            _history.ClearRecent();
            RebuildSwatchPanel(RecentColorsPanel, _history.Recent, isFavorites: false, TxtRecentEmptyHint);
        }

        private void RebuildHistoryPanels()
        {
            RebuildSwatchPanel(RecentColorsPanel, _history.Recent, isFavorites: false, TxtRecentEmptyHint);
            RebuildSwatchPanel(FavoritesPanel, _history.Favorites, isFavorites: true, TxtFavoritesEmptyHint);
        }

        private void RebuildSwatchPanel(WrapPanel panel, IReadOnlyList<string> hexes, bool isFavorites, TextBlock emptyHint)
        {
            if (!isFavorites) BtnClearRecent.IsEnabled = hexes.Count > 0;

            SwatchChips.Rebuild(panel, emptyHint, hexes,
                onClick: (rgb, _) => SetColorFromRgb(rgb, addToRecent: false), // choosing from history shouldn't reshuffle it
                menuItems: (rgb, hex) =>
                {
                    var items = new List<MenuItem>();
                    if (!isFavorites)
                    {
                        items.Add(SwatchChips.Item(SwatchChips.FavoriteHeader(_history, rgb), () =>
                        {
                            _history.ToggleFavorite(rgb);
                            RebuildHistoryPanels();
                            UpdateFavoriteButton();
                        }));
                    }

                    items.Add(SwatchChips.Item(string.Format(SwatchChips.Text("Str_Color_CopyItem"), hex), () =>
                    {
                        try { Clipboard.SetText(hex); } catch (System.Runtime.InteropServices.ExternalException) { }
                    }));
                    items.Add(SwatchChips.Item(SwatchChips.Text("Str_Color_RemoveItem"), () =>
                    {
                        if (isFavorites) _history.RemoveFavorite(hex);
                        else _history.RemoveRecent(hex);
                        RebuildHistoryPanels();
                        UpdateFavoriteButton();
                    }));
                    return items;
                });
        }

        private void SwatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string hex } && ColorManager.TryHexToRgb(hex, out var rgb))
                SetColorFromRgb(rgb, addToRecent: false); // choosing from history shouldn't reshuffle it
        }

        // =================================================================
        //  Eyedropper
        // =================================================================

        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Color_MsgSelectImage", "Select an image"),
                Filter = Loc.T("Str_Color_MsgImageFilter", "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico|All files|*.*"),
            };
            if (dialog.ShowDialog() == true)
                LoadImageFile(dialog.FileName);
        }

        private void LoadImageFile(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                ShowEyedropperImage(bitmap);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_ImageLoad", "Couldn't load that image: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnPasteImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
                {
                    ShowEyedropperImage(image);
                    return;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    string? file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
                    if (file != null)
                    {
                        LoadImageFile(file);
                        return;
                    }
                }

                MessageBox.Show(Loc.T("Str_MB_TheClipboardHasNo", "The clipboard has no image. Copy a screenshot or an image first."), DialogTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_ClipboardRead", "Couldn't read the clipboard: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowEyedropperImage(BitmapSource image)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            _pickBitmap = converted;

            EyedropperImage.Source = image;
            EyedropperEmptyState.Visibility = Visibility.Collapsed;
            UiMotion.FadeIn(EyedropperImage);
        }

        private static bool IsImageFile(string path) =>
            ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        private void EyedropperDropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (DroppedImage(e) != null) UiMotion.FadeVisibility(EyedropperDropHighlight, true);
        }

        private void EyedropperDropArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedImage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void EyedropperDropArea_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(EyedropperDropArea);
            if (p.X <= 0 || p.Y <= 0 || p.X >= EyedropperDropArea.ActualWidth || p.Y >= EyedropperDropArea.ActualHeight)
                UiMotion.FadeVisibility(EyedropperDropHighlight, false);
        }

        private void EyedropperDropArea_Drop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(EyedropperDropHighlight, false);
            if (DroppedImage(e) is string file)
                LoadImageFile(file);
        }

        private static string? DroppedImage(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(IsImageFile) : null;

        private void EyedropperImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_pickBitmap == null || !TryGetHoveredPixel(e.GetPosition(EyedropperImage), out int x, out int y))
                return;

            SetColorFromRgb(ReadPixel(x, y));
            UiMotion.Pop(PreviewSwatch, 0.92);
        }

        private const int MagnifierPixelGridSize = 13; // odd: the picked pixel sits in the middle
        private const double MagnifierDisplaySize = 150;

        private void EyedropperImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_pickBitmap == null) return;

            Point pos = e.GetPosition(EyedropperImage);
            if (!TryGetHoveredPixel(pos, out int x, out int y))
            {
                MagnifierPopup.IsOpen = false;
                return;
            }

            try
            {
                UpdateMagnifier(x, y);
                // Up and to the right of the cursor, so the loupe never hides the target pixel.
                MagnifierPopup.HorizontalOffset = pos.X + 24;
                MagnifierPopup.VerticalOffset = pos.Y - MagnifierDisplaySize - 48;
                MagnifierPopup.IsOpen = true;
            }
            catch (Exception)
            {
                MagnifierPopup.IsOpen = false;
            }
        }

        private void EyedropperImage_MouseLeave(object sender, MouseEventArgs e) => MagnifierPopup.IsOpen = false;

        /// <summary>Control point -> source pixel, accounting for Stretch="Uniform" letterboxing.</summary>
        private bool TryGetHoveredPixel(Point pos, out int pixelX, out int pixelY)
        {
            pixelX = pixelY = 0;
            if (_pickBitmap == null) return false;

            double cw = EyedropperImage.ActualWidth, ch = EyedropperImage.ActualHeight;
            double iw = _pickBitmap.PixelWidth, ih = _pickBitmap.PixelHeight;
            if (cw <= 0 || ch <= 0 || iw <= 0 || ih <= 0) return false;

            double scale = Math.Min(cw / iw, ch / ih);
            double dw = iw * scale, dh = ih * scale;
            double rx = pos.X - (cw - dw) / 2, ry = pos.Y - (ch - dh) / 2;
            if (rx < 0 || ry < 0 || rx >= dw || ry >= dh) return false;

            pixelX = Math.Clamp((int)(rx / scale), 0, (int)iw - 1);
            pixelY = Math.Clamp((int)(ry / scale), 0, (int)ih - 1);
            return true;
        }

        private RgbColor ReadPixel(int x, int y)
        {
            var pixel = new byte[4];
            _pickBitmap!.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return new RgbColor(pixel[2], pixel[1], pixel[0]); // BGRA
        }

        private void UpdateMagnifier(int pixelX, int pixelY)
        {
            var bitmap = _pickBitmap!;
            int size = Math.Max(1, Math.Min(MagnifierPixelGridSize, Math.Min(bitmap.PixelWidth, bitmap.PixelHeight)));
            int half = size / 2;
            int cropX = Math.Clamp(pixelX - half, 0, Math.Max(0, bitmap.PixelWidth - size));
            int cropY = Math.Clamp(pixelY - half, 0, Math.Max(0, bitmap.PixelHeight - size));

            MagnifierImage.Source = new CroppedBitmap(bitmap, new Int32Rect(cropX, cropY, size, size));

            double cell = MagnifierDisplaySize / size;
            MagnifierCrosshair.Width = cell;
            MagnifierCrosshair.Height = cell;
            Canvas.SetLeft(MagnifierCrosshair, (pixelX - cropX) * cell);
            Canvas.SetTop(MagnifierCrosshair, (pixelY - cropY) * cell);

            var color = ReadPixel(pixelX, pixelY);
            MagnifierCrosshair.Stroke = ToBrush(ColorManager.BestTextColor(color));
            MagnifierSwatch.Background = ToBrush(color);
            TxtMagnifierInfo.Text = $"{color.Hex}  ·  {color.R}, {color.G}, {color.B}";
        }

        // =================================================================
        //  Batch
        // =================================================================

        private void TxtBatchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            int lines = ColorManager.SplitLines(TxtBatchInput.Text).Count(l => l.Trim().Length > 0);
            TxtBatchLineCount.Text = lines == 0 ? Loc.T("Str_Color_MsgOnePerLine", "One color per line") : lines == 1 ? Loc.T("Str_Color_MsgLineOne", "1 line") : Loc.F("Str_Color_MsgLineMany", "{0:N0} lines", lines);
        }

        // ---- colors written inside files ----

        private List<string> _scanPaths = new();
        private int _scanRun;

        private void BtnScanFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_Color_MsgScanTitle", "Choose files that contain colors"),
                Filter = Loc.F("Str_Color_MsgScanFilter", "Files with colors|{0}|All files|*.*", ColorFileScanner.DialogPatterns),
                Multiselect = true,
            };
            if (dialog.ShowDialog() == true) _ = ScanAsync(dialog.FileNames);
        }

        private void BtnScanFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Str_Color_MsgScanFolderTitle", "Choose a folder to search for colors"), Multiselect = true };
            if (dialog.ShowDialog() == true) _ = ScanAsync(dialog.FolderNames);
        }

        private void ChkUniqueColors_Click(object sender, RoutedEventArgs e)
        {
            if (_scanPaths.Count > 0) _ = ScanAsync(_scanPaths);
        }

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void BatchArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void BatchArea_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) _ = ScanAsync(paths);
        }

        /// <summary>Reads the files off the UI thread and shows every color found, with the file and line it came from.</summary>
        private async Task ScanAsync(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            bool unique = ChkUniqueColors.IsChecked == true;
            int run = ++_scanRun;
            BtnScanFiles.IsEnabled = BtnScanFolder.IsEnabled = false;
            try
            {
                var result = await Task.Run(() => ColorFileScanner.Scan(list, unique));
                if (run != _scanRun) return;

                _scanPaths = list;
                if (result.Rows.Count == 0)
                {
                    MessageBox.Show(
                        result.FilesRead == 0
                            ? Loc.T("Str_Color_MsgScanNoFiles", "None of those files could be read as text (they may be binary, empty or over 5 MB).")
                            : Loc.F("Str_Color_MsgScanNone", "No colors were found in {0:N0} file(s).", result.FilesRead),
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ShowBatchRows(result.Rows);
                TxtSummaryCount.Text = Loc.F("Str_Color_MsgScanned", "{0:N0} colors in {1:N0} files", result.Rows.Count, result.FilesRead)
                    + (result.Truncated ? " · " + Loc.T("Str_Color_MsgScanTruncated", "list shortened") : "");
                TxtFailedCount.Text = result.FilesSkipped == 1
                    ? Loc.T("Str_Color_MsgScanSkippedOne", "1 file skipped")
                    : Loc.F("Str_Color_MsgScanSkippedMany", "{0:N0} files skipped", result.FilesSkipped);
                FailedPill.Visibility = result.FilesSkipped > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Loc.F("Str_MB_CouldnTReadThat", "Couldn't read that file: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnScanFiles.IsEnabled = BtnScanFolder.IsEnabled = true;
            }
        }

        /// <summary>Puts rows in the results grid and turns Export on.</summary>
        private void ShowBatchRows(List<BatchColorRow> rows)
        {
            _batchRows = rows;
            DataGridBatch.ItemsSource = rows;
            UiMotion.FadeVisibility(BatchEmptyStatePanel, false);
            UiMotion.FadeIn(DataGridBatch, 4);
            SummaryPill.Visibility = Visibility.Visible;
            UiMotion.Pop(SummaryPill);
            BtnExportBatch.IsEnabled = rows.Count > 0;
        }

        private void BtnPasteBatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;

                TxtBatchInput.Text = TxtBatchInput.Text.Trim().Length == 0
                    ? text
                    : TxtBatchInput.Text.TrimEnd() + Environment.NewLine + text;
                TxtBatchInput.ScrollToEnd();
            }
            catch (Exception)
            {
                // clipboard busy
            }
        }

        private void BtnConvertBatch_Click(object sender, RoutedEventArgs e)
        {
            var rows = ColorManager.ConvertBatch(ColorManager.SplitLines(TxtBatchInput.Text));
            if (rows.Count == 0)
            {
                MessageBox.Show(Loc.T("Str_MB_PasteAtLeastOne", "Paste at least one color first - one per line."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                TxtBatchInput.Focus();
                return;
            }

            _scanPaths = new List<string>();   // typed lines replace a file scan
            ShowBatchRows(rows);

            int ok = rows.Count(r => r.Status == ColorRowStatus.Converted);
            int failed = rows.Count - ok;

            TxtSummaryCount.Text = Loc.F("Str_Color_MsgConverted", "{0:N0} of {1:N0} converted", ok, rows.Count);

            TxtFailedCount.Text = failed == 1 ? Loc.T("Str_Color_MsgFailedOne", "1 not recognised") : Loc.F("Str_Color_MsgFailedMany", "{0:N0} not recognised", failed);
            FailedPill.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out ColorExportFormat format)) return;
            if (_batchRows is not { Count: > 0 } rows)
            {
                MessageBox.Show(Loc.T("Str_MB_NothingToExportYet", "Nothing to export yet - click Convert All first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string ext = ColorManager.ExtensionFor(format);
            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_Color_MsgExportTitle", "Export colors"),
                Filter = ColorManager.FileFilterFor(format),
                DefaultExt = ext,
                FileName = "colors" + ext,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                ColorManager.Export(rows, dialog.FileName, format);
                TxtSummaryCount.Text = Loc.F("Str_Color_MsgExported", "Exported {0:N0} rows to {1}", rows.Count, Path.GetFileName(dialog.FileName));
                UiMotion.Pop(SummaryPill);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_MB_CouldnTExport", "Couldn't export: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
