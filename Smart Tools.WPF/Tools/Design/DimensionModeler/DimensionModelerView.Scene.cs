using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Views
{
    // The 3D view and the measuring lines over it. Both are drawn from the same OrbitCamera (the WPF camera is set from it and the
    // lines are placed with its Project), so they always agree. The boards come from BoxMesh, the same surface the exports use.
    public partial class DimensionModelerView
    {
        // Wood tones are data, not theme: the piece looks the same in Light, Dark and Glass.
        private static readonly Dictionary<PartKind, Color> Wood = new()
        {
            [PartKind.Top] = Color.FromRgb(222, 176, 126),
            [PartKind.Board] = Color.FromRgb(204, 158, 108),
            [PartKind.Leg] = Color.FromRgb(170, 124, 82),
            [PartKind.Panel] = Color.FromRgb(186, 140, 96),
        };

        private static readonly Color SelectedColor = Color.FromRgb(255, 168, 64);

        private readonly OrbitCamera _camera = new();
        private readonly HashSet<string> _selected = new();
        private readonly ModelVisual3D _pieceVisual = new();
        private readonly ModelVisual3D _floorVisual = new();
        private bool _needsFit = true;
        private Point _lastMouse;
        private bool _orbiting;
        private bool _panning;

        private void InitScene()
        {
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(205, 205, 205), new Vector3D(-0.5, -1.0, -0.7)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(90, 90, 100), new Vector3D(0.8, -0.3, 0.6)));
            Stage3D.Children.Add(new ModelVisual3D { Content = lights });
            Stage3D.Children.Add(_floorVisual);
            Stage3D.Children.Add(_pieceVisual);
            ApplyCamera();
        }

        // =================================================================
        //  The piece
        // =================================================================

        private static Point3D P(Vec3 v) => new(v.X, v.Y, v.Z);

        private static Vector3D V(Vec3 v) => new(v.X, v.Y, v.Z);

        private void RebuildScene()
        {
            var group = new Model3DGroup();
            foreach (var part in _model.Parts)
            {
                var mesh = new MeshGeometry3D();
                foreach (var t in BoxMesh.Triangles(part))
                {
                    int at = mesh.Positions.Count;
                    foreach (var corner in new[] { t.A, t.B, t.C })
                    {
                        mesh.Positions.Add(P(corner));
                        mesh.Normals.Add(V(t.Normal));
                    }
                    mesh.TriangleIndices.Add(at);
                    mesh.TriangleIndices.Add(at + 1);
                    mesh.TriangleIndices.Add(at + 2);
                }
                mesh.Freeze();
                var brush = new SolidColorBrush(_selected.Contains(part.Name) ? SelectedColor : Wood[part.Kind]);
                brush.Freeze();
                var material = new DiffuseMaterial(brush);
                group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            }
            _pieceVisual.Content = group;
            _floorVisual.Content = BuildFloor();
        }

        /// <summary>A faint ground square under the piece, so it does not float in the empty view.</summary>
        private GeometryModel3D? BuildFloor()
        {
            if (_model.Parts.Count == 0) return null;
            var min = _model.Min;
            var max = _model.Max;
            double reach = Math.Max(max.X - min.X, max.Z - min.Z) * 0.9;
            double cx = (min.X + max.X) / 2, cz = (min.Z + max.Z) / 2, y = min.Y - 0.5;
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(cx - reach, y, cz - reach));
            mesh.Positions.Add(new Point3D(cx - reach, y, cz + reach));
            mesh.Positions.Add(new Point3D(cx + reach, y, cz + reach));
            mesh.Positions.Add(new Point3D(cx + reach, y, cz - reach));
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(i);
            for (int i = 0; i < 4; i++) mesh.Normals.Add(new Vector3D(0, 1, 0));
            mesh.Freeze();
            var brush = new SolidColorBrush(Color.FromArgb(46, 128, 128, 128));
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        // =================================================================
        //  The camera
        // =================================================================

        private void ApplyCamera()
        {
            var position = _camera.Position;
            var look = _camera.LookDirection;
            Cam.Position = P(position);
            Cam.LookDirection = V(look);
            Cam.UpDirection = new Vector3D(0, 1, 0);
            Cam.FieldOfView = _camera.FovDegrees;
            Cam.NearPlaneDistance = Math.Max(10, _camera.Distance * 0.02);
            Cam.FarPlaneDistance = _camera.Distance * 60;
        }

        /// <summary>How far outside the piece the measuring lines are drawn.</summary>
        private double DimensionOffset()
        {
            var size = _model.Size;
            return Math.Clamp(Math.Max(size.X, Math.Max(size.Y, size.Z)) * 0.1, 40, 300);
        }

        private void FitCamera()
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10 || _model.Parts.Count == 0)
            {
                _needsFit = true;
                return;
            }
            _needsFit = false;
            // fit the piece plus the room its measuring lines take
            double off = _settings.ShowDimensions ? DimensionOffset() : 0;
            _camera.FitTo(_model.Min + new Vec3(-off, 0, 0), _model.Size + new Vec3(2 * off, 0, off), w, h);
            ApplyCamera();
            RedrawOverlay();
        }

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_needsFit) FitCamera();
            else RedrawOverlay();
        }

        private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                FitCamera();
                return;
            }
            _orbiting = e.ChangedButton == MouseButton.Left;
            _panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle;
            if (!_orbiting && !_panning) return;
            _lastMouse = e.GetPosition(StageHost);
            StageHost.CaptureMouse();
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_orbiting && !_panning) return;
            var now = e.GetPosition(StageHost);
            double dx = now.X - _lastMouse.X, dy = now.Y - _lastMouse.Y;
            _lastMouse = now;
            if (_orbiting) _camera.Orbit(-dx * 0.4, dy * 0.4);
            else _camera.Pan(dx, dy, StageHost.ActualWidth);
            ApplyCamera();
            RedrawOverlay();
        }

        private void Stage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _orbiting = _panning = false;
            StageHost.ReleaseMouseCapture();
        }

        private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _camera.Zoom(e.Delta > 0 ? 0.88 : 1.14);
            ApplyCamera();
            RedrawOverlay();
            e.Handled = true;
        }

        // =================================================================
        //  The measuring lines
        // =================================================================

        /// <summary>The whole piece's width, height and depth - or, with a cut-list row picked, that board's own three sizes.</summary>
        private void RedrawOverlay()
        {
            Overlay.Children.Clear();
            if (!_settings.ShowDimensions || _model.Parts.Count == 0) return;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40) return;

            var picked = _model.Parts.FirstOrDefault(p => _selected.Contains(p.Name));
            var lines = picked != null
                ? DimensionLines.ForPart(picked, Math.Clamp(Math.Max(picked.Size.X, Math.Max(picked.Size.Y, picked.Size.Z)) * 0.15, 25, 150))
                : DimensionLines.Overall(_model, DimensionOffset());
            foreach (var line in lines) DrawDimension(line, w, h);
        }

        private void DrawDimension(DimensionLine line, double w, double h)
        {
            if (line.LengthMm < 1e-6) return;
            if (_camera.Project(line.A, w, h) is not { } a || _camera.Project(line.B, w, h) is not { } b
                || _camera.Project(line.Start, w, h) is not { } s || _camera.Project(line.End, w, h) is not { } e) return;

            // the short lines joining the measured points to the dimension line
            AddLine(a, s, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(b, e, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(s, e, "AccentColor", 1.75, 1, dashed: false);
            AddArrow(s, e);
            AddArrow(e, s);

            var label = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), BorderThickness = new Thickness(1) };
            label.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            label.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            var text = new TextBlock { Text = LengthFormat.Format(line.LengthMm, _settings.Unit), FontSize = 12, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            label.Child = text;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, (s.X + e.X) / 2 - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, (s.Y + e.Y) / 2 - label.DesiredSize.Height / 2);
            Overlay.Children.Add(label);
        }

        private void AddLine(ScreenPoint from, ScreenPoint to, string brushKey, double thickness, double opacity, bool dashed)
        {
            var shape = new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, StrokeThickness = thickness, Opacity = opacity };
            shape.SetResourceReference(Shape.StrokeProperty, brushKey);
            if (dashed) shape.StrokeDashArray = new DoubleCollection { 3, 3 };
            Overlay.Children.Add(shape);
        }

        /// <summary>A small arrowhead at <paramref name="tip"/>, pointing away from <paramref name="other"/>.</summary>
        private void AddArrow(ScreenPoint tip, ScreenPoint other)
        {
            double dx = other.X - tip.X, dy = other.Y - tip.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 14) return;
            double ux = dx / len, uy = dy / len;
            var arrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(tip.X, tip.Y),
                    new Point(tip.X + ux * 11 - uy * 4, tip.Y + uy * 11 + ux * 4),
                    new Point(tip.X + ux * 11 + uy * 4, tip.Y + uy * 11 - ux * 4),
                },
            };
            arrow.SetResourceReference(Shape.FillProperty, "AccentColor");
            Overlay.Children.Add(arrow);
        }
    }
}
