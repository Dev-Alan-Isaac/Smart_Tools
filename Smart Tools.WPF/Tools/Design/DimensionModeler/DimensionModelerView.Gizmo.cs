using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Smart_Tools.WPF.Views
{
    // The axis gizmo in the corner of the 3D view: X red, Y green, Z blue, turning with the camera. The ends closer to you are
    // drawn on top; a click on an end turns the view to look from that side (the faint ends are the negative sides).
    public partial class DimensionModelerView
    {
        private const double GizmoArm = 30;

        private static readonly string[] AxisBrushes = { "AxisXBrush", "AxisYBrush", "AxisZBrush" };
        private static readonly string[] AxisLetters = { "X", "Y", "Z" };

        /// <summary>Tooltip keys per axis: [axis, 0] = positive end, [axis, 1] = negative end.</summary>
        private static readonly (string Key, string Fallback)[,] GizmoTips =
        {
            { ("Str_Modeler_LookRight", "Look from the right (X)"), ("Str_Modeler_LookLeft", "Look from the left (-X)") },
            { ("Str_Modeler_LookTop", "Look from above (Y)"), ("Str_Modeler_LookBelow", "Look from below (-Y)") },
            { ("Str_Modeler_LookFront", "Look from the front (Z)"), ("Str_Modeler_LookBack", "Look from the back (-Z)") },
        };

        private void DrawGizmo()
        {
            if (Gizmo == null) return;
            Gizmo.Children.Clear();
            double cx = Gizmo.Width / 2, cy = Gizmo.Height / 2;

            // every end (positive and negative), farthest first, so the near ones cover the far ones
            var ends = _camera.GizmoAxes()
                .SelectMany(a => new[] { (a.Axis, Positive: true, a.X, a.Y, a.Depth), (a.Axis, Positive: false, X: -a.X, Y: -a.Y, Depth: -a.Depth) })
                .OrderByDescending(e => e.Depth);
            foreach (var end in ends)
            {
                double x = cx + end.X * GizmoArm, y = cy + end.Y * GizmoArm;
                if (end.Positive)
                {
                    var arm = new Line { X1 = cx, Y1 = cy, X2 = x, Y2 = y, StrokeThickness = 2, IsHitTestVisible = false };
                    arm.SetResourceReference(Shape.StrokeProperty, AxisBrushes[end.Axis]);
                    Gizmo.Children.Add(arm);
                }
                Gizmo.Children.Add(GizmoEnd(end.Axis, end.Positive, x, y));
            }
        }

        /// <summary>One clickable end: a ring with the axis letter (positive) or a small faint dot (negative).</summary>
        private FrameworkElement GizmoEnd(int axis, bool positive, double x, double y)
        {
            double size = positive ? 18 : 11;
            var ring = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderThickness = new Thickness(positive ? 1.5 : 1),
                Opacity = positive ? 1 : 0.55,
                Cursor = Cursors.Hand,
                Tag = (axis, positive),
            };
            ring.SetResourceReference(Border.BorderBrushProperty, AxisBrushes[axis]);
            ring.SetResourceReference(Border.BackgroundProperty, positive ? "PopupBg" : AxisBrushes[axis]);
            if (positive)
            {
                var letter = new TextBlock
                {
                    Text = AxisLetters[axis],
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                letter.SetResourceReference(TextBlock.ForegroundProperty, AxisBrushes[axis]);
                ring.Child = letter;
            }
            var tip = GizmoTips[axis, positive ? 0 : 1];
            ring.ToolTip = Loc.T(tip.Key, tip.Fallback);
            Canvas.SetLeft(ring, x - size / 2);
            Canvas.SetTop(ring, y - size / 2);
            return ring;
        }

        /// <summary>A click on an axis end turns the view; anything on the gizmo is kept from the 3D view (no orbit, no pick).</summary>
        private void Gizmo_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ChangedButton != MouseButton.Left) return;
            for (var hit = e.OriginalSource as DependencyObject; hit != null && hit != Gizmo; hit = System.Windows.Media.VisualTreeHelper.GetParent(hit))
            {
                if (hit is FrameworkElement { Tag: (int axis, bool positive) })
                {
                    _camera.LookFrom(axis, positive);
                    ApplyCamera();
                    RedrawOverlay();
                    return;
                }
            }
        }

        private void Gizmo_MouseUp(object sender, MouseButtonEventArgs e) => e.Handled = true;
    }
}
