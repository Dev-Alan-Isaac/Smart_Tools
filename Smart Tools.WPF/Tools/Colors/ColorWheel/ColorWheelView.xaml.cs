using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Color Wheel view - UI only. Wheel, harmonies and gradients come from
    /// <see cref="ColorWheelManager"/>; conversions from <see cref="ColorManager"/>.
    ///
    /// Click a wedge (on the wheel or in the legend) to select it: it
    /// becomes the harmony's base colour and the gradient start.
    /// Right-click sets the gradient end instead.
    /// </summary>
    public partial class ColorWheelView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int WheelExportSize = 1024;
        private const double WheelMinSize = 220;
        private const double WheelGap = 24;              // the spacer column between the wheel and its details
        private const double WheelDetailsMinWidth = 360;
        private const int GradientPreviewWidth = 1200;
        private const int GradientPreviewHeight = 72;

        private readonly ColorHistoryStore _history = new();
        private List<WheelSwatch> _wheel = new();
        private List<RgbColor> _gradient = new();
        private List<RgbColor> _harmony = new();

        private int _selectedIndex;          // wedge selected on the wheel
        private RgbColor _gradientStart = new(255, 0, 0);
        private RgbColor _gradientEnd = new(0, 0, 255);
        private ColorHarmony _harmonyKind = ColorHarmony.Complementary;

        private bool _optionsCollapsed;
        private bool _settingGradientText;
        private int _hoverIndex = -1;
        private DispatcherTimer? _statusTimer;
        private readonly bool _viewConstructed;

        private enum StatusKind { Neutral, Success, Error }

        public ColorWheelView()
        {
            InitializeComponent();
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _history.Load(); // favorites are shared with the other colour tools - pick up changes made there
            RebuildFavorites();
            TxtHarmonyHint.Text = ColorWheelManager.Describe(_harmonyKind);
            SizeWheelHost();
            RefreshWheel();
            RefreshGradient();
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

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Wheel
        // =================================================================

        private void WheelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtSaturationValue.Text = $"{SaturationSlider.Value:P0}";
            TxtBrightnessValue.Text = $"{BrightnessSlider.Value:P0}";
            RefreshWheel();
        }

        private void BtnResetWheel_Click(object sender, RoutedEventArgs e)
        {
            SaturationSlider.Value = 1;
            BrightnessSlider.Value = 1;
        }

        private void RefreshWheel()
        {
            if (!IsLoaded) return;

            _wheel = ColorWheelManager.GetIttenWheel(SaturationSlider.Value, BrightnessSlider.Value);
            RenderWheelImage();
            RebuildLegend();
            ShowSelection(animate: false);
        }

        /// <summary>The wheel grows with the window: as big as the card's free height allows, leaving the details column its minimum width.</summary>
        private void WheelCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsLoaded && SizeWheelHost() && _wheel.Count > 0) RenderWheelImage();
        }

        /// <summary>Sets the wheel's size (a multiple of 8, so dragging the window edge doesn't redraw it for every pixel). True when it changed.</summary>
        private bool SizeWheelHost()
        {
            double heightRoom = WheelCard.ActualHeight - WheelHoverPill.ActualHeight - WheelHoverPill.Margin.Top;
            double widthRoom = WheelCard.ActualWidth - WheelGap - WheelDetailsMinWidth;
            double room = Math.Min(heightRoom, widthRoom);
            if (double.IsNaN(room) || room <= 0) return false;

            double size = Math.Max(WheelMinSize, Math.Floor(room / 8) * 8);
            if (Math.Abs(WheelHost.Width - size) < 0.5) return false;
            WheelHost.Width = size;
            WheelHost.Height = size;
            return true;
        }

        /// <summary>Renders at the screen's real pixel density so the wheel stays crisp at 125-200% scaling.</summary>
        private void RenderWheelImage()
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int pixels = (int)Math.Round(WheelHost.Width * dpi);
            WheelImage.Source = ColorWheelManager.RenderWheel(_wheel, pixels, _selectedIndex);
        }

        private void RebuildLegend()
        {
            WheelLegendPanel.Children.Clear();

            foreach (var swatch in _wheel)
            {
                var ink = ToBrush(ColorManager.BestTextColor(swatch.Color));
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = swatch.Name,
                    FontSize = 10.5,
                    FontWeight = swatch.Tier == WheelTier.Primary ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = ink,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                content.Children.Add(new TextBlock
                {
                    Text = swatch.Hex,
                    FontSize = 9.5,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = ink,
                    Opacity = 0.85,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                });

                var chip = new Button
                {
                    Content = content,
                    Background = ToBrush(swatch.Color),
                    Padding = new Thickness(4, 6, 4, 6),
                    Tag = swatch.Index,
                    ToolTip = $"{swatch.Name} - {swatch.Hex} ({swatch.TierName})\n{TryString("Str_ColorWheel_WheelSwatchTooltip")}",
                    BorderBrush = swatch.Index == _selectedIndex
                        ? (System.Windows.Media.Brush)FindResource("AccentColor")
                        : System.Windows.Media.Brushes.Transparent,
                };
                chip.SetResourceReference(StyleProperty, "ColorTileStyle");
                chip.Click += (_, _) => SelectWedge(swatch.Index, setGradientStart: true);

                var setStart = new MenuItem { Header = TryString("Str_ColorWheel_SetAsStart", "Set as gradient start") };
                setStart.Click += (_, _) => SetGradientColor(isStart: true, swatch.Color);
                var setEnd = new MenuItem { Header = TryString("Str_ColorWheel_SetAsEnd", "Set as gradient end") };
                setEnd.Click += (_, _) => SetGradientColor(isStart: false, swatch.Color);
                var copy = new MenuItem { Header = $"Copy {swatch.Hex}" };
                copy.Click += (_, _) => CopyText(swatch.Hex, CopiedHex(swatch.Hex));
                chip.ContextMenu = new ContextMenu { ItemsSource = new object[] { setStart, setEnd, FavoriteMenuItem(swatch.Color), copy } };

                WheelLegendPanel.Children.Add(chip);
            }
        }

        private string TryString(string key, string fallback = "") => TryFindResource(key) as string ?? fallback;
        private string CopiedHex(string hex) => string.Format(TryString("Str_ColorWheel_CopiedHex"), hex);
        private string CopiedCount(int count) => string.Format(TryString("Str_ColorWheel_CopiedCount"), count);

        private void SelectWedge(int index, bool setGradientStart)
        {
            if (index < 0 || index >= _wheel.Count) return;

            _selectedIndex = index;
            RenderWheelImage();
            RebuildLegend();
            ShowSelection(animate: true);

            if (setGradientStart)
                SetGradientColor(isStart: true, _wheel[index].Color);
        }

        private void ShowSelection(bool animate)
        {
            if (_wheel.Count == 0) return;
            var swatch = _wheel[_selectedIndex];

            SelectedSwatch.Background = ToBrush(swatch.Color);
            TxtSelectedName.Text = swatch.Name;
            TxtSelectedHex.Text = swatch.Hex;
            TxtSelectedTier.Text = swatch.TierName;
            if (animate) UiMotion.Pop(SelectedSwatch);
            UpdateFavoriteButton();

            RefreshHarmony(animate);
        }

        // ---- wheel mouse ----

        private int IndexAt(MouseEventArgs e)
        {
            var p = e.GetPosition(WheelHost);
            return ColorWheelManager.SwatchIndexAt(p.X, p.Y, WheelHost.ActualWidth);
        }

        private void WheelHost_MouseMove(object sender, MouseEventArgs e)
        {
            int index = IndexAt(e);
            if (index == _hoverIndex) return;
            _hoverIndex = index;

            if (index < 0)
            {
                WheelHost.Cursor = System.Windows.Input.Cursors.Arrow;
                ResetHoverPill();
                return;
            }

            WheelHost.Cursor = System.Windows.Input.Cursors.Hand;
            var swatch = _wheel[index];
            WheelHoverSwatch.Background = ToBrush(swatch.Color);
            WheelHoverSwatch.Visibility = Visibility.Visible;
            TxtWheelHover.Text = $"{swatch.Name} · {swatch.Hex}";
            TxtWheelHover.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        }

        private void WheelHost_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverIndex = -1;
            ResetHoverPill();
        }

        private void ResetHoverPill()
        {
            WheelHoverSwatch.Visibility = Visibility.Collapsed;
            TxtWheelHover.Text = Loc.T("Str_ColorWheel_MsgHover", "Click a colour to select it · right-click: gradient end");
            TxtWheelHover.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }

        private void WheelHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            int index = IndexAt(e);
            if (index >= 0) SelectWedge(index, setGradientStart: true);
        }

        private void WheelHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            int index = IndexAt(e);
            if (index < 0) return;
            SetGradientColor(isStart: false, _wheel[index].Color);
            SetStatus(Loc.F("Str_ColorWheel_MsgSetEnd", "{0} set as the gradient end", _wheel[index].Name), StatusKind.Success);
        }

        private void BtnUseAsStart_Click(object sender, RoutedEventArgs e)
        {
            if (_wheel.Count > 0) SetGradientColor(isStart: true, _wheel[_selectedIndex].Color);
        }

        private void BtnUseAsEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_wheel.Count > 0) SetGradientColor(isStart: false, _wheel[_selectedIndex].Color);
        }

        private string SavedMessage(string path)
        {
            string format = TryString("Str_ColorWheel_SavedStatus", "Saved {0}");
            try { return string.Format(format, Path.GetFileName(path)); }
            catch (FormatException) { return $"Saved {Path.GetFileName(path)}"; }
        }

        // =================================================================
        //  Harmony
        // =================================================================

        private void Harmony_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } ||
                !Enum.TryParse(tag, out ColorHarmony harmony))
                return;

            _harmonyKind = harmony;
            TxtHarmonyHint.Text = ColorWheelManager.Describe(harmony);
            RefreshHarmony(animate: true);
        }

        private void RefreshHarmony(bool animate)
        {
            if (_wheel.Count == 0) return;

            _harmony = ColorWheelManager.GenerateHarmony(_wheel[_selectedIndex].Color, _harmonyKind);
            HarmonyHeader.Content = _harmonyKind == ColorHarmony.SplitComplementary ? "Split-complementary" : _harmonyKind.ToString();
            HarmonyPanel.Children.Clear();

            for (int i = 0; i < _harmony.Count; i++)
            {
                var color = _harmony[i];
                var ink = ToBrush(ColorManager.BestTextColor(color));
                var label = new StackPanel();
                label.Children.Add(new TextBlock
                {
                    Text = i == 0 ? "Base" : NearestWedgeName(color),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ink,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                label.Children.Add(new TextBlock
                {
                    Text = color.Hex,
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = ink,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                });

                var tile = new Button
                {
                    Content = label,
                    Height = 64,
                    Background = ToBrush(color),
                    BorderBrush = System.Windows.Media.Brushes.Transparent,
                    Padding = new Thickness(4),
                    ToolTip = Loc.F("Str_ColorWheel_MsgClickCopy", "Click to copy {0}", color.Hex),
                };
                tile.SetResourceReference(StyleProperty, "ColorTileStyle");
                tile.Click += (_, _) => CopyText(color.Hex, CopiedHex(color.Hex));

                var setStart = new MenuItem { Header = TryString("Str_ColorWheel_SetAsStart", "Set as gradient start") };
                setStart.Click += (_, _) => SetGradientColor(isStart: true, color);
                var setEnd = new MenuItem { Header = TryString("Str_ColorWheel_SetAsEnd", "Set as gradient end") };
                setEnd.Click += (_, _) => SetGradientColor(isStart: false, color);
                tile.ContextMenu = new ContextMenu { ItemsSource = new object[] { setStart, setEnd, FavoriteMenuItem(color) } };

                HarmonyPanel.Children.Add(tile);
                if (animate) UiMotion.FadeIn(tile, 4 + i * 2);
            }
        }

        /// <summary>Name of the wheel wedge closest in hue (for labelling harmony tiles).</summary>
        private string NearestWedgeName(RgbColor color)
        {
            double ryb = ColorWheelManager.RgbHueToRyb(ColorManager.RgbToHsv(color).H);
            int index = (int)Math.Round(ryb / 30) % ColorWheelManager.WedgeCount;
            return _wheel.Count > index ? _wheel[index].Name : "";
        }

        private void BtnCopyHarmony_Click(object sender, RoutedEventArgs e) =>
            CopyText(string.Join(Environment.NewLine, _harmony.Select(c => c.Hex)), CopiedCount(_harmony.Count));

        // =================================================================
        //  Gradient
        // =================================================================

        private void SetGradientColor(bool isStart, RgbColor color)
        {
            _settingGradientText = true;
            try
            {
                if (isStart)
                {
                    _gradientStart = color;
                    TxtStartHex.Text = color.Hex;
                    StartSwatch.Background = ToBrush(color);
                    StartBar.ClearValue(Border.BorderBrushProperty);
                    UiMotion.Pop(StartSwatch);
                }
                else
                {
                    _gradientEnd = color;
                    TxtEndHex.Text = color.Hex;
                    EndSwatch.Background = ToBrush(color);
                    EndBar.ClearValue(Border.BorderBrushProperty);
                    UiMotion.Pop(EndSwatch);
                }
            }
            finally
            {
                _settingGradientText = false;
            }

            RefreshGradient();
        }

        /// <summary>Live: a valid hex updates the gradient as you type; an invalid one marks the field.</summary>
        private void GradientHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _settingGradientText || sender is not TextBox box) return;

            bool isStart = box == TxtStartHex;
            Border bar = isStart ? StartBar : EndBar;

            if (ColorManager.TryParseColor(box.Text, out var rgb))
            {
                bar.ClearValue(Border.BorderBrushProperty);
                if (isStart) { _gradientStart = rgb; StartSwatch.Background = ToBrush(rgb); }
                else { _gradientEnd = rgb; EndSwatch.Background = ToBrush(rgb); }
                RefreshGradient();
            }
            else
            {
                bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            }
        }

        private void StartHex_LostFocus(object sender, RoutedEventArgs e) => NormalizeHexField(TxtStartHex, StartBar, _gradientStart);
        private void EndHex_LostFocus(object sender, RoutedEventArgs e) => NormalizeHexField(TxtEndHex, EndBar, _gradientEnd);

        /// <summary>On leaving the field: show the canonical "#RRGGBB" (or revert an invalid entry).</summary>
        private void NormalizeHexField(TextBox box, Border bar, RgbColor current)
        {
            _settingGradientText = true;
            box.Text = current.Hex;
            _settingGradientText = false;
            bar.ClearValue(Border.BorderBrushProperty);
        }

        private void StartSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (PickColor(_gradientStart) is RgbColor c) SetGradientColor(isStart: true, c);
        }

        private void EndSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (PickColor(_gradientEnd) is RgbColor c) SetGradientColor(isStart: false, c);
        }

        private static RgbColor? PickColor(RgbColor current)
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            return new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }

        private void BtnSwapGradient_Click(object sender, RoutedEventArgs e)
        {
            var (start, end) = (_gradientStart, _gradientEnd);
            SetGradientColor(isStart: true, end);
            SetGradientColor(isStart: false, start);
        }

        private void StepsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtStepsValue.Text = ((int)StepsSlider.Value).ToString();
            RefreshGradient();
        }

        private void GradientOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RefreshGradient();
        }

        private GradientBlend CurrentBlend => RadioModeRgb.IsChecked == true ? GradientBlend.Rgb : GradientBlend.Hsl;

        private void RefreshGradient()
        {
            if (!IsLoaded) return;

            _gradient = ColorWheelManager.GenerateGradient(_gradientStart, _gradientEnd, (int)StepsSlider.Value, CurrentBlend);

            var strip = ColorWheelManager.RenderGradientStrip(_gradient, GradientPreviewWidth, GradientPreviewHeight);
            GradientStrip.Background = new ImageBrush(strip) { Stretch = Stretch.Fill };
            GradientImage.Source = strip;
            TxtGradientHexList.Text = string.Join("   ", _gradient.Select(c => c.Hex));

            GradientStopsPanel.Children.Clear();
            foreach (var color in _gradient)
            {
                var chip = new Button
                {
                    Height = 30,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(2),
                    Background = ToBrush(color),
                    BorderBrush = System.Windows.Media.Brushes.Transparent,
                    ToolTip = Loc.F("Str_ColorWheel_MsgClickCopy", "Click to copy {0}", color.Hex),
                    Content = new TextBlock
                    {
                        Text = color.Hex,
                        FontSize = _gradient.Count > 12 ? 9 : 10.5,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground = ToBrush(ColorManager.BestTextColor(color)),
                    },
                };
                chip.SetResourceReference(StyleProperty, "ColorTileStyle");
                chip.Click += (_, _) => CopyText(color.Hex, CopiedHex(color.Hex));
                GradientStopsPanel.Children.Add(chip);
            }
        }

        private void BtnSaveGradient_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = TryString("Str_ColorWheel_SaveDialogTitle", "Save color wheel and gradient"),
                Filter = Loc.T("Str_ColorWheel_MsgPngFilter", "PNG image|*.png"),
                DefaultExt = ".png",
                FileName = "color-wheel-gradient.png",
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                // Wheel (no selection outline, hex on each wedge) + start colour, gradient strip and end colour.
                var bitmap = ColorWheelManager.RenderComposite(_wheel, _gradient, _gradientStart, _gradientEnd, WheelExportSize,
                    TryString("Str_ColorWheel_StartColor", "Start"), TryString("Str_ColorWheel_EndColor", "End"));
                ColorWheelManager.SaveBitmapToPng(bitmap, dialog.FileName);
                SetStatus(SavedMessage(dialog.FileName), StatusKind.Success);
            }
            catch (Exception ex)
            {
                AppModal.Show(string.Format(TryString("Str_ColorWheel_SaveFailed", "Couldn't save: {0}"), ex.Message),
                    TryString("Str_ColorWheel_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyHexList_Click(object sender, RoutedEventArgs e) =>
            CopyText(string.Join(Environment.NewLine, _gradient.Select(c => c.Hex)), CopiedCount(_gradient.Count));

        private void BtnCopyCss_Click(object sender, RoutedEventArgs e) =>
            CopyText(ColorWheelManager.ToCssGradient(_gradient), TryString("Str_ColorWheel_CopiedCss"));

        // =================================================================
        //  Favorites (shared with Color Picker / Screen Color Picker via ColorHistoryStore)
        // =================================================================

        private void BtnFavoriteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_wheel.Count == 0) return;
            _history.ToggleFavorite(_wheel[_selectedIndex].Color);
            RebuildFavorites();
            UpdateFavoriteButton();
            UiMotion.Pop(IconFavoriteSelected, 0.6);
        }

        private void UpdateFavoriteButton()
        {
            if (_wheel.Count == 0) return;
            bool favorite = _history.IsFavorite(_wheel[_selectedIndex].Color);
            IconFavoriteSelected.Kind = favorite ? PackIconKind.Star : PackIconKind.StarOutline;
            if (favorite) IconFavoriteSelected.SetResourceReference(ForegroundProperty, "StatusWarningFg");
            else IconFavoriteSelected.ClearValue(ForegroundProperty);
        }

        private MenuItem FavoriteMenuItem(RgbColor color) =>
            SwatchChips.Item(SwatchChips.FavoriteHeader(_history, color), () =>
            {
                _history.ToggleFavorite(color);
                RebuildFavorites();
                UpdateFavoriteButton();
            });

        private void RebuildFavorites() =>
            SwatchChips.Rebuild(FavoritesPanel, TxtFavoritesEmptyHint, _history.Favorites,
                onClick: (rgb, _) => SetGradientColor(isStart: true, rgb),
                menuItems: (rgb, hex) => new[]
                {
                    SwatchChips.Item(SwatchChips.Text("Str_ColorWheel_SetAsStart"), () => SetGradientColor(isStart: true, rgb)),
                    SwatchChips.Item(SwatchChips.Text("Str_ColorWheel_SetAsEnd"), () => SetGradientColor(isStart: false, rgb)),
                    SwatchChips.Item(SwatchChips.Text("Str_Color_RemoveFromFavorites"), () =>
                    {
                        _history.RemoveFavorite(hex);
                        RebuildFavorites();
                        UpdateFavoriteButton();
                    }),
                },
                tipKey: "Str_ColorWheel_FavoriteSwatchTip");

        // =================================================================
        //  Helpers
        // =================================================================

        private void CopyText(string text, string status)
        {
            try
            {
                Clipboard.SetText(text);
                SetStatus(status, StatusKind.Success);
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(TryString("Str_ColorWheel_CopyFailed"), ex.Message), StatusKind.Error);
            }
        }

        /// <summary>Shows a message in the status pill; success messages fade back to "Ready" after a few seconds.</summary>
        private void SetStatus(string text, StatusKind kind)
        {
            StatusText.Text = text;
            StatusText.ToolTip = text;

            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                _ => ("RowBg", "TextSecondary", PackIconKind.InformationOutline),
            };
            StatusPill.SetResourceReference(Border.BackgroundProperty, bg);
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconStatus.Kind = icon;
            if (kind != StatusKind.Neutral) UiMotion.Pop(StatusPill);

            _statusTimer?.Stop();
            if (kind == StatusKind.Success)
            {
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _statusTimer.Tick += (_, _) =>
                {
                    _statusTimer?.Stop();
                    StatusText.SetResourceReference(TextBlock.TextProperty, "Str_ColorWheel_ReadyStatus");
                    StatusText.ToolTip = null;
                    StatusPill.SetResourceReference(Border.BackgroundProperty, "RowBg");
                    StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    IconStatus.Kind = PackIconKind.InformationOutline;
                };
                _statusTimer.Start();
            }
        }

        private static SolidColorBrush ToBrush(RgbColor c)
        {
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }
}
