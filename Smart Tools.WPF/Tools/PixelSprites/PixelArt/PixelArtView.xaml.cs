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
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Pixel Art Editor - UI only. Drawing, undo, frames, colours, palettes, the PNG writer and the
    /// settings are in <see cref="PixelArtManager"/>; the picture itself is drawn by
    /// <see cref="PixelCanvasControl"/>. The artwork is never saved by the app: it lives in memory
    /// until it is exported (a PNG opens again with Open image). Only the setup is remembered.
    /// </summary>
    public partial class PixelArtView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const uint DefaultColor = 0xFFFF004D;
        private const string CustomPaletteName = "*custom*";
        private const string FavoritesPaletteName = "*favorites*";

        private readonly PixelArtManager _manager = new(32, 32);
        private readonly ObservableCollection<PixelArtFrameItem> _frames = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _thumbTimer;
        private readonly DispatcherTimer _playTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly bool _viewConstructed;

        private PixelArtTool _tool = PixelArtTool.Pencil;
        private PixelArtSymmetry _symmetry = PixelArtSymmetry.None;
        private int _brush = 1;
        private uint _color = DefaultColor;
        private string _paletteName = "PICO-8";
        private List<uint> _custom = new();
        private readonly ColorHistoryStore _favorites = new();      // the Color Picker's favourites (shared file, read only here)
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _settingHex;
        private bool _fitPending = true;
        private bool _allThumbsDirty = true;
        private object? _thumbDoc;

        // The stroke or shape being dragged.
        private bool _dragging;
        private bool _dragErase;
        private int _lastX, _lastY, _anchorX, _anchorY;

        // Animation preview.
        private int _playFrame;

        private enum StatusKind { Error, Success, Warning }

        public PixelArtView()
        {
            InitializeComponent();
            FramesList.ItemsSource = _frames;
            PixelCanvas.Manager = _manager;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _thumbTimer.Tick += (_, _) => { _thumbTimer.Stop(); UpdateThumbnails(); };
            _playTimer = new DispatcherTimer();
            _playTimer.Tick += (_, _) => PlayStep();
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); UiMotion.FadeVisibility(DonePill, false); };

            _manager.Changed += OnManagerChanged;
            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                PixelCanvas.SetResourceReference(PixelCanvasControl.CheckerLightProperty, "InputBg");
                PixelCanvas.SetResourceReference(PixelCanvasControl.CheckerDarkProperty, "InputBorder");
                PixelCanvas.SetResourceReference(PixelCanvasControl.GridBrushProperty, "TextSecondary");
                PixelCanvas.SetResourceReference(PixelCanvasControl.GuideBrushProperty, "AccentColor");
                ApplySettings(PixelArtManager.LoadSettings());
                OnManagerChanged();
                ResetColorBar();
            }
            else if (_paletteName == FavoritesPaletteName)
            {
                RebuildPalette();                       // a favourite may have been added or removed in the Color Picker meanwhile
            }
            Focus();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => StopPlaying();

        private void ApplySettings(PixelArtSettings s)
        {
            _applyingSettings = true;
            try
            {
                _brush = s.BrushSize;
                _symmetry = s.Symmetry;
                _paletteName = s.PaletteName;
                _custom = (s.CustomColors ?? new List<string>()).Select(PixelArtManager.TryParseColor).Where(c => c.HasValue).Select(c => c!.Value).ToList();
                _color = PixelArtManager.TryParseColor(s.Color) ?? DefaultColor;
                if (_color == 0) _color = DefaultColor;

                (_brush switch { 2 => RadioBrush2, 3 => RadioBrush3, 4 => RadioBrush4, _ => RadioBrush1 }).IsChecked = true;
                (_symmetry switch { PixelArtSymmetry.Horizontal => RadioSymH, PixelArtSymmetry.Vertical => RadioSymV, PixelArtSymmetry.Both => RadioSymBoth, _ => RadioSymNone }).IsChecked = true;
                ChkFilled.IsChecked = s.FilledShapes;
                ChkGrid.IsChecked = s.ShowGrid;
                ChkOnion.IsChecked = s.OnionSkin;
                TxtNewWidth.Text = s.NewWidth.ToString(CultureInfo.InvariantCulture);
                TxtNewHeight.Text = s.NewHeight.ToString(CultureInfo.InvariantCulture);
                SelectTag(CmbFps, s.Fps.ToString(CultureInfo.InvariantCulture));
                (s.ExportScale switch { 1 => RadioScale1, 2 => RadioScale2, 4 => RadioScale4, 16 => RadioScale16, _ => RadioScale8 }).IsChecked = true;

                BuildPaletteCombo();
                RebuildPalette();
                SetColor(_color, fromUser: false);
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
            }
            finally
            {
                _applyingSettings = false;
            }
            SetTool(s.Tool, save: false);
            ApplyViewOptions();
            UpdateExportInfo();
            _manager.NewDocument(s.NewWidth, s.NewHeight);          // a fresh canvas of the remembered size
        }

        private PixelArtSettings ReadSettings() => new()
        {
            Tool = _tool,
            BrushSize = _brush,
            Symmetry = _symmetry,
            FilledShapes = ChkFilled.IsChecked == true,
            ShowGrid = ChkGrid.IsChecked == true,
            OnionSkin = ChkOnion.IsChecked == true,
            ExportScale = ExportScale(),
            Fps = Fps(),
            NewWidth = ParseSize(TxtNewWidth.Text) ?? 32,
            NewHeight = ParseSize(TxtNewHeight.Text) ?? 32,
            PaletteName = _paletteName,
            Color = PixelArtManager.ToHex(_color),
            CustomColors = _custom.Select(PixelArtManager.ToHex).ToList(),
            OptionsCollapsed = _optionsCollapsed,
        };

        private void SaveSettings() => PixelArtManager.SaveSettings(ReadSettings());

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private string Str(string key, string fallback = "") => TryFindResource(key) as string ?? fallback;

        private static void SelectTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string) == tag) { combo.SelectedItem = item; return; }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ScheduleSave();
            _fitPending = true;
        }

        // =================================================================
        //  The picture changed
        // =================================================================

        private void OnManagerChanged()
        {
            var doc = _manager.Document;
            if (!ReferenceEquals(_thumbDoc, doc)) { _thumbDoc = doc; _allThumbsDirty = true; }

            PixelCanvas.Refresh();
            BtnUndo.IsEnabled = _manager.CanUndo;
            BtnRedo.IsEnabled = _manager.CanRedo;
            TxtPillSize.Text = $"{doc.Width} × {doc.Height}";
            TxtPillZoom.Text = $"{PixelCanvas.Zoom * 100}%";
            SyncFrames();
            _thumbTimer.Stop();
            _thumbTimer.Start();
            UpdateExportInfo();

            if (_fitPending && CanvasScroll.ViewportWidth > 0) FitToView();
        }

        private void SyncFrames()
        {
            int count = _manager.Document.Frames.Count;
            while (_frames.Count > count) _frames.RemoveAt(_frames.Count - 1);
            while (_frames.Count < count) { _frames.Add(new PixelArtFrameItem()); _allThumbsDirty = true; }
            for (int i = 0; i < _frames.Count; i++)
            {
                _frames[i].Number = i + 1;
                _frames[i].IsCurrent = i == _manager.CurrentFrame;
            }
            TxtFrameCount.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_PixelArt_FrameCount", "{0} of {1}"), _manager.CurrentFrame + 1, count);
            BtnFrameDelete.IsEnabled = count > 1;
            BtnFrameLeft.IsEnabled = _manager.CurrentFrame > 0;
            BtnFrameRight.IsEnabled = _manager.CurrentFrame < count - 1;
            BtnFrameAdd.IsEnabled = BtnFrameDuplicate.IsEnabled = count < PixelArtDocument.MaxFrames;
            BtnPlay.IsEnabled = count > 1 || _playTimer.IsEnabled;
            ColumnsPanel.Visibility = ChkSheet.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (count == 1 && _playTimer.IsEnabled) StopPlaying();
        }

        private void UpdateThumbnails()
        {
            var doc = _manager.Document;
            if (_allThumbsDirty)
            {
                for (int i = 0; i < _frames.Count && i < doc.Frames.Count; i++)
                    _frames[i].Thumbnail = PixelArtManager.CreateBitmap(doc.Frames[i], doc.Width, doc.Height);
                _allThumbsDirty = false;
            }
            else if (_manager.CurrentFrame < _frames.Count)
            {
                _frames[_manager.CurrentFrame].Thumbnail = PixelArtManager.CreateBitmap(_manager.Pixels, doc.Width, doc.Height);
            }
            if (!_playTimer.IsEnabled) PreviewImage.Source = PixelArtManager.CreateBitmap(_manager.Pixels, doc.Width, doc.Height);
        }

        // =================================================================
        //  Tools, colours and options
        // =================================================================

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            SetTool(Enum.TryParse<PixelArtTool>(tag, out var tool) ? tool : PixelArtTool.Pencil, save: true);
        }

        private void SetTool(PixelArtTool tool, bool save)
        {
            _tool = tool;
            (tool switch
            {
                PixelArtTool.Eraser => RadioToolEraser, PixelArtTool.Fill => RadioToolFill, PixelArtTool.Line => RadioToolLine,
                PixelArtTool.Rectangle => RadioToolRectangle, PixelArtTool.Ellipse => RadioToolEllipse, PixelArtTool.Picker => RadioToolPicker,
                _ => RadioToolPencil,
            }).IsChecked = true;
            TxtToolHint.Text = Str("Str_PixelArt_Hint_" + tool);
            if (save) ScheduleSave();
        }

        private void Brush_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            _brush = int.TryParse(tag, out int size) ? Math.Clamp(size, 1, 4) : 1;
            ScheduleSave();
        }

        private void Symmetry_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            _symmetry = Enum.TryParse<PixelArtSymmetry>(tag, out var s) ? s : PixelArtSymmetry.None;
            PixelCanvas.Symmetry = _symmetry;
            PixelCanvas.InvalidateVisual();
            ScheduleSave();
        }

        private void Option_Click(object sender, RoutedEventArgs e) => ScheduleSave();

        private void ViewOption_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ApplyViewOptions();
            ScheduleSave();
        }

        private void ApplyViewOptions()
        {
            PixelCanvas.ShowGrid = ChkGrid.IsChecked == true;
            PixelCanvas.ShowOnion = ChkOnion.IsChecked == true;
            PixelCanvas.Symmetry = _symmetry;
        }

        // ---- colour

        private void SetColor(uint argb, bool fromUser)
        {
            _color = PixelArtManager.Normalize(argb);
            CurrentColorSwatch.Background = new SolidColorBrush(ToColor(_color));
            if (!fromUser || !TxtColorHex.IsKeyboardFocusWithin)
            {
                _settingHex = true;
                TxtColorHex.Text = PixelArtManager.ToHex(_color);
                _settingHex = false;
            }
            TxtColorError.Visibility = Visibility.Collapsed;
            ColorBar.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
            if (!_fromPicker) SyncPickerFrom(_color);
            if (_playTimer.IsEnabled == false && PaletteList.ItemsSource is IEnumerable<PixelArtSwatch> swatches)
                foreach (var s in swatches) s.IsSelected = s.Argb == _color;
            ScheduleSave();
        }

        private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        private void ResetColorBar() => TxtColorHex.Text = PixelArtManager.ToHex(_color);

        private void TxtColorHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _settingHex) return;
            var parsed = PixelArtManager.TryParseColor(TxtColorHex.Text);
            if (parsed is { } color)
            {
                SetColor(color, fromUser: true);
            }
            else
            {
                TxtColorError.Visibility = Visibility.Visible;
                ColorBar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            }
        }

        private void TxtColorHex_LostFocus(object sender, RoutedEventArgs e)
        {
            _settingHex = true;
            TxtColorHex.Text = PixelArtManager.ToHex(_color);       // put back a readable form (or the last good colour)
            _settingHex = false;
            TxtColorError.Visibility = Visibility.Collapsed;
            ColorBar.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
        }


        // ---- colour mixer (saturation / brightness square, hue bar, opacity bar)

        private HsvColor _hsv = new(348, 1, 1);
        private byte _alpha = 255;
        private bool _fromPicker;
        private int _dragArea;                      // 1 = square, 2 = hue, 3 = opacity

        /// <summary>The mixer follows the colour, but keeps its hue when the colour has none (black, white, grey).</summary>
        private void SyncPickerFrom(uint argb)
        {
            if (argb == 0) { _alpha = 0; UpdatePickerVisuals(); return; }
            var rgb = new RgbColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var hsv = ColorManager.RgbToHsv(rgb);
            _hsv = new HsvColor(hsv.S <= 0 || hsv.V <= 0 ? _hsv.H : hsv.H, hsv.S, hsv.V);
            _alpha = (byte)(argb >> 24);
            UpdatePickerVisuals();
        }

        private void ApplyPicker()
        {
            var rgb = ColorManager.HsvToRgb(_hsv);
            _fromPicker = true;
            SetColor(PixelArtManager.Pack(Math.Max(_alpha, (byte)1), rgb.R, rgb.G, rgb.B), fromUser: false);
            _fromPicker = false;
            UpdatePickerVisuals();
        }

        private void UpdatePickerVisuals()
        {
            var pure = ColorManager.HsvToRgb(_hsv.H, 1, 1);
            SvHueLayer.Background = Frozen(new SolidColorBrush(Color.FromRgb(pure.R, pure.G, pure.B)));
            var rgb = ColorManager.HsvToRgb(_hsv);
            AlphaStart.Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B);
            AlphaEnd.Color = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
            if (SvArea.ActualWidth > 0)
                SvCursor.Margin = new Thickness(_hsv.S * SvArea.ActualWidth - 8, (1 - _hsv.V) * SvArea.ActualHeight - 8, 0, 0);
            if (HueArea.ActualWidth > 0)
                HueCursor.Margin = new Thickness(_hsv.H / 360.0 * HueArea.ActualWidth - 6, -3, 0, 0);
            if (AlphaArea.ActualWidth > 0)
                AlphaCursor.Margin = new Thickness((Math.Max(_alpha, (byte)1) - 1) / 254.0 * AlphaArea.ActualWidth - 6, -3, 0, 0);
        }

        private void PickerArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePickerVisuals();

        private void SvArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragArea = 1;
            SvArea.CaptureMouse();
            MoveSv(e.GetPosition(SvArea));
        }

        private void SvArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_dragArea == 1) MoveSv(e.GetPosition(SvArea));
        }

        private void MoveSv(System.Windows.Point p)
        {
            if (SvArea.ActualWidth <= 0 || SvArea.ActualHeight <= 0) return;
            _hsv = new HsvColor(_hsv.H, Math.Clamp(p.X / SvArea.ActualWidth, 0, 1), 1 - Math.Clamp(p.Y / SvArea.ActualHeight, 0, 1));
            ApplyPicker();
        }

        private void HueArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragArea = 2;
            HueArea.CaptureMouse();
            MoveHue(e.GetPosition(HueArea));
        }

        private void HueArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_dragArea == 2) MoveHue(e.GetPosition(HueArea));
        }

        private void MoveHue(System.Windows.Point p)
        {
            if (HueArea.ActualWidth <= 0) return;
            _hsv = new HsvColor(Math.Clamp(p.X / HueArea.ActualWidth, 0, 1) * 359.999, _hsv.S, _hsv.V);
            ApplyPicker();
        }

        private void AlphaArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragArea = 3;
            AlphaArea.CaptureMouse();
            MoveAlpha(e.GetPosition(AlphaArea));
        }

        private void AlphaArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_dragArea == 3) MoveAlpha(e.GetPosition(AlphaArea));
        }

        private void MoveAlpha(System.Windows.Point p)
        {
            if (AlphaArea.ActualWidth <= 0) return;
            _alpha = (byte)(1 + Math.Round(Math.Clamp(p.X / AlphaArea.ActualWidth, 0, 1) * 254));
            ApplyPicker();
        }

        private void PickerArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

        private void PickerArea_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => _dragArea = 0;

        // ---- palette

        private void BuildPaletteCombo()
        {
            CmbPalette.Items.Clear();
            foreach (var p in PixelArtManager.Palettes)
                CmbPalette.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
            CmbPalette.Items.Add(new ComboBoxItem { Content = Str("Str_PixelArt_Favorites", "Favorites (Color Picker)"), Tag = FavoritesPaletteName });
            CmbPalette.Items.Add(new ComboBoxItem { Content = Str("Str_PixelArt_MyColors", "My colors"), Tag = CustomPaletteName });
            string tag = PixelArtManager.Palettes.Any(p => p.Name == _paletteName) || _paletteName is CustomPaletteName or FavoritesPaletteName
                ? _paletteName : PixelArtManager.Palettes[0].Name;
            _paletteName = tag;
            foreach (ComboBoxItem item in CmbPalette.Items)
                if ((item.Tag as string) == tag) CmbPalette.SelectedItem = item;
        }

        private void CmbPalette_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings || (CmbPalette.SelectedItem as ComboBoxItem)?.Tag is not string name) return;
            _paletteName = name;
            RebuildPalette();
            ScheduleSave();
        }

        private void RebuildPalette()
        {
            bool custom = _paletteName == CustomPaletteName;
            bool favorites = _paletteName == FavoritesPaletteName;
            IEnumerable<uint> colors;
            if (favorites)
            {
                _favorites.Load();                      // never throws; a missing file is an empty list
                colors = _favorites.Favorites
                    .Select(h => ColorManager.TryHexToRgb(h, out var rgb) ? (uint?)(0xFF000000u | (uint)rgb.R << 16 | (uint)rgb.G << 8 | rgb.B) : null)
                    .Where(c => c.HasValue).Select(c => c!.Value).ToList();
            }
            else colors = custom ? _custom : PixelArtManager.PaletteByName(_paletteName).Colors;
            var swatches = colors.Select(c => new PixelArtSwatch
            {
                Argb = c,
                Hex = PixelArtManager.ToHex(c),
                Brush = Frozen(new SolidColorBrush(ToColor(c))),
                IsSelected = c == _color,
            }).ToList();
            PaletteList.ItemsSource = swatches;
            TxtPaletteHint.Text = custom
                ? Str(swatches.Count == 0 ? "Str_PixelArt_MyColorsEmpty" : "Str_PixelArt_MyColorsHint")
                : favorites
                    ? Str(swatches.Count == 0 ? "Str_PixelArt_FavoritesEmpty" : "Str_PixelArt_FavoritesHint")
                    : "";
        }

        private static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PixelArtSwatch swatch) SetColor(swatch.Argb, fromUser: false);
        }

        private void Swatch_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_paletteName != CustomPaletteName || (sender as FrameworkElement)?.Tag is not PixelArtSwatch swatch) return;
            _custom.Remove(swatch.Argb);
            RebuildPalette();
            ScheduleSave();
            e.Handled = true;
        }

        private void BtnAddColor_Click(object sender, RoutedEventArgs e)
        {
            if (_color == 0) return;
            _custom = PixelArtManager.AddCustomColor(_custom, _color);
            // Show them: switch to My colors so the new swatch is visible.
            _paletteName = CustomPaletteName;
            foreach (ComboBoxItem item in CmbPalette.Items)
                if ((item.Tag as string) == CustomPaletteName) CmbPalette.SelectedItem = item;
            RebuildPalette();
            ScheduleSave();
        }

        // =================================================================
        //  Drawing on the canvas
        // =================================================================

        private uint ActiveColor(bool erase) => erase || _tool == PixelArtTool.Eraser ? 0u : _color;

        private bool IsShapeTool => _tool is PixelArtTool.Line or PixelArtTool.Rectangle or PixelArtTool.Ellipse;

        private (int X, int Y) Clamp(int x, int y) =>
            (Math.Clamp(x, 0, _manager.Document.Width - 1), Math.Clamp(y, 0, _manager.Document.Height - 1));

        private void PixelCanvas_PixelPressed(object? sender, PixelPointerEventArgs e)
        {
            if (_playTimer.IsEnabled) StopPlaying();
            var doc = _manager.Document;
            bool inside = e.X >= 0 && e.Y >= 0 && e.X < doc.Width && e.Y < doc.Height;
            _dragErase = e.IsRightButton;

            switch (_tool)
            {
                case PixelArtTool.Picker:
                    if (inside && _manager.Pixels[e.Y * doc.Width + e.X] is var picked && picked != 0) SetColor(picked, fromUser: false);
                    break;
                case PixelArtTool.Fill:
                    if (inside) _manager.Fill(e.X, e.Y, ActiveColor(_dragErase));
                    break;
                case PixelArtTool.Pencil:
                case PixelArtTool.Eraser:
                    if (!inside) break;
                    _dragging = true;
                    _manager.BeginStroke();
                    _manager.StrokePoint(e.X, e.Y, ActiveColor(_dragErase), _brush, _symmetry);
                    (_lastX, _lastY) = (e.X, e.Y);
                    PixelCanvas.Refresh();
                    break;
                default:                                           // line, rectangle, ellipse
                    if (!inside) break;
                    _dragging = true;
                    (_anchorX, _anchorY) = (e.X, e.Y);
                    ShowGhost(e.X, e.Y);
                    break;
            }
        }

        private void PixelCanvas_PixelMoved(object? sender, PixelPointerEventArgs e)
        {
            var doc = _manager.Document;
            bool inside = e.X >= 0 && e.Y >= 0 && e.X < doc.Width && e.Y < doc.Height;
            PillCursor.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
            if (inside) TxtPillCursor.Text = $"x {e.X}  y {e.Y}";

            if (!_dragging) return;
            if (_tool is PixelArtTool.Pencil or PixelArtTool.Eraser)
            {
                if (e.X == _lastX && e.Y == _lastY) return;
                _manager.StrokeLine(_lastX, _lastY, e.X, e.Y, ActiveColor(_dragErase), _brush, _symmetry);
                (_lastX, _lastY) = (e.X, e.Y);
                PixelCanvas.Refresh();
            }
            else if (IsShapeTool)
            {
                ShowGhost(e.X, e.Y);
            }
        }

        private void PixelCanvas_PixelReleased(object? sender, PixelPointerEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            if (_tool is PixelArtTool.Pencil or PixelArtTool.Eraser)
            {
                _manager.EndStroke();
            }
            else if (IsShapeTool)
            {
                var (x, y) = Clamp(e.X, e.Y);
                ClearGhost();
                _manager.ApplyShape(_tool, _anchorX, _anchorY, x, y, ActiveColor(_dragErase), ChkFilled.IsChecked == true, _brush, _symmetry);
            }
        }

        private void PixelCanvas_PointerLeft(object? sender, EventArgs e) => PillCursor.Visibility = Visibility.Collapsed;

        private void ShowGhost(int x, int y)
        {
            var (cx, cy) = Clamp(x, y);
            PixelCanvas.Ghost = PixelArtManager.ShapePoints(_tool, _anchorX, _anchorY, cx, cy, ChkFilled.IsChecked == true);
            PixelCanvas.GhostColor = ActiveColor(_dragErase);
            PixelCanvas.InvalidateVisual();
        }

        private void ClearGhost()
        {
            PixelCanvas.Ghost = null;
            PixelCanvas.InvalidateVisual();
        }

        private void CancelShape()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_tool is PixelArtTool.Pencil or PixelArtTool.Eraser) _manager.EndStroke();
            ClearGhost();
        }

        // =================================================================
        //  Toolbar
        // =================================================================

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => _manager.Undo();
        private void BtnRedo_Click(object sender, RoutedEventArgs e) => _manager.Redo();
        private void BtnFlipH_Click(object sender, RoutedEventArgs e) => _manager.FlipHorizontal();
        private void BtnFlipV_Click(object sender, RoutedEventArgs e) => _manager.FlipVertical();
        private void BtnRotateLeft_Click(object sender, RoutedEventArgs e) { _fitPending = true; _manager.Rotate(false); }
        private void BtnRotateRight_Click(object sender, RoutedEventArgs e) { _fitPending = true; _manager.Rotate(true); }
        private void BtnClearFrame_Click(object sender, RoutedEventArgs e) => _manager.ClearFrame();

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1);
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);
        private void BtnFit_Click(object sender, RoutedEventArgs e) => FitToView();
        private void PixelCanvas_ZoomWheel(object? sender, int e) => StepZoom(e);

        private static readonly int[] ZoomSteps = { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64 };

        private void StepZoom(int direction)
        {
            _fitPending = false;
            int current = PixelCanvas.Zoom;
            int next = direction > 0
                ? ZoomSteps.FirstOrDefault(z => z > current, ZoomSteps[^1])
                : ZoomSteps.LastOrDefault(z => z < current, ZoomSteps[0]);
            SetZoom(next);
        }

        private void SetZoom(int zoom)
        {
            PixelCanvas.Zoom = zoom;
            TxtPillZoom.Text = $"{PixelCanvas.Zoom * 100}%";
            UiMotion.Pop(TxtPillZoom, 0.9, 200);
        }

        /// <summary>The biggest whole zoom that shows the whole picture.</summary>
        private void FitToView()
        {
            var doc = _manager.Document;
            double vw = CanvasScroll.ViewportWidth - 44, vh = CanvasScroll.ViewportHeight - 44;
            if (vw <= 0 || vh <= 0) return;
            int zoom = (int)Math.Floor(Math.Min(vw / doc.Width, vh / doc.Height));
            SetZoom(Math.Clamp(zoom, 1, 64));
            _fitPending = false;
        }

        private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fitPending) FitToView();
        }

        // =================================================================
        //  Canvas size, new, open
        // =================================================================

        private static int? ParseSize(string? text) =>
            int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= PixelArtDocument.MaxSide ? n : null;

        private void TxtSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool bad = (!string.IsNullOrWhiteSpace(TxtNewWidth.Text) && ParseSize(TxtNewWidth.Text) == null)
                       || (!string.IsNullOrWhiteSpace(TxtNewHeight.Text) && ParseSize(TxtNewHeight.Text) == null);
            TxtSizeError.Visibility = bad ? Visibility.Visible : Visibility.Collapsed;
            ScheduleSave();
        }

        private void SizeChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string size) return;
            TxtNewWidth.Text = TxtNewHeight.Text = size;
        }

        private (int W, int H)? ReadSize()
        {
            if (ParseSize(TxtNewWidth.Text) is not { } w || ParseSize(TxtNewHeight.Text) is not { } h)
            {
                TxtSizeError.Visibility = Visibility.Visible;
                return null;
            }
            return (w, h);
        }

        /// <summary>The drawing is only in memory, so ask before throwing it away.</summary>
        private bool ConfirmDiscard()
        {
            if (!_manager.IsDirty) return true;
            return MessageBox.Show(Window.GetWindow(this), Str("Str_PixelArt_DiscardBody", "Start over? The drawing hasn't been exported."),
                Str("Str_PixelArt_DiscardTitle", "Pixel Art Editor"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private void BtnNewCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (ReadSize() is not { } size || !ConfirmDiscard()) return;
            _fitPending = true;
            StopPlaying();
            _manager.NewDocument(size.W, size.H);
        }

        private void BtnResizeCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (ReadSize() is not { } size) return;
            _fitPending = true;
            _manager.Resize(size.W, size.H, RadioAnchorCenter.IsChecked == true ? PixelArtAnchor.Center : PixelArtAnchor.TopLeft);
        }

        private async void BtnOpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Str("Str_PixelArt_OpenImage"),
                Filter = Str("Str_PixelArt_FilterImages", "Pictures") + "|*.png;*.gif;*.bmp;*.jpg;*.jpeg;*.webp;*.ico|" + Str("Str_PixelArt_FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() != true || !ConfirmDiscard()) return;

            string path = dialog.FileName;
            int w = 0, h = 0;
            string problem = "";
            byte[]? rgba = await Task.Run(() => PixelArtManager.LoadImage(path, out w, out h, out problem));
            if (rgba == null)
            {
                ShowStatus(Str("Str_PixelArt_Problem_" + problem, problem), StatusKind.Error);
                return;
            }
            HideStatus();
            StopPlaying();
            _fitPending = true;
            _manager.LoadRgba(w, h, rgba);
            TxtNewWidth.Text = w.ToString(CultureInfo.InvariantCulture);
            TxtNewHeight.Text = h.ToString(CultureInfo.InvariantCulture);
            ShowDone("Str_PixelArt_Opened");
        }

        // =================================================================
        //  Frames and the animation preview
        // =================================================================

        private void Frame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PixelArtFrameItem item) return;
            StopPlaying();
            _manager.SelectFrame(_frames.IndexOf(item));
        }

        private void BtnFrameAdd_Click(object sender, RoutedEventArgs e) => AddFrame(duplicate: false);
        private void BtnFrameDuplicate_Click(object sender, RoutedEventArgs e) => AddFrame(duplicate: true);

        private void AddFrame(bool duplicate)
        {
            StopPlaying();
            if (!_manager.AddFrame(duplicate))
                ShowStatus(Str("Str_PixelArt_Problem_FrameLimit", "At most 64 frames."), StatusKind.Warning);
        }

        private void BtnFrameDelete_Click(object sender, RoutedEventArgs e)
        {
            StopPlaying();
            _manager.DeleteFrame();
        }

        private void BtnFrameLeft_Click(object sender, RoutedEventArgs e) => _manager.MoveFrame(-1);
        private void BtnFrameRight_Click(object sender, RoutedEventArgs e) => _manager.MoveFrame(1);

        private int Fps() => int.TryParse((CmbFps.SelectedItem as ComboBoxItem)?.Tag as string, out int f) ? f : 8;

        private void CmbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (_playTimer.IsEnabled) _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Fps());
            ScheduleSave();
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_playTimer.IsEnabled) { StopPlaying(); return; }
            if (_manager.Document.Frames.Count < 2) return;
            _playFrame = _manager.CurrentFrame;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Fps());
            _playTimer.Start();
            IconPlay.Kind = PackIconKind.Stop;
            PlayStep();
        }

        private void PlayStep()
        {
            var doc = _manager.Document;
            if (doc.Frames.Count < 2) { StopPlaying(); return; }
            _playFrame = (_playFrame + 1) % doc.Frames.Count;
            PreviewImage.Source = PixelArtManager.CreateBitmap(doc.Frames[_playFrame], doc.Width, doc.Height);
            for (int i = 0; i < _frames.Count; i++) _frames[i].IsCurrent = i == _playFrame;
        }

        private void StopPlaying()
        {
            if (!_playTimer.IsEnabled && IconPlay.Kind == PackIconKind.Play) return;
            _playTimer.Stop();
            IconPlay.Kind = PackIconKind.Play;
            SyncFrames();
            UpdateThumbnails();
        }

        // =================================================================
        //  Export
        // =================================================================

        private int ExportScale() =>
            RadioScale1.IsChecked == true ? 1 : RadioScale2.IsChecked == true ? 2 : RadioScale4.IsChecked == true ? 4 : RadioScale16.IsChecked == true ? 16 : 8;

        private PixelArtExportOptions ReadExportOptions()
        {
            int frames = _manager.Document.Frames.Count;
            int columns = int.TryParse(TxtColumns.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? c : frames;
            return new PixelArtExportOptions { Scale = ExportScale(), Sheet = ChkSheet.IsChecked == true && frames > 1, Columns = Math.Clamp(columns, 1, frames) };
        }

        private void ExportOption_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (ChkSheet.IsChecked == true && sender == ChkSheet && !int.TryParse(TxtColumns.Text, out _)) TxtColumns.Text = _manager.Document.Frames.Count.ToString(CultureInfo.InvariantCulture);
            ColumnsPanel.Visibility = ChkSheet.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateExportInfo();
            ScheduleSave();
        }

        /// <summary>When the card is too narrow, the Copy / Export buttons drop to a second row.</summary>
        private void ExportRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = ExportRow.ActualWidth < 860;
            Grid.SetRow(ExportActions, narrow ? 1 : 0);
            Grid.SetColumn(ExportActions, narrow ? 0 : 1);
            Grid.SetColumnSpan(ExportActions, narrow ? 2 : 1);
            ExportActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            ExportActions.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        }

        private void ExportOption_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) UpdateExportInfo();
        }

        /// <summary>Says exactly how big the file will be before it is made.</summary>
        private void UpdateExportInfo()
        {
            if (!_viewConstructed) return;
            var doc = _manager.Document;
            var o = ReadExportOptions();
            int count = o.Sheet ? doc.Frames.Count : 1;
            int columns = o.Sheet ? o.Columns : 1;
            int rows = (count + columns - 1) / columns;
            long w = (long)doc.Width * columns * o.Scale, h = (long)doc.Height * rows * o.Scale;
            bool tooBig = w > PixelArtManager.MaxExportSide || h > PixelArtManager.MaxExportSide;
            TxtExportInfo.Text = tooBig
                ? string.Format(CultureInfo.CurrentCulture, Str("Str_PixelArt_ExportTooBig", "Too big: {0} × {1} px (the limit is {2})"), w, h, PixelArtManager.MaxExportSide)
                : string.Format(CultureInfo.CurrentCulture, Str("Str_PixelArt_ExportInfo", "Saves as {0} × {1} px"), w, h);
            TxtExportInfo.SetResourceReference(TextBlock.ForegroundProperty, tooBig ? "StatusErrorFg" : "TextSecondary");
            BtnExport.IsEnabled = BtnCopyImage.IsEnabled = !tooBig;
        }

        private async void MenuExportPng_Click(object sender, RoutedEventArgs e)
        {
            var options = ReadExportOptions();
            var dialog = new SaveFileDialog
            {
                Title = Str("Str_PixelArt_ExportPng"),
                Filter = "PNG|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = options.Sheet ? "sprite-sheet.png" : "pixel-art.png",
            };
            if (dialog.ShowDialog() != true) return;

            var doc = _manager.Document;
            int frame = _manager.CurrentFrame;
            string path = dialog.FileName;
            var result = PixelArtManager.Export(doc, frame, options);
            if (!result.Ok)
            {
                ShowStatus(Str("Str_PixelArt_Problem_" + result.ProblemCode, result.ProblemCode), StatusKind.Error);
                return;
            }
            string? error = await Task.Run(() => PixelArtManager.SaveBytes(path, result.Png));
            if (error != null)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_PixelArt_SaveFailed", "Couldn't save: {0}"), error), StatusKind.Error);
                return;
            }
            HideStatus();
            _manager.MarkSaved();
            ShowDone("Str_PixelArt_Saved");
        }

        private void BtnCopyImage_Click(object sender, RoutedEventArgs e)
        {
            var result = PixelArtManager.Export(_manager.Document, _manager.CurrentFrame, ReadExportOptions());
            if (!result.Ok)
            {
                ShowStatus(Str("Str_PixelArt_Problem_" + result.ProblemCode, result.ProblemCode), StatusKind.Error);
                return;
            }
            try
            {
                Clipboard.SetImage(PixelArtManager.BitmapFromPng(result.Png));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                ShowStatus(Str("Str_PixelArt_CopyFailed", "The clipboard is busy. Try again."), StatusKind.Warning);
                return;
            }
            HideStatus();
            ShowDone("Str_PixelArt_Copied");
        }

        // =================================================================
        //  Messages
        // =================================================================

        private void ShowDone(string key)
        {
            TxtDonePill.Text = Str(key);
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
        //  Keyboard
        // =================================================================

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;                 // typing in a box keeps its keys
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl)
            {
                if (e.Key == Key.Z) { if (shift) _manager.Redo(); else _manager.Undo(); e.Handled = true; }
                else if (e.Key == Key.Y) { _manager.Redo(); e.Handled = true; }
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { FitToView(); e.Handled = true; }
                return;
            }

            PixelArtTool? tool = e.Key switch
            {
                Key.B => PixelArtTool.Pencil, Key.E => PixelArtTool.Eraser, Key.G => PixelArtTool.Fill, Key.L => PixelArtTool.Line,
                Key.R => PixelArtTool.Rectangle, Key.O => PixelArtTool.Ellipse, Key.I => PixelArtTool.Picker, _ => null,
            };
            if (tool is { } t) { SetTool(t, save: true); e.Handled = true; return; }

            switch (e.Key)
            {
                case Key.Escape: CancelShape(); e.Handled = true; break;
                case Key.OemOpenBrackets: ChangeBrush(-1); e.Handled = true; break;
                case Key.OemCloseBrackets: ChangeBrush(1); e.Handled = true; break;
                case Key.OemPlus or Key.Add: StepZoom(1); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract: StepZoom(-1); e.Handled = true; break;
            }
        }

        private void ChangeBrush(int delta)
        {
            _brush = Math.Clamp(_brush + delta, 1, 4);
            (_brush switch { 2 => RadioBrush2, 3 => RadioBrush3, 4 => RadioBrush4, _ => RadioBrush1 }).IsChecked = true;
            ScheduleSave();
        }

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops the preview and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            CancelShape();
            _playTimer.Stop();
            _thumbTimer.Stop();
            _doneTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
