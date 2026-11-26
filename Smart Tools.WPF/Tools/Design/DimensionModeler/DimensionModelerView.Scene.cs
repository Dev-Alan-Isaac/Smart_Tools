using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
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
        private readonly HashSet<string> _hidden = new();   // boards switched off in the view (not saved, not exported, not part of undo)
        private readonly ModelVisual3D _pieceVisual = new();
        private readonly ModelVisual3D _floorVisual = new();
        private bool _needsFit = true;
        private Point _lastMouse;
        private bool _orbiting;
        private bool _panning;
        private Point _pressPoint;
        private bool _pressedEmpty;

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
            var paints = CurrentPaint();
            foreach (var part in Shown())
            {
                if (_hidden.Contains(part.Name)) continue;
                // one mesh per colour: a board with a coloured face is drawn in two (or more) pieces
                var meshes = new Dictionary<Color, MeshGeometry3D>();
                foreach (var t in BoxMesh.Triangles(part))
                {
                    var colour = FaceColour(part, PiecePaint.FaceOf(part, t), paints);
                    if (!meshes.TryGetValue(colour, out var mesh)) meshes[colour] = mesh = new MeshGeometry3D();
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
                foreach (var (colour, mesh) in meshes)
                {
                    mesh.Freeze();
                    var brush = new SolidColorBrush(colour);
                    brush.Freeze();
                    var material = new DiffuseMaterial(brush);
                    group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
                }
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
            _pressPoint = e.GetPosition(StageHost);
            _pressedEmpty = false;
            StageHost.Focus();   // so Esc reaches the view (full view)
            if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && BoardAt(_pressPoint) is { } board)
            {
                PickBoard(board, add: true);   // Ctrl+click: one more board picked (or one less), nothing is dragged
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Left && _isCustom)
            {
                if (PushAllBack()) return;   // edit the piece where it really is
                if (BeginEdit(_pressPoint))
                {
                    StageHost.Cursor = _edit?.Kind == EditKind.Resize ? System.Windows.Input.Cursors.Cross : System.Windows.Input.Cursors.SizeAll;
                    StageHost.CaptureMouse();
                    return;
                }
                _pressedEmpty = true;   // empty space: turns the view, and a plain click there lets go of the picked board
            }
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
            if (_edit != null)
            {
                UpdateEdit(e.GetPosition(StageHost));
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
            if (_edit != null)
            {
                EndEdit();
                return;
            }
            var at = e.GetPosition(StageHost);
            bool rightClick = e.ChangedButton == MouseButton.Right && Math.Abs(at.X - _pressPoint.X) < 4 && Math.Abs(at.Y - _pressPoint.Y) < 4;
            bool leftClick = e.ChangedButton == MouseButton.Left && Math.Abs(at.X - _pressPoint.X) < 4 && Math.Abs(at.Y - _pressPoint.Y) < 4;
            if (_pressedEmpty && leftClick)
                ListParts.SelectedIndex = -1;
            else if (leftClick && !_isCustom && _orbiting)
                PickBoard(BoardAt(at), add: false);   // a preset: a plain click picks the board (or lets go on empty space)
            _pressedEmpty = false;
            _orbiting = _panning = false;
            StageHost.ReleaseMouseCapture();
            if (rightClick) ShowBoardMenu(at);   // a right click that did not drag: hide this one board
        }

        /// <summary>Right click on a board in the 3D view: hide just that board (one of two doors), or bring everything back.</summary>
        private void ShowBoardMenu(Point at)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40 || _model.Parts.Count == 0) return;
            var shown = Shown();
            var ray = _camera.Ray(at.X, at.Y, w, h);
            int index = PieceDrag.Pick(shown, ray, out double distance, p => _hidden.Contains(p.Name));
            if (index < 0 && _hidden.Count == 0) return;

            var menu = new ContextMenu { PlacementTarget = StageHost };
            if (index >= 0)
            {
                string name = shown[index].Name;
                AddGroupItems(menu, shown[index]);
                AddPaintItems(menu, shown[index], PiecePaint.FaceAt(shown[index], ray.Origin + ray.Direction * distance));
                if (shown[index].Group is { } group)
                {
                    // the whole group: pull it out (or back in), or hide it
                    var pull = new MenuItem { Header = _pulled.ContainsKey(group) ? Loc.F("Str_Modeler_PushIn", "Push {0} back in", group) : Loc.F("Str_Modeler_PullOut", "Pull out {0}", group) };
                    pull.Click += (_, _) => SetPulled(group, !_pulled.ContainsKey(group));
                    menu.Items.Add(pull);
                    var hideGroup = new MenuItem { Header = Loc.F("Str_Modeler_HideGroup", "Hide all of {0}", group) };
                    hideGroup.Click += (_, _) =>
                    {
                        foreach (var p in _model.Parts.Where(p => p.Group == group)) _hidden.Add(p.Name);
                        HiddenChanged();
                    };
                    menu.Items.Add(hideGroup);
                }
                var hide = new MenuItem { Header = Loc.F("Str_Modeler_HideBoard", "Hide {0}", name) };
                hide.Click += (_, _) =>
                {
                    _hidden.Add(name);
                    HiddenChanged();
                };
                menu.Items.Add(hide);
            }
            if (_hidden.Count > 0)
            {
                var all = new MenuItem { Header = Loc.F("Str_Modeler_ShowAll", "Show all ({0} hidden)", _hidden.Count) };
                all.Click += BtnShowAll_Click;
                menu.Items.Add(all);
            }
            menu.IsOpen = true;
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
            DrawHandles();
            if (!_settings.ShowDimensions || _model.Parts.Count == 0) return;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40) return;

            var shown = Shown();
            var picked = shown.FirstOrDefault(p => _selected.Contains(p.Name));
            var lines = picked != null
                ? DimensionLines.ForPart(picked, Math.Clamp(Math.Max(picked.Size.X, Math.Max(picked.Size.Y, picked.Size.Z)) * 0.15, 25, 150))
                : DimensionLines.Overall(new PieceModel(_model.Name, shown), DimensionOffset());
            foreach (var line in lines) DrawDimension(line, w, h);

            // inside a piece: the free space from the picked board to the boards around it (hidden boards, like doors, do not count)
            if (picked != null)
                foreach (var gap in DimensionLines.Gaps(shown, picked, p => _hidden.Contains(p.Name))) DrawDimension(gap, w, h, gapLine: true);
            if (picked != null && picked.Bevels.Count > 0) DrawBevelMarkers(picked, w, h);   // the angle of each cut
            if (_pulled.Count > 0) DrawPulledGroups(w, h);                                         // a pulled-out drawer and how far it came out
        }

        private void DrawDimension(DimensionLine line, double w, double h, bool gapLine = false)
        {
            if (line.LengthMm < 1e-6) return;
            if (_camera.Project(line.A, w, h) is not { } a || _camera.Project(line.B, w, h) is not { } b
                || _camera.Project(line.Start, w, h) is not { } s || _camera.Project(line.End, w, h) is not { } e) return;

            // the short lines joining the measured points to the dimension line
            AddLine(a, s, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(b, e, "TextSecondary", 1, 0.7, dashed: true);
            AddLine(s, e, gapLine ? "StatusWarningFg" : "AccentColor", 1.75, 1, dashed: gapLine);
            AddArrow(s, e, gapLine ? "StatusWarningFg" : "AccentColor");
            AddArrow(e, s, gapLine ? "StatusWarningFg" : "AccentColor");

            var label = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), BorderThickness = new Thickness(1) };
            label.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            label.SetResourceReference(Border.BorderBrushProperty, gapLine ? "StatusWarningFg" : "AccentColor");
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
        private void AddArrow(ScreenPoint tip, ScreenPoint other, string brushKey = "AccentColor")
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
            arrow.SetResourceReference(Shape.FillProperty, brushKey);
            Overlay.Children.Add(arrow);
        }
    }
}
