using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    // Round holes through the picked board of a custom piece (a hole for a door knob, a cable hole...). The rules - keeping a hole
    // inside the board and apart from the others - are PieceHoles.Sanitize; the shape of a board with holes is made in
    // PieceHoles.Triangles, so the 3D view and the exports show the same thing. This file reads the boxes and shows the result.
    public partial class DimensionModelerView
    {
        private int _holeIndex;             // the hole shown in the boxes
        private bool _fillingHoles;         // the boxes are being filled: their events are not the user's

        /// <summary>One step of an Up / Down nudge in millimetres: 10 (Shift 1, Ctrl 100), and 0.5 in (Shift 0.1, Ctrl 5) in inches.</summary>
        private double NudgeStep()
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 100 : 10;
            return _settings.Unit == LengthUnit.Inches ? step * 1.27 : step;
        }

        private void FillHoles(Part part)
        {
            _fillingHoles = true;
            try
            {
                var holes = part.Holes;
                bool any = holes.Count > 0;
                TxtNoHoles.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                PanelHole.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                BtnDeleteHole.IsEnabled = any;
                BtnAddHole.IsEnabled = holes.Count < PieceHoles.MaxHoles;
                ComboHole.Items.Clear();
                for (int n = 0; n < holes.Count; n++) ComboHole.Items.Add(Loc.F("Str_Modeler_HoleN", "Hole {0}", n + 1));
                _holeIndex = Math.Clamp(_holeIndex, 0, Math.Max(0, holes.Count - 1));
                ComboHole.SelectedIndex = any ? _holeIndex : -1;
                if (!any) return;

                var hole = holes[_holeIndex];
                var (a1, a2) = PieceHoles.Across(hole.Axis);
                TxtHoleU.Text = Num(hole.U);
                TxtHoleV.Text = Num(hole.V);
                TxtHoleDia.Text = Num(hole.Diameter);
                CapHoleU.Text = "XYZ"[a1].ToString();
                CapHoleV.Text = "XYZ"[a2].ToString();
            }
            finally
            {
                _fillingHoles = false;
            }
        }

        private void ComboHole_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingHoles || !_viewConstructed || ComboHole.SelectedIndex < 0) return;
            _holeIndex = ComboHole.SelectedIndex;
            int i = ListParts.SelectedIndex;
            if (i >= 0 && i < _customParts.Count) FillHoles(_customParts[i]);
        }

        private void BtnAddHole_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0 || i >= _customParts.Count) return;
            var part = _customParts[i];
            // through the thin side (a door's thickness) unless the board already has holes, which all go the same way
            int axis = part.Holes.Count > 0 ? part.Holes[0].Axis : PieceHoles.ThinAxis(part.Size);
            var (a1, a2) = PieceHoles.Across(axis);
            double l1 = Along(part.Size, a1), l2 = Along(part.Size, a2);
            double diameter = Math.Min(20, Math.Min(l1, l2) / 2);

            // the middle of the face, or the first free spot around it
            foreach (var (fu, fv) in new[] { (0.5, 0.5), (0.25, 0.5), (0.75, 0.5), (0.5, 0.25), (0.5, 0.75), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
            {
                var holes = part.Holes.Append(new Hole(axis, l1 * fu, l2 * fv, diameter)).ToList();
                if (PieceHoles.Sanitize(part.Size, holes).Count != part.Holes.Count + 1) continue;
                _holeIndex = part.Holes.Count;
                ChangeCustom(PieceEditor.Replace(_customParts, i, part with { Cuts = holes }));
                CustomChanged(i);
                return;
            }
            TxtStatus.Text = Loc.T("Str_Modeler_NoRoomForHole", "There is no room for another hole on this board.");
        }

        private void BtnDeleteHole_Click(object sender, RoutedEventArgs e)
        {
            int i = ListParts.SelectedIndex;
            if (i < 0 || i >= _customParts.Count || _holeIndex < 0 || _holeIndex >= _customParts[i].Holes.Count) return;
            var part = _customParts[i];
            var holes = part.Holes.Where((_, n) => n != _holeIndex).ToList();
            ChangeCustom(PieceEditor.Replace(_customParts, i, part with { Cuts = holes }));
            CustomChanged(i);
        }

        /// <summary>A typed change to the picked hole: read the boxes, make the hole safe, and rebuild.</summary>
        private void ApplyHoleEditor()
        {
            int i = ListParts.SelectedIndex;
            if (_fillingHoles || i < 0 || i >= _customParts.Count) return;
            var part = _customParts[i];
            if (_holeIndex < 0 || _holeIndex >= part.Holes.Count) return;
            var old = part.Holes[_holeIndex];
            double V(TextBox box, double fallback) =>
                box.Text.Trim() == Num(fallback) ? fallback
                : LengthFormat.TryParseMm(box.Text, _settings.Unit, out double mm) ? mm : fallback;

            var holes = part.Holes.ToList();
            holes[_holeIndex] = new Hole(old.Axis, V(TxtHoleU, old.U), V(TxtHoleV, old.V), V(TxtHoleDia, old.Diameter));
            var result = PieceEditor.Replace(_customParts, i, part with { Cuts = holes });
            if (result[i] == part)
            {
                FillHoles(part);   // nothing changed: a bad number goes back to the real one
                return;
            }
            ChangeCustom(result);
            CustomChanged(i);
        }

        private void HoleBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) ApplyHoleEditor();
        }

        private void HoleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyHoleEditor();
                e.Handled = true;
            }
            else if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down && sender is TextBox box)
            {
                if (LengthFormat.TryParseMm(box.Text, _settings.Unit, out double now))
                {
                    box.Text = Num(now + (e.Key == System.Windows.Input.Key.Up ? NudgeStep() : -NudgeStep()));
                    ApplyHoleEditor();
                }
                e.Handled = true;
            }
        }
    }
}
