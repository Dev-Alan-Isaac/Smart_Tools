using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace Smart_Tools.WPF.Views
{
    // Colours: right-click a board in the 3D view to colour that board, all of its group, or only the face that was clicked. The colours
    // live beside the piece (settings, per piece id and board name), not in it, so presets keep them when their sliders move. They are
    // view-only: exports (STL has no colour) and the cut list do not change.
    public partial class DimensionModelerView
    {
        private string PaintKey => _isCustom ? CustomId : _template.Id;

        /// <summary>The colours of the piece on screen (null when none were chosen).</summary>
        private Dictionary<string, BoardPaint>? CurrentPaint()
        {
            if (!_settings.Paint.TryGetValue(PaintKey, out var paints)) return null;
            PiecePaint.Sanitize(paints);
            return paints;
        }

        private Dictionary<string, BoardPaint> EditPaint()
        {
            if (!_settings.Paint.TryGetValue(PaintKey, out var paints)) _settings.Paint[PaintKey] = paints = new Dictionary<string, BoardPaint>();
            return paints;
        }

        private void PaintChanged()
        {
            if (_settings.Paint.TryGetValue(PaintKey, out var paints) && paints.Count == 0) _settings.Paint.Remove(PaintKey);
            ScheduleSave();
            RebuildScene();
        }

        /// <summary>The colour a board's face is drawn in: picked = the selection colour, else its own, else the wood of its kind.</summary>
        private Color FaceColour(Part part, int? face, Dictionary<string, BoardPaint>? paints)
        {
            if (_selected.Contains(part.Name)) return SelectedColor;
            return PiecePaint.TryParse(PiecePaint.ColourOf(paints, part.Name, face), out byte r, out byte g, out byte b) ? Color.FromRgb(r, g, b) : Wood[part.Kind];
        }

        /// <summary>The colour items of the right-click menu for the board under the mouse and the face that was clicked.</summary>
        private void AddPaintItems(ContextMenu menu, Part part, int face)
        {
            string name = part.Name;
            menu.Items.Add(ColourMenu(Loc.F("Str_Modeler_PaintBoard", "Colour of {0}", name),
                hex => PiecePaint.PaintBoard(EditPaint(), name, hex)));
            if (part.Group is { } group)
                menu.Items.Add(ColourMenu(Loc.F("Str_Modeler_PaintGroup", "Colour of all of {0}", group), hex =>
                {
                    var paints = EditPaint();
                    foreach (var member in _model.Parts.Where(p => p.Group == group)) PiecePaint.PaintBoard(paints, member.Name, hex);
                }));
            menu.Items.Add(ColourMenu(Loc.F("Str_Modeler_PaintFace", "Colour of this face only ({0})", FaceName(face)),
                hex => PiecePaint.PaintFace(EditPaint(), name, face, hex), resetKey: "Str_Modeler_PaintFaceReset"));
            if (CurrentPaint() is { Count: > 0 })
            {
                var clear = new MenuItem { Header = Loc.T("Str_Modeler_PaintClearAll", "Remove every colour from this piece") };
                clear.Click += (_, _) =>
                {
                    _settings.Paint.Remove(PaintKey);
                    PaintChanged();
                };
                menu.Items.Add(clear);
            }
            menu.Items.Add(new Separator());
        }

        /// <summary>A submenu of ready-made colours, "Other colour..." and the way back to wood.</summary>
        private MenuItem ColourMenu(string header, Action<string?> apply, string resetKey = "Str_Modeler_PaintReset")
        {
            var root = new MenuItem { Header = header, Icon = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.FormatColorFill } };
            foreach (var swatch in PiecePaint.Swatches)
            {
                var item = new MenuItem { Header = Loc.T($"Str_Modeler_Colour_{swatch.Id}", swatch.Id), Icon = Chip(swatch.Hex) };
                string hex = swatch.Hex;
                item.Click += (_, _) => { apply(hex); PaintChanged(); };
                root.Items.Add(item);
            }
            root.Items.Add(new Separator());
            var other = new MenuItem { Header = Loc.T("Str_Modeler_PaintOther", "Other colour...") };
            other.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                apply(PiecePaint.Hex(dialog.Color.R, dialog.Color.G, dialog.Color.B));
                PaintChanged();
            };
            root.Items.Add(other);
            var reset = new MenuItem { Header = Loc.T(resetKey, "Back to wood") };
            reset.Click += (_, _) => { apply(null); PaintChanged(); };
            root.Items.Add(reset);
            return root;
        }

        /// <summary>A small square of the colour for a menu line (the colour is the piece's data, not a theme colour).</summary>
        private static Border Chip(string hex)
        {
            PiecePaint.TryParse(hex, out byte r, out byte g, out byte b);
            var chip = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(r, g, b)) };
            chip.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            return chip;
        }
    }
}
