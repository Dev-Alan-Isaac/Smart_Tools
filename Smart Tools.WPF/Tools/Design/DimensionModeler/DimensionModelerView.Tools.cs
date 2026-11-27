using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    // The everyday modeler tools: copy / cut / paste, repeat in a row, box select (Ctrl+drag on empty space), and the tape measure
    // (click two points, read the distance). The maths lives in Core/SceneTools.cs.
    public partial class DimensionModelerView
    {
        private SceneClip? _clip;
        private int _pasteCount;

        // =================================================================
        //  Copy, cut, paste
        // =================================================================

        private void CopyPicked()
        {
            if (SceneTools.Copy(_scene, _picked) is not { } clip) return;
            _clip = clip;
            _pasteCount = 0;
            TxtStatus.Text = Loc.N(clip.Nodes.Count, "Str_Modeler_CopiedOne", "Copied. Ctrl+V pastes it.", "Str_Modeler_CopiedMany", "{0} copied. Ctrl+V pastes them.", clip.Nodes.Count);
        }

        private void CutPicked()
        {
            if (_picked.Count == 0) return;
            if (_picked.Any(IsLocked))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                return;
            }
            CopyPicked();
            var next = SceneEdit.Delete(_scene, _picked);
            _picked.Clear();
            Change(next, Loc.T("Str_Modeler_Cut", "Cut. Ctrl+V pastes it back."));
        }

        private void PasteClip()
        {
            if (_clip == null) return;
            _pasteCount++;
            double step = Math.Max(10, _settings.SnapGridMm) * 2 * _pasteCount;   // each paste a bit further, so copies don't hide each other
            var next = SceneTools.Paste(_scene, _clip, new Vec3(step, 0, step), out var ids);
            if (ids.Count == 0)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_TooMany", "A design can have at most {0} shapes.", SceneEdit.MaxShapes);
                return;
            }
            Change(next, Loc.T("Str_Modeler_Pasted", "Pasted."));
            Pick(ids);
        }

        // =================================================================
        //  Repeat in a row
        // =================================================================

        private void RepeatPicked()
        {
            var ids = _picked.ToList();
            if (ids.Count == 0) return;
            if (ids.Any(IsLocked))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                return;
            }
            if (AskRepeat() is not { } r) return;
            var next = SceneTools.Repeat(_scene, ids, r.Count, r.Axis, r.Gap, out var made);
            if (made.Count == 0)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_TooMany", "A design can have at most {0} shapes.", SceneEdit.MaxShapes);
                return;
            }
            Change(next, Loc.N(made.Count, "Str_Modeler_RepeatedOne", "1 copy added.", "Str_Modeler_RepeatedMany", "{0} copies added in a row.", made.Count));
            Pick(ids.Concat(made));
        }

        /// <summary>How many copies, which way and how far apart; null when cancelled.</summary>
        private (int Count, int Axis, double Gap)? AskRepeat()
        {
            (int, int, double)? result = null;
            AppModal.Present(null, close =>
            {
                var stack = new StackPanel();
                stack.Children.Add(AppModal.Heading(Loc.T("Str_Modeler_RepeatTitle", "Repeat in a row"), 6));
                var hint = new TextBlock { Text = Loc.T("Str_Modeler_RepeatHint", "Copies are placed one after another, each the size of the pick plus the gap."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(hint);

                TextBlock Label(string key, string fallback)
                {
                    var t = new TextBlock { Text = Loc.T(key, fallback), Margin = new Thickness(0, 8, 0, 4) };
                    t.SetResourceReference(StyleProperty, "FieldLabel");
                    return t;
                }
                var count = new TextBox { Text = "3" };
                var axis = new ComboBox();
                axis.Items.Add(Loc.T("Str_Modeler_RepeatX", "Left to right (X)"));
                axis.Items.Add(Loc.T("Str_Modeler_RepeatY", "Upward (Y)"));
                axis.Items.Add(Loc.T("Str_Modeler_RepeatZ", "Front to back (Z)"));
                axis.SelectedIndex = 0;
                var gap = new TextBox { Text = LengthFormat.Number(10, _settings.Unit) + " " + LengthFormat.Suffix(_settings.Unit) };
                stack.Children.Add(Label("Str_Modeler_RepeatCount", "Copies"));
                stack.Children.Add(count);
                stack.Children.Add(Label("Str_Modeler_RepeatDirection", "Direction"));
                stack.Children.Add(axis);
                stack.Children.Add(Label("Str_Modeler_RepeatGap", "Gap between copies"));
                stack.Children.Add(gap);
                var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
                error.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                stack.Children.Add(error);

                void Accept()
                {
                    if (!int.TryParse(count.Text.Trim(), out int n) || n < 1 || n > SceneTools.MaxRepeat)
                    {
                        error.Text = Loc.F("Str_Modeler_RepeatBadCount", "Copies must be a whole number from 1 to {0}.", SceneTools.MaxRepeat);
                        error.Visibility = Visibility.Visible;
                        return;
                    }
                    if (!LengthFormat.TryParseMm(gap.Text, _settings.Unit, out double mm) || Math.Abs(mm) > SceneEdit.MaxSize)
                    {
                        error.Text = Loc.T("Str_Modeler_RepeatBadGap", "The gap must be a length, like 10 or 1 cm (0 = touching).");
                        error.Visibility = Visibility.Visible;
                        return;
                    }
                    result = (n, axis.SelectedIndex, mm);
                    close();
                }
                count.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
                gap.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };

                var ok = AppModal.MakeButton(Loc.T("Str_Modeler_RepeatOk", "Repeat"), primary: true);
                ok.Click += (_, _) => Accept();
                var cancel = AppModal.MakeButton(AppModal.Text("Str_Vault_Cancel", "Cancel"), primary: false);
                cancel.Click += (_, _) => close();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                stack.Children.Add(buttons);
                AppModal.CancelOnEscape(stack, close);
                return (stack, count);
            }, 380, 460);
            return result;
        }

        // =================================================================
        //  Box select: Ctrl+drag on empty space
        // =================================================================

        private Point? _marqueeStart;
        private Point _marqueeEnd;

        private void StartMarquee(Point at)
        {
            _marqueeStart = at;
            _marqueeEnd = at;
            StageHost.CaptureMouse();
        }

        private void MoveMarquee(Point at)
        {
            _marqueeEnd = at;
            RedrawOverlay();
        }

        /// <summary>Picks (adds to the pick) every visible object whose whole box falls inside the rectangle.</summary>
        private void EndMarquee()
        {
            if (_marqueeStart is not { } a) return;
            _marqueeStart = null;
            StageHost.ReleaseMouseCapture();
            var b = _marqueeEnd;
            double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            if (x1 - x0 < 4 && y1 - y0 < 4)
            {
                RedrawOverlay();
                return;
            }
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            var inside = new List<string>();
            foreach (var node in _scene.Nodes.Where(n => !n.Hidden))
            {
                var box = SceneEdit.BoundsOf(_scene, node);
                if (box.IsEmpty) continue;
                bool all = true;
                for (int i = 0; i < 8 && all; i++)
                {
                    var corner = new Vec3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                    all = _camera.Project(corner, w, h) is { } p && p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
                }
                if (all) inside.Add(node.Id);
            }
            Pick(_picked.Where(id => !IsPart(id)).Concat(inside));
            TxtStatus.Text = Loc.N(_picked.Count, "Str_Modeler_BoxPickedOne", "1 object picked.", "Str_Modeler_BoxPickedMany", "{0} objects picked.", _picked.Count);
        }

        private void DrawMarquee()
        {
            if (_marqueeStart is not { } a) return;
            var b = _marqueeEnd;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Abs(b.X - a.X),
                Height = Math.Abs(b.Y - a.Y),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.9,
            };
            rect.SetResourceReference(Shape.StrokeProperty, "AccentColor");
            rect.SetResourceReference(Shape.FillProperty, "AccentColorSubtle");
            Canvas.SetLeft(rect, Math.Min(a.X, b.X));
            Canvas.SetTop(rect, Math.Min(a.Y, b.Y));
            Overlay.Children.Add(rect);
        }

        // =================================================================
        //  Tape measure: click two points
        // =================================================================

        private const double CornerSnapPixels = 10;

        private bool _measuring;
        private Vec3? _tapeA;
        private Vec3? _tapeB;
        private Vec3? _tapeHover;

        private void BtnMeasure_Click(object sender, RoutedEventArgs e)
        {
            if (_measuring)
            {
                StopMeasuring();
                return;
            }
            CancelDrawing();
            _measuring = true;
            _tapeA = _tapeB = _tapeHover = null;
            StageHost.Cursor = Cursors.Cross;
            StageHost.Focus();
            TxtStatus.Text = Loc.T("Str_Modeler_MeasureStart", "Click the first point, then the second. Corners of shapes catch the pointer. Esc stops.");
            RedrawOverlay();
        }

        private bool StopMeasuring()
        {
            if (!_measuring) return false;
            _measuring = false;
            _tapeA = _tapeB = _tapeHover = null;
            StageHost.Cursor = null;
            RedrawOverlay();
            return true;
        }

        /// <summary>The 3D point under the mouse: a shape corner near the pointer, else the surface hit, else the floor.</summary>
        private Vec3? TapePoint(Point at)
        {
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (w < 10 || h < 10) return null;
            Vec3? best = null;
            double bestPx = CornerSnapPixels;
            foreach (var node in _scene.Nodes.Where(n => !n.Hidden))
            {
                var box = SceneEdit.BoundsOf(_scene, node);
                if (box.IsEmpty) continue;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vec3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                    if (_camera.Project(corner, w, h) is not { } p) continue;
                    double d = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));
                    if (d < bestPx) { bestPx = d; best = corner; }
                }
            }
            if (best != null) return best;
            var ray = _camera.Ray(at.X, at.Y, w, h);
            if (ScenePick.Pick(_objects, ray) is { } hit) return hit.Point;
            return PieceDrag.RayHitsHeight(ray, 0);
        }

        private void MeasureClick(Point at)
        {
            if (TapePoint(at) is not { } p) return;
            if (_tapeA == null || _tapeB != null)
            {
                _tapeA = p;
                _tapeB = null;
            }
            else
            {
                _tapeB = p;
                var d = p - _tapeA.Value;
                string F(double mm) => LengthFormat.Format(Math.Abs(mm), _settings.Unit);
                TxtStatus.Text = Loc.F("Str_Modeler_MeasureResult", "Distance {0}  (X {1} · Y {2} · Z {3}). Click again to measure another.", F(d.Length), F(d.X), F(d.Y), F(d.Z));
            }
            RedrawOverlay();
        }

        private void MeasureHover(Point at)
        {
            _tapeHover = TapePoint(at);
            if (_tapeA != null && _tapeB == null) RedrawOverlay();
        }

        private void DrawTape(double w, double h)
        {
            if (!_measuring || _tapeA is not { } a) return;
            var b = _tapeB ?? _tapeHover;
            if (_camera.Project(a, w, h) is not { } pa) return;
            AddTapeDot(pa);
            if (b is not { } end || _camera.Project(end, w, h) is not { } pb) return;
            AddLine(pa, pb, "StatusWarningFg", 2, 1, dashed: _tapeB == null);
            AddTapeDot(pb);
            var label = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), BorderThickness = new Thickness(1) };
            label.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            label.SetResourceReference(Border.BorderBrushProperty, "StatusWarningFg");
            var text = new TextBlock { Text = LengthFormat.Format((end - a).Length, _settings.Unit), FontSize = 12, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            label.Child = text;
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, (pa.X + pb.X) / 2 - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, (pa.Y + pb.Y) / 2 - label.DesiredSize.Height - 6);
            Overlay.Children.Add(label);
        }

        private void AddTapeDot(ScreenPoint p)
        {
            var dot = new Ellipse { Width = 9, Height = 9 };
            dot.SetResourceReference(Shape.FillProperty, "StatusWarningFg");
            Canvas.SetLeft(dot, p.X - 4.5);
            Canvas.SetTop(dot, p.Y - 4.5);
            Overlay.Children.Add(dot);
        }
    }
}
