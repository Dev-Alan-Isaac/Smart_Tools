using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The floating window of the Network Speed Widget: borderless, always on top, not in the taskbar.
    /// Drag to move, right-click for the menu. Shows download / upload, a small graph and (optionally)
    /// the adapter with its manufacturer; mini mode shows only the two numbers. It only displays what
    /// the view hands it - the numbers come from <see cref="NetworkSpeedWidgetManager"/>.
    /// Colours are theme tokens (SetResourceReference), so a theme switch repaints it.
    /// </summary>
    internal sealed class NetworkSpeedWidgetWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;

        private readonly Border _card = new();
        private readonly TextBlock _downValue, _downUnit, _upValue, _upUnit, _adapterLine;
        private readonly SystemMonitorSparkline _graph = new();
        private readonly StackPanel _detail = new();
        private readonly MenuItem _miniItem, _closeItem, _cornerItem;
        private bool _clickThrough;

        /// <summary>The user asked to close the widget (menu).</summary>
        public event Action? CloseRequested;

        /// <summary>The user dragged / snapped the widget (Left, Top).</summary>
        public event Action<double, double>? PositionChanged;

        /// <summary>The user flipped mini mode from the widget's menu.</summary>
        public event Action<bool>? MiniModeToggled;

        public NetworkSpeedWidgetWindow(NetWidgetSettings settings)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Network Speed";
            UseLayoutRounding = true;

            _card.CornerRadius = new CornerRadius(12);
            _card.BorderThickness = new Thickness(1);
            _card.Padding = new Thickness(12, 8, 12, 8);
            _card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            _card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            _card.Cursor = Cursors.SizeAll;

            (_downValue, _downUnit) = Number();
            (_upValue, _upUnit) = Number();

            var numbers = new StackPanel { Orientation = Orientation.Horizontal };
            numbers.Children.Add(Pair("↓", _downValue, _downUnit, new Thickness(0, 0, 16, 0)));
            numbers.Children.Add(Pair("↑", _upValue, _upUnit, new Thickness(0)));

            _graph.Height = 30;
            _graph.Capacity = NetworkSpeedWidgetManager.HistoryCapacity;
            _graph.Margin = new Thickness(0, 6, 0, 0);
            _graph.SetResourceReference(SystemMonitorSparkline.StrokeProperty, "AccentFill");

            _adapterLine = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 };
            _adapterLine.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            _detail.Children.Add(_graph);
            _detail.Children.Add(_adapterLine);

            var root = new StackPanel();
            root.Children.Add(numbers);
            root.Children.Add(_detail);
            _card.Child = root;
            Content = _card;

            _miniItem = new MenuItem { IsCheckable = true };
            _miniItem.Click += (_, _) => MiniModeToggled?.Invoke(_miniItem.IsChecked);
            _cornerItem = new MenuItem();
            _cornerItem.Click += (_, _) => SnapToCorner();
            _closeItem = new MenuItem();
            _closeItem.Click += (_, _) => CloseRequested?.Invoke();
            _card.ContextMenu = new ContextMenu { Items = { _miniItem, _cornerItem, new Separator(), _closeItem } };
            RefreshMenuText();

            _card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch (InvalidOperationException) { return; }
                PositionChanged?.Invoke(Left, Top);
            };

            ApplySettings(settings);
            Loaded += (_, _) => { PlaceOnScreen(settings); ApplyClickThrough(); };
        }

        private static (TextBlock Value, TextBlock Unit) Number()
        {
            var value = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, MinWidth = 46, TextAlignment = TextAlignment.Right, Text = "0" };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var unit = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 36 };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return (value, unit);
        }

        private static StackPanel Pair(string arrow, TextBlock value, TextBlock unit, Thickness margin)
        {
            var a = new TextBlock { Text = arrow, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            a.SetResourceReference(TextBlock.ForegroundProperty, "AccentFill");
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
            p.Children.Add(a);
            p.Children.Add(value);
            p.Children.Add(unit);
            return p;
        }

        private static string Text(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Re-reads the menu texts (language can change while the widget is open).</summary>
        public void RefreshMenuText()
        {
            _miniItem.Header = Text("Str_NetWidget_Mini", "Mini mode");
            _cornerItem.Header = Text("Str_NetWidget_Corner", "Snap to corner");
            _closeItem.Header = Text("Str_NetWidget_Close", "Close widget");
        }

        public void ApplySettings(NetWidgetSettings s)
        {
            Opacity = Math.Clamp(s.Opacity, 0.3, 1.0);
            _detail.Visibility = s.MiniMode ? Visibility.Collapsed : Visibility.Visible;
            _adapterLine.Visibility = s.ShowManufacturer ? Visibility.Visible : Visibility.Collapsed;
            _miniItem.IsChecked = s.MiniMode;
            _clickThrough = s.ClickThrough;
            ApplyClickThrough();
        }

        /// <summary>Shows one reading. <paramref name="adapterText"/> is the adapter + manufacturer line.</summary>
        public void Update(NetWidgetReading r, IReadOnlyList<double> down, IReadOnlyList<double> up, NetWidgetUnits units, string adapterText)
        {
            var d = NetworkSpeedWidgetManager.FormatRate(r.DownBytesPerSec, units);
            var u = NetworkSpeedWidgetManager.FormatRate(r.UpBytesPerSec, units);
            _downValue.Text = d.Value;
            _downUnit.Text = d.Unit;
            _upValue.Text = u.Value;
            _upUnit.Text = u.Unit;
            _graph.Maximum = Math.Max(1024, Math.Max(down.Count > 0 ? down.Max() : 0, up.Count > 0 ? up.Max() : 0) * 1.15);
            _graph.Values = down.ToArray();
            _graph.Values2 = up.ToArray();
            _adapterLine.Text = adapterText;
            _adapterLine.ToolTip = adapterText;
        }

        /// <summary>Bottom-right corner of the work area.</summary>
        public void SnapToCorner()
        {
            var wa = SystemParameters.WorkArea;
            UpdateLayout();
            Left = wa.Right - ActualWidth - 16;
            Top = wa.Bottom - ActualHeight - 16;
            PositionChanged?.Invoke(Left, Top);
        }

        private void PlaceOnScreen(NetWidgetSettings s)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (s.Left is double l && s.Top is double t && screen.Contains(new System.Windows.Point(l + 20, t + 20)))
            {
                Left = l;
                Top = t;
            }
            else
            {
                SnapToCorner();
            }
        }

        private void ApplyClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow;
            style = _clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
            SetWindowLong(hwnd, GwlExStyle, style);
        }
    }
}
