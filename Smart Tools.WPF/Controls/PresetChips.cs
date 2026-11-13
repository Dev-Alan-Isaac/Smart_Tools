using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The one "quick pick" row for the options sidebar: equal-width <c>PresetChipStyle</c> buttons in a grid (the look of the
    /// Barcode Generator's size presets). Declare the choices in <see cref="Items"/> as <c>"label:value;label:value"</c>
    /// (a bare <c>"16;32"</c> uses the text as both) and handle <see cref="PresetChosen"/> with the value.
    /// </summary>
    public class PresetChips : ContentControl
    {
        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(nameof(Items), typeof(string), typeof(PresetChips),
                new PropertyMetadata("", (d, _) => ((PresetChips)d).Rebuild()));

        public static readonly DependencyProperty ColumnsProperty =
            DependencyProperty.Register(nameof(Columns), typeof(int), typeof(PresetChips),
                new PropertyMetadata(3, (d, _) => ((PresetChips)d).Rebuild()));

        private readonly UniformGrid _grid = new();

        public PresetChips()
        {
            Focusable = false;
            Margin = new Thickness(0, 8, 0, 0);
            Content = _grid;
        }

        /// <summary>The choices: <c>"label:value;label:value"</c>, or <c>"16;32;64"</c> when label and value are the same.</summary>
        public string Items { get => (string)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

        /// <summary>Chips per row (3 by default).</summary>
        public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

        /// <summary>A chip was clicked; the argument is its value.</summary>
        public event EventHandler<string>? PresetChosen;

        private void Rebuild()
        {
            _grid.Children.Clear();
            _grid.Columns = Math.Max(1, Columns);
            var entries = (Items ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                int cut = entries[i].LastIndexOf(':');
                string label = cut > 0 ? entries[i][..cut] : entries[i];
                string value = cut > 0 ? entries[i][(cut + 1)..] : entries[i];
                var button = new Button { Content = label, Tag = value, ToolTip = label };
                button.SetResourceReference(StyleProperty, "PresetChipStyle");
                // Chips sit side by side with a small gap, like the Barcode size row
                button.Margin = new Thickness(i % _grid.Columns == 0 ? 0 : 2, 0, i % _grid.Columns == _grid.Columns - 1 ? 0 : 2, 4);
                button.Click += (_, _) => PresetChosen?.Invoke(this, value);
                _grid.Children.Add(button);
            }
        }
    }
}
