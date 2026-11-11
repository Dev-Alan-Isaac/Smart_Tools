using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>One program in the ranking. <see cref="ColorIndex"/> is its colour in the chart (see <see cref="ChartPalette"/>; grey beyond the heaviest few).</summary>
    public sealed class UsageRow
    {
        public int Rank { get; init; }
        public string Name { get; init; } = "";
        public string ValueText { get; init; } = "";
        public string PeakText { get; init; } = "";

        /// <summary>0-100, relative to the first row (the bar under the name).</summary>
        public double Share { get; init; }

        public int ColorIndex { get; init; }

        /// <summary>The dot's brush, taken from the theme by the board when it shows the row.</summary>
        public Brush ColorBrush { get; set; } = Brushes.Gray;
    }

    /// <summary>One of the three figures above the chart.</summary>
    public sealed record UsageHighlight(PackIconKind Icon, string Label, string Value, string Sub);

    /// <summary>What a page hands the board.</summary>
    public sealed class UsageBoardData
    {
        public IReadOnlyList<UsageRow> Rows { get; init; } = Array.Empty<UsageRow>();
        public IReadOnlyList<UsageHighlight> Highlights { get; init; } = Array.Empty<UsageHighlight>();
        public StackedData Stack { get; init; } = new();
        public string ValueHeaderKey { get; init; } = "";
        public string PeakHeaderKey { get; init; } = "";
    }

    /// <summary>Figures, stacked chart and ranking. The page refreshes it; the chosen program is kept by name and stays strong in the chart.</summary>
    public partial class AppUsageBoard : UserControl
    {
        private string? _selected;
        private bool _updating;

        public AppUsageBoard()
        {
            InitializeComponent();
        }

        public void SetTexts(string chartHeaderKey, string listHeaderKey, string nameHeaderKey, string emptyTitleKey, string emptyHintKey)
        {
            ChartHeader.SetResourceReference(ContentControl.ContentProperty, chartHeaderKey);
            ListHeader.SetResourceReference(ContentControl.ContentProperty, listHeaderKey);
            HeadName.SetResourceReference(TextBlock.TextProperty, nameHeaderKey);
            SetEmptyKeys(emptyTitleKey, emptyHintKey);
        }

        public void SetEmptyKeys(string titleKey, string hintKey)
        {
            EmptyTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
            EmptyHint.SetResourceReference(TextBlock.TextProperty, hintKey);
        }

        public void SetData(UsageBoardData data)
        {
            HeadValue.SetResourceReference(TextBlock.TextProperty, data.ValueHeaderKey);
            HeadPeak.SetResourceReference(TextBlock.TextProperty, data.PeakHeaderKey);

            bool any = data.Rows.Count > 0;
            Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any) return;

            Highlights.ItemsSource = data.Highlights;
            foreach (var row in data.Rows) row.ColorBrush = ChartPalette.Brush(row.ColorIndex);
            Legend.ItemsSource = data.Stack.Series.Select(s => new UsageRow { Name = s.Name, ColorBrush = ChartPalette.Brush(s.ColorIndex) }).ToList();
            Chart.SetData(data.Stack);

            _updating = true;
            try
            {
                List.ItemsSource = data.Rows;
                var keep = data.Rows.FirstOrDefault(r => string.Equals(r.Name, _selected, StringComparison.OrdinalIgnoreCase));
                List.SelectedItem = keep;
                _selected = keep?.Name;
            }
            finally { _updating = false; }
            Chart.Highlight = _selected;
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating) return;
            _selected = (List.SelectedItem as UsageRow)?.Name;
            Chart.Highlight = _selected;
        }

        /// <summary>Click a legend name to pick that program (click again to show all).</summary>
        private void LegendItem_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string name) return;
            var row = List.Items.OfType<UsageRow>().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;   // "Other programs" is not in the list
            if (ReferenceEquals(List.SelectedItem, row)) List.SelectedItem = null;
            else { List.SelectedItem = row; List.ScrollIntoView(row); }
        }
    }
}
