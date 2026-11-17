using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Port Scanner - collapse / expand every host in the results, and export them as HTML, PDF or text.</summary>
    public partial class PortScannerView
    {
        private string _lastTitle = "";

        /// <summary>Enables the buttons that need results and keeps the collapse button's label in step.</summary>
        private void UpdateResultButtons()
        {
            bool any = _hosts.Count > 0;
            BtnCollapseAll.IsEnabled = any;
            BtnExport.IsEnabled = any && _scanCts == null;
            bool allCollapsed = any && _hosts.All(h => !h.IsExpanded);
            TxtCollapseAll.Text = allCollapsed ? Loc.T("Str_PortScanner_ExpandAll", "Expand all") : Loc.T("Str_PortScanner_CollapseAll", "Collapse all");
            IconCollapseAll.Kind = allCollapsed ? MaterialDesignThemes.Wpf.PackIconKind.UnfoldMoreHorizontal : MaterialDesignThemes.Wpf.PackIconKind.UnfoldLessHorizontal;
        }

        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            bool expand = _hosts.Count > 0 && _hosts.All(h => !h.IsExpanded);
            foreach (var h in _hosts) h.IsExpanded = expand;
            UpdateResultButtons();
        }

        private TableReport BuildReport()
        {
            string none = Loc.T("Str_PortScanner_NoOpen", "no open ports");
            var rows = new List<string[]>();
            foreach (var h in _hosts)
            {
                if (h.Ports.Count == 0) rows.Add(new[] { h.Host, h.Label, "", "", h.Status.Length > 0 ? h.Status : none });
                foreach (var p in h.Ports)
                    rows.Add(new[] { h.Host, h.Label, p.Port.ToString(CultureInfo.InvariantCulture), p.Service, p.LatencyMs.ToString(CultureInfo.InvariantCulture) + " ms" });
            }
            int open = _hosts.Sum(h => h.Ports.Count);
            string title = Loc.T("Str_Tool_port-scanner_Name", "Port Scanner") + (_lastTitle.Length > 0 ? " - " + _lastTitle : "");
            string note = Loc.F("Str_PortScanner_ReportNote", "{0} hosts · {1} open ports", _hosts.Count, open);
            var headers = new[]
            {
                Loc.T("Str_PortScanner_ColHost", "Host"), Loc.T("Str_PortScanner_ColName", "Adapter / name"), Loc.T("Str_PortScanner_ColPort", "Port"),
                Loc.T("Str_PortScanner_ColService", "Service"), Loc.T("Str_PortScanner_ColAnswer", "Answered in"),
            };
            return new TableReport(title, note, headers, rows, new[] { 0.22, 0.22, 0.10, 0.26, 0.20 });
        }

        private void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (_hosts.Count == 0 || sender is not MenuItem { Tag: string kind }) return;
            TableReportSaver.Save(Window.GetWindow(this), BuildReport(), kind, "port-scan");
        }
    }
}
