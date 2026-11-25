using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Smart_Tools.WPF.Views
{
    // Working on the custom piece straight in the 3D view, like a CAD program: click a board to pick it, drag it over the floor
    // (Shift = lift it), drag the squares on its faces to stretch it. While you drag, a board jumps to the faces of the other boards,
    // to the floor and to the grid. The maths is PieceDrag (pure, tested); this file reads the mouse and shows the result.
    public partial class DimensionModelerView
    {
        private const double HandleSize = 10;
        private const double HandleReach = 9;      // pixels from a handle's centre that still grab it
        private const double SnapPixels = 8;       // how close, on screen, a face has to get to stick

        private enum EditKind { Move, Lift, Resize }

        private sealed class Edit
        {
            public EditKind Kind;
            public int Index;
            public PieceState Before = null!;
            public Vec3 StartMin;
            public Vec3 Grab;              // where the pointer first touched (a point on the board, or a handle's centre)
            public double StartParam;      // lift and resize: where along the axis the pointer started
            public int Axis;
            public bool High;
            public double StartFace;
            public Point Press;
            public bool Moved;
        }

        private Edit? _edit;
        private bool _loadingSnap;

        private static Vec3 AxisVector(int axis) => axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);

        private static double Along(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        /// <summary>The six square handles of a board: the middle of each face.</summary>
        private static IEnumerable<(int Axis, bool High, Vec3 Center)> Handles(Part p)
        {
            var mid = p.Min + p.Size * 0.5;
            for (int axis = 0; axis < 3; axis++)
            {
                foreach (bool high in new[] { false, true })
                {
                    var face = high ? p.Max : p.Min;
                    yield return (axis, high, axis == 0 ? mid with { X = face.X } : axis == 1 ? mid with { Y = face.Y } : mid with { Z = face.Z });
                }
            }
        }

        // =================================================================
        //  Settings under the board list
        // =================================================================

        private void ComboGrid_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _loadingSnap || ComboGrid.SelectedItem is not ComboBoxItem { Tag: double mm }) return;
            _settings.SnapGridMm = mm;
            ScheduleSave();
        }

        private void ChkMagnet_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _loadingSnap) return;
            _settings.MagnetOn = ChkMagnet.IsChecked == true;
            ScheduleSave();
        }

        // =================================================================
        //  The gesture
        // =================================================================

        /// <summary>A left press in the 3D view: grabs a handle or a board and returns true, or false when it landed on empty space.</summary>
        private bool BeginEdit(Point pos)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40 || _customParts.Count == 0) return false;
            var ray = _camera.Ray(pos.X, pos.Y, w, h);
            int selected = ListParts.SelectedIndex;

            if (selected >= 0 && selected < _customParts.Count)
            {
                var part = _customParts[selected];
                foreach (var (axis, high, center) in Handles(part))
                {
                    if (_camera.Project(center, w, h) is not { } screen) continue;
                    if (Math.Abs(screen.X - pos.X) > HandleReach || Math.Abs(screen.Y - pos.Y) > HandleReach) continue;
                    if (PieceDrag.ClosestOnAxis(ray, center, AxisVector(axis)) is not { } start) continue;
                    _edit = new Edit
                    {
                        Kind = EditKind.Resize, Index = selected, Before = Snapshot(), StartMin = part.Min, Grab = center,
                        StartParam = start, Axis = axis, High = high, StartFace = Along(center, axis), Press = pos,
                    };
                    return true;
                }
            }

            int index = PieceDrag.Pick(_customParts, ray, out double distance, p => _hidden.Contains(p.Name));
            if (index < 0) return false;
            if (index != selected) ListParts.SelectedIndex = index;   // picking it also lights it up and fills the boxes
            var grab = PieceDrag.PointAt(ray, distance);
            bool lift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            double param = 0;
            if (lift)
            {
                if (PieceDrag.ClosestOnAxis(ray, grab, AxisVector(1)) is not { } up) return false;
                param = up;
            }
            _edit = new Edit
            {
                Kind = lift ? EditKind.Lift : EditKind.Move, Index = index, Before = Snapshot(), StartMin = _customParts[index].Min,
                Grab = grab, StartParam = param, Press = pos,
            };
            return true;
        }

        private void UpdateEdit(Point pos)
        {
            var e = _edit;
            if (e == null) return;
            if (!e.Moved && Math.Abs(pos.X - e.Press.X) < 3 && Math.Abs(pos.Y - e.Press.Y) < 3) return;   // still a click
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40) return;
            var ray = _camera.Ray(pos.X, pos.Y, w, h);
            var start = e.Before.Parts;
            double grid = _settings.SnapGridMm, threshold = _camera.MmPerPixel(w) * SnapPixels;
            bool magnet = _settings.MagnetOn;
            Part moved;
            int[]? stuck = null;

            switch (e.Kind)
            {
                case EditKind.Move:
                {
                    if (PieceDrag.RayHitsHeight(ray, e.Grab.Y) is not { } floor) return;
                    var proposed = e.StartMin + new Vec3(floor.X - e.Grab.X, 0, floor.Z - e.Grab.Z);
                    var snap = PieceDrag.Move(start, e.Index, proposed, grid, threshold, magnet);
                    stuck = snap.Target;
                    moved = start[e.Index] with { Min = snap.Min };
                    break;
                }
                case EditKind.Lift:
                {
                    if (PieceDrag.ClosestOnAxis(ray, e.Grab, AxisVector(1)) is not { } now) return;
                    var proposed = e.StartMin + new Vec3(0, now - e.StartParam, 0);
                    var snap = PieceDrag.Move(start, e.Index, proposed, grid, threshold, magnet);
                    stuck = new[] { SnapResult.None, snap.Target[1], SnapResult.None };
                    moved = start[e.Index] with { Min = e.StartMin with { Y = snap.Min.Y } };   // only the height changes
                    break;
                }
                default:
                {
                    if (PieceDrag.ClosestOnAxis(ray, e.Grab, AxisVector(e.Axis)) is not { } now) return;
                    moved = PieceDrag.Resize(start, e.Index, e.Axis, e.High, e.StartFace + (now - e.StartParam), grid, threshold, magnet);
                    break;
                }
            }

            e.Moved = true;
            ApplyLive(PieceEditor.Replace(start, e.Index, moved), e, stuck);
        }

        /// <summary>Shows the board where the drag has it, without recording anything yet (that happens once, on release).</summary>
        private void ApplyLive(IReadOnlyList<Part> parts, Edit e, int[]? stuck)
        {
            _customParts = parts;
            _model = PieceEditor.ToModel(_customName, parts);
            RebuildScene();
            RedrawOverlay();
            var p = parts[e.Index];
            FillEditor(p);

            string L(double mm) => LengthFormat.Format(mm, _settings.Unit);
            if (e.Kind == EditKind.Resize)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_ResizeStatus", "{0}: {1} × {2} × {3}", p.Name, L(p.Size.X), L(p.Size.Y), L(p.Size.Z));
                return;
            }
            var names = new List<string>();
            if (stuck != null)
            {
                foreach (int t in stuck)
                {
                    string? name = t == SnapResult.Floor ? Loc.T("Str_Modeler_DragFloor", "the floor") : t >= 0 && t < parts.Count ? parts[t].Name : null;
                    if (name != null && !names.Contains(name)) names.Add(name);
                }
            }
            string text = Loc.F("Str_Modeler_DragStatus", "{0}: X {1}   Y {2}   Z {3}", p.Name, L(p.Min.X), L(p.Min.Y), L(p.Min.Z));
            if (names.Count > 0) text += "   ·   " + Loc.F("Str_Modeler_DragSticks", "sticks to {0}", string.Join(", ", names));
            TxtStatus.Text = text;
        }

        /// <summary>The mouse was released: one undo step for the whole drag, then the usual full refresh and save.</summary>
        private void EndEdit()
        {
            var e = _edit;
            _edit = null;
            StageHost.ReleaseMouseCapture();
            StageHost.Cursor = null;
            if (e == null || !e.Moved) return;   // only a click: the board is already picked
            _history.Push(e.Before, Snapshot());
            CustomChanged(e.Index);
        }

        // =================================================================
        //  The handles
        // =================================================================

        /// <summary>The six squares on the picked board, drawn in the overlay with the same camera as the 3D view.</summary>
        private void DrawHandles()
        {
            int selected = ListParts.SelectedIndex;
            if (!_isCustom || selected < 0 || selected >= _customParts.Count || _hidden.Contains(_customParts[selected].Name)) return;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 40 || h < 40) return;
            foreach (var (_, _, center) in Handles(_customParts[selected]))
            {
                if (_camera.Project(center, w, h) is not { } s) continue;
                var square = new Rectangle { Width = HandleSize, Height = HandleSize, RadiusX = 2, RadiusY = 2, StrokeThickness = 1.5 };
                square.SetResourceReference(Shape.FillProperty, "AccentColor");
                square.SetResourceReference(Shape.StrokeProperty, "PopupBg");
                Canvas.SetLeft(square, s.X - HandleSize / 2);
                Canvas.SetTop(square, s.Y - HandleSize / 2);
                Overlay.Children.Add(square);
            }
        }
    }
}
