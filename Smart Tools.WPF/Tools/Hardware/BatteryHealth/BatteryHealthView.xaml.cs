using Smart_Tools.WPF.Controllers;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Battery Health - UI only. Reading the battery and the wear level are <see cref="BatteryHealthManager"/>'s.</summary>
    public partial class BatteryHealthView : UserControl
    {
        public sealed record BatteryRow(string Label, string Value);

        public sealed record BatteryCard(string Title, string HealthText, double HealthValue, string Verdict, List<BatteryRow> Rows);

        private bool _loadedOnce;
        private bool _busy;
        private int _run;

        public BatteryHealthView() => InitializeComponent();

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await RefreshAsync();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) { }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            int run = ++_run;
            BtnRefresh.IsEnabled = false;
            var reading = await Task.Run(BatteryHealthManager.Read);
            BtnRefresh.IsEnabled = true;
            _busy = false;
            if (run != _run) return;

            if (reading.Problem != null) ShowStatus(reading.Problem, error: true);
            else StatusBanner.Visibility = Visibility.Collapsed;

            PanelNone.Visibility = reading.Batteries.Count == 0 && reading.Problem == null ? Visibility.Visible : Visibility.Collapsed;
            TxtNote.Visibility = reading.Batteries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BatteryList.ItemsSource = reading.Batteries.Select((b, i) => ToCard(b, reading.Batteries.Count > 1 ? i + 1 : 0)).ToList();
        }

        private static BatteryCard ToCard(BatteryInfo b, int number)
        {
            double? health = b.HealthPercent;
            var rows = new List<BatteryRow>
            {
                new("Design capacity (when new)", BatteryHealthManager.Mwh(b.DesignCapacity)),
                new("Full-charge capacity (now)", BatteryHealthManager.Mwh(b.FullChargeCapacity)),
            };
            if (b.ChargePercent is { } pct) rows.Add(new("Charge right now", pct.ToString(CultureInfo.InvariantCulture) + " %"));
            if (b.RemainingCapacity is { } rem) rows.Add(new("Energy left", BatteryHealthManager.Mwh(rem)));
            rows.Add(new("Charge cycles", b.CycleCount is { } c ? c.ToString("N0", CultureInfo.CurrentCulture) : "not reported"));
            if (b.Charging is { } charging)
                rows.Add(new("Status", charging ? "Charging" : b.PluggedIn == true ? "Plugged in, not charging" : "Running on battery"));
            if (b.Volts is { } v) rows.Add(new("Voltage", v.ToString("0.00", CultureInfo.InvariantCulture) + " V"));
            if (!string.IsNullOrWhiteSpace(b.Chemistry)) rows.Add(new("Type", b.Chemistry!));
            if (!string.IsNullOrWhiteSpace(b.Manufacturer)) rows.Add(new("Maker", b.Manufacturer!));

            return new BatteryCard(
                number > 0 ? $"Battery {number} · {b.Name}" : b.Name,
                health is { } h ? h.ToString("0.#", CultureInfo.InvariantCulture) + " %" : "n/a",
                Math.Min(100, health ?? 0),
                BatteryHealthManager.Describe(health),
                rows);
        }

        private async void BtnReport_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            BtnReport.IsEnabled = false;
            TxtReport.Text = "Making the report...";
            string? problem = null;
            string? path = await Task.Run(() => BatteryHealthManager.CreateWindowsReport(out problem));
            BtnReport.IsEnabled = true;
            TxtReport.Text = "Windows battery report";

            if (path == null) { ShowStatus("Couldn't make the report: " + problem, error: true); return; }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                StatusBanner.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus("The report was saved to " + path + " but couldn't be opened: " + ex.Message, error: false);
            }
        }

        private void ShowStatus(string text, bool error)
        {
            StatusBanner.SetResourceReference(Border.BackgroundProperty, error ? "StatusErrorBg" : "StatusWarningBg");
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "StatusWarningFg");
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        public void CancelActiveRun() => _run++;
    }
}
