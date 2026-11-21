using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Smart_Tools.WPF.Controls;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The in-window modal layer: a transparent grid added on top of a window's content. Each open modal is a scrim (dims and blocks
    /// the page) with a centered card; several can be open at once (the newest is on top). Only the newest one gets the keyboard.
    /// Blocking is done with a nested dispatcher frame, so callers keep the simple "call, then read the answer" shape of a message box.
    /// Use <see cref="AppModal"/> instead of this class directly.
    /// </summary>
    internal sealed class ModalLayer : Grid
    {
        private sealed class Entry
        {
            public required Grid Scrim { get; init; }
            public required DispatcherFrame Frame { get; init; }
            public IInputElement? Focus { get; init; }
            public IInputElement? PreviousFocus { get; init; }
            public bool Closed { get; set; }
        }

        private static readonly ConditionalWeakTable<Window, ModalLayer> Layers = new();

        private readonly Window _window;
        private readonly List<Entry> _stack = new();

        private ModalLayer(Window window)
        {
            _window = window;
            Visibility = Visibility.Collapsed;
            System.Windows.Controls.Panel.SetZIndex(this, 10000);
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            window.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnPreviewGotFocus), true);
            window.Closed += (_, _) => { foreach (var entry in _stack.ToArray()) Close(entry); };
        }

        /// <summary>True while a modal is open in this window (shortcuts must not run behind it).</summary>
        public static bool IsOpen(Window? window) => window != null && Layers.TryGetValue(window, out var layer) && layer._stack.Count > 0;

        /// <summary>The window's layer, added on first use. Null when the window's content cannot host one.</summary>
        public static ModalLayer? For(Window window)
        {
            if (Layers.TryGetValue(window, out var existing)) return existing;
            if (window.Content is not UIElement content) return null;

            var layer = new ModalLayer(window);
            if (content is Grid grid)
            {
                SetRowSpan(layer, Math.Max(1, grid.RowDefinitions.Count));
                SetColumnSpan(layer, Math.Max(1, grid.ColumnDefinitions.Count));
                grid.Children.Add(layer);
            }
            else
            {
                window.Content = null;
                var wrapper = new Grid();
                wrapper.Children.Add(content);
                wrapper.Children.Add(layer);
                window.Content = wrapper;
            }
            Layers.Add(window, layer);
            return layer;
        }

        /// <summary>
        /// Shows <paramref name="build"/>'s content in a card and waits until the content calls the close action it was given.
        /// The builder returns the content and the element that should get the keyboard first.
        /// </summary>
        public void Run(Func<Action, (FrameworkElement Content, IInputElement? Focus)> build, double minWidth, double maxWidth)
        {
            Entry? entry = null;
            var frame = new DispatcherFrame();
            var (content, focus) = build(() => { if (entry != null) Close(entry); });

            var scrim = new Grid();
            scrim.SetResourceReference(BackgroundProperty, "ScrimBg");
            scrim.MouseDown += (_, e) => e.Handled = true;   // clicks on the dimmed page do nothing

            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = minWidth, MaxWidth = maxWidth, Margin = new Thickness(24) };
            // Shadow layer (empty) + card on top: the card's text stays crisp (see ui-style-guide.md).
            var shadow = new Border { CornerRadius = new CornerRadius(12), IsHitTestVisible = false };
            shadow.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            shadow.SetResourceReference(EffectProperty, "CardElevation");
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(22), Child = content };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            card.SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
            host.Children.Add(shadow);
            host.Children.Add(card);
            scrim.Children.Add(host);

            entry = new Entry { Scrim = scrim, Frame = frame, Focus = focus, PreviousFocus = Keyboard.FocusedElement };
            _stack.Add(entry);
            Children.Add(scrim);
            Visibility = Visibility.Visible;

            UiMotion.FadeIn(scrim, 0, 140);
            UiMotion.Pop(host, 0.95, 200);
            FocusTop();

            Dispatcher.PushFrame(frame);   // returns when Close() ran (or the window / app closed)
        }

        private void Close(Entry entry)
        {
            if (entry.Closed) return;
            entry.Closed = true;
            _stack.Remove(entry);
            Children.Remove(entry.Scrim);
            if (_stack.Count == 0) Visibility = Visibility.Collapsed;
            entry.Frame.Continue = false;

            if (_stack.Count > 0) FocusTop();
            else if (entry.PreviousFocus is UIElement previous && previous.IsVisible && previous.IsEnabled) previous.Focus();
        }

        private void FocusTop()
        {
            if (_stack.Count == 0) return;
            var top = _stack[^1];
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_stack.Count == 0 || _stack[^1] != top) return;
                if (top.Focus != null) top.Focus.Focus();
                else top.Scrim.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            });
        }

        /// <summary>Keeps keyboard focus inside the newest modal (Tab cycles there; Alt-Tab back cannot land behind the scrim).</summary>
        private void OnPreviewGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_stack.Count == 0 || e.NewFocus is not DependencyObject target) return;
            var top = _stack[^1];
            if (IsInside(target, top.Scrim)) return;
            if (target is Visual visual && PresentationSource.FromVisual(visual) is { } source && !ReferenceEquals(source, PresentationSource.FromVisual(_window))) return;   // a drop-down list's popup
            e.Handled = true;
            FocusTop();
        }

        private static bool IsInside(DependencyObject element, DependencyObject container)
        {
            for (DependencyObject? current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, container)) return true;
            return false;
        }
    }
}
