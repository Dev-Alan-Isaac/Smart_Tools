using ImageMagick;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DragDropEffects = System.Windows.DragDropEffects;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Canvas = System.Windows.Controls.Canvas;
using Cursors = System.Windows.Input.Cursors;
using DragEventArgs = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// PDF Reader &amp; Signer - UI only. Pages are drawn with Windows' own PDF renderer; marks (text, date, signature, check)
    /// are placed on top with the mouse and written onto a new copy by <see cref="PdfReaderManager"/>. The original is never changed.
    /// This is a visual signature, not a certificate-based digital signature.
    /// </summary>
    public partial class PdfReaderView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const double MinZoom = 0.3, MaxZoom = 4.0;
        private const string DialogTitle = "PDF Reader & Signer";

        private readonly ObservableCollection<PdfReaderPage> _pages = new();
        private readonly Dictionary<PdfReaderPage, Canvas> _canvases = new();
        private readonly Dictionary<PdfMark, Border> _elements = new();
        private readonly SemaphoreSlim _renderGate = new(2);
        private PdfExtrasManager.PageRenderer? _renderer;
        private CancellationTokenSource _renderCts = new();
        private PdfReaderSettings _settings = new();
        private byte[]? _signature;
        private double _signatureAspect = 0.4;
        private string _path = "";
        private string _password = "";
        private double _zoom = 1.0;
        private PdfMark? _selected;
        private bool _viewConstructed, _loadedOnce, _optionsCollapsed, _updatingFields, _busy;

        // dragging a mark
        private PdfMark? _dragMark;
        private Point _dragOffset;

        public PdfReaderView()
        {
            InitializeComponent();
            PageList.ItemsSource = _pages;
            Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
            PreviewKeyDown += View_PreviewKeyDown;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops drawing pages and lets go of the file.</summary>
        public void CancelActiveRun()
        {
            _renderCts.Cancel();
            _renderer?.Dispose();
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = PdfReaderManager.LoadSettings();
            var saved = PdfReaderManager.LoadSignature();
            if (saved != null) SetSignature(saved, remember: false);
            UpdateZoomText();
            UpdateAll();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        // =================================================================
        //  Opening
        // =================================================================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf", Title = DialogTitle };
            if (dialog.ShowDialog() == true) _ = OpenAsync(dialog.FileName, null);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;
            var pdf = files.FirstOrDefault(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf != null) _ = OpenAsync(pdf, null);
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_path.Length > 0) _ = OpenAsync(_path, PwdBox.Password);
        }

        private void PwdBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnUnlock_Click(sender, e);
        }

        private async Task OpenAsync(string path, string? password)
        {
            if (_busy) return;
            if (_pages.Any(p => p.Marks.Count > 0) && _path != path)
            {
                var answer = MessageBox.Show(T("Str_PdfReader_DiscardMarks", "You have marks on the open PDF that are not saved. Open another PDF anyway?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            _busy = true;
            TxtProgress.Text = T("Str_PdfReader_Opening", "Opening…");
            TxtProgressDetail.Text = "";
            try
            {
                _renderCts.Cancel();
                var renderer = await PdfExtrasManager.PageRenderer.OpenAsync(path, password);
                var pages = new List<PdfReaderPage>();
                for (int i = 0; i < renderer.PageCount; i++)
                {
                    var (w, h) = renderer.SizeOf(i);
                    pages.Add(new PdfReaderPage { Index = i, WidthPt = w, HeightPt = h });
                }

                _renderer?.Dispose();
                _renderer = renderer;
                _renderCts = new CancellationTokenSource();
                _path = path;
                _password = password ?? "";
                _selected = null;
                _canvases.Clear();
                _elements.Clear();
                _pages.Clear();
                foreach (var p in pages) _pages.Add(p);

                PanelPassword.Visibility = Visibility.Collapsed;
                PwdBox.Clear();
                TxtFileName.Text = Path.GetFileName(path);
                TxtFileName.ToolTip = path;
                TxtPageInfo.Text = string.Format(T("Str_PdfReader_Pages", "{0} pages"), _pages.Count);
                _zoom = FitWidthZoom();
                ApplyZoom(redraw: false);
                Scroller.ScrollToTop();
                UpdateAll();
                ShowIdle();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _path = path;   // so Unlock can try again with a password
                TxtFileName.Text = Path.GetFileName(path);
                PanelPassword.Visibility = Visibility.Visible;
                TxtProgress.Text = T("Str_PdfReader_CantOpen", "This PDF could not be opened.");
                TxtProgressDetail.Text = T("Str_PdfReader_CantOpenDetail", "If it has a password, type it in the sidebar and press Unlock. Otherwise the file may be damaged.");
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShowIdle()
        {
            TxtProgress.Text = _pages.Count == 0
                ? T("Str_PdfReader_Idle", "Open a PDF to read it, sign it or fill it in.")
                : T("Str_PdfReader_IdleOpen", "Add your signature, text, a date or a check mark, then save a copy.");
            TxtProgressDetail.Text = T("Str_PdfReader_IdleDetail", "Your original is never changed. The signed PDF is saved as a new file.");
        }

        // =================================================================
        //  Pages: drawing and zoom
        // =================================================================

        private double FitWidthZoom()
        {
            if (_pages.Count == 0) return 1.0;
            double widest = _pages.Max(p => p.WidthPt);
            double room = Math.Max(200, Scroller.ActualWidth - 44);
            return Math.Clamp(room / widest, MinZoom, MaxZoom);
        }

        private void ApplyZoom(bool redraw)
        {
            foreach (var page in _pages)
            {
                page.DisplayWidth = page.WidthPt * _zoom;
                page.DisplayHeight = page.HeightPt * _zoom;
                if (redraw) page.Source = null;
            }
            UpdateZoomText();
            if (redraw)
            {
                foreach (var (page, canvas) in _canvases.ToList())
                {
                    FillCanvas(page, canvas);
                    _ = RenderPageAsync(page);
                }
            }
        }

        private void UpdateZoomText() => TxtZoom.Text = $"{Math.Round(_zoom * 100)}%";

        private void SetZoom(double zoom)
        {
            zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            if (Math.Abs(zoom - _zoom) < 0.001 || _pages.Count == 0) return;
            _zoom = zoom;
            ApplyZoom(redraw: true);
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.2);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.2);

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => SetZoom(FitWidthZoom());

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
            e.Handled = true;
        }

        private void PageBorder_Loaded(object sender, RoutedEventArgs e) => AttachPage(sender as Border);

        private void PageBorder_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachPage(sender as Border);

        /// <summary>A page container appeared (or got another page): fill its canvas with the marks and draw the page.</summary>
        private void AttachPage(Border? border)
        {
            if (border?.DataContext is not PdfReaderPage page || border.Child is not Grid { Children.Count: > 1 } grid || grid.Children[1] is not Canvas canvas) return;
            foreach (var key in _canvases.Where(kv => ReferenceEquals(kv.Value, canvas) && kv.Key != page).Select(kv => kv.Key).ToList()) _canvases.Remove(key);
            _canvases[page] = canvas;
            FillCanvas(page, canvas);
            if (page.Source == null) _ = RenderPageAsync(page);
        }

        private async Task RenderPageAsync(PdfReaderPage page)
        {
            var renderer = _renderer;
            if (renderer == null) return;
            var token = _renderCts.Token;
            int longSide = (int)Math.Clamp(Math.Max(page.DisplayWidth, page.DisplayHeight) * 1.25, 300, 3200);
            try
            {
                await _renderGate.WaitAsync(token);
                try
                {
                    if (page.Source != null || !_canvases.ContainsKey(page)) return;   // drawn meanwhile, or scrolled away
                    var picture = await renderer.RenderAsync(page.Index, longSide, token);
                    if (!token.IsCancellationRequested && ReferenceEquals(renderer, _renderer)) page.Source = picture;
                }
                finally
                {
                    _renderGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // A new PDF was opened or the app is closing.
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
            {
                // One page could not be drawn; it stays blank.
            }
        }

        // =================================================================
        //  Marks on the page
        // =================================================================

        private void FillCanvas(PdfReaderPage page, Canvas canvas)
        {
            canvas.Children.Clear();
            foreach (var mark in page.Marks)
            {
                var element = BuildElement(mark);
                _elements[mark] = element;
                canvas.Children.Add(element);
            }
        }

        private Border BuildElement(PdfMark mark)
        {
            FrameworkElement content = mark.Kind switch
            {
                PdfMarkKind.Signature => new Image { Source = DecodePng(mark.Image), Stretch = Stretch.Fill, Width = mark.Size * _zoom, Height = mark.Height * _zoom },
                PdfMarkKind.Check => BuildCheck(mark),
                _ => new TextBlock { Text = mark.Text, FontSize = Math.Max(4, mark.Size * _zoom), FontFamily = new FontFamily("Verdana, Arial"), Foreground = Brushes.Black },
            };
            var border = new Border
            {
                Child = content,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Tag = mark,
                Focusable = false,
            };
            Canvas.SetLeft(border, mark.X * _zoom);
            Canvas.SetTop(border, mark.Y * _zoom);
            border.MouseLeftButtonDown += Mark_MouseLeftButtonDown;
            border.MouseMove += Mark_MouseMove;
            border.MouseLeftButtonUp += Mark_MouseLeftButtonUp;
            if (ReferenceEquals(mark, _selected)) border.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            return border;
        }

        private FrameworkElement BuildCheck(PdfMark mark)
        {
            double s = mark.Size * _zoom;
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M {s * 0.15},{s * 0.55} L {s * 0.40},{s * 0.80} L {s * 0.85},{s * 0.20}")),
                Stroke = Brushes.Black,
                StrokeThickness = Math.Max(1, s * 0.1),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = s,
                Height = s,
            };
            return path;
        }

        private static BitmapSource DecodePng(byte[] bytes)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void PageCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Select(null);

        private void Mark_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: PdfMark mark } border) return;
            Select(mark);
            _dragMark = mark;
            _dragOffset = e.GetPosition(border);
            border.CaptureMouse();
            e.Handled = true;
        }

        private void Mark_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragMark == null || sender is not Border border || border.Parent is not Canvas canvas || !border.IsMouseCaptured) return;
            var page = _pages.FirstOrDefault(p => p.Index == _dragMark.PageIndex);
            if (page == null) return;
            var at = e.GetPosition(canvas);
            double x = Math.Clamp(at.X - _dragOffset.X, 0, Math.Max(0, page.DisplayWidth - 8));
            double y = Math.Clamp(at.Y - _dragOffset.Y, 0, Math.Max(0, page.DisplayHeight - 8));
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            _dragMark.X = x / _zoom;
            _dragMark.Y = y / _zoom;
        }

        private void Mark_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border) border.ReleaseMouseCapture();
            _dragMark = null;
        }

        private void Select(PdfMark? mark)
        {
            if (_selected != null && _elements.TryGetValue(_selected, out var old)) old.BorderBrush = Brushes.Transparent;
            _selected = mark;
            if (mark != null && _elements.TryGetValue(mark, out var now)) now.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            UpdateSelectedPanel();
        }

        private void UpdateSelectedPanel()
        {
            _updatingFields = true;
            try
            {
                bool any = _selected != null;
                TxtNothingSelected.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                PanelSelectedFields.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                if (_selected == null) return;

                PanelTextField.Visibility = _selected.Kind == PdfMarkKind.Text ? Visibility.Visible : Visibility.Collapsed;
                TxtMark.Text = _selected.Text;
                (SliderSize.Minimum, SliderSize.Maximum) = _selected.Kind switch
                {
                    PdfMarkKind.Signature => (40.0, 400.0),
                    PdfMarkKind.Check => (10.0, 80.0),
                    _ => (6.0, 72.0),
                };
                SliderSize.Value = Math.Clamp(_selected.Size, SliderSize.Minimum, SliderSize.Maximum);
                LblSize.Text = _selected.Kind == PdfMarkKind.Text ? T("Str_PdfReader_Size", "Size") : T("Str_PdfReader_SizeShape", "Size");
            }
            finally
            {
                _updatingFields = false;
            }
        }

        private void TxtMark_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingFields || _selected is not { Kind: PdfMarkKind.Text } mark) return;
            mark.Text = TxtMark.Text;
            if (_elements.TryGetValue(mark, out var element) && element.Child is TextBlock block) block.Text = mark.Text;
            UpdateAll();
        }

        private void SliderSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingFields || _selected == null) return;
            _selected.Size = e.NewValue;
            if (_elements.TryGetValue(_selected, out var old) && old.Parent is Canvas canvas)
            {
                var fresh = BuildElement(_selected);
                _elements[_selected] = fresh;
                int at = canvas.Children.IndexOf(old);
                canvas.Children.RemoveAt(at);
                canvas.Children.Insert(at, fresh);
            }
            if (_selected.Kind == PdfMarkKind.Text)
            {
                _settings.TextSize = (int)Math.Round(_selected.Size);
                PdfReaderManager.SaveSettings(_settings);
            }
        }

        private void BtnDeleteMark_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            if (_selected == null) return;
            var mark = _selected;
            var page = _pages.FirstOrDefault(p => p.Index == mark.PageIndex);
            page?.Marks.Remove(mark);
            if (_elements.TryGetValue(mark, out var element) && element.Parent is Canvas canvas) canvas.Children.Remove(element);
            _elements.Remove(mark);
            Select(null);
            UpdateAll();
        }

        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || _selected == null || Keyboard.FocusedElement is TextBox or PasswordBox) return;
            DeleteSelected();
            e.Handled = true;
        }

        // ---------------- Adding ----------------

        private void BtnAddSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            if (_signature == null)
            {
                TxtProgress.Text = T("Str_PdfReader_NeedSignature", "Draw, type or choose a signature first.");
                TxtProgressDetail.Text = "";
                return;
            }
            AddMark(PdfMarkKind.Signature, "", 150, _signature, _signatureAspect);
        }

        private void BtnAddText_Click(object sender, RoutedEventArgs e) => AddMark(PdfMarkKind.Text, T("Str_PdfReader_DefaultText", "Text"), _settings.TextSize);

        private void BtnAddDate_Click(object sender, RoutedEventArgs e) => AddMark(PdfMarkKind.Text, DateTime.Today.ToString("d", CultureInfo.CurrentCulture), _settings.TextSize);

        private void BtnAddCheck_Click(object sender, RoutedEventArgs e) => AddMark(PdfMarkKind.Check, "", 22);

        /// <summary>Puts a new mark in the middle of the part of the page that is on screen, selected, ready to drag.</summary>
        private void AddMark(PdfMarkKind kind, string text, double size, byte[]? image = null, double aspect = 0.4)
        {
            if (_pages.Count == 0) return;
            var (page, centerY) = VisiblePage();
            var mark = new PdfMark { Kind = kind, PageIndex = page.Index, Text = text, Size = size, Image = image ?? Array.Empty<byte>(), AspectRatio = aspect };
            double width = kind == PdfMarkKind.Text ? Math.Max(20, text.Length * size * 0.55) : mark.Size;
            mark.X = Math.Clamp(page.WidthPt / 2 - width / 2, 0, Math.Max(0, page.WidthPt - width));
            mark.Y = Math.Clamp(centerY - mark.Height / 2, 0, Math.Max(0, page.HeightPt - mark.Height));
            page.Marks.Add(mark);

            if (_canvases.TryGetValue(page, out var canvas))
            {
                var element = BuildElement(mark);
                _elements[mark] = element;
                canvas.Children.Add(element);
            }
            Select(mark);
            UpdateAll();
            if (kind == PdfMarkKind.Text) { TxtMark.Focus(); TxtMark.SelectAll(); }
        }

        /// <summary>The page most on screen and the middle of its visible part (in points).</summary>
        private (PdfReaderPage Page, double CenterY) VisiblePage()
        {
            PdfReaderPage? best = null;
            double bestVisible = -1, centerY = 0;
            foreach (var (page, canvas) in _canvases)
            {
                double top;
                try { top = canvas.TranslatePoint(new Point(0, 0), Scroller).Y; }
                catch (InvalidOperationException) { continue; }   // not in the visual tree any more
                double visTop = Math.Max(0, -top), visBottom = Math.Min(page.DisplayHeight, Scroller.ViewportHeight - top);
                double visible = visBottom - visTop;
                if (visible > bestVisible)
                {
                    bestVisible = visible;
                    best = page;
                    centerY = visible > 0 ? (visTop + visBottom) / 2 / _zoom : page.HeightPt / 2;
                }
            }
            return best != null ? (best, centerY) : (_pages[0], _pages[0].HeightPt / 2);
        }

        private void UpdateAll()
        {
            int marks = _pages.Sum(p => p.Marks.Count);
            bool open = _pages.Count > 0;
            PanelEmpty.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            BtnSave.IsEnabled = open && marks > 0 && !_busy;
            BtnAddSignature.IsEnabled = BtnAddText.IsEnabled = BtnAddDate.IsEnabled = BtnAddCheck.IsEnabled = open;
            if (open && marks > 0)
            {
                int onPages = _pages.Count(p => p.Marks.Count > 0);
                TxtProgressDetail.Text = string.Format(T("Str_PdfReader_MarksInfo", "{0} marks on {1} pages, not saved yet."), marks, onPages);
            }
        }

        // =================================================================
        //  Signature
        // =================================================================

        private void SetSignature(byte[] png, bool remember)
        {
            var bitmap = DecodePng(png);
            _signature = png;
            _signatureAspect = bitmap.PixelWidth > 0 ? (double)bitmap.PixelHeight / bitmap.PixelWidth : 0.4;
            SigPreview.Source = bitmap;
            TxtNoSignature.Visibility = Visibility.Collapsed;
            BtnSigForget.Visibility = Visibility.Visible;
            if (remember) PdfReaderManager.SaveSignature(png);
        }

        private void BtnSigForget_Click(object sender, RoutedEventArgs e)
        {
            _signature = null;
            SigPreview.Source = null;
            TxtNoSignature.Visibility = Visibility.Visible;
            BtnSigForget.Visibility = Visibility.Collapsed;
            PdfReaderManager.ForgetSignature();
        }

        private void BtnSigDraw_Click(object sender, RoutedEventArgs e)
        {
            var ink = new InkCanvas
            {
                Background = Brushes.White,
                DefaultDrawingAttributes = new DrawingAttributes { Color = Color.FromRgb(0x1F, 0x2A, 0x60), Width = 2.6, Height = 2.6, FitToCurve = true },
            };
            var png = AskWindow(T("Str_PdfReader_DrawTitle", "Draw your signature"), ink, 520, 300, () => RenderInk(ink), clear: () => ink.Strokes.Clear());
            if (png != null) SetSignature(png, remember: true);
        }

        private static byte[]? RenderInk(InkCanvas ink)
        {
            if (ink.Strokes.Count == 0) return null;
            var bounds = ink.Strokes.GetBounds();
            const double pad = 6, scale = 3;
            int w = (int)Math.Ceiling((bounds.Width + pad * 2) * scale), h = (int)Math.Ceiling((bounds.Height + pad * 2) * scale);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(pad - bounds.X, pad - bounds.Y));
                ink.Strokes.Draw(dc);
            }
            return ToPng(visual, Math.Max(1, w), Math.Max(1, h));
        }

        private void BtnSigType_Click(object sender, RoutedEventArgs e)
        {
            var box = new TextBox { FontSize = 16, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
            var preview = new TextBlock { FontFamily = new FontFamily("Segoe Script"), FontSize = 44, Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x60)), TextTrimming = TextTrimming.CharacterEllipsis };
            box.TextChanged += (_, _) => preview.Text = box.Text;
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = T("Str_PdfReader_TypeLabel", "Type your name"), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(box);
            panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4), Child = preview, MinHeight = 80 });
            var png = AskWindow(T("Str_PdfReader_TypeTitle", "Type your signature"), panel, 520, 260, () => RenderTyped(box.Text), clear: null, focus: box);
            if (png != null) SetSignature(png, remember: true);
        }

        private static byte[]? RenderTyped(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return null;
            var brush = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x60));
            brush.Freeze();
            const double size = 48, scale = 3;
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe Script"), size, brush, 96);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawText(formatted, new Point(6, 4));
            }
            return ToPng(visual, (int)Math.Ceiling((formatted.WidthIncludingTrailingWhitespace + 14) * scale), (int)Math.Ceiling((formatted.Height + 10) * scale));
        }

        private void BtnSigPicture_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = T("Str_PdfReader_PictureFilter", "Pictures") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|*.*|*.*", Title = T("Str_PdfReader_PictureTitle", "Choose a picture of your signature") };
            if (dialog.ShowDialog() != true) return;
            try
            {
                using var image = new MagickImage(dialog.FileName);
                if (!image.HasAlpha)
                {
                    image.ColorFuzz = new Percentage(18);
                    image.Transparent(MagickColors.White);   // a signature on white paper: keep the ink, drop the paper
                }
                image.Trim();
                if (image.Width > 1200) image.Resize(1200, 0);
                using var stream = new MemoryStream();
                image.Write(stream, MagickFormat.Png);
                SetSignature(stream.ToArray(), remember: true);
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(T("Str_PdfReader_PictureFailed", "That picture could not be used: ") + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static byte[] ToPng(DrawingVisual visual, int width, int height)
        {
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        /// <summary>A small modal window with the given content and Clear / Use / Cancel buttons. Returns what <paramref name="produce"/> made, or null.</summary>
        private byte[]? AskWindow(string title, UIElement content, double width, double height, Func<byte[]?> produce, Action? clear, UIElement? focus = null)
        {
            byte[]? result = null;
            var window = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
            };
            window.SetResourceReference(Window.BackgroundProperty, "ContentBg");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8, 14, 14) };
            if (clear != null)
            {
                var clearButton = new Button { Content = T("Str_PdfReader_Clear", "Clear"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
                clearButton.Click += (_, _) => clear();
                buttons.Children.Add(clearButton);
            }
            var cancel = new Button { Content = T("Str_PdfReader_Cancel", "Cancel"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var use = new Button { Content = T("Str_PdfReader_Use", "Use"), MinWidth = 80, IsDefault = true };
            use.Click += (_, _) =>
            {
                result = produce();
                if (result != null) window.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(use);

            var frame = new Border { Margin = new Thickness(14, 14, 14, 0), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = content };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(frame);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);
            window.Content = grid;
            if (focus != null) window.Loaded += (_, _) => focus.Focus();

            return window.ShowDialog() == true ? result : null;
        }

        // =================================================================
        //  Saving
        // =================================================================

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var marks = _pages.SelectMany(p => p.Marks).ToList();
            if (_path.Length == 0 || marks.Count == 0 || _busy) return;

            string folder = Path.GetDirectoryName(_path) ?? "";
            string suggestion = SafeFileOps.UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(_path) + "-signed.pdf"));
            var dialog = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                Title = DialogTitle,
                InitialDirectory = folder,
                FileName = Path.GetFileName(suggestion),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true) return;
            string output = dialog.FileName;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(T("Str_PdfReader_NotOriginal", "Choose a different name: the original PDF is never replaced."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _busy = true;
            BtnSave.IsEnabled = false;
            TxtProgress.Text = T("Str_PdfReader_Saving", "Saving…");
            TxtProgressDetail.Text = "";
            var widths = _pages.ToDictionary(p => p.Index, p => p.WidthPt);
            string input = _path, password = _password;
            try
            {
                await Task.Run(() => PdfReaderManager.Save(input, password, marks, output, widths));
                TxtProgress.Text = T("Str_PdfReader_Saved", "Saved a signed copy.");
                TxtProgressDetail.Text = output;
                ShowInFolder(output);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                TxtProgress.Text = T("Str_PdfReader_SaveFailed", "The copy could not be saved.");
                TxtProgressDetail.Text = ex.Message;
            }
            finally
            {
                _busy = false;
                UpdateSaveEnabled();
            }
        }

        private void UpdateSaveEnabled() => BtnSave.IsEnabled = !_busy && _pages.Any(p => p.Marks.Count > 0);

        private static void ShowInFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start; the path is shown in the status line.
            }
        }
    }
}
