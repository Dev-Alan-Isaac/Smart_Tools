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
    // The 3D view and the lines over it. The finished objects (holes cut out) are built by SceneMeshBuilder on a worker thread,
    // newest design first; the WPF camera and the overlay lines both come from the same OrbitCamera, so they always agree.
    public partial class DimensionModelerView
    {
        // Shape colours are the design's data, not the theme: a design looks the same in Light, Dark and Glass.
        private static readonly Color DefaultSolid = Color.FromRgb(156, 179, 206);
        private static readonly Color PickTint = Color.FromRgb(255, 168, 64);
        private static readonly Color HoleGhost = Color.FromArgb(70, 140, 140, 150);
        private static readonly Color HoleGhostPicked = Color.FromArgb(110, 255, 168, 64);

        private readonly OrbitCamera _camera = new();
        private readonly ModelVisual3D _sceneVisual = new();
        private readonly ModelVisual3D _floorVisual = new();
        private readonly SceneMeshBuilder _viewBuilder = new();   // used only on the worker, one build at a time

        private IReadOnlyList<ObjectMesh> _objects = Array.Empty<ObjectMesh>();
        private Scene? _pendingBuild;
        private bool _building;
        private bool _fitAfterBuild;
        private bool _needsFit = true;
        private Point _lastMouse;
        private Point _pressPoint;
        private bool _orbiting;
        private bool _panning;

        private void InitStage()
        {
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(205, 205, 205), new Vector3D(-0.5, -1.0, -0.7)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(90, 90, 100), new Vector3D(0.8, -0.3, 0.6)));
            Stage3D.Children.Add(new ModelVisual3D { Content = lights });
            Stage3D.Children.Add(_floorVisual);
            Stage3D.Children.Add(_sceneVisual);
            ApplyCamera();
        }

        // =================================================================
        //  Building and drawing
        // =================================================================

        /// <summary>Asks for the design to be built again. While a build runs, only the newest design waits; older ones are skipped.</summary>
        private void RequestBuild(bool fit = false)
        {
            _pendingBuild = _scene;
            _fitAfterBuild |= fit;
            if (!_building) RunBuilds();
        }

        private async void RunBuilds()
        {
            _building = true;
            try
            {
                while (_pendingBuild is { } scene)
                {
                    _pendingBuild = null;
                    try
                    {
                        _objects = await Task.Run(() => _viewBuilder.Build(scene));
                    }
                    catch (Exception ex)
                    {
                        // a shape the cutting cannot handle must not break the page: show what is known and say so
                        _objects = Array.Empty<ObjectMesh>();
                        TxtStatus.Text = Loc.F("Str_Modeler_BuildFailed", "Couldn't build the design: {0}", ex.Message);
                    }
                    if (_pendingBuild == null)
                    {
                        DrawObjects();
                        RefreshTitle();
                        RefreshPartsTotals();
                        if (_fitAfterBuild)
                        {
                            _fitAfterBuild = false;
                            FitCamera();
                        }
                        RedrawOverlay();
                    }
                }
            }
            finally
            {
                _building = false;
            }
        }

        private static Point3D P(Vec3 v) => new(v.X, v.Y, v.Z);

        private static Vector3D V(Vec3 v) => new(v.X, v.Y, v.Z);

        /// <summary>The top-level objects that are picked (a picked shape inside a group lights up its whole object).</summary>
        private HashSet<string> PickedObjects() => _picked.Select(id => SceneEdit.TopOf(_scene, id)?.Id).OfType<string>().ToHashSet();

        private void DrawObjects()
        {
            var lit = PickedObjects();
            var group = new Model3DGroup();
            foreach (var obj in _objects)
            {
                bool picked = lit.Contains(obj.NodeId);
                var shift = DragShift(obj.NodeId);
                if (obj.IsHole)
                {
                    var ghost = Model(obj.Triangles.Select(t => t.Triangle), picked ? HoleGhostPicked : HoleGhost);
                    if (shift != null) ghost.Transform = shift;
                    group.Children.Add(ghost);
                    continue;
                }
                foreach (var byColour in obj.Triangles.GroupBy(t => t.Colour))
                {
                    var colour = ColourOf(byColour.Key);
                    if (picked) colour = Mix(colour, PickTint, 0.3);
                    var model = Model(byColour.Select(t => t.Triangle), colour);
                    if (shift != null) model.Transform = shift;
                    group.Children.Add(model);
                }
            }
            AddPickedParts(group);
            _sceneVisual.Content = group;
            _floorVisual.Content = BuildFloor();
        }

        private static Color ColourOf(string? hex) =>
            PiecePaint.TryParse(hex, out byte r, out byte g, out byte b) ? Color.FromRgb(r, g, b) : DefaultSolid;

        private static Color Mix(Color own, Color tint, double amount)
        {
            byte M(byte a, byte b) => (byte)Math.Round(a * (1 - amount) + b * amount);
            return Color.FromArgb(own.A, M(own.R, tint.R), M(own.G, tint.G), M(own.B, tint.B));
        }

        private static GeometryModel3D Model(IEnumerable<Triangle> triangles, Color colour)
        {
            var mesh = new MeshGeometry3D();
            foreach (var t in triangles)
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
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        /// <summary>The box around the solid objects as last built (holes on their own do not count), or the shapes' boxes before the first build.</summary>
        private Bounds3 SolidBounds()
        {
            var b = Bounds3.Nothing;
            foreach (var o in _objects.Where(o => !o.IsHole)) b = b.With(o.Bounds);
            if (b.IsEmpty)
                foreach (var node in _scene.Nodes) b = b.With(SceneEdit.BoundsOf(_scene, node));
            return b;
        }

        /// <summary>A faint ground square under the design (or a default one when it is empty), so nothing floats in an empty view.</summary>
        private GeometryModel3D BuildFloor()
        {
            var b = SolidBounds();
            double reach = b.IsEmpty ? 300 : Math.Max(150, Math.Max(b.Size.X, b.Size.Z) * 0.9);
            double cx = b.IsEmpty ? 0 : b.Centre.X, cz = b.IsEmpty ? 0 : b.Centre.Z, y = -0.5;
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
            Cam.Position = P(_camera.Position);
            Cam.LookDirection = V(_camera.LookDirection);
            Cam.UpDirection = new Vector3D(0, 1, 0);
            Cam.FieldOfView = _camera.FovDegrees;
            Cam.NearPlaneDistance = Math.Max(1, _camera.Distance * 0.02);
            Cam.FarPlaneDistance = _camera.Distance * 60;
            DrawGizmo();
        }

        private double DimensionOffset(Bounds3 b) => Math.Clamp(Math.Max(b.Size.X, Math.Max(b.Size.Y, b.Size.Z)) * 0.1, 10, 300);

        private void FitCamera()
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10)
            {
                _needsFit = true;
                return;
            }
            _needsFit = false;
            var b = SolidBounds();
            if (b.IsEmpty) b = new Bounds3(new Vec3(-150, 0, -150), new Vec3(150, 150, 150));
            double off = _settings.ShowDimensions ? DimensionOffset(b) : 0;   // room for the measuring lines
            _camera.FitTo(b.Min + new Vec3(-off, 0, 0), b.Size + new Vec3(2 * off, 0, off), w, h);
            ApplyCamera();
            RedrawOverlay();
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e) => FitCamera();

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_needsFit) FitCamera();
            else RedrawOverlay();
        }

        // =================================================================
        //  Mouse: click picks, drag on an object moves it (Shift: lifts it), drag on empty space turns, right-drag pans, wheel zooms
        // =================================================================

        private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _pressPoint = e.GetPosition(StageHost);
            StageHost.Focus();   // so the keys (Delete, Ctrl+Z, Esc) reach the page
            if (_drawing != null && e.ChangedButton == MouseButton.Left)
            {
                DrawClick(_pressPoint, e.ClickCount);   // drawing an outline: a left click adds a point (right-drag still pans)
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                FitCamera();
                return;
            }
            bool onObject = e.ChangedButton == MouseButton.Left && TryBeginPress(_pressPoint);
            _orbiting = e.ChangedButton == MouseButton.Left && !onObject;
            _panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle;
            if (!_orbiting && !_panning && !onObject) return;
            _lastMouse = _pressPoint;
            StageHost.CaptureMouse();
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drawing != null && !_panning)
            {
                DrawHover(e.GetPosition(StageHost));
                return;
            }
            if (_pressHit is { } hit && e.LeftButton == MouseButtonState.Pressed)
            {
                var at = e.GetPosition(StageHost);
                if (_drag == null && (Math.Abs(at.X - _pressPoint.X) >= DragStartPixels || Math.Abs(at.Y - _pressPoint.Y) >= DragStartPixels))
                {
                    StartDrag(hit);
                    if (_drag == null) _pressHit = null;   // locked: nothing moves
                }
                if (_drag != null) DragTo(at);
                return;
            }
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
            var at = e.GetPosition(StageHost);
            bool still = Math.Abs(at.X - _pressPoint.X) < DragStartPixels && Math.Abs(at.Y - _pressPoint.Y) < DragStartPixels;
            bool pressedObject = _pressHit != null;
            bool leftClick = e.ChangedButton == MouseButton.Left && (_orbiting || pressedObject) && still;
            bool rightClick = e.ChangedButton == MouseButton.Right && still && _drawing == null;   // a right-drag pans; a right-click opens the menu
            _orbiting = _panning = false;
            _pressHit = null;
            StageHost.ReleaseMouseCapture();
            if (_drag != null) EndDrag();
            else if (leftClick) ClickPick(at, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            else if (rightClick)
            {
                ShowStageMenu(at);
                e.Handled = true;
            }
        }

        /// <summary>A click (no drag): the object under the mouse becomes the pick; Ctrl adds or takes it away; empty space lets go.</summary>
        private void ClickPick(Point at, bool add)
        {
            string? id = ObjectAt(at);
            if (!add && id != null && TryPickPart(at, id)) return;   // second click on a picked group: the part under the mouse
            if (add)
            {
                if (id == null) return;
                var next = _picked.ToList();
                if (!next.Remove(id)) next.Add(id);
                Pick(next);
            }
            else Pick(id == null ? Array.Empty<string>() : new[] { id });
        }

        private string? ObjectAt(Point at)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10) return null;
            return ScenePick.Pick(_objects, _camera.Ray(at.X, at.Y, w, h))?.NodeId;
        }

        private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _camera.Zoom(e.Delta > 0 ? 0.88 : 1.14);
            ApplyCamera();
            RedrawOverlay();
            e.Handled = true;
        }

        // =================================================================
        //  The lines over the view: picked outlines and measurements
        // =================================================================

        /// <summary>Outlines the picked shapes and measures them (or the whole design when nothing is picked).</summary>
        private void RedrawOverlay()
        {
            Overlay.Children.Clear();
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40) return;

            foreach (var id in _picked)
                if (SceneEdit.Find(_scene, id) is { } node) DrawBox(SceneEdit.BoundsOf(_scene, node), w, h);
            DrawSnapGuides(w, h);
            DrawOutline(w, h);

            var boxes = MeasuredBoxes();
            // a picked part of a group: its group's box, dashed and quiet, so the part is seen in its place
            if (_picked.Any(IsPart))
                foreach (var top in _picked.Select(id => SceneEdit.TopOf(_scene, id)).OfType<SceneNode>().Distinct())
                    DrawBox(SceneEdit.BoundsOf(_scene, top), w, h, "TextSecondary", dashed: true);
            if (!_settings.ShowDimensions) return;
            bool two = boxes.Count > 1;
            foreach (var (measured, outer) in boxes)
            {
                var box = new Part("", measured.Min, measured.Size, PartKind.Board);
                double biggest = Math.Max(measured.Size.X, Math.Max(measured.Size.Y, measured.Size.Z));
                var lines = _picked.Count == 0
                    ? DimensionLines.Overall(new PieceModel("", new[] { box }), DimensionOffset(measured))
                    : DimensionLines.ForPart(box, outer ? Math.Clamp(biggest * 0.3, 20, 300) : Math.Clamp(biggest * 0.15, 8, 150));
                foreach (var line in lines) DrawDimension(line, w, h, two && outer ? "TextSecondary" : "AccentColor");
            }
        }

        /// <summary>The twelve edges of a box, thin and in the accent colour.</summary>
        private void DrawBox(Bounds3 b, double w, double h, string brush = "AccentColor", bool dashed = false)
        {
            if (b.IsEmpty) return;
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++)
                c[i] = new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            foreach (var (i, j) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                if (_camera.Project(c[i], w, h) is { } a && _camera.Project(c[j], w, h) is { } z) AddLine(a, z, brush, 1.25, dashed ? 0.6 : 0.9, dashed);
        }

        private void DrawDimension(DimensionLine line, double w, double h, string brush = "AccentColor")
        {
            if (line.LengthMm < 1e-6) return;
            if (_camera.Project(line.A, w, h) is not { } a || _camera.Project(line.B, w, h) is not { } b
                || _camera.Project(line.Start, w, h) is not { } s || _camera.Project(line.End, w, h) is not { } e) return;

            // the short lines joining the measured points to the dimension line
            AddLine(a, s, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(b, e, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(s, e, brush, 1.75, 1, dashed: false);
            AddArrow(s, e);
            AddArrow(e, s);

            var label = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), BorderThickness = new Thickness(1) };
            label.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            label.SetResourceReference(Border.BorderBrushProperty, brush);
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
            shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
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
            arrow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentColor");
            Overlay.Children.Add(arrow);
        }
    }
}
