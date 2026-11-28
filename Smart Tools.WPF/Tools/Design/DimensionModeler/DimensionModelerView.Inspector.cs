using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    // The Properties tab: what the picked shapes are, where they are and how big, typed in the unit chosen under View.
    // A box shows the value in use; Enter or leaving the box applies it (one undo step), Esc puts the shown value back, and
    // Up / Down change it by a step (Shift smaller, Ctrl bigger). A position is the lowest corner of the box around the pick.
    public partial class DimensionModelerView
    {
        private readonly Dictionary<string, string> _shownField = new();   // box tag -> the text it was filled with
        private bool _fillingInspector;

        private void InitInspector()
        {
            foreach (var (kind, _) in ShapeTiles)
                ComboKind.Items.Add(new ComboBoxItem { Content = KindName(kind), Tag = kind });

            foreach (var swatch in PiecePaint.Swatches)
                PanelSwatches.Children.Add(Swatch(swatch.Hex, Loc.T($"Str_Modeler_Colour_{swatch.Id}", swatch.Id)));
            PanelSwatches.Children.Add(SwatchButton(PackIconKind.FormatColorReset, Loc.T("Str_Modeler_PaintNone", "No colour (the standard look)"), () => SetPickedColour(null)));
            PanelSwatches.Children.Add(SwatchButton(PackIconKind.PaletteOutline, Loc.T("Str_Modeler_PaintOther", "Other colour..."), PickOtherColour));
        }

        private Button Swatch(string hex, string tip)
        {
            PiecePaint.TryParse(hex, out byte r, out byte g, out byte b);
            // the swatch shows the colour itself: design data, not a theme colour
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromRgb(r, g, b)), BorderThickness = new Thickness(1) };
            dot.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var button = new Button { Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), Content = dot, ToolTip = tip };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ListToolButtonStyle");
            button.Click += (_, _) => SetPickedColour(hex);
            return button;
        }

        private Button SwatchButton(PackIconKind icon, string tip, Action click)
        {
            var button = new Button { Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), Content = new PackIcon { Kind = icon, Width = 16, Height = 16 }, ToolTip = tip };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ListToolButtonStyle");
            button.Click += (_, _) => click();
            return button;
        }

        private void SetPickedColour(string? hex)
        {
            if (_picked.Count == 0) return;
            Change(SceneEdit.SetColour(_scene, _picked, hex));
        }

        private void PickOtherColour()
        {
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            SetPickedColour(PiecePaint.Hex(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        }

        // =================================================================
        //  Showing the pick
        // =================================================================

        private List<SceneNode> PickedNodes() => _picked.Select(id => SceneEdit.Find(_scene, id)).OfType<SceneNode>().ToList();

        private void RefreshInspector()
        {
            var nodes = PickedNodes();
            TxtNoPick.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelPick.Visibility = nodes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (nodes.Count == 0) return;

            _fillingInspector = true;
            try
            {
                _shownField.Clear();
                bool single = nodes.Count == 1;
                var shape = single ? nodes[0] as SceneShape : null;
                var unit = _settings.Unit;
                string suffix = LengthFormat.Suffix(unit);

                TxtPickTitle.Text = single ? nodes[0].Name : Loc.F("Str_Modeler_PickedMany", "{0} picked", nodes.Count);
                IconPick.Kind = !single ? PackIconKind.SelectAll
                    : shape == null ? PackIconKind.Group
                    : shape.Kind == ShapeKind.Mesh ? PackIconKind.VectorTriangle
                    : ShapeTiles.First(t => t.Kind == shape.Kind).Icon;

                PanelName.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
                Fill(TxtName, "name", single ? nodes[0].Name : "");

                PanelKind.Visibility = shape != null && shape.Kind != ShapeKind.Mesh ? Visibility.Visible : Visibility.Collapsed;
                if (shape != null) ComboKind.SelectedItem = ComboKind.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ShapeKind k && k == shape.Kind);

                var shapes = nodes.SelectMany(SceneEdit.Shapes).ToList();
                RadioHole.IsChecked = shapes.Count > 0 && shapes.All(s => s.IsHole);
                RadioSolid.IsChecked = shapes.Count > 0 && shapes.All(s => !s.IsHole);

                // the material the picked ones show (own or their group's); a mix of both ticks neither
                var materials = _picked.Select(id => SceneEdit.ShownMaterial(_scene, id)).Distinct().ToList();
                RadioMaterialStandard.IsChecked = materials.Count == 1 && materials[0] == null;
                RadioMaterialCrystal.IsChecked = materials.Count == 1 && materials[0] == SceneMaterial.Crystal;

                var b = SceneEdit.BoundsOf(_scene, _picked);
                TxtPosLabel.Text = Loc.F("Str_Modeler_PositionLabel", "Position, lowest corner ({0})", suffix);
                Fill(TxtPosX, "px", LengthFormat.Number(b.Min.X, unit));
                Fill(TxtPosY, "py", LengthFormat.Number(b.Min.Y, unit));
                Fill(TxtPosZ, "pz", LengthFormat.Number(b.Min.Z, unit));

                // a shape shows its own size (before it is turned); a group or several picks show the box around them
                var size = shape?.Size ?? b.Size;
                TxtSizeLabel.Text = Loc.F("Str_Modeler_SizeLabel", "Size ({0})", suffix);
                Fill(TxtSizeW, "sx", LengthFormat.Number(size.X, unit));
                Fill(TxtSizeH, "sy", LengthFormat.Number(size.Y, unit));
                Fill(TxtSizeD, "sz", LengthFormat.Number(size.Z, unit));
                foreach (var box in new[] { TxtSizeW, TxtSizeH, TxtSizeD }) box.IsEnabled = single;

                PanelRotation.Visibility = shape != null ? Visibility.Visible : Visibility.Collapsed;
                if (shape != null)
                {
                    Fill(TxtRotX, "rx", NumberInput.Format(shape.Rotation.X));
                    Fill(TxtRotY, "ry", NumberInput.Format(shape.Rotation.Y));
                    Fill(TxtRotZ, "rz", NumberInput.Format(shape.Rotation.Z));
                }

                bool round = shape?.Kind is ShapeKind.Cylinder or ShapeKind.Cone or ShapeKind.Sphere or ShapeKind.Tube;
                PanelRound.Visibility = round ? Visibility.Visible : Visibility.Collapsed;
                PanelInner.Visibility = shape?.Kind == ShapeKind.Tube ? Visibility.Visible : Visibility.Hidden;
                if (shape != null && round)
                {
                    Fill(TxtSegments, "seg", shape.Segments.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Fill(TxtInner, "inner", NumberInput.Format(Math.Round(shape.Inner * 100)));
                }
            }
            finally
            {
                _fillingInspector = false;
            }
        }

        private void Fill(TextBox box, string tag, string text)
        {
            box.Text = text;
            _shownField[tag] = text;
        }

        // =================================================================
        //  Typing
        // =================================================================

        private void Field_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { Tag: string tag } box) Commit(tag, box.Text);
        }

        private void Field_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox { Tag: string tag } box) return;
            switch (e.Key)
            {
                case Key.Enter:
                    Commit(tag, box.Text);
                    box.SelectAll();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    RefreshInspector();
                    StageHost.Focus();
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.Down:
                    if (Nudge(tag, box.Text, e.Key == Key.Up ? 1 : -1) is { } nudged)
                    {
                        box.Text = nudged;
                        Commit(tag, nudged);
                        box.CaretIndex = box.Text.Length;
                    }
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>The box's value one step up or down, as text in the box's unit, or null when the box does not hold a number.</summary>
        private string? Nudge(string tag, string text, int direction)
        {
            var mods = Keyboard.Modifiers;
            double scale = mods.HasFlag(ModifierKeys.Control) ? 10 : mods.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
            var unit = _settings.Unit;
            switch (tag)
            {
                case "px" or "py" or "pz" or "sx" or "sy" or "sz":
                    if (!LengthFormat.TryParseMm(text, unit, out double mm)) return null;
                    double step = (unit == LengthUnit.Inches ? 12.7 : 10) * scale;   // 1 cm, 10 mm or half an inch
                    double next = mm + direction * step;
                    if (tag[0] == 's') next = Math.Max(SceneEdit.MinSize, next);
                    return LengthFormat.Number(next, unit);
                case "rx" or "ry" or "rz":
                    if (!NumberInput.TryParse(text, out double deg)) return null;
                    double turn = mods.HasFlag(ModifierKeys.Control) ? 90 : mods.HasFlag(ModifierKeys.Shift) ? 1 : 15;
                    return NumberInput.Format(deg + direction * turn);
                case "seg":
                    if (!NumberInput.TryParse(text, out double seg)) return null;
                    return NumberInput.Format(Math.Clamp(Math.Round(seg) + direction * (mods.HasFlag(ModifierKeys.Control) ? 8 : 1), Primitives.MinSegments, Primitives.MaxSegments));
                case "inner":
                    if (!NumberInput.TryParse(text, out double pct)) return null;
                    return NumberInput.Format(Math.Clamp(pct + direction * (mods.HasFlag(ModifierKeys.Shift) ? 1 : 5), 5, 95));
                default:
                    return null;
            }
        }

        /// <summary>Applies a typed value to the picked shapes. A value left as shown changes nothing; a bad one puts the shown value back.</summary>
        private void Commit(string tag, string text)
        {
            if (_fillingInspector || _picked.Count == 0) return;
            if (_shownField.TryGetValue(tag, out var shown) && shown == text.Trim()) return;
            var nodes = PickedNodes();
            if (nodes.Count == 0) return;
            if (tag != "name" && nodes.Any(n => IsLocked(n.Id)))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                RefreshInspector();
                return;
            }

            var unit = _settings.Unit;
            var single = nodes.Count == 1 ? nodes[0] : null;
            var shape = single as SceneShape;
            var b = SceneEdit.BoundsOf(_scene, _picked);
            Scene? next = null;

            switch (tag)
            {
                case "name" when single != null:
                    next = SceneEdit.Rename(_scene, single.Id, text);
                    break;
                case "px" or "py" or "pz" when LengthFormat.TryParseMm(text, unit, out double at):
                    double now = tag == "px" ? b.Min.X : tag == "py" ? b.Min.Y : b.Min.Z;
                    var delta = tag == "px" ? new Vec3(at - now, 0, 0) : tag == "py" ? new Vec3(0, at - now, 0) : new Vec3(0, 0, at - now);
                    next = SceneEdit.Translate(_scene, _picked, delta);
                    break;
                case "sx" or "sy" or "sz" when single != null && LengthFormat.TryParseMm(text, unit, out double length) && length > 0:
                    length = Math.Clamp(length, SceneEdit.MinSize, SceneEdit.MaxSize);
                    if (shape != null)
                    {
                        // the shape grows from its lowest corner, so it does not sink into the floor
                        var size = tag == "sx" ? shape.Size with { X = length } : tag == "sy" ? shape.Size with { Y = length } : shape.Size with { Z = length };
                        var changed = SceneEdit.Replace(_scene, shape with { Size = size });
                        var after = SceneEdit.BoundsOf(changed, SceneEdit.Find(changed, shape.Id)!);
                        next = SceneEdit.Translate(changed, new[] { shape.Id }, b.Min - after.Min);
                    }
                    else
                    {
                        var size = tag == "sx" ? b.Size with { X = length } : tag == "sy" ? b.Size with { Y = length } : b.Size with { Z = length };
                        next = SceneEdit.Resize(_scene, single.Id, new Bounds3(b.Min, b.Min + size));
                    }
                    break;
                case "rx" or "ry" or "rz" when shape != null && NumberInput.TryParse(text, out double deg):
                    var r = tag == "rx" ? shape.Rotation with { X = deg } : tag == "ry" ? shape.Rotation with { Y = deg } : shape.Rotation with { Z = deg };
                    next = SceneEdit.Replace(_scene, shape with { Rotation = r });
                    break;
                case "seg" when shape != null && NumberInput.TryParse(text, out double seg):
                    next = SceneEdit.Replace(_scene, shape with { Segments = (int)Math.Round(seg) });
                    break;
                case "inner" when shape != null && NumberInput.TryParse(text, out double pct):
                    next = SceneEdit.Replace(_scene, shape with { Inner = pct / 100 });
                    break;
            }
            if (next == null)
            {
                RefreshInspector();   // not a number (or nothing to change): show what is in use again
                return;
            }
            Change(next);
        }

        private bool IsLocked(string id) =>
            SceneEdit.Find(_scene, id) is { Locked: true } || SceneEdit.TopOf(_scene, id) is { Locked: true };

        private void ComboKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingInspector || !_viewConstructed || ComboKind.SelectedItem is not ComboBoxItem { Tag: ShapeKind kind }) return;
            if (PickedNodes() is not [SceneShape shape] || shape.Kind == kind) return;
            if (IsLocked(shape.Id))
            {
                TxtStatus.Text = Loc.T("Str_Modeler_LockedNoEdit", "That is locked: unlock it under Objects to change it.");
                RefreshInspector();
                return;
            }
            Change(SceneEdit.Replace(_scene, shape with { Kind = kind }));
        }

        private void SolidHole_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } || _picked.Count == 0) return;
            Change(SceneEdit.SetHole(_scene, _picked, tag == "hole"));
        }

        private void Material_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag }) SetPickedMaterial(tag.Length == 0 ? null : tag);
        }

        private void SetPickedMaterial(string? material)
        {
            if (_picked.Count == 0 || _picked.Any(IsLocked)) return;
            Change(SceneEdit.SetMaterial(_scene, _picked, material));
        }
    }
}
