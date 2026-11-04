using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using DataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// System Snapshot - UI only. The scan, the collectors and the exporters are in <see cref="SystemSnapshotManager"/>.
    /// It only reads the PC. Closing the page (or the app) cancels a scan that is running.
    /// </summary>
    public partial class SystemSnapshotView : UserControl
    {
        private const string OverviewId = "overview";

        private sealed record Tile(string Label, string Value, PackIconKind Icon);

        private enum StatusKind { Success, Error, Info }

        private readonly SystemSnapshotManager _manager = new();
        private readonly SystemSnapshotSettings _settings;
        private readonly Dictionary<string, CheckBox> _groupBoxes = new();
        private readonly List<SnapshotTable> _tables = new();
        private readonly bool _viewConstructed;

        private CancellationTokenSource? _cts;
        private SystemSnapshot? _snapshot;
        private SnapshotTable? _shown;
        private DataView? _view;
        private string _selectedId = OverviewId;
        private bool _scanning;
        private bool _loadedOnce;

        public SystemSnapshotView()
        {
            InitializeComponent();

            _settings = JsonSettings.Load<SystemSnapshotSettings>(SystemSnapshotManager.SettingsName);
            var chosen = _settings.Groups != null
                ? new HashSet<string>(_settings.Groups, StringComparer.OrdinalIgnoreCase)
                : SystemSnapshotManager.Groups.Where(g => g.OnByDefault).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var group in SystemSnapshotManager.Groups)
            {
                var box = new CheckBox
                {
                    Content = Title("Str_SysSnap_Group_" + group.Id, group.Title),
                    Tag = group.Id,
                    IsChecked = chosen.Contains(group.Id),
                    Margin = new Thickness(0, 6, 0, 0),
                };
                box.SetResourceReference(Control.ForegroundProperty, "SidebarText");
                _groupBoxes[group.Id] = box;
                PanelGroups.Children.Add(box);
            }

            ChkMsTasks.IsChecked = _settings.IncludeMicrosoftTasks;
            ChkBuiltInRules.IsChecked = _settings.IncludeBuiltInFirewallRules;
            if (_settings.SidebarCollapsed) Sidebar.IsCollapsed = true;

            bool admin = SystemSnapshotManager.IsAdministrator();
            TxtAdmin.Text = Str(admin ? "Str_SysSnap_Admin" : "Str_SysSnap_Limited");
            IconAdmin.Kind = admin ? PackIconKind.ShieldCheckOutline : PackIconKind.ShieldAlertOutline;
            BadgeAdmin.SetResourceReference(Border.BackgroundProperty, admin ? "StatusSuccessBg" : "StatusWarningBg");
            TxtAdmin.SetResourceReference(TextBlock.ForegroundProperty, admin ? "StatusSuccessFg" : "StatusWarningFg");
            IconAdmin.SetResourceReference(Control.ForegroundProperty, admin ? "StatusSuccessFg" : "StatusWarningFg");
            BadgeAdmin.ToolTip = Str(admin ? "Str_SysSnap_AdminTip" : "Str_SysSnap_LimitedTip");

            _viewConstructed = true;
        }

        private string Str(string key, string fallback = "") => TryFindResource(key) as string ?? (fallback.Length > 0 ? fallback : key);

        /// <summary>On-screen name of a group or section: the translated one when there is one, else the stable English one.</summary>
        private string Title(string key, string english) => TryFindResource(key) as string ?? english;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ShowSelected();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a scan and remembers the setup.</summary>
        public void CancelActiveRun()
        {
            _cts?.Cancel();
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            _settings.Groups = _groupBoxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
            _settings.IncludeMicrosoftTasks = ChkMsTasks.IsChecked == true;
            _settings.IncludeBuiltInFirewallRules = ChkBuiltInRules.IsChecked == true;
            _settings.SidebarCollapsed = Sidebar.IsCollapsed;
            JsonSettings.Save(SystemSnapshotManager.SettingsName, _settings);
        }

        // =================================================================
        //  Choosing what to scan
        // =================================================================

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var box in _groupBoxes.Values) box.IsChecked = true;
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var box in _groupBoxes.Values) box.IsChecked = false;
        }

        // =================================================================
        //  Scanning
        // =================================================================

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning)
            {
                _cts?.Cancel();
                return;
            }

            var groups = _groupBoxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (groups.Count == 0)
            {
                ShowStatus(Str("Str_SysSnap_NothingChosen"), StatusKind.Error);
                return;
            }

            SaveSettings();
            var options = new SnapshotOptions
            {
                Groups = groups,
                IncludeMicrosoftTasks = ChkMsTasks.IsChecked == true,
                IncludeBuiltInFirewallRules = ChkBuiltInRules.IsChecked == true,
            };

            _tables.Clear();
            _snapshot = null;
            _selectedId = "";
            SetScanning(true);
            RebuildNav();
            ShowSelected();

            _cts = new CancellationTokenSource();
            var progress = new Progress<SnapshotProgress>(OnProgress);
            try
            {
                _snapshot = await _manager.ScanAsync(options, progress, _cts.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Info($"System Snapshot: {ex.Message}");
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_ScanFailed"), ex.Message), StatusKind.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetScanning(false);
            }

            if (_snapshot == null) return;
            int rows = _snapshot.Tables.Sum(t => t.Rows.Count);
            ShowStatus(string.Format(CultureInfo.CurrentCulture, Str(_snapshot.Complete ? "Str_SysSnap_Done" : "Str_SysSnap_Cancelled"), _snapshot.Tables.Count, rows),
                _snapshot.Complete ? StatusKind.Success : StatusKind.Info);

            _selectedId = OverviewId;
            RebuildNav();
            ShowSelected();
        }

        private void OnProgress(SnapshotProgress p)
        {
            BarScan.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_Scanning"), Title("Str_SysSnap_Group_" + GroupIdOf(p.Title), p.Title), p.Done, p.Total), StatusKind.Info, autoHide: false);

            if (p.Finished == null) return;
            _tables.AddRange(p.Finished);
            if (_selectedId.Length == 0)
            {
                _selectedId = p.Finished[0].Id;
                RebuildNav();
                ShowSelected();
            }
            else
            {
                RebuildNav();
            }
        }

        private static string GroupIdOf(string title) => SystemSnapshotManager.Groups.FirstOrDefault(g => g.Title == title)?.Id ?? "";

        private void SetScanning(bool scanning)
        {
            _scanning = scanning;
            IconScan.Kind = scanning ? PackIconKind.Close : PackIconKind.Radar;
            TxtScan.Text = Str(scanning ? "Str_Common_Cancel" : "Str_SysSnap_Scan");
            BarScan.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
            BarScan.Value = 0;
            foreach (var box in _groupBoxes.Values) box.IsEnabled = !scanning;
            ChkMsTasks.IsEnabled = ChkBuiltInRules.IsEnabled = !scanning;

            bool can = !scanning && _tables.Count > 0;
            BtnExportHtml.IsEnabled = BtnExportJson.IsEnabled = BtnExportText.IsEnabled = can;
            if (!scanning) BtnExportCsv.IsEnabled = can && _shown != null;
        }

        // =================================================================
        //  Section list and what it shows
        // =================================================================

        private void RebuildNav()
        {
            PanelNav.Children.Clear();
            if (_snapshot != null) PanelNav.Children.Add(NavButton(OverviewId, Str("Str_SysSnap_Overview"), PackIconKind.ViewDashboardOutline, null));
            foreach (var t in _tables)
                PanelNav.Children.Add(NavButton(t.Id, Title("Str_SysSnap_Sec_" + t.Id, t.Title), ParseIcon(t.Icon), t.Rows.Count));
        }

        private static PackIconKind ParseIcon(string name) => Enum.TryParse(name, out PackIconKind kind) ? kind : PackIconKind.TableLarge;

        private RadioButton NavButton(string id, string title, PackIconKind icon, int? count)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconView = new PackIcon { Kind = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(text, 1);
            row.Children.Add(iconView);
            row.Children.Add(text);
            if (count != null)
            {
                var number = new TextBlock { Text = count.Value.ToString(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(number, 2);
                row.Children.Add(number);
            }

            var button = new RadioButton
            {
                Content = row,
                GroupName = "SnapSection",
                Tag = id,
                IsChecked = id == _selectedId,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = title,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ModeToggleButtonStyle");
            button.Checked += Nav_Checked;
            return button;
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string id } || id == _selectedId) return;
            _selectedId = id;
            ShowSelected();
        }

        private void ShowSelected()
        {
            var table = _tables.FirstOrDefault(t => t.Id == _selectedId);
            bool overview = _selectedId == OverviewId && _snapshot != null;

            EmptyState.Visibility = !overview && table == null ? Visibility.Visible : Visibility.Collapsed;
            PanelOverview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            PanelTable.Visibility = table != null ? Visibility.Visible : Visibility.Collapsed;
            _shown = table;
            BtnExportCsv.IsEnabled = !_scanning && table != null;

            if (overview) ShowOverview();
            else if (table != null) ShowTable(table);
        }

        private void ShowOverview()
        {
            var snapshot = _snapshot!;
            TxtOverviewTitle.Text = $"{snapshot.MachineName} · {snapshot.UserName}";
            TxtOverviewSub.Text = snapshot.CapturedAt.ToString("f", CultureInfo.CurrentCulture) + (snapshot.Complete ? "" : " · " + Str("Str_SysSnap_Partial"));
            OverviewTiles.ItemsSource = SystemSnapshotManager.BuildOverview(snapshot).Select(t => new Tile(t.Label, t.Value, ParseIcon(t.Icon))).ToList();
        }

        private void ShowTable(SnapshotTable table)
        {
            var shown = SystemSnapshotManager.Localize(table);
            TxtTableTitle.Text = shown.Title;
            TxtTableNote.Text = shown.Note ?? "";
            TxtTableNote.Visibility = string.IsNullOrEmpty(shown.Note) ? Visibility.Collapsed : Visibility.Visible;

            // Columns are named c0, c1... so headers with spaces or symbols cannot break a binding path.
            var data = new DataTable();
            for (int c = 0; c < table.Headers.Length; c++) data.Columns.Add("c" + c, typeof(string));
            foreach (var row in shown.Rows) data.Rows.Add(row.Cast<object>().ToArray());
            _view = data.DefaultView;

            GridData.Columns.Clear();
            var ellipsis = TryFindResource("EllipsisCellTextStyle") as Style;
            for (int c = 0; c < table.Headers.Length; c++)
            {
                bool single = table.Headers.Length == 1;
                var column = new DataGridTextColumn
                {
                    Header = shown.Headers[c],
                    Binding = new Binding($"[c{c}]"),
                    SortMemberPath = "c" + c,
                    MaxWidth = single ? double.PositiveInfinity : 460,
                    Width = single ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                };
                if (ellipsis != null) column.ElementStyle = ellipsis;
                GridData.Columns.Add(column);
            }

            GridData.ItemsSource = _view;
            ApplyFilter();
        }

        // =================================================================
        //  Filtering
        // =================================================================

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_view == null || _shown == null) return;
            string text = TxtFilter.Text.Trim();
            if (text.Length == 0)
            {
                _view.RowFilter = "";
            }
            else
            {
                // LIKE needs ' doubled and the wildcard characters wrapped in brackets
                string safe = text.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
                _view.RowFilter = string.Join(" OR ", Enumerable.Range(0, _shown.Headers.Length).Select(c => $"c{c} LIKE '%{safe}%'"));
            }
            TxtRowCount.Text = text.Length == 0
                ? string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_Rows"), _shown.Rows.Count)
                : string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_RowsOf"), _view.Count, _shown.Rows.Count);
        }

        // =================================================================
        //  Export
        // =================================================================

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string kind } || _snapshot == null && kind != "csv") return;

            string content, extension, filter, title;
            string? sectionId = null;
            switch (kind)
            {
                case "html": content = SystemSnapshotManager.ToHtml(_snapshot!); extension = ".html"; filter = "HTML (*.html)|*.html"; title = Str("Str_SysSnap_ExportHtml"); break;
                case "json": content = SystemSnapshotManager.ToJson(_snapshot!); extension = ".json"; filter = "JSON (*.json)|*.json"; title = Str("Str_SysSnap_ExportJson"); break;
                case "txt": content = SystemSnapshotManager.ToText(_snapshot!); extension = ".txt"; filter = "Text (*.txt)|*.txt"; title = Str("Str_SysSnap_ExportText"); break;
                default:
                    if (_shown == null) return;
                    content = SystemSnapshotManager.ToCsv(_shown);
                    extension = ".csv"; filter = "CSV (*.csv)|*.csv"; title = Str("Str_SysSnap_ExportCsv");
                    sectionId = _shown.Id;
                    break;
            }

            var dialog = new SaveFileDialog
            {
                Title = title,
                FileName = SystemSnapshotManager.SuggestFileName(_snapshot?.MachineName ?? Environment.MachineName, extension, sectionId),
                Filter = filter,
                DefaultExt = extension,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                OverwritePrompt = false,   // the app never overwrites: a taken name becomes "name (2)"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string written = await FolderTreeManager.SaveAsync(content, dialog.FileName);
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_Saved"), Path.GetFileName(written)), StatusKind.Success);
                ShellExplorer.Show(written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_SaveFailed"), ex.Message), StatusKind.Error);
            }
        }

        // =================================================================
        //  Portable version: the whole app on a USB drive or in any folder
        // =================================================================

        /// <summary>
        /// Copies the app (not the settings or any scan) into a new "Smart Tools Portable" folder inside the folder or drive that was
        /// chosen, so a stick with other files on it works too. An existing folder is never touched: a taken name becomes "(2)".
        /// </summary>
        private void BtnPortable_Click(object sender, RoutedEventArgs e)
        {
            if (AppMode.Current == AppRunMode.Development)
            {
                ShowStatus(Str("Str_SysSnap_PortableDev"), StatusKind.Info);
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = Str("Str_SysSnap_PortableChoose"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string target = SafeFileOps.UniquePath(Path.Combine(dialog.SelectedPath, Str("Str_SysSnap_PortableFolder", "Smart Tools Portable")));
            var result = CopyProgressWindow.Run(Window.GetWindow(this), Str("Str_Install_Copying", "Making a portable copy..."),
                (progress, token) => AppInstaller.MakePortableCopyAsync(target, copyData: false, progress, token));
            if (result.Cancelled) return;
            if (!result.Ok)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_PortableFailed"), result.Message), StatusKind.Error);
                return;
            }

            ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_SysSnap_PortableDone"), target), StatusKind.Success);
            ShellExplorer.Show(result.ExePath!);
        }

        // =================================================================
        //  Status
        // =================================================================

        private System.Windows.Threading.DispatcherTimer? _statusTimer;

        private void ShowStatus(string message, StatusKind kind, bool autoHide = true)
        {
            TxtStatus.Text = message;
            PillStatus.ToolTip = message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            });
            IconStatus.Kind = kind switch
            {
                StatusKind.Success => PackIconKind.CheckCircleOutline,
                StatusKind.Error => PackIconKind.AlertCircleOutline,
                _ => PackIconKind.InformationOutline,
            };
            PillStatus.Visibility = Visibility.Visible;

            _statusTimer?.Stop();
            if (!autoHide) return;
            _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 9 : 6) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer?.Stop();
                if (!_scanning && _snapshot != null) PillStatus.Visibility = Visibility.Collapsed;
            };
            _statusTimer.Start();
        }
    }
}
