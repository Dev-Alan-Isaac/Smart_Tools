using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    // Draw and extrude: click points on the floor in the 3D view to draw an outline (top view), then it becomes a solid of the chosen
    // thickness. Points land on the grid. Click the first point again, double-click or press Enter to finish; Backspace takes the last
    // point back; Esc stops. Right-drag and the wheel still move the view while drawing. The drawn solid is an ordinary shape after.
    public partial class DimensionModelerView
    {
        private const double ClosePixels = 12;   // how near the first point a click must be to close the outline

        private List<OutlinePoint>? _drawing;
        private OutlinePoint? _drawHover;

        private void ShowExtrudeHeight() =>
            TxtExtrudeHeight.Text = LengthFormat.Number(_settings.ExtrudeMm, _settings.Unit) + " " + LengthFormat.Suffix(_settings.Unit);

        private void TxtExtrudeHeight_LostFocus(object sender, RoutedEventArgs e) => ReadExtrudeHeight();

        private void TxtExtrudeHeight_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ReadExtrudeHeight();
            e.Handled = true;
        }

        private void ReadExtrudeHeight()
        {
            if (LengthFormat.TryParseMm(TxtExtrudeHeight.Text, _settings.Unit, out double mm) && mm >= Extrude.MinHeight && mm <= SceneEdit.MaxSize)
            {
                _settings.ExtrudeMm = mm;
                ScheduleSave();
            }
            ShowExtrudeHeight();   // a bad value goes back to the last good one
        }

        private void BtnExtrude_Click(object sender, RoutedEventArgs e)
        {
            if (_drawing != null)
            {
                CancelDrawing();
                return;
            }
            ReadExtrudeHeight();
            _drawing = new List<OutlinePoint>();
            _drawHover = null;
            StageHost.Cursor = Cursors.Cross;
            StageHost.Focus();
            Pick(Array.Empty<string>());
            TxtStatus.Text = Loc.T("Str_Modeler_DrawStart", "Click points on the floor to draw the outline. Click the first point, double-click or press Enter to finish · Backspace undoes a point · Esc stops.");
            RedrawOverlay();
        }

        private bool CancelDrawing()
        {
            if (_drawing == null) return false;
            _drawing = null;
            _drawHover = null;
            StageHost.Cursor = null;
            TxtStatus.Text = Loc.T("Str_Modeler_DrawCancelled", "Drawing stopped.");
            RedrawOverlay();
            return true;
        }

        /// <summary>Where the mouse meets the floor, on the grid; null when the view looks along the floor.</summary>
        private OutlinePoint? FloorPoint(Point at)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10) return null;
            if (PieceDrag.RayHitsHeight(_camera.Ray(at.X, at.Y, w, h), 0) is not { } p) return null;
            double g = _settings.SnapGridMm;
            return g > 0 ? new OutlinePoint(Math.Round(p.X / g) * g, Math.Round(p.Z / g) * g) : new OutlinePoint(p.X, p.Z);
        }

        /// <summary>A left click while drawing: add a point, or finish when it is on the first point or a double-click.</summary>
        private void DrawClick(Point at, int clickCount)
        {
            if (_drawing == null) return;
            if (clickCount >= 2 && _drawing.Count >= 3)
            {
                FinishDrawing();
                return;
            }
            if (_drawing.Count >= 3 && NearFirst(at))
            {
                FinishDrawing();
                return;
            }
            if (FloorPoint(at) is not { } p) return;
            if (_drawing.Count > 0 && _drawing[^1] == p) return;   // the same grid point twice
            if (_drawing.Count >= Extrude.MaxPoints) return;
            _drawing.Add(p);
            TxtStatus.Text = Loc.N(_drawing.Count, "Str_Modeler_DrawOne", "1 point. Keep clicking around the outline.",
                "Str_Modeler_DrawMany", "{0} points. Click the first point, double-click or press Enter to finish.", _drawing.Count);
            RedrawOverlay();
        }

        private bool NearFirst(Point at)
        {
            if (_drawing is not { Count: > 0 } d) return false;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (_camera.Project(new Vec3(d[0].X, 0, d[0].Z), w, h) is not { } first) return false;
            return Math.Abs(first.X - at.X) <= ClosePixels && Math.Abs(first.Y - at.Y) <= ClosePixels;
        }

        private void DrawHover(Point at)
        {
            _drawHover = FloorPoint(at);
            RedrawOverlay();
        }

        /// <summary>Enter / Backspace while drawing. True when the key was used.</summary>
        private bool DrawKey(Key key)
        {
            if (_drawing == null) return false;
            switch (key)
            {
                case Key.Enter:
                    if (_drawing.Count >= 3) FinishDrawing();
                    return true;
                case Key.Back:
                    if (_drawing.Count > 0) _drawing.RemoveAt(_drawing.Count - 1);
                    RedrawOverlay();
                    return true;
                default:
                    return false;
            }
        }

        private void FinishDrawing()
        {
            if (_drawing == null) return;
            var problem = Extrude.Check(_drawing);
            if (problem != OutlineProblem.None)
            {
                // keep the points, so a crossing can be fixed with Backspace
                TxtStatus.Text = problem switch
                {
                    OutlineProblem.Crossing => Loc.T("Str_Modeler_DrawCrossing", "The outline crosses itself. Backspace takes points back; an outline must go round once without crossing."),
                    OutlineProblem.TooManyPoints => Loc.F("Str_Modeler_DrawTooMany", "An outline can have at most {0} points.", Extrude.MaxPoints),
                    _ => Loc.T("Str_Modeler_DrawTooSmall", "That outline has no area: it needs at least 3 points that are not on one line."),
                };
                return;
            }
            var made = Extrude.Make(Loc.T("Str_Modeler_DrawnName", "Drawn shape"), _drawing, _settings.ExtrudeMm);
            _drawing = null;
            _drawHover = null;
            StageHost.Cursor = null;
            if (made == null) return;

            var next = SceneEdit.AddNodes(_scene, new SceneNode[] { made.Shape }, out var ids, new Dictionary<string, MeshData> { [made.Mesh.Id] = made.Mesh });
            if (ids.Count == 0)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_TooMany", "A design can have at most {0} shapes.", SceneEdit.MaxShapes);
                RedrawOverlay();
                return;
            }
            Change(next, Loc.T("Str_Modeler_Drawn", "Extruded. Alt+drag its top to change the thickness, or type it under Properties."));
            Pick(ids);
        }

        /// <summary>The outline being drawn: its lines on the floor, a line to the mouse, and the first point ringed (click it to finish).</summary>
        private void DrawOutline(double w, double h)
        {
            if (_drawing == null) return;
            var points = _drawing.ToList();
            if (_drawHover is { } hover && (points.Count == 0 || points[^1] != hover)) points.Add(hover);
            bool crossing = _drawing.Count >= 3 && Extrude.Crosses(_drawing);
            string brush = crossing ? "StatusErrorFg" : "AccentColor";

            ScreenPoint? At(OutlinePoint p) => _camera.Project(new Vec3(p.X, 0, p.Z), w, h);
            for (int i = 0; i + 1 < points.Count; i++)
                if (At(points[i]) is { } a && At(points[i + 1]) is { } b)
                    AddLine(a, b, brush, i + 1 < _drawing.Count ? 2 : 1.25, 1, dashed: i + 1 >= _drawing.Count);
            // where the outline will close
            if (points.Count >= 3 && At(points[^1]) is { } last && At(points[0]) is { } start)
                AddLine(last, start, brush, 1, 0.5, dashed: true);

            for (int i = 0; i < _drawing.Count; i++)
            {
                if (At(_drawing[i]) is not { } p) continue;
                double size = i == 0 ? 12 : 7;
                var dot = new Ellipse { Width = size, Height = size, StrokeThickness = i == 0 ? 2 : 0 };
                if (i == 0)
                {
                    dot.SetResourceReference(Shape.StrokeProperty, brush);
                    dot.SetResourceReference(Shape.FillProperty, "PopupBg");
                }
                else dot.SetResourceReference(Shape.FillProperty, brush);
                Canvas.SetLeft(dot, p.X - size / 2);
                Canvas.SetTop(dot, p.Y - size / 2);
                Overlay.Children.Add(dot);
            }
        }
    }
}
