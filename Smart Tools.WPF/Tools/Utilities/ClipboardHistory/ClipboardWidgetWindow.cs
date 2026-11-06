using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The clipboard history as a small floating list: borderless, always on top, not in the taskbar. Click a row to copy it
    /// back to the clipboard. It only reads <see cref="ClipboardHistoryManager"/> (the same history as the tool).
    /// </summary>
    internal sealed class ClipboardWidgetWindow : Window
    {
        private const int MaxRows = 12;

        private readonly ClipboardHistoryManager _history = ClipboardHistoryService.Instance;
        private readonly StackPanel _rows = new();
        private readonly TextBlock _status = new();
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly TextBlock _empty = new();

        public ClipboardWidgetWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = 330;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 40;
            Top = SystemParameters.WorkArea.Top + 80;
            Title = Loc.T("Str_Tool_clipboard-history_Name", "Clipboard History");
            UseLayoutRounding = true;

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 12),
                Margin = new Thickness(6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            // ---- header: drag handle + close ----
            var icon = new PackIcon { Kind = PackIconKind.ClipboardTextOutline, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(PackIcon.ForegroundProperty, "AccentFill");
            var title = new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(icon);
            left.Children.Add(title);

            var close = new Button { Width = 26, Height = 26, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false, ToolTip = Loc.T("Str_Notes_WidgetClose", "Close"), HorizontalAlignment = HorizontalAlignment.Right };
            var closeIcon = new PackIcon { Kind = PackIconKind.Close, Width = 15, Height = 15 };
            closeIcon.SetResourceReference(PackIcon.ForegroundProperty, "TextSecondary");
            close.Content = closeIcon;
            close.SetResourceReference(StyleProperty, "IconButtonStyle");
            close.Click += (_, _) => Close();

            var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(left);
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };

            // ---- rows ----
            _empty.Text = Loc.T("Str_Clipboard_WidgetEmpty", "Nothing copied yet.");
            _empty.FontSize = 12;
            _empty.Margin = new Thickness(2, 12, 2, 4);
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var scroll = new ScrollViewer { MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows, Margin = new Thickness(0, 8, 0, 0) };

            _status.FontSize = 11.5;
            _status.Margin = new Thickness(2, 6, 2, 0);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _status.Text = Loc.T("Str_Clipboard_WidgetHint", "Click an item to copy it again.");
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                _status.Text = Loc.T("Str_Clipboard_WidgetHint", "Click an item to copy it again.");
            };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(scroll);
            stack.Children.Add(_empty);
            stack.Children.Add(_status);
            card.Child = stack;
            Content = card;

            _history.Changed += Refresh;
            Closed += (_, _) => _history.Changed -= Refresh;
            Refresh();
        }

        private void Refresh()
        {
            _rows.Children.Clear();
            var items = _history.Snapshot().Take(MaxRows).ToList();
            _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in items) _rows.Children.Add(MakeRow(item));
        }

        private UIElement MakeRow(ClipboardItem item)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement leading;
            if (item.IsImage && Thumbnail(item.ImagePng) is { } thumb)
            {
                leading = new Image { Source = thumb, Width = 34, Height = 26, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 8, 0) };
            }
            else
            {
                var glyph = new PackIcon { Kind = item.IsImage ? PackIconKind.ImageOutline : PackIconKind.TextBoxOutline, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(PackIcon.ForegroundProperty, "AccentFill");
                leading = glyph;
            }
            grid.Children.Add(leading);

            var preview = new TextBlock { Text = item.Preview, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(preview, 1);
            grid.Children.Add(preview);

            if (item.IsPinned)
            {
                var pin = new PackIcon { Kind = PackIconKind.Pin, Width = 13, Height = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pin.SetResourceReference(PackIcon.ForegroundProperty, "TextSecondary");
                Grid.SetColumn(pin, 2);
                grid.Children.Add(pin);
            }

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = item.IsImage ? item.Preview : (item.Text.Length > 400 ? item.Text[..400] + "…" : item.Text),
            };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "AccentColorSubtle");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => CopyBack(item);
            return row;
        }

        private void CopyBack(ClipboardItem item)
        {
            bool ok = _history.CopyBack(item);
            _status.Text = ok ? Loc.T("Str_Clipboard_Copied", "Copied to the clipboard.") : Loc.T("Str_Clipboard_CopyFailed", "Couldn't copy - another program is using the clipboard.");
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private static BitmapSource? Thumbnail(byte[]? png)
        {
            if (png == null || png.Length == 0) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 68;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>The one clipboard widget: open it from the tool, close it from the widget or the tool.</summary>
    internal static class ClipboardWidgetManager
    {
        private static ClipboardWidgetWindow? _window;

        public static event Action? Changed;

        public static bool IsOpen => _window != null;

        public static void Toggle()
        {
            if (_window != null)
            {
                _window.Close();
                return;
            }
            _window = new ClipboardWidgetWindow();
            _window.Closed += (_, _) =>
            {
                _window = null;
                Changed?.Invoke();
            };
            _window.Show();
            Changed?.Invoke();
        }
    }
}
