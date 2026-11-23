using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Rounds the corners of any element (used for DataGrids, which have no Border of their own to round).
    /// A Clip does not push the content through an offscreen bitmap, so text stays crisp and scrolling stays fast.
    /// The element's own square border would be cut off at the corners by that clip, so the border is drawn instead by an
    /// adorner (a rounded 1 px outline in the control's BorderBrush) and the control itself gets BorderThickness 0.
    /// Usage: &lt;Setter Property="controls:RoundCorners.Radius" Value="12" /&gt;
    /// </summary>
    public static class RoundCorners
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundCorners), new PropertyMetadata(0d, OnRadiusChanged));

        public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

        private static readonly DependencyProperty OutlineProperty = DependencyProperty.RegisterAttached(
            "Outline", typeof(OutlineAdorner), typeof(RoundCorners));

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.SizeChanged -= OnSizeChanged;
            fe.Loaded -= OnLoaded;
            fe.Unloaded -= OnUnloaded;
            if ((double)e.NewValue > 0)
            {
                fe.SizeChanged += OnSizeChanged;
                fe.Loaded += OnLoaded;
                fe.Unloaded += OnUnloaded;
                Apply(fe);
                if (fe.IsLoaded) AddOutline(fe);
            }
            else
            {
                fe.ClearValue(UIElement.ClipProperty);
                RemoveOutline(fe);
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);
        private static void OnLoaded(object sender, RoutedEventArgs e) { var fe = (FrameworkElement)sender; Apply(fe); AddOutline(fe); }
        private static void OnUnloaded(object sender, RoutedEventArgs e) => RemoveOutline((FrameworkElement)sender);

        private static void Apply(FrameworkElement fe)
        {
            double r = GetRadius(fe);
            if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
            var clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
            clip.Freeze();
            fe.Clip = clip;
        }

        private static void AddOutline(FrameworkElement fe)
        {
            if (fe is not Control || fe.GetValue(OutlineProperty) != null) return;
            var layer = AdornerLayer.GetAdornerLayer(fe);
            if (layer == null) return;
            var adorner = new OutlineAdorner(fe);
            layer.Add(adorner);
            fe.SetValue(OutlineProperty, adorner);
        }

        private static void RemoveOutline(FrameworkElement fe)
        {
            if (fe.GetValue(OutlineProperty) is not OutlineAdorner adorner) return;
            adorner.Detach();
            AdornerLayer.GetAdornerLayer(fe)?.Remove(adorner);
            fe.ClearValue(OutlineProperty);
        }

        /// <summary>Draws the rounded 1 px outline on top of the element, so the clip cannot cut it at the corners.</summary>
        private sealed class OutlineAdorner : Adorner
        {
            private readonly DependencyPropertyDescriptor? _brushWatch =
                DependencyPropertyDescriptor.FromProperty(Control.BorderBrushProperty, typeof(Control));

            public OutlineAdorner(FrameworkElement element) : base(element)
            {
                IsHitTestVisible = false;
                _brushWatch?.AddValueChanged(element, OnBrushChanged);
            }

            public void Detach() => _brushWatch?.RemoveValueChanged(AdornedElement, OnBrushChanged);

            private void OnBrushChanged(object? sender, EventArgs e) => InvalidateVisual();

            protected override void OnRender(DrawingContext dc)
            {
                if (AdornedElement is not Control c || c.BorderBrush is not { } brush) return;
                double w = AdornedElement.RenderSize.Width, h = AdornedElement.RenderSize.Height;
                if (w < 2 || h < 2) return;
                double r = Math.Max(0, GetRadius(AdornedElement) - 0.5);
                dc.DrawRoundedRectangle(null, new Pen(brush, 1), new Rect(0.5, 0.5, w - 1, h - 1), r, r);
            }
        }
    }
}
