using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Small, shared motion helpers so every view animates the same way
    /// (same easing, same durations) instead of each one inventing its own.
    ///
    /// All helpers:
    ///  - respect Windows' "Animate controls and elements" setting (no-op
    ///    when the user has reduced motion turned on);
    ///  - animate RenderTransform/Opacity only, never layout, so they are
    ///    cheap and never shift surrounding content.
    /// </summary>
    public static class UiMotion
    {
        private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly IEasingFunction Spring = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };

        public static bool Enabled => SystemParameters.ClientAreaAnimation;

        /// <summary>Quick springy "pop" - use when a value/badge changes (status pill, now-playing art).</summary>
        public static void Pop(FrameworkElement element, double fromScale = 0.86, int durationMs = 320)
        {
            if (!Enabled) return;

            var scale = EnsureScale(element);
            var anim = new DoubleAnimation(fromScale, 1.0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = Spring };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>Fade in and rise a few pixels - use when content is replaced (new response, new track title).</summary>
        public static void FadeIn(FrameworkElement element, double risePx = 6, int durationMs = 220)
        {
            if (!Enabled) return;

            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = EaseOut });

            var shift = EnsureTranslate(element);
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(risePx, 0, TimeSpan.FromMilliseconds(durationMs + 60)) { EasingFunction = EaseOut });
        }

        /// <summary>Show/hide with a short fade (Visibility is set at the right moment).</summary>
        public static void FadeVisibility(UIElement element, bool visible, int durationMs = 150)
        {
            if (!Enabled)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (visible)
            {
                // A fade-out still running (e.g. a list cleared and refilled in the same
                // moment) must not collapse the element when it ends: cancel it.
                if (FadingOut.Remove(element))
                {
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                    element.Opacity = 1;
                    element.Visibility = Visibility.Visible;
                    return;
                }
                if (element.Visibility == Visibility.Visible && element.Opacity >= 1) return;
                element.Visibility = Visibility.Visible;
                element.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = EaseOut });
            }
            else
            {
                if (element.Visibility != Visibility.Visible) return;
                var marker = new object();
                FadingOut.Remove(element);
                FadingOut.Add(element, marker);
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(durationMs));
                fade.Completed += (_, _) =>
                {
                    // Only the latest fade-out may hide the element; a show request cancels it.
                    if (!FadingOut.TryGetValue(element, out var current) || !ReferenceEquals(current, marker)) return;
                    FadingOut.Remove(element);
                    element.Visibility = Visibility.Collapsed;
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                };
                element.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

        /// <summary>Elements with a fade-out in flight (see <see cref="FadeVisibility"/>).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> FadingOut = new();

        // ---- transform plumbing: reuse whatever transform the element already has ----

        private static ScaleTransform EnsureScale(FrameworkElement element)
        {
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5); // qualified: WinForms also brings a Point
            var group = EnsureGroup(element);
            var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (scale == null) { scale = new ScaleTransform(); group.Children.Add(scale); }
            return scale;
        }

        private static TranslateTransform EnsureTranslate(FrameworkElement element)
        {
            var group = EnsureGroup(element);
            var shift = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (shift == null) { shift = new TranslateTransform(); group.Children.Add(shift); }
            return shift;
        }

        private static TransformGroup EnsureGroup(FrameworkElement element)
        {
            if (element.RenderTransform is TransformGroup existing && !existing.IsFrozen)
                return existing;

            var group = new TransformGroup();
            // Keep a pre-existing (non-identity, unfrozen) transform working.
            if (element.RenderTransform is Transform t && t != Transform.Identity && !t.IsFrozen)
                group.Children.Add(t);
            element.RenderTransform = group;
            return group;
        }
    }
}
