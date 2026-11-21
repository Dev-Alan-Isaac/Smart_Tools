using ImageMagick;
using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>PDF Reader &amp; Signer - making, keeping and forgetting the signature, and the in-app dialog used to draw or type it.</summary>
    public partial class PdfReaderView
    {
        private static readonly Color InkColor = Color.FromRgb(0x1F, 0x2A, 0x60);

        private byte[]? _signature;
        private double _signatureAspect = 0.4;

        // the in-app dialog
        private TaskCompletionSource<byte[]?>? _modal;
        private Func<byte[]?>? _modalProduce;
        private Action? _modalClear;

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

        private async void BtnSigDraw_Click(object sender, RoutedEventArgs e)
        {
            var ink = new InkCanvas
            {
                Background = Brushes.Transparent,
                DefaultDrawingAttributes = new DrawingAttributes { Color = InkColor, Width = 2.6, Height = 2.6, FitToCurve = true },
            };
            var paper = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Height = 200, Child = ink, ClipToBounds = true };
            paper.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var png = await AskModalAsync(T("Str_PdfReader_DrawTitle", "Draw your signature"), paper, () => RenderInk(ink), () => ink.Strokes.Clear(), ink);
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

        private async void BtnSigType_Click(object sender, RoutedEventArgs e)
        {
            var box = new TextBox { FontSize = 16, Margin = new Thickness(0, 0, 0, 10) };
            var preview = new TextBlock { FontFamily = new FontFamily("Segoe Script"), FontSize = 44, Foreground = new SolidColorBrush(InkColor), TextTrimming = TextTrimming.CharacterEllipsis };
            box.TextChanged += (_, _) => preview.Text = box.Text;
            var label = new TextBlock { Text = T("Str_PdfReader_TypeLabel", "Type your name"), Margin = new Thickness(0, 0, 0, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var paper = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4), Child = preview, MinHeight = 80 };
            paper.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var panel = new StackPanel();
            panel.Children.Add(label);
            panel.Children.Add(box);
            panel.Children.Add(paper);
            var png = await AskModalAsync(T("Str_PdfReader_TypeTitle", "Type your signature"), panel, () => RenderTyped(box.Text), null, box);
            if (png != null) SetSignature(png, remember: true);
        }

        private static byte[]? RenderTyped(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return null;
            var brush = new SolidColorBrush(InkColor);
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
                AppModal.Show(T("Str_PdfReader_PictureFailed", "That picture could not be used: ") + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // =================================================================
        //  The in-app dialog
        // =================================================================

        /// <summary>
        /// Shows <paramref name="content"/> in the dialog over the page (the page behind is switched off) with Clear / Cancel / Use.
        /// Returns what <paramref name="produce"/> made when Use was pressed, or null when the dialog was cancelled.
        /// </summary>
        private Task<byte[]?> AskModalAsync(string title, UIElement content, Func<byte[]?> produce, Action? clear, UIElement? focus)
        {
            if (_modal != null) return Task.FromResult<byte[]?>(null);
            _modal = new TaskCompletionSource<byte[]?>();
            _modalProduce = produce;
            _modalClear = clear;
            ModalTitle.Text = title;
            ModalBody.Content = content;
            BtnModalClear.Visibility = clear != null ? Visibility.Visible : Visibility.Collapsed;
            MainLayout.IsEnabled = false;
            ModalHost.Visibility = Visibility.Visible;
            Dispatcher.InvokeAsync(() => (focus ?? ModalHost).Focus());
            return _modal.Task;
        }

        private void CloseModal(byte[]? result)
        {
            var finished = _modal;
            _modal = null;
            _modalProduce = null;
            _modalClear = null;
            ModalHost.Visibility = Visibility.Collapsed;
            ModalBody.Content = null;
            MainLayout.IsEnabled = true;
            finished?.TrySetResult(result);
        }

        private void BtnModalUse_Click(object sender, RoutedEventArgs e)
        {
            var result = _modalProduce?.Invoke();
            if (result != null) CloseModal(result);
        }

        private void BtnModalCancel_Click(object sender, RoutedEventArgs e) => CloseModal(null);

        private void BtnModalClear_Click(object sender, RoutedEventArgs e) => _modalClear?.Invoke();

        private void Modal_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseModal(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox)
            {
                BtnModalUse_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
