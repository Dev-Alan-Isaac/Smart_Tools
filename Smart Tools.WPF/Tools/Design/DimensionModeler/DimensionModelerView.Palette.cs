using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>One entry of the Examples dropdown.</summary>
    public sealed record ExampleChoice(string Id, string Name, PackIconKind Icon);

    // The sidebar's Shapes and Examples cards. A shape tile adds that shape in the middle of the view, standing on the floor
    // (the hole row adds the same shapes as holes). An example is set with its sliders and inserted as ordinary shapes, once.
    public partial class DimensionModelerView
    {
        public static readonly IReadOnlyList<(ShapeKind Kind, PackIconKind Icon)> ShapeTiles = new[]
        {
            (ShapeKind.Box, PackIconKind.CubeOutline),
            (ShapeKind.Cylinder, PackIconKind.Cylinder),
            (ShapeKind.Sphere, PackIconKind.Sphere),
            (ShapeKind.Cone, PackIconKind.Cone),
            (ShapeKind.Wedge, PackIconKind.SlopeUphill),
            (ShapeKind.Tube, PackIconKind.CircleDouble),
        };

        private static readonly Dictionary<string, PackIconKind> ExampleIcons = new()
        {
            ["bracket"] = PackIconKind.SetSquare,
            ["box-lid"] = PackIconKind.PackageVariantClosed,
            ["phone-stand"] = PackIconKind.Cellphone,
            ["table"] = PackIconKind.TableFurniture,
            ["chair"] = PackIconKind.SeatOutline,
            ["bookshelf"] = PackIconKind.Bookshelf,
            ["cabinet"] = PackIconKind.WardrobeOutline,
            ["filing-cabinet"] = PackIconKind.FileCabinet,
            ["door"] = PackIconKind.DoorClosed,
            ["desk"] = PackIconKind.Desk,
            ["desk-l"] = PackIconKind.DeskLamp,
            ["shelf"] = PackIconKind.ViewAgendaOutline,
            ["kitchen-base"] = PackIconKind.CountertopOutline,
            ["wall-cabinet"] = PackIconKind.CupboardOutline,
            ["wardrobe"] = PackIconKind.Wardrobe,
            ["sideboard"] = PackIconKind.DresserOutline,
        };

        private readonly Dictionary<string, Slider> _sliders = new();
        private readonly Dictionary<string, TextBox> _boxes = new();
        private SceneExample _example = SceneExamples.All[0];
        private bool _syncing;
        private bool _loadingExample;

        private void InitPalette()
        {
            foreach (var (kind, icon) in ShapeTiles)
            {
                PanelShapes.Children.Add(ShapeTile(kind, icon, hole: false));
                PanelHoleShapes.Children.Add(ShapeTile(kind, icon, hole: true));
            }
            ComboExample.ItemsSource = SceneExamples.All
                .Select(e => new ExampleChoice(e.Id, Loc.T($"Str_Modeler_Template_{e.Id}", e.Name), ExampleIcons.TryGetValue(e.Id, out var i) ? i : PackIconKind.CubeOutline))
                .ToList();
            foreach (double mm in new[] { 0, 1, 5, 10, 25, 50 })
                ComboGrid.Items.Add(new ComboBoxItem { Content = mm == 0 ? Loc.T("Str_Modeler_GridOff", "No grid") : $"{mm} mm", Tag = mm });
        }

        private static string KindName(ShapeKind kind) => Loc.T($"Str_Modeler_Shape_{kind}", kind.ToString());

        private Button ShapeTile(ShapeKind kind, PackIconKind icon, bool hole)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var glyph = new PackIcon { Kind = icon, Width = 22, Height = 22, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            if (hole) glyph.Opacity = 0.55;
            stack.Children.Add(glyph);
            stack.Children.Add(new TextBlock
            {
                Text = KindName(kind),
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var tile = new Button
            {
                Content = stack,
                Height = 58,
                Margin = new Thickness(2),
                Padding = new Thickness(2),
                ToolTip = hole ? Loc.F("Str_Modeler_AddHoleShapeTip", "Add a {0} hole: group it with a shape to cut that shape", KindName(kind).ToLowerInvariant())
                               : Loc.F("Str_Modeler_AddShapeTip", "Add a {0}", KindName(kind).ToLowerInvariant()),
                Tag = (kind, hole),
            };
            tile.SetResourceReference(FrameworkElement.StyleProperty, "NeutralIconButtonStyle");
            if (hole) tile.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "TextSecondary");
            tile.Click += (_, _) => AddShape(kind, hole);
            return tile;
        }

        /// <summary>A new shape in the middle of the view, standing on the floor, sized to fit what is already there; it becomes the pick.</summary>
        private void AddShape(ShapeKind kind, bool hole)
        {
            var b = SolidBounds();
            double side = b.IsEmpty ? 100 : Math.Clamp(Math.Round(Math.Max(b.Size.X, Math.Max(b.Size.Y, b.Size.Z)) * 0.15 / 10) * 10, 20, 400);
            var size = new Vec3(side, side, side);
            double grid = _settings.SnapGridMm > 0 ? _settings.SnapGridMm : 1;
            var target = _camera.Target;
            var centre = new Vec3(Math.Round(target.X / grid) * grid, size.Y / 2, Math.Round(target.Z / grid) * grid);
            string name = hole ? Loc.F("Str_Modeler_HoleName", "{0} hole", KindName(kind)) : KindName(kind);
            var shape = new SceneShape("", name, kind, centre, size, Vec3.Zero, IsHole: hole);
            bool wasEmpty = _scene.Nodes.Count == 0;
            var next = SceneEdit.Add(_scene, shape, out string id);
            if (id.Length == 0)
            {
                TxtStatus.Text = Loc.F("Str_Modeler_TooMany", "A design can have at most {0} shapes.", SceneEdit.MaxShapes);
                return;
            }
            Change(next, hole
                ? Loc.T("Str_Modeler_AddedHole", "Hole added. Pick it and a shape (Ctrl+click), then Group (Ctrl+G) to cut the shape.")
                : Loc.T("Str_Modeler_Added", "Added. Type its size and position under Properties."), fit: wasEmpty);
            Pick(new[] { id });
        }

        // =================================================================
        //  Examples
        // =================================================================

        private void ShowExampleChoice()
        {
            _example = SceneExamples.Find(_settings.ExampleId) ?? SceneExamples.All[0];
            _loadingExample = true;
            ComboExample.SelectedValue = _example.Id;
            _loadingExample = false;
            BuildParamRows();
        }

        private void ComboExample_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _loadingExample || ComboExample.SelectedValue is not string id || SceneExamples.Find(id) is not { } example) return;
            _example = example;
            _settings.ExampleId = id;
            BuildParamRows();
            ScheduleSave();
        }

        private Dictionary<string, double> ExampleValues()
        {
            if (!_settings.Values.TryGetValue(_example.Id, out var values))
                _settings.Values[_example.Id] = values = new Dictionary<string, double>();
            return values;
        }

        private void BtnInsertExample_Click(object sender, RoutedEventArgs e)
        {
            string name = Loc.T($"Str_Modeler_Template_{_example.Id}", _example.Name);
            var node = _example.Build(ExampleValues(), name);
            var ids = AddBeside(new[] { node });
            if (ids.Count == 0) return;
            if (_scene.Nodes.Count == 1) Change(_scene with { Name = name });   // a new design is named after its first example
            TxtStatus.Text = Loc.F("Str_Modeler_Inserted", "{0} inserted as one group: Ungroup (Ctrl+Shift+G) to change its parts.", name);
            Pick(ids);
        }

        private void BtnResetSizes_Click(object sender, RoutedEventArgs e)
        {
            _settings.Values.Remove(_example.Id);
            BuildParamRows();
            ScheduleSave();
        }

        private void BuildParamRows()
        {
            PanelParams.Children.Clear();
            _sliders.Clear();
            _boxes.Clear();
            var values = _example.Resolve(ExampleValues());
            foreach (var spec in _example.Params)
                PanelParams.Children.Add(MakeRow(spec, values[spec.Key]));
        }

        private UIElement MakeRow(ParamSpec spec, double value)
        {
            var label = new TextBlock
            {
                Text = Loc.T($"Str_Modeler_Param_{_example.Id}_{spec.Key}", spec.Label),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");

            var box = new TextBox { Text = NumberInput.Format(value), TextAlignment = TextAlignment.Right };
            box.SetResourceReference(FrameworkElement.StyleProperty, "BareTextBox");
            box.LostFocus += (_, _) => CommitParamBox(spec);
            box.KeyDown += (_, e) => ParamBoxKeyDown(spec, e);
            var boxBar = new Border { Width = 84, Child = box };
            boxBar.SetResourceReference(FrameworkElement.StyleProperty, "InputBarStyle");

            var slider = new Slider
            {
                Minimum = spec.Min,
                Maximum = spec.Max,
                Value = value,
                SmallChange = spec.Step,
                LargeChange = spec.Step * 5,
                Margin = new Thickness(0, 4, 0, 0),
            };
            slider.ValueChanged += (_, e) => SliderMoved(spec, e.NewValue);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(label);
            Grid.SetColumn(boxBar, 1);
            head.Children.Add(boxBar);

            _sliders[spec.Key] = slider;
            _boxes[spec.Key] = box;
            var row = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(head);
            row.Children.Add(slider);
            return row;
        }

        /// <summary>Dragging snaps to the slider's step; the typed box keeps any value you type (inside the range).</summary>
        private void SliderMoved(ParamSpec spec, double raw)
        {
            if (_syncing) return;
            double snapped = Math.Clamp(spec.Min + Math.Round((raw - spec.Min) / spec.Step) * spec.Step, spec.Min, spec.Max);
            if (Math.Abs(snapped - raw) > 1e-9)
            {
                _syncing = true;
                _sliders[spec.Key].Value = snapped;
                _syncing = false;
            }
            _boxes[spec.Key].Text = NumberInput.Format(snapped);
            ExampleValues()[spec.Key] = snapped;
            ScheduleSave();
        }

        private void ParamBoxKeyDown(ParamSpec spec, KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            CommitParamBox(spec);
            e.Handled = true;
        }

        private void CommitParamBox(ParamSpec spec)
        {
            var box = _boxes[spec.Key];
            double shown = _example.Resolve(ExampleValues())[spec.Key];
            if (!NumberInput.TryParse(box.Text, out double typed))
            {
                box.Text = NumberInput.Format(shown);   // not a number: put back what is in use
                return;
            }
            if (spec.Integer) typed = Math.Round(typed, MidpointRounding.AwayFromZero);
            typed = Math.Clamp(typed, spec.Min, spec.Max);
            _syncing = true;
            _sliders[spec.Key].Value = typed;
            _syncing = false;
            box.Text = NumberInput.Format(typed);
            ExampleValues()[spec.Key] = typed;
            ScheduleSave();
        }
    }
}
