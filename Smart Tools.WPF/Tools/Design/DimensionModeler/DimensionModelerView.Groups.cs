using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Smart_Tools.WPF.Views
{
    // Groups: boards that belong together (the front and box of a drawer) carry the same Part.Group. A group can be pulled out to see and
    // measure it - in the VIEW only: the piece itself, the cut list and the exports never change. Dragging a board of a group in the custom
    // piece moves the whole group.
    public partial class DimensionModelerView
    {
        private readonly Dictionary<string, double> _pulled = new();   // group -> how far it is pulled out (mm, towards the front)

        /// <summary>The boards as they are shown: pulled-out groups moved forward.</summary>
        private IReadOnlyList<Part> Shown() =>
            _pulled.Count == 0 ? _model.Parts
                : _model.Parts.Select(p => p.Group != null && _pulled.TryGetValue(p.Group, out double d) ? p with { Min = p.Min + new Vec3(0, 0, d) } : p).ToList();

        private IReadOnlyList<string> Groups() => _model.Parts.Where(p => p.Group != null).Select(p => p.Group!).Distinct().ToList();

        /// <summary>Right out: as far as the group reaches from front to back (a drawer comes out with its whole box).</summary>
        private double PullDistance(string group)
        {
            var parts = _model.Parts.Where(p => p.Group == group).ToList();
            return parts.Count == 0 ? 0 : parts.Max(p => p.Max.Z) - parts.Min(p => p.Min.Z);
        }

        /// <summary>One "pull out" chip per group, under the cut list.</summary>
        private void RefreshGroups()
        {
            foreach (var gone in _pulled.Keys.Where(g => !Groups().Contains(g)).ToList()) _pulled.Remove(gone);
            PanelGroups.Children.Clear();
            var groups = Groups();
            PanelGroups.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (string group in groups)
            {
                var chip = new CheckBox
                {
                    Content = Loc.F("Str_Modeler_PullOut", "Pull out {0}", group),
                    IsChecked = _pulled.ContainsKey(group),
                    Margin = new Thickness(0, 0, 6, 6),
                    Tag = group,
                    ToolTip = Loc.T("Str_Modeler_PullOutTip", "Slide the group out of the piece to see and measure it. Only the view changes; the piece and its files stay as they are."),
                };
                chip.SetResourceReference(StyleProperty, "ToggleChipCheckBoxStyle");
                chip.Click += (_, _) => SetPulled(group, chip.IsChecked == true);
                PanelGroups.Children.Add(chip);
            }
        }

        private void SetPulled(string group, bool pulled)
        {
            if (pulled) _pulled[group] = PullDistance(group);
            else _pulled.Remove(group);
            RefreshGroups();
            RebuildScene();
            RedrawOverlay();
        }

        /// <summary>Back in, before the custom piece is edited (a pulled-out drawer is not where the piece has it).</summary>
        private bool PushAllBack()
        {
            if (_pulled.Count == 0) return false;
            _pulled.Clear();
            RefreshGroups();
            RebuildScene();
            RedrawOverlay();
            TxtStatus.Text = Loc.T("Str_Modeler_PushedBack", "The pulled-out groups went back in, so the board you click is where the piece has it. Click again to edit.");
            return true;
        }

        /// <summary>For each pulled-out group: its own width, height and depth, and how far it sticks out.</summary>
        private void DrawPulledGroups(double w, double h)
        {
            var shown = Shown();
            foreach (var (group, distance) in _pulled)
            {
                var parts = shown.Where(p => p.Group == group).ToList();
                if (parts.Count == 0) continue;
                var min = new Vec3(parts.Min(p => p.Min.X), parts.Min(p => p.Min.Y), parts.Min(p => p.Min.Z));
                var max = new Vec3(parts.Max(p => p.Max.X), parts.Max(p => p.Max.Y), parts.Max(p => p.Max.Z));
                var box = new Part(group, min, max - min, PartKind.Board);
                double reach = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
                foreach (var line in DimensionLines.ForPart(box, Math.Clamp(reach * 0.12, 25, 120))) DrawDimension(line, w, h);

                // how far it came out: from where its front was to where it is now, along its right-hand bottom edge
                var from = new Vec3(max.X, min.Y, max.Z - distance);
                var to = new Vec3(max.X, min.Y, max.Z);
                DrawDimension(new DimensionLine(from, to, new Vec3(Math.Clamp(reach * 0.2, 40, 160), 0, 0), distance), w, h, gapLine: true);
            }
        }
    }
}
