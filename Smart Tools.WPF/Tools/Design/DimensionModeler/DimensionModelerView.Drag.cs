using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    // Dragging in the 3D view. Press on an object and drag: it slides over the floor (Shift: up and down; Alt: the face pressed is
    // pulled, so the object grows or shrinks on that side). It snaps to the other objects (side by side, flush corners, centre on
    // centre) and to the floor, otherwise to the grid. While dragging only the drawn copy moves (no rebuild); letting go makes it one
    // change (one undo step). Esc puts it back.
    public partial class DimensionModelerView
    {
        private const double SnapPixels = 9;      // how close on screen a match must be to snap
        private const double DragStartPixels = 4;

        private enum DragMode { Slide, Lift, Resize }

        private sealed class DragState
        {
            public required string[] Ids;              // the top-level objects being moved (one when resizing)
            public required Scene StartScene;
            public required Bounds3 StartBox;
            public required Vec3 Grab;                 // the point of the object that was pressed
            public required DragMode Mode;
            public int Axis;                           // Resize: the axis of the pulled face
            public bool HighFace;                      // Resize: the face on the high side of that axis
            public required IReadOnlyList<Bounds3> Others;
            public required IReadOnlyList<string> OtherNames;
            public Bounds3 Box;                        // where the box is now
            public IReadOnlyList<SnapGuide> Guides = Array.Empty<SnapGuide>();
        }

        private SceneHit? _pressHit;   // what the left button went down on (a drag of it may start)
        private DragState? _drag;

        /// <summary>Left button down on an object: remember it; moving far enough starts a drag, letting go in place is a click.</summary>
        private bool TryBeginPress(Point at)
        {
            _pressHit = null;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;   // Ctrl+click adds to the pick, never drags
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10) return false;
            if (ScenePick.Pick(_objects, _camera.Ray(at.X, at.Y, w, h)) is not { } hit) return false;
            _pressHit = hit;
            return true;
        }

        private void StartDrag(SceneHit hit)
        {
            var mods = Keyboard.Modifiers;
            var top = SceneEdit.TopOf(_scene, hit.NodeId)?.Id ?? hit.NodeId;
            var pickedTops = PickedObjects();
            var mode = mods.HasFlag(ModifierKeys.Alt) ? DragMode.Resize : mods.HasFlag(ModifierKeys.Shift) ? DragMode.Lift : DragMode.Slide;

            // a resize pulls one object's face; a move takes the whole pick along when the pressed object is in it
            string[] ids = mode == DragMode.Resize || !pickedTops.Contains(top) ? new[] { top } : pickedTops.ToArray();

            // a picked part of a group under the mouse: only that part moves (or is resized), the rest of the group stays
            bool partMode = false;
            var pickedParts = _picked.Where(IsPart).ToList();
            if (pickedParts.Count > 0 && SceneEdit.Find(_scene, top) is GroupNode grp
                && ScenePick.PickShape(_scene, grp, _camera.Ray(_pressPoint.X, _pressPoint.Y, StageHost.ActualWidth, StageHost.ActualHeight)) is { } partHit)
            {
                var owner = pickedParts.FirstOrDefault(p => SceneEdit.Find(_scene, p) is { } pn && SceneEdit.Shapes(pn).Any(s => s.Id == partHit.NodeId));
                if (owner != null)
                {
                    hit = partHit;
                    ids = new[] { owner };
                    partMode = true;
                }
            }
            if (ids.Any(IsLocked))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                return;
            }

            int axis = 0;
            bool high = false;
            if (mode == DragMode.Resize)
            {
                // the face under the mouse: the side the surface faces most (a slanted or round surface pulls its main side)
                var n = hit.Normal;
                double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
                axis = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
                high = (axis == 0 ? n.X : axis == 1 ? n.Y : n.Z) > 0;
            }
            if (!partMode && !(pickedTops.Count == ids.Length && ids.All(pickedTops.Contains))) Pick(ids);

            var moving = ids.ToHashSet();
            var movingShapes = ids.Select(i => SceneEdit.Find(_scene, i)).OfType<SceneNode>().SelectMany(SceneEdit.Shapes).Select(s => s.Id).ToHashSet();
            // what it can snap to: the other objects, and for a part also the other parts of its own group
            var others = partMode
                ? _scene.Nodes.Where(n => n.Id != top && !n.Hidden)
                    .Concat(SceneEdit.Shapes(SceneEdit.Find(_scene, top)!).Where(s => !movingShapes.Contains(s.Id) && !s.Hidden))
                    .ToList()
                : _scene.Nodes.Where(n => !moving.Contains(n.Id) && !n.Hidden).ToList();
            var start = SceneEdit.BoundsOf(_scene, ids);
            _drag = new DragState
            {
                Ids = ids,
                StartScene = _scene,
                StartBox = start,
                Box = start,
                Grab = hit.Point,
                Mode = mode,
                Axis = axis,
                HighFace = high,
                Others = others.Select(n => SceneEdit.BoundsOf(_scene, n)).ToList(),
                OtherNames = others.Select(n => n.Name).ToList(),
            };
        }

        private void DragTo(Point at)
        {
            if (_drag is not { } d) return;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            var ray = _camera.Ray(at.X, at.Y, w, h);
            double threshold = SnapPixels * _camera.MmPerPixel(w);

            if (d.Mode == DragMode.Resize)
            {
                var unit = d.Axis == 0 ? new Vec3(1, 0, 0) : d.Axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
                if (PieceDrag.ClosestOnAxis(ray, d.Grab, unit) is not { } t) return;
                double startFace = Side(d.HighFace ? d.StartBox.Max : d.StartBox.Min, d.Axis);
                var (box, guide) = SceneSnap.Face(d.StartBox, d.Axis, d.HighFace, startFace + t, d.Others, _settings.SnapGridMm, threshold, _settings.MagnetOn);
                d.Box = box;
                d.Guides = guide is { } g ? new[] { g } : Array.Empty<SnapGuide>();
                _scene = SceneEdit.Resize(d.StartScene, d.Ids[0], box);
            }
            else
            {
                Vec3 proposed;
                if (d.Mode == DragMode.Lift)
                {
                    if (PieceDrag.ClosestOnAxis(ray, d.Grab, new Vec3(0, 1, 0)) is not { } t) return;
                    proposed = new Vec3(0, t, 0);
                }
                else
                {
                    if (PieceDrag.RayHitsHeight(ray, d.Grab.Y) is not { } p) return;   // looking along the floor: keep the last place
                    proposed = new Vec3(p.X - d.Grab.X, 0, p.Z - d.Grab.Z);
                }
                var axes = d.Mode == DragMode.Lift ? new[] { false, true, false } : new[] { true, false, true };
                var snap = SceneSnap.Move(d.StartBox, proposed, d.Others, axes, _settings.SnapGridMm, threshold, _settings.MagnetOn);
                d.Box = new Bounds3(d.StartBox.Min + snap.Delta, d.StartBox.Max + snap.Delta);
                d.Guides = snap.Guides;
                _scene = SceneEdit.Translate(d.StartScene, d.Ids, snap.Delta);   // shown, not yet a change (no undo step, no save)
            }
            DrawObjects();
            RedrawOverlay();
            TxtStatus.Text = DragStatus(d);
        }

        private static double Side(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        /// <summary>Lets go: the move or resize becomes one change (undo puts it back in one step).</summary>
        private void EndDrag()
        {
            if (_drag is not { } d) return;
            _drag = null;
            var result = _scene;
            _scene = d.StartScene;
            Change(result, DragStatus(d));
            if (result.Equals(d.StartScene)) { DrawObjects(); RedrawOverlay(); }
        }

        /// <summary>Esc while dragging: back where it was.</summary>
        private bool CancelDrag()
        {
            if (_drag is not { } d) return false;
            _drag = null;
            _pressHit = null;
            _scene = d.StartScene;
            StageHost.ReleaseMouseCapture();
            DrawObjects();
            RedrawOverlay();
            TxtStatus.Text = Loc.T("Str_Modeler_DragCancelled", "Move cancelled.");
            return true;
        }

        private string DragStatus(DragState d)
        {
            string F(double mm) => LengthFormat.Format(mm, _settings.Unit);
            string head = d.Mode == DragMode.Resize
                ? Loc.F("Str_Modeler_ResizeAt", "W {0}  H {1}  D {2}", F(d.Box.Size.X), F(d.Box.Size.Y), F(d.Box.Size.Z))
                : Loc.F("Str_Modeler_DragAt", "X {0}  Y {1}  Z {2}", F(d.Box.Min.X), F(d.Box.Min.Y), F(d.Box.Min.Z));
            if (d.Guides.Count == 0) return head;
            var parts = d.Guides.Select(g => g.Kind switch
            {
                SnapKind.Floor => Loc.T("Str_Modeler_SnapFloor", "on the floor"),
                SnapKind.Touch => Loc.F("Str_Modeler_SnapTouch", "against {0}", d.OtherNames[g.Other]),
                SnapKind.Edge => Loc.F("Str_Modeler_SnapEdge", "flush with {0}", d.OtherNames[g.Other]),
                _ => Loc.F("Str_Modeler_SnapCentre", "centred on {0}", d.OtherNames[g.Other]),
            }).Distinct();
            return head + "  ·  " + string.Join(", ", parts);
        }

        /// <summary>
        /// While dragging, the object is drawn moved or stretched from its last finished shape (the cut-out shape is only rebuilt when the
        /// drag ends): a shift for a move, a stretch about the face that stays for a resize.
        /// </summary>
        private Transform3D? DragShift(string nodeId)
        {
            if (_drag is not { } d || !d.Ids.Contains(nodeId)) return null;
            if (d.Mode != DragMode.Resize)
            {
                var delta = d.Box.Min - d.StartBox.Min;
                return new TranslateTransform3D(delta.X, delta.Y, delta.Z);
            }
            double Ratio(double to, double from) => from > 1e-9 ? to / from : 1;
            var s0 = d.StartBox.Size;
            var s1 = d.Box.Size;
            var fixedCorner = d.HighFace ? d.StartBox.Min : d.StartBox.Max;   // the face that stays
            var scale = new ScaleTransform3D(
                d.Axis == 0 ? Ratio(s1.X, s0.X) : 1, d.Axis == 1 ? Ratio(s1.Y, s0.Y) : 1, d.Axis == 2 ? Ratio(s1.Z, s0.Z) : 1,
                fixedCorner.X, fixedCorner.Y, fixedCorner.Z);
            return scale;
        }

        /// <summary>The snap guides: the box snapped to is outlined, and a line joins the two points that now line up.</summary>
        private void DrawSnapGuides(double w, double h)
        {
            if (_drag is not { } d) return;
            foreach (var other in d.Guides.Where(g => g.Other >= 0).Select(g => g.Other).Distinct())
                DrawGuideBox(d.Others[other], w, h);
            foreach (var g in d.Guides)
            {
                if (g.Other < 0) continue;
                var target = d.Others[g.Other];
                var mine = Along(d.Box.Centre, g.Axis, g.At);
                var theirs = Along(target.Centre, g.Axis, g.At);
                if (_camera.Project(mine, w, h) is not { } a || _camera.Project(theirs, w, h) is not { } b) continue;
                AddLine(a, b, "StatusWarningFg", 1.5, 1, dashed: true);
                AddDot(a);
                AddDot(b);
            }
        }

        private static Vec3 Along(Vec3 v, int axis, double value) =>
            axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };

        private void DrawGuideBox(Bounds3 b, double w, double h)
        {
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++)
                c[i] = new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            foreach (var (i, j) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                if (_camera.Project(c[i], w, h) is { } a && _camera.Project(c[j], w, h) is { } z) AddLine(a, z, "StatusWarningFg", 1, 0.6, dashed: true);
        }

        private void AddDot(ScreenPoint p)
        {
            var dot = new Ellipse { Width = 8, Height = 8 };
            dot.SetResourceReference(Shape.FillProperty, "StatusWarningFg");
            Canvas.SetLeft(dot, p.X - 4);
            Canvas.SetTop(dot, p.Y - 4);
            Overlay.Children.Add(dot);
        }

        // =================================================================
        //  Arrow keys
        // =================================================================

        /// <summary>
        /// Arrow keys on the picked shapes: Left/Right = left/right as seen on screen (along X or Z, whichever the view looks along least),
        /// Up/Down = higher/lower (Y), Shift+Up/Down = away from / towards the viewer on the floor. One grid step a press (10 mm with no
        /// grid), Ctrl = 1 mm. Each press is one change, so undo steps back press by press.
        /// </summary>
        private void NudgePicked(Key key, bool fine, bool alongFloor)
        {
            var ids = PickedObjects().ToArray();
            if (ids.Length == 0) return;
            if (ids.Any(IsLocked))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                return;
            }
            double step = fine ? 1 : _settings.SnapGridMm > 0 ? _settings.SnapGridMm : 10;

            var look = _camera.LookDirection;
            // the screen's "right" on the floor, rounded to the nearer of X and Z (so a nudge never goes slanted)
            var right = new Vec3(-look.Z, 0, look.X);
            var rightAxis = Math.Abs(right.X) >= Math.Abs(right.Z) ? new Vec3(Math.Sign(right.X), 0, 0) : new Vec3(0, 0, Math.Sign(right.Z));
            var away = Math.Abs(look.X) > Math.Abs(look.Z) ? new Vec3(Math.Sign(look.X), 0, 0) : new Vec3(0, 0, Math.Sign(look.Z));

            Vec3 dir = key switch
            {
                Key.Right => rightAxis,
                Key.Left => rightAxis * -1,
                Key.Up when alongFloor => away,
                Key.Down when alongFloor => away * -1,
                Key.Up => new Vec3(0, 1, 0),
                _ => new Vec3(0, -1, 0),
            };
            var moved = SceneEdit.Translate(_scene, ids, dir * step);
            var box = SceneEdit.BoundsOf(moved, ids);
            Change(moved, Loc.F("Str_Modeler_DragAt", "X {0}  Y {1}  Z {2}",
                LengthFormat.Format(box.Min.X, _settings.Unit), LengthFormat.Format(box.Min.Y, _settings.Unit), LengthFormat.Format(box.Min.Z, _settings.Unit)));
        }
    }
}
