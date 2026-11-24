using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Smart_Tools.WPF.Views
{
    // The slider rows of the SIZE card. They are made from the template's ParamSpec list, so a new template needs no XAML:
    // each row is a label, a box to type the number, and a slider; the two stay in step and both feed SetParam.
    public partial class DimensionModelerView
    {
        private readonly Dictionary<string, Slider> _sliders = new();
        private readonly Dictionary<string, TextBox> _boxes = new();
        private bool _syncing;

        private void BuildParamRows()
        {
            PanelParams.Children.Clear();
            _sliders.Clear();
            _boxes.Clear();
            var values = _template.Resolve(CurrentValues());
            foreach (var spec in _template.Params)
                PanelParams.Children.Add(MakeRow(spec, values[spec.Key]));
        }

        private UIElement MakeRow(ParamSpec spec, double value)
        {
            var label = new TextBlock
            {
                Text = Loc.T($"Str_Modeler_Param_{_template.Id}_{spec.Key}", spec.Label),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");

            var box = new TextBox { Text = NumberInput.Format(value), TextAlignment = TextAlignment.Right, Tag = spec };
            box.SetResourceReference(FrameworkElement.StyleProperty, "BareTextBox");
            box.LostFocus += (_, _) => CommitBox(spec);
            box.KeyDown += (_, e) => BoxKeyDown(spec, e);
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
            SetParam(spec.Key, snapped);
        }

        private void BoxKeyDown(ParamSpec spec, KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            CommitBox(spec);
            e.Handled = true;
        }

        private void CommitBox(ParamSpec spec)
        {
            var box = _boxes[spec.Key];
            double shown = _template.Resolve(CurrentValues())[spec.Key];
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
            if (Math.Abs(typed - shown) > 1e-9) SetParam(spec.Key, typed);
        }

        private void SetParam(string key, double value)
        {
            CurrentValues()[key] = value;
            Rebuild();
            ScheduleSave();
        }
    }
}
