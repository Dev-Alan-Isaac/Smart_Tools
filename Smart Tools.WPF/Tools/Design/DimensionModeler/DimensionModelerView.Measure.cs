using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Views
{
    // Picking one part inside a group, and what the measuring lines show. A click picks the whole object (a group is one object);
    // a second click on an object that is already picked picks the single part under the mouse, which can then be moved, resized,
    // measured and changed on its own. When the pick is a part of a group, a small switch over the view chooses what is measured:
    // the part, its whole group, or both.
    public partial class DimensionModelerView
    {
        /// <summary>True when the picked node is inside a group (not a top-level object).</summary>
        private bool IsPart(string id) => SceneEdit.TopOf(_scene, id) is { } top && top.Id != id;

        /// <summary>
        /// A plain click on an object that is already picked: picks the part under the mouse instead (when the object is a group).
        /// Returns true when it did.
        /// </summary>
        private bool TryPickPart(Point at, string topId)
        {
            if (!PickedObjects().Contains(topId) || SceneEdit.Find(_scene, topId) is not GroupNode top) return false;
            double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
            if (ScenePick.PickShape(_scene, top, _camera.Ray(at.X, at.Y, w, h)) is not { } hit) return false;
            if (_picked.Count == 1 && _picked[0] == hit.NodeId) return true;   // already that part
            Pick(new[] { hit.NodeId });
            var name = SceneEdit.Find(_scene, hit.NodeId)?.Name ?? "";
            TxtStatus.Text = Loc.F("Str_Modeler_PartPicked", "Picked {0} inside {1}. Drag, resize or change it on its own; click empty space to let go.", name, top.Name);
            return true;
        }

        /// <summary>Picked parts of groups are drawn on their own, see-through over the joined object, so they show (holes too).</summary>
        private void AddPickedParts(Model3DGroup group)
        {
            foreach (var id in _picked)
            {
                if (!IsPart(id) || SceneEdit.Find(_scene, id) is not { } node) continue;
                var triangles = SceneEdit.Shapes(node).Where(s => !s.Hidden).SelectMany(s => ScenePick.ShapeTriangles(_scene, s));
                group.Children.Add(Model(triangles, HoleGhostPicked));
            }
        }

        private void ShowMeasureChoice()
        {
            string mode = _settings.MeasureMode is "part" or "group" ? _settings.MeasureMode : "both";
            RadioMeasurePart.IsChecked = mode == "part";
            RadioMeasureGroup.IsChecked = mode == "group";
            RadioMeasureBoth.IsChecked = mode == "both";
        }

        private void MeasureMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string mode }) return;
            _settings.MeasureMode = mode;
            ScheduleSave();
            RedrawOverlay();
        }

        /// <summary>
        /// The boxes to measure: with no pick, the whole design; a picked part of a group by the chosen mode (the part, its group, or
        /// both, the part's lines drawn nearer); anything else as picked. The switch shows only when a part of a group is picked.
        /// </summary>
        private List<(Bounds3 Box, bool Outer)> MeasuredBoxes()
        {
            var result = new List<(Bounds3, bool)>();
            bool anyPart = _picked.Any(IsPart);
            MeasureSwitch.Visibility = anyPart && _settings.ShowDimensions && _drag == null ? Visibility.Visible : Visibility.Collapsed;
            if (_picked.Count == 0)
            {
                var all = SolidBounds();
                if (!all.IsEmpty) result.Add((all, true));
                return result;
            }
            var picked = SceneEdit.BoundsOf(_scene, _picked);
            if (!anyPart)
            {
                if (!picked.IsEmpty) result.Add((picked, false));
                return result;
            }
            var groups = SceneEdit.BoundsOf(_scene, _picked.Select(id => SceneEdit.TopOf(_scene, id)?.Id ?? id).Distinct());
            string mode = _settings.MeasureMode;
            if (mode != "group" && !picked.IsEmpty) result.Add((picked, false));
            if (mode != "part" && !groups.IsEmpty) result.Add((groups, true));
            return result;
        }
    }
}
