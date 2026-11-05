using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Canvas = System.Windows.Controls.Canvas;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>PDF Reader &amp; Signer - the things placed on a page: adding, dragging, resizing and deleting them.</summary>
    public partial class PdfReaderView
    {
        private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(110, 255, 235, 59)));

        private PdfMark? _selected;
        private bool _updatingFields;

        /// <summary>True while there are marks that were added or changed since the PDF was opened or last saved.</summary>
        private bool _unsaved;

        // dragging a mark
        private PdfMark? _dragMark;
        private Point _dragOffset;

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

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
                PdfMarkKind.Check => BuildStroke(mark, check: true),
                PdfMarkKind.Cross => BuildStroke(mark, check: false),
                PdfMarkKind.Line => BuildLine(mark),
                PdfMarkKind.Highlight => new Rectangle { Fill = HighlightBrush, Width = mark.Size * _zoom, Height = mark.Height * _zoom },
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

        /// <summary>A check mark or a cross, drawn as a round-capped stroke.</summary>
        private FrameworkElement BuildStroke(PdfMark mark, bool check)
        {
            double s = mark.Size * _zoom;
            string data = check
                ? string.Create(CultureInfo.InvariantCulture, $"M {s * 0.15},{s * 0.55} L {s * 0.40},{s * 0.80} L {s * 0.85},{s * 0.20}")
                : string.Create(CultureInfo.InvariantCulture, $"M {s * 0.20},{s * 0.20} L {s * 0.80},{s * 0.80} M {s * 0.80},{s * 0.20} L {s * 0.20},{s * 0.80}");
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = Brushes.Black,
                StrokeThickness = Math.Max(1, s * 0.1),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = s,
                Height = s,
            };
        }

        /// <summary>A horizontal line: a thin bar in the middle of a taller box, so it is easy to grab.</summary>
        private FrameworkElement BuildLine(PdfMark mark)
        {
            var grid = new Grid { Width = mark.Size * _zoom, Height = PdfMark.LineBox * _zoom, Background = Brushes.Transparent };
            grid.Children.Add(new Rectangle { Height = Math.Max(1, PdfReaderManager.LineThickness * _zoom), Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Center });
            return grid;
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
            if (Math.Abs(_dragMark.X - x / _zoom) > 0.01 || Math.Abs(_dragMark.Y - y / _zoom) > 0.01) _unsaved = true;
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
                    PdfMarkKind.Check or PdfMarkKind.Cross => (10.0, 80.0),
                    PdfMarkKind.Line or PdfMarkKind.Highlight => (30.0, 500.0),
                    _ => (6.0, 72.0),
                };
                SliderSize.Value = Math.Clamp(_selected.Size, SliderSize.Minimum, SliderSize.Maximum);
                LblSize.Text = _selected.Kind is PdfMarkKind.Line or PdfMarkKind.Highlight
                    ? T("Str_PdfReader_Width", "Width")
                    : T("Str_PdfReader_Size", "Size");
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
            _unsaved = true;
            if (_elements.TryGetValue(mark, out var element) && element.Child is TextBlock block) block.Text = mark.Text;
            UpdateAll();
        }

        private void SliderSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingFields || _selected == null) return;
            _selected.Size = e.NewValue;
            _unsaved = true;
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
            _unsaved = true;
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

        /// <summary>Adds the kind picked in the dropdown.</summary>
        private void BtnAddMark_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            switch ((CmbAddKind.SelectedItem as ComboBoxItem)?.Tag as string)
            {
                case "Signature":
                    if (_signature == null)
                    {
                        TxtProgress.Text = T("Str_PdfReader_NeedSignature", "Draw, type or choose a signature first.");
                        TxtProgressDetail.Text = "";
                        return;
                    }
                    AddMark(PdfMarkKind.Signature, "", 150, _signature, _signatureAspect);
                    break;
                case "Text":
                    AddMark(PdfMarkKind.Text, T("Str_PdfReader_DefaultText", "Text"), _settings.TextSize);
                    break;
                case "Date":
                    AddMark(PdfMarkKind.Text, DateTime.Today.ToString("d", CultureInfo.CurrentCulture), _settings.TextSize);
                    break;
                case "Check":
                    AddMark(PdfMarkKind.Check, "", 22);
                    break;
                case "Cross":
                    AddMark(PdfMarkKind.Cross, "", 22);
                    break;
                case "Line":
                    AddMark(PdfMarkKind.Line, "", 140);
                    break;
                case "Highlight":
                    AddMark(PdfMarkKind.Highlight, "", 150);
                    break;
            }
        }

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
            _unsaved = true;

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
            InputCard.SetEmpty(_files.Count == 0);
            CmbAddKind.IsEnabled = BtnAddMark.IsEnabled = open;
            if (open && marks > 0 && _unsaved)
            {
                int onPages = _pages.Count(p => p.Marks.Count > 0);
                TxtProgressDetail.Text = string.Format(T("Str_PdfReader_MarksInfo", "{0} marks on {1} pages, not saved yet."), marks, onPages);
            }
        }
    }
}
