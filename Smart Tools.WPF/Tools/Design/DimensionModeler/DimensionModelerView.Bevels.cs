using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    // Edges cut at an angle on the picked board of a custom piece: bevels, and miters (a cut right through the board). The rules - one cut
    // per edge, the angle kept to 5..85 degrees, a cut never deeper than the board, holes kept clear of cuts - are PieceBevels.Sanitize and
    // PieceEditor; the shape is made in PieceBevels.Triangles. This file reads the boxes and draws the angle in the 3D view.
    public partial class DimensionModelerView
    {
        private int _bevelIndex;
        private bool _fillingBevels;

        private static string FaceName(int face) => face switch
        {
            0 => Loc.T("Str_Modeler_FaceLeft", "Left"),
            1 => Loc.T("Str_Modeler_FaceRight", "Right"),
            2 => Loc.T("Str_Modeler_FaceBottom", "Bottom"),
            3 => Loc.T("Str_Modeler_FaceTop", "Top"),
            4 => Loc.T("Str_Modeler_FaceBack", "Back"),
            _ => Loc.T("Str_Modeler_FaceFront", "Front"),
        };

        private static string Degrees(double angle) => angle.ToString("0.#", CultureInfo.InvariantCulture) + "°";

        /// <summary>"45° × 8 mm, Front / Top" - or "miter" when the cut goes right through the board.</summary>
        private string BevelText(Part part, Bevel b)
        {
            var (_, intoB) = PieceBevels.Setbacks(b);
            double alongA = PieceBevels.AxisOf(b.FaceA) switch { 0 => part.Size.X, 1 => part.Size.Y, _ => part.Size.Z };
            bool miter = intoB >= alongA - 0.01;
            string what = miter ? Loc.T("Str_Modeler_MiterWord", "miter") : LengthFormat.Format(b.Depth, _settings.Unit);
            return $"{Degrees(b.Angle)} × {what}, {FaceName(b.FaceA)} / {FaceName(b.FaceB)}";
        }

        private void FillBevels(Part part)
        {
            _fillingBevels = true;
            try
            {
                var bevels = part.Bevels;
                bool any = bevels.Count > 0;
                TxtNoBevels.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                PanelBevel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                BtnDeleteBevel.IsEnabled = any;
                BtnAddBevel.IsEnabled = bevels.Count < PieceBevels.MaxBevels;
                ComboBevel.Items.Clear();
                for (int n = 0; n < bevels.Count; n++) ComboBevel.Items.Add(BevelText(part, bevels[n]));
                _bevelIndex = Math.Clamp(_bevelIndex, 0, Math.Max(0, bevels.Count - 1));
                ComboBevel.SelectedIndex = any ? _bevelIndex : -1;
                CapBevelDepth.Text = Loc.F("Str_Modeler_BevelDepth", "Depth ({0})", LengthFormat.Suffix(_settings.Unit));
                if (!any) return;

                var b = bevels[_bevelIndex];
                ComboFaceA.Items.Clear();
                for (int f = 0; f < 6; f++) ComboFaceA.Items.Add(FaceName(f));
                ComboFaceA.SelectedIndex = b.FaceA;
                ComboFaceB.Items.Clear();
                for (int f = 0; f < 6; f++) ComboFaceB.Items.Add(new ComboBoxItem { Content = FaceName(f), IsEnabled = PieceBevels.AxisOf(f) != PieceBevels.AxisOf(b.FaceA) });
                ComboFaceB.SelectedIndex = b.FaceB;
                TxtBevelAngle.Text = b.Angle.ToString("0.#", CultureInfo.InvariantCulture);
                TxtBevelDepth.Text = Num(b.Depth);
            }
            finally
            {
                _fillingBevels = false;
            }
        }

        private void ComboBevel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingBevels || !_viewConstructed || ComboBevel.SelectedIndex < 0) return;
            _bevelIndex = ComboBevel.SelectedIndex;
            int i = ListParts.SelectedIndex;
            if (i >= 0 && i < _customParts.Count) FillBevels(_customParts[i]);
            RedrawOverlay();
        }

        private void BtnAddBevel_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0 || i >= _customParts.Count) return;
            var part = _customParts[i];
            // the board's broad face (a door's front), on the first edge that has no cut yet: a 45 degree chamfer
            int thin = PieceHoles.ThinAxis(part.Size);
            int faceA = thin * 2 + 1;
            double depth = Math.Max(1, Math.Min(5, new[] { part.Size.X, part.Size.Y, part.Size.Z }.Min() / 3));
            foreach (int faceB in Enumerable.Range(0, 6).Where(f => PieceBevels.AxisOf(f) != thin).OrderByDescending(f => f))
            {
                if (part.Bevels.Any(b => b.FaceA == faceA && b.FaceB == faceB || b.FaceA == faceB && b.FaceB == faceA)) continue;
                var bevels = part.Bevels.Append(new Bevel(faceA, faceB, 45, depth)).ToList();
                _bevelIndex = part.Bevels.Count;
                ChangeCustom(PieceEditor.Replace(_customParts, i, part with { Edges = bevels }));
                CustomChanged(i);
                return;
            }
            TxtStatus.Text = Loc.T("Str_Modeler_NoRoomForBevel", "Every edge of that face already has a cut. Change one, or pick another face in its box.");
        }

        private void BtnDeleteBevel_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0 || i >= _customParts.Count || _bevelIndex < 0 || _bevelIndex >= _customParts[i].Bevels.Count) return;
            var part = _customParts[i];
            var bevels = part.Bevels.Where((_, n) => n != _bevelIndex).ToList();
            ChangeCustom(PieceEditor.Replace(_customParts, i, part with { Edges = bevels.Count == 0 ? null : bevels }));
            CustomChanged(i);
        }

        /// <summary>The miter button: the same angle, cut right through the board (the depth is shortened to exactly that).</summary>
        private void BtnMiter_Click(object sender, RoutedEventArgs e) => ApplyBevelEditor(throughBoard: true);

        private void BevelFace_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingBevels || !_viewConstructed) return;
            ApplyBevelEditor(throughBoard: false);
        }

        private void ApplyBevelEditor(bool throughBoard)
        {
            int i = ListParts.SelectedIndex;
            if (_fillingBevels || i < 0 || i >= _customParts.Count) return;
            var part = _customParts[i];
            if (_bevelIndex < 0 || _bevelIndex >= part.Bevels.Count) return;
            var old = part.Bevels[_bevelIndex];

            int faceA = ComboFaceA.SelectedIndex >= 0 ? ComboFaceA.SelectedIndex : old.FaceA;
            int faceB = ComboFaceB.SelectedIndex >= 0 ? ComboFaceB.SelectedIndex : old.FaceB;
            if (PieceBevels.AxisOf(faceA) == PieceBevels.AxisOf(faceB))
            {
                // the face to measure from changed: keep the edge partner if it still makes an edge, else take the next one that does
                faceB = old.FaceB != faceA && PieceBevels.AxisOf(old.FaceB) != PieceBevels.AxisOf(faceA) ? old.FaceB
                    : Enumerable.Range(0, 6).First(f => PieceBevels.AxisOf(f) != PieceBevels.AxisOf(faceA));
            }
            double angle = double.TryParse(TxtBevelAngle.Text.Replace(',', '.').Replace("°", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a : old.Angle;
            double depth = TxtBevelDepth.Text.Trim() == Num(old.Depth) ? old.Depth
                : LengthFormat.TryParseMm(TxtBevelDepth.Text, _settings.Unit, out double mm) ? mm : old.Depth;
            if (throughBoard) depth = 1e6;   // shortened by the rules to exactly through the board

            var bevels = part.Bevels.ToList();
            bevels[_bevelIndex] = new Bevel(faceA, faceB, angle, depth);
            var result = PieceEditor.Replace(_customParts, i, part with { Edges = bevels });
            if (result[i] == part)
            {
                FillBevels(part);   // nothing changed: a bad value goes back to the real one
                return;
            }
            ChangeCustom(result);
            CustomChanged(i);
            if (result[i].Holes.Count < part.Holes.Count)
                TxtStatus.Text = Loc.T("Str_Modeler_HoleRemovedByBevel", "A hole that the new cut would have run into was removed.");
        }

        private void BevelBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) ApplyBevelEditor(throughBoard: false);
        }

        private void BevelBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyBevelEditor(throughBoard: false);
                e.Handled = true;
            }
            else if (e.Key is Key.Up or Key.Down && sender is TextBox box)
            {
                bool up = e.Key == Key.Up;
                if (box == TxtBevelAngle && double.TryParse(box.Text.Replace(',', '.').Replace("°", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
                    box.Text = (angle + (up ? 5 : -5) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.2 : 1)).ToString("0.#", CultureInfo.InvariantCulture);
                else if (box == TxtBevelDepth && LengthFormat.TryParseMm(box.Text, _settings.Unit, out double now))
                    box.Text = Num(Math.Max(0.1, now + (up ? NudgeStep() : -NudgeStep()) / 10));
                ApplyBevelEditor(throughBoard: false);
                e.Handled = true;
            }
        }

        // =================================================================
        //  The angle in the 3D view
        // =================================================================

        /// <summary>For the picked board: each cut's angle as an arc with "45°", and how deep it goes into its face.</summary>
        private void DrawBevelMarkers(Part part, double w, double h)
        {
            foreach (var b in part.Bevels)
            {
                var (edge, onA, onB) = PieceBevels.Marker(part, b);
                if (_camera.Project(edge, w, h) is not { } e || _camera.Project(onA, w, h) is not { } a || _camera.Project(onB, w, h) is not { } c) continue;

                // the cut itself, then the angle between face A (towards the edge) and the cut, drawn at the point on face A
                AddLine(a, c, "StatusWarningFg", 2, 1, dashed: false);
                double a1 = Math.Atan2(e.Y - a.Y, e.X - a.X), a2 = Math.Atan2(c.Y - a.Y, c.X - a.X);
                double sweep = a2 - a1;
                while (sweep > Math.PI) sweep -= 2 * Math.PI;
                while (sweep < -Math.PI) sweep += 2 * Math.PI;
                const double radius = 22;
                var arc = new Polyline { StrokeThickness = 1.5 };
                arc.SetResourceReference(Shape.StrokeProperty, "StatusWarningFg");
                for (int k = 0; k <= 12; k++)
                {
                    double t = a1 + sweep * k / 12;
                    arc.Points.Add(new Point(a.X + radius * Math.Cos(t), a.Y + radius * Math.Sin(t)));
                }
                Overlay.Children.Add(arc);

                double mid = a1 + sweep / 2;
                var label = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), BorderThickness = new Thickness(1) };
                label.SetResourceReference(Border.BackgroundProperty, "PopupBg");
                label.SetResourceReference(Border.BorderBrushProperty, "StatusWarningFg");
                var text = new TextBlock { Text = Degrees(b.Angle), FontSize = 12, FontWeight = FontWeights.SemiBold };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                label.Child = text;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, a.X + (radius + 16) * Math.Cos(mid) - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, a.Y + (radius + 16) * Math.Sin(mid) - label.DesiredSize.Height / 2);
                Overlay.Children.Add(label);

                // how far the cut goes into face A
                DrawDimension(new DimensionLine(edge, onA, Vec3.Zero, b.Depth), w, h, gapLine: true);
            }
        }
    }
}
