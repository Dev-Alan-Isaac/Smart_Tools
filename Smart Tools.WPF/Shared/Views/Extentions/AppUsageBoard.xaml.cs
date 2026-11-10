using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>One program in the ranking.</summary>
    public sealed class UsageRow
    {
        public int Rank { get; init; }
        public string Name { get; init; } = "";
        public string ValueText { get; init; } = "";
        public string PeakText { get; init; } = "";
        public string Detail { get; init; } = "";

        /// <summary>0-100, relative to the first row (the bar under the name).</summary>
        public double Share { get; init; }
    }

    /// <summary>What a page hands the board: the ranking, a chart line per program, and how to write a chart value.</summary>
    public sealed class UsageBoardData
    {
        public IReadOnlyList<UsageRow> Rows { get; init; } = Array.Empty<UsageRow>();
        public Func<string?, List<(DateTime When, double Value)>> Series { get; init; } = _ => new();
        public Func<double, string> ChartFormat { get; init; } = v => v.ToString("0.##");
        public string ValueHeaderKey { get; init; } = "";
        public string PeakHeaderKey { get; init; } = "";
    }

    /// <summary>Ranking of programs plus a chart of the chosen one. The page refreshes it; the chosen program is kept by name.</summary>
    public partial class AppUsageBoard : UserControl
    {
        private UsageBoardData? _data;
        private string? _selected;
        private bool _updating;

        public AppUsageBoard()
        {
            InitializeComponent();
        }

        public void SetTexts(string listHeaderKey, string nameHeaderKey, string emptyTitleKey, string emptyHintKey)
        {
            ListHeader.SetResourceReference(ContentControl.ContentProperty, listHeaderKey);
            HeadName.SetResourceReference(TextBlock.TextProperty, nameHeaderKey);
            EmptyTitle.SetResourceReference(TextBlock.TextProperty, emptyTitleKey);
            EmptyHint.SetResourceReference(TextBlock.TextProperty, emptyHintKey);
        }

        /// <summary>Overrides the empty-state wording (for example "needs administrator" instead of "nothing recorded yet").</summary>
        public void SetEmptyKeys(string titleKey, string hintKey)
        {
            EmptyTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
            EmptyHint.SetResourceReference(TextBlock.TextProperty, hintKey);
        }

        public void SetData(UsageBoardData data, string chartTimeFormat)
        {
            _data = data;
            Chart.DateFormat = chartTimeFormat;
            HeadValue.SetResourceReference(TextBlock.TextProperty, data.ValueHeaderKey);
            HeadPeak.SetResourceReference(TextBlock.TextProperty, data.PeakHeaderKey);

            bool any = data.Rows.Count > 0;
            Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any) return;

            _updating = true;
            try
            {
                List.ItemsSource = data.Rows;
                var keep = data.Rows.FirstOrDefault(r => string.Equals(r.Name, _selected, StringComparison.OrdinalIgnoreCase)) ?? data.Rows[0];
                List.SelectedItem = keep;
                _selected = keep.Name;
            }
            finally { _updating = false; }
            ShowChart();
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || List.SelectedItem is not UsageRow row) return;
            _selected = row.Name;
            ShowChart();
        }

        private void ShowChart()
        {
            if (_data == null || _selected == null) return;
            var row = _data.Rows.FirstOrDefault(r => string.Equals(r.Name, _selected, StringComparison.OrdinalIgnoreCase));
            ChartTitle.Text = _selected;
            ChartSubtitle.Text = row == null ? "" : row.Detail.Length > 0 ? row.ValueText + "   " + row.Detail : row.ValueText;
            Chart.SetData(_data.Series(_selected), _data.ChartFormat, higherIsWorse: false);
        }
    }
}
