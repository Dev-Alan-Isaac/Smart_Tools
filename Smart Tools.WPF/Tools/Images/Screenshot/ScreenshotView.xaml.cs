using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Border = System.Windows.Controls.Border;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Screenshot - UI only. Delays, names, arrow maths and settings are in <see cref="ScreenshotManager"/>; the screen is
    /// captured by <see cref="ScreenOcrManager.CaptureVirtualScreen"/> and the region overlay is <see cref="ScreenOcrSelectionWindow"/>.
    /// The picture and the marks on it share one coordinate system (the picture's pixels), so saving is exact.
    /// </summary>
    public partial class ScreenshotView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        /// <summary>The ink colors. These are the user's drawing colors, not theme colors.</summary>
        private static readonly Color[] Palette =
        {
            Colors.Red, Colors.Orange, Colors.Gold, Colors.LimeGreen, Colors.DodgerBlue, Colors.White, Colors.Black,
        };

        private readonly bool _viewConstructed;
        private ScreenshotSettings _settings = new();
        private BitmapSource? _bitmap;
        private ScreenshotInk _ink = ScreenshotInk.Pen;
        private Point? _start;
        private UIElement? _drawing;
        private TextBox? _textBox;
        private CancellationTokenSource? _cts;
        private bool _capturing;
        private bool _optionsCollapsed;
        private bool _applying;
        private bool _loadedOnce;

        /// <summary>A picture was taken, by the button or by the global shortcut. The shell shows this page.</summary>
        public event Action? Captured;

        public ScreenshotView()
        {
            InitializeComponent();
            BuildColorSwatches();
            Loaded += (_, _) =>
            {
                if (_loadedOnce) return;
                _loadedOnce = true;
                ApplySettings(ScreenshotManager.LoadSettings());
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a countdown and keeps the options.</summary>
        public void CancelActiveRun()
        {
            _cts?.Cancel();
            SaveSettings();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;

        // =================================================================
        //  Options
        // =================================================================

        private void ApplySettings(ScreenshotSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                RadioRegion.IsChecked = s.Mode == ScreenshotMode.Region;
                RadioWindow.IsChecked = s.Mode == ScreenshotMode.Window;
                RadioScreen.IsChecked = s.Mode == ScreenshotMode.FullScreen;
                DelayCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScreenshotManager.DelayChoices, s.DelaySeconds));
                ChkHideApp.IsChecked = s.HideApp;
                ChkCopyAfter.IsChecked = s.CopyAfterCapture;
                SliderThickness.Value = s.Thickness;
                SelectColor(s.ColorIndex);
            }
            finally
            {
                _applying = false;
            }
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            ScreenshotManager.SaveSettings(_settings);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _settings.DelaySeconds = DelayCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int seconds) ? seconds : 0;
            _settings.HideApp = ChkHideApp.IsChecked == true;
            _settings.CopyAfterCapture = ChkCopyAfter.IsChecked == true;
            SaveSettings();
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse(tag, out ScreenshotMode mode))
            {
                _settings.Mode = mode;
                SaveSettings();
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // =================================================================
        //  Taking the picture
        // =================================================================

        private async void BtnCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_capturing)
            {
                _cts?.Cancel();   // the button reads "Cancel" during the countdown
                return;
            }
            await CaptureAsync();
        }

        /// <summary>Takes a picture with the current options. Also called by the global shortcut, even before this page was ever opened.</summary>
        public async Task CaptureNowAsync()
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(ScreenshotManager.LoadSettings());
            }
            await CaptureAsync();
        }

        private async Task CaptureAsync()
        {
            if (_capturing) return;
            _capturing = true;
            CommitText();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            BtnCapture.Content = Str("Str_Screenshot_Cancel");

            var main = Application.Current?.MainWindow;
            bool hidden = false;
            ScreenOcrImage? picture = null;
            string? failure = null;
            try
            {
                // A window picture needs time to bring the other window to the front, so it always waits a little.
                int delay = _settings.Mode == ScreenshotMode.Window ? Math.Max(3, _settings.DelaySeconds) : _settings.DelaySeconds;
                for (int left = delay; left > 0; left--)
                {
                    SetInfo(string.Format(CultureInfo.CurrentCulture, Str("Str_Screenshot_Countdown"), left), error: false);
                    await Task.Delay(1000, token);
                }

                if (_settings.HideApp && main is { IsVisible: true })
                {
                    main.Hide();
                    hidden = true;
                    await Task.Delay(300, token);   // let Windows repaint what was behind the app
                }

                var screen = await Task.Run(ScreenOcrManager.CaptureVirtualScreen, token);
                picture = _settings.Mode switch
                {
                    ScreenshotMode.FullScreen => screen,
                    ScreenshotMode.Window => CropToFrontWindow(screen, out failure),
                    _ => PickRegion(screen, main),
                };
            }
            catch (OperationCanceledException)
            {
                // Cancelled during the countdown: nothing to show.
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // Typically the secure desktop (UAC prompt, lock screen) - nothing can be captured then.
                failure = string.Format(CultureInfo.CurrentCulture, Str("Str_Screenshot_CaptureFailed"), ex.Message);
            }
            finally
            {
                if (hidden && main != null)
                {
                    main.Show();
                    main.Activate();
                }
                _capturing = false;
                BtnCapture.Content = Str("Str_Screenshot_CaptureButton");
                _cts?.Dispose();
                _cts = null;
            }

            if (failure != null)
            {
                SetInfo(failure, error: true);
                return;
            }
            if (picture == null)
            {
                SetInfo(Str("Str_Screenshot_ReadyInfo"), error: false);
                return;
            }

            ShowPicture(picture);
            if (_settings.CopyAfterCapture) CopyToClipboard();
            Captured?.Invoke();
        }

        /// <summary>The overlay over a frozen picture of the screen; null when cancelled.</summary>
        private ScreenOcrImage? PickRegion(ScreenOcrImage screen, Window? main)
        {
            var overlay = new ScreenOcrSelectionWindow(
                ScreenOcrSelectionWindow.ToBitmapSource(screen), VirtualScreenInDips(main),
                Str("Str_Screenshot_SelectionTitle"), Str("Str_Screenshot_SelectionHint"));
            overlay.ShowDialog();
            return overlay.Selection is Int32Rect r ? ScreenOcrManager.Crop(screen, r.X, r.Y, r.Width, r.Height) : null;
        }

        private static ScreenOcrImage? CropToFrontWindow(ScreenOcrImage screen, out string? failure)
        {
            failure = null;
            var bounds = ScreenshotManager.ForegroundWindowBounds();
            var origin = System.Windows.Forms.SystemInformation.VirtualScreen;
            var clamped = bounds is { } b
                ? ScreenshotManager.ClampToImage(b.X - origin.Left, b.Y - origin.Top, b.Width, b.Height, screen.Width, screen.Height)
                : null;
            if (clamped is not { } c)
            {
                failure = (Application.Current?.TryFindResource("Str_Screenshot_NoWindow") as string) ?? "No other window to take";
                return null;
            }
            return ScreenOcrManager.Crop(screen, c.X, c.Y, c.Width, c.Height);
        }

        /// <summary>All monitors in WPF units, for the overlay window.</summary>
        private Rect VirtualScreenInDips(Window? main)
        {
            var px = System.Windows.Forms.SystemInformation.VirtualScreen;
            var dpi = VisualTreeHelper.GetDpi(main ?? (Visual)this);
            return new Rect(px.Left / dpi.DpiScaleX, px.Top / dpi.DpiScaleY, px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
        }

        private void ShowPicture(ScreenOcrImage image)
        {
            _bitmap = ScreenOcrSelectionWindow.ToBitmapSource(image);
            Picture.Source = _bitmap;
            Stage.Width = Marks.Width = _bitmap.PixelWidth;
            Stage.Height = Marks.Height = _bitmap.PixelHeight;
            Marks.Children.Clear();
            EmptyPanel.Visibility = Visibility.Collapsed;
            StageBox.Visibility = Visibility.Visible;
            BtnCopy.IsEnabled = BtnSave.IsEnabled = true;
            UpdateMarkButtons();
            SetInfo(string.Format(CultureInfo.CurrentCulture, Str("Str_Screenshot_Size"), _bitmap.PixelWidth, _bitmap.PixelHeight), error: false);
            UiMotion.FadeIn(StageBox);
        }

        private void SetInfo(string text, bool error)
        {
            TxtInfo.Text = text;
            TxtInfo.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }

        // =================================================================
        //  Marking up
        // =================================================================

        private void BuildColorSwatches()
        {
            for (int i = 0; i < Palette.Length; i++)
            {
                var swatch = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 6, 0),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Palette[i]),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = i,
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, "SidebarSeparator");
                swatch.MouseLeftButtonUp += (s, _) =>
                {
                    if (s is FrameworkElement { Tag: int index }) SelectColor(index, save: true);
                };
                PanelColors.Children.Add(swatch);
            }
        }

        private void SelectColor(int index, bool save = false)
        {
            index = Math.Clamp(index, 0, Palette.Length - 1);
            _settings.ColorIndex = index;
            foreach (Border swatch in PanelColors.Children)
            {
                bool on = (int)swatch.Tag == index;
                swatch.SetResourceReference(Border.BorderBrushProperty, on ? "AccentColor" : "SidebarSeparator");
            }
            if (save && _viewConstructed) SaveSettings();
        }

        private Color InkColor => Palette[Math.Clamp(_settings.ColorIndex, 0, Palette.Length - 1)];

        private double Thickness => SliderThickness.Value;

        private void SliderThickness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _applying) return;
            _settings.Thickness = (int)SliderThickness.Value;
            SaveSettings();
        }

        private void Ink_Click(object sender, RoutedEventArgs e)
        {
            CommitText();
            if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse(tag, out ScreenshotInk ink)) _ink = ink;
        }

        private void Marks_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_bitmap == null) return;
            CommitText();
            var at = e.GetPosition(Marks);
            if (_ink == ScreenshotInk.Text)
            {
                BeginText(at);
                e.Handled = true;
                return;
            }

            _start = at;
            _drawing = StartShape(at);
            Marks.Children.Add(_drawing);
            Marks.CaptureMouse();
        }

        private UIElement StartShape(Point at)
        {
            var brush = new SolidColorBrush(InkColor);
            switch (_ink)
            {
                case ScreenshotInk.Pen:
                    var line = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                    line.Points.Add(at);
                    return line;
                case ScreenshotInk.Arrow:
                    return new System.Windows.Shapes.Path
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                case ScreenshotInk.Highlight:
                    return new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x66, InkColor.R, InkColor.G, InkColor.B)) };
                case ScreenshotInk.Pixelate:
                    // Only an outline while dragging; the pixelated picture replaces it on release.
                    var outline = new Rectangle { StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    outline.SetResourceReference(Shape.StrokeProperty, "AccentColor");
                    return outline;
                default:
                    return new Rectangle { Stroke = brush, StrokeThickness = Thickness };
            }
        }

        private void Marks_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_start is not Point start || _drawing == null || e.LeftButton != MouseButtonState.Pressed) return;
            var at = Clamp(e.GetPosition(Marks));
            switch (_drawing)
            {
                case Polyline pen:
                    pen.Points.Add(at);
                    break;
                case System.Windows.Shapes.Path arrow:
                    arrow.Data = ArrowGeometry(start, at, Thickness);
                    break;
                case Rectangle box:
                    var (x, y, w, h) = ScreenshotManager.Normalize(start.X, start.Y, at.X, at.Y);
                    Canvas.SetLeft(box, x);
                    Canvas.SetTop(box, y);
                    box.Width = w;
                    box.Height = h;
                    break;
            }
        }

        private void Marks_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_start is not Point start || _drawing == null) return;
            Marks.ReleaseMouseCapture();
            var at = Clamp(e.GetPosition(Marks));
            var finished = _drawing;
            _start = null;
            _drawing = null;

            bool tooSmall = finished switch
            {
                Polyline pen => pen.Points.Count < 2,
                System.Windows.Shapes.Path => (at - start).Length < 4,
                Rectangle box => box.Width < 4 || box.Height < 4,
                _ => false,
            };
            if (tooSmall)
            {
                Marks.Children.Remove(finished);
            }
            else if (_ink == ScreenshotInk.Pixelate && finished is Rectangle area)
            {
                Marks.Children.Remove(area);
                AddPixelated(Canvas.GetLeft(area), Canvas.GetTop(area), area.Width, area.Height);
            }
            UpdateMarkButtons();
        }

        private Point Clamp(Point p) =>
            new(Math.Clamp(p.X, 0, Marks.Width), Math.Clamp(p.Y, 0, Marks.Height));

        private static Geometry ArrowGeometry(Point from, Point to, double thickness)
        {
            var (left, right) = ScreenshotManager.ArrowHead(from.X, from.Y, to.X, to.Y, ScreenshotManager.ArrowHeadSize(thickness));
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(from, false, false);
                ctx.LineTo(to, true, true);
                ctx.BeginFigure(new Point(left.X, left.Y), false, false);
                ctx.LineTo(to, true, true);
                ctx.LineTo(new Point(right.X, right.Y), true, true);
            }
            geometry.Freeze();
            return geometry;
        }

        /// <summary>A rectangle filled with a shrunken copy of that part of the picture, enlarged without smoothing: blocks you can't read through.</summary>
        private void AddPixelated(double x, double y, double width, double height)
        {
            if (_bitmap == null) return;
            var area = ScreenshotManager.ClampToImage((int)x, (int)y, (int)Math.Ceiling(width), (int)Math.Ceiling(height), _bitmap.PixelWidth, _bitmap.PixelHeight);
            if (area is not { } a) return;

            var crop = new CroppedBitmap(_bitmap, new Int32Rect(a.X, a.Y, a.Width, a.Height));
            double scale = 1.0 / ScreenshotManager.PixelBlock(a.Width, a.Height);
            var small = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
            small.Freeze();

            var block = new Rectangle
            {
                Width = a.Width,
                Height = a.Height,
                Fill = new ImageBrush(small) { Stretch = Stretch.Fill },
            };
            RenderOptions.SetBitmapScalingMode(block, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(block, a.X);
            Canvas.SetTop(block, a.Y);
            Marks.Children.Add(block);
        }

        // ---------------- Text ----------------

        private void BeginText(Point at)
        {
            var brush = new SolidColorBrush(InkColor);
            var box = new TextBox
            {
                MinWidth = 80,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Foreground = brush,
                CaretBrush = brush,
                FontSize = 14 + Thickness * 3,
                FontWeight = FontWeights.Bold,
                AcceptsReturn = true,
            };
            box.SetResourceReference(Control.BorderBrushProperty, "AccentColor");
            Canvas.SetLeft(box, at.X);
            Canvas.SetTop(box, at.Y);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    box.Text = "";
                    CommitText();
                }
                else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    CommitText();
                }
            };
            box.LostKeyboardFocus += (_, _) => CommitText();
            _textBox = box;
            Marks.Children.Add(box);
            Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
        }

        /// <summary>Turns the box being typed in into plain text on the picture (or drops it when empty).</summary>
        private void CommitText()
        {
            var box = _textBox;
            if (box == null) return;
            _textBox = null;
            Marks.Children.Remove(box);
            if (!string.IsNullOrWhiteSpace(box.Text))
            {
                var text = new TextBlock
                {
                    Text = box.Text,
                    Foreground = box.Foreground,
                    FontSize = box.FontSize,
                    FontWeight = FontWeights.Bold,
                };
                Canvas.SetLeft(text, Canvas.GetLeft(box) + 1);
                Canvas.SetTop(text, Canvas.GetTop(box) + 1);
                Marks.Children.Add(text);
            }
            UpdateMarkButtons();
        }

        // ---------------- Undo ----------------

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            CommitText();
            if (Marks.Children.Count > 0) Marks.Children.RemoveAt(Marks.Children.Count - 1);
            UpdateMarkButtons();
        }

        private void BtnClearMarks_Click(object sender, RoutedEventArgs e)
        {
            _textBox = null;
            Marks.Children.Clear();
            UpdateMarkButtons();
        }

        private void UpdateMarkButtons() => BtnUndo.IsEnabled = BtnClearMarks.IsEnabled = Marks.Children.Count > 0;

        // =================================================================
        //  Copy and save
        // =================================================================

        /// <summary>The picture with its marks, at the picture's own size.</summary>
        private BitmapSource? Render()
        {
            if (_bitmap == null) return null;
            CommitText();
            int w = _bitmap.PixelWidth, h = _bitmap.PixelHeight;
            Stage.UpdateLayout();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(Stage), null, new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

        private void CopyToClipboard()
        {
            var image = Render();
            if (image == null) return;
            try
            {
                Clipboard.SetImage(image);
                SetInfo(Str("Str_Screenshot_Copied"), error: false);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                SetInfo(Str("Str_Screenshot_CopyFailed"), error: true);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var image = Render();
            if (image == null) return;
            var dialog = new SaveFileDialog
            {
                Title = Str("Str_Screenshot_Save"),
                Filter = ScreenshotManager.SaveFilter,
                FileName = ScreenshotManager.SuggestedName(DateTime.Now),
                AddExtension = true,
                DefaultExt = ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog() != true) return;

            BitmapEncoder encoder = ScreenshotManager.FormatOf(dialog.FileName) switch
            {
                "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
                "bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            try
            {
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(stream);
                SetInfo(string.Format(CultureInfo.CurrentCulture, Str("Str_Screenshot_Saved"), System.IO.Path.GetFileName(dialog.FileName)), error: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetInfo(string.Format(CultureInfo.CurrentCulture, Str("Str_Screenshot_SaveFailed"), ex.Message), error: true);
            }
        }
    }
}
